// Recolector — Ranked ARAM Caos (app de bandeja del sistema)
//
// Riot oculta las partidas de ARAM: Caos en su API pública; este programa las
// lee del cliente de LoL (API local, solo lectura — misma técnica que Blitz,
// tolerada por Riot) y las sube al ranked del grupo. Vive como iconito en la
// bandeja: click derecho para abrir el ranked, activar el inicio con Windows
// o salir.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RecolectorAram;

class Config
{
    public string workerUrl { get; set; } = "https://aram-ranked.fadiazpuga.workers.dev";
    public string ingestToken { get; set; } = "";
    public string lockfilePath { get; set; } = "";
    public int pollSeconds { get; set; } = 30;
}

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "RecolectorARAM";

    static readonly string BaseDir = AppContext.BaseDirectory;
    static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
    static readonly string SeenPath = Path.Combine(BaseDir, "seen.json");
    static readonly string OpeningPath = Path.Combine(BaseDir, "first-kill.json");
    static readonly string LogPath = Path.Combine(BaseDir, "recolector.log");

    static Config config;
    static HashSet<long> seen = new();
    static NotifyIcon tray;
    static ToolStripMenuItem statusItem;
    static ToolStripMenuItem autostartItem;
    static bool polling;
    static bool clientWasClosed = true;
    static JsonObject pendingOpening;

    static readonly HttpClient lcuHttp = new(new HttpClientHandler
    {
        // El cliente de LoL usa certificado autofirmado en localhost (mismo PC): esperado y seguro.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    static readonly HttpClient webHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient liveHttp = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(4) };

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "RecolectorARAM-instancia-unica", out bool primera);
        if (!primera) return;

        ApplicationConfiguration.Initialize();
        LoadState();

        // Primer arranque: pedir el código de vinculación (se ve en la web tras
        // iniciar sesión). Sin código no hay nada que hacer.
        while (string.IsNullOrWhiteSpace(config.ingestToken) || config.ingestToken == "PEGAR_TOKEN_AQUI")
        {
            string token = AskToken();
            if (token == null) return;
            if (!string.IsNullOrWhiteSpace(token))
            {
                config.ingestToken = token.Trim();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Recolector ARAM Caos",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("Iniciando…") { Enabled = false };
        autostartItem = new ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = IsAutostart() };
        autostartItem.CheckedChanged += (_, _) => SetAutostart(autostartItem.Checked);
        var openItem = new ToolStripMenuItem("Abrir el ranked");
        openItem.Click += (_, _) => Process.Start(new ProcessStartInfo(config.workerUrl) { UseShellExecute = true });
        var exitItem = new ToolStripMenuItem("Salir");
        exitItem.Click += (_, _) => { tray.Visible = false; Application.Exit(); };

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Process.Start(new ProcessStartInfo(config.workerUrl) { UseShellExecute = true });

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(10, config.pollSeconds) * 1000 };
        timer.Tick += async (_, _) => await SafePoll();
        timer.Start();
        _ = SafePoll();

        Log("Recolector iniciado → " + config.workerUrl);
        Application.Run();
    }

    static void LoadState()
    {
        if (!File.Exists(ConfigPath))
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config(), new JsonSerializerOptions { WriteIndented = true }));
        config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
        if (File.Exists(SeenPath))
            seen = JsonSerializer.Deserialize<HashSet<long>>(File.ReadAllText(SeenPath)) ?? new HashSet<long>();
        if (File.Exists(OpeningPath))
        {
            try { pendingOpening = JsonNode.Parse(File.ReadAllText(OpeningPath)) as JsonObject; }
            catch { pendingOpening = null; }
        }
    }

    static async Task SafePoll()
    {
        if (polling) return;
        polling = true;
        try { await Poll(); }
        catch (Exception ex) { Log("✖ " + ex.Message); }
        finally { polling = false; }
    }

    static async Task Poll()
    {
        var creds = FindCredentials();
        if (creds == null)
        {
            SetStatus("Esperando al cliente de LoL…", false);
            clientWasClosed = true;
            return;
        }
        if (clientWasClosed) Log("Cliente de LoL detectado.");
        clientWasClosed = false;
        SetStatus("Vigilando partidas nuevas", true);

        // El resumen post-partida dice quién logró first blood, pero no quién
        // murió. Mientras la partida está activa guardamos el primer ChampionKill.
        await CaptureLiveOpening();

        var history = await LcuGet(creds.Value, "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=14");
        if (history?["games"]?["games"] is not JsonArray games) return;

        foreach (var g in games)
        {
            long gameId = g?["gameId"]?.GetValue<long>() ?? 0;
            if (gameId == 0 || seen.Contains(gameId)) continue;

            var full = await LcuGet(creds.Value, $"/lol-match-history/v1/games/{gameId}");
            var game = full?["gameId"] != null ? full : g;
            bool openingAttached = OpeningMatches(game);
            var gameForUpload = game.DeepClone();
            if (openingAttached && gameForUpload is JsonObject gameObject)
            {
                gameObject["_aramRanked"] = new JsonObject
                {
                    ["firstKill"] = pendingOpening?["firstKill"]?.DeepClone(),
                };
            }

            var payload = new JsonObject { ["token"] = config.ingestToken, ["game"] = gameForUpload };
            using var res = await webHttp.PostAsync(
                new Uri(new Uri(config.workerUrl), "/api/ingest"),
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
            var info = JsonNode.Parse(await res.Content.ReadAsStringAsync());

            bool ok = info?["ok"]?.GetValue<bool>() ?? false;
            bool counted = info?["counted"]?.GetValue<bool>() ?? false;
            string skipped = info?["skipped"]?.GetValue<string>();

            if (ok && counted)
            {
                Log($"✔ {info?["matchId"]}: registrada en el ranked");
                tray.ShowBalloonTip(4000, "Ranked ARAM Caos", "¡Partida registrada en el ranked!", ToolTipIcon.Info);
            }
            else if (ok) Log($"— {info?["matchId"]}: sin suficientes miembros del grupo");
            else if (skipped != null) Log($"— partida {gameId}: {skipped}");
            else { Log($"✖ partida {gameId}: {info?["error"]} (se reintenta)"); continue; }

            seen.Add(gameId);
            File.WriteAllText(SeenPath, JsonSerializer.Serialize(seen));
            if (openingAttached)
            {
                pendingOpening = null;
                try { File.Delete(OpeningPath); } catch { }
            }
        }
    }

    static string NodeString(JsonNode node, params string[] keys)
    {
        foreach (var key in keys)
        {
            try
            {
                var value = node?[key]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }
        return "";
    }

    static double NodeNumber(JsonNode node, params string[] keys)
    {
        foreach (var key in keys)
            if (double.TryParse(node?[key]?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)) return value;
        return 0;
    }

    static async Task<JsonNode> LiveGet(string route)
    {
        using var res = await liveHttp.GetAsync($"https://127.0.0.1:2999{route}");
        if (!res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    static async Task CaptureLiveOpening()
    {
        try
        {
            var data = await LiveGet("/liveclientdata/eventdata");
            var events = data?["Events"] as JsonArray ?? data?["events"] as JsonArray;
            if (events == null) return;

            JsonNode firstKill = null;
            double firstTime = double.MaxValue;
            foreach (var gameEvent in events)
            {
                if (!string.Equals(NodeString(gameEvent, "EventName", "eventName"), "ChampionKill", StringComparison.OrdinalIgnoreCase)) continue;
                double eventTime = NodeNumber(gameEvent, "EventTime", "eventTime");
                if (eventTime < firstTime)
                {
                    firstTime = eventTime;
                    firstKill = gameEvent;
                }
            }
            if (firstKill == null) return;

            var stats = await LiveGet("/liveclientdata/gamestats");
            double gameTime = NodeNumber(stats, "gameTime", "GameTime");
            if (gameTime <= 0) return;
            long gameStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(gameTime * 1000);
            long previousStartMs = (long)NodeNumber(pendingOpening, "gameStartMs");
            if (pendingOpening != null && Math.Abs(previousStartMs - gameStartMs) < 120_000) return;

            string killerName = NodeString(firstKill, "KillerName", "killerName");
            string victimName = NodeString(firstKill, "VictimName", "victimName");
            if (string.IsNullOrWhiteSpace(victimName)) return;

            pendingOpening = new JsonObject
            {
                ["gameStartMs"] = gameStartMs,
                ["firstKill"] = new JsonObject
                {
                    ["killerName"] = killerName,
                    ["victimName"] = victimName,
                    ["eventTime"] = firstTime,
                },
            };
            File.WriteAllText(OpeningPath, pendingOpening.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"⚔ Primera muerte capturada a los {Math.Round(firstTime)} s: {victimName}");
        }
        catch
        {
            // Fuera de partida el puerto 2999 no responde: es el estado normal.
        }
    }

    static bool OpeningMatches(JsonNode game)
    {
        if (pendingOpening == null) return false;
        long gameCreation = (long)NodeNumber(game, "gameCreation");
        long gameStartMs = (long)NodeNumber(pendingOpening, "gameStartMs");
        return gameCreation > 0 && gameStartMs > 0 && Math.Abs(gameCreation - gameStartMs) < 300_000;
    }

    static readonly string[] LockfileCandidates =
    {
        @"C:\Riot Games\League of Legends\lockfile",
        @"D:\Riot Games\League of Legends\lockfile",
        @"E:\Riot Games\League of Legends\lockfile",
        @"F:\Riot Games\League of Legends\lockfile",
    };

    static (int port, string password)? FindCredentials()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(config.lockfilePath)) candidates.Add(config.lockfilePath);
        candidates.AddRange(LockfileCandidates);

        // Riot registra dónde quedó instalado el juego; sirve para instalaciones en rutas raras.
        try
        {
            string meta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml");
            foreach (var line in File.ReadLines(meta))
                if (line.Contains("product_install_full_path:"))
                    candidates.Add(Path.Combine(line.Split(':', 2)[1].Trim().Trim('"'), "lockfile"));
        }
        catch { }

        foreach (var path in candidates)
        {
            try
            {
                // FileShare.ReadWrite: el cliente mantiene el lockfile abierto
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var parts = new StreamReader(fs).ReadToEnd().Split(':');
                if (parts.Length >= 4) return (int.Parse(parts[2]), parts[3]);
            }
            catch { }
        }
        return null;
    }

    static async Task<JsonNode> LcuGet((int port, string password) creds, string route)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{creds.port}{route}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{creds.password}")));
        using var res = await lcuHttp.SendAsync(req);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
        Log("Inicio con Windows: " + (enabled ? "activado" : "desactivado"));
    }

    static void SetStatus(string text, bool active)
    {
        statusItem.Text = (active ? "🟢 " : "⏸ ") + text;
        tray.Text = "Recolector ARAM Caos — " + text;
    }

    static void Log(string msg)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:dd-MM HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    static string AskToken()
    {
        using var form = new Form
        {
            Text = "Recolector ARAM Caos — primer arranque",
            Width = 440,
            Height = 210,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
        };
        var label = new Label
        {
            Text = "Pega tu código de vinculación.\nLo encuentras al iniciar sesión en la web del ranked (pestaña Recolector).",
            Left = 15, Top = 15, Width = 395, Height = 40,
        };
        var box = new TextBox { Left = 15, Top = 62, Width = 395, Font = new Font(FontFamily.GenericMonospace, 11) };
        var ok = new Button { Text = "Guardar", Left = 15, Top = 105, Width = 130, Height = 32, DialogResult = DialogResult.OK };
        var web = new Button { Text = "Abrir la web", Left = 155, Top = 105, Width = 130, Height = 32 };
        web.Click += (_, _) => Process.Start(new ProcessStartInfo(config.workerUrl) { UseShellExecute = true });
        form.Controls.AddRange(new Control[] { label, box, ok, web });
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(10, 20, 40));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var gold = new Pen(Color.FromArgb(200, 170, 110), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(new Pen(Color.FromArgb(200, 170, 110), 2f), 1, 1, 29, 29);
            g.DrawLine(gold, 9, 22, 22, 9);
            g.DrawLine(gold, 9, 9, 22, 22);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
