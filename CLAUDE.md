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
  "Go To, F4", "Sleep Timer, F7". Tooltips are separate and for
  sighted/mouse use. (These read `Ctrl+G` / `Ctrl+T` until the shortcuts moved
  onto function keys — `en.lang` was migrated with the code, this file was not.)
  **The SPEED field was the one control that broke the convention** — its keys
  lived in `Tip.Speed` alone, which is a tooltip, which is exactly what this rule
  says a reader never hears. Fixed 2026-08-28 (`SpeedAccessibleName`), and built
  by joining `Player.Speed.Text` with `Tip.Speed` rather than by adding a key:
  all eleven languages already carried that phrase, so the fix needed no
  translation, and the tooltip and the spoken name are now literally the same
  words and cannot drift. **The shortcut goes LAST here**, after the value,
  because Ctrl+Left/Right are word-navigation keys and JAWS re-reads the control
  on every press (§6) — value first means a reader stepping quickly hears each
  new speed and cuts the tail off with the next press.
  **Position keeps its tooltip and gains nothing** (Gordan, 2026-08-28): Ctrl+1–9
  is *"kao skrivena kontrola"*, so its keys stay off the spoken name.
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

- **The end of a book is a CADENCE, not a signal** (Gordan, 2026-08-23):
  C5-E5-G5 at 160 ms each and then C6 held for 420. Every other sound NBR makes
  is an event; this one climbs to the octave of its own first note and STAYS
  there, two and a half times as long as any step below it. A run says something
  happened — landing and holding is what says finished.
  **It fires only when the book ends BY ITSELF.** `FinishCurrentBook` is reached
  from exactly two places and both are the natural end: mpv going idle with a
  book playing, and `TtsReader.Finished`. Marking a book read from the Library
  goes through `UnloadActiveBook` instead, and neither Stop nor closing the
  player comes near it — so the sound cannot fire for anything the reader did.
  It stands at the TOP of the method, so it sounds as the reading stops rather
  than after the book is saved and the library has opened over it.
  Measured through `SignalTones.Render` by counting zero crossings per note —
  1048 Hz where 1047 was asked for, and the whole cadence 1020 ms including the
  30 ms `GapMs` that follows EVERY tone, the last one included.

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
- **A NEW USER-VISIBLE STRING GOES INTO TWO FILES, in the same commit as the
  feature** (Gordan, 2026-08-22, assigning it to me by name): its key and
  English text in **`Lang/en.lang`**, and its key PREFIX in
  **`tools/lang-order.txt`** under the section it belongs to. The second is not
  optional: `lang-reorder.pl` places a key by matching those prefixes, so a key
  nobody claims lands in "everything else" at the foot of every language file
  and stays there after it is translated. The whole translator flow rests on
  these two — en.lang says WHAT is new, the order file says WHERE it goes, and
  the pending block at the foot of each language file falls out of them.
- **Token/usage awareness** — Gordan batches multiple requests into one
  message to save usage, and the 5-hour limit is a rolling token budget, not
  wall-clock. Full-file regeneration in chat burned tokens fast; Code's
  surgical edits should help. Group related changes.
