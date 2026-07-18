; Instalador del Recolector — Ranked ARAM Caos
; Compilar: ISCC.exe installer.iss   (requiere publish\ ya generado y
; publish\config.json con el token real del grupo)

[Setup]
AppId={{8F4A1C2E-ARAM-CAOS-RANKED-000000000001}
AppName=Recolector ARAM Caos
AppVersion=1.1.0
AppPublisher=Ranked ARAM Caos (grupo)
DefaultDirName={localappdata}\Programs\RecolectorARAM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=Instalar-Recolector-ARAM-Caos
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Si el recolector está abierto, el instalador pide cerrarlo
AppMutex=RecolectorARAM-instancia-unica
UninstallDisplayName=Recolector ARAM Caos

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar con Windows (recomendado: así nunca se pierde una partida)"
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; Flags: unchecked

[Files]
Source: "publish\v1.1.0\RecolectorARAM.exe"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RecolectorARAM"; ValueData: """{app}\RecolectorARAM.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Icons]
Name: "{userprograms}\Recolector ARAM Caos"; Filename: "{app}\RecolectorARAM.exe"
Name: "{userdesktop}\Recolector ARAM Caos"; Filename: "{app}\RecolectorARAM.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RecolectorARAM.exe"; Description: "Abrir el recolector ahora"; Flags: postinstall nowait

[UninstallDelete]
Type: files; Name: "{app}\seen.json"
Type: files; Name: "{app}\recolector.log"
Type: files; Name: "{app}\config.json"
Type: files; Name: "{app}\first-kill.json"
