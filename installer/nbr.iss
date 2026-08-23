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
; PER USER, INTO A FOLDER NBR CAN WRITE TO — AND THE PROGRAM DOES NOT WORK
; ANY OTHER WAY.
;
; Everything NBR keeps lives BESIDE THE EXE: Settings.ini, the pronunciation
; dictionaries, the stored service keys, the fetched voice catalogues and the
; cloud character count. That is a deliberately portable design, and it is fine
; — until the program is installed into Program Files, which a normal process
; may not write to.
;
; And there is no safety net. app.manifest declares
; requestedExecutionLevel="asInvoker", and the manifest's own comment says what
; that costs: "Specifying requestedExecutionLevel element will disable file and
; registry virtualization." So there is no VirtualStore to catch the writes —
; and IniFile.Save swallows its exception, which is the worst shape a failure
; can take: the reader changes a setting, hears nothing wrong, and finds it gone
; next launch. MEASURED 2026-08-23: an unelevated process writing to
; C:\Program Files gets UnauthorizedAccessException, full stop.
;
; So: lowest privileges, and the install goes to {localappdata}\Programs, which
; is what {autopf} resolves to under them. This is what Chrome and VS Code do,
; and it suits this audience twice over — no UAC prompt to navigate, and no
; question asked that a reader has no way to answer correctly.
DefaultDirName={autopf}\{#AppName}
PrivilegesRequired=lowest
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

; NO "for all users" CHOICE, deliberately. Offering it would offer a folder the
; program cannot write to, and the reader would have no way to know that the
; option they picked is the broken one. One kind of install that works beats two
; where one of them silently does not.
;
; The cost, stated: two people sharing a machine each install their own copy.
; For this audience that is nearly always one person to one machine, and a
; second copy costs 63 MB rather than anything a reader would notice.

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
; SETTINGS AND CACHE GO; WORK STAYS. Removing the stored service keys on
; uninstall is a small security gain rather than a loss — nobody wants their
; Azure key left on a machine they have finished with.
Type: files; Name: "{app}\Settings.ini"
Type: files; Name: "{app}\CloudUsage.ini"
Type: files; Name: "{app}\nbr-services.dat"
Type: files; Name: "{app}\azure-voices.txt"
Type: files; Name: "{app}\google-voices.txt"

; Dictionaries is NOT deleted, and neither is the library. A pronunciation
; dictionary is something a reader WROTE, rule by rule, to make one voice say a
; name properly; §8j keeps each scope in its own plain file precisely so it can
; be backed up or passed to somebody else. Leaving an empty folder behind is a
; far smaller fault than deleting an evening's work, and it is the same
; reasoning that keeps the uninstaller away from the library.
