; Nemoviz Book Reader — the installer.
;
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\nbr.iss
;
; Build the RELEASE configuration first (see CLAUDE.md for the MSBuild line);
; this script packages bin\x64\Release and nothing else.
;
; WHY INNO SETUP: it is free, it is what a Windows program of this size is
; normally packaged with, and it does a genuinely SILENT install (/VERYSILENT),
; which is what the Microsoft Store's policy 10.2.9 demands of an .exe. So the
; same script serves the GitHub beta now and the Store later, rather than being
; thrown away and written again.

#define AppName "Nemoviz Book Reader"
#define AppShort "NBR"
#define AppVersion "1.0.0"
#define AppRelease "Beta 1"
#define AppTag "beta-1"        ; the git tag, and what UpdateCheck.Release must match
#define AppPublisher "Nemoguća vizija"
#define AppUrl "https://github.com/gradic76/nemoviz-book-reader"
#define SourceDir "..\Nemoviz Book Reader\bin\x64\Release"

[Setup]
AppId={{7E3A9C21-5B4D-4F62-9A18-2C6D8E1F4B03}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppRelease}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\installer\out
OutputBaseFilename=NemovizBookReader-{#AppTag}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; x64 ONLY, AND THIS IS NOT A PREFERENCE. libmpv-2.dll is 64-bit; a 32-bit
; process loading it dies with 0x8007000B, which is the failure mode this
; project has already met on its own bin\Debug output. Refusing to install on
; a 32-bit Windows is far better than installing something that cannot start.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The licence the reader is shown before installing, and the notices they may
; want afterwards. Both ship in the Release output already.
LicenseFile=..\COPYING
InfoAfterFile={#SourceDir}\THIRD-PARTY-NOTICES.txt

; A per-machine install needs elevation; Inno asks for it only when the chosen
; folder needs it, so a reader without admin rights can still install into
; their own profile rather than being turned away.
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
; Nine of the eleven NBR speaks. Inno ships five of them itself; Croatian,
; both Serbians and Esperanto are among its UNOFFICIAL translations, which is
; why they are vendored into installer\languages rather than referenced out of
; Program Files — the script then compiles on any machine with Inno on it, and
; nobody has to remember to copy four files in first.
;
; Latin and Ancient Greek have no Inno translation and are not invented here.
; Those readers get the installer in English and the PROGRAM in their own
; language, which is the half that matters: the installer is seen once.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "hr"; MessagesFile: "languages\Croatian.isl"
Name: "sr"; MessagesFile: "languages\SerbianLatin.isl"
Name: "srcyrl"; MessagesFile: "languages\SerbianCyrillic.isl"
Name: "eo"; MessagesFile: "languages\Esperanto.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; EVERYTHING IN THE RELEASE FOLDER EXCEPT WHAT MUST NOT SHIP.
;
; recursesubdirs takes Lang, Help, Fonts, Licences and the 15 MB of liblouis
; tables with it. A hand-written file list is exactly the shape that drops the
; one table or the one .lang somebody needs, and it surfaces on their machine
; rather than ours — the same argument that decides the braille catalogue and
; the audio-only libmpv build.
;
; The exclusions are the point:
;   *.pdb            debug symbols, no use to a reader
;   Settings.ini     the DEVELOPER's settings, if a build ever leaves one there
;   nbr-services.dat stored API keys and the Azure pair — must never ship
;   *-voices.txt     the fetched cloud voice catalogues, tied to that account
;   CloudUsage.ini   this month's character count, which is nobody else's
; The last four are written beside the exe at runtime, so they cannot appear in
; a clean Release build — they are named anyway, because "cannot happen" is a
; poor reason to ship somebody's API key.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,Settings.ini,nbr-services.dat,azure-voices.txt,google-voices.txt,CloudUsage.ini"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Nemoviz Book Reader.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Nemoviz Book Reader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Nemoviz Book Reader.exe"; Description: "{cm:LaunchProgram,{#AppShort}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; What NBR writes beside itself while it runs. The LIBRARY is deliberately not
; here: it holds the reader's books, their positions and their bookmarks, it is
; usually somewhere else entirely (Settings → Library location), and an
; uninstaller that deletes somebody's library is a disaster, not a tidy-up.
Type: files; Name: "{app}\Settings.ini"
Type: files; Name: "{app}\CloudUsage.ini"
Type: files; Name: "{app}\nbr-services.dat"
Type: files; Name: "{app}\azure-voices.txt"
Type: files; Name: "{app}\google-voices.txt"
Type: filesandordirs; Name: "{app}\Dictionaries"
