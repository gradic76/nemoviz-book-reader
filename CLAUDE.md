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
- **"Open folder" imports archives now (2026-08-03)**, one book each, through
  the same `ImportFileCore` Open file uses — into the library, source left
  alone. It used to refuse the whole folder, and **the stated reason was
  wrong**: nothing about multi-volume paths is unreliable, and the background
  scan proves the recognition works on every start. What the refusal was
  really protecting is one line inside the scanner — `ExtractAndScan` unpacks
  an archive **into the folder being scanned** and then **deletes every volume
  of it**. Correct for library-owned space; destruction of the user's own disk
  for a folder they picked to import from. Hence `LibraryScanner`'s new
  `extractArchives` flag, false on that path. A continuation volume whose entry
  point is missing is reported **by name** in the skipped list, which is the
  one thing the blanket refusal was catching.
  **The two traps, both got backwards once in conversation:** old RAR numbers
  its continuations from `.r00` because the first volume already used `.rar`,
  and a spanned zip is opened at its `.zip` while `.z01` is a part. Entry
  points are `.zip` / `.7z` / `.rar` / `.part1.rar` / `.001`.
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
which is why they show none.

**Text+audio DAISY — the sync map is built and measured (`DaisySync.cs`,
2026-07-30).** A hybrid DAISY already carries its own alignment: every SMIL
`<par>` pairs a `<text src="dtbook.xml#id">` with an `<audio src clipBegin>`.
Both halves existed here and had simply never been introduced — `TextParsing.
Assemble` hands back `idOffsets`, `DaisyParser` already resolves a fragment to
(file, clipBegin). What was missing was reading the par's **text src** rather
than its internal ids. SMIL is walked tag by tag in document order, not as XML,
for the reason the rest of the DAISY code is: declared encodings that lie and
doctypes pointing at dead URLs.

**Measured across all 22 hybrid samples** (`D:\Test Naslovi\Daisy text + Audio`
— DAISY 2.02 and 3.0, French, Portuguese, Vietnamese, Thai, Sinhala, English;
39 to 17 557 pars each): **21 of 22 join essentially completely**, most of them
exactly, the rest missing under ten pars out of thousands. `Annual_report_1997`
is the one that joins nothing, and correctly — its SMIL points at `ncc.html`,
so it is an ordinary **audio** DAISY, not a hybrid.

- **`TextDoc.SyncIds` carries the text half**, and it is cleaned *with* the text
  by `TextCleaner.CleanDoc`, exactly as the heading and page offsets are. These
  are raw-text coordinates; re-deriving them after the clean is the drift §8e
  already paid for once.
- **A page marker's own id has to survive.** `Extract` replaces `<pagenum>` with
  an empty anchor, which threw the element's id away — silently unjoining one
  par per printed page (153 of 524 in Annie John). The placeholder now emits the
  original id alongside its own.
- **The map is held in BOTH orders** (`SyncMap.ByChar` / `ByTime`), and this is
  not defensive coding. Four books' text and audio do not run strictly together:
  three genuinely read out of order (worst, a Plato edition, 738 of 6099 pars),
  and one had `clipBegin` running past where TagLib measured the audio to end —
  a *duration* problem, the same one `MpvDuration` exists for, not an alignment
  one. A binary search over a list not sorted on its own key does not return a
  near miss, it returns nonsense. With one list per direction the **round trip**
  (follow the audio to a point, ask where the text is, ask that text where the
  audio is) comes back exact in **18 of 22** books and within 7 points in three
  more; Plato keeps 41 of 755, which is the book, not the parser.
- **The timeline must come from real durations.** A probe that gave every file
  the sample's own 59.7 s average reported 59 of 368 steps going backwards —
  entirely its own doing, since a file longer than the placeholder pushes its
  `clipBegin` past the start of the next one. That measurement said nothing at
  all about the parser, and a first diagnosis blaming unstable `List.Sort` on
  tied offsets was wrong.

**Hybrid books now exist (`BookData.IsHybrid`, 2026-07-30).** A hybrid is an
**audio** book that also has the text — deliberately **not** an `IsTextBook`.
Making it one would hand the transport to TTS and silence the narrator, which is
the one thing the reader came for. So the transport, the position and the seek
steps stay exactly an audio book's, and the text is the *second output* §8l
describes: one position, several renderers windowing it. `DetectTextBook` gives
a folder that has audio **and** `content.txt` **and** `sync.map` the hybrid flag
instead of returning early, `BuildDaisyNav` no longer bails on `content.txt`
alone, and `LoadTextNav` runs for a hybrid so its headings and printed pages
come back too.

- **The map lives in `sync.map` beside `content.txt`, not in `Book.ini`.** An
  INI is a settings file written key by key and the biggest sample carries
  **11 953 points**; this is bulk data. One point per line, `offset seconds`,
  **invariant culture** — a decimal comma would read back as a different number
  on a differently-configured machine, so a book would be in sync on one
  computer and not the next. Written once at import, because rebuilding it means
  re-reading every SMIL file and one sample ships 385 of them.
- **`sync.map` is also what tells a hybrid from a TEXT DAISY**, which is why
  `SetupHybrid` writes **nothing at all** unless a real map comes out:
  `content.txt` with no map beside it is exactly how a text DAISY is recognised,
  so writing the text alone would turn a narrated book into a silent one.

**Measured end to end through the real import path and then a COLD reload** — a
fresh `BookData` built off the disk, which is what the app does next time it
starts, since a hybrid that only works before the app closes is not a hybrid.
Of the 22 samples: **21 come back as hybrids, 1 as plain audio, none broken.**
Every one keeps `IsDaisy`, stays out of `IsTextBook`, rebuilds its chapters, and
reloads its map; the round trip is exact in 19 of 21 (Origin of Species and one
Vietnamese textbook lose a point each, Plato 31 of 755).
**Rainbow Readers' 7 bad points disappeared here**, which confirms the earlier
diagnosis: through `BookData` the durations come with the `MpvDuration` fallback
the probe did not have, so those were mis-measured audio lengths and not
alignment at all.

**Still open:** nothing *consumes* the map yet — the player does not show or
follow the text of a hybrid, and Properties still needs the two-tab page (§10b),
which is now unblocked.

---

## 8d. Sound processing — Properties dialog (Session 12)

Per-book audio processing, opened from the library (Alt+Enter / right-click
Properties) or the player (Properties button / **Alt+Enter**, beep when nothing
is loaded). `PropertiesForm.cs` + `SoundSettings.cs` (settings model, persisted
in Book.ini `[Sound]`).

**Chain** (`SoundSettings.BuildAf` → mpv `af` as one `lavfi=[…]` graph, applied
by `Form1.ApplySoundProcessing`): highpass → afftdn (denoise) → deesser →
acompressor → EQ (bass/equalizer/treble) → speechnorm → alimiter.
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

**An auto-analyser belongs at IMPORT, not on the fly** (settled 2026-07-31 after
reading a comparable tool, `D:\Test Naslovi\SlušajKnjigu_Portable` — a
PyInstaller app whose whole configuration is in plain sight in
`_internal\config.json`).

Its approach is worth taking: sample the audio, then **decide which stages to
switch on** from measured thresholds — SNR 14 dB for denoise, spectral centroid
1500 Hz for "bright", clipping peak 0.96 with a 1 % ratio, low-frequency ratio
0.55 for muddiness. That is exactly the "I measure, Gordan judges by ear" split
this section has been waiting for, and those numbers are a free starting point
rather than something to derive from nothing. (Its own processing is a different
school — RMS normalisation, a three-band EQ as plain multipliers, Wiener denoise
in SciPy — not better, just not ours.)

**But it is a measurement of the recording, not real-time processing**, and that
decides where it goes:

- Changing filters mid-playback makes mpv rebuild the `af` graph, and that is
  **heard as a break** — at the start of a book, where the listener is paying
  most attention.
- The measurements need seconds to settle, so the opening moments would give the
  wrong answer.
- The book's settings are already per-book in `Book.ini [Sound]`, and import
  already walks every file for durations. The analysis costs one more pass.

**Sample several files spread through the book, not 20 s from one place.** This
section already records that level varies "between files recorded on different
days"; a single sample would set the whole book from one of them.

**Text sync does NOT help here** (asked, and worth writing down so it is not
re-asked): knowing where speech is would let the noise floor be measured in the
gaps between sentences, which is where it really lives — but ordinary voice
activity detection gives that from the audio alone, with no text and no sync. A
bonus if sync happens to exist, never a reason to build it.

**Settled 2026-08-03, from `docs/Audio properties.txt`.** The normalisation
method is **speechnorm, full stop** — the chooser, the `dynaudnorm` branch, the
`DynaudnormMaxGain` table and the `NormalizeType` field are all gone. A book is a
voice, and asking a reader to pick between two ffmpeg filters was asking a
question they have no way to answer. **Noise removal → Noise reduction.**
**Playback lost its `?`** ("jasno i trogodišnjaku") and **Sound processing gained
one**, standing on the strip beside the master switch: it is the only control
there with anything to explain, and the six stages below are what it explains.
Its width is now worked out backwards from where Bypass starts
(`DialogSkin.MasterWithHelpKey` / `HelpKeyBounds`), because the same code lays
out a hybrid's narrower page and a fixed 264 collided there. The help key's name
comes from `Prop.SoundProcessing`, not the caption — "Help for Use sound
processing" reads like an instruction, not a subject, so `HintSystem.Attach`
takes an optional one.

**Open items** (Session 12, deferred until "critical" sample recordings exist):
tune the preset values by ear; English-name review for the stage titles
(*Even out speech* is the one Gordan has left for later). Objective
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
  **The window offers two of the three** (Gordan, 2026-08-03): the language, and
  every voice that speaks it. There is no "all languages" line — standing inside
  one language and calling a scope "everything" read as a promise, and the file
  behind it belongs to languages the reader is not looking at. It is not
  unreachable: when Settings is on *All other languages* there is no language to
  name, and that first line **is** `global.dic`, which is also the one scope
  `ForLanguage` answers `null` for. Rules already in `global.dic` keep running
  either way — `Active()` is untouched.
- **A rule** is: pattern, match (whole word / anywhere / regular expression),
  case-sensitive yes-no, "say this instead" or "say nothing at all", plus an
  on/off switch and the user's own note. Rules apply in list order, each once over
  the sentence — a replacement can never re-feed its own pattern.
- **A user's regex cannot take the reader down**: patterns are compiled with a
  50 ms `MatchTimeout` and validated when saved (a bad one is explained on the
  spot, not swallowed while reading). Measured: a deliberately catastrophic
  pattern gives up after ~60 ms and reading continues.
- **UI**: Settings → Text Books → "Pronunciation dictionary…" (`SpeechDictionaryForm`
  + `DictRuleForm`). The **Try it** box is not decoration — without it a blind user
  would have to find the right place in a book to hear whether a rule works; it
  runs the rules *as currently edited* and speaks the result **in the scope's own
  voice**, so a rule written for Ivan is not judged in Dragana's mouth
  (`SpeakSample` takes the voice from the caller now). Space toggles a rule
  on/off in the list, Delete removes, Enter edits.
- **Gone from the window on 2026-08-03, all Gordan's calls.** *Move up / Move
  down*: order still decides what each rule sees, but rules that step on each
  other are rare enough that two buttons were not worth the row — reorder by
  removing and adding again. *"Would be read as"*: the try box already speaks the
  answer, and a written copy of it suggested the dictionary touches the text,
  which it does not. *The regex primer*: replaced by a short page that says what
  regular expressions are and advises leaving them alone unless you already know
  (`TextHelpForm` gained a `wrap` flag for it — prose wraps and the window closes
  down onto the text; a page laid out in columns must not). In the rule dialog,
  "Capital letters must match too" and "Say nothing at all" became **Case
  sensitive** and **Skip**, which are the conventions and say it in two words.
- **The hint is at the top of the window, visible, not behind a `?`.** It is the
  one place in the app where that is true, and it is right here: this window is
  opened on purpose by someone who may never have met a pronunciation dictionary,
  and there was room once Move up/down and the result row left. Read-only and
  tabbable, the shape every hint has. The window does **not** open focused on it —
  focus starts on the scope combo, or a reader who came to add a rule would sit
  through four sentences first.

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

## 8l. Three outputs, one position — the reading model (agreed 2026-07-29)

A text book has **three** outputs: speech, braille, on-screen. Everything built so
far quietly assumed speech was the only one — including the block on a book whose
language has no voice, which was written the same afternoon we were talking about
universal design. **"Cannot be spoken" is not "cannot be read."**

