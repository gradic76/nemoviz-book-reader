# CLAUDE.md — Nemoviz Book Reader (NBR)

This file is the persistent project brief for Claude Code. Read it fully at
the start of every session. It replaces the per-session "recap" documents the
project used before moving to Claude Code. When something here goes stale,
update this file rather than letting the code and the brief drift apart.

---

## 1. What NBR is

Nemoviz Book Reader (NBR) is a **Windows Forms desktop audiobook player**
written in **C#** targeting **.NET Framework 4.8**. Its entire reason for
existing is **accessibility**: it is built first and foremost for blind and
visually impaired users who navigate by screen reader and keyboard, not by
mouse. Every design decision is weighed against "how does this behave under a
screen reader" before anything else.

The player uses **libmpv** (`libmpv-2.dll`, P/Invoke) as its audio engine and
**TagLib#** (`TagLibSharp` NuGet package) for reading audio metadata and
durations. It plays classic multi-file audiobooks (a folder of MP3/other
audio files that together make one book) and is being built toward DAISY and
text-book support later.

The developer is **Gordan**. Working language between Gordan and Claude is
**Croatian**. All code identifiers, comments, and the primary language file
are **English**.

---

## 2. Accessibility is the prime directive

This is not a normal WinForms app. Read this section carefully — most of the
non-obvious code choices exist because of it.

- **Primary screen reader: JAWS.** Gordan develops and tests with JAWS.
  **NVDA is a secondary control check**, not the primary target. When a
  behavior must be tuned for one reader, JAWS wins.
- Gordan tests by ear and **reports the exact string the screen reader
  speaks**. When he says JAWS announced "Playback information, I edit Read
  Only", that literal string is the diagnostic. Treat reported reader output
  as precise data, not paraphrase.
- **Keyboard-only operation is mandatory.** Everything must be reachable and
  operable without a mouse. Mouse/tooltip support is a nice-to-have layered on
  top, never a substitute.
- **`AccessibleName` carries keyboard shortcuts**, because JAWS does not read
  tooltips on tab focus. Convention: the accessible name embeds the shortcut,
  e.g. "Back, Shift+Left", "Forward, Shift+Right", "Play, Space",
  "Go To, Ctrl+G", "Sleep Timer, Ctrl+T". Tooltips are separate and for
  sighted/mouse use.
