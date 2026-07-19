using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecolectorAram;

class CollectorUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

static class CollectorUpdater
{
    public static bool IsNewer(string latest, string current)
    {
        return Version.TryParse(latest, out var latestVersion)
            && Version.TryParse(current, out var currentVersion)
            && latestVersion > currentVersion;
    }

    public static async Task<CollectorUpdateManifest> FetchManifest(HttpClient client, string workerUrl)
    {
        var endpoint = new Uri(new Uri(workerUrl), "/api/collector/version");
        using var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        var manifest = JsonSerializer.Deserialize<CollectorUpdateManifest>(await response.Content.ReadAsStringAsync());
        if (manifest == null
            || !Version.TryParse(manifest.Version, out _)
            || !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("El servidor entregó una actualización inválida.");
        return manifest;
    }

    public static async Task<string> DownloadAndVerify(HttpClient client, CollectorUpdateManifest manifest)
    {
        string updateDir = Path.Combine(Path.GetTempPath(), "RecolectorARAM", manifest.Version);
        Directory.CreateDirectory(updateDir);
        string installerPath = Path.Combine(updateDir, "Instalar-Recolector-ARAM-Caos.exe");

        try
        {
            using var response = await client.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(destination);

            await using var installer = File.OpenRead(installerPath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(installer)).ToLowerInvariant();
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La firma SHA-256 del instalador no coincide.");
            return installerPath;
        }
        catch
        {
            try { File.Delete(installerPath); } catch { }
            throw;
        }
    }
}
