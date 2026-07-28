using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

namespace Nemoviz_Book_Reader
{
    public partial class Form1 : Form
    {
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_create();

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_initialize(IntPtr ctx);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command(IntPtr ctx, IntPtr args);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(IntPtr ctx, string name, int format, ref double data);

        // ──────────────────────────────────────────────
        // Multimedia keys (WM_APPCOMMAND)
        // ──────────────────────────────────────────────
        // Handled locally (WM_APPCOMMAND): the keys work while any NBR window
        // control has focus. Settings → General switches them off, or claims them
        // system-wide (RegisterHotKey → WM_HOTKEY) so they work from anywhere.
        private const int WM_APPCOMMAND = 0x0319;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        private const int APPCOMMAND_MEDIA_PLAY = 46;
        private const int APPCOMMAND_MEDIA_PAUSE = 47;

        private const int WM_HOTKEY = 0x0312;
        private const int VK_MEDIA_NEXT_TRACK = 0xB0;
        private const int VK_MEDIA_PREV_TRACK = 0xB1;
        private const int VK_MEDIA_STOP = 0xB2;
        private const int VK_MEDIA_PLAY_PAUSE = 0xB3;
        // Our own ids for the registered hotkeys (any small unique numbers).
        private const int HotkeyPlayPause = 9101;
        private const int HotkeyNext = 9102;
        private const int HotkeyPrev = 9103;
        private const int HotkeyStop = 9104;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Every beep the player makes goes through this, so the feedback comes out
        // of the same card as the book rather than wherever Windows sends system
        // sounds — on headphones or a second card those are different rooms.
        private readonly SignalTones tones = new SignalTones();

        private IntPtr mpvHandle = IntPtr.Zero;
        private bool isPlaying = false;
        private string currentFile = null;
        private BookData currentBook = null;
        // Text-book playback engine (TTS). Created lazily on the first text book.
        private TtsReader tts = null;
        private AppSettings appSettings;
        private System.Windows.Forms.Timer eventTimer;
        private System.Windows.Forms.Timer progressTimer;

        private int currentVolume = 100;
        private int currentSpeed = 100;
        // Reading speed for the current text book (words per minute) and its
        // pitch (-10..10). Both belong to the voice in use and are remembered
        // per voice; the player has a control for the speed, pitch is set in the
        // book's Properties.
        private int currentWpm = 175;
        private int currentTextPitch = 0;
        private int currentProgress = 0;
        private int currentPlaylistIndex = 0;
        private bool isLoadingBook = false;

        // Set in the constructor when there's no book to resume (first run,
        // empty library, deleted folder, or the last book was finished) —
        // the app then starts in the Library window instead of the player.
        private bool openLibraryOnStartup = false;

        // Guards against opening a second (nested) Library window, e.g. when
        // a book finishes while the library is already open.
        private bool isLibraryOpen = false;

        // ──────────────────────────────────────────────
        // Sleep timer state
        // ──────────────────────────────────────────────
        // The timer is COUPLED TO PLAYBACK — it exists because someone is
        // listening and plans to fall asleep, it is not a standalone
        // shutdown scheduler. The rules:
        //   * A timer can only be set with something loaded in the player;
        //     otherwise the button gives a short low beep (same as Ctrl+G).
        //   * Starting a timer starts playback if it isn't running.
        //   * A MANUAL pause (Space, X, on-screen button, media keys)
        //     cancels the timer, with an announcement. Programmatic pauses
        //     (cross-file seeks, book loading, the timer's own expiry
        //     action) do NOT — they never route through BtnPlayPause_Click,
        //     which is the only place the cancel hook lives.
        //   * Changing the book (library pick or Ctrl+O) cancels the timer,
        //     with the same announcement.
        //   * Seeking, volume, speed, part navigation and Go To don't touch
        //     the timer — adjusting things while drifting off is expected.
        //   * If the book ends by itself before the deadline, the chosen
        //     action fires immediately (see FinishCurrentBook): for Stop
        //     the "stop playback" part already happened naturally, so the
        //     timer is just quietly dropped; for close/shutdown the action
        //     runs right away, earlier than the deadline.
        // Audible signals: a series of three beeps at -5 min (skipped for
        // timers of 5 minutes or less), then a smooth volume FADEOUT over
        // the last 45 seconds. The fade only touches the mpv volume — the
        // user's set volume (currentVolume, the Volume field, Book.ini)
        // stays untouched and is restored when the timer ends or is
        // cancelled.
        // While active, the countdown itself is wall-clock (a DateTime
        // deadline, not accumulated ticks), so a busy UI thread can't make
        // it drift; playback speed has no effect. Nothing is persisted —
        // a timer is a one-shot, session-only thing.
        private System.Windows.Forms.Timer sleepTimer;
        private DateTime sleepDeadline;
        private SleepTimerAction sleepAction;
        private bool sleepTimerActive = false;
        // True once the -5 min warning series has fired (or when the timer
        // was started with 5 minutes or less — no point beeping instantly).
        private bool sleepWarned5Min = false;
        // Seconds before the deadline at which the fadeout starts.
        private const int SleepFadeSeconds = 45;

        // UI controls — 3×4 grid, columns A (160) / B (320) / C (160)
        private Panel panelTop;
        private TextBox tbInfo;
        private Panel panelBottom;

        // Column A
        private Button btnLibrary;
        private Button btnSettings;
        private Button btnTimer;
        private Button btnHelp;

        // Column B
        private Label lblSeek;
        private ComboBox cmbSeek;
        // The seek dropdown is dynamic: time steps are always there, Part only
        // for plain audio, Heading/Page only for a DAISY book that has them,
        // Bookmark only while the book has ≥1 bookmark. DAISY headings follow
        // the standard talking-book model — one step per heading depth present
        // ("Heading 1", "Heading 2", …), where level N stops at every heading
        // of depth ≤ N. This list runs parallel to cmbSeek.Items (one entry
        // per row) so the selected step is known without fixed indices.
        // New kinds are appended (never reordered) so the persisted ordinal in
        // Book.ini stays valid across versions.
        private enum SeekStepKind { Sec15, Sec30, Min1, Min5, Part, Heading, Page, Bookmark, Sentence, Paragraph, StandardPage, Min10, Min15, Min30, Chapter }
        private struct SeekStep
        {
            public SeekStepKind Kind;
            public int Level; // heading depth threshold (Heading only); else 0
            public SeekStep(SeekStepKind kind, int level = 0) { Kind = kind; Level = level; }
        }
        private readonly System.Collections.Generic.List<SeekStep> seekSteps =
            new System.Collections.Generic.List<SeekStep>();
        private Button btnBack;
        private Button btnPlayPause;
        private Button btnForward;
        private Label lblVolume;
        private TextBox tbVolume;
        private Label lblSpeed;
        private TextBox tbSpeed;
        private Label lblProgress;
        private TextBox tbProgress;

        // Column C
        private Button btnProperties;
        private Button btnGoTo;
        private Button btnSetBookmark;
        private Button btnManageBookmarks;

        // Tooltips — mouse-hover hints with the keyboard shortcuts
        private ToolTip toolTip;

        // Off-screen labels for screen reader announcements
        private Label lblAnnounceVolume;
        private Label lblAnnounceProgress;
        private Label lblAnnounceSpeed;
        private Label lblAnnounceInfo;

        public Form1()
        {
            InitializeComponent();
            appSettings = new AppSettings();
            appSettings.EnsureLibraryExists();
            appSettings.EnsureLangFolderExists();
            Localization.Initialize(appSettings.LangPath, appSettings.LanguageCode);
            BuildUI();
            InitializeMpv();
            tones.SetDevice(appSettings.AudioDevice);
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;

            DecideStartupView();
        }

        // ──────────────────────────────────────────────
        // Startup flow
        // ──────────────────────────────────────────────
        /// <summary>
        /// Decides where the app starts: the player with the last-read book
        /// resumed (normal case), or the Library window — on first run, when
        /// the library is empty, when the last book's folder is gone, or
        /// when the last book was finished on the previous run.
        /// </summary>
        private void DecideStartupView()
        {
            BookData lastBook = null;
            try
            {
                if (!string.IsNullOrEmpty(appSettings.LastOpenedBookPath)
                    && System.IO.Directory.Exists(appSettings.LastOpenedBookPath))
                {
                    lastBook = new BookData(appSettings.LastOpenedBookPath);
                }
            }
            catch
            {
                lastBook = null;
            }

            if (lastBook != null && lastBook.PercentListened < 100)
            {
                try
                {
                    LoadBook(lastBook, false);
                    return;
                }
                catch
                {
                    // Fall through to the library.
                }
            }

            openLibraryOnStartup = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (openLibraryOnStartup)
            {
                openLibraryOnStartup = false;
                // BeginInvoke: let the player window finish painting first,
                // then open the modal library on top of it.
                BeginInvoke((Action)(() => BtnLibrary_Click(null, EventArgs.Empty)));
            }
        }