**They depend on language completely differently:**

| output | needs | coverage |
|---|---|---|
| visual | nothing at all | total — text is text |
| braille | a liblouis table | **480 vendored table files, ~170 language prefixes** |
| speech | an installed voice for that language | a handful on a typical machine |

So for an uncommon language braille may be the **only** way to read a book, which
makes an unconditional no-voice block actively harmful. **The gate must ask "can
any ENABLED output present this book?", not "is there a voice?"** — and the
no-voice dialog should offer all three ways forward, not just "borrow a voice".
Today only speech exists, so the behaviour is unchanged; the rule should be
*written* the general way now so nothing has to be unpicked later.

### The sync model

**The character offset is the sync point.** It already is the canonical position,
which turns out to be the thing that makes this work: one position, three
renderers windowing it. Sentence-to-sentence sync would have broken on the first
braille line shorter than a sentence.

**"When" and "how much" are two separate questions.** The clock says when to
advance; the unit says how much to take. Speech usually owns both — but not
always, see blink mode below. Separating them is also why a missing voice needs a
**pacer, not a fake TTS**: what braille and visual need from speech is a *clock*,
and a fake voice would drag in voice names, per-voice prefs and the speech
dictionary, none of which mean anything when nothing is spoken.
**"Use any voice and mute the speakers" is worse than it sounds**: a Croatian
voice over French text takes the wrong time per sentence, so the pacing would be
*wrong*, and NBR's own beeps go through the same card.
**Asynchronous reading is just the pacer switched off** — no separate mode. (NBR
still earns its keep there: library, position across sessions, bookmarks,
heading/page navigation, format handling. Only the *reading* part is what Notepad
also does.)

**Braille — two modes (Gordan):** *follow the TTS speed*, or *drive the TTS with
your own navigation*. They differ by whether speech runs **continuously** or
**only on demand**. Panning is a **seek**, like the plain arrows, and its unit is
one or more **display widths** — a new seek step alongside sentence / paragraph /
standard page, present when braille output is on.

**Visual — frames, not content units (Gordan).** Sound has no frame; a screen
does, and its size depends on the font the reader chose. Subtitle mode = two rows
as one frame; a sentence may fit or break into three. Blink and scroll have bigger
frames and their own limits. **In blink mode the frame decides how much** — the
text is shown exactly as long as the TTS needs for it, so speech must be handed
precisely what fits, or the frame flies away half-read. Speech still owns *when*.
**The screen never leads** — no researched app does that, and NBR does not need
it: the existing **seek steps already are** "screen leads", under another name.

**Speak / spell one word on demand** belongs to the *reader*, not to any one
output: a name or foreign word met in braille (which gives letters, not sound), or
a word a low-vision reader cannot resolve. Mouse click in the visual display; a
key otherwise.

### The braille transport — the hard constraint

**No display drivers.** Too many vendors, series and models; the binary would
balloon and some vendors charge for driver access (Gordan). **BrlAPI (BRLTTY)** is
the real universal answer — output *and* input, including routing keys, ~90
display families — but it is **the wrong bet on Windows**: BRLTTY must be running
and holding the display, while on Windows the display is held by NVDA or JAWS, and
two cannot fight over one device.

**Through the screen reader, speech and braille are different channels.** Sending
text to NVDA's `speakText` or JAWS's `SayString` — what PotPlayer and Screen Reader
for Kodi do, and what NBR already does — puts **nothing** on a display. Braille is
`nvdaController_brailleMessage`, a separate call, and a *message* channel:
transient, overwritten as soon as the reader has something else to show. Fine for
"here is the current sentence", poor as a surface to live in. JAWS has no
equivalent public call — braille there is written from a JAWS script, which would
mean shipping one.

### The idea worth testing before any of that is accepted

**Put the text in a real, focusable, read-only control and the screen reader
brailles it by its own normal tracking.** No API, no drivers, every display the
user's reader supports. It would also give **panning** (the reader handles it) and
**routing keys** — which move the caret in that control, and *the caret we can
observe*, which is the word-lookup, free. **Visual and braille output may then be
one feature, not two.**

Consequence if it works: panning becomes a **look-away, not a seek**, because the
reader handles it and we never see it — the opposite of the model above, and not
because it is better but because it is the only one that route allows.

**MEASURED 2026-07-29, and the route works.** With the sentence in a real
focusable read-only TextBox and focus on it, **NVDA put the text on the braille
display** — read off its Braille Viewer, no display attached, no drivers, nothing
but an ordinary control. It showed one display width (~36 cells) with the rest
behind a pan, as expected. **It also follows text rewritten in place**: with the
book playing and focus never leaving the control, the braille content changed
from one sentence to the next on its own.

**SOLVED, and measured both ways (2026-07-29).** The whole book goes into the
control **once**; after that only the **selection** moves onto the sentence being
read. Braille then follows essentially instantly — paired logs give **67–170 ms**,
inside the sampling interval — where rewriting `Text` had left it frozen on one
sentence for 35 seconds. The selection is the thing a screen reader is built to
track; replacing text is not.

It buys three more things at no cost: the rest of the book is **really there to
pan into**, a routing key becomes a **position in the book** rather than an index
into one lonely sentence, and `HideSelection = false` keeps the reading position
visible when focus is elsewhere. `TtsReader.FullText` exists for it — cleaned, so
it shares coordinates with `CharPosition`.

**What it looked like before, kept because the diagnosis is the lesson:**

**Replacing `Text` does NOT reach braille — it FREEZES.** Measured on 2026-07-29
by pairing two clocks instead of comparing screenshots: the braille viewer read
out of NVDA over **UI Automation** (its rows are `RICHEDIT50W`, unreadable with
`GetWindowText` across a process boundary, but their text comes back as the UIA
`Name`), against a timestamped log the player writes whenever the surface text
changes. In 35 seconds the surface went through **~20 sentences** while braille
sat on **one** — the sentence that was current when focus arrived — and never
moved. The blanks between samples are momentary clears, after which the *same*
text returns.

So **"replacing `Text` does not reach braille" is the finding**, and the idea
below stops being an optimisation and becomes the only way forward. Everything
in the paragraph that follows was written before this measurement and is kept
because the reasoning still holds — but where it says "lag", read "freeze": the
apparent movement in earlier screenshots came from focus changes made by the
clicks that produced them, not from the text updating.

**And it is silent — the caret is what makes it so.** A SELECTION is news to a
screen reader: it reads the marked text out, says "selected" and "not selected",
and repeats pieces, all over NBR's own voice. A **caret move made by the
application** is not: braille refreshes and nothing is spoken. So the reading
surface moves a zero-length selection, never a range.

The "random capital letters" heard earlier were **not** this. They came from
Gordan arrowing through the surface **by hand with playback stopped** — a user
walking the caret, which a reader announces character by character because that
is exactly what it should do. Confirmed with the book actually playing: only
NBR's TTS is heard, NVDA says nothing, and braille follows. (An earlier note here
claimed the chatter was unavoidable and pointed at `NVDA+S`. It was wrong — the
test behind it was hand-driven caret movement, not playback.)

**Method note, because it is reusable:** screenshots cannot measure a timing
relationship — the caret blink alone makes a pixel hash change. Two logs on one
clock can. The braille side needs no hardware and no image reading at all.

**(Superseded, kept for its reasoning.) It lags, and inconsistently.** Measured repeatedly with the book playing
and focus never leaving the surface: sometimes braille and the surface hold the
same sentence, sometimes braille is one or more behind. It is a lag, not a
freeze, and the cause is **not** the caret reset — removing `Select(0, 0)` from
the update path changed nothing, because **WinForms moves the caret itself when
`Text` is assigned**, so the event fires either way. That also explains the noise
Gordan hears: NVDA speaks the character under the caret on every sentence, which
is the new sentence's first letter and therefore always a capital.

**The idea to try next, and it may fix both at once: stop replacing `Text`.**
Put the WHOLE book in the control once and move the **selection** to the current
sentence instead. The text then never changes — only the position does, which is
what the reader is built to follow. It would also make panning natural (the rest
of the book is really there to pan into) and turn routing keys into a genuine
position, rather than an index into one lonely sentence.

**A constraint that falls out of all this: braille follows FOCUS.** For the book
to be on the display, the reading surface has to hold focus — click any button
and braille shows that button instead ("Pause, Space"). So the reading surface
cannot be one stop among many in the tab order; when braille output is on it has
to be where focus *lives*, with every other control reachable some other way.
That is a design consequence, not a bug, and it is why **nothing on the player
may swallow an arrow** — verified with Gordan: Space, volume and sentence
stepping all work from inside the control once the arrows are suppressed.

**Two traps that cost a false result each, worth remembering when testing this
way:** braille follows **focus**, so the first sampling run measured the *Claude
window's* prompt box and looked frozen (two hashes alternating — a caret blink);
and the run after that had the book **paused**, so nothing could change anywhere.
Always confirm from the same frame that (a) the player shows the pause glyph, and
(b) the braille content belongs to the book.

**CONFIRMED ON JAWS TOO (2026-07-31), which was the real gap** — every
measurement above had been NVDA, and §2 makes JAWS the primary reader. With
focus on the reading surface and the book playing, JAWS put the text on the
display and followed it:

| | braille line |
|---|---|
| t = 0 | `318p │ if she collared clients for thieves. But` |
| t = 14 s | `323p │ to dispel their own fears of him. But` |

The highlighted cell sat on the caret both times. It also confirmed the
focus constraint literally: while focus was on a key the display read
`btn Pause, Space`, and after one Tab `btn Forward, Shift+Right` — the same
strings NVDA gives.

**Two method notes, because the JAWS side is harder to measure than NVDA's.**
JAWS's *Braille and Text Viewer* is **opaque to UI Automation** — one `Pane`,
zero descendants, no `TextPattern`, where NVDA's viewer rows come back as UIA
`Name`. The only way in is a screenshot of the top strip of the screen, read by
eye. And the reading surface is **tab stop 16**, whose UIA `Name` is the entire
book (585 110 characters here), so it is found by the *length* of the name, not
by matching "Reading surface".

**Synthetic input, which had failed all morning, started working the moment
Gordan clicked into the player himself.** Worth remembering before concluding
that input is broken: the app needs a genuine foreground activation, and
`SetForegroundWindow` from a script does not always supply one.

**Status: braille output is solved and verified, bar one item.** It reaches the
display with no drivers, follows the reading at 67–170 ms, stays silent, and the
player's keys (Space, volume, sentence stepping) all work from inside the
surface. **Routing keys remain unproven** — the detection is built (the surface
polls for a caret it did not move and logs `ROUTED to <offset>` with the words
there), but exercising it needs either real hardware or a sighted hand on the
viewer's "route to cell by hovering". It belongs to the equipped-location pass.
**The original three things to prove. Two are audible, so no hardware is needed:**

1. Does the reader follow the control when its content is rewritten in place, or
   does it go quiet / re-read everything — the lesson `tbInfo` already taught?
2. Do **Space and the arrows** still work while that control has focus? An edit
   control likes to eat exactly those. NBR solved a version of this on the volume
   and speed fields.
3. Do routing keys really surface as a caret move? **This one needs a display.**

**NVDA's Braille Viewer (NVDA menu → Tools) removes the hardware blocker for the
rest**: it shows on screen exactly what would be on the display, as text — so it
can be screenshotted and read like any other window. JAWS has its own under
Utilities. Real hardware is then only a confirmation pass, at the equipped
locations Gordan has in mind.

### The visual output — settled with Gordan 2026-07-31/08-01, nothing built yet

**A separate window, never a modified player window.** The player is a fixed
borderless casing with a `Region` clip; resizing it does not enlarge the text, it
breaks the drawing. A second window also gives **two Alt+Tab entries**, so a
reader has "player" and "reading" as two places rather than one window that
changes identity underneath them.

**One window, three behaviours** — not a subtitle mode inside the player plus a
separate full-screen one, which would be two implementations to keep in step:

| Display mode | the same window, differently |
|---|---|
| Two text rows (subtitle) | **fixed 960 × 480 at the player's own position**, so it reads as the player having *become* a display. The player is really behind it. Deliberately NOT responsive — stretching it breaks the illusion. |
| Full screen, instant | fills the **working area** |
| Full screen, scrolling | fills the working area, text scrolls |