- **Screen-reader announcements** of transient changes (volume, speed, timer
  set/cancelled, info-on-demand, bookmark set) go through
  `AnnounceToScreenReader(label, text)`. **As of Session 10 this speaks the
  text WITHOUT moving focus, via two channels, each picked up by exactly one
  reader:**
  - **JAWS** — a UIA notification event (`UiaRaiseNotificationEvent` on a host
    provider from the form's HWND, `NotificationProcessing.MostRecent` so the
    reader drops stale queued values during fast key-repeat).
  - **NVDA** — the NVDA Controller Client (`NvdaController.Speak` →
    `nvdaControllerClient.dll`, x64, vendored; `cancelSpeech` before
    `speakText` gives the same drop-stale behaviour). NVDA drives WinForms via
    MSAA and **ignores** the UIA notification, so it needs its own channel;
    JAWS in turn ignores the NVDA client, so calling both never double-speaks,
    and each call is a silent no-op when its reader isn't running.

  This replaced the old approach of briefly focusing an off-screen `Label` and
  restoring focus after ~150 ms, which stole focus (rapid key-repeat overlapped
  the focus shuffles and choked the reader) and which NVDA largely ignored. The
  `label` parameter and the off-screen `lblAnnounce*` controls are now
  **vestigial** (kept only so call sites didn't change; safe to remove).

- **The volume/speed value fields (`tbVolume`, `tbSpeed`) and the arrow-key
  read.** These are read-only display fields you can Tab to. Volume changes
  with **Up/Down**, which a screen reader treats as edit caret-navigation and
  so speaks the focused field's current line on every press — a second
  utterance on top of the announcement. `AccessibleRole = StaticText`
  cleans up the focus announcement (drops "read only edit") but does **not**
  stop that read (JAWS keys off the underlying Edit window class). The fix:
  when such a field is focused, let that arrow read BE the feedback — keep the
  field's `Text` current so the spoken line is correct, and **skip** both our
  own announcement and any `AccessibleName` change (whose change re-triggers
  the name announcement). See `ChangeVolume`. Speed is unaffected (Page
  Up/Down aren't caret keys) but shares the same pattern for consistency. If
  this ever regresses, the fallback is making the fields non-focusable
  (`TabStop = false`) and relying solely on the announcement.

### The info box lesson (do not regress this)

The playback info box (`tbInfo`) was the single biggest accessibility
battle. Rules that must not be undone without a very good reason:

- It is a **plain multiline read-only `TextBox`** (an EDIT control),
  deliberately **NOT a `RichTextBox`**. JAWS treats rich edit controls
  specially and re-read the whole box's content every time the user tabbed to
  a neighboring control. A borderless, form-colored look made it worse (the
  screen model tied it to neighbors as static text). The plain TextBox with a
  standard `SystemColors.Window` background and normal border behaves quietly,
  like the other read-only fields.
- It is a **snapshot, not a live ticker.** It is rebuilt only: on focus
  (its `Enter` event), on part change (skipped if it currently has focus),
  and on demand via the **I key**. There is **no periodic refresh** of the
  info box — a constantly-changing edit control under the reader cursor causes
  chatter. Live position instead lives in the separate Position field.
- The **I key** announces fresh info from anywhere in the player through the
  off-screen `lblAnnounceInfo` label, **without touching `tbInfo`** (no text
  change, no echo).

### The "focus echo guard" pattern

Any control whose text changes while it may hold screen-reader focus uses the
same guard: **do not update it on every tick while it has focus; do one fresh
update on its `Enter` event** so the value announced on focus is current.
This is used by the info box and by the Sleep Timer button countdown. Reuse
this pattern for any future live-updating control.

---

## 3. Established workflow and hard rules

These are Gordan's standing preferences. Honor them unless he says otherwise.

- **Complete files, never partial snippets** — historically Claude returned
  whole regenerated files because pasting partial diffs into chat was
  error-prone. In Claude Code, prefer **precise, surgical edits to the real
  files on disk** (that is the point of Code), but never hand back a fragment
  and ask Gordan to splice it manually. Either edit the file directly or
  provide the whole file.
- **English is the only language file until the app is feature-complete.**
  `en.lang` is the single source of truth for all user-visible strings
  (button text, accessible names, tooltips, dialog messages, announcements).
  A Croatian `hr.lang` will be produced as a *translation pass at the end*,
  not maintained in parallel now. When you add a user-visible string, add its
  key to `en.lang`; do not hardcode strings in the C#.
- **Token/usage awareness** — Gordan batches multiple requests into one
  message to save usage, and the 5-hour limit is a rolling token budget, not
  wall-clock. Full-file regeneration in chat burned tokens fast; Code's
  surgical edits should help. Group related changes.
- **Grammar**: "rewound" is the correct past participle (not "rewinded").
- **Git is the safety net.** Before making changes in a session, the working
  state should be committed so there is a point to return to. Do not perform
  destructive git operations (hard reset, force, clean) without explicit
  confirmation. Prefer a commit per working session with a descriptive
  message; these commits also serve as the development chronicle that the old
  recap documents used to provide.

---

## 4. Project files (source of truth is the disk)

Approximate roles — read the actual files for detail:

- **Form1.cs** — the player window. The big one (~1500+ lines). Holds the MPV
  P/Invoke layer, the 3×4 control grid, all keyboard handling, the virtual
  timeline, progress/info display, book loading, and the Sleep Timer. See the
  layout and navigation sections below.
- **SleepTimerForm.cs** — the Sleep Timer modal dialog (added Session 8).
- **GoToForm.cs** — the Go To (named navigation) modal dialog (Session 7).
- **ManageBookmarksForm.cs** — the Manage Bookmarks modal dialog (Session 9).
- **SettingsForm.cs** — the Settings dialog (Session 9 shell, wiring started
  Session 10 — see section 8b).
- **NvdaController.cs** — thin P/Invoke wrapper over the vendored
  `nvdaControllerClient.dll` (x64, LGPL 2.1, in the project root next to
  `libmpv-2.dll`; `nvdaControllerClient-license.txt` beside it). Speaks
  through NVDA without focus change; see section 2.
- **DaisyParser.cs** — parses a DAISY audio book folder into a `DaisyBook`
  (title/author from metadata, `AudioPlayOrder`, `Headings`, `Pages`; each nav
  point resolved to an audio file + clip-begin seconds). Wired into import,
  playback, Go To, metadata, and the seek step — see section 8c.
  `DaisyParser.TryParse(folder)` returns null for non-DAISY and never throws.
- **LibraryForm.cs** — the Library window (book shelf, search, filter, sort,
  context actions).
- **BookData.cs** — a single book: metadata, progress, the virtual timeline
  data (`Chapters`, `Offsets`, `TotalDuration`, `BuildChaptersFromFolder`,
  `LoadChapters`, `SaveChapters`), and bookmarks (`Bookmarks`, `AddBookmark`,
  `SetBookmarks`, `SaveBookmarks`). Persists to `Book.ini` inside each book
  folder: `[Chapters]` as `File0=name.mp3|614.5` (filename|duration-seconds),
  `[Bookmarks]` as `Bookmark0=<virtual-seconds>`.
- **AppSettings.cs** — global app settings and paths (library folder, lang
  folder, `Settings.ini`). Builds paths relative to the app location. Holds
  things like `LastOpenedBookPath`, `GoToAutoPlay`.
- **Localization.cs** — the localization engine. Scans a `Lang` folder for
  `*.lang` files (plain `Key=Value` UTF-8, `;`/`#` comments, `\n`/`\r\n`
  escapes), English as fallback, then the raw key if all else fails. API:
  `Localization.T("Key")` and `Localization.T("Key", args...)`
  (string.Format semantics).
- **IniFile.cs** — INI read/write helper, with defensive try/catch on save
  (a book's folder can vanish while background timers still run).
- **en.lang** — the English language file (in the `Lang` folder; set to copy
  to the build output). Single source of truth for user-visible strings.

`bin/`, `obj/`, and `.vs/` are generated — they should be git-ignored and
never hand-edited.

---

## 5. Player layout — the 3×4 grid

The player's bottom panel is a 3-column × 4-row proportional grid inside a
640-px-wide window. Columns: **A = 160 px** (app-level), **B = 320 px**
(playback, double width), **C = 160 px** (book tools).

|     | A (160)         | B (320)                       | C (160)             |
|-----|-----------------|-------------------------------|---------------------|
| 1   | Library         | Seek step (dropdown)          | Properties          |
| 2   | Settings        | Back / Play-Pause / Forward   | Go To...            |
| 3   | Sleep Timer     | Volume / Speed                | Set Bookmark        |
| 4   | Help            | Position (progress field)     | Manage Bookmarks    |

- **Tab order is column-major**: A (app) → B (playback) → C (book tools).
- Above the grid sits the top panel with the read-only info box (`tbInfo`).
- Placeholder "coming soon" dialogs still exist for: Settings, Properties,
  Help. Set Bookmark and Manage Bookmarks are implemented (Session 9, see
  section 8).

---

## 6. Navigation — four layered levels

Documented in a comment above the seek-step methods in Form1. All four coexist:

1. **Left/Right arrows** — plain 5-second seek, like any player. Intercepted
   even when the seek dropdown has focus (so Left/Right always means seek).
2. **Ctrl+1..9** — percentage jumps to 10%–90% of the whole book's virtual
   duration.
3. **Shift+Left / Shift+Right, media Next/Prev, and the on-screen Back/Forward
   buttons** — jump by the step currently selected in the seek dropdown.
   **Shift+Up / Shift+Down change which step is selected** (`ChangeSeekStep`,
   announced); Down moves *down* the list, Up back up, matching its order.
   The dropdown is **dynamic and ordered coarsest → finest** (largest jump
   first, so the default first row is the biggest unit), rebuilt per book by
   `RebuildSeekSteps()`:
   - **plain audio:** Part, [Bookmark], 5 min, 1 min, 30 s, 15 s
   - **DAISY:** Heading 1, Heading 2, … , Page, [Bookmark], 5 min, 1 min,
     30 s, 15 s
   "Part" (plain audio only) uses `PartForward()` / `PartBack()` (Back logic:
   more than 3 s into the current part rewinds to its start, otherwise the
   previous part). **DAISY headings follow the talking-book level model** —
   one step per heading depth present, where "Heading N" navigates every
   heading of depth ≤ N (H1 = only top-level, larger N = finer). "Page"
   appears only for a DAISY book that has a pageList. Headings/pages jump via
   the generic `StructForward`/`StructBack` (same 3-second Back grace as Part).
   "Bookmark" appears only while the book has ≥1 bookmark; `BookmarkForward()`
   jumps to the next bookmark, `BookmarkBack()` mirrors the 3-second grace.
   Each row is a `SeekStep` (kind + heading depth) held in a list parallel to
   the combo — no fixed indices. The selected step is **remembered per book**
   in `Book.ini` (`[Settings] SeekStep`, encoded: heading depth L → 100+L,
   else the kind ordinal; `-1` = never chosen → defaults to the first row).
4. **Go To... (Ctrl+G)** — named navigation. For plain audio this is a list
   of the book's parts. DAISY/text structure (headings, pages) will plug in
   here later as a separate subsystem.

**Why Shift, not Ctrl, for the seek cluster (Session 10).** Ctrl+Up/Down are
unusable as app shortcuts: when focus is on a non-edit control (a button) the
shell/reader eats the Ctrl and even sends focus off to the desktop; the app
only ever receives a bare "Up"/"Down" (confirmed by a keyData diagnostic).
Ctrl+**Left/Right** are fine (the shell doesn't grab horizontal arrows), so
**speed** lives there. Shift+arrows reach the app on buttons in both readers,
so the whole seek cluster is on Shift. Cosmetic JAWS-only side effects remain
and are accepted: Shift+arrow makes JAWS say "selected", and Ctrl+Left/Right
(word-nav keys) make it re-read the focused control — both are the reader's
built-in arrow semantics, not fixable from the app (AccessibleRole.Application
on the form did not change JAWS here).

**The seek dropdown is keyboard-inert** (`cmbSeek.KeyDown` swallowed): it is a
display, changed only via Shift+Up/Down or the mouse. So plain Up/Down are
volume even when it has focus.

### Virtual timeline

A book is many files but presents as one continuous timeline.
`GetVirtualPosition()` and `SeekToVirtualPosition()` convert between the
current file + in-file position and a single virtual position, using
`BookData.Offsets` (cumulative start time of each part) and `TotalDuration`.
Cross-file seeks use `playlist-play-index` then an absolute seek after a short
delay once the new file has loaded, staying paused if playback was paused
(avoids an audible blip of the wrong position before the seek lands).

### Multimedia keys (WM_APPCOMMAND)

Handled in `WndProc`: Play/Pause (and separate Play, Pause), Next/Prev →
seek step. **Only while NBR has focus.** With no file loaded the message is
passed through to the system (`base.WndProc`) so an empty player doesn't pop
the Open File dialog and other media apps still react. Volume keys are left to
the system. A future Settings option may add a global mode (`RegisterHotKey`)
and an off switch.

### Other player keys

- **Space** — Play/Pause (the only key for it; X was removed in Session 10).
- **Up/Down** — volume ±5 (announced; beep at 0 and at 100).
- **Ctrl+Left/Right** — speed ±10% (range 50–300%; double beep at 100%).
  Replaced PageUp/PageDown in Session 10.
- **I** — announce fresh playback info.
- **Ctrl+O** — Open File.
- **Ctrl+G** — Go To.
- **Ctrl+T** — Sleep Timer.
- **Ctrl+B** — Set Bookmark.
- **Enter** — activates the focused button. (Note: Space does NOT activate a
  focused button — it is globally Play/Pause — so JAWS's generic "press
  spacebar to activate" hint on buttons is misleading; most users have that
  hint off. Buttons activate with Enter.)

---

## 7. Sleep Timer (Session 8)

Playback-coupling bugs found in Session 9 testing are fixed: opening the
`SleepTimerForm` dialog now pauses playback first if it was running (and
resumes on Cancel only if it had been playing before the dialog opened;
paused stays paused either way), and the countdown's spoken `AccessibleName`
now carries the same min:sec / h:min:sec format as the visible button text
(`FormatCountdown`) instead of a bare rounded-up minute count. Otherwise
unchanged from the description below.

Lives in Form1 (`sleepTimer` + state fields) plus the `SleepTimerForm` dialog.
Nothing about the timer is persisted — it is a one-shot, session-only thing.

**Dialog** (`SleepTimerForm.cs`): two radio groups in group boxes — Duration
(15/30/45/60 min, default 30, plus a Custom radio that enables a
`NumericUpDown`, range 1–720). When Custom is picked, the spin box is focused
and its value select-all'd so a typed number overwrites immediately; arrows
still step. Action group ("When the time expires"): Stop / Stop + close /
Stop + close + shut down. Enter = Start, Escape = Cancel.

**Playback coupling (the defining rule).** The timer exists because someone is
listening and plans to fall asleep — it is **not** a standalone shutdown
scheduler. Therefore:

- A timer can only be set with something loaded; on an empty player the button
  gives a short low beep (same as Ctrl+G with no book).
- **Starting a timer starts playback** if it isn't already playing.
- A **manual pause cancels the timer**, with an announcement. The cancel hook
  lives **only** in the pause branch of `BtnPlayPause_Click` — every
  user-initiated pause (Space, X, on-screen button, media keys) routes
  through there, while **programmatic** pauses (cross-file seeks, book
  loading, the timer's own expiry) call mpv directly and must **not** cancel
  the timer. Keep this distinction intact.
- **Changing the book** (library pick or Ctrl+O) cancels the timer, same
  announcement.
- Seeking, volume, speed, part navigation, and Go To do **not** touch the
  timer.
- Pressing the button (or Ctrl+T) **while a timer is active stops playback and
  cancels the timer** — one press, no new dialog. To set another, press the
  now-idle button again.

**Countdown display.** Shown on the button text via `FormatCountdown`: minutes
and seconds ("60:00", "9:59"); the hour part appears only while **more than
60 minutes** remain ("1:29:59"). The button uses the focus echo guard (no
per-tick update while focused; one refresh on `Enter`). The countdown is
wall-clock (a `DateTime` deadline, not accumulated ticks) so a busy UI thread
can't make it drift.

**Audible signals.** A series of three ~900 Hz beeps at **-5 min** (skipped if
the timer was set to 5 minutes or less, so it doesn't fire instantly). Then a
smooth **volume fadeout over the last 45 seconds** (`SleepFadeSeconds`):
a linear ramp from the user's set volume to 0 at the deadline, applied
**only to mpv** — `currentVolume`, the Volume field, and `Book.ini` never see
the faded values, so the saved volume stays correct. The fade is undone
(`RestorePlaybackVolume`) whenever the timer ends or is cancelled, so a later
resume plays at the user's set level. On expiry the code **pauses first, then
restores volume** so the fade doesn't end with a full-volume blip.

**Expiry actions** (`ExecuteSleepTimerAction`): all three pause playback and
save progress. **Stop** announces "Sleep timer finished, playback stopped" and
NBR stays open. **Stop + close** calls `this.Close()` (OnFormClosing saves
again and tears down MPV). **Stop + close + shutdown** runs
`shutdown /s /t 5` — a few seconds of grace so NBR can close cleanly, by
design no long safety countdown — then closes.

**Natural-end override.** If the book plays to its own end before the deadline
(`FinishCurrentBook`), the end of the book counts as the end of the listening
session. For **Stop**, the stop already happened naturally, so the timer is
quietly dropped and the normal finish flow runs (book marked read, library
opens). For **close/shutdown**, the action fires immediately via
`BeginInvoke`, and the library is deliberately **not** opened first (no point
for an app that's closing, and `Close()` under a fresh modal dialog would be
fragile).

**Known edge case (still to verify — see TODO).** If a book finishes while the
Library window is *manually* open and playback was running in the background,
a close/shutdown action would fire with `isLibraryOpen == true`, i.e.
`Close()` under a modal dialog. Shutdown still works (the system command is
issued before closing); "Stop + close" in that combination needs testing, and
if fragile, the fix is to close any open dialogs before `Close()`.

---

## 8. Bookmarks (Session 9)

Storage in `Book.ini`, `BookData.Bookmarks` (`List<double>`, virtual-timeline
seconds only — no stored label; always kept sorted ascending).
`[Bookmarks]` section, keys `Bookmark0=<seconds>`, `Bookmark1=...`. Display
name ("Bookmark 01 (H:MM)") is always computed live from sorted position,
never persisted as text — padding goes to 3 digits past 99 bookmarks.
`IniFile.DeleteSection` was added to support rewriting a shrunk list.

**Set Bookmark (Ctrl+B).** No book loaded → the same low "no go" beep as
Go To/Sleep Timer. With a book: adds the bookmark at the current virtual
position, plays an ascending series of five short beeps (~1 s total), and
announces only **"Bookmark set"** via the off-screen label — deliberately no
position/percent, that level of detail belongs to the Manage dialog. Does
not touch playback or the Sleep Timer.

**Manage Bookmarks** (`ManageBookmarksForm.cs`, modeled on `GoToForm`).
Single-select `ListBox`; no multi-select/multi-delete by design (a failsafe
Gordan asked for explicitly). Buttons: **Delete**, **OK**, **Cancel** — no
separate "Play" button (dropped after review; duplicated OK's job).
- **Delete** (button, context menu, or Del key) stages a removal in a working
  copy and **clears the selection** — nothing is written to `Book.ini` until
  OK, and the user must deliberately pick a bookmark again before OK will
  jump anywhere.
- **OK** (button, double-click, or Enter on the list) commits the working
  copy and, if a bookmark is currently selected, jumps to it and resumes
  playback there. If nothing is selected, it just persists and restores
  whatever playback state was in effect before the dialog opened.
- **Cancel** discards the working copy entirely and restores the pre-dialog
  playback state.
- **Ctrl+Space** on the list toggles the current row's selection off/on
  (deselect so OK won't jump anywhere, or re-select the last one) with an
  off-screen-label announcement of **"Selected"/"Not selected"** — lets the
  user "arm/disarm" the jump without invoking Delete. The dialog has its own
  off-screen announce label + focus-restore timer, same pattern as Form1's
  `AnnounceToScreenReader`.
- Opening the dialog pauses playback first if it was running (same coupling
  as the Sleep Timer dialog — a direct mpv call, so an active Sleep Timer is
  untouched).

**Bookmark seek step.** A "Bookmark" option is appended to the seek dropdown
(after "Part") only while the current book has ≥1 bookmark
(`UpdateSeekStepBookmarkOption`, called after every bookmark add/delete/book
load/unload). `BookmarkForward()` jumps to the next bookmark after the
current position. `BookmarkBack()` mirrors Part's 3-second grace: more than
3 s past the preceding bookmark rewinds to it, otherwise jumps to the one
before it; at the very first bookmark it always rewinds to itself (nothing
earlier to fall back to). Both preserve play/pause state like any other
virtual-position seek.

---

## 8a. Archive import — .zip/.rar/.7z (Session 9; multi-volume + password Session 11)

`LibraryScanner.cs` recognizes all three formats via **SharpCompress** (NuGet,
manually vendored into `packages/` — no `nuget.exe` on this machine, so the
`.nupkg`s were downloaded and unpacked by hand and wired into the
`.csproj`/`packages.config` the same way the other hand-added packages already
were).

**Extraction engine** (`ExtractArchive(path, destFolder, passwordProvider)`):
- **Multi-volume** sets are discovered from the first part via
  `ArchiveFactory.GetFileParts` and opened together
  (`OpenArchive(IReadOnlyList<FileInfo>, …)`). Supports RAR (`.partN.rar`, or
  old `.rar`+`.rNN`), numeric split (`.7z.001/.002`, `.zip.001`), and spanned
  ZIP (`.z01…`+`.zip`). `IsExtractableArchive` picks the entry-point part;
  `IsVolumeContinuation` skips the rest; `BaseArchiveName` strips volume
  suffixes for the folder name.
- **RAR is streamed** with the dedicated `RarReader.OpenReader(volumes, …)`,
  not the random-access Archive API: a file spanning a volume boundary breaks
  per-entry extraction ("unpacked file size does not match header"), and
  `archive.ExtractAllEntries()` refuses a non-solid RAR. **ZIP/7z** branch on
  `archive.IsSolid || Type == SevenZip`: **solid/7z** extract through the forward
  reader (`ExtractAllEntries()` → `MoveToNextEntry`/`WriteEntryToDirectory`) —
  a *solid* archive shares one compression stream, so random-access
  `entry.WriteToDirectory` would re-decompress the whole block per entry (O(N²),
  pegged a core for 15+ min on a 683 MB audiobook); the forward reader is O(N).
  **Non-solid ZIP** uses per-entry random access — `ExtractAllEntries()` *throws*
  on it ("can only be used on solid archives or 7Zip archives"). (That guard was
  a regression from the initial solid-7z fix, which used the forward reader for
  all non-RAR archives and broke every plain ZIP, incl. DAISY-in-zip.) RAR-vs-
  other is told by extension, or by header-sniff (`ArchiveFactory.IsArchive`)
  for `.001`.
- **Hardening** (against carelessly/maliciously packed archives): entries whose
  name is absolute/drive-rooted or climbs out with `..` are skipped
  (`IsUnsafeEntryPath` — path-traversal / "zip slip" guard); nested
  archive-in-archive auto-extraction is capped at `MaxArchiveDepth` (3) so a
  zip-in-zip set can't recurse without bound. `ImportDiag` (→
  `%TEMP%\NBR-import-diagnostic.log`) logs begin/open/summary/skips/exceptions
  and samples per-entry timing (first 3 + every 25th).
- **Background extraction + progress** (`ExtractProgressForm` in libraryform.cs):
  import runs the extraction on a background thread behind a modal progress
  dialog (determinate bar for 7z/zip where the file count is known, marquee for
  RAR), so the window no longer freezes for the whole extraction.
  `ExtractArchive`/`TryExtract` take an `Action<int,int> progress`; the password
  prompt is marshalled back to the UI thread. Outcome flows back via
  `Error`/`Cancelled` into the existing import error handling. (The post-extract
  steps — ResolveBookFolder, chapter/duration build, LoadBooks rescan — still run
  on the UI thread; seconds, not minutes.) Still open (offered, not yet done):
  uncompressed-size / free-disk cap (zip-bomb); append-only library refresh
  instead of full rescan; clearer messages for an unsupported codec (7z PPMd) or
  a header-encrypted 7z; spoken progress for screen-reader users.
- **Password**: `ReaderOptions.Password`. `ExtractArchive` tries with no
  password first; if that hits a crypto/"password"/"encrypt" error
  (`IsPasswordError`), it calls the `passwordProvider` (the UI shows
  `ArchivePasswordPrompt`, an accessible masked-textbox modal) and retries,
  re-prompting on a wrong password. The password is held **in memory only** —
  never stored or logged. A null provider (background scan) throws
  `ArchivePasswordRequiredException`; a user cancel throws
  `OperationCanceledException`.

- **Background scan** (`LibraryScanner.ExtractAndScan`, private): a loose
  archive sitting inside a folder being scanned (library root on
  startup/refresh, or a source folder for "Add Folder") is extracted next to
  itself, recursed into, and the **original archive is deleted** — it's
  already inside library-owned space, nothing left to keep. Corrupt or
  password-protected archives are skipped silently so one bad file doesn't
  stop the whole scan. This already existed for `.zip` pre-Session-9; now
  generalized to all three formats.
- **Direct user action** (Library "Add File" → `ImportFile`, Player Ctrl+O →
  `OpenArchiveFile`): extracts straight into the book's permanent library
  folder (no temp staging). After extraction, `ResolveBookFolder` names the
  book after the **innermost wrapper folder** (the one closest to the files,
  e.g. "Author - Title") rather than the archive file — descending pure
  single-subfolder chains; it keeps the archive name only when content sits at
  the root, and won't clobber an existing book of that name (falls back to
  `FlattenSingleWrapperFolder`). This matches what the background scan already
  does by recursing to the media folder. The **source archive is left
  untouched** (only the background-scan case deletes it). Failures surface as
  an error dialog; a cancelled password prompt just removes the empty folder.
- **"Open folder" refuses archives**: `ImportFolder` shows an info dialog
  (`Dialog.ArchiveInFolder.*`) pointing the user to "Open file" when the
  chosen folder holds archive/volume files (`ContainsArchiveFiles`) — the
  folder path for multi-volume archives is unreliable.
- `ArchivePasswordPrompt.cs` is the accessible password modal. Runtime-verified
  against real samples (Session 11): single & multi-volume, with/without
  password, all three formats — RAR multi-volume needed the streaming reader.

---

## 8b. Settings dialog (Session 9 shell; wiring started Session 10)

`SettingsForm.cs` (takes an `AppSettings`). Classic dialog, `chkShowHints`
checkbox at the top (planned global switch for the hint-box pattern — not yet
wired), then a `TabControl`: **General**, **Audio Books** (WIP placeholder),
**Text Books** (language/engine/voice combos + speed/volume/pitch sliders +
a "coming soon" note for low-vision/dyslexic reader options), **Device**
(sound card combo), **Misc** (WIP placeholder). OK / Cancel / **Apply** at the
bottom; OK and Apply both call `SaveSettings()` (OK also closes), Cancel
discards.

**General tab** is where real wiring began:
- **Library location** — read-only textbox showing the path + **Browse...**
  (`FolderBrowserDialog`). Browse only *stages* the choice; `SaveSettings`
  persists via `AppSettings.SetLibraryPath` + `EnsureLibraryExists`, and only
  if it actually changed. This is the first genuinely functional control.
- **Language** combo — app UI language. Lists only English for now
  (`LanguageName`); **not wired** to `AppSettings.SetLanguage` yet because
  there's nothing to switch to until `hr.lang` exists (end-of-project
  translation pass).
- The two multimedia-key checkboxes are still unwired placeholders.

Everything else (Text Books, Device, sliders, hints toggle) is still
scaffolding to fill in as each subsystem is built. Sound processing was
explicitly deferred by Gordan ("a bit complicated, for later").

---

## 8c. DAISY audio books — parser (Session 11, Phase 1)

`DaisyParser.cs`. A DAISY audio book is just a folder of audio (MP3) plus a
navigation layer, so it overlays cleanly on the existing concatenated-audio
virtual timeline: **audio playback is unchanged**; DAISY only adds headings +
pages, each at a known audio position. Two formats, both handled:

- **DAISY 2.02** — `ncc.html` (headings `h1`–`h6`, pages `span.page-*`, each an
  `<a href="file.smil#fragment">`) + per-section SMIL (`<audio src=... clip-begin="npt=12.5s">`).
- **DAISY 3 / Z39.86** — `.opf` (spine = SMIL play order, `dc:Title` etc.) +
  `.ncx` (navMap navPoints → headings, level = nesting depth; pageList → pages)
  + SMIL (`clipBegin="00:00:12.500"`). A book may ship both (hybrid) — NCC wins.

Model: `DaisyBook` { Version, Title, Author, TotalTime, `AudioPlayOrder`
(distinct audio files in reading order, from master.smil / NCC order / OPF
spine), `Headings`, `Pages` }. Each `DaisyNavPoint` = (Level, Label, AudioFile,
ClipBegin-seconds). `TryParse(folder)` finds the nav file **recursively** (it
may sit in a wrapper subfolder), never throws, returns null if not DAISY.

Hard-won parsing details (verified against 7 real books from different
libraries — see `D:\Test naslovi\Daisy Audio`):
- **Fragment → audio** is resolved by position: id → the first `<audio>` at or
  after it. DAISY 3 puts the nav id on the enclosing `<seq>`, DAISY 2.02 on the
  `<text>`; a par-only scan misses the seq case (all headings collapsed to the
  first clip). Position-based scan handles both.
- **Encoding**: Croatian/CE books from Windows producers routinely declare
  `iso-8859-1` but the bytes are **windows-1250** (iso-8859-1 has no č/ć/š/ž).
  Heuristic: if the declared charset is latin-1/ascii family AND `dc:language`
  is Central-European (hr/sr/cs/…), decode as 1250. Honors real UTF-8/declared
  charsets otherwise.
- Producers lie: e.g. one sample's author is literally "Creator name". Parse
  faithfully; don't invent.

**Phase 2 — wired into the app (Session 11).** On import a DAISY book is
detected (`DaisyParser.TryParse`) and its content flattened to the book root
(`FlattenDaisyToRoot`); `BuildChaptersFromDaisy` builds the virtual timeline in
`AudioPlayOrder` (reading order, *not* the alphabetical sort plain audio uses —
so nav positions line up), and `BuildDaisyNav` resolves each heading/page to an
absolute virtual position (`offset(audioFile) + clipBegin`), stored on
`BookData` (`IsDaisy`, `DaisyHeadings`, `DaisyPages`). Playback follows the
`Chapters` order first (see Form1 `LoadBook`) so files play in reading order.
DAISY carries real metadata, so a book shows a separate **Author** + **Title**
(both drive the shelf's "Author — Title" line and the player title bar/info
box), and Format becomes `"Daisy <version>, <sample rate>, <bitrate>,
<channels>"`. **Go To** lists the headings by their bare tagline in reading
order (no numbering — the label self-describes); the **info box** shows the
current heading's tagline. The **seek step** gains per-depth **Heading** levels
and, when present, **Page** — see section 6. Producers who leave metadata blank
(e.g. Obi's "Untitled Obi Project") are shown as-is; the user fixes them with
F2 rename (which offers Author + Title for DAISY, one field for plain audio).

**Import paths for DAISY (all three now covered):** an archive containing a
DAISY book (Add File) is handled in `ImportFile`; an already-extracted DAISY
**folder** (Add Folder) and a DAISY **nav file** (Add File on `ncc.html` / `.opf`
/ `.ncx`) both route through `LibraryForm.ImportDaisyFolder`, which copies the
whole tree into the library, `FlattenDaisyToRoot`s it, and builds chapters +
Author/Title from the navigation. Without this, Add Folder imported a DAISY book
as loose multi-file audio (ignoring the NCC) and opening ncc.html was mistaken
for a plain HTML text book. `ImportDaisyFolder` returns false when the folder
isn't DAISY so the caller falls back to the generic import.

**Text DAISY (no audio) — read by TTS (Session 16).** A DAISY book with no
audio is a *text* book: `DaisyTextExtractor` pulls the reading text from its
content — DAISY 3 → the DTBook XML (`<dtbook>…`), DAISY 2.02 → the content
XHTML — both through the shared `TextParsing` HTML pipeline (DAISY content uses
`<h1>`–`<h6>`/`<p>` like HTML; `<pagenum>` stripped for now). All three DAISY
import paths route a text DAISY (`DaisyTextExtractor.IsTextDaisy` = no audio) to
`SetupTextBook`: flatten → `content.txt` → `[TextNav]` headings + DAISY
title/author → Format `"DAISY <ver> — …"`; the audio timeline is skipped. On
load `BuildDaisyNav` no longer claims a no-audio DAISY as `IsDaisy` (and
short-circuits when `content.txt` exists), so `DetectTextBook` picks it up;
`DetectTextBook` prefers `content.txt` over a stray `.txt`. `EpubParser.WrapsEpub`
excludes DAISY (a DAISY 3 zip also has a `.opf`) via `LooksLikeDaisy`
(ncc.html / DTBook / Z39.86; **not** dtbncx — epub2 has an NCX too) so a DAISY
zip goes to the archive+DAISY path, not the epub path. Verified end-to-end on
real DAISY 3 + 2.02 text samples. **Still open:** page navigation for text DAISY
(`<pagenum>` markers), and **text+audio DAISY multi-modal** (follow/​highlight
text while the audio plays — currently a text+audio DAISY imports & plays as
plain audio, text unused; that's the Phase-3 on-screen-display work).

---

## 8d. Sound processing — Properties dialog (Session 12)

Per-book audio processing, opened from the library (Alt+Enter / right-click
Properties) or the player (Properties button / **Alt+Enter**, beep when nothing
is loaded). `PropertiesForm.cs` + `SoundSettings.cs` (settings model, persisted
in Book.ini `[Sound]`).

**Chain** (`SoundSettings.BuildAf` → mpv `af` as one `lavfi=[…]` graph, applied
by `Form1.ApplySoundProcessing`): highpass → afftdn (denoise) → deesser →
acompressor → EQ (bass/equalizer/treble) → speechnorm|dynaudnorm → alimiter.
Verified working against the vendored libmpv (statically-linked ffmpeg, Lavf62;
all needed filters confirmed present by grepping the DLL). All numbers formatted
`InvariantCulture` (ffmpeg needs `.`); friendly dB units convert to ffmpeg's
linear amplitudes (threshold/makeup/limit via `10^(dB/20)`).

**UX / accessibility:**
- Stages are **named presets** (a level), not raw DSP knobs — the preset tables
  (the real values) live in `SoundSettings` and feed both the dialog and the
  live technical read-out. Rumble/denoise/deesser/compressor/loudness are
  5-level (Minimal…Maximum); EQ is free-form (three dB bands, ±15); the safety
  limiter is fixed (−0.1 dB, always on, not shown).
- Boxy 3-column grid like the player: column A = full-height info + **live
  technical read-out**; B/C = the six stage cells (left→right, top→bottom);
  merged bottom cell = master switch, Reset all, Bypass, OK, Cancel.
- **Master switch gates everything** (off = cells dimmed and out of Tab order);
  each stage's own switch gates its parameters. NumericUpDown/ComboBox (not
  track bars) so the value is announced. NVDA doesn't auto-read a DropDownList
  on arrow the way JAWS does, so combo changes are spoken via `NvdaController`
  (no-op under JAWS → no double-speak).
- **Live preview**: opening Properties from the player passes a callback so
  every edit (and Bypass) is heard on the fly; on close the persisted state is
  re-applied (OK saved new, Cancel kept old). From the library there's no audio,
  so no preview.

**Open items** (Session 12, deferred until "critical" sample recordings exist):
tune the preset values by ear; finalize the normalization method (the
speechnorm/dynaudnorm chooser is temporary — likely lock to speechnorm and drop
it); English-name review for the stage titles (flagged in `en.lang`). Objective
analysis of user-supplied samples (LUFS/peak/noise-floor/spectral via a static
ffmpeg — "option A") will guide the tuning; **I measure, Gordan judges by ear.**

---

## 8e. Text books — TTS playback (Session 13, Phase 1)

A text book is a folder with a text document and no audio; the player reads it
aloud instead of driving mpv. Phase 1 handles **`.txt`**; richer formats
(epub/fb2/docx/… → clean text at import) are Phase 2, on-screen display Phase 3.

**Engine.** `TtsReader.cs` reads sentence-by-sentence through a pluggable
`ISpeechBackend`; only `Sapi5Backend` (System.Speech / SAPI5, in-process x64 —
the "SAPI 5 x64" equivalent) exists so far. OneCore natural voices (WinRT) and a
32-bit "SAPI 5" satellite for legacy voices (e.g. the user's 32-bit eSpeak) are
planned behind the same interface — mirroring how JAWS exposes several speech
backends. The sentence is the reading unit; position is a **character offset**
(so seeks by sentence/paragraph/standard page/time all snap to a sentence and
the resume point survives reloads). **Pause = cancel** (index stays), so **Play
resumes from the start of the current sentence**, not mid-utterance. SAPI applies
rate/volume/voice only to the *next* utterance, so a live change re-speaks the
current sentence. Pitch is via SSML prosody. `TtsReader.ReadFile` decodes
UTF-8/BOM with a Windows-1250 fallback.

**Text cleaning.** `TextCleaner.cs` tidies unstructured text before reading
(distilled from a Word "cleanup" macro, adapted): collapse runs of blank lines
to **one** (preserving paragraph boundaries — the key fix for long TTS pauses),
de-hyphenate line-broken words, tabs→space, spaced dashes→comma, strip a
conservative set of noise symbols. Deterministic, so saved offsets stay valid.
This becomes the core of Phase 2's cleaning.

**Player integration** (branches on `BookData.IsTextBook`, like DAISY):
- **Detection**: a folder with a `.txt` and no audio (`BookData.DetectTextBook`);
  `TextPosition` (char offset), `TextWpm` (per-book speed override, -1 = global),
  `TextChars` (cached for the estimate) persist in Book.ini.
- **Transport**: `LoadTextBookPlayback` loads the text into `tts` instead of an
  mpv playlist; Space/Back/Forward/position/save all branch to the reader.
  **Crucially, mpv events are skipped for text books** (`EventTimer_Tick`) — an
  IDLE event would otherwise flip `isPlaying` off (killing autoplay) or wrongly
  "finish" the book. The first autoplay `Play()` is also deferred one tick.
- **Seek steps** (per book, `RebuildSeekSteps`): 15 s / 30 s / 60 s / Sentence /
  Paragraph / **Standard page** (1800 chars, the translation/journalism unit) —
  no bookmarks yet.
- **Speed** is **words-per-minute** (nominal; real rate is voice-dependent),
  reusing the player's speed control (`ChangeSpeed` branch): 80–400 WPM, ±10 per
  step, a double-beep when crossing the Settings default; maps to SAPI rate via
  `TtsReader.WpmToRate` (175 WPM → 0). Reading-time estimates use CPM = WPM×6.
- **Global TTS defaults** (voice/WPM/pitch/volume) live in **Settings → Text
  Books** (`AppSettings` `[TextToSpeech]`), with a "Test voice" button.
- **Display**: title bar + info box show **percentage** (one decimal — the
  integer sits at 0 for a long book), estimated Elapsed/Remaining/Time, and the
  voice + WPM ("Voice: RHVoice Karmela, 250 WPM"). A started book is forced to
  ≥1 % so it lands in "Reading", not "Unread". Library shows "Plain text" and an
  estimated reading time. Single-file audio books now use the same plain
  Elapsed/Remaining/Time labels (no part/total split).

**Phase 2 — import parsers (Session 13).** At import (Library "Add File") a
document is extracted to `content.txt` in the book folder (the reader then treats
it as a plain text book). Structured formats also store a heading list in
Book.ini `[TextNav]` (`BookData.TextHeadings`, char offsets), driving DAISY-style
navigation for text (Heading seek-step levels + Go To — `TextHeadingSeek` /
`TextGoTo`, char-based via `tts.SeekToChar`); no headings → flat.

**Parser subsystem (Session 14 refactor).** One self-contained parser per format
behind `ITextFormatParser`, dispatched by `TextExtractor`; shared primitives
(HTML→blocks, block assembly with heading/id offsets, zip/XML) in `TextParsing`.
Parsers: `PlainTextParser` (txt), `RtfParser`, `WordParser` (docx/odt),
`HtmlParser`, `Fb2Parser`, `EpubParser`. Adding a format = one class + a line in
the dispatch list.
- **Editable → flat** (`TextCleaner`, no reliable structure): txt, rtf, docx, odt.
- **`EpubParser`** (validated against ~30 real books; **but the whole epub path
  still needs deeper analysis/testing — expect changes, see memory**): unwraps a
  .zip/double-zip down to the inner epub (most libraries package that way; a
  wrapping .zip is routed to text, not the archive path); OPF via
  `container.xml`; **structure from the TOC (NCX preferred, then EPUB3 nav), not
  raw `<hN>`**, each target resolved to a char offset (spine-file start + `#id`
  position, fragment-aware); `<hN>` then flat as fallback. **DRM only if content
  is encrypted** (font obfuscation via encryption.xml is ignored); real DRM →
  skip + message, never stripped.

**Open items:** per-book Properties for text (TTS override UI); OneCore (WinRT)
+ 32-bit satellite backends; text bookmarks; Layer-2/3 parsers (pdf/mobi/…); a
promised personal `.lit` converter (see memory). Test feedback still to apply is
in memory. eSpeak: the user's is 32-bit-only (invisible to x64 System.Speech) —
install eSpeak NG (64-bit) or add the satellite backend.

---

## 8f. M4B chapters (Apple audiobooks)

`M4bParser.cs`. An M4B is a single MP4 audio file with embedded chapter marks;
it overlays on the single-file virtual timeline like DAISY does on multi-file
(chapters are time positions within the one file). No dependency — TagLib#
doesn't expose MP4 chapters reliably, so the parser walks the box (atom) tree
itself: `ReadMoov` stream-scans the top level for `moov` **without loading the
huge `mdat`**, then `Walk` builds a flat box list. Two chapter sources, in order:
1. **Nero `chpl`** (moov/udta/chpl) — titles + 100 ns start times, all inline;
   record base offset found by trying candidates (9 in every real book).
2. **QuickTime text chapter track** — the audio track's `tref/chap` → chapter
   track **id** (followed by id, *not* handler type — a dangling `chap`→id 0 is
   why one sample book has no chapters), whose `stts` gives start times and
   whose samples (`stsc`/`stsz`/`stco`, read from the file) give titles.
Metadata: `©nam`/`©ART`, falling back to `aART` (album artist) for the author.
Verified against 13 real books (see memory [[project-m4b-analysis]]): chapters
in 12/13 via chpl (identical counts to the QT track).

Wired in: import parses chapters (`M4bParser.TryParse` in `ImportFile`) into
`BookData.IsM4b` + `M4bChapters` (persisted in Book.ini `[M4bNav]` as
`C<i>=<seconds>|<title>`). `GetPlayerType` returns `M4b` when chapters exist
(else single-file audio). Player: title bar `Title — Chapter`; info box adds a
Chapter line + "Apple Book M4B" format; seek step **Chapter** (via
`StructForward`/`StructBack` over `M4bChapterPositions`); Go To lists chapters.
**mpv is set `vid=no` at init** (plus the existing `audio-display=no`) so an
M4B's cover art — a real MP4 video track — never pops a video window.

---

## 8g. TTS backends — 32-bit satellite (Phase 1)

Text-book speech is behind `ISpeechBackend` (sentence chunks; `TtsReader` owns
position). Now multi-backend, presented as one via `CompositeSpeechBackend`:
- `Sapi5Backend` — in-process x64 SAPI5 (only 64-bit voices).
- `Sapi5SatelliteBackend` — launches a **32-bit** host `TtsHost32.exe` so the
  x64 app can use 32-bit-only voices (eSpeak, RHVoice). The host plays audio
  itself and speaks a stdio line protocol (see `TtsHost32.cs`); the backend
  caches its voice list, forwards commands, raises `Completed` on `DONE`.
- `CompositeSpeechBackend` merges voices (64-bit wins duplicate names), routes
  at the selected voice's owning backend, carries rate/volume/pitch across a
  backend switch. `TtsReader()` uses it; SettingsForm's Voice combo + Test do too.

**Packaging:** `TtsHost32.cs` is NOT in the main x64 Compile set; a post-build
MSBuild `Exec` target compiles it x86 with `$(MSBuildToolsPath)\Roslyn\csc.exe`
(note: `$(CscToolPath)` was empty here) into the output dir.

**eSpeak gotcha:** eSpeak's SAPI driver sets `SpeakCompleted.Cancelled = true`
even on a natural end → the reader stopped after each sentence. The host ignores
`e.Cancelled`; an utterance is cancelled only if we sent CANCEL or a newer Speak
superseded it (`e.Prompt != currentPrompt`).

**Phase 2 (done):** Settings → Text Books is a two-combo picker — "Speech
Engine" (vendor + architecture, e.g. "eSpeak (32-bit)", "Microsoft (64-bit)",
"SAPI 5 (32-bit)") filters the "Voice" combo to that group. Backends now report a
per-voice **vendor**; `CompositeSpeechBackend.GetVoiceCatalog()` derives the
engine label (`EngineLabel`: eSpeak from its URL vendor, Microsoft, else "SAPI 5").
Only the voice is persisted (`TtsVoice`); the engine is derived from it on open.
Fine-tuning left: RHVoice voices (Karmela/Marija) expose no vendor/metadata so
they land under "SAPI 5 (32-bit)" rather than "RHVoice".

**Temporary / still to do:** `BtnSettings_Click` pushes a changed Settings voice
onto the live book (no restart) — interim; final design is Settings voice = the
*default* only, real per-book voice in a (not-yet-built) per-book text Properties.
OneCore/WinRT backend (e.g. "Microsoft Matej") is a separate later backend. See
memory `project-tts-backends`.

---

## 8h. Supported formats + official names

`BookData.FriendlyFormatName` is the **single source of truth** for the format
shown in the player and library info boxes. It returns **"TAG — Official Name"**
(e.g. `MP3 — MPEG-1 Audio Layer III`, `EPUB — Electronic Publication`): the short
tag first so it's recognised/spoken immediately, the official name after. For
audio, `DetectAudioFormatString` appends the technical details after a comma
(`…, 44.1 kHz, 128 kbps, stereo`) — the player info box shows only the part
before the comma, the library shows the whole string. DAISY has no extension, so
`Form1.PlayerFormatLabel` shapes it the same way from the parsed version:
`DAISY 2.02 — Digital Accessible Information System` (works for 3.0 too).

Audio extensions (`LibraryScanner.AudioExtensions`, 24): mp3, ogg, flac, m4a,
m4b, wav, opus, aac, wma, ape, mka, spx, oga, dsf, dff, caf, aiff, aif, ac3,
amr, weba, webm, au, voc. Keep this array, the two file-open filters (Form1 and
libraryform) and `FriendlyFormatName` in sync.

**Two independent layers — verified separately (2026-07-20, samples in
`D:\Test Naslovi\misc audio`):** mpv decodes/plays, TagLib# reads duration+tags.
They fail independently: `.caf`/`.oga` (and `.ac3`/`.amr`/`.weba`/`.au`/`.voc`)
play fine but TagLib has no reader → **`MpvDuration.TryGet` is the fallback**
(its own silent `ao=null` libmpv context, lazily created, released via
`MpvDuration.Shutdown()` on exit); without it such books import as 0:00 with a
broken timeline. Conversely `.ape` reads in TagLib but one sample wouldn't play —
that file had a **non-standard ID3v2 tag prepended** (APE uses APEv2 tags at the
END); a clean APE plays fine, so the format stays supported. Lesson learned
twice (APE, and a hand-made VOC with a bad checksum): **a single failing sample
usually means a malformed file, not a missing codec** — verify before dropping a
format.

---

## 8i. Electronic braille (.brf) — liblouis back-translation

A `.brf`/`.brl`/`.bra` is a stream of braille **cells**, not text. `BrfParser`
maps each byte to a cell (standard **Braille ASCII**), converts to Unicode
braille (U+2800…), and **back-translates to text via liblouis** (`LibLouis.cs`,
P/Invoke). Output then flows into the normal text pipeline (TTS, pages, nav).
Form feed = braille page; ornamental rules/boxes are dropped.

- **ABI gotcha:** this Windows liblouis build is `__stdcall` with a **32-bit
  widechar** (UCS-4) → buffers marshal as `uint[]`, not `ushort[]`. Tables are
  passed **by absolute path** so their `include` chains resolve without
  `LOUIS_TABLEPATH`. Vendored: `liblouis.dll` + `louis\tables\` (copied to output).
- **Croatian needed custom tables.** Shipped `hr-g1.ctb` mis-reads literary
  braille: it includes `text_nabcc.dis` (8-dot computer display); `hr-chardefs`
  defines German ä/ö/ü/**ß on ž's cell**; and `hr-digits.uti` puts digits on the
  **č/ć/š/đ cells**, shadowing those letters. Built from the official standard
  (*Standard hrvatske brajice*, Funtek / HSS 2020): **`hr-old.ctb`** (pre-2020,
  single-cell dž/lj/nj) and **`hr-2020.ctb`** (two-cell digraphs; freed cells =
  round brackets). Croatian has **no standardised contracted grade** → grade 1
  only. Digits use the standard's lowered forms, marked `noback` so they don't
  hijack back-translation of punctuation.
- **The unavoidable ambiguity:** the same cell is `lj` (old) or `(` (2020), and a
  .brf declares **neither language nor grade nor standard revision**. So the
  table is **per book**: auto-detected at import, persisted in `Book.ini`
  `[Braille] Table`, and the original .brf is kept beside `content.txt` so the
  reading can be redone with another table. **Detection is a heuristic** (letter/
  junk ratio, mid-word capitals, accent rate, and decisively the share of the
  language's own everyday words) — **the user is the authority**.
- Verified on 19 real books (HR grade 1, FR, EN UEB contracted): 18 detect
  correctly; one English TOC-heavy file misdetects — which is what the override
  is for. **Still open:** the per-book override UI (needs the text Properties
  dialog), `.pef` support, and more languages/samples.

---

## 9. Library window

`LibraryForm.cs`. Book shelf is a single-column **ListView (Details view), one
flat sorted list — no group headers** (the earlier Now Reading / Reading /
Unread / Read native groups were removed by request). Each row instead carries
its status two ways: a **spoken text flag** appended to the item name (", Now
reading" / ", Reading" / ", Read" / ", Unread", then ", Favorite") so screen
readers announce it, and a **colored badge icon** (`SmallImageList` dots —
red = unread, yellow = reading, green = read, blue = now reading; drawn at
runtime by `MakeStatusDot`). The **Now-reading** book (last-opened while still
in progress) is **bold** and pinned to the top; otherwise order follows the
sort menu. The status/**Favorites** filter combo (All / Reading / Unread /
Read / Favorites) replaces the old group navigation — "sections on demand".
`BuildShelfItem` builds each row; `GetShelfStatus`/`IsNowReading` classify it.
Author-merge: DAISY/text carry a separate Author (shown "Author — Title");
plain audio shows a single Title from the folder name. Detailed audio format shown in book details via
TagLib# (e.g. "MP3 Audio, 44.1 kHz, 128 kbps, stereo"), lazy-loaded per book.
Search + filter row with **diacritic-insensitive** matching (č↔c↔ć via Unicode
decomposition, đ special-cased), Ctrl+F. Sort options carry a checkmark plus
an "(active)" text suffix (WinForms MenuStrip check states aren't reliably
announced, so the text is the screen-reader fallback). Context shortcuts on
the shelf: **F2** rename, **Del** delete, **Alt+Enter** properties. Deleting
the currently-active book is blocked with a clear dialog rather than crashing.

**Finish flow**: completing a book saves 100% and resets position (moving it
to the Read group), unloads it from the player (so it's no longer "active" and
can be deleted), and opens the Library.

**Startup flow** (`DecideStartupView`): resume the last book in the player
normally; open the Library instead when the library is empty, the last book's
folder is gone, or the last book was already finished.

---

## 10. Roadmap / suggested order

### Editions: Lite vs Pro (Gordan, Session 15)

NBR ships in two editions. **The player binary is the same** — Pro is simply
Lite plus a set of add-on features that depend on **external resources and
open decisions about which of them to use**. The distinction is about what a
feature *requires*, not about a different app.

- **Lite** — everything self-contained: the whole player as built so far
  (audio + text + DAISY + M4B playback, all the file-format parsers, sound
  processing, TTS text reading, Settings, Bookmarks, Sleep Timer) **plus the
  remaining core items** below (Properties, finishing Settings, Help, and any
  small polish). No cloud, no heavyweight external engines.
- **Pro** — the add-ons that pull in external engines / models / services and
  need a "which one, and do we even use it" decision: **STT / ASR**
  (audiobook → synced on-screen/braille text), **OCR** (scanned image-only
  PDFs/DjVu → text), and **translation**. These are parked until Lite is done.

**Workflow rule:** until Lite is finished, when reporting "where we stopped"
or "what's left", list **Lite items only**. Treat STT/OCR/translate as a
separate Pro backlog — mention them only when explicitly asked about Pro.
See memory `project-lite-pro-editions`.

**Intended sequence going forward (Gordan, Session 10):**

1. **Support for all planned file types** — the remaining audio formats and
   the text-book formats (`.epub`, `.txt`, `.pdf`, `.fb2`, `.mobi`, …) plus
   DAISY. This is the next major thrust and a prerequisite for Properties.
   **DAISY audio is done:** parser (Phase 1) + full integration (Phase 2) —
   import, playback in reading order, Author/Title metadata, Go To, and the
   per-depth Heading / Page seek steps. See sections 8c and 6.
2. **Properties dialogs** (player + library) — deliberately **on hold until
   file-type support lands**, because what a book's properties show depends
   heavily on its type.
3. **Finish the Settings window** — wire up the still-placeholder tabs
   (Text Books / Device / media-keys), now that more subsystems exist.
4. **Help + user documentation** — write the in-app Help and related docs.
5. Ongoing: ad-hoc tweaks and additions as they come up.

The numbered list below is the older feature backlog (kept for reference; the
sequence above supersedes its ordering).

1. **Sleep Timer** — done (Session 8), pending final edge-case test.
2. **Bookmarks** — done (Session 9): Set Bookmark, Manage Bookmarks dialog,
   Bookmark seek step.
3. **Archive import (.zip/.rar/.7z)** — done (Session 9): see section 8a.
4. **Settings window** — shell + first wiring done (Sessions 9–10, section
   8b): Library location and a (single-option) Language combo are in the
   General tab. Text Books/Device tabs and the media-keys checkboxes still
   need wiring to AppSettings and real subsystems. Will use a "hint system":
   a read-only textbox beside most controls with a short explanation, plus a
   global "Show help hints" toggle that flips hint `Visible`/`TabStop` live
   without closing the window (the pattern already lives in the Go To dialog's
   hint box).
5. **Screen-reader announcements** — done (Session 10): UIA notification for
   JAWS + NVDA Controller Client for NVDA, both without moving focus; see
   section 2.
6. **Properties dialogs** (player + library) and library tooltips.
7. **Audio filters** (not yet scheduled): dynaudnorm/speechnorm
   (normalization), scaletempo2 (already active, pitch-preserved speed),
   acompressor (dynamic range), highpass+EQ (voice clarity), afftdn/arnndn
   (noise reduction). Actual availability depends on the specific
   `libmpv-2.dll` build.
8. **DAISY / text-book structure** — a large separate subsystem; plugs into
   the Go To level and the seek dropdown's structural levels.

---

## 11. TODO (open items)

- **Verify Sleep Timer expiry (close/shutdown) in the modal-Library edge
  case** described in section 7: a book finishing while the Library window is
  manually open with background playback, i.e. `Close()` beneath a modal
  dialog.
- **Properties live-preview: finish testing (Session 17).** Volume/speed and a
  text book's voice/speed/volume/pitch now preview live, but the volume path
  was still misbehaving when the session ended: a TEXT book's "Volume" field is
  the TTS volume (`TextVolume`), not playback volume, and it was seeded with a
  flat 100 instead of the live value — shown as 100 while actually 50, jumping
  to the real value on first edit, and "falling" to 50 later when the arrows
  were used. Seeding from the live value is committed but **untested**. Speed
  looked unaffected; retest both, plus Cancel/OK, on a text book AND an audio
  book, and check the Library entry point too (it opens Properties without the
  player's live values, so its fields fall back to what is stored).
- **Combo boxes: NVDA does not announce the selection when it changes with
  Up/Down — app-wide** (confirmed by Gordan, Session 17). JAWS is correct
  everywhere: it reads the name on focus, announces each arrow change, and
  handles Alt+Down + arrows. NVDA reads the value on Tab-focus and handles
  Alt+Down fine, but **arrowing through a closed combo changes the selection
  silently**. This is NVDA's MSAA handling, not our code — the established
  remedy is already in `PropertiesForm`: speak the new value through
  `NvdaController` on `SelectedIndexChanged` (a no-op under JAWS, so no
  double-speak). **Fix globally, not per dialog:** put it in the shared combo
  factory (`SettingsForm.MakeCombo` and the equivalent used by the other
  dialogs) so every combo inherits it. Deferred to the single accessibility
  pass at the end, once the UI has stopped moving — the NVDA controller itself
  is only a speech channel and cannot change how a control reports itself.
- A cosmetic JAWS note on the info box: it announces "i edit read only" rather
  than "read only edit" order — this is JAWS's internal handling of
  multiline vs singleline EDIT controls, not our code. Deferred to final
  polish (options: shortcut in AccessibleDescription, or a naming tweak).
- Archive import is runtime-verified across all three formats (.zip/.rar/.7z),
  single- and multi-volume, with and without a password (Session 11). Only the
  default volume naming was exercised (7z/zip `.001/.002`, RAR `.partN.rar`);
  old-style RAR `.rNN` and spanned ZIP `.zNN` are handled in code but weren't
  sampled.

---

## 12. How to start a session on this project

1. Read this file and skim the actual source of the files you'll touch (the
   disk is the source of truth; this brief can lag).
2. Confirm understanding back to Gordan in Croatian — briefly, in your own
   words — before changing anything, so he can catch a stale brief.
3. Make sure the working tree is committed (a safety point) before edits.
4. Do the work as surgical edits to the real files; keep `en.lang` in sync for
   any user-visible string; respect the accessibility rules in section 2.
5. Gordan tests with JAWS and reports exact spoken strings; iterate from those.
6. When behavior or architecture changes, update this file so it stays true.
