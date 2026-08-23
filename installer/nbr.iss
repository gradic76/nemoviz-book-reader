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
; PROGRAM FILES, where a Windows program belongs (Gordan, 2026-08-23:
; "Konvencija je još od Win 95 da aplikacije idu u Program Files").
;
; This was per-user for one afternoon, because NBR kept everything BESIDE THE
; EXE and Program Files is not writable by a normal process — so installing it
; correctly would have left it unable to save a single setting, silently
; (IniFile.Save swallows its exception, and app.manifest's asInvoker disables
; the VirtualStore that might otherwise have caught the writes). Moving the
; install was sidestepping the fault. UserData fixes it instead: everything NBR
; WRITES now lives in %APPDATA%\Nemoviz Book Reader, and only what it READS —
; the 480 braille tables, the eleven language files, the manuals, the fonts, the
; 32-bit speech host — is installed here, where every account on the machine
; shares one copy of it.
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

; The reader may still choose to install into their own profile instead — Inno
; asks for elevation only when the chosen folder needs it, so somebody without
; administrator rights is not turned away. Both choices work now, which is what
; makes offering them honest: the settings go to %APPDATA% either way.
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
; None of the five is written beside the exe any more — they live in %APPDATA%
; since 2026-08-23 — but a DEVELOPER's build folder still collects them from
; older runs, and "cannot happen" is a poor reason to risk shipping an API key.
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
; NOTHING OF THE READER'S IS DELETED, and after the move to %APPDATA% that is
; the whole of this section.
;
; Their settings, dictionaries, keys and counter are in
; %APPDATA%\Nemoviz Book Reader, and their books are in the library — usually
; somewhere else again (Settings → Library location). An uninstaller has no
; business in either. A reader who uninstalls to try a newer build, or who
; reinstalls after a Windows repair, finds everything as they left it; one who
; genuinely wants it all gone can delete one folder they can name.
;
; This is a REVERSAL of what stood here for one afternoon, when those files were
; beside the exe and leaving them meant leaving litter in Program Files. Now
; that they are in the profile, deleting them would be deleting the reader's
; work from a place Windows itself treats as theirs.
;
; CHECKED rather than assumed, since an empty section invites somebody to fill
; it: nothing else is written here either. SignalTones and SapiWavPlayer both
; put their temporary WAVs in Path.GetTempPath(), and the speech cache lives
; inside the book's own folder. There is genuinely nothing left to remove.