**Size from `Screen.WorkingArea`, not a fixed number.** A fixed 1200 is a bet on
16:9 and overflows anything narrower; the working area is also the only thing
that knows about the taskbar. Measured here: 1280 × **690** of a 1280 × 720
drawing space (§8k).

**The text column is 60 characters, centred, whatever the window size.** That is
the number the reading-difficulty guidance converges on (45–75); long lines are
hard to track back to the start of the next one. It has a consequence that is
easy to get wrong: **line length in characters and font size are not
independent.** At 960 units wide, 60 characters means roughly 26 pt — at 12 pt
the same width holds about 130. So `+`/`−` must change the font **and** narrow or
widen the column to hold 60, rather than changing how much text is on a line.
The empty margins are not wasted space; they are part of the help.

**There IS a frame, and it costs nothing.** Since the column is capped, the rim
and controls occupy margin that was going to be empty anyway — dropping the frame
buys not one extra character. It earns its place three times: the controls have
somewhere to live (no auto-hiding OSD imitation, which is a lot of machinery for
a mouse user who is not the primary audience), the window looks like a window so
`Escape` is expected, and text that touches the screen edge reads worse than text
in a bounded field.

**But quieter than the player's panel.** That one is deliberately rich because it
is an instrument read once from across the room; a reading surface wants the eye
on the *text*. Same materials, calmer: thin rim, no ornament in the field of
view, and **controls along the bottom edge only** — the top competes with the
return sweep of the eye to each new line. Controls are the three transport keys,
`+`/`−`, and the font picker.

**`Escape` closes it.** Standard Windows convention — dialogs close on Escape,
main windows do not — and this is dialog-class. No clash with the info box, which
is a different window (Gordan).

**In high contrast the casing gets out of the way entirely** (§8k). There the
user has told the system what they need; our colours, chosen or not, yield.

### Fonts — measured, not taken from the specimen pages

**Same policy as languages and voices** (§10c): a global preferred font in
Settings, a per-book override that is remembered, and if nothing suits the
book's script, the **system font**. Fonts differ from voices in one way that
must not be copied across: **a font can never block anything.** A voice that
cannot speak the language produces noise, which is why `NoVoice` is a real state
that stops a book loading. A missing glyph is not noise — Windows font-links to a
fallback and the text stays readable. So there is no "no font" state, no dialog,
and no unread book.

**Suitability is measured against the book's ACTUAL characters**, not a
language→script table. `content.txt` already exists; take its distinct characters
and test them against the font. A Croatian book can quote Greek, and a table
would say everything was fine while the reader saw boxes. Same principle as §8e
refusing to trust a declared `dc:language`.

**Transliteration was considered and DROPPED.** It solves a problem that does not
arise — Windows has a system font for every script it supports — and it would
break §8j's rule that the book's own text is never rewritten, only the string
handed to the synthesiser. A blind reader on braille would get one thing and a
sighted reader another, and a Serbian reader who chose Cyrillic would silently be
handed Latin. As a *deliberate, opt-in reading aid* it would be legitimate — and
Serbian is the ideal first case, its two scripts being a true bijection — but it
has no place in a font fallback chain.

**Measured coverage (2026-08-01), and it contradicts the marketing:**

| font | glyphs | hr | sr Cyrillic | ru | Greek | Vietnamese | Thai/Sinhala/Arabic |
|---|---|---|---|---|---|---|---|
| **Andika** | **2 448** | yes | yes | yes | yes | yes | no |
| Lexend | 685 | yes | no | no | no | yes | no |
| **Atkinson Hyperlegible Next** | **362** | yes | **no** | **no** | **no** | **no** | no |

Atkinson's specimen pages advertise "150+ languages including Cyrillic, Greek,
Arabic, CJK and Devanagari". **The file contains 362 glyphs and Latin only** —
and upstream, `googlefonts/atkinson-hyperlegible-next` issue #10 ("Are there any
plans to add Cyrillic?") is still **open**. Google Fonts has no per-script
Atkinson families either, only `atkinsonhyperlegible`, `…mono` and `…next`. The
claim is not quite false — 362 glyphs really does cover a hundred-odd Latin
languages — but it misleads anyone who does not open the file. **Verify coverage
from the `cmap`, never from the specimen page** (`System.Windows.Media.
GlyphTypeface.CharacterToGlyphMap` gives it in one line).

So **Andika is the important one**: the only assistive face covering Cyrillic,
Greek and Vietnamese, which is a good part of this library. Atkinson stays, but
is offered to Latin books only — the coverage filter does that by itself.

**Bundled, never installed.** Installing modifies the user's Windows and NBR is
portable; load them privately in-process (`PrivateFontCollection` /
`AddFontMemResourceEx`). Only **regular and bold** — reading has no use for seven
weights, which keeps the whole set to a couple of MB.

**The user's own installed fonts are offered too**, on the §10c pattern: the
**curated short list in Properties** (picking for one book, in passing), **every
installed font that passes the filters in Settings** (where the rule is set
deliberately). Three filters, none of them a matter of our taste:

1. the book's own characters, as above — this alone removes Wingdings, Marlett
   and most symbol and decorative faces, since they have no `č`;
2. **PANOSE family type = 2 (Latin Text)**, which the font's own author wrote
   into the file, so "made for reading text" is the author's claim, not our
   opinion. Keep this filter **soft** — behind a "show all" — because a fair
   number of fonts carry it empty or wrong;
3. skip bitmap (non-scalable) faces, which fall apart on `+`.

Nothing needs saying when a globally chosen font does not suit a later book: the
per-book filter simply does not offer it and the system font takes over.

### Hiding the reading surface from the eye but not from the reader

Gordan's question (2026-07-31): how does the text stay off the screen while the
screen readers still catch it and put it on the display?

**Not with `Visible = false`, and not with `Enabled = false`.** A hidden control
is taken out of the accessibility tree altogether — no reader sees it, so there
is nothing to braille. A disabled one is announced as unavailable and cannot
take focus, and focus is precisely what braille follows.

**The trick NBR already uses is the answer** (§8k): park it **outside the client
area** — `y = H + 4` and downwards, exactly what the read-only fields and the
`lblAnnounce*` labels have always done. The control stays `Visible = true` as far
as Windows is concerned, keeps its place in the tab order, and still reports its
text; the window simply never paints that region.

**MEASURED AND SETTLED 2026-08-01: parking it WORKS.** With the surface at
`y = ClientSize.Height + 4` — invisible on screen — and a real book playing in
the real player, NVDA put the text on the braille display and followed it
sentence by sentence: 13 changes in 20 samples over 48 seconds, reading
"a job that demanded a hardened hide", "such a trade.", "she was formidable.",
"In the dark cities of the drow elves" and on through the book. Focus was
confirmed on the surface from the same frame (its UIA name is the whole book —
585 110 characters).

**So the old note in `Form1.cs` was wrong**, and it has been corrected there:
it claimed parking had been tried and "showed an empty braille viewer", and
concluded braille goes through the reader's *screen* model. It does not.

**The old failure was almost certainly the focus trap, which caught this project
three more times in one afternoon** and is worth listing, because each looked
exactly like a real negative result:

1. Two runs read the **braille viewer's own check box** back — the test window
   never took the foreground, and `SetForegroundWindow` from a script returns
   false. Only a human clicking the window lifted it.
2. A third read a **stale line**: the harness was doing `Controls.Clear()` and
   re-adding on every switch, which destroys and recreates the control's window
   handle — to a screen reader, the object vanishing and a new one appearing.
   Moving the control and changing its z-order leaves the handle alone.
3. And the first attempt at reading the viewer took its **last** UIA row, which
   is the viewer's own UI. The braille text is row **[2]** — row [1] is the dots,
   [3] and [4] are its check boxes.

**Always keep a control placement in the test.** A run where the visible case
also fails is a broken harness, not a finding — that is what caught every one of
these.

**And it should be ONE control with two placements, not two controls.** When
visual output is off, the surface is parked; when it is on, the same control is
sized, styled and placed as the display. That keeps braille behaving identically
either way, and it is what makes visual and braille "one feature, not two" as
this section hoped rather than two things that must be kept in step.

### Braille output IS the reading window (Gordan, 2026-08-01) — decided, not built

**A hidden surface creates an invisible dependency**, and that is the objection
that settles the design. Braille stops, and the reader has no idea why or what
they changed. A window is the opposite: a **place you are either in or out of**,
with its own Alt+Tab entry, that Escape leaves. When you leave it you know you
left it.

**The principle, in Gordan's words and worth applying beyond this case:** *if we
cannot automate something to the point where the user need not think about it,
then the user has to be told what it depends on.* A hidden mechanism that quietly
stops working is worse than a visible condition they understand.

**And the window's appearance is irrelevant to a braille reader** — to them it is
not a picture but a place, and a place works the same whether it looks like a box
for fifteen words or our styled scroll. So braille needs no separate surface and
no separate design: it is the same window the visual output already uses, which
is what §8l hoped for when it said the two "may then be one feature, not two".

**What this changes:**

- Braille output stops being its own switch and becomes **the reading window
  being open**. Turn it on → the window opens → focus lives there.
- The parked surface stays only as the control's home **while no window is
  open** — and then there is no braille, which is now stated rather than
  silently true.
- `NvdaController.Braille` keeps its job but a smaller one: the window has its
  own controls (transport, size, font), so focus can still wander off the text
  *inside* it. That is exactly the gap it covers.

### A braille display's own keys already work — there is nothing to build

Gordan asked whether a display's keyboard emulation could drive NBR's navigation.
It largely already does, and the reason is worth knowing: **a display does not
send keystrokes to Windows.** It sends commands to the SCREEN READER over its own
protocol, and both NVDA and JAWS offer "emulate a system key" as an assignable
action. Once the user maps one of their display's keys to, say, F9, an ordinary
keystroke arrives and **nothing in the application can tell it from the
keyboard**. Equally, an application cannot request or configure any of it — the
mapping belongs to the reader, and to the user.

**Confirmed in the installed NVDA user guide** (better than a web search — it is
the version Gordan runs): *"NVDA supports inputting keyboard shortcuts and
emulating keypresses using the braille display… Commonly-used keys, such as the
arrow keys or pressing Alt to reach menus, can be mapped directly"*, and **"the
driver for each Braille display comes pre-equipped with some of these
assignments"** — so arrows arrive with no user setup at all. Modifiers go through
**virtual modifier keys**: toggle Shift, then press the key. Two steps, not a
chord, and the guide warns it interacts with contracted braille.

Which lands well for NBR without anyone having planned it: the things used
*constantly while reading* — **volume (Up/Down) and the small nudge
(Left/Right)** — are bare arrows and work in one stroke. The occasional ones
(seek step, speed) carry a modifier and cost two.

**Capability varies enormously by model, and Gordan's call is therefore that
braille shortcuts simply FOLLOW the keyboard ones — no braille-specific
duplicates.** The range runs from a display with only routing and thumb keys
(reader maps them), through one with a braille keyboard (virtual modifiers), to
one that **is** a keyboard: the Baum Pronto! 40 ships with two interchangeable
physical keyboards, braille and full QWERTY, swappable without rebooting, and the
APH/HumanWare Mantis Q40 does the same. On those, our shortcuts arrive as
ordinary keystrokes with nothing mapped at all. A braille-specific key set would
be redundant on one end of that range and no help on the other.

So the only two things in our hands are these:

- **Keep the shortcuts simple enough to be worth mapping.** Modifier-free
  function keys are the easy case. Moving off letter keys (§11) was done because
  the seek combo swallows them as type-ahead — that it is also the best possible
  choice for display emulation is a happy accident, but it is a reason to keep
  them that way.
- **Document them**, since the user has to know what is worth mapping. That goes
  in the HTML manual.

**And it exposed a real gap, now fixed:** the reading window forwarded only the
transport keys, so from inside it the Library, Go To, the bookmarks and the timer
were unreachable — a room with no doors, and precisely the room a braille reader
is meant to live in. It now forwards the whole set.

**Still unproven and still needing hardware: routing keys.** Those are the other
half of the story — a cell tap moving the reading position — and the detection is
built but has never been exercised.

### The reading window opens on PLAY (Gordan, 2026-08-01)

Opening the player says nothing about what the reader means to do — continue this
book, pick another, or something else — so it must not put a second window in
front of them before they have decided. **Play is the decision**, and it brings
the book's properties with it. Once per book, so closing the window is not undone
by the next pause and resume.

Two traps met on the way, both worth keeping:

