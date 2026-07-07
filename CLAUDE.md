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
  e.g. "Back, Z", "Forward, B", "Play, Space or X", "Go To, Ctrl+G",
  "Sleep Timer, Ctrl+T". Tooltips are separate and for sighted/mouse use.
- **Screen-reader announcements** of transient changes (volume, speed, timer
  set/cancelled, info-on-demand) go through **off-screen `Label` controls**
  placed at negative coordinates (e.g. `new Point(-600, -600)`), with
  `TabStop = false`. The helper `AnnounceToScreenReader(label, text)` sets the
  label's text, briefly focuses it, then restores focus after ~150 ms via a
  one-shot timer. This makes the reader speak the text without disturbing the
  real control the user is on.

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
- **LibraryForm.cs** — the Library window (book shelf, search, filter, sort,
  context actions).
- **BookData.cs** — a single book: metadata, progress, and the virtual
  timeline data (`Chapters`, `Offsets`, `TotalDuration`,
  `BuildChaptersFromFolder`, `LoadChapters`, `SaveChapters`). Persists to
  `Book.ini` inside each book folder, including a `[Chapters]` section in the
  form `File0=name.mp3|614.5` (filename|duration-seconds).
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
  Set Bookmark, Manage Bookmarks, Help.

---

## 6. Navigation — four layered levels

Documented in a comment above the seek-step methods in Form1. All four coexist:

1. **Left/Right arrows** — plain 5-second seek, like any player. Intercepted
   even when the seek dropdown has focus (so Left/Right always means seek);
   Up/Down are left to the dropdown while it is focused.
2. **Ctrl+1..9** — percentage jumps to 10%–90% of the whole book's virtual
   duration.
3. **B / Z keys, media Next/Prev, and the on-screen Back/Forward buttons** —
   jump by the step currently selected in the seek dropdown. Steps: 15 s /
   30 s / 1 min / 5 min / **Part**. "Part" uses `PartForward()` / `PartBack()`
   (Back logic: more than 3 s into the current part rewinds to that part's
   start, otherwise jumps to the previous part).
4. **Go To... (Ctrl+G)** — named navigation. For plain audio this is a list
   of the book's parts. DAISY/text structure (headings, pages) will plug in
   here later as a separate subsystem.

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

- **Space / X** — Play/Pause.
- **Up/Down** — volume ±5 (announced; beep at 0 and at 100).
- **PageUp/PageDown** — speed ±10% (range 50–300%; double beep at 100%).
- **I** — announce fresh playback info (off-screen label).
- **Ctrl+O** — Open File.
- **Ctrl+G** — Go To.
- **Ctrl+T** — Sleep Timer.
- **Enter** — activates the focused button.

---

## 7. Sleep Timer (Session 8 — current feature)

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

## 8. Library window

`LibraryForm.cs`. Book shelf migrated from ListBox to **ListView with native
groups** (four groups: **Now Reading / Reading / Unread / Read**, empty groups
suppressed) — native groups solved the screen-reader item-count problem that
separators caused. Author-merge: no separate Author field; a single Name/Title
taken from the folder name. Detailed audio format shown in book details via
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

## 9. Roadmap / suggested order

1. **Sleep Timer** — done (Session 8), pending final edge-case test.
2. **Bookmarks** — next. Saving (likely a new block in `Book.ini`) plus a
   Manage UI. Design to be settled at implementation time. Buttons already
   exist as placeholders (Set Bookmark, Manage Bookmarks in column C).
3. **Settings window** — will use a "hint system": a read-only textbox beside
   most controls with a short explanation, plus a global "Show help hints"
   toggle that flips hint `Visible`/`TabStop` live without closing the window
   (the pattern already lives in the Go To dialog's hint box). Settings will
   also hold: media-keys mode (Off / Only when focused / Always-global via
   RegisterHotKey) and language selection.
4. **Properties dialogs** (player + library) and library tooltips.
5. **Audio filters** (not yet scheduled): dynaudnorm/speechnorm
   (normalization), scaletempo2 (already active, pitch-preserved speed),
   acompressor (dynamic range), highpass+EQ (voice clarity), afftdn/arnndn
   (noise reduction). Actual availability depends on the specific
   `libmpv-2.dll` build.
6. **DAISY / text-book structure** — a large separate subsystem; plugs into
   the Go To level and the seek dropdown's structural levels.

---

## 10. TODO (open items)

- **Verify Sleep Timer expiry (close/shutdown) in the modal-Library edge
  case** described in section 7: a book finishing while the Library window is
  manually open with background playback, i.e. `Close()` beneath a modal
  dialog. (Custom-duration select-all and Ctrl+T were resolved in Session 8.)
- A cosmetic JAWS note on the info box: it announces "i edit read only" rather
  than "read only edit" order — this is JAWS's internal handling of
  multiline vs singleline EDIT controls, not our code. Deferred to final
  polish (options: shortcut in AccessibleDescription, or a naming tweak).
- Seek-step selection is session-only; per-book memory in `Book.ini` is a
  possible later refinement.

---

## 11. How to start a session on this project

1. Read this file and skim the actual source of the files you'll touch (the
   disk is the source of truth; this brief can lag).
2. Confirm understanding back to Gordan in Croatian — briefly, in your own
   words — before changing anything, so he can catch a stale brief.
3. Make sure the working tree is committed (a safety point) before edits.
4. Do the work as surgical edits to the real files; keep `en.lang` in sync for
   any user-visible string; respect the accessibility rules in section 2.
5. Gordan tests with JAWS and reports exact spoken strings; iterate from those.
6. When behavior or architecture changes, update this file so it stays true.