- **Grammar**: "rewound" is the correct past participle (not "rewinded").
- **THE APP'S NAME IN USER-VISIBLE TEXT IS "Nemoviz Book Reader" OR "NBR", never
  "Nemoviz" alone** (Gordan, 2026-08-17). The full name where the program
  introduces or identifies itself — the title bar, `App.Name`, the About window,
  the first line of a file it writes — and **NBR in running prose**, which is the
  convention `en.lang` had already settled into on its own: the About text opens
  with the full name and then says "the parts NBR is built from". Bare "Nemoviz"
  names the maker rather than the product, so in a sentence about what the program
  DOES it names the wrong thing. It applies to everything a reader can reach,
  including the text inside a log file; a machine token such as the HTTP
  `UserAgent` is not user-visible text and is left alone.
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
2. **Ctrl+1..9** — jumps to 10%–90% of the book, **in whatever unit that book
   measures itself in**: seconds of audio, or characters of text.
   **It said "virtual duration" here and that is exactly why it did nothing on a
   text book, from the initial commit until 2026-08-28.** The nine cases asked
   `SeekToVirtualPosition(currentBook.TotalDuration * X)`, and that method
   returns at its first line when `Chapters` is empty — which a text book's
   always is. So the keys were dead on every .txt, EPUB, MOBI, PDF, Word,
   FB2, HTML, braille file, text DAISY and OCR import, i.e. most of a real
   shelf, while working perfectly on audio, M4B, CUE, DAISY audio and hybrids.
   Nobody had noticed because the formats it worked on are the ones it was
   written against.
   They now go through `JumpToFraction` → **`SeekToBookPosition`**, the path the
   bookmarks already took and which was therefore already right for both, with
   `BookLength()` as the counterpart of the existing `BookPosition()`. The audio
   branch is unchanged code — for a non-text book `SeekToBookPosition` is
   literally `SeekToVirtualPosition`.
   **Reported by Gordan as a question, not a bug** (*"Imaju li svi formati knjiga
   CTRL + 1-9 kao navigaciju?"*), which is worth noting: an accessible shortcut
   that silently does nothing gives a reader no evidence at all, so it can sit
   there for the life of a project.
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
4. **Go To... (F4)** — named navigation. For plain audio this is a list
   of the book's parts. DAISY/text structure (headings, pages) will plug in
   here later as a separate subsystem.
   **Plus GO TO PAGE, since 2026-08-28** (Gordan's shape, from the beta notes):
   a group of its own holding one spin box, opening on the page you are on with
   its number selected so typing replaces it, Enter confirming exactly as it does
   for the list. **The group is not built at all for a book with no printed
   pages**, and then the list takes the room back. Which one the confirmation
   meant is decided by where the reader was: a number typed or stepped, or the
   box holding focus, means the page; anything else means the selected row.
   A number with no marker of its own lands on the nearest one at or before it.
   **Only NUMERIC labels are offered**, and that is measured rather than assumed:
   across 400 EPUBs and the whole braille corpus, 98–100 % of page labels are
   plain numbers, the rest being roman numerals on the front matter and the odd
   "Cover Page" — those stay reachable with the Page seek step, which walks every
   marker whatever it says. `Form1.PrintedPageNumbers` / `CurrentPrintedPage` /
   `SeekToPrintedPage` are the only code that knows a text book keeps pages as
   character offsets and an audio one as seconds.

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

**The letter keys are gone — everything named is on a function key** (done; this
list was stale until 2026-08-04, when it was read back off `ProcessCmdKey`
rather than remembered). The move was made because `cmbSeek` swallows letter
keys as type-ahead while it has focus, and it turned out to suit braille
displays too: a modifier-free function key is the easy case for a display's key
emulation (§8l).

- **Space** — Play/Pause (the only key for it; X was removed in Session 10).
- **F11 / F12** — volume ±5, the higher key raising (announced; beep at
  0, 50 and 100). **The bare Up/Down arrows do nothing and are still
  SWALLOWED**: letting them fall through hands them to WinForms, which walks the
  focus with them. See the long note in `ProcessCmdKey` for why volume left the
  arrows — JAWS' say-all keeps asking for the next line and the volume slid away
  underneath, and nothing inside the app can tell its Down from the user's.
- **Left/Right** — the plain 5-second / one-sentence nudge.
- **Ctrl+Left/Right** — speed ±10% (range 50–300%; double beep at 100%).
  Replaced PageUp/PageDown in Session 10.
- **Shift+Left/Right** — jump by the selected seek step;
  **Shift+Up/Down** — change which step is selected.
- **F1** Help · **F2** Settings · **F3** Library · **F4** Go To ·
  **F5** Set Bookmark · **F6** Manage Bookmarks · **F7** Sleep Timer ·
  **F9** the reading window.
- **F10** — how far into the book you are; **pressed twice** inside 600 ms, how
  much is LEFT (2026-08-28, from Gordan's beta notes). F8's whole info block is
  too much to hear when the question is only "how much longer". Works on text
  books too, where both figures are the reading-speed estimate the info box and
  the Library already show, so the three cannot disagree; nothing loaded gets the
  "no go" beep. **F10 was reserved for a menu bar** and this window has none —
  the Library, which has one, is a separate form that never sees this handler.
  A letter key was not an option for the reason the whole set moved to the
  function row: `cmbSeek` eats letters as type-ahead whenever it has focus.
- **F8** — announce fresh playback info. **Pressed twice inside 600 ms it moves
  focus INTO the info box**, and a third press (or Escape) brings focus back
  where it came from — the box is parked off the client area and out of the tab
  order (§8k), so this is the only way in, and the way out matters as much.
- **F4 is swallowed before anything else sees it**, because F4 on a focused
  ComboBox drops its list open and `cmbSeek` is focusable. **F10 and Alt+F4 are
  left alone** (menu bar, close).
- **Ctrl+O** — Open File. **Ctrl+Shift+O** — Open Folder.
- **Ctrl+1..9** — jump to 10%–90% of the book.
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
  gives a short low beep (same as F4 with no book). The test is the **book**,
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
- Pressing the button (or F7) **while a timer is active stops playback and
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

**Set Bookmark (F5).** No book loaded → the same low "no go" beep as
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
- **Language** combo — app UI language, listing every `.lang` file really in the
  folder (eleven ship). **It follows Windows on a first run** (2026-08-23):
  `AppSettings.LanguageCode` defaults to EMPTY rather than "en", so "not chosen
  yet" is distinguishable from "chose English", and `Localization.Initialize`
  then asks `MatchLanguage(CultureInfo.InstalledUICulture)`.
  - **Matched most specific first: the full tag, then the tag cut back to its
    SCRIPT, then the bare two letters.** The order is the whole method — ask for
    the two letters second and sr-Cyrl-RS meets `sr` before `sr-Cyrl`, handing
    every Serbian reader on a Cyrillic Windows the Latin file. That is exactly
    what the first version did, and the check caught it.
  - `InstalledUICulture`, not `CurrentUICulture`: the first is the language
    Windows itself is in, the second follows a per-user formatting choice and can
    name a language the display is not in.
  - **Deliberately NO "System language" row in the combo** (Gordan's call). It
    would be a rule that resolves to one of the languages already listed — the
    same thing "Follow Windows" was in the Look combo before he removed it — so
    the system language is the STARTING POINT, and the moment a reader picks one
    it is theirs.
  - Verified through the shipped `Localization` against thirteen cultures:
    hr-HR→hr, sr-Cyrl-RS→sr-Cyrl, sr-Latn-RS→sr, de-AT→de, es-MX→es, ja-JP→en,
    the invariant culture→en.
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

- **Check for update (done 2026-08-23, `UpdateCheck.cs`).** One GET against
  GitHub's public releases endpoint, parsed with the same hand-written `Json` the
  translation and cloud-voice code uses — no library, no account, and nothing
  sent about the reader, their library or what they read. Two ways in, and the
  division is the one that put the service guides under Help and left their
  switches in Settings: **Library → Help → Check for update** is the thing you
  DO, **Settings → General → Updates** is the rule you set. On by default,
  because for a beta somebody who does not know a fix exists cannot ask for it.
  - **It runs once a DAY, not once a launch** (`[App] LastUpdateCheck`, written
    when the check STARTS so a machine with no network does not retry on every
    start), from `Form1.OnShown` rather than the Library — someone resuming a
    book never opens the Library, and they are exactly the reader a fix has to
    reach. Off the UI thread: §11 has a bulk import that blocked the window for
    a minute and a file dialog that blocked it on a network read.
  - **The automatic one speaks only when there is something newer.** A reader who
    did not ask has no use for "checked, all well" and less for "the check
    failed", so a manual check reports all three outcomes and the automatic one
    reports one. The manual check announces that it has STARTED
    (`ScreenReader.Announce`), or the ten seconds it may take are a silence with
    nothing on screen and nothing said.
  - **`UpdateCheck.Release` is the git tag this build is, and it is NOT
    `Dialog.About.Release`.** That one is prose the reader hears, it lives in the
    language files and it is translated; this one is an identifier compared
    character for character. **Bump both when a release goes out** — leave this
    one behind and every reader is told there is an update when there is not.
  - **`UpdateCheck.Repo` names a repository that does not exist yet.** NBR's
    source is local by design (§10e's memory), so until the beta is pushed every
    check honestly reports that it could not be made. Verified in exactly that
    state, and against `mpv-player/mpv` as a control so a "could not check" is
    known to be the repository and not the code.

  **AND IT FOUND A LIVE BUG IN TWO SHIPPED FILES.** The check did not work at
  first, and the reason was not its own: **.NET Framework's default
  `SecurityProtocol` here is still `Ssl3, Tls`** (measured — as shipped the
  request threw *"Could not create SSL/TLS secure channel"*, forced to TLS 1.2 it
  returned 20 402 bytes), and every modern service refuses TLS 1.0.
  `AzureProvision` and `Translator` each carry a `|= Tls12` line; **`AzureVoices`
  and `GoogleCloudVoices` did not**, and worked only because the flag is
  process-wide and one of those two happened to run first. Nobody runs first for
  a reader who only ever uses cloud voices, or who pasted an Azure key in by hand
  and never opened the wizard. Both fixed. **A failure of this kind is
  indistinguishable from having no network**, which is why it could sit there
  unreported.

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
F2 rename, which offers **Author and Title for EVERY book** (Gordan, 2026-08-23).
It was gated on `IsDaisy`, and that was never true of the shelf: `BuildShelfItem`
prints "Author — Title" for any book whose Author is set, and EPUB, M4B, MOBI, PDF
and tagged audio all fill it in — so a reader could see a wrong author and have no
way to correct it, the single box writing only the title. The two fields are
offered even where the author is empty, which is the more useful way round: a
folder of MP3s named after the book alone can now be given its author.

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

### The auto-analyser is BUILT, and it runs on the switch, not at import (2026-08-07)

`SoundAnalysis.cs` — `SoundAnalyser` measures, `SoundAdvisor` maps the numbers
onto the six stages, `BookData.Analysis` keeps them in `Book.ini`
`[SoundAnalysis]`. Committed `da2efa8`. **Not yet wired to the UI**: the hook on
Properties' master switch, the `en.lang` keys, and keeping the dialog responsive
for the ~1.6 s are still to do.

**It runs when the reader switches sound processing ON.** Gordan, 2026-08-07:
*"Ako Sound processing ne treba tj. čitatelj je zadovoljan zvukom ne rade se
bespotrebne radnje na uvozu."* **That supersedes the section below**, whose three
reasons do not survive the move: changing filters mid-playback is heard as a
break — but switching processing on already rebuilds mpv's graph, so the break is
what the reader just asked for; the measurement needs seconds to settle — but it
never touches the player, running in its own silent context and seeking where it
likes, so the segment is a choice and not a constraint; and "import already walks
every file" is an argument about amortising a cost most readers never incur.
What it stores is the **measurements**, not only the levels they produced — his
instruction, and also why the analysis runs once rather than on every visit.

**No ffmpeg.exe, which was proved before anything was designed.** The shipped
audio-only libmpv carries `astats` and `ebur128`, and mpv forwards a filter's own
output through `mpv_request_log_messages` — so the numbers come back over the
channel the player already has. Verified through the real C API on real books,
not by finding strings in the DLL (§10e′'s rule). Two settings decide whether it
works at all: **`msg-level=ffmpeg=v`**, without which the graph runs perfectly
and reports nothing — which reads exactly like a filter that is not there; and
**`speed=100`**, because a null AO still paces to the clock, so 20 s of audio
cost 20 s. Measured at about **50× real time — one decode, 530 ms**, so a book is
~1.6 s.

**One graph, three bands** (`asplit` → full / `lowpass=300` / `highpass=6000` →
`amix`). **`ebur128` stands AHEAD of the split**: behind the `amix` it measures
the mixed-down signal, and a real book came back at **−21.7 LUFS against its true
−13.8**, with nothing in the output to say the number was wrong.
**`aspectralstats` is accepted but prints no end-of-stream summary** — it only
sets per-frame metadata, which never reaches the log — so the spectral centroid
is unavailable and the two band ratios answer the same two questions instead.

**A sweep of 113 real books found three faults in the parser, every one silent:**

| fault | how often | what it produced |
|---|---|---|
| astats reports `Noise floor: -inf` | **49 %**, plus 6 % with no such line | clamping it to −120 dB made half the sample report a noise floor never measured |
| a missing key read back as **0** | 6 books | a signal-to-noise ratio of ≈ −21 dB — arithmetic on a value that does not exist |
| a segment landed on **silence** | 7 books | the book's mean level dragged down by the whole depth of the gap |

So `NaN` means *not measured* and never enters an average, `-inf` is kept as an
infinity so "silent" stays distinguishable from "not measured", and a segment
below −60 dB is rejected as no sample at all. **The real noise measure is
astats' RMS trough** — the quietest window, which between sentences *is* the
noise; this section had already reasoned that voice activity detection would find
the noise in the gaps, and the trough gets there without the detection. (ffmpeg
spells it `RMS through dB` in this build. Match the log, not the documentation.)
**Even so the SNR is unavailable in a third of books** (76 of 113), and there
denoise stays off rather than guessing.

**The reference tool's thresholds were NOT copied, and measurement is why.** The
section below offers SlušajKnjigu's SNR 14 dB as a free starting point. Run
against this sample it fires on **zero of 113 books** — the noisiest measures
20.9 dB. Their SNR is a different quantity on a different scale, so the constant
is meaningless here. Every threshold in `SoundAdvisor` comes from our own
distribution, recorded beside each rule:

| measure | have | min | p25 | median | p75 | max |
|---|---|---|---|---|---|---|
| LUFS | 113 | −34.1 | −20.6 | **−18.6** | −16.7 | −7.2 |
| LRA | 113 | 1.1 | 3.2 | **4.3** | 5.7 | 11.7 |
| true peak | 113 | −12.0 | −4.8 | −3.1 | −1.8 | **+3.0** |
| SNR | 76 | 20.9 | 58.0 | 65.4 | 74.6 | 243 |
| low band below | 113 | 1.2 | 2.9 | 3.7 | 4.6 | 9.2 |
| high band below | 113 | 9.2 | 16.4 | 19.9 | 23.1 | 40.2 |

Replayed through the advisor: 4 stages for 46 books, 3 for 27, 5 for 26, and the
four noisiest all take denoise at maximum while clean recordings take none.
**These are starting points for the ear, not a verdict** — the rules are set so a
book at the library's median gets roughly what the dialog already defaults to, so
the analysis moves a book that is unusual rather than re-deciding every book.
§8d's split stands: I measure, Gordan judges by ear.

### The numbers come from a PROPERTY, never from the log — and the log route would have failed in the shipped player (2026-08-08)

**FFmpeg's log callback is process-global.** The first mpv context created in the
process captures it; every later one receives nothing. Measured: alone, a segment
yields **83** ffmpeg log lines; with one earlier context alive, **zero**.
`Form1` creates its context at start-up and holds it for the whole session, so
the analysis would have returned null every single time a reader used it.

**It passed its end-to-end test the day before** — because the harness was the
only mpv context in the process. A false pass of exactly the kind this file keeps
recording. What exposed it was Gordan's six sample files, because that script
asked `MpvDuration` for durations first.

The fix is to read the values as **`af-metadata/<label>`** off a labelled filter
(`@st:lavfi=[…]`), which is per-context; verified identical with and without an
earlier context alive. It must be polled **while the segment plays** — at
end-of-file the graph is torn down and the property is empty. Three things
improved rather than merely being fixed: the `asplit`/`amix` graph went away, and
with it the trap of `ebur128` measuring the mixed-down signal; **`aspectralstats`
started working**, since it publishes exactly the per-frame metadata this route
reads; and keys are prefixed by filter name, so three measurements share one
decode. One unit trap on the way: r128 publishes true peak as a **linear
amplitude** here where the log printed dBFS.

**The centroid is measured but decides nothing.** It was given a veto over the
dullness rule and it had to be taken away: with only the sampling points moved it
swung 1759→3553 on one file and 3404→1352 on another, while the band ratio
measuring the same property moved at most 2.3 dB. astats and ebur128 accumulate
over the segment; aspectralstats publishes per frame, so a poll reads one
essentially random frame. Averaging every frame would fix it.

### Every book is brought to one loudness — −16 LUFS (2026-08-08)

`SoundSettings.TargetLufs`, applied as a `volume` stage just before the limiter,
with the gain worked out once from the measurement. **It cuts as well as lifts**,
and that is the point: the loudest sample comes DOWN 8.2 dB. Gordan named that
sample's level (−7.8 LUFS) as ideal for everything; what he wants is books that
stop jumping, and −7.8 is unreachable for the rest of the shelf. That sample
true-peaks at **+2.8 dBFS** — already clipped — with a crest of 10.6 dB where a
clean recording measures 18.8; lifting the clean one to −7.8 hands the limiter
11 dB to remove, which is precisely the "compressed, muddy" quality he heard on
it. **Its loudness and its muddiness are one thing, not two.**

**A lift stops rather than squashes**: capped so peaks land no more than 5 dB over
the ceiling. The cap fired on a real file — *Prihvatljivo* already peaks at
+0.3 dBFS, so it stops at −17.4 instead of being limited into the target.

### The loudness target had never once been heard, and the chain order follows from that (2026-08-09)

Gordan tuned four bad recordings by ear and asked for the normalisation to move
to the **front** of the chain: *"pa da se zahvati rade na maksimalnom volumenu
jer je, osim šumova, krckanja, šuštanja i tko zna čega, glasnoća veliki problem
kod zvučnih knjiga."* Reading his four books' `Book.ini` back found something
underneath the request.

**`PropertiesForm.FillSettings` was dropping `GainDb`/`GainEnabled`.** It builds
its `SoundSettings` from the CONTROLS, and the loudness target has no control —
it is a single number computed from the measurement. A fresh `SoundSettings`
defaults it to off, and `FillSettings` never wrote it, so it was gone from
**every live preview** and wiped from the book **on OK**. Measured on his four:
the advisor had computed **−9.1 dB** for one and **+2.4** for another, and all
four came back off. So −16 LUFS, the whole "every book at one loudness" feature,
had never reached anyone's ears.

**That is also why he ended up at speechnorm max on three of the four.** The
static stage that was supposed to supply loudness was not in the chain, so the
only loudness control left to him was the DYNAMIC one — which rides gain over
time, lifts the quiet passages, and so raises exactly the noise the denoiser had
taken out. His *"neke stvari koje su bile uklonjene su se na većoj glasnoći
pojačale"* is that, precisely.

**Fixed in three places, because the gain is not a setting:**
- `FillSettings` **carries** it rather than rebuilding it, from a pair of fields
  seeded off the book and replaced when an analysis lands.
- `BookData.Load` **derives** it whenever a measurement exists, so it cannot go
  stale and needs no migration flag — and the books already wiped repair
  themselves on next load. Verified: three of the four came back with a gain
  (−9.1, +2.4, and two at zero because they measure −16.4 and −15.8, already at
  target).
- `SoundAdvisor.GainFor` is public for it.

**And then the order, which he was right about — but only for the static half.**
`volume` now stands FIRST, ahead of the highpass. The reason is sharper than the
intuition: `afftdn`, `deesser` and `acompressor` all take thresholds in
**absolute dB**, while the books they meet run **−7 to −22 LUFS**, so "noise
reduction, medium" has meant something different on every book. Bringing the
level to target first makes one preset mean one thing. **The highpass and the EQ
are linear and do not care** — nothing about them changes, and nobody should
expect it to.

**`speechnorm` stays at the far end, and that distinction is the whole of it.**
In front, a dynamic gain rider would hand `afftdn` a noise floor that moves. Only
the fixed number moves. Clipping is not a concern at the front: mpv's graph is
float, and `alimiter` still owns the way out.

**Verified through the shipped dialog**, on a copy of a real `Book.ini`: the
advisor's −9.1 dB comes out as `lavfi=[volume=-9.1dB,highpass=f=100,afftdn=…]` —
first in the chain — and survives a real `Persist()` and a cold reload, where
before it was lost.

**What his four books say about the rules** (his values against what NBR
proposed):

| book | high band below | NBR rumble | his rumble | NBR EQ | his EQ |
|---|---|---|---|---|---|
| Barbara | 39.6 | strong (3) | **max** | 0,0,+2,+4,+7 | −15,−10,+3,+4,+7 |
| Jevtušenko | 31.5 | strong (3) | **max** | 0,0,+1,+2,+5 | **identical** |
| Aragon | 36.5 | strong (3) | **max** | 0,0,+2,+3,+6 | −10,−15,+5,+5,+5 |
| Torton | 42.0 | **light (1)** | **max** | 0,0,+2,+4,+8 | **identical** |

- ~~**The rumble rule is too timid — 4 of 4, unanimously.**~~ **WRONG, and
  disproved by the re-listen on 2026-08-10 — see §8d's closing section.** He went
  to maximum on every one *because the 200 Hz band did not exist yet*; once it
  did, he accepted the advisor's rumble level on three of four and nudged the
  fourth by one step. **Nothing was changed on the strength of this bullet**,
  which is the only reason it did no harm. A user pinning a control at its limit
  says the control they wanted is missing at least as often as it says this one
  is too weak.
- **The treble ramp is landing right.** Two of four are identical to the dB, and
  they are the two where he did not also raise the level.
- **The two big low-band cuts are downstream of the missing gain**, not of the
  EQ rule: both are books where he turned normalisation up and then had to take
  300 and 800 Hz out by 10–15 dB — which is the highpass being too weak, seen
  from the other end. Worth re-listening before treating them as EQ data.
- **`NoiseShare` is 0.0 on all four**, so denoise came from the damage rule, as
  §8d's `MinNoiseShare` intends. He kept its answer on three and softened Torton
  by one step.

**His settings were tuned against a chain that no longer exists** — the gain was
absent and the order was different — so all four are owed a re-listen before any
threshold is moved on their evidence. The rumble finding is the exception: it is
unanimous and does not depend on level.

### The ear's verdict, and the sound work is closed (Gordan, 2026-08-10)

He re-listened to all four bad recordings against the advisor's proposals, on
the rebuilt chain — gain first, five bands with the lowest at 200 Hz, ±20, and
the gate. His verdict: *"ovo što smo složili zajedno sa savjetnikom je dobar
alat i ne znam iskreno što bismo još mogli dobiti. Od loše snimke neće nikad
ispasti kristalno čista, cilj je da se knjiga može poslušati do kraja bez
previše nerviranja."* **Treat the sound chain as finished unless a new sample
says otherwise.**

| | advisor | his | |
|---|---|---|---|
| Jevtušenko | HP3 DN2 EQ 0,0,+1,+2,+5 · gain −9.1 | **accepted unchanged** | |
| Torton | HP1 DN2 EQ 0,0,**+2,+4,+8** | HP1 DN2 EQ +1,+3,**+2,+4,+8** · norm 4 | top three identical |
| Aragon | HP3 DN2 EQ 0,0,**+2,+3,+6** · norm 1 · gain +2.4 | HP4 DN4 EQ −12,−10,**+2,+3,+6** · norm 0 · gate 4 · gain +2.4 | top three identical |
| Barbara | HP3 DN2 EQ 0,0,+2,+4,+7 | HP3 DN2 EQ −20,−10,+6,+3,+5 · norm 4 | within 2 dB |

- **The treble ramp is solved**: three of four identical to the decibel, the
  fourth within 2 dB. It was two of four before the recalibration.
- **The rumble rule was right all along** — see the struck-through bullet above.
- **The gate works and does not pump.** The one thing measurement could not
  answer, answered by ear on Aragon at maximum: *"u pauzama je sve rezao, ispod
  govora se čuje ali su prijelazi korektni. Vjerujem da neutrenirano uho to ne bi
  ni primijetilo."* The attenuate-don't-silence choice (20 dB cap, 250 ms
  release) is vindicated; do not turn it into a hard gate.
- **The two low bands are TASTE, not a measurable, and the advisor is right to
  leave them at zero.** Measured: Barbara and Torton sit 1.6 dB apart on LowBand
  and got opposite treatment (−20 against +1), and the centroid is worse than
  useless here — Torton has the LOWEST centroid of the four, i.e. the most
  low-heavy, and is the one he **boosted**. Four points, no predictor. Do not
  invent a rule for these two bands.
- **Loudness matters more than noise to him**, and this is a standing priority
  rather than a comment on these files: *"nekad je poštena glasnoća čak i
  važnija od šumova… postoje snimke koje doslovno moraš držati na uhu zvučnik s
  maksimalnim volumenom."* He has no such sample here. **Known limit if one turns
  up:** the −16 LUFS lift is capped by `MaxLimitingDb` (5 dB), so a recording far
  below target stops short rather than being limited into it. That cap is the
  first thing to revisit for that case, and it should be revisited with the
  sample in hand, not before.
- He turns speechnorm up where the advisor leaves it off (4, off, 0, 4 against
  0, 0, 1, 0) — on the two books whose gain was already zero because they measure
  at −16. Three points, so no rule change; noted as the one gap with a direction.

### `tools/spectrum` — the analyser we already had (2026-08-08)

Gordan asked whether a small free analyser could be downloaded. **No, and none is
needed.** The audio-only libmpv carries ffmpeg's whole audio filter set; the small
candidates were also the wrong licence — [aubio](https://aubio.org/) is
GPL-3-or-later and Essentia AGPL, and §10e's LGPL build exists precisely to avoid
that. `tools/spectrum` prints 22 ISO third-octave bands, each relative to the
file's own RMS so recordings of different loudness compare directly.

What it says about the six samples: all peak at 250–400 Hz, and **the damage is a
smooth tilt, not a step** — the four bad ones track the good pair to about 800 Hz
and then fall away, 4 dB down at 1 kHz, 8 at 2 kHz, 12 at 3–4 kHz, 15 by 8 kHz.
It also named something nobody had spotted: **Loše 2 carries 5–10 dB MORE than
every other file at 100–125 Hz**, which is the rumble under the "svakakvi šumovi"
Gordan heard, and the reason a strong highpass belongs on it.

### The bands moved, and a seventh stage arrived (2026-08-09)

**The lowest bell is 200 Hz, not 300, and the range is ±20 dB, not ±15.** A
third-octave profile put the bad recordings' real excess at **160–250 Hz** —
+5.6/+7.9 dB over the reference on Loše 3, +5.5/+6.0 on Loše 4, +2.3/+5.6 on
Loše 2 — and **nothing reached it**: the highpass stops at 120 and the lowest bell
started at 300. That gap is why Gordan pinned rumble at maximum on all four books
*and* cut 300 Hz by 10–15 dB; he was attacking a 200 Hz problem from both sides.
He also hit the ±15 wall twice in one session, and a control a reader pins at its
limit has run out of travel.

**Do NOT extend the highpass — measured, it buys nothing.** 120 Hz ×1 removes
0.1–0.6 dB of the whole signal; 180 Hz, which already eats a male fundamental,
removes at most 1.4 dB. There is nothing below 120 Hz in a speech recording.
(150 ×1 and 120 ×2 measure almost identically, so "steeper" and "higher" are the
same purchase.) The 4-of-4 "the rumble rule is too timid" signal is real as a
report and misleading as a diagnosis: what he wanted was the 200 Hz band.

**"Noise in pauses" — a gate (`agate`), the seventh stage.** Gordan asked for
"another compressor that pushes everything below x dB into inaudibility"; that is
a gate, the opposite of a compressor. Measured with a level trace in **0.2 s
windows** — nothing coarser sees a pause — it identifies his two books by itself:
noise floors at **−40.0** (Loše 2) and **−46.4 dBFS** (Loše 4) against −57.5 on
Loše 1 and **−99.2** on his reference, which is digital silence. With thresholds
from that measurement the pauses drop **6.1 and 12.7 dB while the speech median
moves 0.1 dB**. Four decibels of threshold was the difference between doing
nothing and working, so the presets step 3 dB. It **attenuates rather than
silences** (20 dB cap, 250 ms release), because a hard-closing gate is more
tiring than steady noise and chops word tails. **Off by default** — every other
stage has an advisor rule and this one cannot have one until the noise floor is
measured (below).

**It shares the loudness cell, and Gordan's reasoning beat mine.** I proposed
pairing it with Noise reduction; he pointed out that **the six cells already read
the chain in order**, so the stage that acts last belongs in the last cell — and
that these two fight each other directly, one lifting quiet passages and the
other pushing them down. It costs no layout: that cell shares row three with
Tone, already the tall one. The group is retitled **"Loudness and pauses"** and
each switch carries its own name, since a group name that covers only one of two
controls is exactly what misleads a reader on entry.

**The noise floor IS measurable, and this file was wrong about it.** §8d says it
is unavailable in 99 % of segments; that is true of astats' `RMS_trough` — a
minimum over tiny windows, so it hits an instant of digital silence and reads
−inf — and false of the noise floor itself. **The p5 of RMS over 0.2 s windows is
stable on every sample.** Getting that into the analyser is the most valuable
thing left here: it gives the gate an automatic level and stops denoise falling
back to the damage rule, which is what it does on all four of Gordan's books
(`NoiseShare = 0.0`). Take it from ONE decode by polling `af-metadata`, not the
150 separate decodes the probe used.

**Superseded, and left here as the record:** the paragraph below suggested
centres of 120 · 400 · 1500 · 3500 · 8000 Hz. The measurement moved them.

**Open, and Gordan's call: five EQ bands instead of three.** A `treble` shelf
gives a FLAT lift above its corner, but the deficit is a ramp — matching the
reference needs +4 at 1 k, +8 at 2 k, +12 at 3.5 k, +15 above 5 k, which three
bands cannot express. Centres suggested by the measurement rather than by
convention: **120 · 400 · 1500 · 3500 · 8000 Hz**. Cost: §10b's Tone cell already
sets the height of the whole stage row, so two more rows is a layout change, not
two more controls.

**Known artefact of the tool, not hidden:** at or above Nyquist the `bandpass`
degenerates to passthrough and the band reads 0.0. Half these samples are
22.05 kHz, so their 12.5 kHz row is meaningless.

### How many samples, and the two rules that mattered more than any of them (2026-08-09)

Gordan supplied the six WHOLE books his extracts came from —
`D:\Test naslovi\Audio Test`, **100 hours**. Extracts could not answer this: the
whole premise of sampling more is that a book varies across its length, and an
extract has no tenths. Raw readings kept in **`docs/sound-sampling-6-books.tsv`**
(2203 of them, including a 2003-point grid through every book) so any future rule
change can be tested in seconds instead of re-measuring for 45 minutes.

**Three samples was not defensible.** Twenty independent triples produced on
average **six different sets of settings per book**, and the loudness they implied
**spanned 10.5 dB** — comfortable against unusable, decided by which three seconds
were drawn.

**Sampling is now ten tenths of the TIMELINE, two readings in each — twenty.**
Of the timeline and not the file list: one of these books has 5 files and another
156, so picking by file index samples a long file and a short one equally. Fixed
positions, never random — the same book measured twice must give the same
settings, or *"why did it change?"* has no answer.

**Twenty, and the corpus chose it.** Worst gain error falls 10.5 → 4.1 → **2.7 dB**
from 3 to 10 to 20 samples and then stops: sixty gives 2.2, ninety 2.1. Gordan
asked for quality regardless of duration; the honest answer is that **quality
stopped being limited by sample count.** What limited it was two rules.

**The noise rule was a lottery.** It used the mean SNR, and the mean takes only
the segments that HAVE one. Measured: **astats' RMS trough is available in 0–1 %
of 2003 segments** — it is a minimum over windows, and 20 s of speech nearly
always contains an instant of digital silence, so it is `-inf`. (This file called
it "the real noise measure" on one favourable reading. Corrected.) With the noise
floor as patchy as it is, **two books had their denoise decided by ONE OR TWO
readings out of six hundred, whose median was 122 dB.** The figure now decides
nothing unless **at least half** the segments produce one; otherwise the damage
rule takes over, which is stable. That change alone took agreement with the
reference from **23 % to 76 %**.

**The treble steps were the rest, and the corpus says so almost exactly** — how
reproducible a book's settings were is predicted by nothing but its distance from
a 6 dB step edge:

| book | high band below | from an edge | reproducible |
|---|---|---|---|
| Torton Vajlder | 42.0 | **0.0 — on it** | **45 %** |
| luj aragon | 36.4 | 0.4 | 58 % |
| Jevtušenko | 31.5 | 1.5 | 85 % |
| Barbara | 39.4 | 2.6 | 95 % |
| hallmarked man | 19.8 | 4.2 | 98 % |

A book on a boundary flips for ever however well it is measured: the measurement
converges and the STEP turns what is left into a categorical difference. The lift
is continuous now through the same anchors, rounded to 1 dB. Torton went **45 % →
85 %**, and the **treble error is at most 1 dB anywhere, mean 0.2** — which is what
reaches the ear, and what an exact-match metric was hiding in both directions.

**Method note worth keeping: the metric was wrong before the rule was.** Counting
exact equality of an integer dB value penalises a 1 dB difference as heavily as a
4 dB one, so finer quantisation made agreement look WORSE while the actual error
fell. Measure the quantity that reaches the ear, not the label.

**Open, and NOT a sampling problem:** about 2.5 dB of gain error that does not
improve with more samples (2.1 at ninety). That is the book varying within itself,
and the answer would be **settings per part of a book** — a different feature.

### Recalibrated against Gordan's own samples — the library median was NOT the target (2026-08-08)

Six files in `D:\Test naslovi\Audio Test`: four he calls bad, one **Poželjno**
(his reference for how a book should sound) and one **Prihvatljivo**. They
disproved most of the thresholds set from the library distribution, which is
exactly what they were for.

| | LUFS | LRA | true peak | SNR | low below | **high below** | **centroid** |
|---|---|---|---|---|---|---|---|
| Loše 1 | −15.7 | 3.0 | −1.7 | — | 4.1 | **42.5** | **987** |
| Loše 2 | −19.9 | 8.5 | −3.3 | — | 2.3 | **36.7** | **1280** |
| Loše 3 | −7.3 | **+2.8** | — | — | 2.5 | **27.6** | **1583** |
| Loše 4 | −20.7 | 2.7 | −4.3 | — | 2.6 | **38.9** | **1437** |
| Poželjno | −22.3 | 5.9 | −2.8 | — | 4.7 | **20.0** | **1759** |
| Prihvatljivo | −21.8 | 5.6 | −2.5 | **35.0** | 5.8 | **17.8** | **3404** |

**His bad recordings are not noisy — they are DULL**, and the two dullness
measures separate them with nobody in the gap. Four rules were wrong, three of
them backwards:

- **De-esser fired on healthy voices.** Its gate was the library median (19.9),
  and both good samples sit at 17.8 and 20.0 — so the reference recording was
  de-essed while four muffled ones were not. Gate moved to 15.
- **Denoise was wildly aggressive.** 35 dB below the speech is what Gordan calls
  *"malo rooma, skoro nezamjetno"*, and the rule applied FULL denoise there. The
  scale is now anchored on that one ear-verified point, not on the library's
  median of 65.
- **LRA does not separate good from bad at all** — the good pair measures 5.9 and
  5.6 against three bad ones at 3.7, 3.0 and 2.7. So the compressor was
  compressing the reference and leaving the bad alone. Gate moved above the good
  pair, which leaves only the one genuine outlier at 8.5.
- **Normalisation treated the median as the target.** Both good samples are
  quieter than the library median; a book a little quieter than average does not
  need its level rebuilt. Gate moved under them.
- **Dullness now scales.** +2 dB was the whole correction, which is nothing to a
  recording sitting 42 dB down; it is 2 dB per 6 dB past the edge of normal,
  capped at 8, and requires the centroid to agree.

**Result: both reference recordings come out with nothing but the mildest
highpass**, and the four bad ones get treble lifted in proportion to how dull
they measure.

**Two honest negatives worth keeping.** *Plosives cannot be detected this way* —
the idea was low-band crest, and measured it says the opposite of the truth
(Poželjno, which has none, has the highest crest at 19.2; the bad four are
13.7–16.6, because crest tracks compression). A plosive is a handful of events
in an hour and an aggregate over 20-second windows cannot see one by
construction; the highpass is cheap insurance either way. And *clipping cannot be
repaired* — Loše 3 runs to +2.8 dBFS and no rule is offered for it, because
nothing in the chain can undo it and the always-on limiter already stops it
clipping again on the way out.

**The lesson under all of it:** the median of a 1622-book shelf is the average of
what people happen to own, not a definition of good. Calibrating to it and
calling it the target is how every one of these rules went wrong.

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
  **Both of those last two claims were measured on too little (corrected
  2026-08-04).** Run over 33 real books, the cleaner as committed failed
  `pieces == whole` on **2** of them and idempotence on **4**. The unwrapping
  rewrite below fixes the first outright — 0 of 33 — and makes the second worse;
  see there for why that is the right trade.

**The unwrapping rule, rewritten 2026-08-04.** A line break is the source's
wrapping unless the line before it **ended a sentence**. It replaces "the next
line starts with a lower-case letter", which asked the wrong end of the break.

- **Measured, the old rule caught nothing in braille**: 0 joins out of 43 466
  breaks across 19 books, because a braille line that continues a sentence
  usually starts with a space, a quote or a capital. Across the corpora it left
  13 954 mid-sentence breaks in braille, 7 987 in plain text, 1 212 in flat Word.
  The new rule removes **94.4%** of them in plain text against the old rule's
  27.6%. Gordan asked for it after hearing the breaks in a braille book, and was
  right that looking at the full stop is the sounder question: joining a line
  whose predecessor DID end a sentence is pointless rather than harmful, since
  the stop still separates them for speech.
- **Short lines are left alone, and the corpus picked that guard.** Without it
  the rule glues title pages — "The Yield by" + "Tara June Winch" + "print
  pages", "HRVOJE HITREC" + "SMOGOVCI". Counting could not tell damage from
  repair; reading the joins could, and the split was clean: every repair has a
  full line in it, every piece of damage is a stack of short ones. "Short" is
  half the width the text itself wraps at, taken from its own 90th percentile.
- **It runs ONCE over the WHOLE text, before any cutting, and that placement is
  the design.** Two attempts put it inside `Clean`, and both broke
  `pieces == whole` on 7–8 books of 21. The cause is the same either way round: a
  piece's first and last lines are cut off mid-line, so their LENGTHS are wrong,
  and any test that measures a line answers differently at a piece edge than in
  the middle of a text. **Deciding before the cutting removes the question
  instead of answering it** — and it fixed the 2 pre-existing failures as well.
  Length-preserving to the character, so every offset still lands where it did.
- **Idempotence got worse — 4 failures to 15 — and that is accepted.** The guard
  reads a line's length, and a line that has just been joined is longer, so a
  second pass asks the same question of different text and can join once more.
  Nothing depends on it: cleaning runs once at import, `[Book] TextCleaned`
  guards the one-time migration, and a re-read always starts from the braille
  file. The invariant that *is* load-bearing — pieces matching the whole, which
  is what keeps a heading pointing at its own title — is now exact for the first
  time.
- A break inside a hyphenated word is skipped and left to `Dehyphenate`, which
  needs to see the `-\n` it matches on.

**Player integration** (branches on `BookData.IsTextBook`, like DAISY):
- **Detection**: a folder with a `.txt` and no audio (`BookData.DetectTextBook`);
  `TextPosition` (char offset), `TextWpm` (per-book speed override, -1 = global),
  `TextChars` (cached for the estimate) persist in Book.ini.
- **Transport**: `LoadTextBookPlayback` loads the text into `tts` instead of an
  mpv playlist; Space/Back/Forward/position/save all branch to the reader.
  **Crucially, mpv events are skipped for text books** (`EventTimer_Tick`) — an
  IDLE event would otherwise flip `isPlaying` off (killing autoplay) or wrongly
  "finish" the book. The first autoplay `Play()` is also deferred one tick.
- **Seek steps** (per book, `RebuildSeekSteps`): the time steps, **Standard
  page** (1800 chars, the translation/journalism unit) or the book's real Pages,
  **Paragraph** where it is worth having, and **Bookmark** once the book has one.
  **This list said "Sentence / Paragraph" for a long time and neither was in it**
  — both are in `SeekStepKind` and in `TextSeek`'s dispatch, and
  `RebuildSeekSteps` added neither, so `TtsReader.NextParagraph` could not be
  called by anybody. Sentence stays out on purpose: the bare Left/Right arrows
  already move by one sentence in a text book, so a step would be a second name
  for the same thing.
  **Paragraph is offered since 2026-08-28, and only where a paragraph is one.**
  A paragraph is a blank-line block, and whether that is navigable depends
  entirely on the format. Median sentences per paragraph over the test corpus:
  **docx 2.8, epub 2.6, mobi 3.0, azw3 2.5, odt 3.2** against **braille 28.8,
  .doc 119, .dxb 956, .rtf 1777** — where the "paragraph" is a chapter or the
  whole book and one press would carry the reader pages away. `Form1.
  AddParagraphStepIfUseful` gates on at least two paragraphs and **at most 15
  sentences** in each; there is nobody between 3.2 and 28.8, so the threshold
  sits in a gap five times wider than anything it separates. Gordan asked for the
  step and predicted the inconsistency ("kod flatova možda i jako nekonzistentno,
  pretpostavljam") — he was right, and the numbers say by how much.
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
- **Speed is a MULTIPLIER, exactly as an audio book's is — 0.5× to 3.0×, ±0.1 a
  step** (Gordan, 2026-08-23), reusing the player's speed control (`ChangeSpeed`
  branch), with a double-beep when crossing the current voice's own default
  rather than 100 %. Stored as a whole percentage (50–300) so no settings file
  ever carries a decimal point, and mapped to the backends' −10…10 rate by
  `TtsReader.SpeedToRate`.

  **It was words per minute until then, and his question is what retired it:**
  *"otkad smo speech prebacili na audio izlaz i brzinu mu kontroliramo tamo ima
  li smisla pričati o WPM?"* No — the figure was a label on an engine rate that
  every voice interpreted its own way, and no voice ever read at it.

  **Geometric, not linear, because the rate scale is** — the same curve
  `CloudSpeechBackend` already handed to mpv, so `SpeedToRate` is simply its
  inverse and a cloud voice and a local one asked for 1.5× both deliver it.
  Consequences worth knowing: **50 % is rate −6**, so −7…−10 are unreachable
  (the old control bottomed out at −5, so the slow end is one step LONGER than
  it was); and 26 positions map onto 17 rates, so a few presses do not change
  the engine — which is still far better than 65-onto-16 before.

  **Migration is through the RATE, never through the words** (`WpmToSpeed` =
  `RateToSpeed(WpmToRate(w))`), so a book set up under the old scale keeps
  sounding exactly as it did. `RateToSpeed` snaps to the control's own tenth
  only where that leaves the rate alone — at rate −4 it does not (64 % rounds to
  60, which reads back as −5), and sounding unchanged outranks a tidy number.
  Stored under NEW keys so the two scales can never be confused: `[Settings]
  TextSpeed` in Book.ini, `[TextToSpeech] Speed` in Settings.ini, and
  `[TextVoices] S<i>` in place of `V<i>` — a version marker was not optional,
  since 200 is a sensible number under either reading. Verified on Gordan's own
  Settings.ini: nine voices, all at 175 WPM, all out at 100 %, V lines gone,
  stable on reload.

  Reading-time estimates use `TtsReader.CharsPerMinute`, which IS linear in the
  percentage — that question is "how long will this take", and twice the speed
  really is half the time. **This moves a migrated book's estimate** (250 WPM
  became 160 %, so 1628 chars/min where it read 1500), and toward the truth:
  §8g′ measured the old estimate running 6.9 % long against a real export.

  **The spin boxes in Settings and Properties step by the same amount as the
  player** (`MakeDecimal`/`MakeNumeric`'s `increment`): 0.1× speed, 5 % volume —
  stepping by 1 is far too slow when every step is spoken.
- **Global TTS defaults** (voice/speed/pitch/volume) live in **Settings → Text
  Books** (`AppSettings` `[TextToSpeech]`), with a "Test voice" button.
- **Display**: title bar + info box show **percentage** (one decimal — the
  integer sits at 0 for a long book), estimated Elapsed/Remaining/Time, and the
  voice + speed ("Voice: RHVoice Karmela, 1.5x"). A started book is forced to
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

> **STALE — CORRECTED 2026-08-15, read off the code after Gordan said so.**
> **There is no Speech Engine combo and has not been for some time.**
> `SettingsForm` has `cmbLanguage` and `cmbVoice` and nothing between them:
> Settings → Text Books asks for a LANGUAGE and then offers every voice that
> speaks it (`cmbTranslateEngine` is the translation service, a different thing
> entirely). The `Engine` field survives inside the `voiceCatalog` tuple with no
> control over it. §10c records the removal and its reason — grouping voices by
> the vendor they report is not a question a reader has an opinion about — but
> this paragraph was never updated, and it was quoted back at Gordan as a live
> feature. **Anything below about the two-combo picker describes what was built
> in Session 15, not what ships.**

### AZURE SPEECH VOICES WORK — end to end, on a real account (Gordan, 2026-08-17)

*"azzure glasovi rade."* The second cloud is in and heard. What it took, and
every step of it was a thing checked rather than assumed:

- **`AzureVoices` + `CloudSpeechBackend`.** One backend serves both clouds —
  eight of its four hundred lines knew a vendor's name, so a second class would
  have been a twin that drifts. Azure names carry a vendor tag
  (`Gabrijela (hr-HR, Azure)`) because Google rebuilds its ids by parsing its
  own names and would otherwise have claimed an Azure voice, sent it to the
  wrong service and charged the wrong allowance, silently.
- **No region field.** The endpoint is regional *and* a resource-name form
  exists; Microsoft's docs contradict themselves about which Speech accepts, so
  both are tried and whichever answers is remembered.
- **`AzureSpeechSetupForm` — NBR creates the resource itself.** The ARM engine
  had existed since 2026-08-15 and had never been reachable: no dialog called
  it. The portal is what this replaces, which is the whole point.

**Three live failures, each of which taught something:**

1. **`/common` refuses a personal Microsoft account** for an ARM scope — the
   thing memory already recorded as the one open piece, and it caught us again
   because `AzureProvision.Tenant` was a static nothing ever set. It is a stored
   setting now, asked for once, and it takes a **domain** as readily as a GUID.
2. **The dialog was discarding Azure's own reason.** `AzureResult.Error` is
   always set and always generic; `Detail` carries the sentence Azure sent. It
   was written `Error ?? Detail`, so the coalesce could never fire. Two attempts
   were spent on a bare "403" that had an explanation attached the whole time.
3. **West Europe is closed to new Cognitive Services customers** — *"The
   selected region is currently not accepting new customers"*. That is capacity,
   it says nothing about the account, and it changes without notice. `SpeechRegion`
   became **`SpeechRegions`**, eight of them nearest-first, each verified to be in
   Microsoft's own text-to-speech endpoint table; the list is walked only for a
   refusal that means "not here", since a permission or quota would say the same
   in all eight.

**The switch lives in its own group**, between the two services: it governs
cloud voices as a KIND and reads as one vendor's if it sits inside one. Gordan
found that by looking for it in the Azure group and not finding it.

**Still true and worth repeating before anyone leans on these voices:** the free
allowance is ~0.5 M characters a month — about ONE book, against Google's two to
nine — and Azure's `hr-HR` has no custom pronunciations, so §8j's dictionary
works less well there than with a local voice.

### The reader is told what a book will cost BEFORE it starts (`CloudUsage.cs`, 2026-08-23)

Gordan's idea, parked 2026-08-17 and built now. Both services answer this
question only AFTERWARDS — Google has a monitoring page, Azure a portal — so a
reader who has to open one to learn what a book costs learns it once the book has
cost it. NBR already knows every character it sends, so the number can stand in
front of them instead.

- **Counted inside `CloudVoices.Synthesize`, which is BEHIND the speech cache.**
  A second reading, an export replayed from disk and a sentence the look-ahead
  already fetched are all counted as nothing — which is exactly how the service
  bills them. Counting where speech is SPOKEN would have counted a re-read as a
  fresh cost. Only a reply that arrived is counted: a refused or timed-out
  request is not charged, so it is not counted either.
- **Warned from `SyncPrefillToPlayback`**, which is the one place that knows all
  three things at once — something is sounding, it is a text book, and the voice
  is somebody else's to bill for. Asked BEFORE the look-ahead starts, because the
  look-ahead buys the whole book at ten times reading speed and would be most of
  the way through it before a reader could be asked anything. Deferred with
  `BeginInvoke`: the caller is `SetPlayPauseState`, and a modal dialog part-way
  through setting the transport state would leave the button saying one thing and
  the player doing another. Once per book and voice; a voice change asks again,
  because it may be a different service with a different allowance left.
- **The book's whole length is quoted, which may be more than it will cost** —
  anything already cached is paid for. Overstating is the safe direction for a
  number somebody is about to spend money on, and the sentence says what the book
  HAS rather than what it will cost, which is true either way.

**IT LIVES IN ITS OWN FILE, AND `Settings.ini` WOULD HAVE LOST THE COUNT.**
`IniFile` reads a whole file into memory when constructed and writes the whole of
it back on every Save; `AppSettings` holds one such object for the life of the
program. **The first thing it saved — a volume change, the last opened book —
would have written its start-up snapshot back over every character counted since.**
A reader on a long book nudging the volume would have watched the number reset
with nothing appearing to go wrong. `CloudUsage.ini` also files it honestly: this
is a MEASUREMENT, not a setting.

**The two allowances have a source and neither is invented, but they are not the
same KIND of fact.** Azure's **0.5 M** is Microsoft's own pricing page, read
2026-08-23. Google's **1 M** for Chirp 3 HD is NOT from Google — its pricing page
is JavaScript-rendered and gives up nothing to a fetch, still true from
2026-08-17 — it is what several third-party summaries agree on, beside the $30
per million Gordan read off Google's page himself. **And a reader's real
allowance may be neither**: a trial credit, an account that has already paid, a
project with billing off. So both are overridable in `CloudUsage.ini`
(`GoogleFreeChars` / `AzureFreeChars`), and the warning is worded to stay honest
when the figure is a little wrong — what has been used, what this book adds, and
only then that continuing may be charged.

### The Advanced tab is split — agreed AND BUILT 2026-08-17

Gordan: *"zakompliciralo se sve u Settings/Advanced, treba to malo podijeliti."*
Five groups now, three of them credential dialogs, on a page a reader visits
once. The split he asked for:

- **A new place under Help** — one page per service: what it is, what it does,
  a **step-by-step account guide**, and its configuration dialog. His example
  format is numbered and literal ("1. Open deepseek.com  2. Find Signup and
  click…"), and he is explicit about why: *"sam sam se kao iskusan korisnik
  pogubio, manje iskusni korisnici će posijediti."* Google and Azure will be the
  hairy ones and that cannot be avoided.
- **Settings → Advanced keeps only** the list of services, a hint saying what
  each is for, the on/off checks, and one line saying where they are configured.

**Two things settled while the rest waits:**

- **The cloud-voices check goes FIRST in the voices group** when the page is
  rearranged (Gordan, 2026-08-17). It is one group then, not the separate
  "Using cloud voices" box that stands there now.
- **Google's own group needs no hint of its own.** He wrote the text on the
  CHECK — *"This one turns on and off your use of cloud voices… the switch
  governs the picker, not what can be played"* — and judged that enough:
  *"mislim da ne treba više od toga."* `Hint.Settings.Cloud` is deliberately
  EMPTY, which since 2026-08-17 means no `?` at all rather than a button
  opening nothing.

**Both halves are now built.** The window came first (`ServicesForm`), and the
stripping followed the same day. Settled while doing it: the name is *Services and
accounts*, and the guides cover **only the four that need an account** — for a
Windows service *"je dovoljan hint, ne zahtijeva izlazak na web niti kakve
registracije"*, so OCR languages and OneCore voices stay where they are.

**What Advanced holds now: three groups, no credential dialog.** OCR untouched;
Translation keeps the service combo (which still SAYS "key stored" or "no key") and
the standing notes, minus its key button; and **one** cloud-voices group where there
were three — the switch first, then a line for Google and a line for Azure, then the
line pointing at Help. Measured 0 problems in both looks with `tools/check-layout`.

**THREE THINGS THE STRIP TURNED UP, and each was a real loss waiting to happen.**
The lesson is the general one: moving a job is not moving a dialog, and what the old
place did *besides* the obvious has to be counted first.

- **`Forget` had nowhere to go.** Google's and Azure's only removal path was the
  button on Advanced, so stripping the page would have left a stored credential
  impossible to remove. `ServicesForm` has a Forget button now, per service, using
  each service's own warning text. (The two translation engines already had Remove
  inside their own key dialog.)
- **The Services window never fetched the catalogue.** `LoadCloudCredential` on
  Advanced called `GoogleCloudVoices.Refresh()` and reported failure while the
  reader stood there; the version written into `ServicesForm` the day before only
  stored the file. A reader setting Google up through the new window would have been
  left with *"a service account is stored, but the list of voices has not been
  fetched yet"* and nothing to do about it. Fixed there, and it is the reason the
  old method was read before being deleted rather than after.
- **Azure's manual pair had no other door.** `AzureVoices.Save(resource, key)` was
  called from Advanced alone — `AzureSpeechSetupForm` only ever provisioned — so
  stripping it would have removed the way in for anyone who ALREADY has a resource.
  That matters most for Azure precisely because provisioning is the path with a
  history of refusing (personal account outside its directory, a region closed to
  new customers, a lagging provider registration). The pair fields are in the setup
  dialog now, under *"Or, if you already have a Speech resource"*, so that dialog is
  the whole Azure job.

**Still Gordan's to run by eye and ear**, as every layout change here is.

### Cloud voices — where they live, settled with Gordan 2026-08-15 (spec, not yet built)

Chirp 3 HD passed by ear (see memory). The whole difficulty was never the
synthesis but **where 30 voices that speak 53 languages can go without wrecking
two dialogs that are already full**. Measured first, because it decides
everything: the catalogue holds 2066 entries, 1568 of them Chirp 3 HD — but only
**30 distinct speakers**, and the same 30 in every language checked (hr, en, ja,
ar, verified). **A cloud voice is a SPEAKER, not a voice-plus-language.** The
language is a parameter of the request. That is the opposite of SAPI/OneCore,
where Matej *is* Croatian and Zira *is* English, and it is why one voice is
called the same thing for Croatian and for English.

**The design, Gordan's, and every part of it has a reason he gave:**

- **Settings → Speech and Braille keeps ONLY installed voices.** That page
  assigns per-language DEFAULTS, and a cloud voice may never be a default — so
  it has no business being offered there. The rule is enforced by the place not
  existing, not by a rule somebody has to remember.
- **A new group on the OCR and Translate tab: "Google Cloud Voices"** — the
  button that loads the credential, and the "use them" check. It goes there
  because that tab already holds every other service credential, and because it
  is the only one of the three candidate pages with room: measured, its content
  ends at y=352 of about 500, where Text Books' Speech group ends at 234 of 246
  and Properties' at 212 of 212.
- **The check IS persisted** — a reversal of the session-only rule agreed an
  hour earlier, and Gordan's reason is the better one: *"pošto je na neočekivanom
  mjestu ipak mora pamtiti"*. A switch a reader had to hunt for must not have to
  be hunted for again every launch.
- **With the check on, cloud voices appear in PROPERTIES only**, and a chosen
  one is remembered **for that book alone**.
- **`NoVoiceForm` never shows them, on purpose.** The reader reaching a book
  whose language nothing speaks is answering a question about *this book now*;
  the way to a cloud voice is to get past that and open Properties from the
  shelf. **This also kills an edge case by construction**: the "remember this
  voice for this language" tick being designed for that dialog can never create
  a per-language rule pointing at the cloud, because the cloud is not in the
  list to be picked.

**The credential is NOT a key, and the group cannot look like the others.**
Cloud TTS refuses API keys outright (*"API keys are not supported by this
API"*); it takes a **service account**, a JSON file of a few kB with a private
key in it. So: a button that LOADS the file, storing its **contents and not its
path** (Gordan keeps his on a custom drive and it must survive being moved), and
"check the credential" means minting a token once rather than asking a service
whether a string is valid.

**The session flag must be reachable from two places** — `SettingsForm` and
`PropertiesForm` each build their own catalogue through `GetVoiceCatalog()`.

**Open:** the tab is called "OCR and Translate" and that name stops covering its
contents; "Cloud" in the title would mislead the other way, since OCR is local.
Gordan names it.

**Phase 2 (superseded — the engine picker is gone, see the correction above):**
Settings → Text Books was a two-combo picker — "Speech Engine" (vendor +
architecture, e.g. "eSpeak (32-bit)", "Microsoft (64-bit)", "SAPI 5 (32-bit)")
filtering the "Voice" combo to that group. Backends now report a
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
the same nominal speed, so carrying the previous voice's numbers across a change
of engine or speaker is worse than useless. Picking a voice now shows/applies, in
order: **what this book was last read with using that voice → how that voice is
set up in Settings → the neutral default (100 %, 100 %, pitch 0)** — never the
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

## 8g′. The speech cache — pay once (built 2026-08-15)

Speech already made, kept beside the book as MP3, so a sentence bought from a
cloud voice is not bought twice. `SpeechCache` · `Mp3Encoder` (vendored
`libmp3lame.dll`, LAME 3.100) · `MpvClipPlayer` · `SpeechPrefill`.

**Keyed on the text that reaches the ENGINE** — `TtsReader.Spoken`, so after the
pronunciation dictionary — plus the voice. Change a dictionary rule and the key
changes, so the sentence is made afresh, which is what a reader who just rewrote
a rule expects. **Speed and volume are deliberately NOT in the key**: they are a
listening habit and they change, while the audio is an asset bought once. That
forced the playback move — `SapiWavPlayer` cannot change speed, mpv can, through
the same `scaletempo2` §6 already uses for audiobooks. Volume left the key with
it, free, because mpv has a volume property.

**Cloud voices cache by default; local ones only when asked.** A local voice is
free and faster than listening, so keeping it buys nothing for ordinary reading
and costs ~214 MB a book. There the cache is for the EXPORT.

**`SpeechPrefill` is Gordan's runaway look-ahead**: reading starts at once and
the rest of the book is made behind it, ~10× faster than listening, so a
nine-hour book is done in about three quarters of an hour. Its own path to the
service, never the backend the reader is listening through. **Deliberate, never
automatic** — it commits the reader to the whole book's cost at the moment they
press play, and the free allowance is ~2 average books a month (measured on this
library: 472 000 characters a book, 2.1 books per million).

### Measured, and the export is NOT just concatenation

| | |
|---|---|
| second render of a sentence | **10 ms** against 1 686 ms, no network |
| MP3 at ABR 64 | 14.1 % of the WAV, 54 kbps actual |
| encoding | ~480× real time at quality 2, 79× at quality 0 |
| mpv speed | 1.0 → 4.4 s, 2.0 → 2.1 s, 0.5 → 9.0 s on a 4.6 s sentence |
| prefill, second pass | 0 made, everything already there skipped at once |

**THE EXPORT TRAP, measured 2026-08-15 before it was built.** Laying cached
sentences end to end *plays* correctly — mpv reports 10.23 s for a file whose
pieces sum to 10.20 — but the result carries **one Xing header per piece**, and
the first one describes only the first piece: it announces **106 frames /
17 688 bytes = 2.54 s** for a 73 296-byte, 10.23-second file. mpv survives it by
noticing the byte count disagrees and rescanning. A player that trusts the header
would report two and a half seconds for a whole book and seek accordingly — and
the export exists precisely to be played OUTSIDE NBR.

So the export must strip each piece's Xing frame, walk the frames to count them,
and write one correct header at the front. Not built.

### The export is VERIFIED on a real book, and NBR's own figure is the wrong one

Gordan's eSpeak export, 2026-08-16 — *Isčezli svet*, 144 MB — walked frame by
frame with an independent parser:

| | |
|---|---|
| Xing headers in the file | **1** (the fault above was one per piece) |
| header claims | 5:33:19 |
| real, counted from 765 621 frames | **5:33:19 — the header is CORRECT** |
| format | 22 050 Hz, mono, 60 kbps average |

**Winamp's 333:14 was right and NBR's "5:56" was not**, which is the opposite of
what it looked like. That figure is the **estimated reading time** computed from
WPM (§8e, CPM = WPM × 6), not a measurement — it runs 6.9 % long here. Nothing
to fix in the export; worth knowing before anyone chases a duration bug that is
an estimate behaving like one.

### The look-ahead follows PLAYBACK, not the book being open (Gordan, 2026-08-16)

> *"Dok se svira troši se, dok se ne svira ne troši se."*

It used to start from `LoadTextBookPlayback`, so opening a cloud book began
buying the whole of it and a paused book went on spending with nobody
listening. It now lives in **`Form1.SyncPrefillToPlayback`**, called from
`SetPlayPauseState` — the one place that knows whether anything is sounding, and
already the home of the sound-card keep-alive (§10f) for exactly the same
argument. Nothing is lost: the look-ahead runs ~10× faster than listening so it
stays far ahead of the ear anyway, and a resumed pass skips what is on disk at
once.

**It is voice-aware now, which it was not.** Gordan switched a book to eSpeak
and the cloud look-ahead carried on — it had been started for the previous voice
and nothing ever asked again. The voice it was started for is remembered;
a change restarts it, or stops it dead when the new voice is a local one.

**And the info box parity he reported was a REAL bug, in neither look.**
`ToggleInfoBoxFocus` set `tbInfo.TabStop = true` on the way in with **F8 and
never restored it**, so after one press the box was a permanent tab stop in both
looks. Focus could then land on it — and §2's focus echo guard correctly blocks
every refresh while it is focused, because it must not change text under a
reader's cursor. What Gordan saw as *"classic shows Speech kept and it does not
advance"* was that box holding focus. It was not stale; it was focused. Restored
on the way out now, **after** focus has moved.

### The 32-bit host: a dead one must not be paid for once per sentence

Gordan's first eSpeak export "blocked" and left a player process behind; the
second, after a restart, went through. **Nothing was deadlocked.**
`Sapi5SatelliteBackend.Render` already had a 60-second deadline and it was
working exactly as written — but it is a minute **each**, and a book of five
thousand passages against a host that has stopped answering is **83 hours** of
perfectly correct waiting, which from outside is a hang.

Three timeouts in a row now mean the host is gone rather than slow
(`GiveUpAfter = 3`, `RenderingGaveUp`) — the same number and the same reasoning
as the translation chain's stand-down. The export then finishes and says how
many passages are missing (`Export.DoneShort`) instead of running until somebody
kills it. A successful render clears the count.

**The export's up-front estimate was a CLOUD rate applied to everything.** One
second a passage: announced 87 minutes for a book eSpeak recorded in about five,
out by seventeen times, before he had committed to it. Now 1.0 s for a cloud
voice and 0.15 for a local one — 0.15 rather than his measured 0.06 because
eSpeak is the fastest local engine and there is one measurement, not a curve;
being early is the safe direction and the progress window's own figure is
measured from the passages already done. The wording says which number it is:
*"that is how long the making takes, not how long the book is to listen to"*.

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
  is for. **Still open:** `.pef` support, and more languages/samples.

**The per-book override — the engine is in (2026-08-04), the UI is not.**
`BookData.RetranslateBraille(tableId)` reads the book's original braille file
again with a chosen table and replaces the reading text with the result;
`BookData.BrailleSourcePath` finds that original, or answers null for a book that
did not come from one. Almost none of it was new: `BrfParser.ParseBytes` already
took a table id and detected only when it was not given, `DuxburyParser.Parse`
already forwarded one, and the import already kept the original beside
`content.txt` — all three verified in code rather than taken from this file,
which had been wrong twice that day.

**It is an import operation wearing a settings dialog's clothes**, and that
decides the hard part. The table is spent when the text is written, so changing
it is doing the import again: the reading position, the bookmarks and the
percentage are offsets into a text that no longer exists, and they are reset
rather than carried. **Gordan's call (2026-08-04)** — a reader notices a wrong
table long before they start setting bookmarks, so the 99% case costs nothing,
and the 1% is owed a warning. That warning is to carry **"Don't show this
again"**: *"kroz par puta će se naučiti i isključiti"*.

**Measured on the misdetecting English sample**, three passes:

| table | chars | first line |
|---|---|---|
| `en-g2` | 19 576 | *How can a man be Born Again?* |
| `en-g1` | 18 198 | `H\246/ c a man ; Born Ag?` |
| `en-g2` again | 19 576 | identical to the first pass |

So it is **reversible** — the third pass reproduces the first byte for byte — which
is the property that makes offering the choice safe at all: a wrong pick costs a
re-read and nothing else. Pages survive (20), position and bookmarks come back at
nought, and a book that is not from braille answers `BrailleSourcePath = null`
and refuses.

**The table list is in, and it is two lists (2026-08-04).** Gordan: *"dodaj sve
tablice koje možeš dodati da imamo što veći izbor. Jezike koje nismo mogli
testirati, nismo mogli, zato nudimo mogućnost reloada."* That splits cleanly in
two, and measurement says it has to:

- **`BrailleTables.All` — what auto-detection tries — stays small.** Detection
  back-translates a sample through every table it is offered and scores the
  result, and the score only knows Croatian, English and French words. Measured:
  **10 ms per table**, so the whole catalogue would cost ~2 s per import, and
  — much worse — with a hundred unscoreable languages in the running one of them
  wins by accident. It grew by three: **EBAE contracted and uncontracted**
  (`en-us-g2`, `en-us-g1`) and **British contracted**, so nine.
- **`BrailleTables.Catalog` — what the READER may choose — is everything**, read
  out of the shipped tables' own metadata rather than listed by hand: the §10e′
  rule again, since a hand-written list drops the one entry someone needs and it
  surfaces on their machine. **148 tables, 113 languages, built in 13 ms** on
  first use.

Two filters, both load-bearing, and one exception that matters more than either:

- `#+type:literary` keeps out maths, chess, computer-braille and display tables.
- **`#+direction:forward` is excluded** — 52 literary tables say they go
  text→braille only, and back-translating with one yields confident nonsense
  rather than an error.
- **The curated set is always in, whatever its metadata says.** Our two Croatian
  tables are hand-written (§8i) and carry **no `#+type:` at all**, so the
  mechanical filter would have thrown out precisely the tables this project
  built. Verified by probe: all nine survive, ids are unique, and every entry
  round-trips through `ById`.

**The picker offers ONE language's tables, and it hangs off the language combo
that is already there** (Gordan, 2026-08-04: *"a zašto ne iskoristiti polje Jezik
koje se već koristi za TTS i samo mu dokrpati granu za tablice?"* — right, and it
saves a control). `BrailleTables.ForLanguage(code)` is the branch.

**Measured, and it is a short list almost always:** after alias resolution the
catalogue is **132 tables in 85 languages**, and **81 of the 85 have three or
fewer**. Croatian 3, French 2, Serbian 2, German 2, Portuguese 2, Vietnamese 1;
English 5, Danish 10 — the two worst. *(The dedupe took 148 to 132: liblouis
ships `.tbl` wrappers that are nothing but `include` lines round a real table, so
`en-us.tbl` and `en-us-g2.ctb` were the same translation offered twice under two
names. A table whose body is only includes is resolved to its target and the
curated name wins.)*

**A language's tables may be filed under a code nobody would guess** (checked
2026-08-04). Korean is `ko` and Vietnamese `vi` as expected, but Chinese is under
`cmn` and Hebrew under `phn`, so a book detected as `zh` or `he` finds nothing
and drops to the whole catalogue — which is the right outcome, but by accident
rather than design. **Thai has no literary table at all.** The occasion for
checking was Gordan reading a detection sweep and concluding liblouis had no
tables for Korean, Thai or Vietnamese; two of the three do, and a reader can
reach them through the picker.

**An unknown or empty language falls back to the WHOLE catalogue, deliberately.**
The language is detected from the text, and when the table is wrong the text is
gibberish — so the language can be wrong too. Measured, on this project's own
samples: §10g's two NALIS books are **English detected as French**. A picker
filtered to the detected language would offer them two French tables and hide
the English ones, which is precisely the case the feature exists for. Because the
filter hangs off the language combo, the reader changes the language and the
tables follow.

**The re-read fires on OK, not on the combo — and Gordan's reasoning beat mine.**
I argued for a separate button, on the grounds that arrowing through a combo
would otherwise fire a re-import per keypress. He pointed out the thing that
actually matters: *"Ako napravim re-read na posebnom gumbu tu mi Cancel više neće
pomoći ni ovako ni onako. Ako s druge strane odaberem tablicu u combu i ipak
odlučim kontra toga Cancel me još uvijek spašava."* **Cancel has real power only
while the action is deferred**, and deferring it to OK also disposes of the
arrowing problem, since nothing fires on change at all.

**BUILT 2026-08-04.** Properties → the text page carries a **Braille source**
group with **Input Braille Table**, in the slot the fake output table used to
occupy and captioned so the two can never be confused again. It appears only for
a book with a `BrailleSourcePath`; the visual group closes the gap otherwise.
Choosing a table only stages it — `RereadBrailleIfAsked` runs from `Persist()`,
after the book is saved, and warns through `ConfirmOnceForm` unless the reader
has switched that off (`[App] WarnBrailleReread`).

**`ConfirmOnceForm` is a new dialog and had to be.** `MessageForm.ShowConfirm` is
a real `MessageBox` on purpose — "a question is a notice too… always the real
thing" — and a system dialog cannot carry a check box. So it is built the way
`ArchivePasswordPrompt` is: ordinary controls, keyboard-reachable, the message a
read-only multiline TextBox rather than a Label (a reader driven by Tab never
visits a Label, and here the text is the whole dialog). Focus starts on the
question. **Ticking "don't show again" and then cancelling does NOT switch the
warning off** — that is a decision not to do this, not a decision to skip the
warning next time.

**The braille check box is gone, and with it `BookData.TextBraille` and
`AppSettings.Braille`** — the Settings braille group went too, since one dead
check box was all that was left in it. `OpensReadingWindow` is now simply
`TextVisual`, and `PushBrailleIfFocusLeft` gates on **the reading window being
open** rather than on a setting. One fact, three consequences: window open and
focus in the text, the reader brailles the control; window open and focus
wandered, NBR pushes the sentence; window shut, no braille. §8l had decided this
in words on 2026-08-01 — *"Braille output IS the reading window"* — and this
finishes it. Gordan, 2026-08-04: the check box *"je u naravi besmislen"*.

**A trap this nearly walked into, worth keeping.** `DialogSkin` attached each
group's help by POSITION — `"Hint.Text" + i`. That is safe only while every group
is always present, and the braille-source group is not. The visual group would
have inherited the braille group's help the moment it moved up a slot: the wrong
text under the right button, which is worse than no text. Groups may now name
their own key through `Tag`, and the two that move do.

**Verified after the surgery:** every new language key resolves and the warning
carries the table name; the whole library cold-loads byte-identical to before, so
dropping `TextBraille` moved nothing; and the re-read still gives *How can a man
be Born Again?* on `en-g2`, gibberish on `en-g1`, and the first result again on
the way back.
**Not seen by anyone yet** — the group, the dialog and the hint are all unmeasured
by eye, and go on §11's eyes-and-hands list.

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

### A Library action that changes a book on disk must hand back a FRESH BookData

Found 2026-08-14, chasing a book that had just been read by OCR and still made
no sound. Everything on disk was right — 2009 characters of text, `hr-HR`, a
Croatian voice installed and mapped — so the fault was in what the player
*received*.

`ReadSelectedBookNow` writes `content.txt`, then calls `LoadBooks()`, which
**rebuilds the shelf and can lose the selection**. The fallback was then the
`BookData` object built while `content.txt` was still empty, and that is what
went to `LoadBook`. The player was handed a description of the book as it used
to be.

**The rule: after writing to a book's folder, re-read it —
`new BookData(folder)` — rather than reusing the object you wrote through or
whatever `GetSelectedBook()` happens to return afterwards.** The same shape will
bite any future command that edits a book in place.

Two smaller ones from the same hunt, both worth knowing:

- **`b.Save()` writes the in-memory value, stale or not.** The book kept
  `TextChars=0` — what import recorded when it had no text — because nothing
  updated it. It self-heals on the next `EnsureTextInfo`, which is exactly why it
  went unnoticed, and until then the reading-time estimate is wrong.
- **`IsTextBook` is true for a book whose `content.txt` is EMPTY.** That is how a
  scanned PDF imported in bulk became a text book with nothing to say, and
  pressing Enter played silence — indistinguishable, to someone who cannot see
  the screen, from a book that has simply gone quiet. Hence
  `OcrImport.IsEmptyTextBook`, which catches it by the symptom.

---

## 8k. Two looks side by side (temporary, for the redesign)

> ### THE NBR DEFAULT LOOK IS CLOSED — 2026-08-10 (Gordan)
>
> *"Možeš zatvoriti i dio s dizajnom, barem ovaj za NBR default. Classic nek
> ostane još malo otvoren."* The new look — the player panel and the three
> sub-windows — is **done and not to be reworked**, on the same footing as §9's
> Library. Reopen only for a fault found in use, not for an improvement.
>
> What that closing pass fixed, all found on captures of the running player and
> the real dialogs rather than by eye:
> - the vertical seam down every key and the play disc (a gradient brush's tile
>   wrap at the end-fade's own edge);
> - square corners round every round bed, twice — the panel keys and then the
>   dialog buttons — both from a control filling its own rectangle with one flat
>   colour over a gradient background;
> - the key face lit from off to one side, so its two ends did not match;
> - the bed's light/shadow walls, which made every corner a different corner and
>   the bottom-right read as pinched: the direction now comes from a CLIP, and
>   then went away entirely at Gordan's call — **the bed is one flat dark all
>   round, and the lip is not to come back**;
> - one milled grip shared by the speed knob and the progress blade;
> - the glass: labels keep their colons, the time captions come from the line
>   instead of being invented, and they are no longer clipped by the tiles;
> - the dialogs: the `?` glyph fitting its key, the key clearing the combos,
>   Settings' pages on metal so their groups read as recessed rather than raised,
>   the EQ spin boxes matching the Speech page, and the Sleep Timer's rows with
>   room for their descenders.
>
> **Classic stays open**, deliberately — it is the untouched fallback that
> regular testing runs on, and §8k's rule that `ClassicTheme.Style` is empty so
> it cannot drift still holds.

> ### ONE LAYOUT PASS SERVES BOTH LOOKS — 2026-08-16
>
> Gordan, reformulating the whole classic job: *"Otvoriš npr. Settings, napraviš
> screenshot … i kompletno ga iskopiraš u klasičnoj formi. Buttoni ostaju
> buttoni, combo ostaje combo, text box ostaje text box, samo što nije crtan,
> šminkan i farban nego je classic koji izvlači stilove i boje iz windows teme.
> Naravno, sve u dimenzijama koje jesu na nbr default."*
>
> **`DialogSkin.Painting`** is that sentence in code. Every primitive
> (`Shell`, `AsKey`, `AsSticker`, `AsGlass`, `OnGlass`, `AsSwitch`,
> `StyleTabStrip`, `EnsureFonts`) now sets **bounds and structure
> unconditionally** and skips only the metal, the glass and the owner-drawing.
> So the six dialogs call the skin's `Apply` in **both** looks — the
> `if (BuildsOwnLayout) … else ClassicLayout.…` branch is gone from Settings,
> Properties, Go To, Bookmarks, Sleep Timer, Library and the archive password
> prompt.
>
> **Why this rather than maintaining the second layout:** `ClassicLayout`'s
> dialog half had already begun to differ — it kept the old always-on hint box
> where the new look has a `?` key — which is exactly the drift he was asking to
> remove. A classic path built by the same code **cannot be missing a control
> the new one has.** The dialog half is deleted; `ClassicLayout` is now the
> player only, which genuinely differs (a drawn ring has no classic equivalent;
> his answer was five square buttons in a cross).
>
> **What classic gets that it did not have:** the `?` help keys (an ordinary
> `Button` all along — only its colours were the skin's), the Library's
> Load/Close naming, and the new look's own dimensions and places.
>
> **What differs, by design:** the window keeps its **title bar and border**
> (there is no drawn power key to close it with), and the type is the Windows
> theme's rather than 12 pt Segoe — so labels and combos come out a few units
> shorter and the value column, which is measured from the real text, moves
> left with them. `Shell` returns an **unparented** `DialogCanvas` so callers'
> `Wells.Add`/`Rebuild` stay inert without a null check at every site;
> `DialogCanvas.Active` is set only from `OnPaint`, so it stays null exactly as
> before. `MessageForm` still falls back to a real `MessageBox` under classic.
>
> **Measured, because this touches code §8k closes.** A harness dumps every
> control of all six dialogs as `path type name x y w h tab`, with every tab
> page selected first (§10c's trap):
> - **The new look is byte-identical to the build before the change** — diff
>   clean across all six dialogs, in both the audio and the text Properties page.
> - **Structurally, classic differs from the new look by one line per dialog:
>   the `DialogCanvas`** — the painted metal, and nothing else. Same controls,
>   same tree, same order.
> - A collision/overflow sweep inside every container reports **11 for classic
>   against 15 for the new look, and classic's are a strict subset** — so every
>   one of them predates this and is in the look Gordan already accepted.
>
> **Not covered by the sweep:** a **hybrid** book's two-tab Properties page —
> there is no hybrid in the library to open. It uses the same primitives plus
> the per-page canvas, which is guarded, but it is unverified.
>
> ### Volume, Speed and Position were each on the classic panel TWICE
>
> Found by Gordan's describer on the new classic player: *"pozicija je ispisana
> dvaput"*. It is right, and it is wider than reported — **all three value rows
> did it**, and it dates from the **initial commit**, not from this work.
>
> Form1 writes one finished, self-describing string — `"Volume: 80%"`,
> `"Speed: 1.0x"`, `"Position: 00:00:41 / 07:58:38"` — into the field **and into
> the caption label above it** (`lblVolume.Text = text`, `lblSpeed.Text = text`,
> `lblProgress.Text = posText`, at six call sites). Two identical lines, one
> above the other.
>
> **The field is the half that must keep the prefix**, because of §2: the arrow
> keys make a reader speak the focused field's own line, so the line has to name
> itself or the reader hears a bare number. So the LABEL is what goes —
> **hidden, not re-captioned**, since "Position" standing over "Position:
> 00:00:41 / 07:58:38" is still saying it twice. `ClassicLayout.ApplyPlayer`
> hides Volume, Speed and Progress and `PlayerBoxes` no longer reserves a row
> for them. **`SeekLabel` stays** — its combo reads "5 minutes" and names
> nothing.
>
> **Why it went unseen for the life of the project:** the new look hides all
> four labels while it draws its own legends (`NewPlayerSkin`), so it was only
> ever visible on the classic panel, which nobody had looked at.
>
> **There is NO slider on the player, and there never was one** — checked
> against every `panelBottom.Controls.Add` call: no `TrackBar`, no
> `ProgressBar`. What the describer read as "duga vodoravna traka (slider)" is
> the Position box itself, a read-only `TextBox` 350 wide and 24 tall with a
> border. The new look's draggable progress blade is **painted on the canvas**,
> not a control, so under classic there is nothing to drag and the position is
> read-only. Whether classic should gain a real seek bar is **Gordan's call and
> not made** — there is room (the boxes end at y=370 of a 480 panel), but §8k
> already reasons that a slider here must not consume the arrows, which are
> global.
>
> Checked with a harness over `ClassicLayout.PlayerBoxes`, which is a **pure
> function of the panel size** and so needs no `Form1` — instantiating the real
> player drives mpv and loads a book, and hangs a harness. 18 boxes, **no
> overlaps, nothing off the panel**.
>
> ### The hybrid page passes — and the sweep found a real fault in the NEW look
>
> The two-tab hybrid Properties page, left unverified above, has since been
> measured on a real hybrid (`NBR Library\S13304`, 84 audio files with
> `content.txt` + `sync.map`). **It lays out**, and classic reports **fewer**
> problems than the new look on it — 2 against 6.
>
> That gap is the finding. **The five EQ spin boxes overlap each other by 4
> units, on BOTH Properties pages, in the new look only.**
>
> | | asked for | actual | pitch |
> |---|---|---|---|
> | `PropertiesForm.EqBand` | `new Size(90, 24)` | **29** at 12 pt Segoe | `EqRowH = 25` |
>
> **A `NumericUpDown` forces its own height from the font** — it ignores the 24
> it is given. So the pitch has been 4 units short of the control ever since the
> box was enlarged. The enlargement itself was right and is Gordan's own
> (*"controls in EQ are too squeezed… in the speech part they are more
> relaxed"*); what was missed is that the ROW did not grow with the box.
>
> **Classic is unaffected**, and by accident rather than design: the theme's
> smaller type makes the box 23 tall, which clears the 25 pitch.
>
> **It cannot be fixed by widening the pitch alone — the cell is too small.**
> Bands run 36, 61, 86, 111, 136 and the last ends at 165 inside a 166-unit
> cell, so they *fit* only because they overlap. At a pitch of 29 the last would
> end at 181. The cell needs about **187 and has 166 — short by ~21**, and has
> been since §8d took the EQ from three bands to five while §10b's cell was
> sized for three.
>
> **NOT FIXED — it needs a design decision, and §8k closes this dialog.** Three
> ways, for Gordan:
> 1. **Two columns inside the EQ cell** (3 + 2). Fits with room to spare and
>    keeps the box size he asked for; the cell is 308 wide and the value column
>    sits at x=162.
> 2. **Give the FORM page the per-row measuring the hybrid page already has** —
>    rows as tall as what stands in them, so EQ's row grows and the two above
>    give up what they are not using. The form page still uses fixed
>    `StageRowY`/`StageH`.
> 3. **Leave it.** Four units is a hairline and he has been using the dialog.
>
> **Method note, and it is a correction:** this was first reported here as *"the
> audio page is fine, both looks agree"*. That was read off a **truncated**
> harness dump — `-Context 0,10` cut the output at ten lines and the EQ rows
> were below the cut. The full count is **15 for the new look against 11 for
> classic**. A sweep is only as good as the part of it you actually read.
>
> **THE EQ WAS NEVER LAID OUT 3 + 2.** Gordan asked whether it had been since the
> bands went to five. It has not, and three things say so together: `EqBand`
> takes only a `y` and hard-codes `x = 162`; the runtime rectangles are one
> column at 36, 61, 86, 111, 136; and the only commit ever to touch `EqRowH` is
> `a2d9f91`, the one that enlarged the boxes. §8d's own note agrees — *"two more
> rows is a layout change"*. **Two columns was proposed as the repair and never
> built.**
>
> **What 3 + 2 costs, measured, because it is not free.** A column needs a
> caption plus a 90-wide spin box. The cell's usable inner width is ~272 on the
> hybrid page, so a column gets ~136 and the caption ~46 — and the captions are
> "200 Hz", "800 Hz", "1.8 kHz", "3.5 kHz" and **"5 kHz and above"**, the last of
> which was deliberately given room (§ the comment in `EqBand`) so it would not
> read as a different band. So 3 + 2 needs the visible captions shortened —
> `"5 kHz+"` is the conventional shelf notation — with the full wording kept in
> `AccessibleName`, which is where a reader gets it anyway. **Not built: that is
> a caption change and Gordan's call.**

### The tab order is the same in every theme (Gordan, 2026-08-16)

> *"sredi tab order na classic, sve mora biti identično u svim temama. Primijeni
> i na druge prozore ako se razlikuje."*
>
> **This reverses what `ClassicLayout` used to say.** It had argued that §5's
> column-major order should stay because every accessible name and shortcut was
> learned against it. Overruled, and rightly: a reader who learns one look must
> not have to relearn the other, and the shortcuts are untouched either way —
> only the order of the stops changes.
>
> **Measured first, so only the one that differed was touched. The DIALOGS were
> already identical** — comparing name + `TabIndex` + `TabStop` across both
> looks, the only line that differs is the `DialogCanvas`, which has
> `TabStop = false` and `AccessibleRole.Graphic` and is not in the ring at all.
> That is what running one layout pass for both looks bought.
>
> **Only the player differed**, and it now takes the new look's ring key for key
> (`ClassicLayout.SetTabRing`): Play/Pause 0, Forward 1, Back 2, volume up 3,
> down 4, seek step 5, speed 6, position 7, then the eight commands 20–27. The
> volume READOUT and the INFO BOX are `TabStop = false`, both for §8k's own
> reasons — the volume keys already speak on every step, and the info box is
> reached with **F8** so the arrows never have two owners. The F8 path sets
> `TabStop` back on while focus is inside it and is look-independent, so it
> works here unchanged.
>
> **The command COLUMN was re-ordered with it**, and that was the hidden half.
> `p.Left` is `{Library, Settings, Timer, Help}` and `p.Right` is
> `{Properties, GoTo, SetBookmark, ManageBookmarks}` — the order `BuildUI`
> happens to declare them in, not the order they are read. Classic walked those
> arrays straight through and so put Timer third and Properties fifth, where the
> new look reads Library, Settings, Properties, Help, then Go To, Bookmark,
> Bookmarks, Timer. `ClassicLayout.Command()` now picks the same eight the same
> way `NewPlayerSkin.LayOutButtons` does; **the two lists have to be read
> together.**
>
> Checked by `ClassicLayout.PlayerTabRing()`, which is plain data for the same
> reason `PlayerBoxes` is — nobody here can look at a tab order. It matches §8k's
> documented sequence, has no duplicate index, and carries the two non-stops
> explicitly rather than by omission.
>
> ### THE RULE, in Gordan's words (2026-08-16)
>
> > *"Kako se ponaša skin, tako se ponaša i classic. To je pravilo za sve."*
>
> Not a decision about one window — the standing rule. Anything the two looks do
> differently is drift unless there is a reason written down beside it.
>
> **`MessageForm` was the last of it, and the description of it here was
> imprecise as well as out of date.** `ShowInfo` and `ShowConfirm` are a real
> `MessageBox` in **both** looks and always were — a notice is an event, not
> material — so they were never the difference. Only **`ShowHint`** and
> **`ShowContinue`** had two paths. They now have one, and `ShowContinue` gains
> by it: Windows cannot relabel a real message box, so the classic path had been
> settling for **OK / Cancel** where the question asked for **Continue /
> Cancel**. Both looks now ask it in the same words.
>
> **`Shell` fixes the border under classic** (`FormBorderStyle.FixedDialog`).
> The skinned window is borderless at a size worked out to the unit, so nothing
> can survive being dragged wider. Most forms already ask for `FixedDialog`
> themselves; the two that did not — **the message box and the Library** — came
> out `Sizable`, which is WinForms' default and nobody's decision. All six
> dialogs now report `FixedDialog` at exactly the skin's sizes.

### The EQ is 3 + 2 (Gordan, 2026-08-16)

The overlap above is fixed, and by the shape he expected rather than by nudging
a number. `PropertiesForm`: five bands in **two columns, 3 + 2**, pitch 33.

- **Why one column could never be made to work.** The pitch cannot simply grow:
  at 29 the last band ends at 181 in a cell that is 166 and **cannot get any
  taller**, because it shares row three with the loudness cell and the skin pins
  that row's bottom at 570. Two columns need three rows instead of five and end
  at 131, with 35 to spare — and fit the hybrid page's narrower cell too.
- **The spin box KEEPS its 90 × 24**, which is the whole point. That size is
  Gordan's own (*"controls in EQ are too squeezed, number box and the arrows"*),
  and shrinking it to buy the second column would have put back exactly what he
  objected to. Measured after: the spin arrows are 16 × 27, the same as the
  Speech page's.
- **The CAPTION gave way instead** — `ShortBandLabel`: **200 · 800 · 1.8k ·
  3.5k · 5k+** on screen, with the full "5 kHz and above" kept on the spin box's
  `AccessibleName`, which is where a screen reader takes it. This is §10d's rule
  exactly, and the short form is the **familiar** one here rather than a
  compromise — every graphic equalizer ever built labels its bands that way.
  Built from the frequency, never by cutting the localized phrase down, so a
  translation cannot break it.
- **A second, older overlap fell out of it**: the stage's enable check runs the
  full width of the cell and ends at 38, so a first band row at 36 ran under it.
  Bands start at 40 now. It had been there all along, hidden among the
  band-on-band overlaps.

**Measured, per page, in both looks:**

| | before | after |
|---|---|---|
| audio Properties, new look | 15 | **10** |
| audio Properties, classic | 11 | **10** |
| hybrid Properties, new look | 6 | **1** |
| hybrid Properties, classic | 2 | **1** |

**The two looks now report the same count on both pages**, and what is left is
the known artefact of the single-page path (controls moved onto the form while
the hidden `TabControl` still fills the client area) plus one `?` key.

**The new look's other five dialogs are still byte-identical to the build
before any of this work** — re-checked after every change in this run, since
that is the invariant §8k's closure depends on.

### `FlatStyle.Flat` MADE JAWS READ THE COMBO OFF THE SCREEN (2026-08-28)

Reported from beta 1, on Win 11 and two Win 10 machines: arrowing through a
**closed** combo, JAWS said **"blank"** before each item — clean going down the
list, and from the first press back **up** it said it every time and never
stopped. Every combo, every window, **and only on the NBR look**.

**The cause was one line, set on every combo by the skin and only when it
paints**: `cb.FlatStyle = FlatStyle.Flat`, in `DialogSkin.OnGlass` and
`NewPlayerSkin.LayOutCombo`. That is exactly why classic was clean — both
methods return before it. Flat takes the drawing away from Windows and hands the
whole control to WinForms, **and JAWS then reads the combo off the SCREEN rather
than off the control**.

**Fixed by splitting the job:** `DialogSkin.PaintComboItems` draws the ITEMS
(`OwnerDrawFixed`, glass background, `Lit` text, a darker band for the selected
row) and the BOX goes back to Windows. The player's seek combo already had item
drawing, so it only lost the one line. Same colours; the frame and arrow are now
the system's, which Gordan accepted (*"vizualno ne mogu procijeniti no nije ni
toliko bitno"*). Measured before and after on the Shift+arrow path: **65–70 ms
either way**, so nothing slowed.

**The finding that outlives the bug: neither accessibility channel could see
it.** MSAA (`SetWinEventHook` + `AccessibleObjectFromEvent`) and UIA
(`System.Windows.Automation`), both recorded from outside the process, are
**identical with the fault and without it**, and neither ever carried an element
with an empty name. **JAWS's own speech history does not record the blank
either.** When a reader reports something no event stream shows, the remaining
channel is the screen — and the way in is to vary how the control is DRAWN.

**What proved it:** five combos in one throwaway program, differing in one thing
each, judged by ear over three passes. Standard, Standard with the theme's
colours, and Standard with our owner-drawing are all clean; **Flat is not**, and
Popup was not once. The mechanism showed itself the moment the test window had a
title of its own: JAWS spoke `Combo test 7 — Fl` — the window title, **cut off
mid-word**, the way text read off a narrow caption bar is. NBR's skin leaves no
text behind the box, so there was nothing to read and it said "blank".

**SIX WRONG DIAGNOSES CAME FIRST, each disproved rather than argued away** —
kept so they are not re-tried: owner-draw · nesting in panels · the skin as such
(classic shows the same MSAA event) · our UIA notification, early and late ·
`SpeakOnChange` and the NVDA client · the manifest and comctl v6 · and the .NET
accessibility switches.

**And a red herring worth naming.** Every NBR combo emits an extra
`EVENT_OBJECT_SELECTION` whose objid is a **process-wide counter** and whose
object oleacc will not hand over. Real and reproducible — it comes from WinForms'
4.7.1+ accessibility improvements, and a bare `csc` build reproduces it the
moment the `TargetFramework` attribute is added — and
`Switch.UseLegacyAccessibilityFeatures.3=true` removes it. **It was tried in the
shipped `.exe.config` and the blank survived.** A finding that is genuine,
measurable and irrelevant is the most expensive kind there is.

**The instruments are kept in `D:\Player\JAWS test\`**: `MsaaSpy.exe`
(`<pid|exe>`, plus `--send "{TAB}|{DOWN}"`, `--for <seconds>`, `--drive N`),
`UiaSpy.exe`, `Snimi.cmd` for a 120-second recording while a human drives, and
the ComboTest programs. `MsaaSpy --send` doubles as a way to walk a window nobody
here can see — the FOCUS lines name every control in tab order.

**Not this bug, and confirmed unchanged:** JAWS says **"selected"** before each
Shift+arrow announcement. That is its own reading of the key (§6), it predates
all of this, and NVDA does not do it — which is most of why the same step feels
slower under JAWS.

> ### High contrast is the DEFAULT, not a lock (2026-08-03)
>
> Gordan asked the question that fixed this: *does a reader with a high-contrast
> theme lose the right to choose?* They do not. High contrast is a setting for
> the SYSTEM, not an instruction to this app — someone may run it for one reason
> and still want NBR to look the way they like.
>
> So the stored theme has three values. **`follow`** is the default and the one
> a fresh install carries: Windows decides, and under high contrast that means
> the system-colours layout. **`classic`** and **`new`** are deliberate choices
> and are honoured whatever Windows is doing — `UiTheme.chosenByUser` is what
> tells them apart, and it is the only thing that lets an explicit choice
> outrank high contrast.
>
> **This closes a real gap, not just a policy one.** `UiTheme.Apply` had always
> yielded to high contrast, but that is only the COLOURING pass; the layout
> skins are called separately (`if (UiTheme.Current.BuildsOwnLayout) …`) and had
> no such check, so the new look painted its own dark glass over the user's
> chosen scheme. Resolving `follow` to the classic theme under high contrast
> fixes it at the source — the skins are simply never reached.
>
> **Letting an explicit choice win is only safe because it is reversible without
> seeing**: the keyboard and the screen reader are untouched by any of this, and
> F2 opens Settings from anywhere. A choice that could not be undone would be a
> trap rather than a freedom.
>
> **Measured with a contrast theme actually switched on** (Gordan turned one on,
> 2026-08-03). `follow` and empty resolve to `ClassicTheme` and the skins are not
> reached; `classic` likewise; `new` stays `NewTheme` and keeps its own look.
> End to end, the Settings dialog under `follow` comes up in the system's own
> colours — dialog and page both (45,50,54), the scheme's `Control` exactly —
> while under `new` it is the skin's metal and glass.
>
> **The same run settled a separate question.** Windows dark mode does NOT reach
> a WinForms app through `SystemColors`: with Windows dark, `SystemColors.Window`
> was white; with Windows light, white; with a contrast theme, (45,50,54). So
> "follow the system colours" buys high contrast and nothing else — a Light/Dark
> theme has to read `HKCU\…\Themes\Personalize\AppsUseLightTheme` and carry its
> own palette.
>
> **Seen, and the worry was unfounded**: under `new` + high contrast a group
> reports a white `BackColor` against pale text, but the stickers are painted by
> the canvas and never filled from it. Gordan's screenshot shows dark groups with
> readable text. Reading a property is not the same as seeing a pixel.


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
  `AccessibleName` — the screen reader still says "Postavi knjižnu oznaku, F5".

  > **CORRECTION, 2026-08-22: the built column is 108, not 91, and legends are
  > drawn at 12 pt only.** The 91 above is the figure this section settled
  > BEFORE the skin existed; what the skin actually does is
  > `NewPlayerSkin.CellW = 108` — the same width as the key itself — handed to
  > `DrawString` by `PaintLegends` with no inset, at `FLegend`, which is
  > `new Font("Segoe UI", 12f)`. So the 14 pt column never applied to a legend,
  > and 17 units of headroom were being left on the table.
  >
  > **It cost two unnecessary shortenings, both since undone.** Serbian
  > Cyrillic's `Библиотека` measures 93 and had been replaced by `Књиге` on the
  > strength of the 91; Russian was about to be given the same treatment. Both
  > now carry the real word, and `tools/sr-cyrillic.pl`'s override is gone.
  > Measured across all six shipping languages, the **widest legend is 93** and
  > English's own is 85.
  >
  > **AND THE INSTRUMENT WAS WRONG TOO, which is the deeper half.** With 108 in
  > hand I set a rule of "full word if `MeasureString` says 100 or less" and
  > wrote off `Einstellungen` (102), `Eigenschaften` (106) and `Einschlaftimer`
  > (106). All three fit. `MeasureString` **pads**, so it reads high — while
  > `DrawString`'s own layout box carries side bearings, so a word whose ink is
  > **101 can still ellipsise inside 108** (`Knjižne oznake` does exactly that).
  > The two disagree in both directions, so no threshold on `MeasureString` can
  > be right.
  >
  > **`tools/check-legends.cs` asks the only question that cannot be wrong**: it
  > draws each legend the way `PaintLegends` does — same font, same rectangle,
  > same `EllipsisCharacter` trimming — once into the real 108-wide cell and
  > once into one far too wide to trim, and compares the ink. Narrower in the
  > real cell means the ellipsis bit. Run it on any new language file.
  >
  > **`tools/check-captions.cs` asks the same question of the DIALOGS**, where a
  > control sized by the skin has no equivalent of the panel's 108-unit key.
  > Its first run, 2026-08-23, found two captions that had been cut off since
  > they were written: Esperanto's `Preterpasi la prilaboradon` and Ancient
  > Greek's `Τὴν θεραπείαν παρελθεῖν`, both 182 units in a cell with 176.
  > Neither is a language anyone here reads, which is exactly why a machine has
  > to ask. Add a row to its `Cells` table to cover another control.
  >
  > Measured that way, **every legend in all six shipping languages fits**, the
  > tightest being `Einschlaftimer` and Spanish `Temporizador` with 12 units
  > free. Only genuinely long phrases clip — `Knjižne oznake`, `Lesezeichen
  > setzen`, `Postavi knjižnu oznaku` — which is why Croatian's `Označi`/`Oznake`
  > split was right all along. German now carries its real words
  > (`Einstellungen`, `Eigenschaften`, `Lesezeichen`, `Einschlaftimer`), with
  > `Markieren` on the Set Bookmark key because German spells the singular and
  > the plural of `Lesezeichen` the same way and two keys cannot share a legend.
  >
  > **Read the constant, not the brief; and measure the thing that reaches the
  > screen, not a proxy for it.** A design number written down before the code
  > was built outlived the code that superseded it by four weeks, and the first
  > two attempts to correct it were made with an instrument that could not see
  > the answer.
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
  of the tab order by agreement — it is reached with **F8** (twice to put focus
inside it, a third press or Escape to leave).

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
  (year) · format · voice + speed. **Part x/y and the per-part times are dropped**:
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

### The governing requirement: one place, three channels (Gordan, 2026-08-04)

> *"Ono što treba postići jest da se tekst koji se trenutno izgovara TTS-om,
> tekst koji je highlightan na ekranu i tekst koji se vidi na brajičnom retku
> poklapaju. Poanta cijele priče je da korisnik čuje, vidi i pipa istu stvar."*

If the TTS says **"Ana voli Milovana"**, that is what is highlighted and that is
what stands on the display. This is the requirement everything in this chapter
serves; a braille transport that cannot hold it is not worth having.

**Already true, and built deliberately — do not "improve" it without reading
why.** All three hang off ONE position. `UpdateReadingSurface` works out `start`
(from the TTS for a text book, from the audio clock through the sync map for a
hybrid) and everything follows from it: caret, highlight, scroll, braille. There
is no second source that could drift. And **the look-ahead does not move the
position**: `index` advances only in `TtsReader.OnCompleted`, i.e. when the
engine reports the previous sentence *finished*; `PreRender` of sentence n+1 is
a hint to the backend, not an advance. `PositionChanged` is raised AFTER the
utterance is handed over, so it means "this sentence has started". Those two
orderings are what closed the stolen sentence-beginnings and the queue growing
behind the narrator.

**Three gaps, found by reading the code on 2026-08-04:**

1. **High contrast had the eye switched off** — the highlight was skipped
   entirely rather than recoloured, so a high-contrast reader had two channels
   of three and nothing said so. Gordan runs high contrast. **Fixed** (5fe7713):
   the theme's own `Highlight`/`HighlightText` pair is painted there.
2. **Braille and the eye need not show the same SPAN, and this is structural.**
   The caret is placed at the sentence's START, so a focused surface is brailled
   by the reader as the LINE containing that start — while an unfocused surface
   gets the whole sentence as a `brailleMessage`. Crossed with the two highlight
   modes that is four combinations, of which only two agree:

   | | highlight = line | highlight = sentence |
   |---|---|---|
   | surface focused (reader brailles the caret) | agrees | disagrees |
   | not focused (`brailleMessage`) | disagrees | agrees |

   A sentence spanning two display lines gives the finger only the first.
3. **The grain is a whole sentence.** Nothing moves inside a long one. That is
   not drift — all three are equally coarse — but word-level travel would need
   SAPI's word-boundary event, which is wired nowhere today.

**OPEN, and explicitly NOT decided (Gordan, 2026-08-04): does braille follow the
highlight mode, or does the highlight follow braille?** He declined to choose
from the desk — *"to se isto mora provjeriti u praksi. Treba naći model koji će
staviti sve u sync."* So the model is to be found by trying it on a display, not
argued out here. The proposal on the table was that the SENTENCE become the
shared unit of all three and "line" stay a purely visual preference; it is a
proposal, not a decision. Whatever is chosen, **the deep test below decides
it**, and it must be run on NVDA and JAWS separately.

**Offered and not yet built:** a probe that MEASURES the sync instead of feeling
it — logging, per step, the spoken text, the text actually under
`markStart`/`markLength`, and the text pushed to braille, and asserting the
three are equal. It covers the two channels NBR fully controls, needs no display,
and turns "we hope it is in sync" into a number. What it cannot cover is whether
what the reader PUTS on the display matches what was sent — that stays with the
deep test.

### The braille transport — the hard constraint

**No display drivers.** Too many vendors, series and models; the binary would
balloon and some vendors charge for driver access (Gordan). **BrlAPI (BRLTTY)** is
the real universal answer — output *and* input, including routing keys, ~90
display families.

> **CORRECTION, 2026-08-04.** This section used to call BrlAPI "the wrong bet on
> Windows: two cannot fight over one device". The device really does have one
> owner, but the conclusion was wrong, and Gordan pushed back on it. **Not
> fighting over the device is what BrlAPI is FOR:** *"An essential purpose of
> BrlAPI is to manage concurrent access to the braille display between the
> brltty daemon and applications, managed per Tty."* Clients stack as a pile of
> sheets — brltty at the bottom, each client above. A client that takes its tty
> gets the cells, and keys it does not claim fall back down as brltty commands.
> **NVDA is already such a client**: it ships a BRLTTY braille driver, and NVDA
> 2026.1 updated its BrlAPI to 0.8.7 (#18657), so the client is current, not
> legacy. The shape is `display → BRLTTY → BrlAPI → {NVDA, NBR}`, with no screen
> reader switched off.
>
> **What is genuinely unknown is Windows, not sharing.** BrlAPI's whole
> concurrency chapter is written for Linux — VTs, X11, `WINDOWPATH` — and says
> nothing about what a "tty" is on Windows. That is to be TESTED, and this file
> should not assert it either way a second time.
>
> **Cost of entry, from BRLTTY's own Windows page:** the BrlAPI service is
> installed by `enable-brlapi.bat`, which "should be run by a user that has
> administrative privileges", and USB access goes through LibUSB-Win32 or
> libusb-1.0/WinUSB with an `.inf` to install per device. So it can be bundled
> (BRLTTY is LGPL 2.1+, and a separate process makes the obligation simple) but
> it **cannot be made invisible** — and swapping a display's USB driver can take
> it away from NVDA's own native driver, which makes this a change to the user's
> whole braille stack rather than an addition to ours.
>
> **Nothing here is decided until the deep test below.** If a focusable control
> already gives panning and routing through the screen reader, BrlAPI buys very
> little for that price.
>
> Method note: the NVDA version was first read by grepping `changes.html`, which
> lists every release, and a mention of "NVDA 2024.1 and above" was taken for the
> installed version. It is 2026.1.1. Read `nvda.exe`'s `VersionInfo`.

### JAWS is not symmetric with NVDA (checked 2026-08-04)

Both readers are on Gordan's machine — **NVDA 2026.1.1 and JAWS 2025**
(`jfw.exe` 2025.2508.120.400) — so the deep test can be run on both. It has to
be, because the two are not the same bet.

**The API route is a dead end on JAWS, and the code already said so.** FSAPI is
installed (`…\Shared\FSAPI\1.0\FSAPI.dll`, both bitnesses) and the COM object
`FreedomSci.JawsApi` is registered, so the API is *there*. But its surface is
`RunFunction(BSTR FunctionName) as Bool` and `RunScript(BSTR ScriptName) as
Bool` — **a name, with no arguments** — plus `SayString` for speech and nothing
for braille. JAWS's `BrailleString()` lives in the scripting language, reachable
only from a script. So arbitrary text to the display means shipping a script
into the user's version-specific JAWS settings folder AND having that script
fetch the text itself, since we cannot pass it. Exactly what
`NvdaController.cs:118` predicted; treat it as settled.

**On BRLTTY, the either/or that was WRONG for NVDA is right for JAWS.** NVDA
ships a BRLTTY driver and keeps its BrlAPI current; JAWS does not — it uses
vendor drivers. BRLTTY's Windows page mentions JAWS only under
`--release-device`, which is **taking turns with the device, not BrlAPI
multiplexing**. `display → BRLTTY → {JAWS, NBR}` does not exist.

**The risk that matters, and it is specific: the reading surface is a
`RichTextBox`.** The whole braille bet is that a focusable control gets brailled
and panned by the reader itself — which needs no API on either reader, and is
why the surface choice suddenly matters. But `Form1.cs:1527` records that the
info box was **deliberately moved OFF a RichTextBox** because *"JAWS handles
rich edit controls specially and kept re-reading the box's content when tabbing
to neighboring controls"*. The surface has to stay rich — colours and highlight
need it. So the JAWS half carries an already-observed hazard the NVDA half does
not, and **an NVDA result does not transfer to JAWS.**

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

**Routing keys, on hardware at last (Gordan, 2026-08-08) — half proven.** With
NVDA set to report the character under the cursor, a routing key press speaks the
character it lands on. So the chain display → reader → our surface's text is
real, which is the half this section predicted and could not test.

**The other half is still open, and the distinction matters.** That NVDA answers a
routing key says nothing about whether NBR sees it: the surface polls for a caret
it did not move and logs `ROUTED to <offset>`, and no one has confirmed that fires
or that the offset lands where the finger was. Proving the input path is not the
same as proving our use of it, and only the first has evidence.

**A display's Space does drive playback, inconsistently.** Bare keys need no
virtual modifier (see below), so this is the easy case and ought to be reliable.
Unchecked candidates: focus not on the surface at that instant, the reader eating
it in browse mode, or the reading window's own key forwarding. **Gordan asked for
this to be recorded, not acted on** — it is an observation, not a diagnosis.

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

  **Confirmed again 2026-08-04, on the build that fixed the chunk bugs (§10h).**
  Gordan: the text now changes on a seek and keeps refreshing as far as he could
  follow voice and text together — so that half is good — but *"čitač ne lovi
  automatski fokus, morao sam to pratiti ručno"*. The retry is therefore still
  not winning, and this is now the **last thing standing between the reading
  window and being testable**, because every other braille question is asked of
  a window somebody is standing in.

  **His follow-up question is the right one, and it has a two-part answer:**
  *would the text reach braille at all if the reader is not following what is
  shown?*
  - **As a surface to live in — no, and that is structural.** The display is fed
    by the screen reader tracking FOCUS. Focus on a player key means the display
    shows that key ("Pause, Space"), not the book. No amount of work on our side
    changes that; it is the whole reason §8l says the surface has to be where
    focus *lives*.
  - **As a transient line — yes, and this is new as of the same day.**
    `PushBrailleIfFocusLeft` exists for exactly this gap: focus off the text, so
    NBR pushes the current sentence to the display itself. It now obeys the
    book's braille switch (§11), so it is on only if the reader asked for it, and
    it is NVDA-only — JAWS has no public braille call.
  So a reader whose focus wanders keeps a sentence at a time and loses panning,
  routing keys and the rest of the book. **Which of those two he actually
  experiences is one of the things the deep test has to report**, and it cannot
  be settled from here.

Also open: **hybrid sync cannot be judged by ear.** Gordan tried the French and
the Darwin and could not tell whether narrator and text stay together. This needs
someone sighted watching the caret, and no amount of instrument work replaces it.

### What is left on the three outputs (Gordan's list, 2026-08-01)

1. ~~**Make braille output open the reading window**~~ **— DONE** (`186dfff`).
   `TextBraille` persists beside `TextVisual` / `TextVisualMode`, and
   `BookData.OpensReadingWindow` is what the player now tests, so either switch
   brings the window up. Focus already landed in the surface on `Shown`, so
   nothing was needed there. (`TextBrailleTable` stood beside it until
   2026-08-04, when it was removed as dead — see §11.)

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

   **The table combo that stood beside the check box is GONE (2026-08-04), and
   the question it left open is answered: NBR does not translate cells.** The
   combo said it described a text book being written *out*, as against
   `book.BrailleTable` which back-translates a `.brf` being *read* — same
   library, opposite directions. But the outward direction does not exist:
   `LibLouis.cs` binds back-translation only, and it should stay that way,
   because the screen reader translates what it puts on the display using the
   table in its own braille settings. Ours could only ever disagree. Worse, the
   combo **wrote `book.BrailleTable` anyway** on save, erasing the real import
   table. Full account in §11.
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

### The visual output, first real report (Gordan, 2026-08-10)

Tested across a good many books. **Mostly right and in sync**; what unevenness
there is he puts at *"dobrih 90 %"* down to how the books were produced and the
rest to our parsing. Testing continues.

- **FIXED: the subtitle mode's two rows were pinned to the BOTTOM of the glass.**
  They are centred now. The original reasoning — "where subtitles live on a
  picture" — does not survive the window it is in: this is not a picture with
  captions under it, it is a frame containing nothing but those two rows, so
  pinned low they read as having fallen to the floor of it. **The height is
  deliberately unchanged**, because `Form1.ScrollSurfaceForMode` derives how many
  lines a frame holds from `ClientSize.Height / Font.Height` — a roomier box
  would silently make the two-row mode a three-row one.
- **OPEN, unlocated: a blank line where a paragraph break falls mid-sentence,
  which "zna malo kočiti".** Gordan cannot say which book — they tested many.
  Not chased without a sample: the plausible mechanisms are different in each
  output (a blank line eats one of the two rows in subtitle mode; a paragraph
  break inside a sentence splits what the reader speaks), and picking one from
  a second-hand description is how §11's four wrong diagnoses started. **When a
  sample turns up, the question to ask first is whether the blank line is in
  `content.txt` — i.e. the parser's — or only in the wrapping.**

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

- **The speed floor.** The bottom of the range was chosen for *speech*; driving
  **fingers** it is still fast for a beginner or a foreign language. Braille
  probably needs its own, lower range rather than inheriting the speech one.
  (In *braille-leads* mode the problem dissolves — but that mode needs input we
  may not have.) `TtsReader.SilentSpeed` is where a separate range would go: it
  takes the same percentage and converts to a real words-per-minute pace inside,
  because a pace is a duration and nothing about it is a multiplier.
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

> ### NAILED DOWN — 2026-08-03 (Gordan)
>
> **The Library window is finished and is not to be touched again**, unless
> something outside it demands a change: a bug reported from use, a format that
> needs a new import path, or the manual and About box arriving to fill the two
> Help items that are deliberately unwired.
>
> That covers the window, the shelf, Now reading, the Sort menu, the tab order,
> the buttons, and the whole of Open file / Open folder including the grouping
> rules. **Both Help items are wired** (2026-08-03, "da ne ostaju repovi"):
> `Help`/F1 opens `Help\index.html`, a page that says the manual is coming, and
> `About NBR` opens the window it will be, empty and saying so. What is missing
> in both is the TEXT, not the plumbing — writing it changes no code. The rules themselves, with the measurements behind every one of them,
> are in **`docs/Open file i Open folder.txt`** — read that before changing any
> of this, because most of what looks like an obvious improvement was already
> tried against a real disk of 1622 books and found to be wrong.
>
> ### The Help menu gained two items, 2026-08-28 (both from the beta notes)
>
> **What's new** — `HintSystem.ShowWhatsNew`, above About, because after an
> update that is the one a reader is looking for. It is a `TextHelpForm` box and
> **not** another HTML page: Gordan left the choice open ("ovisno o veličini
> odabrati hoće li biti standardni box… ili još jedan HTML") and three things
> settle it — the manual opens in the system browser and he had just reported
> that being slow on some machines, this page is read on EVERY update, and prose
> in the language files falls back to English by itself instead of needing eleven
> HTML files regenerated per release. Written per release, newest first, as prose
> for a reader rather than as the commit log.
>
> **Export report** — `HintSystem.ExportReport` + `DiagnosticReport.cs`, and it is
> a **beta item** (`Beta.DiagnosticReport`), at his word. It creates no log: the
> crash handler, the hang watchdog, the import, the speech host and the speech
> inventory have written to `%TEMP%` since they were built — seven files, one of
> them 970 kB unread on this machine — and what was missing was a way to send
> them. It collects those plus `Settings.ini` and `CloudUsage.ini` and a header
> (release, build, paths, Windows, .NET, culture) into one file through a Save
> dialog opening on Documents with a dated name. **`nbr-services.dat` is not read
> and its name is not in the collector**, so a fault report cannot carry the
> reader's API keys; the finished report was searched for key-shaped words and
> came back clean. Each log contributes its **last** 128 kB, since after a fault
> the end is what matters.
>
> **The root of C: was ruled out by measurement** (he had suggested it): a write
> there is refused even on this machine, from an unelevated process, and NBR's
> manifest disables the virtualisation that lets some programs seem to succeed.
>
> **`LibraryScanner.Scan` reads and never writes** (2026-08-03). Unpacking used
> to live inside it, which is why "Open folder" first refused any folder holding
> an archive and then leaned on a flag defaulting to safe — both of which worked
> by somebody remembering. It is now `LibraryScanner.AbsorbArchives()`, called by
> name, and called only on the library. There is no flag left to set wrongly.
> **That one method is the only thing in NBR that deletes a user's file**, and it
> may never be pointed anywhere but the library.

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

### Editions — and FEATURE FREEZE, 2026-08-16

> ## THE FUNCTIONALITY IS CLOSED (Gordan, 2026-08-16)
>
> *"Sve funkcije su tu, više se ništa ne dodaje, zatvaramo opcije i mogućnosti."*
> **Nothing further is added.** Everything from here is verification, polish,
> cosmetics, and the visual and braille checks that need eyes and hands.
>
> A new capability is not a small change to this file — it is a reopening, and it
> takes Gordan saying so. A fault found in use is of course still a fault.
>
> ### The editions are named, not tiered
>
> **Lite is gone as a name. The two editions are:**
>
> - **Nemoviz Book Reader** — what was Lite. The whole player: audio, text,
>   DAISY, M4B, braille, OCR, translation, cloud voices, the speech cache and the
>   audiobook export.
> - **Nemoviz Book Reader Plus** — what was Pro. **STT** and **BRLTTY**, the two
>   things that change what the user must install.
>
> The line itself is unchanged — see below — only what the two sides are called.
> "Lite" reads as a cut-down thing, and after 2026-08-15 the smaller edition is
> not cut down: it is the reader, whole.

### The line itself (Gordan, Session 15, redrawn 2026-08-15)

NBR ships in two editions. **The player binary is the same** — Pro is simply
Lite plus a set of add-on features. The distinction is about what a feature
*requires*, not about a different app.

> ### THE LINE MOVED — 2026-08-15, and this section's own test was replaced
>
> The old test was *"does this need an external service"*. Gordan's is **"does
> this change what the user has to install"**, and it is better because it is
> the only one the reader can see: *"Ovo sve što smo dosad napravili nam
> faktički nije povećalo ni installer niti traženi prostor pa što ne bi ostalo
> ovdje?"*
>
> - **Lite** — the whole player as built, **plus translation, plus cloud
>   voices, plus OCR**. All three cost **zero installer bytes**: translation and
>   cloud TTS are ~100 lines of HTTP over the hand-rolled JSON parser, and
>   Windows OCR is reflection into WinRT with no SDK and nothing vendored (see
>   the section immediately below, which proves it).
> - **Pro** — **STT / ASR** (a model of hundreds of MB plus a runtime) and
>   **BRLTTY / BrlAPI** (a service install needing administrator rights, plus a
>   USB driver swap that can take the display away from NVDA's own driver).
>   Those two change the user's machine; nothing else does.
>
> **The wrinkle, and it argues for Help rather than against the line:** ease of
> entry and quality are inversely arranged. Azure Speech takes a plain key and
> already has portal-free ARM provisioning (§ memory), but its Croatian voices
> were judged nothing special; Google's Chirp 3 HD, accepted by ear
> 2026-08-15, **takes no API key at all** — service account and a downloaded
> JSON. Translation's Help says "paste a key"; cloud voices' Help must say much
> more.
>
> **The price, accepted knowingly: Lite ships later.** Cloud voices need a
> backend, service-account auth, key storage, chunking at the 5000-byte request
> ceiling, and the untested question of an audible seam between requests.
> Agreed placement: **the end of the Lite queue**, behind Help and behind
> everything already reported from use.
>
> **The workflow rule below still stands, but its list is now longer** — Lite
> items now include translation, cloud voices and OCR; only STT and BRLTTY are
> held back.

Superseded, kept as the record of what the split used to mean:

- **Lite** — everything self-contained: the whole player as built so far
  (audio + text + DAISY + M4B playback, all the file-format parsers, sound
  processing, TTS text reading, Settings, Bookmarks, Sleep Timer) **plus the
  remaining core items** below (Properties, finishing Settings, Help, and any
  small polish). No cloud, no heavyweight external engines.
- **Pro** — the add-ons that pull in external engines / models / services and
  need a "which one, and do we even use it" decision: **STT / ASR**
  (audiobook → synced on-screen/braille text), **OCR** (scanned image-only
  PDFs/DjVu → text), and **translation**. These are parked until Lite is done.

**OCR NEEDS NO EXTERNAL ENGINE — MEASURED 2026-08-11, the whole chain is in
Windows.** Gordan asked whether OCR could go the way OneCore voices went (first
"impossible", then "needs the MS SDK", finally "works with neither"). It can, and
the same trick does it: `Type.GetType("…, Windows, ContentType=WindowsRuntime")`
plus `AsTask` from `System.Runtime.WindowsRuntime` in the GAC. **No Windows SDK
is installed on this machine and none is needed** — `Windows.Media.winmd` ships
in `C:\Windows\System32\WinMetadata`. Nothing to vendor, nothing to ship, no new
licence, zero bytes on the installer.

Three Windows pieces make the whole pipeline, so an image-only PDF needs no
outside help at any step:

| step | API | verified |
|---|---|---|
| rasterize a PDF page | `Windows.Data.Pdf.PdfDocument` → `PdfPage.RenderToStreamAsync` | yes |
| decode to a bitmap | `Windows.Graphics.Imaging.BitmapDecoder` → `GetSoftwareBitmapAsync(Bgra8, Premultiplied)` | yes |
| recognize | `Windows.Media.Ocr.OcrEngine.RecognizeAsync` | yes |

Verified on **.NET 4.8, x64, an STA thread** (WinForms' own apartment), against
Gordan's five real Croatian scans in `D:\Test naslovi\Image PDF`.

**The measurements, and two of them overturn the obvious guess:**

- **More resolution buys NOTHING.** Same page rendered 2000→7000 px tall gives
  the **same 136 words** every time, while OCR goes from 96 ms to 915 ms. Render
  at the page's natural size. Do not oversample "to be safe".
- **`MaxImageDimension` lies.** The property says **10000**; 4948x7000 works and
  5655x8000 throws `ArgumentException: The parameter is incorrect / Image
  dimensions are too large`. **Cap well under the advertised number and be ready
  to catch.**
- **Scanned PDFs are often laid out 1:1 with their scan pixels** — these pages
  are 2479x3507 *points*, not 595x842. Multiplying by `dpi/72` for "300 dpi"
  overshoots into the failure above. Scale to a pixel target, never to a DPI.
- **Speed:** ~1.1 s render + ~0.8 s OCR for a 2-page scan; call it **0.5 s a
  page**, so a 300-page scanned book is ~3 minutes.
- **Croatian is right, including diacritics** — `IZVJEŠTAJ`, `OKOLIŠA`,
  `Smičiklasa`, `Babić` all correct. On clean text CER is **0–2 %** once cap
  height is ≥ 20 px; below ~14 px it collapses (i/l/m mush).
- **`đ` is the one weak letter, and it is now MEASURED on a whole book** (Gordan's
  252-page scan, 2026-08-14): **40.2 % of `đ` words lose the diacritic** — 98
  correct against 66 damaged. **Uneven by WORD, not uniform by letter**:
  `svađ`/`sviđ` lose it **84 %** of the time, `izmeđ` 21 %, `takođ` 10 %. It is
  also the **rarest** letter in the book — 160 occurrences against š 2929, č 2113,
  ć 1616, ž 1325 — which is why nobody notices until they hit "roden".
  **A fix-up list is worth building, and it has a safe form: only stems whose
  d-form is NOT a word** — izmed, takod, dogad, medut, roden, izad, svad, svid.
  **`grad`, `led` and `prod` must stay OUT**: they are real words, and
  "correcting" them turns *grada* into *građa*. Measuring this the first time by
  counting `de`, `rad`, `vod`, `tud`, `dak` gave pure noise — the wrong form has
  to be a non-word or the number means nothing.
- **A foreign page through the hr-HR engine cost nothing** *on that page*: an
  English paragraph came back at **0.0 % CER**. **DO NOT GENERALISE THAT — I did,
  and Gordan corrected it (2026-08-11).** See the correction below.

**CORRECTION: "one Latin pack reads all Latin languages" IS WRONG.** I measured
one clean synthetic paragraph at large type — the easiest case there is, where
no glyph is ambiguous and the language model never has to decide anything. From
that I concluded the language choice barely matters, and then repeated it in the
code, in Settings and in the Help text. Gordan, from having listened to a great
many real OCR'd books:

- Croatian or Serbian pushed through the **English** engine gives **"Yatikan"
  for "Vatikan"** — and the reverse in the other direction.
- A **Serbian** recognizer on a Croatian book, where foreign names are written
  as spelt rather than transcribed, turns **"William" into "Vvilliam"**.
- And the argument that settles it without any measurement at all: **if it were
  only a matter of Latin letters, Microsoft would ship ONE Latin pack, not
  thirty-five.** The per-language models exist precisely because the language
  decides the ambiguous glyphs.

So: the recognizer language **matters**, the reader must be able to choose it,
and any claim resting on that 0.0 % belongs to a clean synthetic page and to
nothing else. Where a fallback still happens (a requested language that is not
installed) it is a *fallback* — better than refusing — and never a reason to
withhold the choice.

**"Automatic" cannot exist at the point of reading, and that is not a gap to
fill later.** Choosing the language automatically would mean reading a page to
see what language it is in, and reading a page is the thing that needs the
language. The loop has no start. Automatic survives only in Settings, where it
means "whatever Windows would pick" — a default, not an answer.

**What Windows OCR does NOT cover, honestly:**
- **DjVu** — Windows cannot open it at all, so DjVu is the one line in the Pro
  description that still implies something external. (There is not a single
  `.djvu` on this machine.)
- **Languages the user's Windows lacks.** Only **hr-HR** is installed here;
  `en-US`, `de`, `cs`, `sr-Latn` all returned "not installed". NBR cannot ship a
  language — the user adds it in Windows' own language settings. Per the point
  above this only really bites for non-Latin scripts.
- Needs Windows 10 or newer (both APIs); fine for the current target, would have
  to be re-checked if NBR ever aims lower.

**Conclusion: do not add an external OCR tool.** Tesseract would cost ~30 MB plus
per-language data against a 16 MB installer, to do worse on Croatian. Probes kept
in the session scratchpad (`ocrprobe.cs` … `ocrprobe5.cs`) — probe 4 is the one
that matters, it is the pure Windows chain with no third-party reference at all.

**INPUT COVERAGE, measured (probe 5).** Every one of these reads through the same
`BitmapDecoder` → `OcrEngine` chain: **PNG, JPEG, BMP, GIF, TIFF**. **Multi-page
TIFF works** — `BitmapDecoder.FrameCount` reported 3 and `GetFrameAsync(i)` gave
each page separately, all three recognized correctly, so one TIFF is a whole book.
**A picture with no text returns 0 words and an empty string**, so "found nothing"
is cleanly distinguishable from "the engine failed" — say so instead of importing
an empty book. Image-only EPUB is just its images in spine order. **DjVu remains
the only input Windows cannot open at all.**

**HOW A USER GETS ANOTHER LANGUAGE — and why Gordan could not find it
(measured 2026-08-11).**

- **There is no "OCR" item to find, and that is the whole answer.** Non-elevated
  `Get-InstalledLanguage` reports `LanguageId=hr-HR`, `Features = BasicTyping,
  Handwriting, TextToSpeech, OCR`. **OCR is a FEATURE OF AN INSTALLED LANGUAGE**,
  not a separate download. You do not install "OCR for German", you install
  German and OCR arrives with it — Settings → Time & language → Language &
  region → Add a language. Help must say exactly this; hunting for an OCR
  checkbox finds nothing because there isn't one.
- **NBR cannot install one. Measured, not assumed:** `Install-Language -Language
  zz-ZZ` throws `UnauthorizedAccessException 0x80070005` **before it even
  validates the tag** — the elevation check comes first. `Get-WindowsCapability
  -Online` fails the same way. So a "Download language" button in our dialog
  would be a lie unless it elevates, and elevating to install OS components is
  not a thing a book reader should do.
- **What NBR CAN do, all non-elevated:** list what is installed
  (`OcrEngine.AvailableRecognizerLanguages`), and **deep-link into Settings** —
  `ms-settings:` is registered on this machine (checked in `HKLM\SOFTWARE\
  Classes`). Which page id is current was NOT verified (launching Settings would
  have thrown a window at a screen-reader user mid-session), so **try a chain**
  (`ms-settings:language`, then `ms-settings:regionlanguage`) and check the
  `LaunchUriAsync` return rather than trusting one id.
- An OCR model is **tiny**: `C:\Windows\OCR\hr-hr\MsOcrRes.orp` is **0.23 MB**.
  The language pack around it is not, and its size was not measured.

**THE REAL LIMIT IS NOT OCR, IT IS THE PDF RASTERIZER: WINDOWS DOES NOT DRAW
JBIG2 IMAGE MASKS** (measured 2026-08-11 on two scanned books pulled from
archive.org — 19 MB, with Gordan's go-ahead, kept in the session scratchpad).

A mass-digitized scanned book is stored in two layers, DjVu-style: a **JPX
(JPEG 2000) background** holding the paper and pictures, and a **JBIG2 bitonal
mask** holding the text. Filter counts prove the construction —
`meditationsofmar00marc.pdf`: `/JBIG2Decode ×256`, `/JPXDecode ×512`, one per
page. **`Windows.Data.Pdf` renders the background and silently omits the mask.**
The rendered page is real, correctly sized, correctly coloured paper with faint
ghosts of the lines — and no text at all. OCR then correctly reports nothing:
**0 words on 32 of 32 sampled pages**, and 4 words in the whole of the second
book. Looking at the PNG is what settled it; the word counts alone read like an
OCR failure, which is exactly the wrong diagnosis.

**Gordan's own scans are unaffected** — they are `/JPXDecode` + `/FlateDecode`
with no JBIG2, and they read perfectly. So the split is:

| kind of PDF | renders | OCR |
|---|---|---|
| ordinary scan (DCT/JPX/Flate/CCITT) | yes | works |
| mass-digitized book (JPX + **JBIG2** text mask) | background only | **finds nothing** |

### CORRECTION, 2026-08-15: MOST OF THESE BOOKS NEVER NEED THE IMAGE AT ALL

Gordan asked the question this whole section failed to ask — *"ako taj pdf ima
text layer, zašto ga uopće provlačimo kroz OCR?"* — and it lands squarely.
Everything above measured **OCR of our own render**. Nobody asked whether the
same file carries a text layer, which a mass-digitized book usually does,
because searchability is the point of digitizing it.

**Measured through `PdfPig`, using exactly what `PdfParser.cs` does**
(`ContentOrderTextExtractor`, falling back to `page.Text`), on three JBIG2 books
from three different scanning centres:

| book | scanner | chars | pages with text |
|---|---|---|---|
| `meditationsofmar00marc` | scribe3 Boston | **320 048** | 213 / 256 |
| `principlesofpsyc01jame` | scribe6 Boston | **1 679 249** | 701 / 716 |
| `onliberty00millgoog` | **google** | 2 970 | **1 / 227** |

`meditationsofmar00marc` **is the very file this section measured at "0 words on
32 of 32 sampled pages"**. It yields 320 000 characters of real text — page 40
reads *"appear with modesty, obligingness, and dignity of behaviour"*. The
rasterizer finding is still true and still irrelevant to it: **NBR already reads
this book correctly today**, because `PdfParser` runs before OCR is ever offered.

**So PDFium and the hand-written JBIG2 decoder are OFF the plan for the common
case.** They come back only for a scan with no text layer — which the Google-
scanned item is, so the case is real but much narrower than "mass-digitized".

**`/Font` per page is the cheap tell**, and it agrees: 230, 231 and 723 against
256, 227 and 716 pages, where a genuinely image-only PDF has none at all.

**AND IT EXPOSED A DEFECT — `OcrImport.IsEmptyTextBook` uses an ABSOLUTE
threshold** (`< 200` characters). The Google scan yields **2 970**, so it passes
as a real text book: 227 pages of unreadable scan import silently, OCR is never
offered, and to someone who cannot see the screen the book simply stops after
twenty seconds. **The measured distribution says the test must be PER PAGE:**

| | chars/page |
|---|---|
| genuinely image-only (Gordan's 7 own PDFs) | **0** |
| broken text layer (Google scan) | **13** |
| real text layer (archive.org scribe) | **1 250 – 2 345** |

Two orders of magnitude of clear air, so anything in 50–500 chars/page separates
them; this is not a fine judgement. The page count is already in hand —
`PdfParser` fills `doc.Pages`. Keep the absolute test for a book with no pages.
**Not implemented — Gordan's call.**

### THE PDF TEXT PATH NOW STRIPS FURNITURE AND HAS PARAGRAPHS (2026-08-28)

Ten books off a sharing forum (`D:\Player\Blind test\pdf uzorci`) — the first
samples of that kind we have had, and Gordan's note was that the forum "svašta
trpa u njih". Measured through the shipped classes, and two different faults came
out, neither of them what the eye would guess.

**1. The furniture was never stripped.** §10g's `RunningHeads.Strip` was written
for braille and this file recorded that whether the PDF path runs it was
unchecked. It did not — and a PDF is the ONE format that hands us real page
boundaries, which is exactly what that stripper needs. `PdfParser` was throwing
them away by appending every page straight into one StringBuilder. It now reads
every page into lines, strips, and assembles; **the page offsets are computed
after the strip**, or every mark would point past its own page.

| | before | after |
|---|---|---|
| printed page number as a line of its own | 166–334 a book, **in all ten** | **0** |
| scanner's signature in the FOOTER of every page | 222–334 a book, in five of ten | **1** (the one at the end) |
| text lost | | **0.1–1.3 %** |

The 0.1–0.2 % books are the ones that only had numbers; 0.8–1.3 % are the ones
that also carried the signature. **Control is clean**: Gordan's own scans lose
nothing, and `meditationsofmar00marc` keeps its 35 bare numbers because they
appear on too few pages to reach the 60 % share. The single signature at the END
of a book is deliberately left — it appears once, so it is not furniture, and
Gordan's call was that a tail there does not matter as long as it is not mixed
into the reading.

**2. A PDF has no paragraphs at all.** Measured before writing anything: the
extracted lines have **no indent** — every one starts at column zero — and no
blank line, so the only break in the book is between pages. On all ten that came
out as paragraphs == pages: no pauses for the reader, and the Paragraph seek step
correctly refusing to appear because a paragraph was a whole page.

**The signal was already there and simply not written down: a line that ENDS A
SENTENCE ends a paragraph**, everything else having been wrapped at the right
margin. That is the same test `TextCleaner.Unwrap` uses from the other side, so
the two meet — what is marked as a break stays one, what is left as a single
newline gets joined back. Result: **2 764 – 5 103 paragraphs a book, one every
98–198 characters**, which for a novel full of dialogue is the right order.
Nothing is invented; every one of those breaks was already in the text as a
single newline.

**Deliberately NOT the reference cleaner's `auto_paragraf`**, which breaks after
every sentence: run after unwrapping it gives one paragraph per sentence, and a
Paragraph step identical to a Sentence step.

**A bug in the first build of that rule, caught by READING a passage and not by
the counts:** the list of closing marks had `»` but not `«`, and Croatian and
Serbian quote as »ovako« — so every line of dialogue stayed glued to the next.
The numbers looked entirely plausible either way.

### Drawn rules are gone from every format (2026-08-28)

`TextCleaner.DrawnRule` removes a line that is ten or more of the SAME character
(`═════`, `─────`). A speech engine reads it out one character at a time and it
carries nothing.

**Ten of the same, NOT "three or more symbols"** — that rule would also take
`* * *`, which is a scene break and means something. The threshold is the one the
reference cleaner arrived at independently. Measured over 355 books: rules 192
(docx) + 99 (braille) + 71 (epub) + 43 (txt) → **0**, while `* * *` survives 60,
69, 42 and 38 times. Collateral damage: **245 characters in 101.8 million** of
docx, and braille, txt and epub identical to the character.

### AND THE REFUSAL IS WRONG — it throws away books that OCR perfectly well

Gordan's question, and it is the one nobody asked: *"Ovo zadnje bi značilo da
korisnik dobije poruku da od te knjige ništa umjesto da je prepoznaje za par
znakova?"* Measured through the shipped classes, driving `OpenPdf` /
`RenderPdfPage` past the refusal and asking the real `WindowsOcr`, six pages
spread through each book:

| book | `UsesJbig2` | pages that yielded words | words |
|---|---|---|---|
| `onliberty00millgoog` | **true** | **6 / 6** | **1 590** |
| `meditationsofmar00marc` | true | 0 / 6 | 0 |
| `Gavez mast` (control) | false | 6 / 6 | 2 253 |

**One of these books reads.** *"only an indirect interest ; comprehending all
that portion of a person's life and conduct"* — mediocre against modern type
(`LIBEBTY`, `tbey`) but a readable book. And `OcrPageSource.Open` returns
`UndrawablePdf` before a single page is tried, so NBR hands the reader nothing.

**The heading of this section was right and the TEST is too coarse.** Windows
does not draw JBIG2 **masks**; `UsesJbig2` looks for the filter. The two books
are built differently and the filter counts already said so — Meditations has
512 JPX over 256 pages, a background with a mask painted over it; On Liberty has
**5 JPX over 227 pages**, so its JBIG2 *is* the page rather than a mask on one,
and Windows draws it fine. Rendered sizes agree: ~1.4 MB a page against
200–400 kB of near-blank paper.

**BUILT the same day, and the shape is Gordan's, not mine.** I proposed probing
three pages up front; he proposed reading and asking when the result looks
hopeless, assuming it was not viable because it needs the whole book first. It
is viable, because the question does not have to wait for the end — the worker
already goes page by page behind `OcrProgressForm`.

- `OcrRefusal.UndrawablePdf` is **gone**, along with `Ocr.Refusal.Undrawable`
  whose wording ("there is nothing for Nemoviz to read") is the claim this
  measurement disproved. `OcrPageSource.Jbig2` replaces it: a **diagnosis
  carried forward**, never a gate.
- `OcrProgressForm` asks after **twenty blank pages IN A ROW**, once, and a "no"
  is a Cancel rather than a failure — `Finish` keys off `Cancelled`, and without
  setting it a reader who had just declined would then be told no text was
  found.
- **Not the first twenty, and the measurement is the whole reason.** Meditations
  gives text on 4 of its first 20 pages — title page, imprint, contents — and
  nothing for the 240 after. **Front matter renders even when the body does
  not**, so a rule reading the opening of the book measures the one part of it
  that works. My first version did exactly that and would never have fired.
- **Pages, never a word count.** Gordan's own monograph objection to the sparse
  rule applies to his own "1 500 words in 500 pages" formulation, and he took the
  correction.

**Measured by walking every page of three whole books through the shipped
classes:**

| book | pages with text | longest blank run | outcome |
|---|---|---|---|
| `meditationsofmar00marc` | 5 / 256 | **240**, from page 9 | **asks at page 28, ~10 s** |
| `onliberty00millgoog` | 218 / 227 | 4 | never asks, reads to the end |
| `Marko Podrug` (control) | 249 / 252 | 1 | never asks |

The longest legitimate blank run in a real book is **4** against a threshold of
20, so the margin is fivefold. The book the old code threw away now reads, and
the one that really is blank costs ten seconds instead of two and a half
minutes.

**Method note worth keeping.** The refusal was justified by "0 words on 32 of 32
sampled pages" of `meditationsofmar00marc` — a true measurement of ONE book,
generalised to a whole class by its filter. The class turned out to contain the
opposite case. One sample cannot establish a rule about a family of files, and
this file has now made that mistake twice about JBIG2 in one week.

**Also seen, and already solved elsewhere:** the extracted text carries running
heads with page numbers glued to the first line (*"14 MEDITATIONS."*, *"20
PSYCHOLOGY."*) — exactly what §10g's `RunningHeads.Strip` and `OcrTidy` were
built for. Whether the PDF text path runs them is unchecked.

**Consequences of the rasterizer limit, for the narrower case that remains:**
- **How common is it? Measured on Gordan's own corpus: 2 of 109 local PDFs, and
  both are the two I downloaded today.** Not one of his own ~107 files uses JBIG2
  — they are `/JPXDecode` ×27, `/DCTDecode` ×23, `/CCITTFaxDecode` ×2. **JBIG2 is
  a mass-digitization artefact** (archive.org, Google Books, HathiTrust), not what
  scanners and office kit produce. So this is a *later* problem, not a blocker —
  though blind readers who take books from digital libraries will meet it. (Sample
  caveat: 1693 of his PDFs are OneDrive placeholders and were deliberately not
  touched, so this is 109 files, not 1800.)
- **Detect it, do not let it look like a bad scan.** `/JBIG2Decode` is findable in
  the raw bytes before we start, and "page rendered but zero words across many
  pages" is a second signal. Say *"this PDF stores its text in a form Windows
  cannot draw"*, never *"OCR failed"*.
- This is the one place the external-tool question genuinely reopens — but for a
  **rasterizer**, not for OCR. PDFium (BSD) handles JBIG2 at ~10 MB of native
  dependency, against a 16 MB installer.
- **Or write the decoder — and it is smaller than "implement JBIG2" sounds.**
  **We never need to composite the page.** For OCR the *mask alone* is what we
  want: it is the text at full bitonal resolution, a cleaner input than the
  finished page. So the job is "decode one image XObject", not "write a PDF
  renderer". **Measured scope** (segment headers parsed out of the real files,
  `jbig2probe.cs`): symbol dictionary (type 0), immediate text region (6),
  immediate generic region (38), page info (48). **No refinement, no halftone, no
  custom Huffman tables** — that is a large part of T.88 we would not need. There
  is also **no `/JBIG2Globals`** in these files, so every page's stream is
  self-contained and no shared-dictionary plumbing is required. What is left:
  MQ arithmetic decoder, generic region with its templates, symbol dictionary,
  text region.
- **CORRECTION to what this file said first time round.** I wrote that JBIG2 and
  DjVu's JB2 share "the same MQ arithmetic coder" so one decoder unlocks both.
  **That is wrong on the coder**: JBIG2 uses the **MQ** coder, DjVu's JB2 uses
  Bottou's **ZP** coder — different state tables, different bitstreams. What they
  really share is the *design*: symbol dictionary, text-region placement, generic
  region with context templates. So the second one is much cheaper after the
  first, but it is **not free** — do not plan as if it were.

**THE RENDER-SIZE RULE, and neither obvious answer is right.** Two real cases sit
at opposite extremes: Gordan's scans are laid out 1:1 with their scan pixels
(page **3507 pt**), archive.org's book uses ordinary page units (**749 pt**).
"Natural size" gives 749 px and OCR sees nothing; "300 dpi" gives 14612 px and
throws. **Aim the LONG SIDE at a pixel target (~3400 px) and cap at ~6800**,
whatever the points say.

**DJVU IS MUCH CHEAPER THAN IT LOOKS — THE TEXT IS ALREADY IN THE FILE.** Chunk
dump of two real DjVu books: **`TXTz` present on 229 of 256 pages** (and 9 of 16
in the other). DjVu was designed for scanned books *with* a searchable text layer,
so for most real files **no OCR and no image decoding is needed at all**:

- **Tier 1 — container + `TXTz`.** Walk the IFF chunks (`AT&T`/`FORM:DJVM` →
  `DIRM`, per-page `FORM:DJVU` → `INFO`, `TXTz`), then BZZ-decompress. BZZ is
  Burrows–Wheeler plus a ZP arithmetic coder — self-contained, a few hundred
  lines. **The ZP state table must come from the published DjVu specification,
  not from DjVuLibre, which is GPL and cannot be copied into NBR.** The container
  walker already exists as `djvuprobe.cs` in the scratchpad.
- **Tier 2 — no text layer.** Then `Sjbz` (JB2) must be decoded to an image for
  OCR. That is the big one. It is the *same shape* as the JBIG2 work above and
  much cheaper once that exists — but a different coder and bitstream, so not
  free (see the correction above).

**LAYOUT: MEASURED AT LAST, AND IT PASSES — TWO-COLUMN READING ORDER IS CORRECT**
(2026-08-11, on `D:\Test naslovi\Image PDF\Gavez mast.pdf`, which Gordan found:
JPX + Flate, no JBIG2, no `/Font` at all, so genuinely image-only and renderable).

Page 2 is a dense **two-column** Croatian page. Checked at the seam, which is the
only place that proves it: the left column's last line —
*"…Nakon 40 dana bolovi su nestali…"* — is immediately followed in the output by
the right column's first line, *"Iscjeljuje sve vrste rana pa i onih najtežih…"*,
and the text ends on the right column's last line. **It reads the left column
whole, then the right. No interleaving.** So `OcrEngine` does its own column
detection and we do not have to build reading order out of `BoundingRect`.

**Quality on that page is poor, and the source is why.** Looking at the render
settles it (again): a **faded photocopy with the left edge of the left column
physically clipped** — several lines lose their first letter on paper, before any
software is involved. Compare the clean official forms, which read almost
perfectly. So: *clean scan → very good; bad photocopy → readable but mangled.*
Neither is a statement about the engine.

**"MORE RESOLUTION" IS NOW DISPROVED ON REAL MATERIAL TOO**, not just on synthetic
renders. Same page, long-side target swept 1700 → 6800 px:

| target px | words | median word box | OCR ms |
|---|---|---|---|
| 1700 | 732 | 23 px | 343 |
| 2400 | 726 | 33 px | 490 |
| 3400 | 692 | 47 px | 756 |
| 4800 | 687 | 66 px | 1426 |
| 6800 | 692 | 92 px | 2376 |

Flat across a 4× range while time grows 7×. **Once the type clears the floor
(~20 px), extra pixels are pure cost.** ~2400 px on the long side is the sweet
spot, not 300 dpi.

**PRE-PROCESSING: one thing helps a little, the obvious thing HURTS** (probe 8,
same page):

| variant | words | plausible-word % |
|---|---|---|
| as rendered | 692 | 65 |
| greyscale | 691 | 65 |
| **contrast stretch (2nd/98th pct)** | 654 | **66** |
| **Otsu binarize** | **605** | **54** |
| stretch + sharpen | 656 | 63 |
| invert | 691 | 65 |

- **Do NOT binarize.** Thresholding is the classic scanned-page move and it is the
  worst result here — 87 words and 11 points lost. The engine wants greyscale and
  does its own thing with it.
- **A mild contrast stretch is the only gain, and it is modest.** The aggregate
  score barely moves, but the same passage goes from `porodica (tx)raźine)` to
  `porodica Boraginacee (boraźine)`. Worth having; not a rescue.
- **The engine is polarity-invariant** — inverting changes nothing, so white-on-
  black pages need no special handling.
- **Word count is not quality.** The contrast-stretch pass returned *fewer* words
  and *better* ones. Never tune on word count alone.

**MEASURED AT LAST on a real scanned BOOK** — Gordan's own, scanned years ago and
exported as an image-only PDF today: `Marko Podrug - Sve samo ne romantika.pdf`,
**252 pages, 100 MB, JPX + Flate, no JBIG2, no `/Font`**. Kept in
`D:\Test naslovi\Image PDF`. Two faults found, both ours rather than the engine's,
both now fixed in `OcrTidy`:

- **Words broken across a line come out split**: `kono- barom`, `napisa- no`,
  `za- boraviti`. **Two of every seven lines** end in a hyphen, so this is not a
  rarity — and spoken aloud it is two words that are not words. `JoinBrokenWords`
  rejoined **73 of 77** on 24 pages. **The space after the hyphen is what makes it
  safe**: an author's own hyphen never has one, and every single one was left
  alone — `hip-hop`, `FPZ-u`, `PC-ju`, `DOS-u`, `sori-sori`, `kakvih-takvih`,
  `Fu-Schnickense`.
- **The printed page number is a LINE OF ITS OWN at the top**, which flattening
  hides: every page reads "7 PROLOG…", "9 Ali, dobro…". Read aloud that is a bare
  number at the head of every page. **Measured over the whole book, not a sample:
  214 removed of 249 pages that have text, and NOT ONE missed** — every page whose
  first line was digits was caught. The other 35 genuinely have no printed number
  (covers, title, contents, and pages the layout leaves unnumbered), and none of
  them has it glued to the text or sitting at the foot.
- **Whole-book timing, on the i9-14900HX: 252 pages in 147 s = 0.58 s a page** —
  which is what the import dialog's estimate promises, confirmed at book length.
- **Reading order and columns were already fine** (the leaflet proved that), and
  `OcrResult.Text` returns a page as one run rather than preserving line breaks,
  which is what we want — the line breaks belong to the paper.

**WHAT THE WRONG LANGUAGE ACTUALLY DOES, aligned character by character** on one
page of that book, hr-HR against en-US — the evidence for Gordan's correction,
now quantified rather than argued:

| | | |
|---|---|---|
| `č` → `é` ×4 | `š` → `é` ×3 | `ž` → `i` ×3 |
| `ć` → `é` ×4 | `š` → `ö` ×3 | `č` → `E`, `ö`, `e` |

**30 characters changed out of 1093 — 2.7 %** — and every frequent substitution
is a Croatian diacritic replaced by a **French or German** one, because the
English model has no `č/ć/š/ž` but does have `é` and `ö` from loanwords. That is
the mechanism behind his *Yatikan* for *Vatikan*. It is not only diacritics
either: `dakle`→`dalde`, `isključen`→`isldjuéen`, `špici`→`épici`. And 2.7 % is
worse for a reader than it sounds — those are the commonest letters in Croatian,
and `éto` for `što` is spoken as noise.

**Later, and NOT for alpha — book descriptions from the internet** (Gordan,
2026-07-29). A "fancy feature" on the Lite backlog, deliberately parked: it needs
no translation engine, so it does not belong in Pro.

- **Goodreads is out** — its public API was shut down at the end of 2020.
- **"Google Books needs no key for basic queries" was WRONG** (mine, corrected
  2026-08-07 against Google's own page): *"Requests to the Books API for public
  data must be accompanied by an identifier, which can be an API key or an access
  token."* Keyless requests do often work, but they are undocumented and rated by
  IP — a test call came back **429**. And a key shipped inside a distributed app
  can be extracted: someone burns the quota and the abuse is attributed to our
  project. There is no useful key restriction for a desktop app (no referrer, no
  fixed IP).
- **THEN IT WAS MEASURED, AND THE ANSWER IS: DO NOT BUILD IT** (2026-08-07,
  Gordan asked to try it before committing to anything — which is what saved the
  work). Both services, against the 235 ISBNs NBR's own parser found:
  - **Open Library**, no key needed, all 235 asked in 12 batched requests:
    **174 known to it at all**, but only **30 carry a description**. Of the 32
    books that actually need one — an ISBN and no local blurb — it supplies
    **three**, and all three are the same series (Temeraire). Three books out of
    596. **0.5 %.**
  - **Where both exist** (27 books) Open Library's is consistently the THINNER
    one: 349→159 characters, 1414→577, 2179→226, 2411→142. So it is not an
    upgrade either, and there is no argument for preferring it.
  - **Google Books, keyless, from a real home IP: 429 on all 32**, first request,
    900 ms apart. Not quota pressure — a refusal, exactly as their page says
    ("must be accompanied by an identifier"). So the keyless path is closed, and
    a key in a shipped app can be extracted and burned on our project.
  - **Conclusion.** Even if Google's coverage tripled Open Library's, that is
    about ten books out of 596 in exchange for an account, a key that leaks, a
    network path, a consent dialog and telling a third party what someone reads.
    The local pass already gets 45 % for nothing. Park it; if it ever returns, it
    is a per-book action a reader asks for, never a bulk fetch.
- **MEASURED 2026-08-07 on 596 real EPUBs** (Test naslovi + three OneDrive
  libraries; every OPF readable). This is what the whole remote idea is worth:
  | | books | share |
  |---|---|---|
  | `dc:description` AND ISBN | 199 | 33 % |
  | `dc:description` only | 70 | 12 % |
  | **ISBN only — where a SAFE lookup adds something** | **31** | **5 %** |
  | neither — a lookup must guess by title | 296 | 50 % |
  The local pass gets **45 %** free and offline. A safe remote lookup adds **5 %**:
  the books that carry an ISBN mostly carry a description already, because the
  same publishers do both. So the local pass is the feature; the network is a
  footnote, and bulk title-matching for the other half is where wrong blurbs
  would come from.
- **The local pass is BUILT, and the sidecar `.txt` is the best source of the
  lot** (2026-08-07). `SidecarDescription` reads the small text file people leave
  beside a book — the same files the 5 KB import filter refuses to treat AS a
  book, which is one mechanism seen from both ends. Measured by running the
  compiled class over 161 book folders in Test naslovi and four OneDrive
  collections: **155, 96 %**, median ~1050 characters. Every one of the six
  rejections is right (torrent lines, a file-host referral advert, "BBC Comedy
  Series", one blurb cut off at 99 characters).
  **Sources now, in the order they are tried:** the book's own metadata — EPUB
  `dc:description` 45 %, MOBI EXTH 103 13 %, M4B `desc`/`ldes` 10 of 13, DAISY —
  then a description trailing the text itself, then the sidecar. Hooked into all
  three import paths: `ImportFileCore`, `ImportDaisyFolder`, and `CopyAudioInto`
  for a folder of audio, which is where the sidecars actually live.
  **The method is the part worth keeping.** Every rule came out of a
  baseline-vs-rebuild sweep with each change read by hand, and four rules that
  looked obviously right did damage the sweep caught: "start at the first line
  that is not scaffolding" began INSIDE a header block; `unabridged` in the
  technical-marker list deleted a sentence out of a real blurb; an all-caps
  heading with no colon split an omnibus into its last novel only; and a "must
  contain a full stop" test — measured before any of the line filtering existed —
  was rejecting two genuine blurbs and nothing else.
- **Two things the samples proved about `dc:description`:** it is usually
  **escaped HTML**, so it needs decoding and THEN tag-stripping (one pass leaves
  `<p class="description">` in the text), and some are Apple exports carrying
  inline CSS. And **23 of the 269 run past 3000 characters** — the next book's
  first chapter, or the author's bibliography. Median of a real blurb: **993
  characters**, which is a paragraph and settles where it can go.
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
- **Where it goes — and the old answer here was wrong.** This used to say "the
  Library's details pane has room". It has not: that pane is a **two-column
  ListView**, `Field` 120 px and `Value` 280 px. It is a label/value grid, not a
  text area — a 993-character paragraph would not wrap, would be clipped at the
  column edge, and a screen reader would read it as one unbroken sub-item with no
  way to move inside it. The window is not full; the CONTROL is the wrong shape.
  So: a **row in the details grid as the doorway** ("Description", Enter opens),
  and the paragraph itself in **its own small dialog** on the `TextHelpForm`
  pattern — read-only, tabbable, Escape closes. That is how NBR already shows
  prose to a reader, and it adds nothing to any window that is already crowded.
**Workflow rule:** until Lite is finished, when reporting "where we stopped"
or "what's left", list **Lite items only**. **Since 2026-08-15 that includes
translation, cloud voices and OCR**; the separate Pro backlog is now just
**STT and BRLTTY**, mentioned only when explicitly asked about Pro.
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

**The Playback group is gone too (2026-08-09, Gordan's call).** It held volume
and speed — both set from the player, both remembered per book regardless of
this dialog, so the controls were a second way to do something already done,
occupying the one band of height the five tone bands will need. Its 76 units
plus the 8 below go straight into the three stage rows: **StageH 138 → 166**,
and `PropGeom.For(960, 628, 570)` still reproduces every constant to the unit,
so the invariant that lets the hybrid page compute its own narrower layout is
intact.

**But his premise for it was wrong, and checking it is what made the change
safe.** He expected the two numbers to be visible in the info boxes. They were
not — measured: the player's audio info box lists title, author, chapter/part,
page, bookmarks and the times, and Properties' own info column lists title,
author, publisher, producer, year, format, time and description. **Neither
showed volume or speed anywhere.** They could be changed from the player and
heard announced, never read. So they were added first — `BookInfoField.Volume`
beside the `Speed` that already existed unused, in both boxes — and only then
were the controls taken out. Removing them without that would have made two
settings unreadable.

Two consequences worth knowing. Focus on opening Properties now starts on the
**master switch** (it started on the Playback group, which no longer exists),
which is what a reader came to this page for. And the info column is tighter:
§10b measured 19 lines of 22 with every stage on, and this adds two — plus a
transient "Analysing the recording" line — so the worst case now touches the
scroll bar that column has always had.

**The analysis is visible now as well as spoken.** Gordan heard the two
measurement announcements and asked what was happening on screen; the honest
answer was *nothing at all* — they go out through `ScreenReader.Announce`, which
reaches a screen reader and draws nothing, so for ~1.6 s the dialog sat still and
then rewrote six cells with no visible cause. The read-out now carries the line
while it runs.

**And then the job grew, so the line became a window (2026-08-09).**
`AnalysisProgressForm` — a modal dialog with a determinate bar, a status line and
Cancel. **Measured: 20.6 s for twenty segments on the i9-14900HX**, ~1.0 s each,
which is four to seven minutes on the minimum machine. An announcement and a
still dialog is a defensible answer to 1.6 s and not to that.

- **The estimate is measured on the machine it is running on**, from the segments
  already done — so it is right on hardware nobody here has seen. **Rebuilt only
  when a segment lands, never per tick**: recomputing every 300 ms made it WALK
  BACKWARDS between segments (measured: 80 → 85 → 65 → 70 seconds), because the
  elapsed time keeps growing while the count dividing it does not. A remaining
  time that goes up while you watch reads as the job getting worse.
- **Spoken at the quarters** — three utterances plus the opening line and
  Properties' own "recording analysed", against twenty if every segment spoke.
  A bar is for the eye; the quarters are the progress as far as the ear is
  concerned, which also settles §8a's long-open "spoken progress for
  screen-reader users".
- **Focus starts on Cancel, and it has to.** The status line is a read-only edit
  control under §2's focus echo guard, so it does not change while it is focused.
  Measured with focus starting there: **the line stood at "Starting the analysis"
  for the whole twenty seconds.** Focus therefore starts on the only action in
  the window and the line is one Tab away, refreshing on the way in. Nothing the
  ear gets depends on it — announcements need no focus.
- **Cancel does not close the window; the worker closing it does.** The stop flag
  is read *between* segments, so giving up takes up to one segment. Closing on
  the keypress would put the reader back in Properties with a decode still
  running against the book, and a second visit could start another. The close box
  means Cancel and behaves identically; only Windows shutting down gets its
  window back at once.
- **A cancelled job is not a failed one.** `AnalyseIfNeeded` skips
  `Prop.Analysing.Failed` when `dlg.Cancelled` — "the recording could not be
  analysed" is the wrong sentence for a job somebody stopped on purpose.
- **Playback is held for the duration and put back** (Gordan: *"Može privremena
  pauza dok se radi analiza ali da se knjiga onda opet pokrene jer iz properties
  dijaloga se ne može kontrolirati a poželjno je da svira dok se podešavaju
  kontrole"*). `Form1.HoldPlaybackForAnalysis` goes through
  `PausePlaybackQuietly`/`ResumePlaybackQuietly`, so it is a **programmatic**
  pause — no sleep timer is cancelled (§7) — and it resumes only if it was the
  one that stopped it, leaving an already-paused book paused.

**Verified through the shipped classes** on a real 145-part book, not on a
harness copy of the logic: the bar walks 0→20, the wording comes out of `en.lang`
and counts down monotonically, the result is `Measured` with the same
`HighBandBelow` on repeat runs, `book.Analysis` is untouched by the dialog
itself, and Cancel at segment 4 closes with `DialogResult.Cancel` and a null
result. **Not seen by eye yet** — it goes on §11's eyes-and-hands list with the
five-band Tone cell.

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
- **Loose controls get one too, since 2026-08-03.** General and Misc have no
  groups, so their five and one explanations sat written in `en.lang` with
  nothing to open them — **six of the ten**, found by Gordan while writing the
  Help. `SettingsForm` records each loose control's key as the page is built
  (`LooseHints`, the only place they are in scope by name) and
  `SettingsSkin.AttachLooseHints` hangs the key. They line up in a **column at
  the right edge** rather than chasing each control's right edge: a checkbox, a
  text box with a Browse button and a combo are of every width, and keys
  following them would read as scatter. Audited: nine keys for nine texts, all
  tabbable and all visible on their own page.
  **A note for the next audit:** WinForms reports `Visible = false` for a control
  on a tab page that is not the selected one. It reads exactly like a missing
  button. Select each tab before believing the dump.
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
minute):" became "Reading speed:", the unit moving to the value (then "175 WPM",
now "1.0x" — see §8e),
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

**Checked by ear and passed** (Gordan, corrected here 2026-08-04 — this line had
stood at "not yet checked by ear" long after he had listened to it, which is the
worse way for a brief to be wrong: it invites redoing work that is done).
Rollback is one file: the 93.6 MB build is in git.

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
(text present), TagLib# LGPL 2.1, libmpv LGPL 2.1+ — with FFmpeg **also LGPL
2.1**, though that line said v3 until 2026-08-07; see the bullet below.

**The texts ship now (2026-08-07)** — `Licences\LGPL-2.1.txt` and
`Licences\MIT.txt`, beside the fonts' OFL and CC-BY. **Both were copied from
files already on this machine**: the LGPL 2.1 from the copy nvaccess ships with
its controller client, the MIT from inside `System.ComponentModel.Annotations`'
own package. Neither was typed out, and neither should ever be — a licence
reconstructed from memory is not the licence, and this is the one file in the
project where being approximately right is worth nothing.

**The bigger gap was the component list, not the texts.** The notices named
three managed packages; the project references **sixteen**. Every licence is now
read out of that package's own `.nuspec` inside its `.nupkg` — all MIT except
**TagLib#, which declares `LGPL-2.1-only`** — with each MIT package's copyright
holder listed, since the terms are shared but the holder is not.

**All of it is settled as of 2026-08-07, and the last three came off the web
without a licence ever passing through a language model.** That distinction is
the method worth keeping: `WebFetch` renders a page through a small model, which
is fine for establishing a FACT and unacceptable for a text that ships. The
**GitHub API returns a file's exact bytes** (base64), so it is a legitimate
source for the text itself.
- **PdfPig 0.1.15 → Apache-2.0**, as GitHub classifies the repository, with the
  text taken from PdfPig's own `LICENSE`. It had been missing from the notices
  altogether — seven assemblies of PDF extraction, unnamed.
- **Microsoft.Bcl.HashCode 6.0.0 → MIT**, read from the package's own nuspec on
  nuget.org (`<license type="expression">MIT</license>`, repo
  `dotnet/maintenance-packages`).
- **FFmpeg is LGPL v2.1 — and I got this WRONG first, in the notices, in this
  file and in a commit message.** The fork carries **two** patches that configure
  FFmpeg. `compile-lgpl-libmpv.patch` is zhongfly's upstream one and keeps
  `--enable-version3`; reading it gives "LGPL v3", confidently and wrongly,
  because **NBR's build never applies that file** — it is reached only with
  `lgpl=true`, and the run that produced the shipped DLL ran `lgpl=false`. What
  it does apply is `patch/0099-NBR-LGPL-audio-only.patch`, which removes
  `--enable-gpl` **and** `--enable-version3`.
  **Three independent checks agree**: both flags are removal lines in that patch;
  `--enable-version3` appears nowhere in the 3067-line build log of the run that
  produced the DLL, while "Applying: NBR: LGPL, audio only" does; and the DLL
  carries no version marker at all. So it has been v2.1 since §10e′, and the
  "LGPL v3" line was true of the 93.6 MB zhongfly build it was written for and
  was never updated. `LGPL-3.0.txt` and `GPL-3.0.txt` shipped for one day and are
  gone; they covered nothing.
  **The lesson worth more than the fix: two patches in one repository can both
  look authoritative, and the one that is NOT applied reads exactly like the one
  that is. Check what the BUILD did, not what a patch says.**

**The whole "remove the obligation" exercise was ANSWERING A QUESTION THAT WAS
NOT OPEN (2026-08-07).** Gordan's instruction was sound — if Microsoft will
hammer us for something avoidable, avoid it — but FFmpeg was already LGPL v2.1.
What followed is kept because the mistakes are the useful part.

1. **`--enable-version3` was edited out of `compile-lgpl-libmpv.patch`** (commit
   `efb690a`), after a genuinely careful check against FFmpeg's own `configure`:
   exactly eight components sit behind version3 — `gmp`, `libaribb24`,
   `liblensfun`, `libopencore_amrnb`, `libopencore_amrwb`, `libvo_amrwbenc`,
   `mbedtls`, `rkmpp` — and this build enables none of them. **That analysis is
   still correct and worth keeping**: `libaribcaption` is not `libaribb24`, the
   AMR entries are encoders, TLS goes through openssl. It was simply applied to a
   file the build does not use.
2. **The build was dispatched with `lgpl=true`** to exercise that file, and that
   is what broke it. With `lgpl=true` the workflow applies
   `compile-lgpl-libmpv.patch` ON TOP of `patch/0099-NBR-LGPL-audio-only.patch`,
   and the two configure the same two files: CONFLICT in `packages/ffmpeg.cmake`
   and `packages/mpv.cmake`, job dead in 1.6 minutes. **Every working build has
   used `lgpl=false`**, where 0099 alone does the job.
3. **The run still reported SUCCESS**, because the ordinary `(64, false)` leg
   built fine for 46 minutes while the `(64, true)` leg failed. A green run is not
   a green build when a matrix leg can fail on its own — and the handover note
   written the night before had said "do not infer it from the run going green",
   which turned out to be the right warning for the wrong reason.
4. Both changes are reverted (`ad0d057`). The fork is back to what built the
   shipped DLL, and **no rebuild is needed at all**.

**The first attempt (run 31129028299) failed on nothing of ours**, and that
diagnosis stands. No step failed: the `params` job sat **15 minutes with zero
steps and no runner assigned** and was cancelled, taking the run with it. The
comparison that settled it is the last successful run, where the same job took
**3 seconds and had 4 steps**. GitHub's status API then named the cause —
**Actions in a major outage**, critical incident opened 15:22 UTC. **It was NOT
re-sent while that lasted**: re-dispatching into an outage is not persistence, it
is a row of identical red runs that say nothing, and the previous seven attempts
were worth making precisely because they were seven DIFFERENT causes.

**Two things left behind, neither a licence question:**
- A run dispatched with **`lgpl=true` will always fail** in this fork, for the
  conflict above. Build with `lgpl=false`.
- The fork's own patches for **libpsl, curl, c-ares and mpv-enable-libcurl no
  longer apply** against upstream. They fail tolerated — that loop ends in
  `|| git am --abort` — so builds still succeed without them, but the drift will
  widen.

---

## 10f. The sound card can eat the start of every sentence (2026-08-01)

> **It is a switch now** — Settings → Device, "Keep the sound card awake", **on
> by default** (Gordan, 2026-08-03). On by default because the fault it prevents
> is one almost nobody would diagnose; a switch because it does hold an audio
> endpoint open for as long as a book plays, and on a machine that does not need
> it that is a cost with no return. Read on **every** Play, so switching it off
> takes effect at the next sentence rather than the next launch, and the
> keep-alive already running is stopped rather than left holding the card.


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

- ~~**The English table is auto-detected wrongly.**~~ **— FIXED 2026-08-04, and
  it was never the table.** `Smith_Chuck…BRF` read "**Have** can a man be Born
  Again", blamed here on the wrong English standard (UEB vs EBAE). Measured
  rather than argued: **all three English grade-2 tables — UEB, EBAE
  (`en-us-g2`) and British — produced the same "Have"**, which is not what a
  wrong-table result looks like. The file writes the title as `,h{ …`, and the
  byte `{` was being **dropped by our own cell map**, leaving a bare `h` — the
  word sign for *have* in every English standard. The tables agreed, and they
  agreed on what we had handed them.
  **The bug: the lowercase convention shifts all of 0x40..0x5E, not just A..Z.**
  `BuildCellMap` accepted lowercase letters and nothing else, so `@ [ \ ] ^`
  arriving as `` ` { | } ~ `` were thrown away. That one line covers this bullet
  **and** the "stray bytes" one below: `0x60` in the French integrals and `0x7C`
  in the abridged are the same five cells.
  **Measured before and after across all 24 affected samples** — a stashed build
  for the baseline, so the comparison is real and not remembered: **no file lost
  a character and no file changed its detected table**; 24 gained text, from +3
  characters to +66 119 on a Korean book, +2 000 to +3 300 on each `.i55`, +310
  on the English one. The title now reads *"How can a man be Born Again?"*, and
  the ministry's phone number stops being `1-hjj-272-WILL` and becomes `…-WORD`,
  which is what it actually is.
  **Now a separate question: NBR ships only UEB for English.** EBAE is measurably
  better on the two American samples (`en-us-g2` gave "Incarnation" where UEB gave
  "IncarnN !Ascension"), and both tables are already vendored — adding them is two
  lines. **But do not add them yet.** UEB and EBAE are genuinely hard to tell
  apart automatically, and since the per-book override was removed (§11) a wrong
  automatic pick has no remedy. The table choice at IMPORT is the prerequisite,
  not the longer table list.
- ~~**Two English books are detected as FRENCH**~~ **— GONE by 2026-08-04, and
  not by anything aimed at it.** `NALIS_BR_ 00038` and `00041` now read
  `en-gb-g2` and `en-us-g2`. Two changes made that day did it between them: the
  cell-map fix stopped throwing away five cells per file, and EBAE and British
  joined the detection set, so the right answer was finally on the list to be
  picked. **Worth remembering as a pattern** — the note called this "a real
  `Detect` failure", and the scoring was never the problem; it was being asked to
  choose from a set that did not contain the answer, using input with holes in
  it.
  **Swept over all 82 braille samples afterwards**, 77 of which get a table (the
  rest are Braillo, `.smb` and `.bopf`, correctly refused): 44 EBAE, 22 French,
  6 Croatian `hr-old`, 2 UEB, 2 `fr-g1`, 1 British. The French count matches the
  French corpus exactly and the Croatian one the Croatian, which is the check
  that the distribution is not just plausible-looking. The Korean, Thai and
  Vietnamese files land on English or French tables — wrong, and expected, since
  detection is only offered the nine it can score. **That is what the per-book
  chooser is for.**
- French `<auteur>` markup arrives as text, now as `àauteurù`. **The `Haüy` half
  is FIXED (2026-08-28), and it was the wrong GRADE, not a bad cell.** The
  Valentin Haüy library ships each book four ways -- `_ABR_` (abrégé, contracted)
  and `_INT_` (intégral, uncontracted) × the French and the North American ASCII
  convention -- and detection used to give all four `fr-g2`. The `_INT_` files now
  detect as `fr-g1` and open *"Presses de la Cité … Association Valentin Haüy"*
  where they read *"Brûleez tout … Haouy"* before; the `_ABR_` files are genuinely
  contracted and correctly stay on `fr-g2`. Done by the round-trip refinement in
  §11, which separates the two editions by 63 to 73 points. **The filename says
  which** (`INT` = intégral = grade 1), and so does the book's own title page --
  worth knowing before anyone chases it in the cell map again.
- `.i55` decorative rules survive as `\5/∷∷∷∷∷:`. **The guess that these were the
  `{ | } ~` bytes was wrong** -- those are mapped now, and the rules came out byte
  for byte unchanged. **ANSWERED AND GONE (2026-08-28):** `\5/` is liblouis's own
  notation for a cell it cannot back-translate, the digits being the dot numbers,
  so the rule was not surviving -- it was being reported as untranslatable, one
  escape per cell. `StripUntranslated` drops the notation now, and the `∷` run
  goes with it because the line left behind is caught by `IsDecorative`. See §11
  for why this was pointless before the table detection was fixed and is not now.
- Stray byte not yet mapped: `0xA4` in one abridged French file. (`0x60` and
  `0x7C` are fixed — see the first bullet.)
- ~~**Running heads and page numbers end up inside the sentences.**~~ **— FIXED
  2026-08-04, and generally rather than per book.** A paragraph running over a
  page break came back with the producer's furniture spliced into it: *"You can
  hear conversations from the top **1 we all live here** floor as the words float
  upwards"*, 111 times in one book. Gordan's instruction was that a rule per book
  is no rule.
  **`RunningHeads.Strip` keys on the REPETITION, not the words** — a line that
  stands in the same place on most pages is furniture, whatever it says, in any
  language. Digits are normalised away before counting, so "1 we all live here"
  and "2 we all live here" are recognised as one thing; **two** candidates are
  taken per end, because a book printed both sides alternates author and title
  and catching only the commoner one would leave the fault looking intermittent;
  and it needs 60% of at least 5 pages, because the cost of being wrong is a
  deleted sentence.
  It lives in its own file rather than in `BrfParser` so Braillo and PDF can use
  it. **`BrfParser` now translates in one pass and assembles in a second**: the
  old single loop had appended a line before it could ever see the page it
  belonged to.
  **Measured over 58 braille books, before and after.** 19 changed, losing
  1.2–2.6% of their characters; the other 39 have no running heads and lost
  nothing — including all 19 in `Test Naslovi\Braille`, which is why that corpus
  alone would have shown a flat zero and proved nothing. In the reported book
  *"we all live here"* goes **114 → 0**, in *Safe Enough* 117 → 4 and in *Daily
  Gospel Devotional* 101 → 23, the remainder being the title page and genuine
  mentions. **No distinct word disappears from any of the three** — the check
  that matters, since it is the one that would catch a deleted sentence.
  **A second shape, and a second pass for it (same day).** *Daily Gospel
  Devotional* kept 23 of its heads because it breaks the first pass twice over:
  its page number is written the braille way — the letters **a to j** standing
  for 1 to 0, so `pblea`, `pbleb`, `pblec` — which a digit rule has nothing to
  normalise; and the head shares its line with the day's reading, so removing the
  line would take a sentence with it. `StripPrefix` matches the common **prefix**
  instead, removes it plus the token holding the page number, and leaves the rest
  of the line standing. 101 → 14.
  **The two passes run prefix-first, and the order is not a preference.** Run
  second it changed *no book at all*: the whole-line pass had already taken the
  heads that stand alone, so the pages no longer agreed about how they begin and
  the 60% was never reached. They also do not overlap — a head whose number comes
  FIRST ("1 we all live here") has no common prefix to find, and one written in
  braille letters has nothing to normalise. Each catches what the other
  structurally cannot.
  **Still not exhaustive:** 14 remain in that one book, and the shape of them has
  not been looked at.
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

   ~~All of which was the wrong diagnosis.~~ **FIXED 2026-08-04, and the cause was
   none of the above.** The claim that "both samples yield zero `h1…h6`" is simply
   **false** — measured, the old importer collected **24 headings with correct
   titles** from this sample's `<hN>` and wrote them to `[TextNav]`, and the
   library's own copies have carried them all along (`th=24`, `th=21`).

   **The real fault was an ordering bug in `BookData.Load()`.**
   `BuildHybridNavFromText` was called from the end of `BuildDaisyNav`, which runs
   **early** in `Load()` — before `DetectTextBook` has decided whether the book is
   a hybrid, and long before `LoadTextNav` has read the headings out of
   `[TextNav]`. So both of its guards (`!IsHybrid`, empty `TextHeadings`) were
   always true and it returned every time, for every book, since it was written.
   `DaisyHeadings` therefore stayed empty and Go To fell back to the audio file
   names. It is called at the end of `Load()` now, after both of the things it
   needs. **That one move is the whole fix**: 24 chapters, correct Portuguese
   titles, correctly placed on the clock, surviving a cold reload.

   **Why the note was so confident and so wrong:** an empty heading list and a
   guard that never fires produce exactly the same symptom, and the note picked
   the explanation it could see. Do not diagnose a nav problem from what Go To
   shows — load a book and print both lists.

   **AND THE PRINTED PAGES HAD THE SAME GAP, one list further down — found
   2026-08-28.** `BuildHybridNavFromText` carried the HEADINGS across the sync
   map when it was written and left the PAGES in `TextPages`, where they are
   character offsets. A hybrid is an audio book, so everything that navigates it
   reads `DaisyPages` — which meant a narrated book that HAS printed pages showed
   none, was offered no Page seek step, and could not have a Go To page group.
   Both lists are now built the same way. Measured on the library: *Distribution*
   had 431 pages in its text and **0** in navigation, now 431; *S13304* has 402
   of its own from the DAISY navigation and is untouched, which is what shows the
   existing path still wins. Gordan is the reason it was looked at at all —
   *"računaj i da daisy ima stranice, čak i u audio"*.

   **The TOC work was kept, but it is a policy improvement and not the fix.**
   Headings now come from the NCX, then the EPUB3 nav, then `<hN>` — the same
   order and the same code (`EpubParser.ParseNcx/ParseNav/ResolveToc`, which took
   a path-resolver argument so one implementation serves both the in-zip document
   path and the unpacked hybrid one) that §8e already chose for a plain EPUB, and
   for its reason: raw `<hN>` is wildly inconsistent. On this sample it yields 23
   entries against `<hN>`'s 24, the difference being the book's own title sitting
   at 0.0 s as a heading. A book imported as a document and as a hybrid now gets
   the same chapter list.

   **Regression-checked across the whole library, before and after** (a stashed
   build for the baseline): of seven books **only the two EPUB hybrids changed**,
   both from `dh=0` to a full list (24 and 21). Every DAISY, M4B and text book
   came back byte-identical.

   **And it was still not enough — a third layer, found by Gordan testing it.**
   With the headings built and on the clock, Go To *still* offered `aud001`. The
   navigation is gated in three places by **the format instead of the headings**:
   `GetPlayerType`, the Go To list builder, and `DaisyHeadingIndexAt` (which
   feeds the title bar and the info box's Chapter line) all read
   `IsDaisy && DaisyHeadings.Count > 0`. **A narrated EPUB is a hybrid, not a
   DAISY**, so `IsDaisy` is false and all three fell through — to `MultiAudio`,
   which navigates by parts, which are the audio files. Same symptom as the
   original report, one layer further down than the fix for it reached.

   All three test the headings alone now. `DaisyHeadings` is the generic store of
   "named positions on the audio timeline" — its name is as historical as
   `M4bChapters`' — so having any is the whole qualification, and a DAISY with no
   headings still falls through exactly as before. Measured over the library:
   `1ep_001` is `daisy=False, dh=23`, so it qualified under neither half of the
   old test and qualifies now; **it is the only one of seven books whose
   behaviour changes**. `Distribution` (`daisy=True, dh=148`) passed before and
   passes now.

   **The lesson is the one this whole item keeps teaching:** every layer asked
   "what format is this?" when the question it needed answered was "does this
   book have named positions?". Three copies of one wrong test, each hiding the
   next.
2. ~~**Granta Portugal's text side is nearly empty**~~ **— INVESTIGATED AND
   HANDLED 2026-08-04. Nothing was wrong with the parser: the text is not in the
   file.** The note guessed that "its ids do not match what the XHTML yields".
   Measured instead, across all 22 content documents: **10 787 self-closing
   `<span id="dtb_…"/>` anchors, none of them with any content**, and **712
   characters of readable text in the whole book** — the 21 chapter titles in
   their `<h1>`s plus the nav document's own list. The 30–59 kB documents are
   `id` attributes and nothing else.

   Two things say it was deliberate. The stylesheet is **19 bytes**:
   `div{display:none}`. And the book carries **`tpbnarrator.res`** — TPB
   Narrator, the Swedish talking-book agency's production tool, which builds an
   EPUB3 whose text layer exists only to hang media overlays on.

   **Gordan's call: such a book imports as ordinary multi-file audio, not as a
   hybrid**, because it sets two traps and the second is the worse one:
   - a reader who turns on braille or the reading window is promised text there
     is none of;
   - **a reader who opens an `.epub` at all expects a book to read.** They may
     not know narrated EPUBs exist, and what arrives is an audiobook. *"Morat ću
     se u Helpu i na još kojem mjestu malo jače ograditi od gluposti koje rade
     producenti knjiga."*

   **The navigation survives, which is the part worth keeping.** The chapter
   titles are put on the audio clock through the sync map that was just built,
   in the same store a CUE sheet uses (§8f — chapters at times; the `M4b` name
   there is historical). Go To lists *"A Casa Abandonada"* instead of
   *"aud005.mp3"*. `PlayerFormatLabel` reads `Format`, not the player type, so
   the book still calls itself EPUB.

   **The test is the body, not the total:** how much text there is beyond the
   chapter titles, since a skeleton still carries those. The two real samples are
   three orders of magnitude apart — about **40** characters of body against
   **~125 800** — so the threshold is not a fine judgement and does not pretend
   to be.

   **Measured after the change:** the skeleton comes back from a cold reload as
   `hybrid=False, text=False, m4b=True`, 21 parts and 21 named chapters, with no
   `content.txt` and no `sync.map`; the real hybrid is untouched — `hybrid=True`,
   both files written, 23 headings.
   **Note for the library:** a book already imported keeps the shape it was
   imported with. The copy of this one in the library is still a hybrid and has
   to be re-imported to pick this up.
3. ~~**The surface stops refreshing after a large seek**~~ **— FIXED 2026-08-04,
   and it was not really about seeking.** Two bugs, both in the chunk logic, both
   left over from when the whole book really was in the control (§8l).

   **The one Gordan reported.** `UpdateReadingSurface` computed `start` as an
   offset into `readingText` — the whole book — and then bounds-checked it
   against `tbReadingSurface.TextLength`, which since chunking is the length of
   the **~5000-character chunk**. So the moment the reading passed the end of the
   loaded chunk, the method returned **one line before the `EnsureChunkFor` that
   would have loaded the next one**, and nothing could ever put it right: surface,
   highlight and braille stopped together and stayed stopped. A large seek trips
   it instantly, which is how it was noticed; ordinary reading trips it too, just
   later. Now bounded against `readingText.Length`; the clamp to the chunk still
   happens three lines below, where the offset is relative to the chunk and means
   something.

   **The one nobody had reported, and it is worse for braille.** While the caret
   sits in the first or last quarter of the BOOK, neither of `EnsureChunkFor`'s
   guards returns — there is no more text to slide towards — so the recomputed
   window comes out **identical** and the old code assigned `Text` anyway, on
   every tick. §8l measured what that costs: replacing `Text` **freezes** braille
   (the display sat on one sentence for 35 seconds) and resets the caret the
   reader is tracked by. So the opening minutes of every hybrid were the one place
   braille could not work. Guarded now: same window, no assignment.

   **Measured on the real books in the library**, replaying the old guard against
   their own sync maps:

   | book | froze at | never followed |
   |---|---|---|
   | `1ep_001` (126 230 chars, 178 min) | **11.3 min** | 93.7% of the book |
   | `Distribution` (1 214 431 chars, 1313 min) | **9.2 min** | 99.3% of the book |

   11.3 minutes against Gordan's "~15 minutes in" is the same event. The second
   bug measures at **~5.2 minutes of frozen braille** at the start of each of
   them. `2ep_001` never tripped either bug — it is item 2 below, 377 characters,
   shorter than a single chunk.
   **Not verified on a display**; that goes with the rest of the eyes-and-hands
   list in §11.
4. `Ctrl+C` in the reading surface copies nothing, because the surface never
   SELECTS anything by design (§8l — a selection is read aloud over NBR's own
   voice). Decide whether a bare Copy should take the current paragraph.

---

## 10i. Beta 1 is out — the repo, the installer, the release (2026-08-23)

**`github.com/gradic76/nemoviz-book-reader`, PUBLIC**, at Gordan's word. That is
what GPL v3 asks once a binary is distributed, and it is also what makes
Releases reachable at all — a private repo's releases are not public, so the
installer could not have gone out from one.

**The version label is a PAIR and both halves must move together**:
`Dialog.About.Release` is prose the reader hears and is translated into all
eleven; `UpdateCheck.Release` is the git tag, compared character for character.
Beta 1 / `beta-1`. Leave one behind and every reader is told there is an update
that does not exist.

**The installer is Inno Setup 6.7.3** — `installer\nbr.iss`, compiled with
`"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\nbr.iss` after a
Release build. **18.9 MB against 63 MB installed.** Chosen over NSIS because it
does a genuinely silent install, which is what Store policy 10.2.9 demands of an
.exe — the same script serves the Store later rather than being written twice.

- **It packages `bin\x64\Release` WHOLE and EXCLUDES**, never lists. A
  hand-written list is the shape that drops the one braille table or the one
  `.lang` somebody needs, and it surfaces on their machine rather than ours —
  §10e′'s rule again. The exclusions are the point: `*.pdb` and the five files
  NBR writes beside itself (`Settings.ini`, `nbr-services.dat` with the API keys
  and the Azure pair, the two fetched voice catalogues, `CloudUsage.ini`). None
  can appear in a clean Release build; they are named anyway, because "cannot
  happen" is a poor reason to risk shipping somebody's API key.
- **x64 only**, and not as a preference: libmpv is 64-bit and a 32-bit process
  loading it dies with 0x8007000B.
- **Nine of the eleven languages.** Inno ships five; Croatian, both Serbians and
  Esperanto are among its UNOFFICIAL translations and are **vendored into
  `installer\languages`**, so the script compiles on any machine with Inno and
  nobody has to copy files into Program Files first. Latin and Ancient Greek have
  no Inno translation and none was invented. **An Inno language name takes no
  hyphen** — sr-Cyrl is `srcyrl`.
- **Verified by installing and uninstalling, not by compiling.** Everything
  arrives (11 `.lang`, 11 manuals, 480 braille tables, the 32-bit speech host,
  the four licence texts); none of the six excluded files does; and **the LIBRARY
  survives the uninstall**, which is the only part of an uninstall worth testing.
- **NOT code-signed.** SmartScreen calls the publisher unknown. The README and
  the release notes say so plainly rather than letting a reader meet it cold. A
  certificate is the next real cost.

**THE FIRST PUSH WAS REFUSED, and by the HISTORY rather than the working tree.**
Two old versions of `libmpv-2.dll` — 114.8 MB (the GPL build) and 93.6 MB (the
LGPL one before audio-only) — exceed GitHub's hard 100 MB per-file limit. Only
that file, in three commits. Fixed by rewriting the history to drop exactly those
two blobs **by hash**, keeping today's 30 MB one and all 526 commits with their
messages, after a full `--mirror` backup clone. 95 MB → 21.8 MB.
**Check before creating a repo next time:**

    git rev-list --objects --all | git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' | awk '$1=="blob" && $3>45000000'

`filter-branch` leaves the originals on `refs/original/`, so the old blobs stay
reachable until those refs are deleted, the reflog expired and `gc --prune=now`
run — measuring the size before that step says nothing.

---

## 10j. Where a reader's own things live (2026-08-23)

**`%APPDATA%\Nemoviz Book Reader`, and the program in Program Files** — Gordan:
*"Konvencija je jos od Win 95 da aplikacije idu u Program Files… korisnicke mape
s individualnim postavkama idu u User folder."* Installing per user was
sidestepping the fault; `UserData.cs` fixes it.

**The split is by whether NBR WRITES it, not by what the thing is.**

| where | what | why |
|---|---|---|
| beside the exe | 480 liblouis tables, 11 `.lang`, 11 manuals, fonts, licences, `TtsHost32.exe`, the DLLs | read-only, and one copy shared by every account |
| `%APPDATA%\Nemoviz Book Reader` | `Settings.ini`, `Dictionaries\`, `nbr-services.dat`, the two voice catalogues, `CloudUsage.ini` | written at runtime, and private to the person |
| inside the BOOK folder | `Book.ini`, `sync.map`, `content.txt`, the speech cache, `translation-glossary.txt` | what makes a library copyable to another disk complete |

**Roaming, not Local**: these belong to the PERSON and should follow a domain
profile between machines — the cloud counter included, since a free allowance is
reckoned per ACCOUNT and somebody on two machines should see one running total
rather than two that each look comfortable.

**THE KEYS COULD NOT STAY BESIDE THE PROGRAM**, which was the one part of
Gordan's proposed split that could not work, and two measurements settle it:
Program Files carries `BUILTIN\Users : ReadAndExecute`, so **every account on the
machine can read what is there**, while `%APPDATA%\Roaming` grants only the owner
and SYSTEM; and they are written at runtime, so a folder no ordinary process may
write to cannot hold them at all. Beside the program they would be both
unwritable and readable by everyone.

**Migration runs from `Program.Main` before anything reads a setting** —
`Form1`'s constructor builds an `AppSettings`, which would otherwise hand an
upgrading reader a fresh install with no library location, no voices and no keys.
It **copies, never moves** (the program may now be somewhere it cannot delete
from, and the reader's only copy must not be the one in flight) and **never
overwrites** (so running an old build once more cannot let its settings reach
forward).

**A probe testing that migration has to RUN FROM the old folder**, because it is
defined in terms of `AppDomain.BaseDirectory` — which inside a probe is the
PROBE's folder. The same trap made one harness report the braille tables living
in the scratchpad, and an earlier one report every language falling back to
English.

**The uninstaller now deletes nothing of the reader's.** With the files in the
profile, removing them would delete somebody's work from a place Windows treats
as theirs. Checked rather than assumed that nothing else is left behind:
`SignalTones` and `SapiWavPlayer` both use `Path.GetTempPath()`, and the speech
cache is inside the book.

---

## 11. TODO (open items)

### FIXED: the braille markers were a WRONG TABLE, and the round trip is what tells them apart (2026-08-28)

§11 asked what `ghBraille`, `ComSaint`, `Cdd`, `Vdd` are. They are **UEB indicator
cells read with an EBAE table** — the book is written in one English standard and
back-translated with another. Nothing was missing from the tables and nothing was
wrong with the cell map; the table CHOICE was wrong, and it is now chosen in two
stages instead of one.

**Measured over 88 braille books, before and after, through the shipped parser:**

| | prije | poslije |
|---|---|---|
| marker tokens (`ghX`, `ComX`, `CddX`…) | 383 | **0** |
| symbols a wrong table invents (`< > ~ ^`) | 17 959 | **12 106** |
| untranslated-cell notation reaching the reader | 2 997 | **0** |

`1670702.brf` — *The Yield*, Tara June Winch, produced by the Australian Braille
Writing Association, and **Australia has been UEB since 2005** — now reads
*"I cycled out to Massacre Food Mart and I bought milk and a $1 Harbour Bridge
scratchie"* where it used to read `"ghBraille House"ar`.

**53 of 88 books change table; the six Croatian ones do not.**

#### Why the existing scorer could not be repaired

**It is not blind here, it is biased.** EBAE expands UEB's indicator cells into
contractions, so it produces MORE letters and MORE common English words than the
correct table — and `Plausibility`'s letter and stopword terms both reward it for
the damage. On `1670702` it won by **0.032**. Measured: **with the junk term
removed entirely the wrong table still wins**, so no reweighting of the existing
terms can fix it. Three candidate reweightings were measured across the corpus and
all three failed; do not re-try them:

1. **Weight the mid-word capital more.** No safe weight exists — anything that
   flips the English books also flips French grade 2 → grade 1 on books that are
   right today, and the target book never flips at any weight up to 200.
2. **Penalise characters that never occur in prose** (`< > ~ \``). Ranks the
   CORRECT table worse: its escapes are made of backslashes.
3. **Score the whole book rather than the sample.** See the method note below.

#### The fix: plausibility picks the LANGUAGE, the round trip picks the STANDARD

`BrfParser.RefineStandard`, on top of the unchanged `Plausibility`.

**The round trip** takes the detector's own sample, back-translates it with a
table, translates the result straight back with the same table, and compares the
two as **multisets of words**. The wrong table has nowhere to hide: the book
spells `BY` out in full and EBAE writes it as the single cell `0`, so that word
cannot come back. Read off the shipped tables rather than from memory — *"by the
way"* is `BY ! WAY` under UEB and `0! WAY` under EBAE; likewise *table*
(`TABLE`/`TA#`) and *o'clock* (`O'CLOCK`/`O'C`). `LibLouis.Translate` binds
`lou_translateString` for it; it is the only reason NBR ever translates text INTO
braille.

**Words, not cells.** A cell-by-cell LCS was tried first and is not usable: it
depends on exactly how liblouis's `\NNN/` escapes are stripped, and changing that
detail flipped the answer. Word multisets have no alignment to lose.

**Same language only, and this is load-bearing.** An uncontracted table is close
to an identity map — cells to letters and straight back — so it round-trips at
95 % and better on any file whatever. Measured: letting the round trip choose
freely handed **44 of 93 files to Croatian**, Thai and Korean ones included. It
answers *"does this table explain these cells"*; it has no opinion about what
language they are in and must not be asked for one.

**Two bars, both measured** (`MinAdvantage = 6.0`, `MinAgreement = 70.0`). The
Valentin Haüy library ships the same title contracted and uncontracted: the
uncontracted editions win by **+63 to +73 points** while the contracted ones are
mis-preferred by at most **+4.8**, so 6 separates them with room to spare. The
absolute bar catches the rest — the Ukrainian and Korean files have no table here
at all and their best reaches only 61–65 %, where every genuine correction lands
at 70 % or better and the English ones at 92–99 %.

**Cost: 125 ms a book**, measured over the corpus, against ~90 ms before.

#### It closes §10g's `Haüy` bullet as well

The French `_INT_` (intégral) editions were being read with the contracted table.
They now detect as `fr-g1` and read *"Presses de la Cité … Association Valentin
Haüy"* instead of *"Brûleez tout … Haouy"*; the `_ABR_` editions are genuinely
contracted and correctly stay on `fr-g2`.

#### And the escapes finally became worth stripping

`StripUntranslated` now also drops liblouis's `\NNN/` notation for a cell it could
not back-translate. **That was pointless before and is not now**, which is the
whole point: with the wrong table usually winning the notation appeared 2 997
times, because a wrong table does not fail honestly — it reads an indicator cell
as a contraction and produces a word. With the right table chosen it appeared
**36 680** times, i.e. honest failure became visible where silent damage used to
be, and a reader would have heard *"backslash four six slash"* through the book.
Safe by construction: nothing in prose writes a backslash, digits and a slash with
no spaces.

#### Two method notes worth more than the fix

**`Detect` scores `Sample(pages)`, not the book.** The first analysis rescored
whole back-translated books and predicted two flips; the shipped detector produced
neither. Any future rule must be tested through `Detect` itself.

**Read the tokens, do not count them.** What settled which table was right was
reading them: `en-g2`'s 23 suspicious tokens are `2nd`, `£5`, `70cm`,
`McDonalds`, `GoPro`, `1838—1st` — all real English — while `en-us-g2`'s 237 are
`~7yarran`, `>3tinent`, `Prot~steg~ste`, `ComSaint`. The counts alone said the
opposite of the truth.

#### Also fixed in passing: typography was being charged as junk

`Plausibility`'s whitelist was `".,;:!?-'\"()[]«»…"`, so an em dash, a curly quote
and the `* * *` scene break each cost 3 points — **22 344 legitimate characters
across 88 books**. `BrfParser.Punctuation` covers them now. On its own it changes
no book's table (verified through the real `Detect` before and after); it is
correctness, not the repair.

#### Still open

- **Languages with no table of their own.** The Portuguese books (Biblioteca
  Nacional de Portugal) improve — their bodies now read as correct Portuguese
  where EBAE gave `naro havia censura prforvia` — but `en-g1` renders their
  capital sign as a Greek letter, so the title reads `βiblioteca νacional`. There
  is no Portuguese table in the curated set; the per-book picker is the remedy,
  as §10g already says for Korean, Thai and Vietnamese.
- French `<auteur>` markup still arrives as text, now as `àauteurù`.

### PARKED, WITH THE MEASUREMENTS DONE: the RHVoice voices ignore volume below 10 (2026-08-28)

Gordan, reading a text book with **Dragana**: turned the volume down to zero and
the speech was still there. It is not ours — it is the driver — but it hits
precisely the voices Croatian and Serbian books are read with.

**Measured by rendering one sentence at each volume and taking the peak sample,
so no ear and none of our code is in the chain:**

| volume | 100 | 50 | 10 | 1 | 0 |
|---|---|---|---|---|---|
| **Dragana** | 0.91 | 0.51 | 0.256 | 0.256 | **0.256** |
| **Karmela** | 0.93 | 0.59 | 0.296 | 0.296 | **0.296** |
| **Marija** | 0.93 | 0.61 | 0.303 | 0.303 | **0.303** |
| Zira (Microsoft) | 0.76 | 0.18 | 0.057 | 0.006 | **0.0001** |

All three RHVoice voices **stop obeying SAPI's volume below 10 and sit at about
30 % of full amplitude**, which is plainly audible. The Microsoft voice goes to
silence properly. So the bottom tenth of the scale does nothing on the voices
this project cares most about, and "zero" is not zero.

**Why the app cannot fix it where it stands:** a 64-bit SAPI voice speaks LIVE
through `voice.Speak`, i.e. through the driver, and the driver is what ignores
the number. The buffered path — render, then play through mpv — is the one where
volume really works, and today only the cloud backend and the 32-bit host use it.

**THE EXPORT IS NOT AFFECTED, and this was checked rather than assumed** (his
question): `SpeechExportForm` renders through `ISpeechRenderer` → `Sapi5Backend.
Render`, which **sets `renderVoice.Volume = 100` itself** before rendering
anything. The player's current volume never reaches an exported MP3 — a recording
is a record, not what you happen to be hearing. RHVoice voices make MP3 books
normally.

**Two ways out, both costed, neither built — Gordan parked it 2026-08-28
("ajmo mi to zasad ostaviti kako jeste"):**

1. **Small: zero means silence.** At 0, stop sending the sentence to the driver
   and cut what is sounding; raise the volume and it resumes where it stopped.
   Ten lines, touches no timing. Fixes only the endpoint — 5 % and 10 % still
   sound identical on these voices.
2. **Large, and it is Gordan's own idea: put live local speech through our own
   output**, rendering each sentence and playing it through mpv exactly as
   cached and cloud speech already are. Volume and speed then work regardless of
   the driver, the whole scale becomes real, and local voices gain the cache and
   therefore a much faster export. **Measured as feasible: Dragana renders 35×
   faster than she speaks** — 692 ms for 24.0 s of speech, worst single sentence
   409 ms (first, so it carries the voice's start-up), the rest 16–130 ms — and
   the existing look-ahead hides even that. The cost is that it moves the timing
   of reading itself, which is where the stolen sentence-beginnings of §8g′ came
   from, so it wants its own day and a test across all three voices plus the
   32-bit eSpeak.

### WHAT IS LEFT TO TEST, as of 2026-08-16 (Gordan's own list)

He worked through the whole untested list that day. **Cleared, by ear, nothing
outstanding:** the message dialogs under classic (the `?` hint and the bulk
import Continue both read properly — the one real risk of the parity change),
the classic player's **tab order**, and the **EQ band names** (a reader hears
"5 kHz and above" off the accessible name while the caption says "5k+"). OCR had
nothing new to test — no new samples.

**These two are what remain:**

1. **Everything under "speech and export", RE-TEST** — because four faults were
   found and fixed on 2026-08-16 after he reported them, and none of the fixes
   has been heard yet: the look-ahead now following playback rather than the
   book being open, its being voice-aware, the info box tab-stop leak, the
   32-bit host's stand-down, and the export's corrected time estimate and
   wording. See §8g′.
2. **The translator**, untouched since 2026-08-15: the heartbeat during long
   silences, the **Azure last-resort marker that has never once appeared in a
   real book** (the test is to park the DeepSeek key and run a short book), and
   the widow/orphan seams at chunk and chapter boundaries.

**Verified in passing and worth not re-deriving:** the exported audiobook was
walked frame by frame — one Xing header, claiming exactly what the frames add up
to. NBR's own shorter figure is the WPM estimate, not a measurement. See §8g′.

- ~~**The braille settings are largely inert, and one of them lies**~~ **— DONE
  2026-08-04.** Gordan reasoned it out from the tables, the code confirmed every
  part of it, and the cleanup is in. What the trace found and what became of it:
  - `LibLouis.cs` binds **only** `lou_backTranslateString`. There is **no**
    text→braille translation anywhere in NBR, so an output table could not work
    even if something wanted one. **Unchanged — and it does not need to change.**
    **The screen reader owns the translation**, using the table set in its own
    braille settings. Two tables and one display would only ever disagree.
  - `BookData.TextBrailleTable` and the "Braille table" combo it fed in **both**
    Settings and Properties: **removed.** It was written to the ini and read back
    by the dialogs that offered it, and by nothing else.
  - `SettingsForm.chkBraille` was never loaded from and never saved to
    `AppSettings` — there was no braille field there at all — so it reset on
    every open and its only effect was greying the combo beside it. **`AppSettings.
    Braille` now exists** (`[Visual] Braille`, beside `Use`), the box reads and
    writes it, and **a book with no braille setting of its own inherits it**,
    exactly as `TextVisual` inherits `Visual`.
  - Braille was sent **unconditionally**: `PushBrailleIfFocusLeft` tested focus
    and no setting, so a reader who unticked the box still got braille. **It
    tests `currentBook.TextBraille` now** (Gordan's call: *"ako kvačice ima da
    lovi fokus i šalje na redak, ako nema ne radi ništa"*).
    **But be precise about what the switch can mean.** It governs the one braille
    channel NBR owns — the sentence pushed when focus has wandered off the text.
    While the reading surface itself holds focus the screen reader brailles that
    control by its own tracking, which NBR neither asks for nor could prevent. So
    the only complete way to have no braille is to leave the reading window shut.
  - A fault the trace did not name, found while removing the combo: the output
    combo **overwrote `book.BrailleTable`** — the live import table — three lines
    under a comment in its own group saying in as many words that it must not.
    On "Detect from the book" (index 0) that erased it to `""`. Both gone.
  - What is live now: `BookData.BrailleTable` (the liblouis table a `.brf` was
    back-translated with — set by `BrfParser`, carried on import, **and written by
    nothing else**) and `BookData.TextBraille` (per book: drives
    `OpensReadingWindow` and gates the braille push).
  - **Tables are an IMPORT concern, and the table is spent when `content.txt` is
    written.** Gordan put it as a question — *"Braille table nakon toga više nema
    nikakvog smisla, ili se varam?"* — and he is not wrong: nothing re-translates
    a `.brf`, so after import the field is a record of how the book was read, not
    a setting. **Consequence for §10g's misdetected English table: the place to
    correct it is the import, not a dialog.** Building that is still open.
  - Verified by probe: the rule round-trips through `Settings.ini`, a fresh book
    inherits it, an explicit per-book setting still beats it, `Book.ini` no longer
    carries `TextBrailleTable`, and the import table survives a save/load.
    **Not verified: how it reads on a display** — that is the deep test below.
- **JAWS AND BRAILLE: THE READING WINDOW BEING MODAL IS THE FAULT** (found by
  elimination on Gordan's FS Focus, 2026-08-10). Six throwaway programs, kept at
  `D:\Player\Braille A-B test\` — extend them, do not rebuild them:

  | | what it changed | result |
  |---|---|---|
  | **A** plain `TextBox`, ordinary window | baseline | **works** |
  | **B** `RichTextBox`, ordinary window | control type | **works** |
  | **C** re-parented into a **modal** bordered window | + modal, + re-parent | **fails** |
  | **D** built in a **modal** borderless window | + modal, + borderless | **fails** |
  | **E** re-parented into a **non-modal** window | modal removed | **works** |
  | **F** built in a **non-modal** borderless window | modal removed | **works** |

  - **The `RichTextBox` is innocent.** §8l has carried the worry since
    2026-08-03 that the switch from `TextBox` broke JAWS. It did not — B works.
    Drop the suspicion rather than keep testing round it.
  - **Re-parenting is innocent** (E), even though it recreates the control's
    window handle; and **borderless is innocent** (F).
  - **The change to make is `Show()` rather than `ShowDialog()`** for the reading
    window, with everything that follows: who owns Escape, what the player does
    while it is up, and whether `GiveSurfaceBack` still runs at the right moment.
    Not done — it is a real change to a window a reader lives in, and it wants
    doing awake.
  - **Open and NOT the same fault:** even in E and F "rečenica se reže". In A/B
    that was the SPEECH — the plain box cut it, the rich one read the whole
    sentence. Unexplained; do not fold it into the modal finding.

  **Method note.** C and D were built to separate two variables and both held a
  third constant — `ShowDialog`. **Two tests that fail identically point at the
  variable you did not vary.** E and F exist only because that was spotted
  afterwards, and they are what produced the answer.

- **THE SKINNED MESSAGE BOX STEALS INITIAL FOCUS FROM THE FIELD — check every
  dialog that has one** (found 2026-08-10). Six password-protected archives came
  out of a bulk import marked "cancelled" when Gordan had typed every password
  and pressed Enter.

  The chain: `WorkDialogSkin.ApplyPassword` replaces the prompt Label with
  `DialogSkin.NewMessageBox`, a **read-only but FOCUSABLE** TextBox, and gives it
  `TabIndex = 0`. A Label cannot take focus and a TextBox can, so WinForms opened
  the dialog with focus on the MESSAGE. The characters went nowhere, Enter fired
  the accept button, `tb.Text` was empty, `Show` maps empty to null, and
  `ExtractArchive` throws `OperationCanceledException` for a null password — so
  the reader was told they had given up on a book they were trying to open, with
  nothing visible or audible at any point in between.

  **Two fixes, and the second is the general one.** Focus is now put in the field
  on `Shown`; and an empty field no longer accepts at all — the dialog stays open
  and says so, because in a password box there is nothing to see either way.
  Verified by driving the real dialog: focus lands on "Archive password",
  `SendKeys` reaches it, and Enter returns the typed string.

  **The trap is not specific to passwords.** §10c's message shell turns prose
  into a focusable read-only TextBox everywhere, which is right for reading —
  §8b's rule that a reader driven by Tab never visits a Label — and wrong for
  initial focus in any dialog whose task is a FIELD. Anything using
  `NewMessageBox` beside an input wants an explicit `Shown` focus. Worth auditing
  the rename prompt and `ConfirmOnceForm` on the same grounds.

- **OPEN: a bulk folder import blocks the UI for a minute or more, and it is not
  a hang** (2026-08-10). Gordan imported the whole of Test naslovi into an empty
  library and the app went "not responding" after an archive password prompt.
  `UiWatchdog` caught five samples of the same stall and they are in **five
  different places** — a regex in `TextCleaner`, another regex mid-scan,
  `File.InternalReadAllBytes`, `LibLouis.lou_backTranslateString`, and the first
  regex again — with the UI thread **Running at 88 % of a core**. Nothing is
  stuck. It is `ImportFolder` doing the whole job synchronously from
  `MenuFileOpenFolder_Click`, so no message is pumped until every book is
  parsed, cleaned and back-translated. The reports doubled 5 → 10 → 20 → 40 → 80
  s and then stopped, so it finished somewhere between 80 and 160 seconds.

  **This falsifies §8a's line that the post-extract steps are "seconds, not
  minutes".** That was true of one archive and is not true of a library's worth.
  §8a already puts EXTRACTION behind `ExtractProgressForm`; everything after it —
  per-book parsing, text cleaning, liblouis, the rescan — has neither progress
  nor a background thread.

  **Not fixed, and deliberately not fixed on the spot**: it is a real change to
  the Library, which §9 nails down except for faults found in use — this is one,
  but it is a piece of work rather than a patch. The shape it wants is the whole
  bulk import behind the progress dialog that already exists, reporting book by
  book. **Cheap thing worth doing first either way: a breadcrumb per book in
  `ImportOne`,** so the next capture of this says which title it was on instead
  of stopping at "rebuilding the shelf".

  **AND IT LEFT SIX NON-BOOKS ON THE SHELF, found 2026-08-17.** The library held
  `base_library`, `portable.bouncycastle.1.8.9` and `sharpziplib.1.3.3`, each
  twice — a PyInstaller stdlib bundle of 154 `.pyc` files and two NuGet packages,
  21 MB in all, every one with `Format=Unknown` and neither audio nor text.
  Gordan deleted them; the finding is why they were there.

  - **Nothing of his was destroyed**, which was the first thing to check because
    §9 says `AbsorbArchives` deletes the original: `packages` is whole with all
    17 `.nupkg`, neither package is even referenced by the project, and
    `base_library.zip` still sits in `SlušajKnjigu_Portable\_internal\`. The
    "Open folder" path does not delete, and that held.
  - **NBR never decided they were not books** — Gordan's guess was that it
    realised and then left both copies alone. Nothing in the import asks: an
    archive is a book, so it imported them as books.
  - **The duplicates are `MakeUniqueBookFolder` working as written.** Two bulk
    imports 26 minutes apart; the second found the names taken and appended the
    extension rather than clobber an existing book. Hence `X` and `X (zip)`.
  - **The gap worth naming:** an imported archive with no audio and no text still
    becomes a book. `OcrImport.IsEmptyTextBook` catches the same shape one layer
    later, for a scanned PDF that yields nothing; there is no equivalent at
    import. Not built — recorded so the next bulk import does not re-teach it.

- ~~**OPEN BUG: the player freezes on Ctrl+O in the Library**~~ **— SOLVED
  2026-08-10, and it was never NBR's.** Gordan found it: a **virtual optical
  drive with an NRG mounted**, and that NRG living in OneDrive. Unmounting the
  image stopped it.

  **Confirmed by reading the file attributes**: every `.nrg` and `.iso` in his
  OneDrive carries `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` **and**
  `FILE_ATTRIBUTE_OFFLINE` — Files On-Demand placeholders, metadata local and
  **content not**, at 360–780 MB each.

  **The mechanism, and every observation falls out of it.** The Vista file
  dialog builds its navigation pane by enumerating the shell namespace,
  including This PC and every drive. Querying the virtual optical drive makes
  the drive software read the NRG; that read hits OneDrive's cloud-files filter
  driver, which has to **fetch the bytes over the network**; and the dialog's own
  thread waits on it — inside `IFileDialog::Show`, on an ordinary handle
  (`UserRequest`), at 0 % CPU, before the message loop ever starts. Hence the
  missing "up and pumping" breadcrumb. **And hence the idle gap**: once fetched,
  the bytes are cached for a while, so a repeat within a minute or two opens in
  400 ms, while one a few minutes later pays the download again.

  **It is not an NBR defect.** Nothing above `ShowDialog` in that stack is ours,
  and any application opening a Vista file dialog on that machine would do the
  same — Notepad's Open is a thirty-second confirmation.

  **"But it started when CD support went in" — and that is a COINCIDENCE with a
  cause, not a clue.** Gordan noticed the correlation and it is real: the hard
  freeze appeared around the time `AudioCd` was built. The explanation is that
  **the virtual optical drive was not installed until then** — he put it in to
  test CD support, and mounted NRGs from OneDrive to do it. The drive and the
  feature arrived together. `AudioCd`/`OpticalDrive` touch a drive only on a
  deliberate action (the Library's rip button, the Settings device list) and hold
  no handle open; nothing there polls. **Do not go hunting in the CD code**: this
  note exists because the timing alone is convincing enough to send a future
  session there for nothing.

  **A separate, milder symptom is on record and unexplained**: before any of
  this, Open file would occasionally "hit a wall" — a system ding as if something
  were unavailable, no dialog, no freeze — and worked on a second press a couple
  of seconds later. Gordan's words, and he rates it harmless. Left alone unless
  it comes back.

  **The legacy-dialog proposal is WITHDRAWN.** It was reasoned from the wrong
  cause, and it would not have helped: `GetOpenFileName` enumerates drives for
  its own drive list too. It would have cost the modern dialog for nothing.

  **The method lesson, which is the part worth keeping.** The instrument I built
  to crack this — "which foreign modules are injected" — could only ever accuse
  an injected DLL, and it duly accused NVDA, then JAWS, then the cloud shell
  extensions. **The actual cause was a FILE, and a file never appears in a module
  list.** What solved it was pairing every open in the log with whether it pumped
  and noticing the gaps separated cleanly at ~100 s — an analysis of data that
  had been sitting in the log the whole time. When an instrument keeps naming
  plausible suspects, ask what it is structurally incapable of seeing.

  Kept below because the reasoning is still sound about what the stack shows:
  **What is already known, so none of it is worth re-deriving:**
  - It stalls **inside `ofd.ShowDialog()`** — the last breadcrumb is "showing the
    file dialog" and nothing follows.
  - **CPU 0 %**, so the thread is WAITING, not spinning. That rules out a loop, a
    repaint storm and a corrupted collection.
  - **The folder is not the cause.** The same dialog opened on the same folder
    twice earlier in the same session and closed normally both times. It is a
    state the app reaches, not a path it takes.
  - Thread count falls 33 → 27 across the stall: nothing is running at all.
  - **It took three cycles.** Each was identical — import a book, load it, open
    the Library, Ctrl+O — and only the third hung. That shape suggests something
    accumulating rather than something happening.
  **CAUGHT WITH A STACK, 2026-08-10.** Two facts are now settled and one of them
  narrows the search a great deal.
  - **The dialog never opens.** `NoteWhenPumping` is a WinForms timer, so it can
    only tick from a turning message loop — and *"file dialog is up and pumping"*
    **never fired**, while `showing the file dialog` did. So this is not "opened,
    then froze": the UI thread is inside `IFileDialog::Show` **before that call
    starts pumping**.
  - **It is not blocked on a call out of the process.** The wait reason is
    **`UserRequest`**, not `LpcReceive`/`LpcReply`. It is waiting on an ordinary
    handle for something *inside* the process to finish — and the shell dialog
    does its start-up work on threads of its own. CPU 0 %, 37 threads, and
    nothing of ours anywhere above `IFileDialog.Show` in the stack.
  - The call site is not the problem: `OpenFileDialog` is built fresh, disposed
    by `using`, and `InitialDirectory` is `Directory.Exists`-checked first.
  - **Ruled out by Gordan, 2026-08-10: two screen readers at once.** He runs one
    at a time — *"they are clashing"* — so JAWS and NVDA fighting over the dialog
    is not it. Do not re-raise it.
  - **What the next capture adds** (`c7c3d41`): every OTHER thread's wait reason
    grouped by kind — one on `LpcReply` would name a provider outside, one on
    `Executive` a disk not answering — and **the modules loaded from outside the
    app folder and Windows**, i.e. the shell extensions, cloud providers and
    anti-virus hooks injected into the process. The file dialog is the one place
    NBR hands control to code nobody here wrote; the same foreign DLL in every
    hang names the cause, and an empty list says the cause is ours.

  **IT NAMED SOMETHING — 2026-08-10 15:29, Gordan's own session.** The list is
  not empty:

  ```
  other threads: 1 Running, 7 Wait/EventPairLow, 4 Wait/Unknown, 14 Wait/UserRequest
  injected modules: nvdaHelperRemote.dll, IAccessible2Proxy.dll, ISimpleDOM.dll, rhvoicesvr.dll
  ```

  **`nvdaHelperRemote.dll` is NVDA's in-process helper**, with the two
  accessibility proxies it brings (`IAccessible2Proxy`, `ISimpleDOM`);
  `rhvoicesvr.dll` is RHVoice, which is ours by choice. So the one participant
  in `IFileDialog::Show` that nobody here wrote, and that hooks the process from
  outside, is **the screen reader**.

  **What that is and is not.** It is the first hard pointer in three weeks, and
  it fits every fact already collected: only Ctrl+O, because that is the one
  place NBR hands off to a shell COM dialog that fires a burst of accessibility
  events as it builds; intermittent, because it is a race between the shell's
  own start-up and a hook in another thread; `UserRequest` rather than
  `LpcReceive`, because the thread is waiting on an event inside `Show` and not
  on a call out of the process; and the dialog never reaching its message loop.
  It is **not** proof — the module list says NVDA is in the process, not that it
  caused the stall, and the same DLLs are loaded on the three opens in that
  session that worked perfectly.

  **A SECOND capture, 15:38 the same day, and it widens the field:**

  ```
  injected modules: nvdaHelperRemote.dll, IAccessible2Proxy.dll, ISimpleDOM.dll,
                    FileSyncShell64.dll, DropboxExt64.96.0.dll, rhvoicesvr.dll
  ```

  **`FileSyncShell64.dll` is OneDrive's shell extension, `DropboxExt64` is
  Dropbox's.** Those load when the shell namespace is used — which is to say
  when this very dialog is built — and a cloud-sync extension waking up and
  calling its own service is a textbook way for `IFileDialog::Show` to sit
  before it pumps.

  **So there are now two candidates, and the evidence splits them:**
  - NVDA's trio and RHVoice are in **both** hangs.
  - The two cloud extensions are in the second only. The 15:29 hang had neither,
    and in that session no file dialog had opened yet — so they had not been
    pulled in. **The cloud extensions are therefore not necessary for the
    hang**, though they may make it likelier.
  - In both sessions, earlier opens with the same modules loaded worked. Nothing
    here is sufficient on its own; it is a race.

  **The tests, and they answer different questions — do not confuse them:**
  - **No screen reader at all** is the clean one. If it still hangs, every
    reader-hook theory dies at once.
  - **Switching NVDA → JAWS** (which Gordan did on 2026-08-10) tests something
    narrower, because **JAWS injects too**. Still hanging under JAWS means "not
    NVDA-specific", not "not the reader".
  - Signing out of OneDrive and Dropbox, or opening the dialog on a path far
    from any synced folder, separates the other candidate.

  **THIRD capture, 15:49, under JAWS — and it hung the same way.** Every NVDA
  module is gone and JAWS's are in their place:

  ```
  GlobalHooksDispatcher.dll, jhook.dll, HookManager.dll, GdiHooks.dll,
  AccEventCache.dll, uiahooks.dll, FileSyncShell64.dll, DropboxExt64.96.0.dll,
  rhvoicesvr.dll, FSDomNodeRichEdit.DLL
  ```

  Same stack, same `UserRequest`, same missing breadcrumb while the earlier open
  in that session has it. **So NVDA-specific is dead.** What survives all three
  hangs is: a screen reader is hooked into the process — `nvdaHelperRemote` in
  one, `jhook`/`GdiHooks`/`uiahooks` in the other — and the stall is inside the
  shell dialog's start-up, before it pumps.

  **What this does NOT say.** A reader is hooked in during the successful opens
  too, so its presence is not the trigger; something races. Nothing here
  identifies which hook, and nothing here is a defect in NBR: the whole stack
  above `IFileDialog.Show` is ours only as far as `ShowDialog`.

  **THE PATTERN, found 2026-08-10 by pairing every open in the log with whether
  it pumped. It is not the count — it is the IDLE GAP.**

  | gap since the previous open | opens | result |
  |---|---|---|
  | 8, 10, 13, 33, 34, 36, 95 s | 7 | **all opened** |
  | 153, 218, 455, 464, 468 s | 5 | **all hung** |

  Clean separation with no overlap, across four sessions and both screen
  readers. Gordan's impression that it went wrong "after the third or fourth
  open" was the symptom: rapid repeats while testing succeed, and the one he
  came back to after a few minutes of doing something else is the one that
  hangs. That also explains why the failing open was the 1st in one session,
  the 2nd in two and the 4th in another — the count never mattered.

  **What behaves like that: something initialised on first use and TORN DOWN
  after an idle timeout, whose re-initialisation is what deadlocks.** That is
  exactly how an out-of-process COM server with an idle shutdown behaves, and
  how a shell/cloud extension host behaves when it is unloaded and has to come
  back. It fits the rest of the evidence — a wait on an ordinary handle inside
  `Show`, before the dialog pumps, with the process's foreign modules being
  cloud providers and reader hooks.

  **And it makes the bug reproducible on demand, which it never was before:
  open Ctrl+O, cancel, wait three minutes, open again.** Any test of a candidate
  cause or of a fix should use that recipe rather than hoping.

  **The conclusion the evidence supports, and the reason to stop diagnosing.**
  Both surviving candidates — reader hooks and cloud shell extensions — are
  things the **Vista dialog** brings in and the **legacy one does not**.
  `AutoUpgradeEnabled = false` gives `GetOpenFileName`: a plain Win32 window,
  no shell COM object, no cloud provider, and a fraction of the accessibility
  traffic. For a blind-first app that is arguably the better dialog anyway —
  simpler, no navigation pane, and twenty-five years of reader support. It costs
  a plainer look and nothing functional: `InitialDirectory`, `Filter`,
  `FilterIndex` and `FileName` all behave the same.

  **The fix if it holds is two lines, and it is already understood.**
  `OpenFileDialog.AutoUpgradeEnabled = false` drops back to the legacy
  `GetOpenFileName` common dialog — a plain Win32 window with no shell COM
  object, no `IFileDialog`, and twenty-five years of screen-reader support
  behind it. NBR sets this nowhere today, so every file dialog in the app takes
  the Vista path. The cost is a plainer-looking dialog with no modern navigation
  pane, which for this audience is arguably not a cost at all — but it is a
  visible change and therefore Gordan's call, not one to make on a hypothesis.

- **A SEPARATE stall, fully diagnosed in the same log and NOT the Ctrl+O one**
  (2026-08-10; different stack, different wait reason, CPU 13 % against 0 %).
  `RebuildShelf` (libraryform.cs:930) sets `ListViewItem.Selected`, which fires
  `SelectedIndexChanged` **synchronously** → `ShowDetails` →
  `EnsureDurationDetails` → `BuildChaptersFromFolder` → TagLib opening **every
  audio file of the book on the UI thread**, waiting on `Executive` inside
  `CreateFile`. Measured from the breadcrumbs: **~18 s per shelf rebuild**, three
  in a row.
  It is **once per book ever** — `EnsureDurationDetails` returns early once
  `Chapters` is built and caches to `Book.ini`, and the log shows later rebuilds
  are instant — so it bites the first time a big newly-added book is selected.
  Still serious for a reader arrowing down the shelf. **Not fixed**: §9 allows a
  bug reported from use, but moving this to a background thread needs care —
  `BookData` is not thread-safe and `SaveChapters` writes `Book.ini`.
- **Waiting on Gordan's own eyes and hands** (list opened 2026-08-03). None of
  these is a suspected fault — they are things that were built, measured and
  found correct by probe, and that a measurement *cannot* confirm:
  - **Settings on the shared three-column frame.** Measured clean on all three
    tabs (no overlaps, nothing outside a group, columns at 12/317/622), but how
    it reads and looks is unmeasured.
  - **The three visual reading modes in motion** — page, two rows, single row.
    A probe can say the right range is painted; only a reader can say whether
    the text moves the way the mode promises.
  - **Braille on the reading surface — the deep test, and now the item the
    braille transport hangs on.** The surface is a `RichTextBox`, chosen partly
    because a real focusable text control is what lets the screen reader braille
    and pan it by its own tracking (§ "The idea worth testing"). Today there are
    **two routes switched by focus**: with the surface focused `PushBrailleIfFocusLeft`
    deliberately stays silent and NVDA brailles the control itself; without it,
    NBR pushes the sentence as a transient `brailleMessage`. Neither has been on
    a display. **If the first route really gives panning and routing keys, BrlAPI
    buys almost nothing; if it does not, it buys everything.** Test this before
    spending another hour on BRLTTY.
    **DONE, ON REAL HARDWARE — 2026-08-08, and the bet paid off.** Gordan tested
    with a display attached, on plain text, EPUB and a DAISY text+audio hybrid.
    **Text and EPUB work**: the line refreshes per sentence or per paragraph, the
    display's own panning keys widen the text by hand, and the whole thing stays
    "u poprilično dobrom syncu". So the route this project bet on — a real
    focusable control, tracked by the screen reader itself, no drivers and no API
    — **is confirmed on hardware**. Panning works. **BrlAPI is therefore not
    needed**, which retires the whole BRLTTY question for Lite; §8l's long
    argument stands as written and nothing further should be spent on it.
    **Routing keys were exercised too, and they reach the reader.** With NVDA's
    "report character under the cursor" switched on, pressing a routing key
    speaks the character it sits on — so the key travels display → reader →
    the surface's text, which is the chain §8l predicted. **What that does NOT
    yet show is NBR's own side**: the surface polls for a caret it did not move
    and logs `ROUTED to <offset>`, and nobody has confirmed that fires or that
    the offset is right. So the input path is proven and our *use* of it is not
    — two different claims, and only the first has evidence.
    **The display's Space starts and stops playback, but not consistently**
    (Gordan). Space is a bare key, so it needs no virtual modifier and should be
    the easy case. Candidates, none of them checked: focus not on the surface at
    that moment, the reader consuming it in browse mode, or the reading window's
    key forwarding. Left as an observation, not a diagnosis.
    **The hybrid was the exception and it is NOT the surface's fault** —
    *"ćudljiv, ne sinka baš, zna zaglaviti i ne micati se"*. Measured: it is the
    resolution of the book's own sync map. See §8l's estimate-between-anchors
    note and `DaisySync.CharAt`.
  - **Light and Dark themes** — deferred by Gordan ("za light/dark ćemo još
    vidjeti"). Remember `SystemColors` does NOT track Windows dark mode; the
    signal is `HKCU\…\Themes\Personalize\AppsUseLightTheme`, and scrollbars,
    combo popups and ListView headers will not obey `BackColor`.
  - **EPUB and the other hybrids across all three ranks — voice, text and
    braille.** A narrated EPUB imports and plays, and DAISY parses, but no
    hybrid has been read end to end in each of the three outputs by a person.
    This is the widest item on the list.

- ~~**A key fires but a keyboard SHORTCUT does not light it.**~~ **— DONE
  2026-08-04.** The backlight was hung on `Button.Click`, so the mouse and
  Enter/Space lit a key while a shortcut did not: those handlers call
  `BtnLibrary_Click(null, …)` **directly** and never raise `Click`.
  `Form1.FlashKey` now lights it and `FireKey` does both, from the one
  `ProcessCmdKey` switch this note always pointed at. **Not `PerformClick()`**,
  as the note warned — it silently does nothing when a control cannot be
  selected, and would route the command through a second path on the classic
  look too. Flashing and invoking stay separate.
  - **Covered:** F1–F7 and Alt+Enter (their eight panel keys), Space
    (the ring centre, from `Form1_KeyDown` and from the reading window's
    forwarder), Shift+Left/Right (which *are* the ring's left and right — the
    skin places `btnBack`/`btnForward` there), Up/Down (the ring's volume keys),
    and the focused **WM_APPCOMMAND** media keys.
  - **Deliberately not covered:** the **WM_HOTKEY** global claim, which fires
    while NBR is in the background and would light a panel nobody is looking at;
    F9, the plain arrows and the speed pair, which have no key of their own.
  - `NewPlayerSkin.RingVolumeUp/Down` had to be exposed: the two volume keys are
    the only controls the skin creates, so they were the one pair Form1 could not
    name. Null under the classic look, which `FlashKey` already treats as
    "nothing to light" along with a null `Canvas`.
  - **Verified by reading, not by eye**: all four paint paths consult
    `RingFor` (`PaintKey`, `PaintSector`, `PaintPlay`, `PaintPower`), and `Flash`
    invalidates both the key and the 22-unit bloom around it, so a flash from any
    caller reaches the screen. **That it LOOKS right is unverified** — it belongs
    on the eyes-and-hands list above.
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
- ~~**Publication year is never extracted**~~ **— DONE 2026-08-04.**
  `BookData.Year` (`[Book] Year`), filled at import from EPUB `dc:date`, DAISY
  `ncc:sourceDate` then `dc:date` — the source date first, because that is the
  print edition a reader means by "the year" — and an audio book's TagLib year
  tag. Shown as **"Publisher (year)"** (`BookData.WithYear`), the shape §8k asks
  for.
  **The fallbacks are the part that earns its keep, and the 2026-07-28 note
  predicted it exactly.** `ResolveYear` tries the date, then the publisher, then
  the title, because a real shelf keeps the year in the wrong field:
  `Catherine Coulter - FBI 01 The Cove 1996` in the library resolves to **1996
  from its title**, having no date tag at all.
  **`BookInfoField.Year` exists for the case with no publisher to hang it on**,
  which is that same commonest case; with a publisher the year rides on that line
  instead, so it is never shown twice.
  **Measured on 14 real EPUBs:** 13 carry `dc:date` and **10 resolve to a year**.
  The three that do not all declare `0101-01-01` — a placeholder — and are
  rejected by the 1400–2100 bound, which is also what keeps an ISBN or a page
  count from passing for a year.
  **Known and left alone:** two samples resolve to 2026, which is when the file
  was made rather than when the book was published. `dc:date` does not say which
  it means, and §8c's rule stands — parse faithfully, do not invent.
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
  ~~**Still to do:** move the main window's shortcuts off letter keys onto
  function keys~~ **— DONE.** Read back off `ProcessCmdKey` on 2026-08-04, since
  this file had gone on listing the letter keys long after they were gone: F1
  Help, F2 Settings, F3 Library, F4 Go To, F5 Set Bookmark, F6 Manage Bookmarks,
  F7 Sleep Timer, F8 info, F9 the reading window; `Ctrl+O` / `Ctrl+Shift+O` and
  `Ctrl+1..9` kept. `en.lang`'s accessible names carry the new keys too
  ("Go To, F4"). The hazards the note listed were all respected — F4 is
  swallowed before a focused `cmbSeek` can open its list, F10 and Alt+F4 are
  untouched, F1 stayed Help. The full list is in §6.
  **Still open from this item:** the tab order for the new layout, and **the
  laptop case** — many laptops default the F-row to OEM media/brightness, so
  without Fn-lock every shortcut needs Fn held. That has not been checked on one.
- ~~Settings → Misc is still an empty placeholder~~ — **gone** (2026-08-03).
  Misc held nothing but the look switch and a "work in progress" line; the look
  moved to General and the tab was deleted.
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