- **A failed window must not destroy the reading it was for.** `BeginInvoke`
  throws when the form has no handle yet — which is exactly the case while a book
  loads at start-up — and the call sat inside the `try` that read the text, so
  the throw landed in a catch meaning "the text could not be read" and wiped it.
  F9 then refused with the text read, the sync map loaded and 3 674 points in
  hand. A catch that names one failure must not be able to catch another.
- **Taking focus is the hard part, and is NOT settled.** The window appears but
  the play path keeps putting focus back on the player's own controls, so Gordan
  repeatedly had to fetch it with F9 — and a window nobody is standing in does
  nothing at all, since braille and the test aid both follow FOCUS. One deferred
  attempt was not enough; it now retries every 120 ms for about three quarters of
  a second, stopping as soon as the surface really has it (`SurfaceHasFocus`).
  **Still reported unreliable — needs another look.**

Also open: **hybrid sync cannot be judged by ear.** Gordan tried the French and
the Darwin and could not tell whether narrator and text stay together. This needs
someone sighted watching the caret, and no amount of instrument work replaces it.

### What is left on the three outputs (Gordan's list, 2026-08-01)

1. ~~**Make braille output open the reading window**~~ **— DONE** (`186dfff`).
   `TextBraille` and `TextBrailleTable` persist beside `TextVisual` /
   `TextVisualMode`, and `BookData.OpensReadingWindow` is what the player now
   tests, so either switch brings the window up. Focus already landed in the
   surface on `Shown`, so nothing was needed there.

   **The convention the two switches follow (Gordan, 2026-08-01, `563d290`).**
   The starting question was whether visual should imply braille or whether
   braille stays a separate tick. The answer turned on a fact worth stating
   plainly: **visual output gives braille whether we ask for it or not.** The
   display is fed by the screen reader following FOCUS into the reading surface,
   so nothing in NBR enables it and nothing in NBR could disable it. A check box
   claiming to switch braille on or off would be a lie the display would
   immediately contradict. So they are not two outputs — they are one output and
   one declaration about the reader.

   - **Use visual stands alone.** On, off, an ordinary sighted setting; opens no
     braille channel of ours. A reader who *has* a display still gets braille,
     and that is the platform working, not a leak.
   - **Use braille brings visual with it.** Ticking it turns the window on,
     drops it to the smallest form (two rows, the subtitle strip) and
     **disables** the visual box so it cannot be pulled out from under. Untick
     braille and the box comes back, still ticked, to do as one likes with.

   Two implementation notes that are easy to get wrong: the mode is set on the
   **transition**, in the check box's own handler — doing it in
   `UpdateTextEnabled` would snap the choice back to two rows every time
   anything else on the page was touched. And `UpdateTextEnabled` **repairs** as
   well as enforces, because the braille group is built *before* the visual one
   (on load the transition handler fires while `chkTVisual` is still `null`) and
   because a book stored while the switches were independent can carry braille
   on with visual off.

   Accessibility consequence, handled: **Windows skips a disabled control in the
   tab order**, so a screen-reader user never lands on the greyed visual box and
   would have no way to learn it is on, let alone why. The glass says *"Required
   by braille output"* under it — the one place they will hear it.

   The table combo deliberately does **not** reuse `book.BrailleTable`: that one
   back-translates a `.brf` being *read*, this one describes a text book being
   written *out*. Same library, opposite directions. **Open:** with the screen
   reader doing the translation, our table choice does not actually reach the
   display — it is stored and remembered, which beats forgetting, but whether
   NBR should translate cells itself is Gordan's call, not something to wire
   silently.