        // ──────────────────────────────────────────────
        // Multimedia keys — WM_APPCOMMAND
        // ──────────────────────────────────────────────
        // WM_APPCOMMAND bubbles up from the focused child control to the
        // form via DefWindowProc, so handling it here covers the whole
        // window. With no book loaded — or with the keys switched off in
        // Settings — the message is passed through to the system
        // (base.WndProc), so pressing Play/Pause doesn't pop up the Open File
        // dialog and other media apps still react.
        //
        // The GLOBAL mode is a different mechanism: RegisterHotKey claims the
        // media keys system-wide and delivers WM_HOTKEY even when NBR is in the
        // background. It is off by default because claiming them takes them
        // away from every other player on the machine.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APPCOMMAND && currentBook != null && appSettings.MediaKeys)
            {
                int cmd = (int)((m.LParam.ToInt64() >> 16) & 0x0FFF);
                switch (cmd)
                {
                    case APPCOMMAND_MEDIA_PLAY_PAUSE:
                        BtnPlayPause_Click(null, EventArgs.Empty);
                        m.Result = (IntPtr)1;
                        return;

                    case APPCOMMAND_MEDIA_PLAY:
                        if (!isPlaying) BtnPlayPause_Click(null, EventArgs.Empty);
                        m.Result = (IntPtr)1;
                        return;

                    case APPCOMMAND_MEDIA_PAUSE:
                        if (isPlaying) BtnPlayPause_Click(null, EventArgs.Empty);
                        m.Result = (IntPtr)1;
                        return;

                    case APPCOMMAND_MEDIA_NEXTTRACK:
                        SeekStepForward();
                        m.Result = (IntPtr)1;
                        return;

                    case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                        SeekStepBackward();
                        m.Result = (IntPtr)1;
                        return;
                }
            }
            if (m.Msg == WM_HOTKEY && currentBook != null && appSettings.MediaKeys)
            {
                switch (m.WParam.ToInt32())
                {
                    case HotkeyPlayPause: BtnPlayPause_Click(null, EventArgs.Empty); return;
                    case HotkeyNext: SeekStepForward(); return;
                    case HotkeyPrev: SeekStepBackward(); return;
                    case HotkeyStop:
                        if (isPlaying) BtnPlayPause_Click(null, EventArgs.Empty);
                        return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>Claims (or releases) the media keys system-wide, to match
        /// Settings. Safe to call at any time and as often as you like — it always
        /// releases what it registered before. A key another app has already
        /// claimed simply fails to register; NBR then still gets it while focused,
        /// through WM_APPCOMMAND.</summary>
        private void ApplyMediaKeySettings()
        {
            if (!IsHandleCreated) return;
            foreach (int id in new[] { HotkeyPlayPause, HotkeyNext, HotkeyPrev, HotkeyStop })
                try { UnregisterHotKey(this.Handle, id); } catch { }

            if (!appSettings.MediaKeys || !appSettings.MediaKeysGlobal) return;
            try
            {
                RegisterHotKey(this.Handle, HotkeyPlayPause, 0, VK_MEDIA_PLAY_PAUSE);
                RegisterHotKey(this.Handle, HotkeyNext, 0, VK_MEDIA_NEXT_TRACK);
                RegisterHotKey(this.Handle, HotkeyPrev, 0, VK_MEDIA_PREV_TRACK);
                RegisterHotKey(this.Handle, HotkeyStop, 0, VK_MEDIA_STOP);
            }
            catch { }
        }

        // ──────────────────────────────────────────────
        // Screen reader announcement
        // ──────────────────────────────────────────────
        //
        // Transient values (volume, speed, timer, info-on-demand, bookmark
        // set, seek step...) are announced through a UIA *notification event*
        // raised on the player window itself. This speaks the text WITHOUT
        // moving focus, which fixes the two problems the old off-screen-label
        // approach had:
        //   * it briefly stole focus to a hidden label and restored it 150 ms
        //     later — rapid key repeats overlapped those focus shuffles and
        //     choked the reader, and
        //   * NVDA (unlike JAWS) largely ignored the programmatic focus to an
        //     off-screen label, so pressing volume/speed keys while focus was
        //     on a button produced no feedback at all.
        // NotificationProcessing.MostRecent tells the reader to drop pending
        // older notifications and speak only the latest, so holding/​repeating
        // a key no longer backs up a queue of stale values.
        //
        // The announceLabel parameter is kept so the many call sites don't
        // change; it is no longer used. The off-screen label controls are now
        // vestigial and can be removed in a later cleanup.
        private void AnnounceToScreenReader(Label announceLabel, string text)
        {
            // Two channels, each picked up by exactly one reader: JAWS hears
            // the UIA notification (and ignores the NVDA client); NVDA hears
            // the NVDA client (and ignores our UIA notification). Both degrade
            // to no-ops when their reader isn't present, so calling both is
            // safe and never double-speaks.
            RaiseUiaNotification(text);
            NvdaController.Speak(text);
        }

        // Focus echo guard for the volume/speed fields: they are not updated
        // while focused (that would make JAWS re-read them on every step), so
        // they are resynced to the current value when focus lands on them, so
        // the value announced on focus is correct. Only touches the control
        // when it has actually drifted, to avoid an extra name-change utterance
        // on a normal focus-in.
        private void SyncVolumeField()
        {
            string acc = Localization.T("Player.Volume.Accessible", currentVolume);
            if (tbVolume.AccessibleName != acc)
            {
                tbVolume.Text = Localization.T("Player.Volume.Text", currentVolume);
                tbVolume.AccessibleName = acc;
            }
        }

        private void SyncSpeedField()
        {
            string speedStr = (currentSpeed / 100.0).ToString("0.0");
            string acc = Localization.T("Player.Speed.Accessible", speedStr);
            if (tbSpeed.AccessibleName != acc)
            {
                tbSpeed.Text = Localization.T("Player.Speed.Text", speedStr);
                tbSpeed.AccessibleName = acc;
            }
        }

        private object uiaHostProvider;      // cached IRawElementProviderSimple for this HWND
        private bool uiaNotifyUnavailable;   // set if the API is missing (pre-Win10-1709)

        private void RaiseUiaNotification(string text)
        {
            if (uiaNotifyUnavailable || string.IsNullOrEmpty(text) || !IsHandleCreated)
                return;

            try
            {
                if (uiaHostProvider == null)
                {
                    IRawElementProviderSimple provider;
                    int hr = UiaHostProviderFromHwnd(this.Handle, out provider);
                    if (hr != 0 || provider == null)
                    {
                        uiaNotifyUnavailable = true;
                        return;
                    }
                    uiaHostProvider = provider;
                }

                UiaRaiseNotificationEvent(
                    (IRawElementProviderSimple)uiaHostProvider,
                    NotificationKind.Other,
                    NotificationProcessing.MostRecent,
                    text,
                    string.Empty);
            }
            catch
            {
                // UiaRaiseNotificationEvent needs Windows 10 1709+. On older
                // systems the export is missing and the first call throws —
                // stop trying so we don't throw on every keystroke.
                uiaNotifyUnavailable = true;
            }
        }

        [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRawElementProviderSimple
        {
            // Never called from managed code — we only obtain the provider
            // from the OS and hand it back to UiaRaiseNotificationEvent, so no
            // members need to be declared for the pointer to marshal.
        }

        private enum NotificationKind
        {
            ItemAdded = 0,
            ItemRemoved = 1,
            ActionCompleted = 2,
            ActionAborted = 3,
            Other = 4
        }

        private enum NotificationProcessing
        {
            ImportantAll = 0,
            ImportantMostRecent = 1,
            All = 2,
            MostRecent = 3,
            CurrentThenMostRecent = 4
        }

        [DllImport("UIAutomationCore.dll")]
        private static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IRawElementProviderSimple provider);

        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
        private static extern int UiaRaiseNotificationEvent(
            IRawElementProviderSimple provider,
            NotificationKind notificationKind,
            NotificationProcessing notificationProcessing,
            [MarshalAs(UnmanagedType.BStr)] string displayString,
            [MarshalAs(UnmanagedType.BStr)] string activityId);

        // ──────────────────────────────────────────────
        // Seek step (from the seek dropdown)
        // ──────────────────────────────────────────────
        // Navigation is layered in four levels:
        //   1. Left/Right arrows  — plain 5 s seek, like any other player.
        //   2. Ctrl+1..9          — percentage jumps across the whole book.
        //   3. Shift+Left / Shift+Right, media Next/Prev, and the on-screen
        //      Back/Forward buttons — jump by the step selected in the seek
        //      dropdown (time steps / whole Part / Bookmark). Shift+Up/Down
        //      change which step is selected.
        //   4. Go To... (Ctrl+G)  — pick a named target from a list; for
        //      plain audio that's the book's parts.
        /// <summary>The step currently selected in the seek dropdown.</summary>
        private SeekStep CurrentSeekStep()
        {
            int i = cmbSeek.SelectedIndex;
            return (i >= 0 && i < seekSteps.Count) ? seekSteps[i] : new SeekStep(SeekStepKind.Sec15);
        }

        // Persist a step to Book.ini as a single int: heading depth L → 100+L,
        // any other kind → its enum ordinal. Kept compact so [Settings] SeekStep
        // stays a plain number across sessions.
        private int EncodeSeekStep(SeekStep step)
        {
            return step.Kind == SeekStepKind.Heading ? 100 + step.Level : (int)step.Kind;
        }

        private SeekStep DecodeSeekStep(int value)
        {
            if (value >= 100) return new SeekStep(SeekStepKind.Heading, value - 100);
            return new SeekStep((SeekStepKind)value);
        }

        /// <summary>Seconds for the currently selected time step.</summary>
        private int GetSeekStepSeconds()
        {
            switch (CurrentSeekStep().Kind)
            {
                case SeekStepKind.Sec15: return 15;
                case SeekStepKind.Sec30: return 30;
                case SeekStepKind.Min1: return 60;
                case SeekStepKind.Min5: return 300;
                case SeekStepKind.Min10: return 600;
                case SeekStepKind.Min15: return 900;
                case SeekStepKind.Min30: return 1800;
                default: return 15;
            }
        }

        private void SeekStepForward()
        {
            SeekStep step = CurrentSeekStep();
            if (currentBook != null && currentBook.IsTextBook) { TextSeek(step, +1); return; }
            switch (step.Kind)
            {
                case SeekStepKind.Part: PartForward(); break;
                case SeekStepKind.Heading: StructForward(HeadingPositions(step.Level)); break;
                case SeekStepKind.Page: StructForward(PagePositions()); break;
                case SeekStepKind.Chapter: StructForward(M4bChapterPositions()); break;
                case SeekStepKind.Bookmark: BookmarkForward(); break;
                default: SeekRelative(+GetSeekStepSeconds()); break;
            }
        }

        private void SeekStepBackward()
        {
            SeekStep step = CurrentSeekStep();
            if (currentBook != null && currentBook.IsTextBook) { TextSeek(step, -1); return; }
            switch (step.Kind)
            {
                case SeekStepKind.Part: PartBack(); break;
                case SeekStepKind.Heading: StructBack(HeadingPositions(step.Level)); break;
                case SeekStepKind.Page: StructBack(PagePositions()); break;
                case SeekStepKind.Chapter: StructBack(M4bChapterPositions()); break;
                case SeekStepKind.Bookmark: BookmarkBack(); break;
                default: SeekRelative(-GetSeekStepSeconds()); break;
            }
        }

        /// <summary>Seek in a text book by the selected step (dir +1/-1).</summary>
        private void TextSeek(SeekStep step, int dir)
        {
            if (tts == null) return;
            switch (step.Kind)
            {
                case SeekStepKind.Heading:
                    TextHeadingSeek(step.Level, dir); break;
                case SeekStepKind.Page:
                    TextPageSeek(dir); break;
                case SeekStepKind.Sentence:
                    if (dir > 0) tts.NextSentence(); else tts.PrevSentence(); break;
                case SeekStepKind.Paragraph:
                    if (dir > 0) tts.NextParagraph(); else tts.PrevParagraph(); break;
                case SeekStepKind.StandardPage:
                    tts.SeekChars(dir * TtsReader.StandardPageChars); break;
                case SeekStepKind.Bookmark:
                    // The same jump an audio book makes; BookmarkForward/Back work
                    // in the book's own unit, so they need no text branch of their
                    // own. Without this case the step fell through to the time
                    // seek below and wandered off by 15 seconds instead.
                    if (dir > 0) BookmarkForward(); else BookmarkBack(); break;
                default: // time steps (15/30/60 s / 5 / 10 min)
                    tts.SeekSeconds(dir * GetSeekStepSeconds()); break;
            }
        }

        /// <summary>Seek to the next/previous print-page marker in a structured
        /// text book (mirrors TextHeadingSeek's 50-char back grace).</summary>
        private void TextPageSeek(int dir)
        {
            if (tts == null || currentBook == null || currentBook.TextPages.Count == 0)
            {
                tones.Play(300, 150);
                return;
            }
            var pages = currentBook.TextPages;
            int cur = tts.CharPosition;
            if (dir > 0)
            {
                for (int i = 0; i < pages.Count; i++)
                    if (pages[i].Offset > cur + 1) { tts.SeekToChar(pages[i].Offset); return; }
                tones.Play(300, 150);
            }
            else
            {
                int idx = -1;
                for (int i = pages.Count - 1; i >= 0; i--)
                    if (pages[i].Offset <= cur) { idx = i; break; }
                if (idx < 0) { tones.Play(300, 150); return; }
                int target = (idx == 0 || cur - pages[idx].Offset > 50) ? pages[idx].Offset : pages[idx - 1].Offset;
                tts.SeekToChar(target);
            }
        }

        // Distinct heading depths present in the current text book, ascending.
        private System.Collections.Generic.List<int> TextHeadingLevelsPresent()
        {
            var levels = new System.Collections.Generic.List<int>();
            if (currentBook != null)
                foreach (var h in currentBook.TextHeadings)
                    if (!levels.Contains(h.Level)) levels.Add(h.Level);
            levels.Sort();
            return levels;
        }

        // Character offsets of the text headings at depth ≤ maxLevel, ascending.
        private System.Collections.Generic.List<int> TextHeadingOffsets(int maxLevel)
        {
            var list = new System.Collections.Generic.List<int>();
            if (currentBook != null)
                foreach (var h in currentBook.TextHeadings)
                    if (h.Level <= maxLevel) list.Add(h.Offset);
            list.Sort();
            return list;
        }

        /// <summary>Heading navigation for a structured text book: jump to the
        /// next/previous heading of depth ≤ maxLevel, with a small grace so Back
        /// from just inside a heading rewinds to its start.</summary>
        private void TextHeadingSeek(int maxLevel, int dir)
        {
            var offs = TextHeadingOffsets(maxLevel);
            if (tts == null || offs.Count == 0) { tones.Play(300, 150); return; }
            int cur = tts.CharPosition;
            if (dir > 0)
            {
                foreach (int o in offs)
                    if (o > cur + 1) { tts.SeekToChar(o); return; }
                tones.Play(300, 150);
            }
            else
            {
                int idx = -1;
                for (int i = offs.Count - 1; i >= 0; i--)
                    if (offs[i] <= cur) { idx = i; break; }
                if (idx < 0) { tones.Play(300, 150); return; }
                // >~50 chars into the heading rewinds to its start, else previous.
                tts.SeekToChar((idx == 0 || cur - offs[idx] > 50) ? offs[idx] : offs[idx - 1]);
            }
        }

        /// <summary>Go To for a structured text book — pick a heading from the
        /// list (indented by depth) and jump the reader there.</summary>
        private void TextGoTo()
        {
            var hs = currentBook.TextHeadings;
            string[] names = new string[hs.Count];
            int[] targets = new int[hs.Count];
            for (int i = 0; i < hs.Count; i++)
            {
                names[i] = new string(' ', 2 * Math.Max(0, hs[i].Level - 1)) + hs[i].Label;
                targets[i] = hs[i].Offset;
            }

            int cur = tts != null ? tts.CharPosition : 0;
            int preselect = 0;
            for (int i = hs.Count - 1; i >= 0; i--)
                if (hs[i].Offset <= cur) { preselect = i; break; }

            using (GoToForm dlg = new GoToForm(names, preselect, appSettings.GoToAutoPlay, true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK &&
                    dlg.SelectedPartIndex >= 0 && dlg.SelectedPartIndex < targets.Length)
                {
                    appSettings.SetGoToAutoPlay(dlg.AutoPlayChecked);
                    tts.SeekToChar(targets[dlg.SelectedPartIndex]);
                    if (dlg.AutoPlayChecked && !isPlaying)
                    {
                        tts.Play();
                        SetPlayPauseState(true);
                    }
                }
            }
        }

        /// <summary>Shift+Up / Shift+Down: cycles the seek dropdown's selected
        /// step and announces the new value, from anywhere in the window. The
        /// dropdown can still be changed directly when it has focus.</summary>
        private void ChangeSeekStep(int delta)
        {
            int count = cmbSeek.Items.Count;
            if (count <= 0) return;
            // Circular: past the last item wraps to the first (and vice versa)
            // when navigating in the same direction.
            int newIndex = ((cmbSeek.SelectedIndex + delta) % count + count) % count;
            cmbSeek.SelectedIndex = newIndex;
            AnnounceToScreenReader(lblAnnounceInfo, Localization.T("Player.Seek.Announce", cmbSeek.Text));
        }

        /// <summary>Rebuilds the seek dropdown to show exactly the steps the
        /// current book supports: the four time steps and Part are always
        /// present; Heading and Page appear only for a DAISY book that has
        /// them; Bookmark appears only while the book has ≥1 bookmark. The
        /// previously selected kind is preserved across the rebuild when it
        /// still exists, otherwise selection falls back to the first step.
        /// Called whenever the book, its bookmarks, or its structure change.</summary>
        private void RebuildSeekSteps()
        {
            SeekStep previous = CurrentSeekStep();

            cmbSeek.BeginUpdate();
            cmbSeek.Items.Clear();
            seekSteps.Clear();

            PlayerType type = GetPlayerType();

            // Structural unit(s) first (coarsest), then time steps (finest last),
            // then Bookmark (dynamic — only when the book has bookmarks). Per type:
            //   Single audio:  30m,15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Multi audio:   Part, 30m,15m,10m,5m,60s,30s,15s,[Bookmark]
            //   DAISY:         H1,H2,…,Page, 15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Flat text:     Standard page, 15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Structured:    H1,H2,…,Page, 15m,10m,5m,60s,30s,15s
            switch (type)
            {
                case PlayerType.MultiAudio:
                    AddSeekStep(new SeekStep(SeekStepKind.Part), Localization.T("Seek.Item.Part"));
                    goto case PlayerType.SingleAudio;

                case PlayerType.SingleAudio:
                    AddSeekStep(new SeekStep(SeekStepKind.Min30), Localization.T("Seek.Item.30min"));
                    AddTimeSteps15DownWithBookmark();
                    break;

                case PlayerType.Daisy:
                    foreach (int level in HeadingLevelsPresent())
                        AddSeekStep(new SeekStep(SeekStepKind.Heading, level),
                            Localization.T("Seek.Item.HeadingLevel", level));
                    if (currentBook.DaisyPages.Count > 0)
                        AddSeekStep(new SeekStep(SeekStepKind.Page), Localization.T("Seek.Item.Page"));
                    AddTimeSteps15DownWithBookmark();
                    break;

                case PlayerType.M4b:
                    // Chapter, then 15 min → 15 s, then Bookmark (dynamic).
                    AddSeekStep(new SeekStep(SeekStepKind.Chapter), Localization.T("Seek.Item.Chapter"));
                    AddTimeSteps15DownWithBookmark();
                    break;

                case PlayerType.StructuredText:
                    foreach (int level in TextHeadingLevelsPresent())
                        AddSeekStep(new SeekStep(SeekStepKind.Heading, level),
                            Localization.T("Seek.Item.HeadingLevel", level));
                    if (currentBook.TextPages.Count > 0)
                        AddSeekStep(new SeekStep(SeekStepKind.Page), Localization.T("Seek.Item.Page"));
                    AddTimeSteps15DownWithBookmark();
                    break;

                case PlayerType.FlatText:
                    // A flat book with real print pages (a paged PDF with no
                    // outline) navigates by its actual pages; a page-less book
                    // (plain .txt) falls back to the fixed 1800-char page unit.
                    if (currentBook.TextPages.Count > 0)
                        AddSeekStep(new SeekStep(SeekStepKind.Page), Localization.T("Seek.Item.Page"));
                    else
                        AddSeekStep(new SeekStep(SeekStepKind.StandardPage), Localization.T("Seek.Item.StandardPage"));
                    AddTimeSteps15DownWithBookmark();
                    break;
            }

            int idx = seekSteps.FindIndex(s => s.Kind == previous.Kind && s.Level == previous.Level);
            cmbSeek.SelectedIndex = idx >= 0 ? idx : 0;
            cmbSeek.EndUpdate();
        }

        /// <summary>Appends the shared time steps 15 min → 15 s, then a Bookmark
        /// step when the current book has any bookmarks.</summary>
        private void AddTimeSteps15DownWithBookmark()
        {
            AddSeekStep(new SeekStep(SeekStepKind.Min15), Localization.T("Seek.Item.15min"));
            AddSeekStep(new SeekStep(SeekStepKind.Min10), Localization.T("Seek.Item.10min"));
            AddSeekStep(new SeekStep(SeekStepKind.Min5), Localization.T("Seek.Item.5min"));
            AddSeekStep(new SeekStep(SeekStepKind.Min1), Localization.T("Seek.Item.1min"));
            AddSeekStep(new SeekStep(SeekStepKind.Sec30), Localization.T("Seek.Item.30s"));
            AddSeekStep(new SeekStep(SeekStepKind.Sec15), Localization.T("Seek.Item.15s"));
            if (currentBook != null && currentBook.Bookmarks.Count > 0)
                AddSeekStep(new SeekStep(SeekStepKind.Bookmark), Localization.T("Seek.Item.Bookmark"));
        }

        private void AddSeekStep(SeekStep step, string label)
        {
            seekSteps.Add(step);
            cmbSeek.Items.Add(label);
        }

        /// <summary>Distinct DAISY heading depths present, ascending — one seek
        /// step is offered per depth.</summary>
        private System.Collections.Generic.List<int> HeadingLevelsPresent()
        {
            var levels = new System.Collections.Generic.List<int>();
            if (currentBook != null)
                foreach (var h in currentBook.DaisyHeadings)
                    if (!levels.Contains(h.Level)) levels.Add(h.Level);
            levels.Sort();
            return levels;
        }

        // Absolute virtual-timeline positions of the DAISY headings (down to a
        // given depth) / pages, in reading order, for the Heading / Page steps.
        private System.Collections.Generic.List<double> HeadingPositions(int maxLevel)
        {
            var list = new System.Collections.Generic.List<double>();
            if (currentBook != null)
                foreach (var h in currentBook.DaisyHeadings)
                    if (h.Level <= maxLevel) list.Add(h.Position);
            return list;
        }

        private System.Collections.Generic.List<double> PagePositions()
        {
            var list = new System.Collections.Generic.List<double>();
            if (currentBook != null)
                foreach (var p in currentBook.DaisyPages) list.Add(p.Position);
            return list;
        }

        /// <summary>Generic "next structural mark" jump (headings or pages),
        /// mirroring BookmarkForward: seeks to the first mark past the current
        /// position, low beep if already past the last. Positions are assumed
        /// ascending (reading order).</summary>
        private void StructForward(System.Collections.Generic.List<double> positions)
        {
            if (positions == null || positions.Count == 0) { tones.Play(300, 150); return; }
            double pos = GetVirtualPosition();
            foreach (double p in positions)
                if (p > pos + 0.05) { SeekToVirtualPosition(p); return; }
            tones.Play(300, 150);
        }

        /// <summary>Generic "previous structural mark" jump, mirroring
        /// BookmarkBack's 3-second grace: more than 3 s past the current mark
        /// rewinds to it, otherwise jumps to the one before.</summary>
        private void StructBack(System.Collections.Generic.List<double> positions)
        {
            if (positions == null || positions.Count == 0) { tones.Play(300, 150); return; }
            double pos = GetVirtualPosition();
            int cur = -1;
            for (int i = positions.Count - 1; i >= 0; i--)
                if (positions[i] <= pos + 0.05) { cur = i; break; }
            if (cur < 0) { tones.Play(300, 150); return; }

            if (cur == 0 || pos - positions[cur] > 3.0)
                SeekToVirtualPosition(positions[cur]);
            else
                SeekToVirtualPosition(positions[cur - 1]);
        }

        // ──────────────────────────────────────────────
        // ProcessCmdKey
        // ──────────────────────────────────────────────
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool infoBoxHasFocus = this.ActiveControl == tbInfo;

            switch (keyData)
            {
                case Keys.Up:
                    if (!infoBoxHasFocus) { ChangeVolume(+5); return true; }
                    break;

                case Keys.Down:
                    if (!infoBoxHasFocus) { ChangeVolume(-5); return true; }
                    break;

                case Keys.Right:
                    if (!infoBoxHasFocus) { ArrowSeek(+1); return true; }
                    break;

                case Keys.Left:
                    if (!infoBoxHasFocus) { ArrowSeek(-1); return true; }
                    break;

                case Keys.I:
                    // Read out fresh playback info from anywhere in the
                    // player, via the off-screen announcement label. The
                    // info box itself is not touched — no text change, no
                    // echo.
                    AnnounceToScreenReader(lblAnnounceInfo, BuildCurrentInfoText());
                    return true;

                // Speed — Ctrl+Left/Right (replaced Page Up/Down). Ctrl is
                // fine on Left/Right (unlike Up/Down, which the shell/JAWS grab
                // for vertical navigation).
                case Keys.Control | Keys.Left:
                    ChangeSpeed(-10);
                    return true;

                case Keys.Control | Keys.Right:
                    ChangeSpeed(+10);
                    return true;

                // Seek jump by the selected step — Shift+Left/Right.
                case Keys.Shift | Keys.Left:
                    SeekStepBackward();
                    return true;

                case Keys.Shift | Keys.Right:
                    SeekStepForward();
                    return true;

                // Change the seek step (the dropdown value) — Shift+Up/Down.
                // Down moves down the list (H1 → … → 15 sec), Up back toward H1,
                // matching the visual order and arrow direction.
                case Keys.Shift | Keys.Up:
                    ChangeSeekStep(-1);
                    return true;

                case Keys.Shift | Keys.Down:
                    ChangeSeekStep(+1);
                    return true;

                case Keys.Control | Keys.O:
                    OpenFile();
                    return true;

                case Keys.Control | Keys.G:
                    BtnGoTo_Click(null, EventArgs.Empty);
                    return true;

                case Keys.Control | Keys.T:
                    BtnTimer_Click(null, EventArgs.Empty);
                    return true;

                case Keys.Control | Keys.B:
                    BtnSetBookmark_Click(null, EventArgs.Empty);
                    return true;

                case Keys.Alt | Keys.Enter:
                    BtnProperties_Click(null, EventArgs.Empty);
                    return true;

                case Keys.Enter:
                    if (this.ActiveControl is Button btn)
                        btn.PerformClick();
                    return true;

                case Keys.Control | Keys.D1:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.1);
                    return true;
                case Keys.Control | Keys.D2:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.2);
                    return true;
                case Keys.Control | Keys.D3:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.3);
                    return true;
                case Keys.Control | Keys.D4:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.4);
                    return true;
                case Keys.Control | Keys.D5:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.5);
                    return true;
                case Keys.Control | Keys.D6:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.6);
                    return true;
                case Keys.Control | Keys.D7:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.7);
                    return true;
                case Keys.Control | Keys.D8:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.8);
                    return true;
                case Keys.Control | Keys.D9:
                    if (currentBook != null) SeekToVirtualPosition(currentBook.TotalDuration * 0.9);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ──────────────────────────────────────────────
        // KeyDown — Space = Play/Pause
        // ──────────────────────────────────────────────
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                BtnPlayPause_Click(null, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ──────────────────────────────────────────────
        // Volume
        // ──────────────────────────────────────────────
        private void ChangeVolume(int delta)
        {
            int newVol = Math.Max(0, Math.Min(100, currentVolume + delta));
            currentVolume = newVol;

            if (currentBook != null && currentBook.IsTextBook)
            {
                if (tts != null) tts.SetVolume(currentVolume);
                // For a text book the Volume field IS the speech volume, so the
                // book's own TextVolume follows it — the two are one number, and
                // Properties must never show a different one from the player. It
                // is filed under the voice in use, so it comes back with it.
                RememberCurrentVoicePrefs();
            }
            else if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "volume", currentVolume.ToString());

            string text = Localization.T("Player.Volume.Text", currentVolume);
            lblVolume.Text = text;
            // Volume uses the Up/Down arrows, which a reader treats as edit
            // caret navigation and so speaks the focused field's current line
            // on every press — regardless of the field's accessible role
            // (JAWS keys off the underlying Edit window class). We can't stop
            // that read, so we make it the SINGLE feedback: keep the field's
            // Text current (so the line spoken is the right value) and, while
            // it has focus, do NOT also fire our own announcement (that would
            // be a second utterance) and do NOT touch AccessibleName (its
            // change re-triggers the name announcement).
            tbVolume.Text = text;
            if (!tbVolume.Focused)
            {
                tbVolume.AccessibleName = Localization.T("Player.Volume.Accessible", currentVolume);
                AnnounceToScreenReader(lblAnnounceVolume, text);
            }

            if (currentVolume == 0)
                tones.Play(300, 150);
            else if (currentVolume == 100)
                tones.Play(1200, 150);
        }

        // ──────────────────────────────────────────────
        // Speed
        // ──────────────────────────────────────────────
        private void ChangeSpeed(int delta)
        {
            // Text book: the speed control is words-per-minute, not an mpv
            // multiplier. Step ±5 WPM, beep when passing the Settings default.
            if (currentBook != null && currentBook.IsTextBook)
            {
                int step = delta > 0 ? 5 : -5;
                int newWpm = Math.Max(80, Math.Min(400, currentWpm + step));
                // The "default" the beep marks is this voice's own default speed,
                // not the one belonging to whichever voice Settings happens to name.
                int def = appSettings.PrefsFor(EffectiveTextVoice()).Wpm;
                bool crossedDefault = (currentWpm - def) * (newWpm - def) <= 0 && currentWpm != newWpm;
                currentWpm = newWpm;
                if (tts != null) tts.SetRate(TtsReader.WpmToRate(currentWpm));
                RememberCurrentVoicePrefs();
                UpdateSpeedDisplay();
                AnnounceToScreenReader(lblAnnounceSpeed, Localization.T("Player.Speed.WpmAccessible", currentWpm));
                if (crossedDefault) tones.Play(new[] { (880, 70), (880, 70) });
                return;
            }

            int newSpeed = Math.Max(50, Math.Min(300, currentSpeed + delta));
            currentSpeed = newSpeed;

            if (mpvHandle != IntPtr.Zero)
            {
                double speed = currentSpeed / 100.0;
                mpv_set_property_string(mpvHandle, "speed",
                    speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            string speedStr = (currentSpeed / 100.0).ToString("0.0");
            string text = Localization.T("Player.Speed.Text", speedStr);
            lblSpeed.Text = text;
            // Focus echo guard — see ChangeVolume.
            if (!tbSpeed.Focused)
            {
                tbSpeed.Text = text;
                tbSpeed.AccessibleName = Localization.T("Player.Speed.Accessible", speedStr);
            }

            AnnounceToScreenReader(lblAnnounceSpeed, text);

            if (currentSpeed == 100)
                tones.Play(new[] { (800, 120), (800, 120) });
        }

        // ──────────────────────────────────────────────
        // Seek
        // ──────────────────────────────────────────────
        // Seek arrows (Left/Right): 5 s in audio, one sentence in a text book.
        private void ArrowSeek(int dir)
        {
            if (currentBook != null && currentBook.IsTextBook)
            {
                if (tts == null) return;
                if (dir > 0) tts.NextSentence(); else tts.PrevSentence();
                return;
            }
            SeekRelative(dir * 5);
        }

        private void SeekRelative(int seconds)
        {
            if (mpvHandle == IntPtr.Zero) return;

            if (currentBook != null && currentBook.Chapters.Count > 0)
            {
                double virtualPos = GetVirtualPosition();
                SeekToVirtualPosition(virtualPos + seconds);
            }
            else
            {
                MpvCommand("seek", seconds.ToString(), "relative");
            }
        }

        // ──────────────────────────────────────────────
        // Virtual timeline
        // ──────────────────────────────────────────────
        private double GetVirtualPosition()
        {
            if (currentBook == null || currentBook.Chapters.Count == 0)
            {
                double pos = 0;
                mpv_get_property(mpvHandle, "time-pos", 5, ref pos);
                return pos;
            }
            if (currentPlaylistIndex >= currentBook.Offsets.Count) return 0;

            double position = 0;
            mpv_get_property(mpvHandle, "time-pos", 5, ref position);
            return currentBook.Offsets[currentPlaylistIndex] + position;
        }

        private void SeekToVirtualPosition(double virtualSeconds, Action onComplete = null)
        {
            if (currentBook == null || currentBook.Chapters.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            virtualSeconds = Math.Max(0, Math.Min(virtualSeconds, currentBook.TotalDuration));

            // Find which file
            int targetIndex = 0;
            for (int i = currentBook.Offsets.Count - 1; i >= 0; i--)
            {
                if (virtualSeconds >= currentBook.Offsets[i])
                {
                    targetIndex = i;
                    break;
                }
            }

            double seekPos = virtualSeconds - currentBook.Offsets[targetIndex];

            if (targetIndex == currentPlaylistIndex)
            {
                // Same file, just seek
                MpvCommand("seek", seekPos.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
                onComplete?.Invoke();
            }
            else
            {
                // Different file — playlist-play-index, then seek once it loads.
                // Pause *through* the switch even when playing: a freshly loaded
                // file starts at position 0 and would be audible for the ~300 ms
                // until the seek lands (the "blip" — you hear the file's opening
                // before it jumps to the heading). Resume only after the seek.
                bool wasPlaying = isPlaying;
                MpvCommand("playlist-play-index", targetIndex.ToString());
                mpv_set_property_string(mpvHandle, "pause", "yes");

                System.Windows.Forms.Timer seekTimer = new System.Windows.Forms.Timer();
                seekTimer.Interval = 300;
                seekTimer.Tick += (s, ev) =>
                {
                    seekTimer.Stop();
                    seekTimer.Dispose();
                    MpvCommand("seek", seekPos.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
                    if (wasPlaying)
                        mpv_set_property_string(mpvHandle, "pause", "no");
                    onComplete?.Invoke();
                };
                seekTimer.Start();
            }
        }

        // ──────────────────────────────────────────────
        // Building the UI
        // ──────────────────────────────────────────────
        private void BuildUI()
        {
            this.Text = Localization.T("App.Name");
            this.ClientSize = new Size(640, 400);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(100, 100);

            BuildTopPanel();
            BuildBottomPanel();

            eventTimer = new System.Windows.Forms.Timer();
            eventTimer.Interval = 50;
            eventTimer.Tick += EventTimer_Tick;

            progressTimer = new System.Windows.Forms.Timer();
            progressTimer.Interval = 500;
            progressTimer.Tick += ProgressTimer_Tick;

            // Sleep timer — 1 s tick while a countdown is running.
            sleepTimer = new System.Windows.Forms.Timer();
            sleepTimer.Interval = 1000;
            sleepTimer.Tick += SleepTimer_Tick;
        }

        private void BuildTopPanel()
        {
            panelTop = new Panel();
            panelTop.Location = new Point(0, 0);
            panelTop.Size = new Size(640, 180);
            panelTop.BorderStyle = BorderStyle.FixedSingle;
            panelTop.TabStop = false;

            // The info box is a plain multiline read-only TextBox (EDIT
            // control) — deliberately NOT a RichTextBox. JAWS handles rich
            // edit controls specially and kept re-reading the box's content
            // when tabbing to neighboring controls; the borderless,
            // form-colored look also made it register as static text tied
            // to those controls in the screen model. A standard TextBox
            // with the same look as the other read-only fields (tbVolume,
            // tbProgress...) follows their proven, quiet behavior.
            tbInfo = new TextBox();
            tbInfo.Multiline = true;
            tbInfo.Location = new Point(5, 5);
            tbInfo.Size = new Size(620, 168);
            tbInfo.ReadOnly = true;
            tbInfo.BackColor = SystemColors.Window;
            tbInfo.Font = new Font("Segoe UI", 10);
            tbInfo.TabStop = true;
            tbInfo.TabIndex = 0;
            tbInfo.AccessibleName = Localization.T("Player.Info.AccessibleName");
            tbInfo.Text = BuildInfoBoxPlaceholder();

            // The info box is a SNAPSHOT, not a live ticker. JAWS treats
            // rich edit controls specially and re-reads their content when
            // the text keeps changing (which made it repeat the whole box
            // when tabbing to neighboring controls). So the periodic
            // refresh is gone entirely — the text is rebuilt only at the
            // moment the box receives focus (below), on part change, and
            // on demand via the I key. Live position stays in the Position
            // field.
            tbInfo.Enter += (s, e) =>
            {
                tbInfo.Text = BuildCurrentInfoText();
            };

            panelTop.Controls.Add(tbInfo);
            this.Controls.Add(panelTop);
        }

        private string BuildInfoBoxPlaceholder()
        {
            string dash = Localization.T("Common.Dash");
            string nl = "\r\n";
            string zero = FormatTime(0);
            return
                Localization.T("Player.Info.TitleLabel") + " " + dash + nl +
                Localization.T("Player.Info.AuthorLabel") + " " + nl +
                Localization.T("Player.Info.BookmarksLabel") + " 0" + nl + nl +
                Localization.T("Player.Info.ElapsedLabel") + " " + zero + nl +
                Localization.T("Player.Info.RemainingLabel") + " -" + zero;
        }

        /// <summary>
        /// Bottom panel — 3×4 grid.
        /// Columns: A = x 0–160, B = x 160–480 (double width), C = x 480–640.
        /// Rows (each ~55 px within the 220 px panel):
        ///   1: Library      | Seek dropdown            | Properties
        ///   2: Settings     | Back / Play / Forward    | Go To...
        ///   3: Sleep Timer  | Volume / Speed           | Set Bookmark
        ///   4: Help         | Progress (position)      | Manage Bookmarks
        /// Tab order is column-major: A (app), B (playback), C (book tools).
        /// </summary>
        private void BuildBottomPanel()
        {
            panelBottom = new Panel();
            panelBottom.Location = new Point(0, 180);
            panelBottom.Size = new Size(640, 220);
            panelBottom.BorderStyle = BorderStyle.FixedSingle;
            panelBottom.TabStop = false;

            // ── Column A (x=10, width 140) ──
            btnLibrary = new Button();
            btnLibrary.Text = Localization.T("Btn.Library");
            btnLibrary.Size = new Size(140, 40);
            btnLibrary.Location = new Point(10, 8);
            btnLibrary.AccessibleName = Localization.T("Btn.Library");
            btnLibrary.TabIndex = 0;
            btnLibrary.Click += BtnLibrary_Click;

            btnSettings = new Button();
            btnSettings.Text = Localization.T("Btn.Settings");
            btnSettings.Size = new Size(140, 40);
            btnSettings.Location = new Point(10, 61);
            btnSettings.AccessibleName = Localization.T("Btn.Settings");
            btnSettings.TabIndex = 1;
            btnSettings.Click += BtnSettings_Click;

            btnTimer = new Button();
            btnTimer.Text = Localization.T("Btn.Timer");
            btnTimer.Size = new Size(140, 40);
            btnTimer.Location = new Point(10, 114);
            btnTimer.AccessibleName = Localization.T("Btn.Timer.Accessible");
            btnTimer.TabIndex = 2;
            btnTimer.Click += BtnTimer_Click;
            // While the countdown runs, the button text is NOT updated on
            // every tick if the button has focus (same JAWS echo guard as
            // the info box). One fresh refresh happens at the moment focus
            // arrives, so the announced value is current.
            btnTimer.Enter += (s, e) =>
            {
                if (sleepTimerActive)
                    UpdateSleepTimerButton(true);
            };

            btnHelp = new Button();
            btnHelp.Text = Localization.T("Btn.Help");
            btnHelp.Size = new Size(140, 40);
            btnHelp.Location = new Point(10, 167);
            btnHelp.AccessibleName = Localization.T("Btn.Help");
            btnHelp.TabIndex = 3;
            btnHelp.Click += BtnHelp_Click;

            // ── Column B (x=170, width 300) ──
            // Row 1: seek dropdown
            lblSeek = new Label();
            lblSeek.Text = Localization.T("Player.Seek.Label");
            lblSeek.Location = new Point(170, 5);
            lblSeek.Size = new Size(300, 16);
            lblSeek.TabStop = false;

            cmbSeek = new ComboBox();
            cmbSeek.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSeek.Location = new Point(170, 23);
            cmbSeek.Size = new Size(300, 24);
            cmbSeek.TabIndex = 4;
            cmbSeek.AccessibleName = Localization.T("Player.Seek.Accessible");
            // Populate the steps for the current (no) book — time steps + Part.
            RebuildSeekSteps();
            // Keyboard-inert display: the step is changed only with
            // Shift+Up/Down (from anywhere) or the mouse. Swallowing KeyDown
            // (and the resulting KeyPress) stops the combo from reacting to
            // arrows, type-ahead letters, Home/End, or opening its dropdown
            // from the keyboard, so focusing it never steals those keys.
            cmbSeek.KeyDown += (s, e) => { e.Handled = true; e.SuppressKeyPress = true; };

            // Row 2: transport buttons
            btnBack = new Button();
            btnBack.Text = "⏮";
            btnBack.Size = new Size(95, 45);
            btnBack.Location = new Point(170, 58);
            btnBack.AccessibleName = Localization.T("Btn.Back.Accessible");
            btnBack.Font = new Font("Segoe UI", 14);
            btnBack.TabIndex = 5;
            btnBack.Click += BtnBack_Click;

            btnPlayPause = new Button();
            btnPlayPause.Text = "▶";
            btnPlayPause.Size = new Size(95, 45);
            btnPlayPause.Location = new Point(270, 58);
            btnPlayPause.AccessibleName = Localization.T("Btn.Play.Accessible");
            btnPlayPause.Font = new Font("Segoe UI", 14);
            btnPlayPause.TabIndex = 6;
            btnPlayPause.Click += BtnPlayPause_Click;

            btnForward = new Button();
            btnForward.Text = "⏭";
            btnForward.Size = new Size(95, 45);
            btnForward.Location = new Point(370, 58);
            btnForward.AccessibleName = Localization.T("Btn.Forward.Accessible");
            btnForward.Font = new Font("Segoe UI", 14);
            btnForward.TabIndex = 7;
            btnForward.Click += BtnForward_Click;

            // Row 3: volume (left half) + speed (right half)
            lblVolume = new Label();
            lblVolume.Text = Localization.T("Player.Volume.Text", 100);
            lblVolume.Location = new Point(170, 111);
            lblVolume.Size = new Size(145, 16);
            lblVolume.TabStop = false;

            tbVolume = new TextBox();
            tbVolume.Location = new Point(170, 129);
            tbVolume.Size = new Size(145, 24);
            tbVolume.ReadOnly = true;
            tbVolume.TabStop = true;
            tbVolume.TabIndex = 8;
            tbVolume.Text = Localization.T("Player.Volume.Text", 100);
            tbVolume.AccessibleName = Localization.T("Player.Volume.Accessible", 100);
            tbVolume.BackColor = SystemColors.Window;
            // Present as static text, not an edit: volume is changed with the
            // Up/Down arrows, which a reader otherwise treats as edit caret
            // navigation and speaks the field's current line on every press
            // (on top of our announcement). As static text the arrows are
            // inert here and the announcement is the single feedback — the
            // same clean behaviour Speed already gets from Page Up/Down.
            tbVolume.AccessibleRole = AccessibleRole.StaticText;
            tbVolume.Enter += (s, e) => SyncVolumeField();

            lblSpeed = new Label();
            lblSpeed.Text = Localization.T("Player.Speed.Text", "1.0");
            lblSpeed.Location = new Point(325, 111);
            lblSpeed.Size = new Size(145, 16);
            lblSpeed.TabStop = false;

            tbSpeed = new TextBox();
            tbSpeed.Location = new Point(325, 129);
            tbSpeed.Size = new Size(145, 24);
            tbSpeed.ReadOnly = true;
            tbSpeed.TabStop = true;
            tbSpeed.TabIndex = 9;
            tbSpeed.Text = Localization.T("Player.Speed.Text", "1.0");
            tbSpeed.AccessibleName = Localization.T("Player.Speed.Accessible", "1.0");
            tbSpeed.BackColor = SystemColors.Window;
            // Static text for the same reason as tbVolume (harmless for Speed,
            // which uses Page Up/Down, but keeps the two fields consistent).
            tbSpeed.AccessibleRole = AccessibleRole.StaticText;
            tbSpeed.Enter += (s, e) => SyncSpeedField();

            // Row 4: progress / position
            lblProgress = new Label();
            lblProgress.Text = Localization.T("Player.Position.Label");
            lblProgress.Location = new Point(170, 164);
            lblProgress.Size = new Size(300, 16);
            lblProgress.TabStop = false;

            tbProgress = new TextBox();
            tbProgress.Location = new Point(170, 182);
            tbProgress.Size = new Size(300, 24);
            tbProgress.ReadOnly = true;
            tbProgress.TabStop = true;
            tbProgress.TabIndex = 10;
            tbProgress.Text = Localization.T("Player.Position.Text", FormatTime(0), FormatTime(0));
            tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", 0);
            tbProgress.BackColor = SystemColors.Window;

            // ── Column C (x=490, width 140) ──
            btnProperties = new Button();
            btnProperties.Text = Localization.T("Btn.Properties");
            btnProperties.Size = new Size(140, 40);
            btnProperties.Location = new Point(490, 8);
            btnProperties.AccessibleName = Localization.T("Btn.Properties.Accessible");
            btnProperties.TabIndex = 11;
            btnProperties.Click += BtnProperties_Click;

            btnGoTo = new Button();
            btnGoTo.Text = Localization.T("Btn.GoTo");
            btnGoTo.Size = new Size(140, 40);
            btnGoTo.Location = new Point(490, 61);
            btnGoTo.AccessibleName = Localization.T("Btn.GoTo.Accessible");
            btnGoTo.TabIndex = 12;
            btnGoTo.Click += BtnGoTo_Click;

            btnSetBookmark = new Button();
            btnSetBookmark.Text = Localization.T("Btn.SetBookmark");
            btnSetBookmark.Size = new Size(140, 40);
            btnSetBookmark.Location = new Point(490, 114);
            btnSetBookmark.AccessibleName = Localization.T("Btn.SetBookmark.Accessible");
            btnSetBookmark.TabIndex = 13;
            btnSetBookmark.Click += BtnSetBookmark_Click;

            btnManageBookmarks = new Button();
            btnManageBookmarks.Text = Localization.T("Btn.ManageBookmarks");
            btnManageBookmarks.Size = new Size(140, 40);
            btnManageBookmarks.Location = new Point(490, 167);
            btnManageBookmarks.AccessibleName = Localization.T("Btn.ManageBookmarks");
            btnManageBookmarks.TabIndex = 14;
            btnManageBookmarks.Click += BtnManageBookmarks_Click;

            // ── Off-screen announcement labels ──
            lblAnnounceVolume = new Label();
            lblAnnounceVolume.Text = "";
            lblAnnounceVolume.Location = new Point(-600, -600);
            lblAnnounceVolume.Size = new Size(200, 20);
            lblAnnounceVolume.TabStop = false;

            lblAnnounceSpeed = new Label();
            lblAnnounceSpeed.Text = "";
            lblAnnounceSpeed.Location = new Point(-600, -620);
            lblAnnounceSpeed.Size = new Size(200, 20);
            lblAnnounceSpeed.TabStop = false;

            lblAnnounceProgress = new Label();
            lblAnnounceProgress.Text = "";
            lblAnnounceProgress.Location = new Point(-600, -640);
            lblAnnounceProgress.Size = new Size(200, 20);
            lblAnnounceProgress.TabStop = false;

            lblAnnounceInfo = new Label();
            lblAnnounceInfo.Text = "";
            lblAnnounceInfo.Location = new Point(-600, -660);
            lblAnnounceInfo.Size = new Size(200, 20);
            lblAnnounceInfo.TabStop = false;

            // Column A
            panelBottom.Controls.Add(btnLibrary);
            panelBottom.Controls.Add(btnSettings);
            panelBottom.Controls.Add(btnTimer);
            panelBottom.Controls.Add(btnHelp);
            // Column B
            panelBottom.Controls.Add(lblSeek);
            panelBottom.Controls.Add(cmbSeek);
            panelBottom.Controls.Add(btnBack);
            panelBottom.Controls.Add(btnPlayPause);
            panelBottom.Controls.Add(btnForward);
            panelBottom.Controls.Add(lblVolume);
            panelBottom.Controls.Add(tbVolume);
            panelBottom.Controls.Add(lblSpeed);
            panelBottom.Controls.Add(tbSpeed);
            panelBottom.Controls.Add(lblProgress);
            panelBottom.Controls.Add(tbProgress);
            // Column C
            panelBottom.Controls.Add(btnProperties);
            panelBottom.Controls.Add(btnGoTo);
            panelBottom.Controls.Add(btnSetBookmark);
            panelBottom.Controls.Add(btnManageBookmarks);
            // Announcement labels
            panelBottom.Controls.Add(lblAnnounceVolume);
            panelBottom.Controls.Add(lblAnnounceSpeed);
            panelBottom.Controls.Add(lblAnnounceProgress);
            panelBottom.Controls.Add(lblAnnounceInfo);

            // Tooltips — hover hints for mouse/visual mode, listing the
            // keyboard shortcuts. Screen reader flow is unaffected (info
            // lives in AccessibleName as before).
            toolTip = new ToolTip();
            toolTip.SetToolTip(btnBack, Localization.T("Tip.Back"));
            toolTip.SetToolTip(btnPlayPause, Localization.T("Tip.PlayPause"));
            toolTip.SetToolTip(btnForward, Localization.T("Tip.Forward"));
            toolTip.SetToolTip(cmbSeek, Localization.T("Tip.Seek"));
            toolTip.SetToolTip(btnGoTo, Localization.T("Tip.GoTo"));
            toolTip.SetToolTip(btnTimer, Localization.T("Tip.Timer"));
            toolTip.SetToolTip(btnProperties, Localization.T("Tip.Properties"));
            toolTip.SetToolTip(tbVolume, Localization.T("Tip.Volume"));
            toolTip.SetToolTip(tbSpeed, Localization.T("Tip.Speed"));
            toolTip.SetToolTip(tbProgress, Localization.T("Tip.Progress"));

            this.Controls.Add(panelBottom);
        }

        // ──────────────────────────────────────────────
        // MPV event timer
        // ──────────────────────────────────────────────
        private void EventTimer_Tick(object sender, EventArgs e)
        {
            if (mpvHandle == IntPtr.Zero) return;

            while (true)
            {
                IntPtr eventPtr = mpv_wait_event(mpvHandle, 0);
                if (eventPtr == IntPtr.Zero) break;
                int eventId = Marshal.ReadInt32(eventPtr);
                if (eventId == 0) break;

                // Text book → mpv is not the engine. Drain its events (idle,
                // end-of-file from a previous audio book, …) but ignore them:
                // acting on IDLE would flip isPlaying off (killing the autoplay)
                // or wrongly "finish" the book. TTS drives text playback.
                if (currentBook != null && currentBook.IsTextBook) continue;

                if (eventId == 6) // MPV_EVENT_START_FILE
                {
                    // Get the current playlist index
                    double idx = 0;
                    mpv_get_property(mpvHandle, "playlist-pos", 5, ref idx);
                    currentPlaylistIndex = (int)idx;

                    if (!isPlaying)
                        mpv_set_property_string(mpvHandle, "pause", "yes");

                    currentProgress = 0;
                    tbProgress.Text = Localization.T("Player.Position.Text", FormatTime(0), FormatTime(0));
                    tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", 0);

                    UpdateTitleBar();

                    // Refresh the info box snapshot on part change — but
                    // never while the screen reader is reading it.
                    if (this.ActiveControl != tbInfo)
                        tbInfo.Text = BuildCurrentInfoText();
                }

                if (eventId == 7) // MPV_EVENT_END_FILE
                {
                    // Don't save if this is the END_FILE of the OLD book caused
                    // by "loadfile replace" when switching to a new book —
                    // at that point currentBook already points to the NEW book,
                    // so we'd write the old file's position/percent into it.
                    if (!isLoadingBook)
                        SaveCurrentBookProgress();
                }

                if (eventId == 11) // MPV_EVENT_IDLE
                {
                    // Natural end of the playlist while a book was playing —
                    // the whole book has been listened to.
                    if (currentBook != null && !isLoadingBook && isPlaying)
                        FinishCurrentBook();

                    SetPlayPauseState(false);
                    currentPlaylistIndex = 0;
                    currentProgress = 0;
                    tbProgress.Text = Localization.T("Player.Position.Text", FormatTime(0), FormatTime(0));
                    tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", 0);
                }
            }
        }

        // ──────────────────────────────────────────────
        // Progress timer
        // ──────────────────────────────────────────────
        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            // Text books update their position from the TTS reader's events.
            if (currentBook != null && currentBook.IsTextBook) return;
            if (mpvHandle == IntPtr.Zero || !isPlaying) return;

            double duration = 0;
            double position = 0;
            mpv_get_property(mpvHandle, "duration", 5, ref duration);
            mpv_get_property(mpvHandle, "time-pos", 5, ref position);

            if (duration > 0)
            {
                int prog = (int)(position / duration * 1000);
                currentProgress = prog;

                // Virtual position
                double virtualPos = GetVirtualPosition();
                double totalDur = (currentBook != null && currentBook.TotalDuration > 0)
                    ? currentBook.TotalDuration : duration;
                double virtualRemaining = totalDur - virtualPos;

                int percent = (int)(virtualPos / totalDur * 100);

                string posText = Localization.T("Player.Position.Text", FormatTime(virtualPos), FormatTime(totalDur));
                tbProgress.Text = posText;
                tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", percent);
                lblProgress.Text = posText;

                // Title bar counts down live (window caption — a sighted user
                // sees remaining time / percent advance during playback, not
                // only on pause / part change).
                UpdateTitleBar();

                // Info box: refresh only while it does NOT have focus, so the
                // displayed times advance for a sighted user without causing
                // screen-reader chatter (the on-Enter refresh keeps it correct
                // the moment it's focused). This preserves the "no live ticker
                // under the reader cursor" rule.
                if (this.ActiveControl != tbInfo)
                    tbInfo.Text = BuildCurrentInfoText();
            }
        }

        // ──────────────────────────────────────────────
        // Sleep timer
        // ──────────────────────────────────────────────
        // Spec (Session 5 + playback-coupling revision, Session 8):
        // presets 15/30/45/60 min + custom; expiry actions Stop /
        // Stop+close / Stop+close+shutdown; pressing the button (Ctrl+T)
        // while a timer runs stops playback and cancels the timer;
        // countdown on the button text; audible signals: a beep series at
        // -5 min, then a volume fadeout over the last 45 seconds. See the
        // comment at the state fields for the playback-coupling rules.

        private void BtnTimer_Click(object sender, EventArgs e)
        {
            // The timer is tied to a listening session — with an empty
            // player there is nothing to time. Same audible feedback as
            // Ctrl+G without a book: a short low beep. The test is the BOOK,
            // not a current file: a text book is read by the speech engine and
            // has no current file at all, which used to lock it out of the
            // timer entirely.
            if (currentBook == null)
            {
                tones.Play(300, 150);
                return;
            }

            // While a timer is ACTIVE, the button (or Ctrl+T) acts as a
            // one-press "good night is over" switch: playback stops and
            // the timer is cancelled, with the usual announcement. No new
            // dialog opens — to set another timer, press the (now idle)
            // button again; starting it will resume playback anyway.
            // Pause first, cancel second: the cancel also restores a
            // possible fadeout volume, which must happen while inaudible.
            if (sleepTimerActive)
            {
                if (isPlaying) PausePlaybackQuietly();
                CancelSleepTimer(true);
                return;
            }

            // Opening the dialog itself pauses playback, if it was running,
            // so the dialog is never modal-over-audible-playback. Confirming
            // always resumes via StartSleepTimer; cancelling resumes only if
            // playback was actually running before the dialog opened.
            bool wasPlaying = isPlaying;
            if (wasPlaying) PausePlaybackQuietly();

            using (SleepTimerForm dlg = new SleepTimerForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    StartSleepTimer(dlg.SelectedMinutes, dlg.SelectedAction);
                else if (wasPlaying)
                    ResumePlaybackQuietly();
            }
        }

        private void StartSleepTimer(int minutes, SleepTimerAction action)
        {
            sleepDeadline = DateTime.Now.AddMinutes(minutes);
            sleepAction = action;
            sleepTimerActive = true;
            // For timers of 5 minutes or less, the -5 min series would fire
            // immediately after closing the dialog — pointless noise, skip it.
            sleepWarned5Min = minutes <= 5;

            UpdateSleepTimerButton(true);
            sleepTimer.Start();

            // Starting a timer starts the listening session: if playback is
            // paused, it begins now. (Deliberately NOT BtnPlayPause_Click, which
            // is reserved for user-initiated toggles and carries the cancel hook.)
            if (!isPlaying) ResumePlaybackQuietly();

            AnnounceToScreenReader(lblAnnounceInfo,
                Localization.T("SleepTimer.Announce.Set", minutes));
        }

        /// <summary>Pauses whatever is playing — mpv for an audio book, the speech
        /// reader for a text one — WITHOUT the user-pause semantics: these are the
        /// sleep timer's own programmatic pauses, which must not cancel the timer
        /// (see the coupling rules in section 7 of the brief).</summary>
        private void PausePlaybackQuietly()
        {
            if (currentBook != null && currentBook.IsTextBook)
            {
                if (tts != null) tts.Pause();
            }
            else if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "pause", "yes");
            SetPlayPauseState(false);
        }

        /// <summary>The other half: resumes the right engine for this book.</summary>
        private void ResumePlaybackQuietly()
        {
            if (currentBook != null && currentBook.IsTextBook)
            {
                SetPlayPauseState(true);
                if (tts != null) tts.Play();
                return;
            }
            if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "pause", "no");
            SetPlayPauseState(true);
        }

        /// <summary>
        /// Stops the countdown, restores the button and — if the fadeout
        /// had already started — brings the mpv volume back to the user's
        /// set value. Announcing is optional: silent when the cancel is
        /// part of executing the expiry action or of the natural end of
        /// the book.
        /// </summary>
        private void CancelSleepTimer(bool announce)
        {
            sleepTimer.Stop();
            sleepTimerActive = false;
            ResetSleepTimerButton();
            RestorePlaybackVolume();

            if (announce)
                AnnounceToScreenReader(lblAnnounceInfo,
                    Localization.T("SleepTimer.Announce.Cancelled"));
        }

        /// <summary>
        /// Puts the mpv volume back to the user's set value (currentVolume).
        /// The fadeout only ever touches mpv directly — currentVolume, the
        /// Volume field and Book.ini never see the faded values — so this
        /// one call fully undoes it. Harmless when no fade was in progress.
        /// </summary>
        private void RestorePlaybackVolume()
        {
            if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "volume", currentVolume.ToString());
            // A text book fades the speech volume instead, so put that back too.
            if (tts != null && currentBook != null && currentBook.IsTextBook)
                tts.SetVolumeQuiet(currentVolume);
        }

        private void SleepTimer_Tick(object sender, EventArgs e)
        {
            TimeSpan remaining = sleepDeadline - DateTime.Now;

            if (remaining.TotalSeconds <= 0)
            {
                ExecuteSleepTimerAction();
                return;
            }

            UpdateSleepTimerButton(false);

            int sec = (int)Math.Ceiling(remaining.TotalSeconds);

            // -5 min warning: a short series of three beeps, once.
            if (!sleepWarned5Min && sec <= 300)
            {
                sleepWarned5Min = true;
                tones.Play(new[] { (900, 120), (900, 120), (900, 120) });
            }

            // Final stretch: a smooth volume fadeout over the last
            // SleepFadeSeconds (45 s). Linear ramp from the user's set
            // volume down to 0 at the deadline, applied straight to mpv —
            // currentVolume and the UI stay untouched, so the saved volume
            // and the Volume field never see the faded values. One step
            // per tick (1 s) is plenty smooth for a ~45-step ramp.
            // A text book fades the same way, through the speech engine. Speech
            // volume only takes effect on the NEXT sentence (changing it mid-
            // utterance would mean re-speaking the sentence), so there the fade
            // steps down sentence by sentence rather than second by second —
            // still a fade, just coarser, and the last minute of a book is
            // exactly where sentences are short.
            if (sec <= SleepFadeSeconds && isPlaying)
            {
                int fadedVolume = (int)Math.Round(currentVolume * (sec / (double)SleepFadeSeconds));
                if (currentBook != null && currentBook.IsTextBook)
                {
                    if (tts != null) tts.SetVolumeQuiet(fadedVolume);
                }
                else if (mpvHandle != IntPtr.Zero)
                    mpv_set_property_string(mpvHandle, "volume", fadedVolume.ToString());
            }
        }

        /// <summary>
        /// Refreshes the countdown on the timer button (text + spoken
        /// AccessibleName). While the button has FOCUS, per-tick updates are
        /// skipped (force=false) — the same JAWS echo guard as the info box;
        /// a changing control under the screen reader cursor causes chatter.
        /// One forced refresh happens on the button's Enter event, so the
        /// value announced on focus is current.
        /// </summary>
        private void UpdateSleepTimerButton(bool force)
        {
            if (!sleepTimerActive) return;
            if (!force && this.ActiveControl == btnTimer) return;

            TimeSpan remaining = sleepDeadline - DateTime.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            btnTimer.Text = Localization.T("Btn.Timer.Countdown", FormatCountdown(remaining));

            btnTimer.AccessibleName = remaining.TotalSeconds >= 60
                ? Localization.T("Btn.Timer.Accessible.Active", FormatCountdown(remaining))
                : Localization.T("Btn.Timer.Accessible.LessThanMinute");
        }

        private void ResetSleepTimerButton()
        {
            btnTimer.Text = Localization.T("Btn.Timer");
            btnTimer.AccessibleName = Localization.T("Btn.Timer.Accessible");
        }

        /// <summary>
        /// Compact countdown in minutes and seconds ("60:00", "29:59");
        /// the hour part appears only while more than 60 minutes remain
        /// ("1:29:59" → ... → "1:00:01", "60:00", "59:59", ...).
        /// </summary>
        private string FormatCountdown(TimeSpan t)
        {
            if (t.TotalMinutes > 60)
                return string.Format("{0}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
        }

        /// <summary>
        /// Executes the chosen expiry action — on the deadline, or earlier
        /// if the book ends by itself (see FinishCurrentBook). All three
        /// actions start the same way: pause playback (if anything is
        /// playing), then undo the fadeout and save progress. Pausing FIRST
        /// matters — restoring the volume while still audible would end the
        /// gentle fade with a full-volume blip.
        /// </summary>
        private void ExecuteSleepTimerAction()
        {
            if (isPlaying) PausePlaybackQuietly();

            // Also restores the (now inaudible) volume, so a later resume
            // plays at the user's set level.
            CancelSleepTimer(false);

            SaveCurrentBookProgress();

            switch (sleepAction)
            {
                case SleepTimerAction.Stop:
                    AnnounceToScreenReader(lblAnnounceInfo,
                        Localization.T("SleepTimer.Announce.Finished"));
                    break;

                case SleepTimerAction.StopClose:
                    // OnFormClosing saves progress again and tears down MPV.
                    this.Close();
                    break;

                case SleepTimerAction.StopCloseShutdown:
                    // Shutdown with a few seconds of grace so NBR (and the
                    // system) can finish closing cleanly. No long safety
                    // countdown by design — the user asked for this action.
                    try
                    {
                        Process.Start("shutdown", "/s /t 5");
                    }
                    catch
                    {
                        // If the shutdown command can't start (unlikely),
                        // still close the app — playback is already stopped
                        // and progress saved.
                    }
                    this.Close();
                    break;
            }
        }

        // ──────────────────────────────────────────────
        // Info box
        // ──────────────────────────────────────────────
        // Player book type — drives title bar, info box, seek steps and Go To.
        // M4B currently falls through to the audio branches (fallback = single-
        // file) until a dedicated chapter parser exists.
        private enum PlayerType { SingleAudio, MultiAudio, Daisy, M4b, FlatText, StructuredText }

        private PlayerType GetPlayerType()
        {
            if (currentBook == null) return PlayerType.SingleAudio;
            if (currentBook.IsTextBook)
                return currentBook.TextHeadings.Count > 0 ? PlayerType.StructuredText : PlayerType.FlatText;
            if (currentBook.IsDaisy && currentBook.DaisyHeadings.Count > 0)
                return PlayerType.Daisy;
            // M4B with real chapter marks (a single file navigated by chapter);
            // one with none falls through to single-file audio.
            if (currentBook.IsM4b && currentBook.M4bChapters.Count > 0)
                return PlayerType.M4b;
            // DAISY with no headings, or plain audio → single vs multi by parts.
            return currentBook.Chapters.Count > 1 ? PlayerType.MultiAudio : PlayerType.SingleAudio;
        }

        // Short format name for the info box (MP3 Audio / Daisy Audio 3 / Apple
        // Book M4B / EPUB / MS Word Docx …).
        private string PlayerFormatLabel()
        {
            if (currentBook == null) return Localization.T("Common.Dash");
            if (currentBook.IsTextBook)
                return string.IsNullOrWhiteSpace(currentBook.Format)
                    ? Localization.T("Common.Dash") : currentBook.Format;

            string fmt = currentBook.Format ?? "";
            int comma = fmt.IndexOf(',');
            string head = (comma >= 0 ? fmt.Substring(0, comma) : fmt).Trim();
            // DAISY carries its version instead of an extension; give it the same
            // "TAG — Official Name" shape as FriendlyFormatName produces.
            if (currentBook.IsDaisy && head.StartsWith("Daisy", StringComparison.OrdinalIgnoreCase))
                return "DAISY " + head.Substring(5).Trim() + " — Digital Accessible Information System";
            return string.IsNullOrWhiteSpace(head) ? Localization.T("Common.Dash") : head;
        }

        // Producer (audio/accessible-edition producer), already normalized;
        // empty when there is none.
        private string PlayerProducer()
        {
            return currentBook != null ? BookData.NormalizeProducer(currentBook.Producer) : "";
        }

        // Publisher (print-edition publisher, dc:publisher); empty when none.
        private string PlayerPublisher()
        {
            return currentBook != null ? BookData.NormalizeProducer(currentBook.Publisher) : "";
        }

        // Percentage of the whole audiobook played, one decimal (e.g. "12.3").
        private string AudioPercentString(double pos, double total)
        {
            if (total <= 0) return "0.0";
            double p = 100.0 * pos / total;
            if (p < 0) p = 0; else if (p > 100) p = 100;
            return p.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Index of the M4B chapter covering the given position (last chapter at
        // or before it). -1 when the book has no M4B chapters.
        private int M4bChapterIndexAt(double virtualPos)
        {
            if (currentBook == null || currentBook.M4bChapters.Count == 0) return -1;
            var cs = currentBook.M4bChapters;
            int idx = 0;
            for (int i = cs.Count - 1; i >= 0; i--)
                if (cs[i].Position <= virtualPos + 0.05) { idx = i; break; }
            return idx;
        }

        // Absolute virtual-timeline positions of the M4B chapters, in order.
        private System.Collections.Generic.List<double> M4bChapterPositions()
        {
            var list = new System.Collections.Generic.List<double>();
            if (currentBook != null)
                foreach (var c in currentBook.M4bChapters) list.Add(c.Position);
            return list;
        }

        // DAISY page label covering the given position (last page at or before
        // it). null when the book declares no page targets.
        private string DaisyPageLabelAt(double virtualPos)
        {
            if (currentBook == null || currentBook.DaisyPages.Count == 0) return null;
            var ps = currentBook.DaisyPages;
            string label = null;
            for (int i = ps.Count - 1; i >= 0; i--)
                if (ps[i].Position <= virtualPos + 0.05) { label = ps[i].Label; break; }
            return label;
        }

        // Current print-page label in a structured text book (last page marker
        // at or before the reading position). null when the book has no pages.
        private string CurrentTextPageLabel()
        {
            if (currentBook == null || tts == null || currentBook.TextPages.Count == 0) return null;
            int at = tts.CharPosition;
            string label = null;
            foreach (var p in currentBook.TextPages)
            {
                if (p.Offset <= at) label = p.Label;
                else break;
            }
            return label;
        }

        // Current heading in a structured text book (last heading whose offset is
        // at or before the reading position). Label is null when none applies.
        private (int Level, string Label) CurrentTextHeading()
        {
            var res = (Level: 0, Label: (string)null);
            if (currentBook == null || tts == null || currentBook.TextHeadings.Count == 0)
                return res;
            int at = tts.CharPosition;
            foreach (var h in currentBook.TextHeadings)
            {
                if (h.Offset <= at) res = (h.Level, h.Label);
                else break;
            }
            return res;
        }

        private string BuildInfoBoxText(double segPosition, double segDuration, double segRemaining,
            double virtualPos, double totalDur, double virtualRemaining)
        {
            string dash = Localization.T("Common.Dash");
            string nl = "\r\n";
            PlayerType type = GetPlayerType();

            string title = currentBook != null ? currentBook.Title :
                (currentFile != null ? System.IO.Path.GetFileNameWithoutExtension(currentFile) : dash);
            string author = currentBook != null && !string.IsNullOrWhiteSpace(currentBook.Author)
                ? currentBook.Author : "";
            int bmk = currentBook != null ? currentBook.Bookmarks.Count : 0;

            var sb = new System.Text.StringBuilder();
            sb.Append(Localization.T("Player.Info.TitleLabel")).Append(' ').Append(title).Append(nl);
            sb.Append(Localization.T("Player.Info.AuthorLabel")).Append(' ').Append(author).Append(nl);

            if (type == PlayerType.Daisy)
            {
                // TITLE / AUTHOR / Chapter / Page / Bookmarks / Daisy Audio / times.
                int hi = DaisyHeadingIndexAt(virtualPos);
                string chapter = hi >= 0 ? currentBook.DaisyHeadings[hi].Label : dash;
                string page = DaisyPageLabelAt(virtualPos) ?? dash;
                sb.Append(Localization.T("Player.Info.ChapterLabel")).Append(' ').Append(chapter).Append(nl);
                sb.Append(Localization.T("Player.Info.PageLabel")).Append(' ').Append(page).Append(nl);
                sb.Append(Localization.T("Player.Info.BookmarksLabel")).Append(' ').Append(bmk).Append(nl);
                sb.Append(PlayerFormatLabel()).Append(nl).Append(nl);
                sb.Append(Localization.T("Player.Info.ElapsedLabel")).Append(' ').Append(FormatTime(virtualPos)).Append(nl);
                sb.Append(Localization.T("Player.Info.RemainingLabel")).Append(" -").Append(FormatTime(virtualRemaining));
            }
            else if (type == PlayerType.MultiAudio)
            {
                // TITLE / AUTHOR / format / Bookmarks / Part X/Y / part + total times.
                string part = dash;
                int partNum = currentPlaylistIndex + 1;
                if (currentBook != null && currentBook.Chapters.Count > 0
                    && currentPlaylistIndex < currentBook.Chapters.Count)
                    part = partNum + "/" + currentBook.Chapters.Count;
                sb.Append(PlayerFormatLabel()).Append(nl);
                sb.Append(Localization.T("Player.Info.BookmarksLabel")).Append(' ').Append(bmk).Append(nl);
                sb.Append(Localization.T("Player.Info.PartLabel")).Append(' ').Append(part).Append(nl).Append(nl);
                sb.Append(Localization.T("Player.Info.PartNumElapsed", partNum)).Append(' ').Append(FormatTime(segPosition)).Append(nl);
                sb.Append(Localization.T("Player.Info.PartNumRemaining", partNum)).Append(" -").Append(FormatTime(segRemaining)).Append(nl);
                sb.Append(Localization.T("Player.Info.ElapsedLabel")).Append(' ').Append(FormatTime(virtualPos)).Append(nl);
                sb.Append(Localization.T("Player.Info.RemainingLabel")).Append(" -").Append(FormatTime(virtualRemaining));
            }
            else if (type == PlayerType.M4b)
            {
                // TITLE / AUTHOR / Chapter / Bookmarks / Apple Book M4B / times.
                int ci = M4bChapterIndexAt(virtualPos);
                string chapter = ci >= 0 ? currentBook.M4bChapters[ci].Title : dash;
                sb.Append(Localization.T("Player.Info.ChapterLabel")).Append(' ').Append(chapter).Append(nl);
                sb.Append(Localization.T("Player.Info.BookmarksLabel")).Append(' ').Append(bmk).Append(nl);
                sb.Append(PlayerFormatLabel()).Append(nl).Append(nl);
                sb.Append(Localization.T("Player.Info.ElapsedLabel")).Append(' ').Append(FormatTime(virtualPos)).Append(nl);
                sb.Append(Localization.T("Player.Info.RemainingLabel")).Append(" -").Append(FormatTime(virtualRemaining));
            }
            else
            {
                // Single-file audio (and M4B-without-chapters fallback): TITLE /
                // AUTHOR / format / Bookmarks / elapsed / remaining.
                sb.Append(PlayerFormatLabel()).Append(nl);
                sb.Append(Localization.T("Player.Info.BookmarksLabel")).Append(' ').Append(bmk).Append(nl).Append(nl);
                sb.Append(Localization.T("Player.Info.ElapsedLabel")).Append(' ').Append(FormatTime(virtualPos)).Append(nl);
                sb.Append(Localization.T("Player.Info.RemainingLabel")).Append(" -").Append(FormatTime(virtualRemaining));
            }

            return sb.ToString();
        }

        /// <summary>Index of the DAISY heading covering the given virtual
        /// position (the last heading at or before it). -1 when the current
        /// book has no DAISY headings.</summary>
        private int DaisyHeadingIndexAt(double virtualPos)
        {
            if (currentBook == null || !currentBook.IsDaisy || currentBook.DaisyHeadings.Count == 0)
                return -1;
            var hs = currentBook.DaisyHeadings;
            int idx = 0;
            for (int i = hs.Count - 1; i >= 0; i--)
                if (hs[i].Position <= virtualPos + 0.05) { idx = i; break; }
            return idx;
        }

        /// <summary>
        /// Builds the info text for the current playback moment — used by
        /// the on-focus snapshot and the I key. Returns the placeholder
        /// when nothing is loaded.
        /// </summary>
        private string BuildTextInfoText()
        {
            string dash = Localization.T("Common.Dash");
            string nl = "\r\n";
            PlayerType type = GetPlayerType();

            var sb = new System.Text.StringBuilder();
            sb.Append(Localization.T("Player.Info.TitleLabel")).Append(' ')
              .Append(string.IsNullOrWhiteSpace(currentBook.Title) ? dash : currentBook.Title).Append(nl);
            sb.Append(Localization.T("Player.Info.AuthorLabel")).Append(' ')
              .Append(string.IsNullOrWhiteSpace(currentBook.Author) ? "" : currentBook.Author).Append(nl);

            // Structured text carries producer + publisher and chapter/page lines.
            if (type == PlayerType.StructuredText)
            {
                string prod = PlayerProducer();
                if (prod.Length > 0)
                    sb.Append(Localization.T("Player.Info.ProducerLabel")).Append(' ').Append(prod).Append(nl);
                string pub = PlayerPublisher();
                if (pub.Length > 0)
                    sb.Append(Localization.T("Player.Info.PublisherLabel")).Append(' ').Append(pub).Append(nl);
            }

            sb.Append(PlayerFormatLabel()).Append(nl);

            if (type == PlayerType.StructuredText)
            {
                var h = CurrentTextHeading();
                sb.Append(Localization.T("Player.Info.ChapterLabel")).Append(' ')
                  .Append(string.IsNullOrWhiteSpace(h.Label) ? dash : h.Label).Append(nl);
                // PAGE sits between Chapter and Bookmarks; shown only when the
                // book has page markers.
                if (currentBook.TextPages.Count > 0)
                {
                    string page = CurrentTextPageLabel();
                    sb.Append(Localization.T("Player.Info.PageLabel")).Append(' ')
                      .Append(string.IsNullOrWhiteSpace(page) ? dash : page).Append(nl);
                }
            }
            else if (type == PlayerType.FlatText && currentBook.TextPages.Count > 0)
            {
                // Flat book that still has real print pages (a paged PDF with no
                // outline): no Chapter line, but the page is a useful locator.
                string page = CurrentTextPageLabel();
                sb.Append(Localization.T("Player.Info.PageLabel")).Append(' ')
                  .Append(string.IsNullOrWhiteSpace(page) ? dash : page).Append(nl);
            }

            int bmk = currentBook != null ? currentBook.Bookmarks.Count : 0;
            sb.Append(Localization.T("Player.Info.BookmarksLabel")).Append(' ').Append(bmk).Append(nl);

            // Speech engine + voice + reading speed, e.g.
            // "Speech engine: RHVoice Karmela, 250 WPM".
            string voice = tts != null && !string.IsNullOrEmpty(tts.CurrentVoice) ? tts.CurrentVoice : dash;
            sb.Append(Localization.T("Player.Info.SpeechEngineLabel")).Append(' ')
              .Append(voice).Append(", ").Append(currentWpm).Append(" WPM").Append(nl).Append(nl);

            int total = tts != null ? tts.TotalChars : 0;
            int at = tts != null ? tts.CharPosition : 0;
            double elapsed = TextSeconds(at);
            double totalSec = TextSeconds(total);

            // Elapsed/remaining carry no "≈" — the approximation mark is reserved
            // for the total-time figure (shown only in the Library info box).
            sb.Append(Localization.T("Player.Info.ElapsedLabel")).Append(' ')
              .Append(FormatTime(elapsed)).Append("  ").Append(TextPercentString()).Append('%').Append(nl);
            sb.Append(Localization.T("Player.Info.RemainingLabel")).Append(" -")
              .Append(FormatTime(totalSec - elapsed));
            return sb.ToString();
        }

        private string BuildCurrentInfoText()
        {
            if (currentBook != null && currentBook.IsTextBook)
                return BuildTextInfoText();

            if (mpvHandle == IntPtr.Zero || currentFile == null)
                return BuildInfoBoxPlaceholder();

            double duration = 0;
            double position = 0;
            mpv_get_property(mpvHandle, "duration", 5, ref duration);
            mpv_get_property(mpvHandle, "time-pos", 5, ref position);

            if (duration <= 0)
                return BuildInfoBoxPlaceholder();

            double virtualPos = GetVirtualPosition();
            double totalDur = (currentBook != null && currentBook.TotalDuration > 0)
                ? currentBook.TotalDuration : duration;

            return BuildInfoBoxText(position, duration, duration - position,
                virtualPos, totalDur, totalDur - virtualPos);
        }

        private string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private double ParseTimeToSeconds(string timeStr)
        {
            if (string.IsNullOrEmpty(timeStr)) return 0;
            TimeSpan t;
            if (TimeSpan.TryParse(timeStr, out t))
                return t.TotalSeconds;
            return 0;
        }

        // ──────────────────────────────────────────────
        // Title bar
        // ──────────────────────────────────────────────
        private void UpdateTitleBar()
        {
            string appName = Localization.T("App.Name");

            if (currentBook == null)
            {
                if (currentFile != null)
                {
                    string st = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");
                    this.Text = appName + " — " + System.IO.Path.GetFileNameWithoutExtension(currentFile) + st;
                }
                else this.Text = appName;
                return;
            }

            string stateText = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");
            const string sep = " — ";
            PlayerType type = GetPlayerType();
            string title = currentBook.Title;
            string body;

            if (type == PlayerType.FlatText || type == PlayerType.StructuredText)
            {
                // Text: per TTS speed. Structured → Title / Chapter / Page (or
                // -remaining if no pages); flat → Title / X.Y% / -remaining.
                double elapsed = tts != null ? TextSeconds(tts.CharPosition) : 0;
                double total = tts != null ? TextSeconds(tts.TotalChars) : 0;
                string remaining = "-" + FormatTime(total - elapsed);
                if (type == PlayerType.StructuredText)
                {
                    var h = CurrentTextHeading();
                    string chap = string.IsNullOrWhiteSpace(h.Label) ? Localization.T("Common.Dash") : h.Label;
                    // A book with print pages shows the page number here instead
                    // of the (estimated) remaining time.
                    string tail = remaining;
                    if (currentBook.TextPages.Count > 0)
                    {
                        string page = CurrentTextPageLabel();
                        tail = Localization.T("Player.Info.PageLabel") + " " +
                               (string.IsNullOrWhiteSpace(page) ? Localization.T("Common.Dash") : page);
                    }
                    body = title + sep + chap + sep + tail;
                }
                else
                {
                    // Flat text. With real print pages (a paged PDF/EPUB that has
                    // no chapters) the page is the natural locator →
                    // "Title — Page: N — X.Y%"; a plain .txt (no pages) keeps
                    // "Title — X.Y% — -remaining".
                    string pct = (tts != null && tts.TotalChars > 0) ? TextPercentString() : "0.0";
                    if (currentBook.TextPages.Count > 0)
                    {
                        string page = CurrentTextPageLabel();
                        string p = Localization.T("Player.Info.PageLabel") + " " +
                                   (string.IsNullOrWhiteSpace(page) ? Localization.T("Common.Dash") : page);
                        body = title + sep + p + sep + pct + "%";
                    }
                    else
                    {
                        body = title + sep + pct + "%" + sep + remaining;
                    }
                }
            }
            else
            {
                double virtualPos = GetVirtualPosition();
                double totalDur = currentBook.TotalDuration > 0 ? currentBook.TotalDuration : 0;
                if (totalDur <= 0 && mpvHandle != IntPtr.Zero)
                {
                    double d = 0;
                    mpv_get_property(mpvHandle, "duration", 5, ref d);
                    totalDur = d;
                }
                string remaining = "-" + FormatTime(totalDur - virtualPos);

                if (type == PlayerType.Daisy)
                {
                    // DAISY: Title / Chapter (heading) / Page.
                    int hi = DaisyHeadingIndexAt(virtualPos);
                    string chap = hi >= 0 ? currentBook.DaisyHeadings[hi].Label : Localization.T("Common.Dash");
                    string page = DaisyPageLabelAt(virtualPos);
                    body = title + sep + chap + (page != null ? sep + page : "");
                }
                else if (type == PlayerType.MultiAudio)
                {
                    // Multi-file audio: Title / part X/Y / -remaining.
                    string part = Localization.T("Common.Dash");
                    if (currentBook.Chapters.Count > 0 && currentPlaylistIndex < currentBook.Chapters.Count)
                        part = (currentPlaylistIndex + 1) + "/" + currentBook.Chapters.Count;
                    body = title + sep + part + sep + remaining;
                }
                else if (type == PlayerType.M4b)
                {
                    // M4B: Title / Chapter.
                    int ci = M4bChapterIndexAt(virtualPos);
                    string chap = ci >= 0 ? currentBook.M4bChapters[ci].Title : Localization.T("Common.Dash");
                    body = title + sep + chap;
                }
                else
                {
                    // Single-file audio (and M4B-without-chapters fallback):
                    // Title / X.Y% / -remaining.
                    body = title + sep + AudioPercentString(virtualPos, totalDur) + "%" + sep + remaining;
                }
            }

            this.Text = appName + " — " + body + stateText;
        }

        // ──────────────────────────────────────────────
        // Saving progress
        // ──────────────────────────────────────────────
        private void SaveCurrentBookProgress()
        {
            if (currentBook == null) return;
            try
            {
                // Text book → remember the character offset and percentage.
                if (currentBook.IsTextBook)
                {
                    if (tts != null)
                    {
                        currentBook.TextPosition = tts.CharPosition;
                        int pct = tts.TotalChars > 0
                            ? (int)(100.0 * tts.CharPosition / tts.TotalChars) : 0;
                        // A started book (even <1 % of a long one, which rounds
                        // to 0) must count as "reading", not "unread".
                        if (pct == 0 && tts.CharPosition > 0) pct = 1;
                        currentBook.PercentListened = pct;
                    }
                    currentBook.Volume = currentVolume;
                    RememberCurrentVoicePrefs();   // fills TextWpm/TextVolume/TextPitch too
                    currentBook.SeekStep = EncodeSeekStep(CurrentSeekStep());
                    currentBook.Save();
                    return;
                }

                double position = 0;
                double duration = 0;
                mpv_get_property(mpvHandle, "time-pos", 5, ref position);
                mpv_get_property(mpvHandle, "duration", 5, ref duration);

                double virtualPos = GetVirtualPosition();
                currentBook.LastPosition = FormatTime(virtualPos);

                if (currentBook.TotalDuration > 0)
                    currentBook.PercentListened = (int)(virtualPos / currentBook.TotalDuration * 100);
                else if (duration > 0)
                    currentBook.PercentListened = (int)(position / duration * 100);

                currentBook.Volume = currentVolume;
                currentBook.Speed = currentSpeed;
                // Persist the step *kind* (and heading depth), not the row
                // index — the row layout varies per book (Part vs Heading/Page/
                // Bookmark come and go).
                currentBook.SeekStep = EncodeSeekStep(CurrentSeekStep());
                currentBook.Save();
            }
            catch (Exception)
            {
                // Silently ignore — e.g. if the book's folder was deleted while
                // it was active (Form1's background timers keep running while
                // the modal Library window is open).
            }
        }

        /// <summary>
        /// Called when a book plays to its natural end: marks it as finished
        /// (100%, position reset — the "Read" shelf group), unloads it from
        /// the player (so it's no longer "active" and can be deleted), and
        /// opens the Library so the next step — pick another book, delete
        /// the finished one — is right at hand.
        /// With an active sleep timer, the natural end counts as the end of
        /// the listening session and triggers the chosen action early.
        /// </summary>
        private void FinishCurrentBook()
        {
            try
            {
                currentBook.PercentListened = 100;
                currentBook.LastPosition = "00:00:00";
                currentBook.TextPosition = 0;
                currentBook.Volume = currentVolume;
                currentBook.Speed = currentSpeed;
                currentBook.Save();
            }
            catch (Exception)
            {
                // Silently ignore — folder may have been deleted meanwhile.
            }

            // Unload — the player returns to its empty state. With
            // currentBook == null, no later SaveCurrentBookProgress can
            // overwrite the saved 100% with a stale position.
            currentBook = null;
            currentFile = null;
            RebuildSeekSteps();
            tbInfo.Text = BuildInfoBoxPlaceholder();
            UpdateTitleBar();

            // Sleep timer override: the book ending by itself ends the
            // listening session, so the chosen action fires now rather
            // than at the deadline.
            if (sleepTimerActive)
            {
                if (sleepAction == SleepTimerAction.Stop)
                {
                    // The "stop playback" part has already happened
                    // naturally — quietly drop the timer and continue with
                    // the normal finish flow (library opens below).
                    CancelSleepTimer(false);
                }
                else
                {
                    // Close/shutdown: execute right away. The library is
                    // deliberately NOT opened first — no point in a modal
                    // window for an app that's about to close (and Close()
                    // under a fresh modal dialog would be fragile anyway).
                    // BeginInvoke: let the MPV event loop finish this tick
                    // first, same as the library path.
                    BeginInvoke((Action)(() => ExecuteSleepTimerAction()));
                    return;
                }
            }

            // BeginInvoke: let the MPV event loop finish this tick first,
            // then open the modal library. Skipped if the library is
            // already open (book finished while browsing it).
            if (!isLibraryOpen)
                BeginInvoke((Action)(() => BtnLibrary_Click(null, EventArgs.Empty)));
        }

        /// <summary>
        /// Unloads the active book without touching its saved progress — used
        /// when the user marks the currently-playing book as read from the
        /// Library (which already persisted it at 100%). Stops playback and
        /// returns the player to its empty state.
        /// </summary>
        private void UnloadActiveBook()
        {
            SetPlayPauseState(false);
            if (tts != null) tts.Stop();
            // "stop" unloads the current file and clears the playlist, so mpv
            // releases its handle on the audio — otherwise the folder can't be
            // deleted while it's still open (the reason a marked-read active book
            // couldn't be deleted until another book was loaded).
            MpvCommand("stop");

            // currentBook == null first, so no stray SaveCurrentBookProgress can
            // overwrite the 100% the Library just wrote with a stale position.
            currentBook = null;
            currentFile = null;
            RebuildSeekSteps();
            tbInfo.Text = BuildInfoBoxPlaceholder();
            UpdateTitleBar();
        }

        // ──────────────────────────────────────────────
        // Buttons
        // ──────────────────────────────────────────────
        private void SetPlayPauseState(bool playing)
        {
            isPlaying = playing;
            btnPlayPause.Text = playing ? "⏸" : "▶";
            btnPlayPause.AccessibleName = playing ? Localization.T("Btn.Pause.Accessible") : Localization.T("Btn.Play.Accessible");
            UpdateTitleBar();
        }

        private void BtnPlayPause_Click(object sender, EventArgs e)
        {
            // Text book → drive the TTS reader instead of mpv.
            if (currentBook != null && currentBook.IsTextBook)
            {
                if (isPlaying)
                {
                    tts.Pause();
                    SetPlayPauseState(false);
                    if (sleepTimerActive) CancelSleepTimer(true);
                }
                else
                {
                    tts.Play();
                    SetPlayPauseState(true);
                }
                return;
            }

            if (mpvHandle == IntPtr.Zero || currentFile == null)
            {
                OpenFile();
                return;
            }

            if (isPlaying)
            {
                mpv_set_property_string(mpvHandle, "pause", "yes");
                SetPlayPauseState(false);

                // A MANUAL pause ends the listening session — an active
                // sleep timer is cancelled, with an announcement. This is
                // the only place the cancel hook lives: every user-initiated
                // pause (Space, X, on-screen button, media keys) routes
                // through here, while programmatic pauses (seeks, loading,
                // the timer's own expiry) use mpv directly and are
                // unaffected. The cancel runs AFTER the pause so that the
                // volume restore inside it (undoing a possible fadeout in
                // progress) happens while nothing is audible.
                if (sleepTimerActive)
                    CancelSleepTimer(true);
            }
            else
            {
                mpv_set_property_string(mpvHandle, "pause", "no");
                SetPlayPauseState(true);
            }
        }

        // On-screen Back/Forward buttons are the mouse/visual-mode
        // equivalent of Shift+Left/Shift+Right and the media keys — all of
        // them are navigation level 3 and follow the step selected in the
        // seek dropdown.
        private void BtnBack_Click(object sender, EventArgs e)
        {
            SeekStepBackward();
        }

        private void BtnForward_Click(object sender, EventArgs e)
        {
            SeekStepForward();
        }

        /// <summary>
        /// Part navigation used by the "Part" seek step: more than 3 s into
        /// the current part rewinds to its start, otherwise jumps to the
        /// previous part.
        /// </summary>
        private void PartBack()
        {
            if (mpvHandle == IntPtr.Zero) return;
            double position = 0;
            mpv_get_property(mpvHandle, "time-pos", 5, ref position);
            if (position > 3.0)
            {
                MpvCommand("seek", "0", "absolute");
                return;
            }
            MpvCommand("playlist-prev", "weak");
            if (!isPlaying)
                mpv_set_property_string(mpvHandle, "pause", "yes");
        }

        private void PartForward()
        {
            if (mpvHandle == IntPtr.Zero) return;
            MpvCommand("playlist-next", "weak");
            if (!isPlaying)
                mpv_set_property_string(mpvHandle, "pause", "yes");
        }

        /// <summary>
        /// Bookmark navigation used by the "Bookmark" seek step. Forward
        /// jumps to the next bookmark after the current position. Back
        /// mirrors PartBack's 3-second grace: more than 3 s past the
        /// preceding bookmark rewinds to it, otherwise jumps to the one
        /// before it (or re-seeks to the first bookmark if there is none
        /// earlier). Both preserve the current play/pause state, same as
        /// any other virtual-position seek.
        /// </summary>
        // ── Positions in the book's own unit ──────────────────────────────
        // An audio book is measured in seconds on the virtual timeline, a text
        // book in characters. Bookmarks are stored in whichever unit the book
        // uses, so these three keep the bookmark code identical for both.

        /// <summary>Where we are, in this book's own unit.</summary>
        private double BookPosition()
        {
            if (currentBook != null && currentBook.IsTextBook)
                return tts != null ? tts.CharPosition : 0;
            return GetVirtualPosition();
        }

        /// <summary>Jumps there, in this book's own unit.</summary>
        private void SeekToBookPosition(double position, Action after = null)
        {
            if (currentBook != null && currentBook.IsTextBook)
            {
                if (tts != null) tts.SeekToChar((int)Math.Round(position));
                UpdateTextPositionDisplay();
                after?.Invoke();
                return;
            }
            SeekToVirtualPosition(position, after);
        }

        /// <summary>How a bookmark's position reads in the Manage Bookmarks list.
        /// An audio book shows the clock time it sits at. A text book's position is
        /// a character offset, which tells the user nothing, so it shows how far
        /// into the book it is — and then **the words it sits on**, which is what
        /// actually identifies the place ("41,7 %, Tada je Perica shvatio da…").</summary>
        private string FormatBookmarkPosition(double position)
        {
            if (currentBook != null && currentBook.IsTextBook)
            {
                int total = tts != null ? tts.TotalChars : 0;
                double pct = total > 0 ? 100.0 * position / total : 0;
                string where = pct.ToString("0.0") + " %";
                string snippet = tts != null ? tts.SnippetAt((int)Math.Round(position), 6) : "";
                return string.IsNullOrEmpty(snippet) ? where : where + ", " + snippet;
            }
            TimeSpan t = TimeSpan.FromSeconds(position);
            return string.Format("{0:D2}:{1:D2}", (int)t.TotalHours, t.Minutes);
        }

        /// <summary>The "you have only just passed it" window that makes Back
        /// rewind to the current mark instead of the one before: 3 seconds, in
        /// whichever unit this book counts in.</summary>
        private double BookBackGrace()
        {
            if (currentBook != null && currentBook.IsTextBook)
                return Math.Max(20, TtsReader.CharsPerMinute(currentWpm) * 3.0 / 60.0);
            return 3.0;
        }

        private void BookmarkBack()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                tones.Play(300, 150);
                return;
            }

            double pos = BookPosition();
            int currentIndex = -1;
            for (int i = currentBook.Bookmarks.Count - 1; i >= 0; i--)
            {
                if (currentBook.Bookmarks[i] <= pos)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                tones.Play(300, 150);
                return;
            }

            if (currentIndex == 0 || pos - currentBook.Bookmarks[currentIndex] > BookBackGrace())
                SeekToBookPosition(currentBook.Bookmarks[currentIndex]);
            else
                SeekToBookPosition(currentBook.Bookmarks[currentIndex - 1]);
        }

        private void BookmarkForward()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                tones.Play(300, 150);
                return;
            }

            double pos = BookPosition();
            foreach (double bookmark in currentBook.Bookmarks)
            {
                if (bookmark > pos)
                {
                    SeekToBookPosition(bookmark);
                    return;
                }
            }

            // Already past the last bookmark.
            tones.Play(300, 150);
        }

        private void BtnLibrary_Click(object sender, EventArgs e)
        {
            if (isLibraryOpen) return;

            SaveCurrentBookProgress();

            isLibraryOpen = true;
            try
            {
                // The callback lets the Library unload the active book the moment
                // it's marked read (so it can be deleted in the same session,
                // with mpv's file handle released) rather than waiting for close.
                using (LibraryForm libraryForm = new LibraryForm(appSettings,
                    currentBook != null ? currentBook.FolderPath : null, UnloadActiveBook))
                {
                    libraryForm.ShowDialog(this);

                    if (libraryForm.DialogResult == DialogResult.OK && libraryForm.SelectedBook != null)
                    {
                        LoadBook(libraryForm.SelectedBook, true);
                    }
                }
            }
            finally
            {
                isLibraryOpen = false;
            }
        }

        private void BtnSettings_Click(object sender, EventArgs e)
        {
            // Pass the live device list + a live-apply hook so the user hears the
            // output move to the picked card immediately (great for testing without
            // fiddling with an external amp).
            var audioDevices = MpvAudioDevices.Get(mpvHandle);
            using (SettingsForm dlg = new SettingsForm(appSettings, audioDevices, SetAudioDeviceLive))
            {
                dlg.ShowDialog(this);
            }
            // Re-apply the persisted device: on OK/Apply this is the newly-saved
            // one; on Cancel it reverts any live preview that wasn't kept.
            SetAudioDeviceLive(appSettings.AudioDevice);
            // The media keys may have been switched on/off or gone global.
            ApplyMediaKeySettings();
            // The user may have edited their speech dictionary: drop what is
            // cached and take whatever now applies, from the next sentence on.
            SpeechDictionaries.Reload();
            if (tts != null && currentBook != null && currentBook.IsTextBook)
                tts.Dictionaries = SpeechDictionaries.Active(tts.CurrentVoice, currentBook.TextLanguage);
            // A book that has chosen its own voice in Properties is NEVER touched by
            // a Settings change — that is the whole point of the per-book setting.
            // A book that has not is simply reading with the default, so when the
            // default changes it follows, voice and all: its speed/volume/pitch
            // come from what that voice was last read with here (ApplyTtsSettings),
            // not from the voice being left behind.
            if (currentBook != null && currentBook.IsTextBook && tts != null
                && string.IsNullOrEmpty(currentBook.TextVoice))
            {
                ApplyTtsSettings();
            }
        }

        /// <summary>Applies a reading-setting edit from Properties to the live
        /// reader, so the voice is heard as it is chosen. Only the reader is touched;
        /// the book settles when the dialog closes, so Cancel restores it.</summary>
        private void PreviewTextSpeech(string voice, int wpm, int volume, int pitch)
        {
            if (tts == null) return;
            if (!string.IsNullOrEmpty(voice) &&
                !string.Equals(tts.CurrentVoice, voice, StringComparison.OrdinalIgnoreCase))
                tts.SetVoice(voice);
            currentWpm = Math.Min(400, Math.Max(80, wpm));
            currentVolume = Math.Min(100, Math.Max(0, volume));
            currentTextPitch = Math.Min(10, Math.Max(-10, pitch));
            tts.SetRate(TtsReader.WpmToRate(currentWpm));
            tts.SetVolume(currentVolume);
            tts.SetPitch(currentTextPitch * 5);
            // The player's own fields follow the preview, so what is heard and what
            // is shown never disagree; Cancel puts the stored values back.
            UpdateSpeedDisplay();
            UpdateVolumeDisplay();
        }

        /// <summary>Hears a volume / speed edit from the Properties dialog straight
        /// away, the same way the processing stages preview. Only playback is
        /// touched — the player's own fields are settled when the dialog closes, so
        /// Cancel simply restores what the book had.</summary>
        private void PreviewPlayback(int volume, int speedPercent)
        {
            if (mpvHandle == IntPtr.Zero) return;
            mpv_set_property_string(mpvHandle, "volume",
                Math.Min(100, Math.Max(0, volume)).ToString());
            mpv_set_property_string(mpvHandle, "speed",
                (Math.Min(300, Math.Max(50, speedPercent)) / 100.0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Switch libmpv's output device live (empty → "auto", the
        /// system default). Used for the Settings → Device live preview and to
        /// re-apply the persisted choice when the dialog closes.</summary>
        private void SetAudioDeviceLive(string device)
        {
            // Audio books play through mpv; text books through the TTS backend
            // (SAPI AudioOutput). Route the picked card to both so the choice
            // works whichever kind of book is open.
            if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "audio-device",
                    string.IsNullOrEmpty(device) ? "auto" : device);
            tts?.SetAudioDevice(device);
            tones.SetDevice(device);   // the app's own beeps follow the book
        }

        private void BtnHelp_Click(object sender, EventArgs e)
        {
            MessageBox.Show(Localization.T("Dialog.Help.ComingSoon"), Localization.T("Dialog.Help.Title"));
        }

        private void BtnProperties_Click(object sender, EventArgs e)
        {
            // Same "no go" feedback as Go To / bookmarks with nothing loaded.
            if (currentBook == null)
            {
                tones.Play(300, 150);
                return;
            }
            // Volume and speed live in the player's own fields until progress is
            // saved, so hand the CURRENT values to the dialog — otherwise it would
            // show the last-saved ones and look stale.
            currentBook.Volume = currentVolume;
            currentBook.Speed = currentSpeed;
            // The speech settings live in the player's own fields until progress is
            // saved, so file the LIVE ones under the voice in use — that is what the
            // dialog will show for it.
            if (currentBook.IsTextBook) RememberCurrentVoicePrefs();

            // Pass a live-preview hook so edits are heard on the fly while the
            // dialog is open.
            using (PropertiesForm dlg = new PropertiesForm(currentBook, ApplySoundProcessing, PreviewPlayback,
                                                           PreviewTextSpeech, appSettings))
            {
                dlg.ShowDialog(this);
            }
            if (currentBook == null) return;

            // Settle on the persisted state (OK saved the new values; Cancel
            // kept the old ones) — either way re-apply the book's settings.
            ApplySoundProcessing(currentBook.Sound, false);

            // The dialog may have changed volume / speed / the reading voice; take
            // them back so playback matches what the user just set.
            currentVolume = Math.Min(100, Math.Max(0, currentBook.Volume));
            currentSpeed = Math.Min(300, Math.Max(50, currentBook.Speed));
            if (mpvHandle != IntPtr.Zero)
            {
                mpv_set_property_string(mpvHandle, "volume", currentVolume.ToString());
                mpv_set_property_string(mpvHandle, "speed",
                    (currentSpeed / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            UpdateVolumeDisplay();
            // A text book takes its speed / volume / pitch back from the voice the
            // dialog left it on, not from the fields the player had before.
            if (currentBook.IsTextBook) ApplyTtsSettings();
            UpdateSpeedDisplay();
        }

        /// <summary>Builds the book's sound-processing filter chain and applies
        /// it to mpv's audio output (empty string = no filters). Safe to call
        /// any time; a no-op with no engine. Used on book load, for the live
        /// preview while the Properties dialog is open, and after it closes.</summary>
        private void ApplySoundProcessing(SoundSettings s, bool bypass)
        {
            if (mpvHandle == IntPtr.Zero) return;
            string af = SoundSettings.BuildAf(s, bypass);
            mpv_set_property_string(mpvHandle, "af", af ?? "");
        }

        private void BtnGoTo_Click(object sender, EventArgs e)
        {
            if (currentBook == null)
            {
                tones.Play(300, 150);
                return;
            }

            // Text book → navigate by headings (structured) or a low beep (flat,
            // nothing to jump to).
            if (currentBook.IsTextBook)
            {
                if (currentBook.TextHeadings.Count > 0) TextGoTo();
                else tones.Play(300, 150);
                return;
            }

            // Single-file audio (and the M4B single-file fallback) have no parts
            // or chapters to list — Go To is inactive (low beep).
            if (GetPlayerType() == PlayerType.SingleAudio)
            {
                tones.Play(300, 150);
                return;
            }

            if (currentBook.Chapters.Count == 0)
            {
                // No playable content — a short low beep as audible feedback.
                tones.Play(300, 150);
                return;
            }

            // DAISY: navigate by the book's headings (indented by depth). Plain
            // audio: navigate by the book's parts (files). Either way the list
            // maps 1:1 to virtual-timeline target positions.
            string[] names;
            double[] targets;
            int preselect;
            bool daisyNav = currentBook.IsDaisy && currentBook.DaisyHeadings.Count > 0;
            bool m4bNav = GetPlayerType() == PlayerType.M4b;

            if (daisyNav)
            {
                var hs = currentBook.DaisyHeadings;
                names = new string[hs.Count];
                targets = new double[hs.Count];
                for (int i = 0; i < hs.Count; i++)
                {
                    names[i] = new string(' ', 2 * Math.Max(0, hs[i].Level - 1)) + hs[i].Label;
                    targets[i] = hs[i].Position;
                }
                double pos = GetVirtualPosition();
                preselect = 0;
                for (int i = hs.Count - 1; i >= 0; i--)
                    if (hs[i].Position <= pos + 0.05) { preselect = i; break; }
            }
            else if (m4bNav)
            {
                var cs = currentBook.M4bChapters;
                names = new string[cs.Count];
                targets = new double[cs.Count];
                for (int i = 0; i < cs.Count; i++) { names[i] = cs[i].Title; targets[i] = cs[i].Position; }
                double pos = GetVirtualPosition();
                preselect = 0;
                for (int i = cs.Count - 1; i >= 0; i--)
                    if (cs[i].Position <= pos + 0.05) { preselect = i; break; }
            }
            else
            {
                names = new string[currentBook.Chapters.Count];
                for (int i = 0; i < names.Length; i++)
                    names[i] = System.IO.Path.GetFileNameWithoutExtension(currentBook.Chapters[i].FileName);
                targets = currentBook.Offsets.ToArray();
                preselect = currentPlaylistIndex;
            }

            // Both DAISY headings and plain-audio parts list as bare names now
            // (no "N/M —" prefix — the name/file already self-numbers).
            using (GoToForm dlg = new GoToForm(names, preselect, appSettings.GoToAutoPlay, true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK &&
                    dlg.SelectedPartIndex >= 0 && dlg.SelectedPartIndex < targets.Length)
                {
                    // Jump to the selected target. Default: playback state is
                    // preserved (paused stays paused, playing keeps playing);
                    // with the auto-play checkbox, playback starts after the
                    // jump (in the onComplete callback, after the delayed
                    // cross-file seek, to avoid an audible blip).
                    bool autoPlay = dlg.AutoPlayChecked;
                    appSettings.SetGoToAutoPlay(autoPlay);
                    SeekToVirtualPosition(targets[dlg.SelectedPartIndex], () =>
                    {
                        if (autoPlay && !isPlaying)
                        {
                            mpv_set_property_string(mpvHandle, "pause", "no");
                            SetPlayPauseState(true);
                        }
                    });
                }
            }
        }

        private void BtnSetBookmark_Click(object sender, EventArgs e)
        {
            // Same "no go" feedback as Go To / Sleep Timer with nothing loaded.
            if (currentBook == null)
            {
                tones.Play(300, 150);
                return;
            }

            // Stored in the book's own unit — seconds for audio, the character
            // offset for a text book, which is what its position is.
            currentBook.AddBookmark(BookPosition());
            RebuildSeekSteps();

            // Ascending series of five short beeps (~1 second total) — a
            // bit more attention-grabbing than the plain "no go" beep, since
            // this confirms a successful action rather than a blocked one.
            tones.Play(new[] { (500, 200), (650, 200), (800, 200), (950, 200), (1100, 200) });

            // Deliberately no position/percent details here — TMI for a
            // one-key command; the Manage Bookmarks list is where that
            // detail belongs.
            AnnounceToScreenReader(lblAnnounceInfo, Localization.T("Bookmark.Announce.Set"));
        }

        private void BtnManageBookmarks_Click(object sender, EventArgs e)
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                tones.Play(300, 150);
                return;
            }

            // Opening the dialog pauses playback (if running), same coupling
            // as the Sleep Timer dialog — a programmatic pause, so it does not
            // touch an active Sleep Timer. Works for both engines.
            bool wasPlaying = isPlaying;
            if (wasPlaying) PausePlaybackQuietly();

            using (ManageBookmarksForm dlg = new ManageBookmarksForm(currentBook.Bookmarks, FormatBookmarkPosition))
            {
                DialogResult result = dlg.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    currentBook.SetBookmarks(dlg.ResultBookmarks);
                    RebuildSeekSteps();

                    if (dlg.PlayIndex >= 0)
                    {
                        // OK confirmed with a bookmark selected: jump there
                        // and make sure playback continues from there.
                        double pos = dlg.ResultBookmarks[dlg.PlayIndex];
                        SeekToBookPosition(pos, () => { if (!isPlaying) ResumePlaybackQuietly(); });
                        return;
                    }
                }

                // Plain OK (edits only, no jump) or Cancel: restore exactly
                // the playback state from before the dialog opened.
                if (wasPlaying) ResumePlaybackQuietly();
            }
        }

        // ──────────────────────────────────────────────
        // Loading a book (from the library or at startup)
        // ──────────────────────────────────────────────
        // ──────────────────────────────────────────────
        // Text books (TTS)
        // ──────────────────────────────────────────────
        private void EnsureTts()
        {
            if (tts != null) return;
            tts = new TtsReader();
            // SAPI raises SpeakCompleted on a background thread, so marshal UI
            // updates back to the form.
            tts.PositionChanged += () =>
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)UpdateTextPositionDisplay); } catch { }
            };
            tts.Finished += () =>
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)(() => { SetPlayPauseState(false); FinishCurrentBook(); })); } catch { }
            };
            // Start on the output device chosen in Settings → Device.
            tts.SetAudioDevice(appSettings.AudioDevice);
        }

        private void LoadTextBookPlayback(bool autoPlay)
        {
            EnsureTts();
            // Silence any mpv audio left from a previous (audio) book.
            if (mpvHandle != IntPtr.Zero) mpv_set_property_string(mpvHandle, "pause", "yes");
            currentFile = null;
            currentPlaylistIndex = 0;
            isLoadingBook = false;

            // A book imported before cleaning moved to import time is brought up to
            // date once, here: content.txt is rewritten cleaned and every stored
            // character offset (headings, pages, the reading position, bookmarks)
            // moves with it. After that the reader takes the file as it stands.
            currentBook.CleanTextFileOnce();
            string bookText = TtsReader.ReadFile(currentBook.TextFilePath);
            tts.LoadText(bookText, currentBook.TextCleaned);
            // A book imported before NBR could tell languages apart gets told now,
            // once, so it too is read by a voice that speaks it.
            if (string.IsNullOrEmpty(currentBook.TextLanguage))
            {
                currentBook.TextLanguage = LanguageDetector.Resolve("", bookText);
                if (!string.IsNullOrEmpty(currentBook.TextLanguage))
                    try { currentBook.Save(); } catch { }
            }
            // Voice, speed, volume and pitch all come from ApplyTtsSettings: they
            // belong to the voice this book is read with, not to the player's
            // previous state.
            ApplyTtsSettings();
            tts.SeekToChar(currentBook.TextPosition);

            // Cache the character count for the reading-time estimate.
            currentBook.TextChars = tts.TotalChars;

            UpdateTitleBar();
            UpdateTextPositionDisplay();
            appSettings.SetLastOpenedBook(currentBook.FolderPath);

            if (autoPlay)
            {
                SetPlayPauseState(true);
                // Defer the first Play one tick so any pending SAPI cancel from
                // loading has settled — otherwise it swallows this utterance and
                // playback silently doesn't start (needed two Spaces to begin).
                BeginInvoke((Action)(() =>
                {
                    if (tts != null && currentBook != null && currentBook.IsTextBook && isPlaying)
                        tts.Play();
                }));
            }
            else SetPlayPauseState(false);
        }

        /// <summary>The voice this book is actually read with: its own if it has
        /// one, otherwise the Settings default. The name the reader resolved wins
        /// when there is one, so what we remember is filed under the voice that
        /// really spoke.</summary>
        private string EffectiveTextVoice()
        {
            string configured = currentBook != null && !string.IsNullOrEmpty(currentBook.TextVoice)
                ? currentBook.TextVoice : DefaultVoiceForBook();
            string live = tts != null ? tts.CurrentVoice : "";
            return !string.IsNullOrEmpty(live) ? live : configured;
        }

        /// <summary>How a voice should sound here: what this book was last read
        /// with using that voice, else how the voice is set up in Settings, else
        /// the neutral default — never the settings of the voice used before it.</summary>
        private VoicePrefs ResolveVoicePrefs(string voice)
        {
            VoicePrefs global = appSettings.PrefsFor(voice);
            return currentBook != null ? currentBook.TextVoicePrefs.Get(voice, global) : global;
        }

        /// <summary>Files the speed / volume / pitch now in use under the voice in
        /// use, so switching away and back restores them.</summary>
        private void RememberCurrentVoicePrefs()
        {
            if (currentBook == null || !currentBook.IsTextBook) return;
            string voice = EffectiveTextVoice();
            if (string.IsNullOrEmpty(voice)) return;
            currentBook.TextVoicePrefs.Set(voice, new VoicePrefs(currentWpm, currentVolume, currentTextPitch));
            currentBook.TextWpm = currentWpm;
            currentBook.TextVolume = currentVolume;
            currentBook.TextPitch = currentTextPitch;
        }

        /// <summary>The voice a book with no chosen voice of its own should be read
        /// with: the Settings default when it speaks the book's language, otherwise
        /// the best voice that does. A Croatian book must not be read out in
        /// English merely because that is what Settings happens to name.</summary>
        private string DefaultVoiceForBook()
        {
            string settingsVoice = appSettings.TtsVoice ?? "";
            string lang = currentBook != null ? currentBook.TextLanguage : "";
            if (tts == null || string.IsNullOrEmpty(lang)) return settingsVoice;

            List<(string Name, string Vendor, string Language)> voices;
            try { voices = tts.GetVoiceInfos(); }
            catch { return settingsVoice; }

            // Already right? Keep it — the user's default wins whenever it fits.
            foreach (var v in voices)
                if (string.Equals(v.Name, settingsVoice, StringComparison.OrdinalIgnoreCase)
                    && LanguageDetector.SameLanguage(v.Language, lang))
                    return settingsVoice;

            // Otherwise the first voice that speaks the language. The catalog is
            // ordered in-process first, so a 64-bit voice wins over the satellite.
            foreach (var v in voices)
                if (LanguageDetector.SameLanguage(v.Language, lang))
                    return v.Name;

            return settingsVoice;   // nothing installed speaks it
        }

        private void ApplyTtsSettings()
        {
            if (tts == null) return;
            // A book can carry its own voice (its Properties); where it doesn't, the
            // Settings default applies — unless the book is in a language that
            // default doesn't speak. The speed/volume/pitch then follow THAT voice —
            // remembered per voice, so a change of voice or engine never drags the
            // previous one's numbers along.
            string voice = currentBook != null && !string.IsNullOrEmpty(currentBook.TextVoice)
                ? currentBook.TextVoice : DefaultVoiceForBook();
            VoicePrefs p = ResolveVoicePrefs(voice);
            currentWpm = p.Wpm;
            currentVolume = p.Volume;
            currentTextPitch = p.Pitch;

            tts.SetVoice(voice);
            // Whatever the user has put in their own dictionary for this voice and
            // this language — nothing at all unless they wrote it themselves.
            tts.Dictionaries = SpeechDictionaries.Active(
                !string.IsNullOrEmpty(tts.CurrentVoice) ? tts.CurrentVoice : voice,
                currentBook != null ? currentBook.TextLanguage : "");
            tts.SetPitch(currentTextPitch * 5); // -10..10 → -50..50 %
            tts.SetVolume(currentVolume);
            tts.SetRate(TtsReader.WpmToRate(currentWpm));

            RememberCurrentVoicePrefs();
            UpdateVolumeDisplay();
            UpdateSpeedDisplay();
        }

        /// <summary>Refreshes the Volume field and its spoken name from
        /// <c>currentVolume</c> (the same text ChangeVolume shows).</summary>
        private void UpdateVolumeDisplay()
        {
            string text = Localization.T("Player.Volume.Text", currentVolume);
            lblVolume.Text = text;
            if (!tbVolume.Focused)
            {
                tbVolume.Text = text;
                tbVolume.AccessibleName = Localization.T("Player.Volume.Accessible", currentVolume);
            }
        }

        /// <summary>Refreshes the speed field: "N WPM" for a text book, "N.Nx"
        /// for audio.</summary>
        private void UpdateSpeedDisplay()
        {
            bool text = currentBook != null && currentBook.IsTextBook;
            string display = text
                ? Localization.T("Player.Speed.Wpm", currentWpm)
                : Localization.T("Player.Speed.Text", (currentSpeed / 100.0).ToString("0.0"));
            lblSpeed.Text = display;
            if (!tbSpeed.Focused)
            {
                tbSpeed.Text = display;
                tbSpeed.AccessibleName = text
                    ? Localization.T("Player.Speed.WpmAccessible", currentWpm)
                    : Localization.T("Player.Speed.Accessible", (currentSpeed / 100.0).ToString("0.0"));
            }
        }

        // Percentage read of the current text book, one decimal so it still
        // moves on a long book where the integer percent sits at 0 for a while.
        private string TextPercentString()
        {
            if (tts == null || tts.TotalChars <= 0) return "0.0";
            return (100.0 * tts.CharPosition / tts.TotalChars).ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // Estimated reading time (seconds) for a character offset at the current
        // reading speed (nominal words-per-minute → characters per minute).
        private double TextSeconds(int chars)
        {
            int cpm = TtsReader.CharsPerMinute(currentWpm);
            return cpm > 0 ? chars * 60.0 / cpm : 0;
        }

        private void UpdateTextPositionDisplay()
        {
            if (tts == null || currentBook == null || !currentBook.IsTextBook) return;
            int percent = tts.TotalChars > 0 ? (int)(100.0 * tts.CharPosition / tts.TotalChars) : 0;
            string posText = Localization.T("Player.Position.Text",
                FormatTime(TextSeconds(tts.CharPosition)), FormatTime(TextSeconds(tts.TotalChars)));
            tbProgress.Text = posText;
            tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", percent);
            lblProgress.Text = posText;

            // Live title bar + info box (same rule as audio: caption always,
            // info box only while unfocused) so text progress advances visibly.
            UpdateTitleBar();
            if (this.ActiveControl != tbInfo)
                tbInfo.Text = BuildCurrentInfoText();
        }

        private void LoadBook(BookData book, bool autoPlay)
        {
            // Changing the book ends the previous listening session — an
            // active sleep timer is cancelled, with the same announcement
            // as a manual pause. (At startup no timer can be active, so
            // this only ever fires on a library pick.)
            if (sleepTimerActive)
                CancelSleepTimer(true);

            // Stop any TTS reading from a previous text book.
            if (tts != null) tts.Stop();

            currentBook = book;
            RebuildSeekSteps();
            // Restore the book's saved seek step by kind (and heading depth).
            // A never-chosen book (SeekStep < 0) or one whose saved step no
            // longer exists (e.g. Bookmark, but the book now has none) falls
            // back to the first — and largest — step (H1 / Part).
            int savedIdx = -1;
            if (currentBook.SeekStep >= 0)
            {
                SeekStep saved = DecodeSeekStep(currentBook.SeekStep);
                savedIdx = seekSteps.FindIndex(s => s.Kind == saved.Kind && s.Level == saved.Level);
            }
            cmbSeek.SelectedIndex = savedIdx >= 0 ? savedIdx : 0;

            currentVolume = Math.Min(100, Math.Max(0, currentBook.Volume));
            currentSpeed = Math.Min(300, Math.Max(50, currentBook.Speed));

            string volText = Localization.T("Player.Volume.Text", currentVolume);
            lblVolume.Text = volText;
            tbVolume.Text = volText;
            tbVolume.AccessibleName = Localization.T("Player.Volume.Accessible", currentVolume);

            string speedStr = (currentSpeed / 100.0).ToString("0.0");
            string spdText = Localization.T("Player.Speed.Text", speedStr);
            lblSpeed.Text = spdText;
            tbSpeed.Text = spdText;
            tbSpeed.AccessibleName = Localization.T("Player.Speed.Accessible", speedStr);

            mpv_set_property_string(mpvHandle, "volume", currentVolume.ToString());
            double speed = currentSpeed / 100.0;
            mpv_set_property_string(mpvHandle, "speed",
                speed.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Text book → read it with TTS instead of building an mpv playlist.
            if (currentBook.IsTextBook)
            {
                LoadTextBookPlayback(autoPlay);
                return;
            }

            string[] audioExts = { ".mp3", ".ogg", ".flac", ".m4a", ".m4b", ".wav", ".opus", ".aac", ".wma" };
            var playlist = new List<string>();

            // Play in the book's chapter order, not a fresh alphabetical sort:
            // for plain audiobooks the two match, but DAISY audio is ordered by
            // its navigation (BuildChaptersFromDaisy), which is not always the
            // filename order — and playback must match the virtual timeline the
            // headings/pages are positioned against.
            if (currentBook.Chapters.Count > 0)
            {
                foreach (var ch in currentBook.Chapters)
                {
                    string p = System.IO.Path.Combine(currentBook.FolderPath, ch.FileName);
                    if (System.IO.File.Exists(p)) playlist.Add(p);
                }
            }
            if (playlist.Count == 0)
            {
                string[] allFiles = System.IO.Directory.GetFiles(currentBook.FolderPath);
                Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);
                foreach (string f in allFiles)
                {
                    string ext = System.IO.Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(audioExts, ext) >= 0)
                        playlist.Add(f);
                }
            }

            if (playlist.Count == 0) return;

            currentFile = playlist[0];

            // Build chapters if not already in the ini
            if (currentBook.Chapters.Count == 0)
                currentBook.BuildChaptersFromFolder(playlist.ToArray());

            currentPlaylistIndex = 0;
            isLoadingBook = true;
            // Always start paused — prevents an audible "plays then jumps"
            // while we look for the remembered position.
            LoadPlaylist(playlist.ToArray(), false);
            // Apply this book's saved sound processing (no-op when it's off).
            ApplySoundProcessing(currentBook.Sound, false);

            double resumeSeconds = ParseTimeToSeconds(currentBook.LastPosition);
            System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();
            resumeTimer.Interval = 600;
            resumeTimer.Tick += (s, ev) =>
            {
                resumeTimer.Stop();
                resumeTimer.Dispose();

                Action finishLoad = () =>
                {
                    isLoadingBook = false;
                    if (autoPlay)
                    {
                        mpv_set_property_string(mpvHandle, "pause", "no");
                        SetPlayPauseState(true);
                    }
                    else
                    {
                        SetPlayPauseState(false);
                    }
                };

                if (resumeSeconds > 0.5)
                    SeekToVirtualPosition(resumeSeconds, finishLoad);
                else
                    finishLoad();
            };
            resumeTimer.Start();

            appSettings.SetLastOpenedBook(currentBook.FolderPath);
            UpdateTitleBar();
        }

        // ──────────────────────────────────────────────
        // Loading a playlist
        // ──────────────────────────────────────────────
        private string BuildFileFilter()
        {
            return
                Localization.T("Filter.Audiobooks") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc|" +
                Localization.T("Filter.TextBooks") + "|*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc;*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra;*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllFiles") + "|*.*";
        }

        private void OpenFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = BuildFileFilter();
                ofd.FilterIndex = 4; // default to "All supported files"
                ofd.Title = Localization.T("Player.OpenFile.Title");
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string ext = System.IO.Path.GetExtension(ofd.FileName).ToLower();
                    if (LibraryScanner.IsExtractableArchive(System.IO.Path.GetFileName(ofd.FileName)))
                    {
                        OpenArchiveFile(ofd.FileName);
                        return;
                    }

                    // Same rule as a library pick: a new file ends the
                    // previous listening session, so an active timer goes.
                    if (sleepTimerActive)
                        CancelSleepTimer(true);

                    currentFile = ofd.FileName;
                    currentBook = null;
                    RebuildSeekSteps();
                    currentPlaylistIndex = 0;
                    LoadPlaylist(new string[] { ofd.FileName });
                    UpdateTitleBar();
                }
            }
        }

        /// <summary>Ctrl+O given an archive: extracting it in place and
        /// trying to "play" it makes no sense, so instead it gets extracted
        /// straight into its own permanent library folder (named from the
        /// archive's file name — no temp staging) and loaded like any other
        /// book. The source archive is left untouched, since it was picked
        /// from an arbitrary external location via the file dialog.
        /// LoadBook already cancels an active Sleep Timer, same as any other
        /// change of book.</summary>
        private void OpenArchiveFile(string archivePath)
        {
            string destFolder = null;
            bool createdFolder = false;
            try
            {
                string sourceName = System.IO.Path.GetFileName(archivePath);
                destFolder = System.IO.Path.Combine(appSettings.LibraryPath,
                    LibraryScanner.BaseArchiveName(archivePath));

                createdFolder = !System.IO.Directory.Exists(destFolder);
                if (createdFolder)
                    System.IO.Directory.CreateDirectory(destFolder);

                // Multi-volume sets are gathered from the first part; encrypted
                // archives prompt for a password (kept in memory only).
                int pwAttempts = 0;
                LibraryScanner.ExtractArchive(archivePath, destFolder,
                    () => ArchivePasswordPrompt.Show(this, sourceName, pwAttempts++ > 0));
                // Name the book after the folder closest to the files (the
                // wrapper), not the archive file itself.
                destFolder = LibraryScanner.ResolveBookFolder(destFolder, appSettings.LibraryPath);

                BookData book = new BookData(destFolder);

                DaisyBook daisy = DaisyParser.TryParse(destFolder);
                if (daisy != null && DaisyTextExtractor.IsTextDaisy(daisy))
                {
                    DaisyTextExtractor.SetupTextBook(book, destFolder, daisy, appSettings.UseMetadata);
                }
                else if (daisy != null)
                {
                    LibraryScanner.FlattenDaisyToRoot(destFolder, daisy.ContentRoot);
                    book.BuildChaptersFromDaisy(DaisyParser.TryParse(destFolder));
                }
                else
                {
                    List<string> audioFiles = new List<string>();
                    foreach (string f in System.IO.Directory.GetFiles(destFolder))
                    {
                        if (Array.IndexOf(LibraryScanner.AudioExtensions, System.IO.Path.GetExtension(f).ToLower()) >= 0)
                            audioFiles.Add(f);
                    }
                    if (audioFiles.Count > 0)
                    {
                        audioFiles.Sort(StringComparer.OrdinalIgnoreCase);
                        book.BuildChaptersFromFolder(audioFiles.ToArray());
                    }
                    else
                    {
                        book.Format = LibraryScanner.DetectFormat(destFolder);
                    }
                }
                book.DateAdded = DateTime.Now;
                book.Save();

                LoadBook(book, true);
            }
            catch (OperationCanceledException)
            {
                // User cancelled the archive password prompt — undo the empty
                // folder, no error dialog.
                if (createdFolder) TryDeleteFolder(destFolder);
            }
            catch (Exception ex)
            {
                if (createdFolder) TryDeleteFolder(destFolder);
                MessageBox.Show(Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
            }
        }

        private static void TryDeleteFolder(string path)
        {
            try
            {
                if (path != null && System.IO.Directory.Exists(path))
                    System.IO.Directory.Delete(path, true);
            }
            catch { }
        }

        private void LoadPlaylist(string[] files, bool startPlaying = true)
        {
            SetPlayPauseState(startPlaying);

            MpvCommandUtf8("loadfile", files[0], "replace");
            for (int i = 1; i < files.Length; i++)
                MpvCommandUtf8("loadfile", files[i], "append");

            mpv_set_property_string(mpvHandle, "pause", startPlaying ? "no" : "yes");

            currentProgress = 0;
            tbProgress.Text = Localization.T("Player.Position.Text", FormatTime(0), FormatTime(0));
            tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", 0);

            eventTimer.Start();
            progressTimer.Start();
        }

        // ──────────────────────────────────────────────
        // MPV helpers
        // ──────────────────────────────────────────────
        private static IntPtr StringToUtf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        private void MpvCommandUtf8(params string[] args)
        {
            if (mpvHandle == IntPtr.Zero) return;
            IntPtr[] ptrs = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = StringToUtf8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            GCHandle handle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try { mpv_command(mpvHandle, handle.AddrOfPinnedObject()); }
            finally
            {
                handle.Free();
                foreach (var ptr in ptrs)
                    if (ptr != IntPtr.Zero)
                        Marshal.FreeHGlobal(ptr);
            }
        }

        private void MpvCommand(params string[] args)
        {
            if (mpvHandle == IntPtr.Zero) return;
            IntPtr[] ptrs = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToHGlobalAnsi(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            GCHandle handle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try { mpv_command(mpvHandle, handle.AddrOfPinnedObject()); }
            finally
            {
                handle.Free();
                foreach (var ptr in ptrs)
                    if (ptr != IntPtr.Zero)
                        Marshal.FreeHGlobal(ptr);
            }
        }

        private void InitializeMpv()
        {
            try
            {
                mpvHandle = mpv_create();
                if (mpvHandle == IntPtr.Zero) return;
                mpv_initialize(mpvHandle);
                // Audio-only player: never open a video window. "audio-display"
                // suppresses attached-picture cover art (MP3/FLAC), but an M4B/MP4
                // cover is a real video track, so also disable video-track
                // selection outright — otherwise mpv pops a window showing the
                // cover image.
                mpv_set_property_string(mpvHandle, "audio-display", "no");
                mpv_set_property_string(mpvHandle, "vid", "no");
                // Output device chosen in Settings → Device (empty = mpv "auto").
                if (appSettings != null && !string.IsNullOrEmpty(appSettings.AudioDevice))
                    mpv_set_property_string(mpvHandle, "audio-device", appSettings.AudioDevice);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // The media keys can only be claimed once there is a window to
            // deliver WM_HOTKEY to.
            ApplyMediaKeySettings();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Give the media keys back to the system / other players.
            foreach (int id in new[] { HotkeyPlayPause, HotkeyNext, HotkeyPrev, HotkeyStop })
                try { UnregisterHotKey(this.Handle, id); } catch { }
            SaveCurrentBookProgress();
            eventTimer?.Stop();
            progressTimer?.Stop();
            sleepTimer?.Stop();
            if (mpvHandle != IntPtr.Zero)
                mpv_terminate_destroy(mpvHandle);
            try { tones.Dispose(); } catch { }
            MpvDuration.Shutdown();   // release the duration-probe context too
            LibLouis.Shutdown();      // release liblouis' table cache
            base.OnFormClosing(e);
        }
    }
}
