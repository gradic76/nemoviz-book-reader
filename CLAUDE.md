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

- **Every sound NBR makes comes out of the sound card the book is playing on**
  (`SignalTones.cs`). The beeps — "nothing loaded", the volume floor/ceiling, the
  speed-default double beep, the bookmark confirmation, the sleep timer's
  five-minute warning — are generated as small WAVs and played on **their own
  libmpv context**, pointed at the same `audio-device` as everything else.
  **Not through SAPI, and this is a hard-won rule:** playing a tone through a
  second `SpVoice` on the same output token **kills the 32-bit speech host's
  playback**. Measured — with eSpeak reading, every sentence after a beep was
  reported finished in ~420 ms instead of being spoken (silence); the same test
  with the tone on the *default* device, or through `Console.Beep`, read normally.
  SAPI output is not shareable across processes; mpv opens WASAPI in shared mode,
  so its tones simply mix with the book, the speech host and everything else.
  `Console.Beep` cannot do the job either: it goes
  wherever Windows sends system sounds, so for a listener on headphones or a
  second card the feedback landed in a different room from the audio it belongs
  to. It also blocked the UI thread for the length of the tone (the five-beep
  bookmark series froze the player for a second); a series is now one buffer
  played in the background, which keeps its timing exact too. Tones fade in and
  out over 5 ms, because a sine that starts at full amplitude is heard as a click.

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

**One rule for every seek, in every kind of book: a step that does NOT get you
the whole way plays the "no go" beep; a full step is silent.** For the steps that
jump from mark to mark (Part, Heading, Page, Chapter, Bookmark, Sentence,
Paragraph) that means "there was no next mark". For the **continuous** steps
(the time rows, Standard page) it means the jump ran into the beginning or the
end of the book: it still lands on the edge, and the beep says "that is as far as
it goes this way". Testing the first version showed why the distinction matters —
near the end a time step moved two seconds and said nothing, which reads as "the
time steps don't work".

**The plain Left/Right arrows are exempt and stay silent** (5 s of audio, one
sentence of text): that is the small constant nudge you use while listening, and
a beep on every nudge past the end would be noise. The audible edge belongs to
the deliberate jump — the seek step. Each seek helper
(`PartForward/Back`, `StructForward/Back`, `BookmarkForward/Back`,
`SeekRelative`, `TextSeek`) *returns whether it went anywhere* and makes no sound
itself; the two dispatchers own the beep. So "there is nothing further that way"
feels identical whether the step is Heading 1 or 15 seconds, and whether the book
is audio, text or hybrid — including the plain Left/Right arrows. Standing on the
first mark of anything, Back beeps rather than silently re-seeking to where you
already are. Verified across every text step at both ends of a real book.

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
  gives a short low beep (same as Ctrl+G with no book). The test is the **book**,
  not a current file — a text book is read by the speech engine and has no
  current file, which used to lock it out of the timer altogether.
- **It works the same on a text book.** Every playback move the timer makes
  (the pause when its dialog opens, the resume on Cancel, the start, the
  one-press cancel, the pause at expiry) goes through
  `PausePlaybackQuietly`/`ResumePlaybackQuietly`, which drive mpv or the reader
  depending on the book — still *programmatic* pauses, so they don't cancel the
  timer the way a user pause does. The fadeout fades speech volume via
  `TtsReader.SetVolumeQuiet` (no re-speak), so there it steps down sentence by
  sentence rather than second by second.
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

**The modal-Library edge case — VERIFIED (Gordan, Session 18), no fix needed.**
A timer expiring while the Library window is *manually* open, with playback
running in the background, fires the action with `isLibraryOpen == true`, i.e.
`Close()` under a modal dialog. Both **Stop** and **Stop + close** were tested in
exactly that state and did the right thing, so the long-standing worry about
closing beneath a modal dialog is settled.

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
- **Multimedia keys (done).** The first checkbox turns them on or off (off = the
  message goes back to the system, so another player gets it); the second claims
  them **system-wide** via `RegisterHotKey` → `WM_HOTKEY`, so they work while NBR
  is in the background. Global is off by default (claiming them takes them from
  every other player) and is disabled while the keys are off. NBR registers on
  handle creation and whenever Settings closes, and releases them on exit.
  `WM_APPCOMMAND` gates on the **book**, not a current file, so the keys work on
  text books too.