2. ~~**Bundle the fonts.**~~ **— DONE** (`6c88cd0`). Andika, Atkinson
   Hyperlegible Next, Lexend, Luciole and OpenDyslexic, 2.3 MB, embedded in the
   assembly and registered into the process only. Three things had to be
   measured rather than assumed, and every one of them changed the code:

   - **`PrivateFontCollection.AddMemoryFont` keeps only the FIRST FAMILY.** Give
     one collection several families and the rest are dropped — no exception, no
     failed return, they are simply not there. The first build registered Andika
     and silently lost four. Two faces of the *same* family are fine, so the
     fonts are grouped **one collection per family**. `AddFontFile` does not
     behave this way but wants the files loose on disk, which is the thing
     embedding was meant to avoid.
   - **Register with GDI+ *and* GDI.** GDI+ renders; GDI is what `CanRender`
     reaches through `Font.ToHfont`, and it cannot see a GDI+ private font. One
     way only and `GetFontUnicodeRanges` answers for whatever face GDI
     **substituted** — our font judged on a stand-in's coverage. With
     `AddFontMemResourceEx` alongside, the probe reads the real ranges; all five
     cover the full Croatian set.
   - **`new Font("Andika", 26f)` does not give you Andika.** GDI+ resolves the
     name against *installed* fonts and returns Microsoft Sans Serif — shown
     side by side in the check. A private family must be built from the
     `FontFamily` the collection owns. That is what `BundledFonts.Make` is for,
     and why `ApplyFont` goes through it.

   The two variable fonts arrive as a shelf of weight families (Lexend eight,
   Atkinson five, several chopped at GDI+'s 31-character family-name limit).
   That is a weight axis wearing family clothing, so only base families reach
   the picker — kept by rule (*a name that is another name plus a suffix is a
   variant*), not by the order GDI+ happens to return them in.

   Licences in `Fonts\Licences`, each copyright line read out of the font's own
   name table rather than a download page. OpenDyslexic's metadata claims "All
   rights reserved" with no licence URL; its upstream repo ships OFL.txt for
   exactly these files, so the **metadata is stale, not the licence**. No font
   is modified, which satisfies the Reserved Font Name clauses outright.
3. **Test the four combinations on a real book**: speech alone, speech+braille,
   speech+visual, speech+braille+visual. Note that the first two differ *only in
   where focus sits*, which is precisely the thing worth watching. **This is the
   one left, and it needs Gordan** — hardware and ears.

**Two of the three colour pickers are real as of 2026-08-03.** Text colour and
background colour are stored per book (`BookData.TextColour` /
`TextBackColour`, `[Settings]` in Book.ini, indices into the new
`ReadingColours`) and the reading window paints with them. **High contrast still
outranks them**, and below that the book's pair outranks the skin's dark glass —
a reader who went into Properties and chose yellow on black meant it. Round trip
verified, including a hand-corrupted index. Defaults are what the dialog always
showed before anyone could choose: yellow on black.

**The highlight works too, since 2026-08-03, and what unlocked it was Gordan
saying what it is for:** *"ne trebaju ti boundaries po strukturnim dijelovima
teksta nego caption iz samog prikaza"* — the unit is **the display's line**, not
a unit of the text. "Current word" had been offered in the combo and could never
have been kept: marking a word needs the engine to report which word it is
speaking, and no backend NBR uses does. **The item is now "Current line"**, with
"Current sentence" beside it, and both are things the control itself can answer
for at any font size and any wrapping.

- **The surface is now a `RichTextBox`**, for the one reason that a plain edit
  control cannot colour a range. Everything the braille path was measured on is
  the same underneath: a real, focusable, read-only edit control whose CARET the
  reader follows. No rich text comes from the book — the text still goes in
  plain.
- **The selection is borrowed and given straight back.** Colouring a range is a
  selection-based API, so the selection exists for the length of two calls and
  the caret is restored. Verified: after painting, `SelectionLength` is **0** —
  a standing selection is exactly what made a screen reader read over NBR's own
  voice when this was tried by selecting the sentence.
- **Verified by reading the colour back off the text**, not by eye:
  `SelectionBackColor` inside the marked line is the chosen blue, the characters
  either side of it are the background.

**The three modes are three modes now.** They used to differ only by whether a
scroll bar was drawn — both full modes let `ScrollToCaret` do the work, which
scrolls the least it can and therefore sat still until the reading hit the
bottom edge and then jumped. Now: **two rows and instant switch turn pages**
(the band is divided into frames and the frame holding the reading is shown
whole — measured stepping 0, 0, 2, 2, 4, 4 through a two-row window), and
**scrolling keeps the line in the middle**, so text rises past it steadily.
`EM_GETFIRSTVISIBLELINE` / `EM_LINESCROLL`, because `ScrollToCaret` cannot
express either.

**The Settings side has a home now, and it is the RULE.** `AppSettings` carries
all six under `[Visual]`, Settings reads and writes them, and **a book with no
look of its own inherits them on load** through `AppSettings.Current` — the same
shape as the language→voice rule (§ *Settings and Properties are the same two
combos*). Once a book is saved it owns its copy, so changing the rule later does
not walk over a book someone set up by hand. Verified: a rule of white-on-red,
marked green, scrolling, is inherited whole by a fresh book; that book then
choosing yellow keeps yellow while the rule still fills everything else.

**The reading window remembers its font and size**, in `[Visual] Font` /
`FontSize`, written the moment a face is applied and only on a real change. Not
per book, by Gordan's call — *"najbolje da pamti zadnje odabrano"* — because
this is the reader's eyesight, which does not change from one book to the next,
and it is chosen in the window rather than in Properties.

### Waiting on Gordan (2026-08-03) — the display and braille

Everything above was verified by machine: colours read back off the characters,
the paging measured, the inheritance round-tripped, `Settings.ini` byte-identical
after the probe. **None of it has been seen or felt.** Two things need a person:

1. **Braille on the `RichTextBox`.** The path was measured on a `TextBox`. The
   caret is the same and the control is still a real focusable edit control, so
   there is every reason to expect it to behave — but "every reason to expect"
   is not the standard this feature was built to. Open a book with the display
   and check the line follows as it did, **and that the reader does not comment**
   while the mark moves.
2. **The three modes in motion.** Paging steps 0, 0, 2, 2, 4, 4 through a
   two-row window and the scrolling mode holds the line in the middle; whether
   that reads well at reading speed is an eye's question, not a number's.

### Still open

- **The WPM floor.** 80 WPM was chosen for *speech*; driving **fingers** it is
  still fast for a beginner or a foreign language. Braille probably needs its own,
  lower range rather than inheriting the TTS number. (In *braille-leads* mode the
  problem dissolves — but that mode needs input we may not have.)
- **Visual on, no voice for the language:** the book opens and is readable but
  silent. Say so once, or stay quiet? Not decided. No block either way.
- **DECIDED (Gordan, 2026-07-29): Play, elapsed/remaining and the sleep timer are
  all functions of the SPEECH, and none of them needs a new meaning.** Where
  speech is part of the session — the synchronised reading this section is about
  — every one of them works exactly as it does today. Where it is not, they
  simply do not apply: Play has nothing to start, the times are computed from the
  synthesiser's WPM while the *reader* is setting the pace instead, and the timer
  has nothing to quieten. So the question was never "what do these mean for
  braille", it was **"is speech in this session at all"** — one flag rather than
  three redesigns. Position tracking by character offset survives all of it.
  **One idea worth keeping, not required:** the sleep timer could become a plain
  **reminder** for someone reading silently — "45 minutes and then you have to
  get ready" — sounding an alarm **without stopping anything**. That is a
  different thing from the timer NBR has, which exists because someone is
  listening and plans to fall asleep (§7), and it should not be bolted onto it.

---

## 9. Library window

### Now reading is its own place (2026-08-03)

**"Now reading" is whatever the PLAYER has loaded, and nothing else.** The
Library used to decide for itself: the last opened book, *and* only if it had
been listened to at all. Those are two different questions, and they gave two
different answers — Gordan had *Test rječnik* loaded and playable while the
Library said "No book loaded", because he had not yet played a second of it, so
the shelf filed it under Unread and refused to call it the book being read.
**Loading a book is reading it**; progress decides how far in, not whether. The
player already hands its answer in (`currentBook.FolderPath`, or null), so the
two windows now agree by construction rather than by two rules kept in step.

That also closes the invariant Gordan asked for: "No book loaded" can now only
mean the player is holding nothing, and that is precisely when
`DecideStartupView` sets `openLibraryOnStartup` and the player opens this window
by itself.

**Two lists, not one with a pinned row.** The book being read sits in its own
one-row `ListView` above a **one-pixel rule**, and is **taken off the shelf
entirely** — a book in two places at once is a book you can lose track of. Each
list is its own stop for Tab, which is the point: "what am I reading" is answered
by *arriving somewhere*, not by trusting that the first row of a long list is the
right one. When nothing is loaded the list says **"No book loaded"** in a row of
its own, because arriving at an empty box and being told nothing is what leaves a
reader wondering whether something failed.

**The library opens focused on Now reading** (Gordan) — the question people most
often come here with is "carry on", and that is now answered by pressing Enter
where focus already is. **The focus has to be POSTED**, not set in `OnShown`: the
form's own activation puts focus on the first control in the tab order
afterwards, and the search box won every time.

**`GetSelectedBook` answers from whichever list has focus**, falling back to the
shelf. Both lists keep their selection while focus is elsewhere — that is what
shows a reader where they were — so "which one is selected" is not a question
with one answer, and focus is what settles it.

**The tab order is written down once** (`TabRing`), and `StepTabRing` walks it
both ways with wraparound: **Now reading → Bookshelf → Infobox → Search → Filter
→ Refresh → Load → Close**, and the exact reverse. It used to be a handful of
separate "if this has focus and Tab is pressed" rules, which is why it was
neither symmetric nor closed — the way back was not the way out reversed, and
once the ring handed over to the default order Now reading fell out of it for
good. (Docking makes Now reading the *last* child of its panel, so by TabIndex it
comes after the shelf; for the same reason the FILL control must be added to
`Panel1` first.)

**Finding the focused control is the part that bites.** `Form.ActiveControl`
answers with the CONTAINER — the `SplitContainer` — so a ring asking "is this
focused?" recognised the buttons and neither list. Descending through each
container's own `ActiveControl` gets one level further and stops dead at the
`SplitterPanel`, because `Panel` does not implement `IContainerControl`. **It
asks Windows now** (`GetFocus`), with the managed descent as a fallback. Verified
by driving real Tab and Shift+Tab keystrokes through two full laps in both looks.

**The menu is called Sort, and the shelf opens the way it was left.** Both
choices live in `[Library] SortKey` / `SortAscending`, are written the moment
they change, and are read *before* `BuildUI` so the menu opens with the right two
ticks instead of showing the default and correcting itself. Verified across a
real close and reopen.

**The Sort menu asks two questions, not one** (Gordan, 2026-08-03). It used to
offer six combined entries — alphabetical ascending, alphabetical descending, and
so on — so every new sort key cost *two* lines, and four keys would have been
eight. Now: **Alphabetically / Date added / Format / Status**, a separator, then
**Ascending / Descending**, with **two ticks showing at once**. `sortKey` and
`sortAscending` are separate, the direction is applied by flipping the sign at
the end, and the **title tie-break stays ascending whichever way the main key
runs** — inside one format or one status a reader is looking a title up, not
admiring the order. **Status is the reading lifecycle**: unread, being read,
read. Not "now reading", which is a place of its own above the shelf, and not
favourite, which is a mark worn *on top of* a status rather than one of them.

The `(active)` suffix stays beside the tick: screen readers do not reliably
announce the check state of a `MenuStrip` item, and text always gets read. Two
groups means it now appears twice, which is exactly the state of things.

**Help is in the menu bar**, with `Help` (F1) and `About NBR`. Both are
deliberately **unwired**: the manual does not exist yet and neither does the
About box. F1 is claimed all the same, so nothing else can take it — the key must
mean Help here exactly as it does in the player, one function with two ways in.

**Load and Close, not OK and Cancel**, in both looks now — the new look had
already renamed them and the classic had not. Their accessible names are the
captions themselves: "Load the selected book" and "Close the library" said
nothing the word did not, on every pass through the button row. Same for
Refresh.

`LibraryForm.cs`. Book shelf is a single-column **ListView (Details view), one
flat sorted list — no group headers** (the earlier Now Reading / Reading /
Unread / Read native groups were removed by request). Each row instead carries
its status two ways: a **spoken text flag** appended to the item name (", Now
reading" / ", Reading" / ", Read" / ", Unread", then ", Favorite") so screen
readers announce it, and a **colored badge icon** (`SmallImageList` dots —
red = unread, yellow = reading, green = read, blue = now reading; drawn at
runtime by `MakeStatusDot`).

**Favorite is NOT in the text at all — it is a heart on the badge** (Gordan's
idea, 2026-07-29). An image is never announced, so a favorite is *seen and not
said*, where ", Favorite" was one more tail to listen past on every favorite row;
and the Favorites filter already answers "which are mine" properly. It could not
be a character in the text either — the item's text **is** what a reader reads,
so a heart there comes out as "black heart suit".

**The badge does not gain a mark — it changes SHAPE.** A favorite is the same
badge in the same colour drawn as a **heart instead of a circle**: the shape
carries "favorite", the colour still carries the status, and neither has to make
room for the other. That was Gordan's second thought and it is much better than
the first attempt, a small white mark tucked into the corner — which swallowed
the dot at 16 px, and at 20 px read as a smudge rather than a heart, because a
shape squeezed beside another never gets the pixels to be recognisable. A shape
filling the badge does. (20 px stayed; a heart needs more room than a circle to
read.) The edge is the same hue darkened, so the outline holds against a selected
row's highlight without introducing a colour that means nothing.

**The flag is APPENDED, never prefixed, and that is not a presentation choice.**
A list view jumps to the next item beginning with the typed letter, so a status
in front makes every row start with R, U or N and **first-letter navigation
dies** — which is what happened when these flags replaced the old groups, and
what Gordan noticed (2026-07-29). On a shelf of thousands that aid is worth far
more than hearing "unread" a second earlier, and nothing is lost by moving it:
the status is still spoken, and the badge still carries it at a glance. Symbols
in place of the words do not help either — a `ListView` item's accessible name
**is** its text, so replacing it would need a custom control with a hand-written
accessibility object, and a symbol at the front blocks the letter jump exactly as
a word does.

The **Now-reading** book (last-opened while still
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

**Later, and NOT for alpha — book descriptions from the internet** (Gordan,
2026-07-29). A "fancy feature" on the Lite backlog, deliberately parked: it needs
no translation engine, so it does not belong in Pro.

- **Goodreads is out** — its public API was shut down at the end of 2020. The
  realistic sources are **Google Books** (best coverage, no key needed for basic
  queries) and **Open Library** (open data, no key).
- **The API is not the problem; matching is.** Folder-derived titles like
  `Silvia_Urich_El_chico_de_la_mascara_de` will mis-match, and a wrong blurb is
  worse than none — the reader has no way to notice. **ISBN is the way out where
  it exists**: EPUB carries it as `dc:identifier` (NBR does not read it yet), and
  an exact key needs no candidates and no confirmation.
- **Look in the FILE first.** EPUB and DAISY carry `dc:description`. But do not
  trust it: official editions put the first chapter of the next book, or a list of
  the author's other titles, in that field. It is a **candidate like any other**
  and goes through the same confirmation.
- **Confirmation is where the title gets fixed**, rather than asking the reader to
  tidy names beforehand: no candidate fits → correct the title there → search
  again, and the name stays corrected for the library.
- **Off by default, opted into.** Sending titles to a third party discloses what
  someone is reading.
- **Fetch once, cache in `Book.ini`.** No repeat requests, works offline after.
- **Language:** ask Google Books to restrict to the language before thinking about
  translating anything. Chaining Player → Books → Translate → Player couples the
  app to two outside services for one paragraph — Gordan's call, and the right
  one. Lite shows the description in whatever language the source has it.
- **Where it goes:** not the player's info column (about three spare lines there,
  measured — a description is a paragraph). The Library's details pane has room.
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

**The reading page, gone over on the rendered dialog (2026-07-29).** Four things
the first screenshot showed, all fixed and re-checked on the running dialog:

- **The help key opened the dialog.** `BringToFront` is what makes the `?` draw
  above the sticker, but it also makes it the first CHILD — and **WinForms breaks
  a TabIndex tie by child order**, so a `TabIndex` of 0 put it ahead of the
  settings it explains. The reader announced "Help for Speech, button" on open and
  Tab hit a `?` on the way into every group. It now carries a high TabIndex, which
  keeps the paint and gives the order back. This was wrong on the **audio** page
  too — both share `HintSystem.Attach`.
- **One value column for the page, not one per group.** Measured per group it
  landed in three places (Speech pushed right by "Reading speed (words per
  minute):", Braille barely past "Braille table:"), which read as a ragged edge
  down the column. `UnstickLabels` split into `LabelColumn` (measure every group
  first) and `PlaceValues` (then move).
- **The page fills its own width.** Values stopped a third short of the right
  edge; a combo now takes the rest of its row. **A spin box deliberately does
  not** — a three-digit number does not need 340 units, and stretching one moves
  its arrows away from its digits.
- **Slack goes to the gaps, not into the boxes.** Growing a box does not move
  anything inside it, so it only bought a band of dead glass under the last row of
  each group (~37 units under Pitch). Each box is now snug around its subject.

**Accepted, not fixed:** the info glass keeps a system-coloured scroll bar on the
dark glass, and it shows whether or not the text overflows. Changing either needs
an owner-drawn control, and the bar has to stay reachable for a long title.

**Not done yet:** the tabs above (needs a commit-without-close path, which is
new behaviour, not paint); tightening the innards now that the cells grew from
112 to 138.

**The hybrid two-tab page is BUILT and rendered (2026-07-30).** Both pages lay
themselves out **inside** their tab page, unlike the two single-page paths which
move their controls onto the form and hide the strip — here the strip has
something to show. Each page gets its **own `DialogCanvas`**, because a TabPage
paints its own background over whatever is behind it, so the metal has to be
drawn on the page; the canvas still takes the form as owner, so dragging the
window by the metal keeps working. Which page is which is decided by **what is on
it** (`p.TextInfo`), not by index.

**The reading tab is not decoration on a book that plays itself** (Gordan): even
where the narration sets the pace, the voice, pitch and volume decide how a word
looked up on demand is spoken, and braille and on-screen output are switched on
there. **The condition is `IsHybrid`** — `IsTextBook && Chapters > 0` still
cannot both be true and never will be.

- **`DialogSkin.PropGeom.For(w, contentH, rowsBottom)` computes the layout
  instead of a second set of constants being written down.** The hand-tuned
  numbers turn out to derive from one another exactly, and `For(960, 628, 570)`
  reproduces **every one of them to the unit** — verified by a probe before the
  hybrid page was built on top of it, so the tested audio layout is provably
  untouched. A tab page costs ~32 units of width and ~100 of height, giving
  columns of 292 and stage cells of 117.
- **Rows are as tall as what stands in them.** Five of the six audio cells hold
  one combo; Tone holds three spin rows. One height for all six clipped Treble
  off the bottom of the page. Each cell is asked what it needs by measuring its
  own children — the 112 they were all *built* at is the same number for every
  cell and so cannot tell them apart.
- **Do NOT test `c.Visible` when measuring inside `SuspendLayout`.** On a page
  that is not the selected one every child answers false, every row measures
  empty, and the six cells collapse to a stack of title bars. Measured, seen,
  fixed.
- **A check box built at a hand-picked width cuts its own caption off** — "…show
  the text on screen while readi". It was not overflowing anything, so widening
  is the fix, not wrapping. **This was wrong on the text-only page too, all
  along**, and is fixed there in the same change.
- **`DialogSkin.StyleTabStrip` is now shared with `SettingsSkin`** rather than
  each owning a copy of the owner-drawn strip.

**Rendered and checked on all three kinds** (a hybrid, an audio-only and a
text-only book): the hybrid shows both tabs with no scroll bars and every group's
bottom edge intact, and the two single-tab layouts are unchanged.

**Method note, because synthetic input failed again:** keys stopped reaching the
running player entirely this session (no focus groove moved, `Alt+Enter` and Tab
both dead) — so the dialog was built and shown by a small harness instead, which
is a better test anyway. Two traps in doing that: `DrawToBitmap` comes back as
**bare metal with every control missing**, because a TabControl does not honour
`WM_PRINT` for its pages; and the capture must be taken from a **DPI-aware**
process, since the app is DPI-unaware and every coordinate it can see — including
`GetWindowRect` — is virtualized to 1280×720 while the screen is 1920×1080.

**Hybrid books exist as of 2026-07-30.**
The note that stood here said hybrid Properties was not the blocker because
hybrids were impossible by construction, and that was right at the time: two tabs
needed `IsTextBook` **and** `Chapters.Count > 0`, while `DetectTextBook` only
called a book a text book when its folder had **no** audio. §8c has since built
the text+audio DAISY join, and a hybrid is now its **own** thing
(`BookData.IsHybrid`) rather than a text book with audio — measured across 22
real samples, 21 import and cold-reload as hybrids.

**So the condition to branch on is `IsHybrid`, NOT `IsTextBook && Chapters > 0`**
— that pair still cannot both be true and never will be, by design.

What was already true and still is: `PropertiesSkin.Apply` bails on
`TabPages.Count != 1`, so the first hybrid opened gets the plain Windows dialog
until the page is built. And it is not a repaint job — both existing paths
**move** their controls off the tab page onto the form and hide the strip, so a
hybrid needs versions that lay out *inside* a page.

---

## 10c. Choosing a voice — BUILT 2026-07-29

**Language → Voice. There is no middle step.** The platform step the first
design called for was dropped by Gordan once it was looked at properly: it
grouped voices by what they *report as their vendor*, which is not a question a
reader has an opinion about, and `CompositeSpeechBackend` already merges the
backends and lets the 64-bit copy win a duplicate name — so there was nothing
left for the step to disambiguate. Two combos instead of three is also one Tab
stop fewer, every visit. (Bitness was never a user concept; if the *same voice*
ever turns up on two platforms, that is the one case a label is needed, and the
merge already prevents it.)

**Per-language default voices are the point of the whole thing, and they are in.**
Settings → Text Books is now two combos: **"Books in this language"** (first row
= *All other languages*, i.e. the global default) and **Voice**. Choices are
staged, so several languages can be set up in one visit and Cancel discards them
all; on save the global default keeps **its own** voice's speed/volume/pitch
rather than whichever row happened to be on screen. Stored in `Settings.ini`
`[LanguageVoices]` (`Languages=sr` + `sr=Dragana`), keyed by primary code because
that is a safe INI key where a voice name is not.

### A LANGUAGE IS ITSELF. Read this before touching any of it.

**Croatian is Croatian, Serbian is Serbian, Czech is Czech** (Gordan,
2026-07-29). A first build offered voices from languages that read a book *well
enough* — Serbian and Croatian standing in for each other, Czech and Slovak for
both — on the strength of Gordan's own listening. He then **rejected the whole
idea**, and the reasoning is worth keeping because the feature looks helpful
right up until you use it: NBR does not get to decide on the reader's behalf
that a near-enough accent will do. If they want a Mandarin voice for a Russian
book, that is theirs to choose **by hand**; it is never something NBR offers by
noticing that two languages share an alphabet.

Two traps if this is ever revisited:

- **`LanguageDetector.SameLanguage` must not be used to pick a voice.** It
  groups BCMS (hr/sr/bs/sh/cnr) and that is the right answer to a *different*
  question — "is this the same language", which is what DETECTION needs. Used
  for voices it silently pulled five Croatian voices into the Serbian list.
  Voice matching is on the **primary code** alone.
- The stand-in table is **gone, not disabled.** Don't reintroduce it as a
  "smart default".

**The whole rule, in `VoiceChooser` and nowhere else:** the voice chosen for
that language in Settings → else the first installed voice that **speaks** it →
else **nothing**. It never falls through to the first voice on the machine: a
voice that cannot speak the language does not read the book badly, it reads it
as noise. The empty answer comes back as `VoiceSource.NoVoice`, which is a
result the caller must handle, not an error. The player and Properties each used
to hold their own shortened copy of this rule and had already begun to differ.

**When a language has no voice at all** — caught in the wild on a Spanish EPUB
(Gordan, 2026-07-29), which opened in Croatian on Karmela and started speaking
the moment it was activated from the Library. Two separate faults, and the
second is the one to remember:

- Properties fell back to the **first row of the language list** when the book's
  language was not in it. Sorted by name, "first" meant Croatian. A guard that
  looks harmless is exactly how the forbidden behaviour gets back in. Both boxes
  now stay **empty**.
- **An empty voice name does not clear a voice — it leaves whatever spoke last.**
  The player passed `VoiceChooser`'s empty answer straight to the reader and got
  the previous book's voice. `ApplyTtsSettings` now recognises
  `VoiceSource.NoVoice` and **touches nothing**; `LoadTextBookPlayback` refuses to
  autoplay and says why; Space says it again rather than starting.

The notice is written **twice**: one line between the controls (`Prop.Text.
NoVoiceShort`), the full sentence on the info panel (`Prop.Text.
NoVoiceForLanguage`). **`DialogSkin` derives pad and gap from what is left over**
rather than fixed amounts, because the speech group grows when it carries the
notice and the stack used to run onto the OK button.

### `NoVoiceForm` — the question, put once (Gordan, 2026-07-29)

An announcement was **not enough, and the reason is universal design**: it was
gone the moment it was said, so a reader looking away — or one who cannot hear it
— got no message at all, and a silent player with nothing on screen to explain
it. This is a **state that has to be acknowledged**, not news to catch in
passing, so it is a dialog that also offers the way out. (Contrast volume 80→65,
which genuinely is transient; that belongs on the future transient glass readout,
a separate job.)

Gordan's rules, exactly:

- **Asked on EVERY activation**, exactly as if it were the first. Coming back to
  a title left unread is another attempt to read it, not a repeat of a decision.
  This needs **no flag**: a book with no voice never becomes the last-opened one,
  so NBR never resumes one by itself, and every load of one is therefore
  deliberate (double-click, Enter, the button, Ctrl+O).
- **Declining does not load the book at all.** The question comes *before*
  anything is swapped (`EnsureVoiceForBook`, at the top of `LoadBook`), so
  whatever was loaded stays as it was and the title stays on the shelf. It used
  to be asked after the load, which left a book nobody could read in the player —
  the opposite of "not loaded". Answered **off the shelf**, without reading the
  book: the language is already in `Book.ini`.
- **Declining from the Library returns you to the SHELF**, not the player —
  nothing was loaded, so there is nothing to be left looking at.
  `BtnLibrary_Click` loops for it.
- **The choice is for THIS BOOK.** There is deliberately **no** "use for every
  book in this language": a French book read by a Romanian voice is fair once and
  poor as a rule, and *a rule nobody knowingly set is a rule they do not know they
  have*. Making it a rule is Settings' job and **should take some effort**.
- **A book with no voice is not "now reading".** It is not recorded as last
  opened (so NBR does not resume it) and stays **unread** in the Library until
  something is settled. Choosing a voice is what promotes it — which is why
  `SetLastOpenedBook` lives in `AskForVoice`, reached by both routes.

**The state, as opposed to the moment, lives in the player's info line.** It read
"Speech engine:" over what was always a voice name; it is `Player.Info.VoiceLabel`
now, and while the book has no voice it carries `Player.Info.NoVoiceLabel` plus
the language instead. That line would otherwise name whatever spoke last, which
is the hardest lie on the page.

**The Library staying visible behind the dialog is INTENDED — do not "fix" it**
(Gordan, 2026-07-29). Activating a book from the Library leaves that window on
screen underneath, because the flow is *Library → activate → problem → decide*,
and the decision leads either to everything closing and playback starting, or to
being back on the shelf. A dialog that had wiped the Library away first would
leave the second outcome nowhere to return to. (Mechanically the Library is
closed but not yet disposed, since `BtnLibrary_Click`'s `using` cannot run while
the dialog blocks further down the same stack — but the effect is the wanted one.)

**Related, and already implied by this section's 960-wide shell:** the Library is
to be resized so that while open it **completely covers the player**, as
Properties does. Not done.

**Still wearing plain Windows chrome:** `NoVoiceForm`, `SpeechDictionaryForm`,
`DictRuleForm`, `TextHelpForm`, the rename prompt. `DialogSkin` now covers
Properties, Settings, the Library, the four working dialogs and every message.

### Three shells, one per KIND of dialog (Gordan, 2026-07-29)

The windows were being reskinned one at a time until Gordan pointed out they are
not N windows but **three kinds**, and that the hint pop-ups and the message
boxes are each *one design with the text swapped*. That reframing is what made
the remaining work small — and it dissolved the objection to replacing
`MessageBox`, which was "21 replacements each having to re-earn what the system
dialog gives free". One shell earns it **once**.

| shell | size | who |
|---|---|---|
| `MessageForm` | grows with its text | every hint pop-up, all 21 former `MessageBox.Show` |
| `WorkDialogSkin` large | 580 × 600 | Go To, Manage Bookmarks |
| `WorkDialogSkin` small | 420 × 360 | Sleep Timer, archive password |
| `DialogSkin` | 960 × 640 | Properties, Settings, Library |

**The message shell grows DIAGONALLY, then only taller** (Gordan's call). Width
and height scale together up to a width capped at a comfortable reading line;
past that only the height grows. Most confirmations never reach the cap, so they
all come out the same size — it is only the rare long message that goes tall
rather than wide, which is also the right way round for reading.

**Under the classic theme every message falls straight back to a real
`MessageBox`.** The well-tested classic path, and every screen reader's built-in
handling of a genuine system dialog, is untouched; only the new look gets the
skinned version. **Default buttons were preserved per call site, not made
uniform:** clearing the library already defaulted Enter to *No* and still does
(`defaultToNo`), while the single-book delete still defaults to *Yes* — a
pre-existing inconsistency left alone rather than fixed as a drive-by. Escape is
always No.

**Two size families, not one and not four.** Lists need height, a handful of
radio buttons does not. One size would leave the short dialogs rattling; four
would be four things to learn. **The large family anchors bottom-right of the
player, the small family bottom-left** — same bottom edge, so it stays one
convention, but each family holds its own zone. `DialogSkin.AnchorToOwner`
clamps to the working area, and that already matters: at 600 tall the large
family cannot fit above the player's bottom edge on this screen, so it rides the
screen top rather than walking off it.

**`WidenLabels` exists because a caption sized by hand for a narrower dialog
just gets cut off** — the sleep timer's third option was reading "…close the
program and shut". It widens only radios and checks that own their row, so the
Custom radio sitting beside its spin box is left where it is.

### The Library under the new look — `LibrarySkin.cs` (2026-07-29)

Same 960 × 640 casing. The grid Gordan set: **menu bar full width across the
top**, then three columns with **A and B joined and C on its own** — search
across AB with the filter over C, the shelf across AB with the info box under C.
A column therefore means the same thing all the way down. **Refresh** on the
metal at the left, **Load** and **Close** at the right; the names are new keys,
so the classic path still says OK and Cancel ("OK" says nothing about a shelf).

**The shelf's right-click menu is a real Windows menu now.** Gordan's report:
both readers announced it as a **drop-down list** with nothing selected until he
arrowed onto something. A `ContextMenuStrip` **is not a menu** — it is a
`ToolStrip` .NET paints itself, and it is exposed as what it is. A `ContextMenu`
builds a real HMENU: announced as a menu, first item highlighted the moment it
opens, behaving like every other Windows menu because it is one. It is *shown*
rather than attached, so the right-click is caught in **MouseUp** (after the
click that selects the row), `Popup` cannot cancel the way `Opening` could so
the empty shelf is caught at the call site, and the keyboard route opens it **at
the selected row**. The only loss is the shortcut column, which no reader ever
read.

**The menu BAR could not follow, and this was measured.** A real Win32 menu bar
(`Form.Menu`) *does* draw on a borderless form — but Windows draws it in the
window's own top strip, **outside the rounded casing**, in system colours, and
it takes 15 units of client height with it, shoving the whole skin down. A menu
bar lives in the non-client area by definition and this window has none. **A
popup has no such problem**, which is why the popup could change and the bar
could not. The bar stays a `MenuStrip` and is **repainted** instead
(`SkinMenuRenderer`), highlight carrying an **outline as well as a colour** —
two darks a colour apart are not a difference everyone can see.

**Two ToolStrip facts worth keeping:** setting `BackColor` does nothing and
neither does `ToolStripRenderMode.System` — a ToolStrip paints its own
background over both, so colours must come from a renderer. And **`AutoSize`
wins over `Size`**: without turning it off the bar shrinks to the width of
"File View" instead of running end to end.

**Also learned here:** `BringToFront` is not optional when re-parenting onto a
skinned form — `Controls.Add` appends, and in WinForms the **end** of the
collection is the **back** of the z-order, so the three buttons were painting
underneath the metal. And a two-column list's widths must be **passed in**, less
the panel padding and the scroll bar, or it grows a horizontal bar under itself.

### Settings under the new look — `SettingsSkin.cs` (2026-07-29)

Same shell as Properties: **960 × 640**, borderless casing, silver rim, dark
glass, groups as stickers, **OK / Cancel / Apply** on the metal (Properties has
no Apply; Settings saves itself, so it does).

- **The tab strip is owner-drawn; the `TabControl` is real.** A drawn strip
  would lose the tab role, the arrow navigation and the "page 2 of 5" a reader
  announces. Selected tab **lit**, the rest **silkscreened** — the display
  glass's two levels, so the current page reads without colour carrying it alone.
- **The hint boxes and their switch are gone** (Gordan). A hint under every
  control cost a third of each page to say things a reader wants once; the `?`
  per group with `F1` says it on demand and costs a corner. Reclaiming that
  space is most of why the pages fit. **Where no help text was written, no `?`
  appears** — an unwritten key renders as the key, and "Hint.Settings.General.0"
  is worse than no button.
- **No info column** (Settings has no book to describe), which is the width that
  fixed Text Books: it did not fit 640 in one column and used to scroll. Groups
  fall to **two columns** when one will not fit, balanced by height and keeping
  the order they are read in.

**Three things only the rendered dialog showed, all worth keeping:**

1. **Page size must come from the `TabControl`, never off the `TabPage`.** Inside
   `SuspendLayout` the pages have not been resized yet, so `ClientSize` still
   answers with what they were *built* at — every group came out half a page wide
   with its values clipped off the right edge.
2. **The value column is worked out PER COLUMN.** One shared across both let the
   widest label on the page shove the other column's values into its own edge.
3. **A button row built for 500 does not fit a 444-wide column** — "Speech
   dictionary…" was cut off. They move **together**, by the worst overflow among
   them, because nudging only the offender pushes it into its neighbour.
   `HintSystem.IsHelpKey` now identifies its own `?` buttons so those stay pinned.

**Not done:** per-tab tightening. General, Device and Misc carry loose controls
rather than groups, so they are recoloured where they stand and sit in the left
third of a much wider page.

**Settings and Properties are the same two combos.** Settings sets the global
rule (*this language → this voice*), Properties overrides it **for one book** —
"the main character is a man in the first person, so not Karmela for this one".
Settings has no book, so it has no detection; Properties has both.

**Settings lists every language the LIBRARY holds a book in, as well as every
language something speaks** (Gordan, 2026-07-29). Voices alone were never the
right set: a French book with no French voice is exactly the case a rule is
wanted for, and it could not be set while French was absent — which made "go to
Settings and sort it out there" a dead end. A language joins the list the moment
a book in it is read off the shelf or imported, via a **static hook on
`AppSettings`** that `BookData` calls on both load and save; the Library scan
builds a `BookData` per book, so **opening the shelf once registers an existing
library for free**. Persisted as `[Languages] Seen`.
**Rows with no voice say so**, because "French" and "Croatian" otherwise look
identical in one list and behave nothing alike.

**A language nothing speaks offers EVERY installed voice.** This is the part
that makes the rest work, and it is *not* the substitution NBR refuses to make:
nothing is suggested, nothing is ranked by how close it sounds, and the reader
came to Settings on purpose. It is the one place a deliberate cross-language rule
can be written. **Properties and `NoVoiceForm` keep the narrow list** — there you
are choosing what reads this one book, so only languages with a voice make sense.

Settings' list carries one extra first row, *All other languages* = the global
default, which is what a book whose language could **not** be worked out is read
with.

**Considered and parked: the full Windows language list** (~350, installed ones
at the top, a separator, the rest below). The separator is what kills it —
WinForms combo boxes have no such thing, so it would be a fake row a screen
reader announces like any other and the user can select; making it behave needs
owner-drawing and skip logic, i.e. the construction most hostile to readers,
introduced for the eye's benefit. "Unsupported" would also be the wrong word:
those languages are not unsupported, they just have no voice installed. Revisit
only if setting a rule for a language you own no book in turns out to be wanted.

**"Set as default" in Properties is still not built** — the storage it would
write to (`AppSettings.SetLanguageVoice`) is. It promotes this book's voice to
the default for its language, and must **ask first**, because it changes a rule
affecting every future book in that language and nothing else in Properties
does that.

**Both things the first text-page screenshot exposed are now fixed.** The colon:
the reading page adds `": "` to Title / Author / Format the way the audio page
does — the `Details.Field.*` captions carry none of their own because the Library
uses them as column headings, and it is not cosmetic, since the player's glass
renderer splits a line on `": "` to tell the silkscreened label from the lit
value. And the apparent disagreement — "Language: Serbian" over a picker showing
Croatian — was **two different facts wearing one label**. They are now
**"Detected language:"** and **"Reading in:"**, so a book read by another
language's voice says so instead of looking like a bug.

**Still to decide before code:** what happens when the detected language has no
default. The chain is language default → global default → nothing. **Do not fall
through to "first available voice"** — a voice that cannot speak the language
reads the book as gibberish, and a silent wrong choice is worse than an empty box
and a message.

---

## 10d. One order for a book's information (Gordan, 2026-07-30)

Gordan's report: the info boxes did not agree with each other. Measured across
the three, they did not — **Format was third** in Properties, **ninth** in the
Library's audio details and **fifth** in its text details; **Publisher and
Producer** sat third and fourth in the Library and were **absent from Properties
altogether**; Pages came after Format in one place and after Headings in another.
Each box had grown its own order by being written on a different day. Two pairs
of keys even said the same thing under different names — `Duration`/`Time` and
`Listened`/`Elapsed` — which is how the drift got started.

**The fix is not to re-sort three lists but to remove the opportunity.**
`BookInfo.cs` owns the order; a caller says *which* fields it has and
`BookInfoBuilder` decides *where* they go, so the boxes cannot drift again even
when someone adds a field to only one of them. The Library now has a **single**
`AddDetailRow` call site.

**The order follows the player's glass** (§8k), which was settled first and does
not change:

| band | fields |
|---|---|
| identity | Title · Author |
| where you are | Time · Elapsed · Remaining · Read |
| where it came from, what it is made of | Publisher · Producer · Format · Pages · Headings · Characters · Language |
| how it is read | Speed · Sound processing |
| the library entry, not the book | Added |

The player's live-only slots (chapter, page, bookmarks) have no equivalent in a
dialog and simply do not appear. **A field with nothing to say is not shown** —
the player's rule, and it matters more to a screen reader than to the eye: a
value is always in the same place, so it is found by counting rather than by
reading everything above it. A box that would rather keep a row and show a dash
says so at the call site (`AddAlways`), which is presentation, not order.

Two things fell out of doing it. **Properties now shows Publisher and Producer**,
which it never had. And the Library's label column is sized to **the longest
caption there is**, measured over every field rather than over the rows on
screen — the column is fitted once while the list is still empty, but its rows
change with every book, so a flat 38 % share cut "Sound processing" to "Sound
proc…". **Still true and accepted:** the *value* column truncates a long title or
format string, because a `ListView` cell cannot wrap; Properties shows those in
full.

### Control captions are short; the explanation is in the hint

**A control's caption is at most about 30 characters** (Gordan, 2026-07-30, after
seeing "Use visual output (show the text on screen while readi" cut off). A
sentence on a control is a hint that landed in the wrong place — the `?` and `F1`
are where an explanation belongs, and they cost a corner instead of a third of
the page (§ SettingsSkin).

**The long form is deliberately NOT moved into `AccessibleName`.** That would
sound the explanation on every Tab past the control, which is exactly the noise
the hint system was built to remove. Accessible names stay short too — they carry
the *shortcut*, not the manual.

Trimmed: `Settings.TextBooks.UseVisual` and `.UseBraille` (57 → 17/18),
`Settings.Audio.UseMetadata` (57 → 21), `SleepTimer.Action.StopClose` and
`.StopShutdown` (35/59 → 23/37 — the group legend "When the time expires" already
supplies the rest, but "the computer" stays, because "shut down" alone could mean
the app), and `Settings.TextBooks.Speed` (33 → 14): "Reading speed (words per
minute):" became "Reading speed:", the unit moving to the value ("175 WPM"),
which also un-did a redundancy in the info glass. **That one paid twice** — §10b
recorded it as the caption pushing the whole reading page's value column right,
and the values moved back left when it went.

**Left long on purpose:** messages and notices (they are prose), combo-box items
(values, not captions), the folder-browser prompt, and accessible names.

---

## 10e′. The audio-only libmpv is in: 93.6 MB → 30.2 MB (2026-08-02)

Built from our own fork, `gradic76/mpv-winbuild`, carrying one patch. **Seven CI
runs**, and the failures are the lesson:

- an option written from memory that mpv does not have (`-Dsdl2`; it has
  `sdl2-audio`, `sdl2-video`, `sdl2-gamepad`) — an hour to find out;
- the same option passed twice, because `mpv.cmake` expands a variable that
  already carries it as a PAIR, so a line-matcher never saw it;
- **and the big one: 43 video options left at their default `auto`.** Auto means
  "on if the dependency is present", the build image has them, and mpv compiled
  D3D11 and VAAPI sources into an audio-only build — until every one was named.

The generator now reads mpv's own `meson.options` and refuses to emit a patch
containing a name that does not exist there, or any option passed twice. Both
earlier failures would have been caught in a second rather than an hour. **Never
add a build option without checking it against the project's own option list.**

FFmpeg is cut by CATEGORY, not by what NBR happens to use: every decoder FFmpeg
marks audio, every filter it marks `A->A`, derived mechanically from
`codec_desc.c` — never from `LibraryScanner.AudioExtensions`. A hand-picked list
would silently drop the one codec a reader's book needs, and §8d could not reach
for a filter without a rebuild.

**Verified on the shipped file**, not on the artifact: GPL markers clean; 214 of
214 audio decoders present and no video or image decoder at all, compared name by
name; 14 of 14 sample books read for duration, decoded, and the whole §8d chain
accepted through the real C API. Names needed normalising to compare — mpv
reports libavcodec driver names (`8svx_exp`, `atrac3plus`, `real_144`, `g722`)
where the enable-list carries FFmpeg's configure names (`eightsvx_exp`,
`atrac3p`, `ra_144`, `adpcm_g722`).

**Not yet checked by ear.** Rollback is one file: the 93.6 MB build is in git.

---

## 10e. libmpv is an LGPL build now — keep it that way (2026-07-30)

**The vendored libmpv was a GPL build, and that was a real problem for a closed
application** — Store or plain installer, it makes no difference. Found by
scanning the binary rather than by reading a label: it carried libx264's own
banner (`x264 - core`, `videolan.org/x264`, the `libx264 — H.264 / AVC` encoder
registration) and mpv's DVD code with libdvdnav linked (`libdvdnav: %s`,
`dvdnav error: %s`, the `dvd://` protocol). **libx264 and libdvdnav/libdvdread
are GPL-2**, and FFmpeg with x264 requires `--enable-gpl`.

**Replaced with `mpv-dev-lgpl-x86_64` from
[zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild)** (mpv
`-Dgpl=false`). shinchiro, the usual source of Windows builds, has no LGPL
variant — the request has been open since January 2024. **93.6 MB against the
old 114.8 MB**, and mpv's own `Copyright` file confirms the diagnosis from the
other side: DVD navigation and CDDA are GPL-only and are dropped automatically
in LGPL mode, which is exactly what the scan found in the old DLL.

**Verified, not assumed, and both ways round.** The new DLL: no libx264
(`x264 - core` survives twice, but in FFmpeg's H.264 **SEI parser**, next to
"Mastering Display Metadata" — it reads the tag an encoder left in the
bitstream; `libx264` and `x264_encoder_open` are absent), no libdvdnav /
libdvdread / `dvd://` / `cdda://` / libx265 / librubberband / libsmbclient. Every
filter in the §8d chain, `scaletempo2`, WASAPI and the codecs are all still
there. Then the whole §8d chain was **handed to mpv through the real C API** with
every stage switched on at once, over the eleven `misc audio` samples: accepted
11/11, and duration + decode identical to the old DLL at 10/11 — the one failure
being the APE with a non-standard ID3v2 tag prepended that §8h already documents.