- **Hint system (done).** `SettingsForm.MakeHint` puts an explanatory hint under a
  control and the "Show help hints" switch at the top shows or hides all of them
  **live**. Persisted (`[App] ShowHints`) so other dialogs can honour it as they
  grow hints of their own. Written for General, Text Books (speech / braille /
  visual), Device and Audio Books.
  **A hint is a read-only TABBABLE TextBox, never a Label** (the shape the Go To
  dialog already used). The first version used labels and Gordan reported the
  hints were simply not there: a screen reader driven by Tab — which is how this
  app is used — never visits a label. Each hint carries the TabIndex right after
  the control it explains, and the switch takes `TabStop` away with `Visible`, so
  turning hints off also takes them out of the tab order. Verified by dumping the
  real tab order of every tab, switch on and off.

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
real DAISY 3 + 2.02 text samples. **Print pages are kept** (both spellings: a
DAISY 3 `<pagenum>` element and a DAISY 2.02 `<span class="page-normal|front|
special">`): the number is taken out of the reading flow — nobody wants "247"
read out mid-sentence — but its position is recorded, so the book navigates by
its real printed pages (Page seek step). Measured on the samples: 67–712 pages in
the DAISY 3 books; the three 2.02 samples genuinely carry no page markers at all,
which is why they show none. **Still open:** **text+audio DAISY multi-modal** (follow/​highlight
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
conservative set of noise symbols, and blank out **Private Use Area** characters
(U+E000–U+F8FF — a Word/Wingdings list bullet, U+F0B7, is the usual one: not text
at all, but a glyph from a symbol font that survived conversion, which a speech
engine either stumbles on or invents a name for) along with zero-width marks and
a stray mid-file BOM. Deterministic, so saved offsets stay valid.
This becomes the core of Phase 2's cleaning.

**Cleaning happens ONCE, at import (fixed Session 18).** It used to run again on
every load, which meant the heading and page offsets — taken at import, on the
uncleaned text — pointed further and further past their targets as the reader's
copy lost characters. Measured before the fix: 0 of 20 headings in one epub
landed on their own title, and a braille book's marks were 2071 characters out by
the end.
- `TextCleaner.CleanWithOffsets(text, offsets)` cleans **and moves a set of
  character offsets with it**: the offsets are the cut points, each piece is
  cleaned on its own, and each offset's new value is the length of everything
  cleaned before it. No marker characters are smuggled into the text, where they
  would change what the rules see.
- Two rules span a line break (de-hyphenation, unwrapping a continued line) and
  one spans a space (a dash used as punctuation), so a cut landing exactly there —
  a braille page mark sits at a line break — is handled at the seam by hand.
  Without that the assembled text differed from a plain clean and stopped being
  idempotent.
- `CleanDoc(TextDoc)` does it for an extracted document at import;
  `BookData.CleanTextFileOnce()` does it for a book imported earlier, once, and
  `[Book] TextCleaned` records that it happened. `TtsReader.LoadText(text,
  alreadyClean: true)` then leaves the file exactly as written.
- **The two coordinate systems.** Heading and page offsets come from the PARSER
  and are in raw-text coordinates → they get moved. The reading position and
  bookmarks came from the READER, which had already cleaned the text → they are
  already in cleaned coordinates and must be left alone; moving them would apply
  the drift twice. (They can be a character or two out where a cut fell inside a
  rewritten pattern; the reader snaps to the nearest sentence, so it never shows.)
- Verified: headings land on their own titles (20/20 where they had been 0/20),
  the assembled text is identical to a whole-text clean and idempotent, and the
  one-time migration of a real book leaves its position where it was.

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
  Paragraph / **Standard page** (1800 chars, the translation/journalism unit),
  and **Bookmark** once the book has one.
- **Bookmarks** work here too. A mark is stored in the book's own unit — the
  character offset for text, seconds for audio — and `BookPosition` /
  `SeekToBookPosition` / `BookBackGrace` keep one set of bookmark code serving
  both (the three-second "just passed it" window becomes characters at the
  book's reading speed). **A text book seeks through its own dispatch**
  (`TextSeek`), so every step needs a case there — Bookmark was missing at first
  and fell through to the time seek, wandering 15 seconds instead of jumping to
  the mark, while audio behaved perfectly. Manage Bookmarks shows a text mark as **how far into the
  book it is plus the words it sits on** ("41,7 %, Tada je Perica shvatio da…" —
  `TtsReader.SnippetAt`, six words); a character offset tells the reader nothing,
  the words tell them exactly where they were. A fragment that is only
  punctuation (a stray full stop after a page number) is skipped for the next
  sentence with actual words in it.
- **Speed** is **words-per-minute** (nominal; real rate is voice-dependent),
  reusing the player's speed control (`ChangeSpeed` branch): 80–400 WPM, **±5 per
  step**, a double-beep when crossing the Settings default; maps to SAPI rate via
  `TtsReader.WpmToRate` (175 WPM → 0). Reading-time estimates use CPM = WPM×6.
  **The spin boxes in Settings and Properties step by the same amount as the
  player** (`MakeNumeric`'s `increment`): 5 WPM, 5 % volume, 10 % playback speed —
  stepping by 1 is far too slow when every step is spoken.
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

**Language detection (`LanguageDetector.cs`).** An imported text book works out
what language it is in, so it is read by a voice that speaks it instead of
whatever Settings happens to name. Three layers, cheapest first: **script**
(Greek/Cyrillic/Arabic/Hebrew/Hangul/kana/Han from Unicode ranges), **stopword
share** (~20 languages, ~50 commonest words each — the winner takes 27–58 % of
all tokens where the runner-up takes 6–27 %), and **neighbour markers** for the
one pair stopwords cannot split, **hr vs sr**: the ijekavian/ekavian axis
(vrijeme/vreme, dijete/dete, prije/pre) plus lexical pairs (tisuća/hiljada,
kruh/hleb). Nothing in the two lexical lists may appear in both, or a clear book
scores a tie. Two thresholds keep it honest — below 0.10 of tokens, or a margin
under 0.05, it says **nothing** rather than guessing.

**The file's own `dc:language` does NOT win.** Parsers now carry it
(`TextDoc.Language`: EPUB OPF, FB2 `<lang>`, MOBI EXTH 524, DAISY), but of 24
declaring samples **4 (17 %) declared it wrongly** — three Vietnamese DAISY books
and a Greek one all said "en". So `LanguageDetector.Resolve` lets a confident
reading of the actual words overrule the declaration, and falls back to the
declaration only when the text can't tell (unknown script, degraded braille).
Stored per book in `Book.ini` `[Book] Language`; a book imported before this
existed gets it on first load. **Measured over ~85 real books** (txt, docx/odt/
rtf, epub, brf, Kindle, DAISY, in en/hr/sr/fr/es/pt/el/ar/vi): every book with
extractable text landed on the right voice language.

**Open items:** text bookmarks; Layer-3 parsers; a promised personal `.lit`
converter (see memory). Test feedback still to apply is in memory.

---

## 8f. Chapters inside one audio file — M4B, and CUE sheets

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

**CUE sheets (`CueParser.cs`) are the same thing written outside the file.** A
`.cue` beside one long audio file marks where each track begins, so it becomes
the same chapter list (`SetM4bChapters` — the storage is shared; the name is
historical). `BuildChaptersFromFolder` reads it, which covers every route into
the library (file import, folder import, background scan), and a single-file
import copies the sheet in beside its audio so the book keeps it. Rules:
**only** with exactly one audio file, and a **multi-FILE sheet is ignored** —
that describes a folder of tracks, which NBR already navigates by Part.
**The name in the sheet is matched WITHOUT its extension**: a ripper writes the
CUE against its WAV and the WAV is encoded to FLAC afterwards, so
`FILE "… .wav"` beside a real `… .flac` is the normal case, not a mismatch —
that was exactly why the first version found nothing in Gordan's sample. What
actually guards against a sheet copied in from another rip is the **duration
check**: a sheet whose last mark lies beyond the end of the audio is refused. `INDEX 01` is the track start (`INDEX 00` is the
pre-gap); times are `MM:SS:FF` with **75 frames to the second**; an untitled
track falls back to "Track N". The sheet's header TITLE/PERFORMER are parsed and
available but deliberately **not** applied — the audio tags already fill those in.

---

## 8g. TTS backends — three engines, one voice list

Text-book speech is behind `ISpeechBackend` (sentence chunks; `TtsReader` owns
position). Three backends, presented as one via `CompositeSpeechBackend`:
- `Sapi5Backend` — in-process x64, SAPI COM `SpVoice`. Every 64-bit SAPI 5 voice
  (Zira, RHVoice Karmela/Marija) plus output-device selection via `AudioOutput`.
- **`OneCoreBackend` — the OneCore/WinRT voices** (`Speech_OneCore` hive), which
  SAPI cannot see at all: on a Croatian machine this is the only way to
  **Microsoft Matej (hr-HR)**. **No Windows SDK and no vendored winmd** — the
  type comes from `Type.GetType("…, Windows, ContentType=WindowsRuntime")`, and
  the async result (a bare `__ComObject` reflection can't inspect) is unwrapped
  with `AsTask` from `System.Runtime.WindowsRuntime` (in the GAC, part of the
  framework). Synthesis hands back a finished `audio/wav` in memory. Rate/volume/
  pitch map onto WinRT's multipliers (`SpeakingRate`/`AudioVolume`/`AudioPitch`).
  Synthesis runs on a worker thread; a UI-thread timer starts playback and
  watches for the end, so every COM call stays on one thread.
- `Sapi5SatelliteBackend` — launches the **32-bit** host `TtsHost32.exe` for
  32-bit-only voices (eSpeak); stdio line protocol (see `TtsHost32.cs`), the
  backend caches its voice list, forwards commands, raises `Completed` on `DONE`.
- `CompositeSpeechBackend` merges voices (64-bit wins duplicates, compared on the
  bare name), routes at the selected voice's owning backend, carries
  rate/volume/pitch across a switch, cancels the backend it switches away from,
  and `Cancel()` reaches **all** backends. `TtsReader()` uses it; SettingsForm's
  Voice combo + Test button do too.

**`SapiWavPlayer` — one playback path for rendered speech (shared by the OneCore
backend and the 32-bit host, compiled into both).** Anything that produces a WAV
plays it through SAPI's `SpVoice.SpeakStream` with `AudioOutput` set from the mpv
device id, because that gives the two things `SoundPlayer` cannot: **the sound
card chosen in Settings → Device**, and a **purge that stops playback instantly**.
It also owns `TrimTrailingSilence` (engines pad the end — Zira by ~¾ s — which
would otherwise be heard as a gap between sentences).

**The 32-bit host runs on SAPI COM, not System.Speech** (rewritten once
`SpVoice` was proven able to render every voice — eSpeak included — to a wave
file, which System.Speech could not). That single change fixed three things:
32-bit voices now follow the chosen sound card (`DEVICE` command → the player's
`AudioOutput`); eSpeak no longer needs the crackly real-time path, so **every**
voice gets the buffered, gapless one; and the host names a voice by its token
`Name`, the same name the in-process backend reports, so the two lists merge
instead of duplicating. The old System.Speech gotcha (eSpeak's driver calling a
natural end "cancelled") is gone with it — completion is now decided by the host
from playback, via a generation counter.

**Packaging:** `TtsHost32.cs` is NOT in the main x64 Compile set; a post-build
MSBuild `Exec` target compiles it x86 with `$(MSBuildToolsPath)\Roslyn\csc.exe`
(note: `$(CscToolPath)` was empty here) into the output dir, together with the
shared `SapiWavPlayer.cs` and a `Microsoft.CSharp` reference (both drive SAPI
through late-bound `dynamic`).

**Phase 2 (done):** Settings → Text Books is a two-combo picker — "Speech
Engine" (vendor + architecture, e.g. "eSpeak (32-bit)", "Microsoft (64-bit)",
"SAPI 5 (32-bit)") filters the "Voice" combo to that group. Backends now report a
per-voice **vendor**; `CompositeSpeechBackend.GetVoiceCatalog()` derives the
engine label (`EngineLabel`: eSpeak from its URL vendor, Microsoft, else "SAPI 5").
Only the voice is persisted (`TtsVoice`); the engine is derived from it on open.
**A voice is named by its plain name in every backend** — `Sapi5Backend` and the
host both report the SAPI token's `Name` attribute, not `GetDescription()` (which
appends the language) — so the same voice seen by two backends is recognised as
one and the 64-bit copy wins. A name saved in the old (description) form still
resolves: lookup falls back to comparing the bare name. The catalog on Gordan's
machine (verified end to end): "Microsoft (64-bit)" = Zira, "Olga Yakovleva
(64-bit)" = Karmela/Marija (RHVoice, via the SpVoice vendor attribute),
**"Microsoft OneCore (64-bit)" = Matej**, "espeak.sf.net (32-bit)" = eSpeak-hr
and eSpeak-hr+michael. All four speak, and a cancel is reported in 46 ms
(in-process), 143 ms (OneCore) and 319 ms (32-bit host, IPC included).

**Sound card.** Every backend follows Settings → Device now: `Sapi5Backend` via
`SpVoice.AudioOutput`, OneCore and the 32-bit host via `SapiWavPlayer`. The mpv
device id and the SAPI output token are matched on the shared WASAPI endpoint
guid; empty/"auto" = system default. `Form1.SetAudioDeviceLive` routes a live
change to mpv AND the reader, and Settings' **Test voice** now speaks through the
card being chosen rather than the system default.

**Speed / volume / pitch are remembered PER VOICE** (`VoicePrefs.cs`;
`VoicePrefsTable` persists as an indexed `[TextVoices]` section in both
Settings.ini and Book.ini). Voices differ enormously in how fast they sound at
the same nominal WPM, so carrying the previous voice's numbers across a change of
engine or speaker is worse than useless. Picking a voice now shows/applies, in
order: **what this book was last read with using that voice → how that voice is
set up in Settings → the neutral default (175 WPM, 100 %, pitch 0)** — never the
settings of the voice being left behind. `Form1.ResolveVoicePrefs` is that
cascade; `RememberCurrentVoicePrefs` files the live values under the voice in use
(player volume/speed keys, Properties, and every save). Settings and Properties
stage the voices touched in one visit and commit them on OK/Apply, so Cancel
discards. Upgrading doesn't lose anything: a book's (or Settings') single old set
of numbers is filed under the voice it was last used with.

**Settings vs. the book (settled).** A book that has chosen its own voice in
Properties is never touched by a Settings change. A book that hasn't is reading
with the default, so it follows a Settings change — voice **and** that voice's
remembered speed/volume/pitch. (This replaces the old "TEMPORARY" live-push.)

**Still to do:** **SAPI 4** is the one speech source not covered (32-bit only,
would need direct COM interop in the host); see the auto-discovery requirement in
memory `project-tts-backends`. Engine labels stay exactly as the voices report
themselves — **no hard-coded renaming** (e.g. RHVoice shows as its vendor "Olga
Yakovleva"); Gordan wants to consult the authors before any relabeling.

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

## 8j. User speech dictionary

What NBR should say instead of what the book says: "John" read as "Džon", a
footnote marker skipped, a comma or an apostrophe dropped into a word to move
where a particular engine puts the stress.

**It ships empty and stays empty until the user writes something.** No supplied
rules, no abbreviation list, nothing inferred — Gordan's explicit instruction, and
the right one: what one reader wants another does not, and much of it depends on
which voice they use. NBR supplies the tool, the user supplies the content.

- **Where it applies.** `TtsReader` rewrites only the string handed to the speech
  engine (`Spoken()`, called from `SpeakCurrent`/`PreRender`) — nowhere else. The
  book's own text is untouched, so every stored character offset (reading
  position, headings, pages, bookmarks) stays valid, and braille (and the future
  on-screen display) still show what the author wrote. It runs *after* sentence
  splitting, so a replacement containing a full stop cannot break a sentence.
- **Literally.** A replacement is passed on exactly as typed; the spaces, commas
  and apostrophes people use to bend an engine's stress are the whole point, so
  nothing tidies them afterwards.
- **Three scopes, most specific first: voice → language → global.** A voice rule
  fixes one engine, a language rule belongs to the language whatever reads it, a
  global rule is the user's own habit. Each is its own plain-text file in
  `Dictionaries\` (`voice-<name>.dic`, `lang-hr.dic`, `global.dic`) so a
  dictionary can be backed up or passed to someone else.
- **A rule** is: pattern, match (whole word / anywhere / regular expression),
  case-sensitive yes-no, "say this instead" or "say nothing at all", plus an
  on/off switch and the user's own note. Rules apply in list order, each once over
  the sentence — a replacement can never re-feed its own pattern.
- **A user's regex cannot take the reader down**: patterns are compiled with a
  50 ms `MatchTimeout` and validated when saved (a bad one is explained on the
  spot, not swallowed while reading). Measured: a deliberately catastrophic
  pattern gives up after ~60 ms and reading continues.
- **UI**: Settings → Text Books → "Speech dictionary…" (`SpeechDictionaryForm` +
  `DictRuleForm`). The **Try it** box is not decoration — without it a blind user
  would have to find the right place in a book to hear whether a rule works; it
  runs the rules *as currently edited* and speaks the result in the selected
  voice. Space toggles a rule on/off in the list, Delete removes, Enter edits.

---

## 8k. Two looks side by side (temporary, for the redesign)

`UiTheme.cs`. While the new design is being worked out, the app can be built
either way and the switch lives in **Settings → Misc**:

- **Classic** — exactly what NBR has always looked like. `ClassicTheme.Style` is
  deliberately **empty**, so the look regular testing runs on cannot drift while
  the new one is being played with.
- **New** — where the redesign happens. Today it only proves the plumbing and
  shows the shapes are available (flat rounded buttons with hover/pressed faces,
  a quieter window colour); the layout is still the classic 3×4 grid.

**The seam.** A window builds itself exactly as before and calls
`UiTheme.Current.Apply(this)` **once, at the end** of BuildUI — Classic does
nothing there, New restyles what was built. When the new design needs its own
LAYOUT rather than a new coat of paint, `BuildsOwnLayout` flips to true and
`BuildPlayerLayout` takes the window over, again without touching the classic
path. Nothing in a theme touches roles, names or the tab order: the look changes,
what a screen reader gets does not.

**High contrast outranks both.** When Windows is in a high-contrast scheme,
`Apply` is a no-op whichever theme is chosen — the user has told the system what
they need to see, and hand-picked colours would override exactly that.

Persisted in `Settings.ini` `[App] Theme` (`classic` / `new`); the player selects
the theme in its constructor, before anything builds itself, and a change offers
to restart the app (a window builds itself once). **All of this is scaffolding
and comes out when the new look replaces the old one for good.**

**Design room on Gordan's machine, measured (2026-07-27):** the app is
DPI-unaware, so it draws in **1280 × 720** units which Windows stretches ×1.5 onto
1920 × 1080. Work area 1280 × **690**; window chrome costs 6 × 29 (fixed) or
16 × 39 (sizable), so the largest sensible dialog client is about **1264 × 650**.
The player is 640 wide — **there is width to spare and almost no height**. A
borderless window (`FormBorderStyle.None`) would win 29 units and, measured, the
caption text and accessible name survive for `INSERT+T` / `NVDA+T` — but that
needs verifying by ear before it is relied on.

**Settled with Gordan so far (2026-07-28)** — decisions only, nothing built yet:

- **Panel legends are short.** `Označi` (57 units at 12 pt) and `Oznake` (62)
  replace `Postavi knjižnu oznaku` (167) and `Knjižne oznake` (113). The longest
  legend on the panel is now **`Knjižnica`, 71 units at 12 pt / 88 at 14 pt**, so
  a side column of **91 units** carries the whole set. The full wording stays in
  `AccessibleName` — the screen reader still says "Postavi knjižnu oznaku, Ctrl+B".
- **Type.** 12 pt base, 14 pt for the display, 11 pt floor. Legends are printed
  under clean buttons; only the transport ring is iconographic.
- **A groove around every control.** Each button and the ring sits in a recess
  cut into the panel, **3–4 units wide** — that reads as about a millimetre on
  both a 13" laptop (0.22 mm per unit) and Gordan's screen (0.42 mm per unit).
  The groove is what solves the one real accessibility risk in a silver-on-silver
  panel: a control the same colour as its background has no edge without it.
- **Groove colour: near-black, two-tone.** Shadow wall (top/left) at `#000`,
  lit wall (bottom/right) at `#3A3A38`, so the groove itself looks round rather
  than like a drawn line. Measured against a `#C0C0BC` panel: shadow wall
  **11.5:1**, lit wall **6.2:1**, and **4.8:1** in the worst case where the lit
  wall meets the darkest part of the silver — every edge stays well past the 3:1
  a boundary needs. Wall against wall is only 1.8:1, which is fine: that pair is
  a modelling cue, not the edge that carries the information.
- **Legends are jet black** (`#0A0A0A`) on the silver — **10.8:1** on the panel
  body, 8.3:1 at its darkest, past AAA either way. Keep ~8 units between a groove
  and the cap height of the legend under it, or the text looks stuck to the shadow.
**The two lit colours, settled 2026-07-28.** Amber `#FFC14A` means **the keyboard
is here** — focus, and nothing else. Electric blue `#4FB8FF` means **the device is
showing you something** — the seconds marker, the power lamp, and the backlight
that flashes round a key when it fires. The first build had the marker in amber
too, at 1.0:1 against the focus ring, which broke the rule that those two must
never be confusable. Gordan's instinct for blue turned out to be the more
accessible choice as well: amber against blue survives red-green colour blindness,
which is the common kind, whereas amber against the glass's phosphor green would
not have. Blue measures 8.2:1 on the ring's near-black channel.

**A key does not sink when pressed** — it is not a switch and has no on and off.
Instead its well goes electric blue and the glow blooms outward onto the silver
over 260 ms. Firing outranks focus while it lasts.

**The power key.** With no title bar there is no X, so the panel carries its own:
a round key with a drawn standby mark at the top of the middle column, an electric
blue lamp beside it, above the speed slider. **The lamp burns steady while the app
is simply running and breathes — a slow fade up and down over 2.8 s, never a hard
blink — while a sleep timer counts down.** One lamp, two states, no second colour;
an active timer was otherwise invisible to anyone not using a screen reader.
Measured: the lamp's pixel sits at 169 with no timer and swings 62 ↔ 168 with one.
The sleep-timer key gets the same breath as a **steady blue bloom around it** on
the same clock, so lamp and key pulse together rather than drifting. Focus still
wins the well: amber inside the groove, blue blooming outside, so a focused
counting-down key shows both facts at once.
The full repaint stays at once a second — the in-between ticks repaint the lamp
and that one key, nothing else. The power key is **out of the tab order** by
Gordan's decision: whoever can see it can click it, whoever cannot has `Alt+F4`,
and a keyboard user could never reach a title bar's X either — so nothing is
lost, and the one irreversible key on the panel cannot be landed on in passing.
Verified by synthetic click: the process exits.

**Legends are live, not frozen.** They were captured once at build time, which
silently broke the sleep timer: the player writes its countdown into
`btnTimer.Text` and that never reached the panel. The canvas now prefers a key's
current `Text` over its stored legend, and drops to the last word when the live
text will not fit the cell — so "Sleep Timer 14:59" prints as "14:59" instead of
being cut off. This also moved the legends off the cached layer, which is right:
anything the player can change at runtime does not belong in a bitmap drawn once. Making room for it cost the ring six units of radius and moved the
speed legend from above its slider to below, which is what the eight keys and the
progress bar were already doing.

- **Focus lives in the groove.** Instead of a rectangle drawn over a control, the
  focused control's groove lights up. Against a near-black recess that is an
  enormous change, it does not disturb the legend or the relief, and it keeps the
  rule that every control must show focus from across the room.

**Built and running (2026-07-28).** `NewPlayerSkin.cs` does the layout and the
shapes, `SkinCanvas.cs` paints everything that is not a control, and
`NewTheme.Style` is now **deliberately empty** — `Apply` runs *after*
`BuildPlayerLayout`, so anything it did would undo the skin. The skin invents no
command and renames nothing: the same Buttons carry the same handlers and the
same `AccessibleName`s, only the two ring volume keys are new. Form1 exposes a
small `SkinParts` / `Skin*` surface for it and nothing else.

Two things worth keeping:

- **The glass is rendered from `tbInfo.Text`**, not from the player's internals.
  The part before the first `": "` becomes the silkscreened label, the rest is
  lit, and anything shaped like a time becomes flap tiles with the seconds
  dropped. What is drawn and what a screen reader reads therefore cannot drift.
- **The read-only fields are parked below the client area** (`y = H + 4` and on
  down), the same trick the `lblAnnounce*` labels have always used. They stay in
  the tab order and still speak; the drawn panel gets the space. `tbInfo` is out
  of the tab order by agreement — it is reached with `I`.

**Lessons from the first build, all measured on the screenshot rather than
argued:** a 4-unit groove of one flat colour reads as a **border, not a recess**
— what sells a hole is a light lip outside the bottom-right and a black cut edge
at the top-left. A two-stop face gradient reads as a flat card; the half cylinder
needs five stops plus a specular line on the crown and a dark one under the
belly. Ring marks of 1.4 units at `#6A706C` measured ~3:1 against the channel and
**vanished** — a third-party description of the screenshot reported the ring as
having no marks at all. And the first focus treatment replaced the whole recess
with amber, which made the focused key read as *a different control*: both that
describer and my own pixel scan miscounted the column because of it. Focus now
rides inside the well and the groove structure stays.

**The display (left square) — settled 2026-07-28.** Glass is **424 × 424** (480
less a standard 12 margin, the 4-unit groove and a 12 bezel on each side).

- **One fixed slot order, lines appear only when they have content** — so a value
  is always in the same place whatever the book type, which matters more to a
  screen reader than to the eye: title (2 lines reserved) · author · chapter
  (2 lines reserved) · page · bookmarks · **times** · publisher (year) · producer
  (year) · format · voice + WPM. **Part x/y and the per-part times are dropped**:
  for multi-file books the chapter line shows the part's *name*, which is more
  use than "3/17". Measured against 54 real chapter labels from the library —
  half of them do not fit one line at any size, hence the two-line reservation.
- **Times sit in the middle and split the box**: above is what the book *is*
  (static), below is where you *are* (live). **Hours and minutes only** — real
  split-flap clocks had no seconds, and dropping them takes the digits from 24 pt
  to **32 pt** (12.7 mm on Gordan's screen).
- **Retro treatment.** Static labels are 11 pt, dull, silkscreened onto the glass;
  dynamic values are 14 pt, lit, with a glow **behind** the glyph — the glyph
  itself always stays crisp, since blurring it takes exactly what low vision
  needs. Measured on `#0E1210` glass: silkscreen must not go below `#8A928C`
  (5.9:1) or it stops being legible; lit `#D8F0E0` is 15.7:1 and reads **3.0×
  brighter** than the label, which is the whole effect.
- **Numbers are split-flap tiles, drawn not fonted.** No usable free split-flap
  font exists and one would not help — the look is the card and the hairline seam
  across its exact middle, not the glyph. Consolas and Segoe UI Semibold are both
  installed and both have tabular figures. The tile is invisible against the glass
  on its own (1.1:1), so **each tile gets the same groove as the panel buttons**.
  Keep the seam 1 unit and never below 20 pt digits — it crosses 8, 0, 6 and 9.
- **Flip animation on the minute and hour only**, ~120–180 ms. Gordan's call is
  to run it even when Windows animations are off, on the grounds that once a
  minute is not disturbing; the classic theme remains the way out. Rules that
  make it safe: animation is a **target, not a queue**; only a ±1 change animates
  and anything larger snaps (a heading jump is not 40 flips); animation is
  **suspended while a seek key is held** and resumes on release; flipping
  reverses direction when seeking backwards, which shows the direction for free.

**Seconds live on the play ring, not on the glass.** A 44-unit dial was too small
to resolve (10 mm on a 13" laptop) and cost the digits a third of their size. The
ring is ~200 units — 84 mm on Gordan's screen, 44 mm on a laptop — and its
circumference carries **12 marks 52 units apart, so one mark = 5 seconds = exactly
one arrow seek step**. This is the answer to "what shows a 5-second step": at
H:MM the clock only moves on every twelfth press, the progress bar cannot resolve
5 s in a ten-hour book, and the percentage does not change — the ring marker is
the only thing that moves at that resolution, and it jumps a clean 30° per press.

- The marker rides the ring **band**, never crossing the play glyph, so it needs
  no transparency and keeps full contrast.
- It must differ in shape from the 12 scale marks *and* from the single/double
  transport arrows — a **filled lit dot** rather than a dash, since every 5 s it
  lands exactly on a mark and a dash would just look like a brighter mark.
- It must also be distinguishable from the focus indicator (which is the whole
  groove lighting up) — different colour and weight.
- It steps once a second, never sweeps; the panel centre is where the eye rests,
  so continuous motion there is more intrusive than it would be in a corner.
- **One ring, one meaning**: the ring is seconds. Progress through the book stays
  on the bar below.

**Tab order on the new panel, set by Gordan and confirmed working (2026-07-28):**
Play/Pause (where focus starts every time the window opens) · forward · back ·
up · down · seek step · speed · position · Library · Settings · Properties ·
Help · Go To... · Bookmark · Bookmarks · Timer. The power key is out of it.
Returning from another application restores the last focused control rather than
jumping back to Play. The keys stand in that same order down column A and then
down column D, which is **not** how BuildUI groups them — the columns are named
explicitly in `LayOutButtons` rather than taken from the arrays it hands over.

**The volume READOUT is deliberately not in the tab order.** The ring's two
arrows carry volume and speak on every step, so the field would only add a stop.
The cost is real and was weighed: volume can no longer be *queried* without being
changed. Gordan chose this over the alternative of giving volume a permanent slot
on the glass — one arrow press either way is not, in his words, all that
noticeable to the ear.

**Ring mapping and the mouse story — settled 2026-07-28.** The centre is
Play/Pause: `▶` when paused, `❚❚` when playing, and the `AccessibleName` says the
**action** the press will perform ("Reproduciraj" / "Pauziraj"), which agrees with
the glyph in both states. Around it: **up/down = volume, left/right = the seek
step currently chosen in the combo**. That kills the volume slider outright —
**the only two sliders left in the design are speed and the seek bar.**

- The four ring arrows need `AccessibleName`s that **name the current step**
  ("Naprijed, poglavlje"), refreshed whenever the seek-step combo changes;
  "Naprijed" alone says nothing. They are seek-step controls, so they take the
  "nothing further that way" beep — the exemption is only for the keyboard's
  plain arrows.
- **Open gap: volume has no readout any more.** It is not one of the display's
  twelve slots and its slider is gone, so a mouse user changing volume on the
  ring sees nothing. Suggested fix is a **transient readout** on the glass —
  appears for ~2 s on change, then the display returns to normal, exactly like a
  hi-fi amplifier. Costs no permanent slot.
- **The wheel does the fine step of whatever is under the pointer**: over the
  seek bar = 5 s (or one sentence), over the speed slider = one speed step, over
  the ring = one volume step. This is what gives mouse users the precision the
  keyboard gets from its plain arrows — dragging cannot, since the bar resolves
  about three minutes per unit in a ten-hour book.

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

## 10b. The three sub-windows under the new look

`DialogSkin.cs`. Library, Settings and Properties share one shell: **960 wide —
the player's own width — so they cover it flush left and right**, borderless with
the same rounded casing, a 12-unit silver rim, and the panel's dark glass.
**960 × 640** was measured against both screens: as a fixed dialog that is a
966 × 669 window with 21 units of headroom here and 59 on a 13" laptop; sizable
costs another 10. The ceiling is 661 fixed / 651 sizable, so 640 leaves a real
but small margin — a taller taskbar is what would break the sizable variant.

**The panel's rule matters more here, not less.** These windows are made of list,
combo and check boxes, so **every control stays a real control and is only
repainted**: a drawn GroupBox loses the group name a reader announces on the way
in, a drawn ComboBox loses type-ahead. The "stickers" are real `GroupBox`es with
a `Paint` handler.

**Properties, audio (done 2026-07-28):** info glass down the whole left third
(262 × 582 — measured on the real lines, see below), playback across the top of
the other two thirds, then the master check with **Bypass as a rocker switch** and
Reset all on the metal, then the six stages as stickers three and three, and the
buttons on the metal at the foot.

- **Thirds, not quarters, and 12 pt is the ceiling.** Measured on the real info
  text plus the worst case with all six stages on: in a quarter-width column the
  block overflows at *every* readable size, and in a third it overflows at 14 pt.
  At 12 pt the worst case is 19 lines against 22 available — **room for about
  three more fields before something has to give.**
- **The dialogs use 12 pt where the player's glass uses 14.** That is hierarchy,
  not drift: the panel is read from across the room, a dialog is read leaning in.
- **Bypass is not a duplicate of the master switch**, which is why it stayed. The
  audible result is identical, but `s.Enabled = chkMaster.Checked` is **written to
  the book on OK** while `Bypass` never leaves the dialog — so A/B-ing with the
  master risks saving "this book uses no processing". The master also greys out
  all six cells, so you cannot keep tuning while you compare.

**Two things this pass learned the hard way.** `ComboBox` and `NumericUpDown`
**throw** on `BackColor = Color.Transparent` — only controls that paint their own
background accept it, so those two get the glass colour instead. And a *generic*
reflow of a group's children pulls every label away from the control it labels:
those cells were laid out pair by pair and a loop that only knows "control"
cannot put them back. The children are recoloured and left where they are.

**Apply was dropped, deliberately (2026-07-28).** Every change is already live
through `onPreview`, so Apply would only mean "persist now instead of on OK" —
and Properties does not persist itself: the caller writes the book on
`DialogResult.OK`. Gordan's call, and it also removes the refactor that would
have needed. Note for anyone tempted to re-add it: **Apply exists in Settings,
not here, and never did here** — `SettingsForm` has `btnApply` because Settings
saves itself.

**The `?` hint system is in.** One small `?` at the top right of each of the
seven groups; its `AccessibleName` is "Help for <group>", never "?", because a
reader announcing "question mark, button" seven times says nothing. `F1` opens
the same text from wherever the focus already is, walking up the parents so it
works from a combo inside a group. The pop-up is modal, its body is a read-only
multiline TextBox (the shape a reader can walk line by line), `Esc` closes it,
and focus returns to exactly where it came from.

**Info column headroom, measured on the rendered dialog (not modelled):** text
reaches 355 of 580 units — **61 % full, about eight spare lines at 12 pt** — for
a book with five of six stages off. With everything on the model puts it at 19
lines of 22, so **plan against three spare lines, not eight**.

**Tabs, for hybrid books only:** a real `TabControl` over the **whole** client
area, strip at the top, each page carrying its own info column and controls —
because a hybrid's info box changes with the tab too (`tbInfo` vs `tbTextInfo`),
so the left column belongs *inside* the tab, not above it. Costs ~28 units (one
line off the glass) against the 78 that putting the strip in the info column
would have cost, and keeps the real tab role and arrow navigation. Audio-only and
text-only books get no strip at all.

**Not done yet:** the tabs above (needs a commit-without-close path, which is
new behaviour, not paint); the `?` hint buttons and their pop-up with `F1` as the
second route; tightening the innards now that the cells grew from 112 to 138; and
**hybrid books, which still have two tabs — the agreed layout has nowhere to put
a tab strip, so those keep the classic dialog until that is decided.**

---

## 10c. Choosing a voice — the flow, agreed 2026-07-29

**Language → Platform → Voice.** Not vendor: the **platform** is the thing that
actually changes behaviour (in-process or through the 32-bit satellite, SAPI or
OneCore), while Acapela or Ivona is only a name inside it. So the vendor stays
part of the voice's display name and is not a step. The platform list is
**Microsoft Speech, SAPI 5**, with SAPI 4 and a built-in eSpeak as possible
later entries.

**Bitness is not a user concept.** 32- or 64-bit is our routing problem, not
theirs; it is shown only if the *same voice* turns up in both, where it becomes
disambiguation — the same problem the duplicated Zira was.

**Per-language default voices are the point of the whole thing.** Settings holds
one default voice per language. Language detection currently has nowhere to send
its answer; with this, opening a book becomes *detect → look up that language's
default → set it into the book's Properties*, which is what finally makes the
detector a feature rather than a fact.

**Settings has no detection, Properties does** — Settings has no book, so there
is nothing to detect. Settings sets the rule, Properties applies it.

**"Set as default" lives in Properties, beside the language picker** (Gordan,
2026-07-29). You have just chosen a voice for this book; that button promotes it
to the default for that language, which is how Settings gets filled without
anyone having to go there. It asks first — "set <voice> as the default voice for
<language>?" — because it changes a rule that affects **every future book in that
language**, not the book in front of you, and nothing else in Properties does
that.

**Two things the first text-page screenshot exposed (2026-07-29):**
the info box and the picker can disagree on the same screen — it read
"Language: Serbian" while the picker showed Croatian — so the two need either to
agree or to be labelled apart ("detected" versus "reading with"). And the text
page's info box writes "Title Elizabeth George" and "Format TXT" **without the
colon** the audio page uses; that needs squaring, not least because the player's
glass renderer splits its lines on `": "`.

**Still to decide before code:** what happens when the detected language has no
default. The chain is language default → global default → nothing. **Do not fall
through to "first available voice"** — a voice that cannot speak the language
reads the book as gibberish, and a silent wrong choice is worse than an empty box
and a message.

---

## 11. TODO (open items)

- **A key fires but a keyboard SHORTCUT does not light it.** The backlight is
  hung on `Button.Click`, so the mouse and Enter/Space light the key, but the
  shortcut handlers call `BtnLibrary_Click(null, ...)` and friends **directly**
  and never raise `Click`. Do not "fix" this by swapping in `PerformClick()` —
  that silently does nothing when a control cannot be selected, which would be a
  behaviour change on the classic path too. It lands naturally with the move of
  the shortcuts onto function keys: that single `ProcessCmdKey` switch is the
  right place to call `NewPlayerSkin.Canvas.Flash(theKey)`.
- **Mouse operation cannot be tested by Gordan** (stated 2026-07-28) — so it has
  to be verified some other way, and half of it still is not. Driven with real
  synthetic input against the running player and **confirmed working**: Play /
  Pause in the ring centre, the ring's seek arrows, dragging the progress blade
  (jumped to the right chapter and the blade landed where the times said), the
  Library key, dragging the speed knob, and **the ring's volume keys together
  with the transient volume readout and the focus glow** — a capture after six
  synthetic down-clicks shows the bottom sector focused and "Volume 70 percent"
  lit on the glass, which is exactly 100 − 6 × 5.
  **Still not confirmed: the mouse wheel** (over the bar, the speed slot and the
  ring). Wheel events were sent but produced nothing a probe could read back, so
  it stays unproven until a sighted mouse user tries it.
  Note for whoever tests next: reading a parked field's text with
  `GetWindowText` gave a **stale** value and made working volume keys look
  broken. Trust the drawn panel, not the field text.
  That test pass is the thing that found the progress bar reading zero.
- **Realism, second tier, not done**: anisotropic (directional) brushing per
  part, and a true angular gradient on the ring rather than the per-sector
  approximation now in place. The casing also has **no drop shadow onto the
  desktop** — that needs `CS_DROPSHADOW` via `CreateParams` on Form1 itself,
  which the skin cannot reach from outside. Third tier (film grain, scanlines,
  drawn screws) was rejected: it costs contrast for nothing.
- **Publication year is never extracted** (raised by Gordan, 2026-07-28). The new
  info box wants "Izdavač (godina)" and "Producent (godina)", but no year field
  exists anywhere today — not in `BookData`, not in `DaisyParser`, not in
  `TextExtractor`, and nothing reads an audio year tag. The sources are there to
  read: DAISY `dc:date` and `ncc:sourceDate`, EPUB `dc:date`, ID3/Vorbis year via
  TagLib. **Gordan's recollection is confirmed** — his library really does carry
  years, but inside the wrong fields: `Publisher=Školska knjiga, Zagreb 2008.`
  and `Title=Catherine Coulter - FBI 01 The Cove 1996`. So a parser that reads
  `dc:date` correctly may still come back empty on real books, while the year is
  sitting in plain sight at the end of another field. Whatever gets built should
  fall back to a trailing-year sniff on publisher and title.
- **Keyboard model for the new player, decided 2026-07-28.** Tab + shortcuts
  stay exactly as they are — a roving-tabindex grouping was considered and
  **rejected for a good reason**: in a player the arrows are *global*
  (up/down = volume, left/right = seek) and a roving group would swallow them
  into whichever group has focus, so the two models are mutually exclusive.
  **Consequence, which settles the open slider question:** the volume/speed/
  progress controls must NOT consume arrows, so they keep today's
  `AccessibleRole.StaticText` semantics — drawn groove and knob for the eye and
  the mouse, value in `AccessibleName`, arrows still global. Announcing does not
  depend on any of it: `AnnounceToScreenReader` already speaks through a UIA
  notification (JAWS) and the NVDA client, focus or no focus.
  **Still to do:** adjust the tab order for the new layout, and move the main
  window's shortcuts off letter keys onto function keys, modifiers and
  navigation keys only. Watch out for: `F4` opens a focused ComboBox's dropdown,
  `F10` activates the menu bar, `Alt+F4` closes — none of those may be reused;
  `F1` should stay Help. The change does fix a real conflict (`cmbSeek` swallows
  letter keys as type-ahead while focused), but **check the laptop case before
  committing**: many laptops default the F-row to OEM media/brightness, so
  without Fn-lock every shortcut needs Fn held.
- **Settings → Misc is still an empty placeholder** — waiting on what Gordan
  wants there.
- **RESOLVED & CONFIRMED BY GORDAN (Session 18): the four per-voice/voice-
  routing symptoms.** Root causes were voice-name duplication across backends
  (SAPI description vs plain Name), the 32-bit host mutating the voice without
  `synthLock`, a look-ahead buffer that survived a settings change, a Cancel
  that reached only the active backend, and the text-book volume existing
  twice. All fixed (e05cba6) and tested by ear: "TTS-ovi se mijenjaju u hodu,
  zvučne kartice se mijenjaju u hodu". Details in sections 8g and 8e.
  **Properties is fully tested too (Session 18):** Cancel and OK on both an audio
  and a text book, and from the Library as well as the player — volume, speed and
  what each voice remembers all behave. Nothing outstanding here.
  **Known, not hit on this machine:** a 32-bit *buffered* voice pausing late
  would point at the host's playback pair (Play/Stop) — eSpeak uses the
  real-time path and 64-bit voices never go through the host.
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