**A trap that cost a false result, worth remembering:** `Marshal.
StringToHGlobalAnsi` converts to the system code page, but **mpv takes UTF-8,
always**. Three samples with `Č` and `Đ` in their names failed to load and looked
exactly like unsupported formats. What exposed it was running the OLD DLL as a
control and seeing it fail on the same three files and no others — a difference
you cannot see without a control run.

**The rule from here on: any libmpv update must be the LGPL artifact, and must be
scanned before it is committed.** The probe is trivial to redo — read the DLL as
ASCII and count `x264 - core`, `videolan.org/x264`, `libx264`, `libdvdnav`,
`dvd://`, `cdda://`.

**Audio-only build — prepared, not yet built (`tools/mpv-build/`).** Gordan's
call (2026-07-30): cut to audio, **no image or video decoding at all** — the
Library shows format icons, not cover art, so an M4B's cover (a real MP4 video
track, §8f) is deliberately given up.

**The rule he set, and it shapes everything: keep every audio format and filter
available, do not narrow it to what NBR currently uses.** A hand-picked list can
silently drop the one codec a reader's book needs, and it surfaces on their
machine rather than ours. So the list is **derived mechanically from FFmpeg's own
classification**: `libavcodec/codec_desc.c` assigns each codec an
`AVMEDIA_TYPE`, 223 are `AUDIO`, and the DLL's own `decoder-list` turns those
into **214 decoders to enable**. Measured on the current DLL: **510 decoders, 296
of them video or image.**

Two traps found while preparing it, both recorded in the folder's README:
`allcodecs.c`'s `/* audio codecs */` marker is **not** usable as a classifier —
its "video" block is really an alphabetical list, and `rka` and `dvaudio` are
audio codecs sitting inside it. And **mpv's driver spelling is not FFmpeg's
symbol name** in eleven cases (`8svx_exp` → `eightsvx_exp`, `g722` →
`adpcm_g722`, `real_144` → `ra_144`, …); every final name is checked to exist as
`ff_<name>_decoder` before it goes in the configure line.

**Two dependencies cannot be removed**, confirmed in mpv's `meson.build`:
`libass` (`dependency('libass', version: '>= 0.12.2')` — no `required:`, no
feature option) and `libplacebo` (required since
[f5ca11e](https://github.com/mpv-player/mpv/commit/f5ca11e12bc5)). They and
freetype/harfbuzz stay, so the estimate is **~28–32 MB, not the ~20 MB it first
looked like** — worth saying because the installer maths was based on the smaller
number. libplacebo can at least be built with Vulkan, OpenGL, D3D11, shaderc and
glslang all off, which is where the big saving is.

**Not done:** the build itself. It means forking the winbuild CI (a local mingw
toolchain is a much larger lift for the same artifact), and `gh` is not installed
on this machine. `tools/mpv-build/` holds the enable-list, the decoder oracle the
new build must match, and both verification harnesses.

**The visual reading display must NOT go through mpv** (researched 2026-07-30),
which is what makes an audio-only build free. mpv's OSD renders **pixels**, and
§8l's braille route works precisely because the text sits in a real focusable
control that the screen reader tracks — measured at 67–170 ms, silent, with
routing keys landing as caret positions. Through mpv, braille and visual would
split back into two features. Two more reasons point the same way: an OSD needs a
video output window, undoing the `vid=no` that keeps cover art from popping one;
and libass has its own font handling and ignores the Windows high-contrast
setting that §8k says outranks everything. A borderless custom-painted WinForms
overlay gives the fonts, colours and highlight Properties already offers, and NBR
already animates at that level.

**Licences (all verified on disk 2026-07-30, recorded in
`THIRD-PARTY-NOTICES.txt`, which ships with the app):** SharpCompress MIT,
System.Text.Encoding.CodePages MIT (both read from their nuspec), liblouis and
its tables LGPL 2.1+ (read from a table header), nvdaControllerClient LGPL 2.1
(text present), TagLib# LGPL 2.1, libmpv LGPL 2.1+ with FFmpeg stated as LGPL v3.
**Not done:** the verbatim licence texts must ship alongside the notices —
naming a licence is not providing it.

**One wrinkle for MSIX specifically:** FFmpeg here is LGPL **v3**, which wants
the user to be able to relink or replace the library. A signed MSIX package puts
the DLL inside `WindowsApps` where it cannot be replaced. Worth checking whether
the build uses `--enable-version3` and what needs it, since FFmpeg is LGPL 2.1+
by default. NBR decodes AMR natively, so the usual reason for version3
(libopencore-amr, encoding only) probably does not apply.

---

## 10f. The sound card can eat the start of every sentence (2026-08-01)

**Gordan's finding, after most of a day chasing it in software.** Reading was
losing the first word of sentence after sentence. It was not NBR. His HDMI
output — an Ace Magic mini PC feeding a TV — powers down almost the instant the
signal stops, and it had been kept awake all along by NVDA and JAWS, which have
their own settings for exactly this and were on the same output. The moment the
screen readers were moved to a Creative card (to tell the two voices apart while
testing the reading window), nothing was holding HDMI open any more, and the
endpoint slept in the gap between utterances. Moving a reader back onto HDMI
fixed it instantly.

**Why no amount of measuring found it, and what that cost.** Every log was
clean, and correctly so: the utterance was handed to SAPI, SAPI reported
speaking, and it played for its full length — 7 266 ms against 7 201 ms of
audio, sentence after sentence. Nothing purged, nothing cancelled, the sentences
tiled the book contiguously, and the rendered WAV even carried 96 ms of its own
lead-in. **The loss happened past the last point software can see.** Chasing it
produced four wrong diagnoses in a row (disk writes per sentence, UIA
notifications, thread affinity, a look-ahead race) before Gordan recognised what
had changed in his own setup.

Two lessons worth keeping:

- **When every measurement says the software is correct and the user can hear
  that it is not, stop tightening the software.** Ask what changed outside it.
  The question "what did you change about your machine?" was worth more than any
  of the instrumentation.
- **Bisecting on the first report, not the fifth**, would have shown within one
  round that a build from three days earlier behaved identically — and therefore
  that nothing we had written was responsible. It was Gordan who suggested it.

**What NBR must do about it.** A player cannot depend on a screen reader to keep
its output device awake — a sighted user has no screen reader, and a braille
reader may well put speech on another card, which is precisely the arrangement
that exposed this. NBR needs its own keep-alive on whichever device it is using.
See §11.

---

## 10g. Braille files are not one format (2026-08-01, the `!New` samples)

86 files in `Test naslovi\!New`, and they disproved the assumption the parser was
built on — that a `.brf`/`.brl` is braille ASCII and that is that.

**What the extensions actually contain.**

| | files | what it is | now |
|---|---|---|---|
| `.brf` | 58 | braille ASCII, four conventions | read |
| `.i55` | 11 | braille ASCII under another producer's extension | read |
| `.brl` | 5 | **Braillo Text — ordinary text, not braille** | own parser |
| `.dxb` | 10 | Duxbury, binary, `FF D S I` header | refused |
| `.smb` | 2 | embosser stream | refused |
| `.bopf` | 2 | XML package descriptor, not a book | — |

**Braille ASCII comes in at least four conventions**, and files do not say which:
lowercase NABCC; UPPERCASE cells (`17722v01.brf`); a Vietnamese mapping; and
**French, which writes é è à ê ç and the diaeresis as the Latin-1 characters
themselves**. That last one was losing **5.18% of every cell** — 19 068 in one
book — because a byte outside 7-bit ASCII was skipped as "not a cell", silently.
The dot patterns are now read out of `fr-bfu-comp6.utb`, which we already ship,
and the loss falls to 0.09%. **Read the table, never write dot patterns from
memory.** Line width is 31, 39 or 41 depending on producer — nothing may assume
40.

**A percentage test cannot identify a format.** Genuine `.brf` runs from 0.00% to
**13.96%** non-cell bytes; Duxbury starts at **2.95%**. The ranges overlap, so any
threshold that refused Duxbury would refuse real books. Hence two tests: a wide
threshold (20%) that catches Braillo at 90% and `.smb` at 49%, plus **signatures**
for the formats we have identified. Verified on all 86: 58/58 and 11/11 accepted,
17/17 refused.

**Refusing beats guessing.** Braillo through the BRF parser produced *fluent-
looking nonsense* from the tenth of its bytes that happened to map, and a reader
cannot tell that from a badly transcribed book. `BrfParser` returns **null**, not
an empty document: "I cannot read this" and "this book is empty" are different
answers and only one is true.

**Braillo Text, measured (no specification available).** A `Braillo Text` header,
a title, a binary block, then the body as **16-bit units — low byte the character,
high byte an attribute** (`0x03` in running text); CR/LF ends a line; each page
sits in a frame of ordinary ASCII letters 36 columns apart. The body is found by
anchoring on three running-text attributes in a row, not a fixed offset. The code
page is **scored per file**, not assumed — the samples are Cyrillic under 1251,
but that is what these files happened to be. The frame is stripped by LINE, since
it is drawn with letters and cannot be removed character by character without
eating text.

**Duxbury (`.dxb`) turned out to be braille in an envelope**, not a new format:
past the `FF D S I` header and a variable-length block of table names, 97.6% of
the file is printable contracted braille ASCII with markup inline as
`0x1C name 0x1F`. The parser strips the envelope and hands the cells to
`BrfParser` — one translator, not two that can disagree. Dropping a tag without
leaving a space welds two records into one word (`decemberthe`,
`volumesvolume`); the braille either side is fine, only the join is lost.

**`.smb` and `.bopf` are not books.** Both are companions to `.brf` files we
already read (`4137A-7.smb` beside `4137A-7.brf`, `17722.bopf` beside
`17722v01.brf`), so skipping them costs nothing.

**Confirmed by import, 2026-08-02.** Ukrainian Braillo comes out clean; French
keeps its accents (`adapté`, `Médiathèque`, `abrégé`), so the Latin-1 fix works
end to end; and **Duxbury's *Faithful Place* reads the same as the `.doc` set of
the same book** — two independent formats agreeing, which is the strongest check
available and needs nobody to know the language.

### Fine tuning still owed (2026-08-02)

- **The English table is auto-detected wrongly.** `Smith_Chuck…BRF` reads "**Have**
  can a man be Born Again" — `h` alone is the contraction for *have*, so the
  chosen standard is not the one the file was written in (UEB vs EBAE). Same
  cause as `Publishs` for *Publishing*. Formats are right; the table choice is
  not. Per book it can be corrected in Properties.
- French `<auteur>` markup arrives as text (`chauteuroi`), and `Haüy` comes out
  `Haouy`.
- `.i55` decorative rules survive as `\5/∷∷∷∷∷:` — the `{ | } ~` bytes, probably
  8-dot cells.
- Stray bytes not yet mapped: `0x60` in French integral files, `0x7C`/`0xA4` in
  one abridged.
- **More samples wanted** in languages not yet tested — Gordan's own sources are
  exhausted, so free download sites are worth finding when there is time.

---

## 10h. EPUB 3 Media Overlays — built, and what is left (2026-08-02)

A narrated EPUB imports as a **hybrid**, not a document: read as a document it
came in as text and was handed a synthesiser, with the narrator sealed in the
zip. `EpubOverlayImporter` unpacks it and hands the join to `DaisySync`, because
the SMIL is structurally identical to DAISY 3 — `<par>` pairing a `<text src>`
fragment with `<audio clipBegin clipEnd>`. The text side is
`TextParsing.Assemble`, which was always generic HTML.

Three things EPUB does differently, all in the packaging: reading order is the
**OPF spine**, each document names its overlay through a **`media-overlay`**
attribute, and the audio must be **moved to the book folder root** — a chapter is
stored as a bare file name and the player looks for it beside `Book.ini`, so an
EPUB's `EPUB/audio/` subfolder meant a book that imported perfectly and would not
play a note.

**`media:duration` is unusable.** One sample declares `00:00:07.299` for an 80 MB
book, the other `00:00:00`. Durations come from the files (§8c's rule again).

**Verified** on *O Universo Explicado aos meus Netos*: 602 sync points — exactly
the `<par>` count measured before any of it was written — 25 chapters, 10 674 s,
pt-PT, 126 230 characters. It plays, and the text follows the narrator.

### Open

1. **Go To and the seek step offer `aud001`, `aud002`.** Both samples yield
   **zero** `h1…h6`, so there is nothing to place on the timeline —
   `BookData.BuildHybridNavFromText` already turns a heading's character offset
   into seconds through the sync map, and is waiting for headings to exist. The
   source is the **EPUB nav document** (`epub:type="toc"`), which points at
   fragments this code already resolves. **Clear, bounded, and first.**
2. **Granta Portugal's text side is nearly empty** — 21 sync points and 377
   characters against 2.2 MB of SMIL, so its ids do not match what the XHTML
   yields. Audio and duration are correct. Needs investigation, not a fix.
3. **The surface stops refreshing after a large seek** (Gordan: ~15 minutes in).
   Not diagnosed.
4. `Ctrl+C` in the reading surface copies nothing, because the surface never
   SELECTS anything by design (§8l — a selection is read aloud over NBR's own
   voice). Decide whether a bare Copy should take the current paragraph.

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
