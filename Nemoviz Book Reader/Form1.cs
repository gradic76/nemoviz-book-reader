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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Multimedia keys (WM_APPCOMMAND)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Handled locally (WM_APPCOMMAND): the keys work while any NBR window
        // control has focus. Settings â†’ General switches them off, or claims them
        // system-wide (RegisterHotKey â†’ WM_HOTKEY) so they work from anywhere.
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
        // sounds â€” on headphones or a second card those are different rooms.
        private readonly SignalTones tones = new SignalTones();

        private IntPtr mpvHandle = IntPtr.Zero;
        private bool isPlaying = false;
        private string currentFile = null;
        private BookData currentBook = null;
        // Text-book playback engine (TTS). Created lazily on the first text book.
        private TtsReader tts = null;
        /// <summary>The words the reading window shows, kept apart from
        /// <see cref="tts"/> on purpose.
        ///
        /// <para>The surface used to read its text straight off the TTS reader,
        /// which tied the whole visual and braille output to speech. That left a
        /// HYBRID with nothing: its narration is audio, so no TtsReader is ever
        /// made for it, and both the automatic open and F9 fell through â€” the
        /// switches sat there in Properties saving a setting that could not
        /// happen. It was worse than dead, too: <c>tts</c> outlives a book
        /// change, so opening a text book and then a hybrid left the reader
        /// holding the PREVIOUS book, and F9 would have shown that one's text
        /// under this one's title.</para>
        ///
        /// <para>Cleared on every book load, so a stale one cannot survive.</para></summary>
        private string readingText = null;
        /// <summary>Keeps the output device awake while anything is playing â€”
        /// see Â§10f and <see cref="AudioKeepAlive"/>. Not tied to text books:
        /// an audio book pauses too, and the same endpoint sleeps in the
        /// same way.</summary>
        private AudioKeepAlive keepAlive;
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
        // empty library, deleted folder, or the last book was finished) â€”
        // the app then starts in the Library window instead of the player.
        private bool openLibraryOnStartup = false;

        // Guards against opening a second (nested) Library window, e.g. when
        // a book finishes while the library is already open.
        private bool isLibraryOpen = false;

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Sleep timer state
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The timer is COUPLED TO PLAYBACK â€” it exists because someone is
        // listening and plans to fall asleep, it is not a standalone
        // shutdown scheduler. The rules:
        //   * A timer can only be set with something loaded in the player;
        //     otherwise the button gives a short low beep (same as Ctrl+G).
        //   * Starting a timer starts playback if it isn't running.
        //   * A MANUAL pause (Space, X, on-screen button, media keys)
        //     cancels the timer, with an announcement. Programmatic pauses
        //     (cross-file seeks, book loading, the timer's own expiry
        //     action) do NOT â€” they never route through BtnPlayPause_Click,
        //     which is the only place the cancel hook lives.
        //   * Changing the book (library pick or Ctrl+O) cancels the timer,
        //     with the same announcement.
        //   * Seeking, volume, speed, part navigation and Go To don't touch
        //     the timer â€” adjusting things while drifting off is expected.
        //   * If the book ends by itself before the deadline, the chosen
        //     action fires immediately (see FinishCurrentBook): for Stop
        //     the "stop playback" part already happened naturally, so the
        //     timer is just quietly dropped; for close/shutdown the action
        //     runs right away, earlier than the deadline.
        // Audible signals: a series of three beeps at -5 min (skipped for
        // timers of 5 minutes or less), then a smooth volume FADEOUT over
        // the last 45 seconds. The fade only touches the mpv volume â€” the
        // user's set volume (currentVolume, the Volume field, Book.ini)
        // stays untouched and is restored when the timer ends or is
        // cancelled.
        // While active, the countdown itself is wall-clock (a DateTime
        // deadline, not accumulated ticks), so a busy UI thread can't make
        // it drift; playback speed has no effect. Nothing is persisted â€”
        // a timer is a one-shot, session-only thing.
        private System.Windows.Forms.Timer sleepTimer;
        private DateTime sleepDeadline;
        private SleepTimerAction sleepAction;
        private bool sleepTimerActive = false;
        // True once the -5 min warning series has fired (or when the timer
        // was started with 5 minutes or less â€” no point beeping instantly).
        private bool sleepWarned5Min = false;
        // Seconds before the deadline at which the fadeout starts.
        private const int SleepFadeSeconds = 45;

        // UI controls â€” 3Ă—4 grid, columns A (160) / B (320) / C (160)
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
        // Bookmark only while the book has â‰Ą1 bookmark. DAISY headings follow
        // the standard talking-book model â€” one step per heading depth present
        // ("Heading 1", "Heading 2", â€¦), where level N stops at every heading
        // of depth â‰¤ N. This list runs parallel to cmbSeek.Items (one entry
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

        // Tooltips â€” mouse-hover hints with the keyboard shortcuts
        private ToolTip toolTip;

        // Off-screen labels for screen reader announcements
        private Label lblAnnounceVolume;
        private Label lblAnnounceProgress;
        private Label lblAnnounceSpeed;
        private Label lblAnnounceInfo;
        // F8 twice in quick succession walks into the info box; see ProcessCmdKey.
        private DateTime lastInfoKey = DateTime.MinValue;
        private Control infoBoxCameFrom;

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The new look's way in
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // NewPlayerSkin lays the redesigned player out and paints it, but the
        // controls and the commands stay here where they have always been â€” the
        // skin only rearranges and repaints what BuildUI already made, so roles,
        // names, tab order and every handler are untouched. None of this runs
        // under the classic theme.
        internal PlayerParts SkinParts
        {
            get
            {
                return new PlayerParts
                {
                    Top = panelTop,
                    Bottom = panelBottom,
                    Info = tbInfo,
                    VolumeField = tbVolume,
                    SpeedField = tbSpeed,
                    ProgressField = tbProgress,
                    Seek = cmbSeek,
                    SeekLabel = lblSeek,
                    VolumeLabel = lblVolume,
                    SpeedLabel = lblSpeed,
                    ProgressLabel = lblProgress,
                    Left = new[] { btnLibrary, btnSettings, btnTimer, btnHelp },
                    Right = new[] { btnProperties, btnGoTo, btnSetBookmark, btnManageBookmarks },
                    Back = btnBack,
                    PlayPause = btnPlayPause,
                    Forward = btnForward
                };
            }
        }

        /// <summary>Progress through the book, 0â€“1000, for the skin's bar.</summary>
        internal int SkinProgress { get { return currentProgress; } }

        /// <summary>True while something is actually playing â€” the skin's seconds
        /// marker steps only then, which is what makes it a state indicator.</summary>
        internal bool SkinIsPlaying { get { return isPlaying; } }

        /// <summary>True while a sleep timer is counting down â€” the panel's lamp
        /// breathes instead of burning steady, which is the only place that state
        /// is visible to someone who is not using a screen reader.</summary>
        internal bool SkinSleepActive { get { return sleepTimerActive; } }

        internal void SkinVolume(int delta) { ChangeVolume(delta); }
        internal void SkinSpeed(int delta) { ChangeSpeed(delta); }
        internal void SkinArrowSeek(int dir) { ArrowSeek(dir); }

        /// <summary>Speed in whatever unit this book counts it in â€” words a minute
        /// for a text book, hundredths of a multiplier for audio. Both step by 5,
        /// so the skin's knob can work in the same numbers the keyboard does and
        /// the two can never land between steps.</summary>
        internal int SkinSpeedRaw
        {
            get { return (currentBook != null && currentBook.IsTextBook) ? currentWpm : currentSpeed; }
        }

        internal bool SkinTextBook { get { return currentBook != null && currentBook.IsTextBook; } }

        /// <summary>Where the progress blade was dropped, 0â€“1 of the whole book.
        /// Called once on mouse-up, never during the drag: seeking on every pixel
        /// would hammer mpv and the speech engine for a gesture the user has not
        /// finished making.</summary>
        internal void SkinSeekFraction(double f)
        {
            if (currentBook == null) return;
            double total = currentBook.IsTextBook
                ? (tts != null ? tts.TotalChars : 0)
                : (currentBook.TotalDuration > 0 ? currentBook.TotalDuration : 0);
            if (total <= 0) return;
            SeekToBookPosition(Math.Max(0.0, Math.Min(1.0, f)) * total);
        }

        public Form1()
        {
            InitializeComponent();
            appSettings = new AppSettings();
            UiTheme.Select(appSettings.UiTheme);   // before anything builds itself
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Startup flow
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        /// <summary>
        /// Decides where the app starts: the player with the last-read book
        /// resumed (normal case), or the Library window â€” on first run, when
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Multimedia keys â€” WM_APPCOMMAND
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // WM_APPCOMMAND bubbles up from the focused child control to the
        // form via DefWindowProc, so handling it here covers the whole
        // window. With no book loaded â€” or with the keys switched off in
        // Settings â€” the message is passed through to the system
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
        /// Settings. Safe to call at any time and as often as you like â€” it always
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Screen reader announcement
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Transient values (volume, speed, timer, info-on-demand, bookmark
        // set, seek step...) are announced through a UIA *notification event*
        // raised on the player window itself. This speaks the text WITHOUT
        // moving focus, which fixes the two problems the old off-screen-label
        // approach had:
        //   * it briefly stole focus to a hidden label and restored it 150 ms
        //     later â€” rapid key repeats overlapped those focus shuffles and
        //     choked the reader, and
        //   * NVDA (unlike JAWS) largely ignored the programmatic focus to an
        //     off-screen label, so pressing volume/speed keys while focus was
        //     on a button produced no feedback at all.
        // NotificationProcessing.MostRecent tells the reader to drop pending
        // older notifications and speak only the latest, so holding/â€‹repeating
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
                // systems the export is missing and the first call throws â€”
                // stop trying so we don't throw on every keystroke.
                uiaNotifyUnavailable = true;
            }
        }

        [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRawElementProviderSimple
        {
            // Never called from managed code â€” we only obtain the provider
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Seek step (from the seek dropdown)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Navigation is layered in four levels:
        //   1. Left/Right arrows  â€” plain 5 s seek, like any other player.
        //   2. Ctrl+1..9          â€” percentage jumps across the whole book.
        //   3. Shift+Left / Shift+Right, media Next/Prev, and the on-screen
        //      Back/Forward buttons â€” jump by the step selected in the seek
        //      dropdown (time steps / whole Part / Bookmark). Shift+Up/Down
        //      change which step is selected.
        //   4. Go To... (Ctrl+G)  â€” pick a named target from a list; for
        //      plain audio that's the book's parts.
        /// <summary>The step currently selected in the seek dropdown.</summary>
        private SeekStep CurrentSeekStep()
        {
            int i = cmbSeek.SelectedIndex;
            return (i >= 0 && i < seekSteps.Count) ? seekSteps[i] : new SeekStep(SeekStepKind.Sec15);
        }

        // Persist a step to Book.ini as a single int: heading depth L â†’ 100+L,
        // any other kind â†’ its enum ordinal. Kept compact so [Settings] SeekStep
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // One rule for every seek, in every kind of book: a step that CAN'T move
        // says so with the "no go" beep, a step that moves is silent. Each seek
        // helper below reports whether it went anywhere and beeps for nobody; the
        // two dispatchers here own the sound. That way "there is nothing further
        // that way" feels the same whether the step is Heading 1 or 15 seconds,
        // and whether the book is audio, text or a hybrid.
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void SeekStepForward()
        {
            SeekStep step = CurrentSeekStep();
            bool moved;
            if (currentBook != null && currentBook.IsTextBook) moved = TextSeek(step, +1);
            else switch (step.Kind)
            {
                case SeekStepKind.Part: moved = PartForward(); break;
                case SeekStepKind.Heading: moved = StructForward(HeadingPositions(step.Level)); break;
                case SeekStepKind.Page: moved = StructForward(PagePositions()); break;
                case SeekStepKind.Chapter: moved = StructForward(M4bChapterPositions()); break;
                case SeekStepKind.Bookmark: moved = BookmarkForward(); break;
                default: moved = SeekRelative(+GetSeekStepSeconds()); break;
            }
            if (!moved) tones.Play(300, 150);
        }

        private void SeekStepBackward()
        {
            SeekStep step = CurrentSeekStep();
            bool moved;
            if (currentBook != null && currentBook.IsTextBook) moved = TextSeek(step, -1);
            else switch (step.Kind)
            {
                case SeekStepKind.Part: moved = PartBack(); break;
                case SeekStepKind.Heading: moved = StructBack(HeadingPositions(step.Level)); break;
                case SeekStepKind.Page: moved = StructBack(PagePositions()); break;
                case SeekStepKind.Chapter: moved = StructBack(M4bChapterPositions()); break;
                case SeekStepKind.Bookmark: moved = BookmarkBack(); break;
                default: moved = SeekRelative(-GetSeekStepSeconds()); break;
            }
            if (!moved) tones.Play(300, 150);
        }

        /// <summary>Seek in a text book by the selected step (dir +1/-1). True when
        /// the whole step was available. A text book's position is a character
        /// offset and its seeks are immediate, so for the steps that jump from mark
        /// to mark "did it move" is simply read back afterwards; the two continuous
        /// steps (standard page, time) say for themselves whether they fitted.</summary>
        private bool TextSeek(SeekStep step, int dir)
        {
            if (tts == null) return false;
            int before = tts.CharPosition;
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
                    return tts.SeekChars(dir * TtsReader.StandardPageChars);
                case SeekStepKind.Bookmark:
                    // The same jump an audio book makes; BookmarkForward/Back work
                    // in the book's own unit, so they need no text branch of their
                    // own. Without this case the step fell through to the time
                    // seek below and wandered off by 15 seconds instead.
                    if (dir > 0) BookmarkForward(); else BookmarkBack(); break;
                default: // time steps (15/30/60 s / 5 / 10 min)
                    return tts.SeekSeconds(dir * GetSeekStepSeconds());
            }
            return tts.CharPosition != before;
        }

        /// <summary>Seek to the next/previous print-page marker in a structured
        /// text book (mirrors TextHeadingSeek's 50-char back grace).</summary>
        private void TextPageSeek(int dir)
        {
            if (tts == null || currentBook == null || currentBook.TextPages.Count == 0)
                return;                                   // TextSeek beeps for us
            var pages = currentBook.TextPages;
            int cur = tts.CharPosition;
            if (dir > 0)
            {
                for (int i = 0; i < pages.Count; i++)
                    if (pages[i].Offset > cur + 1) { tts.SeekToChar(pages[i].Offset); return; }
            }
            else
            {
                int idx = -1;
                for (int i = pages.Count - 1; i >= 0; i--)
                    if (pages[i].Offset <= cur) { idx = i; break; }
                if (idx < 0) return;
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

        // Character offsets of the text headings at depth â‰¤ maxLevel, ascending.
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
        /// next/previous heading of depth â‰¤ maxLevel, with a small grace so Back
        /// from just inside a heading rewinds to its start.</summary>
        private void TextHeadingSeek(int maxLevel, int dir)
        {
            var offs = TextHeadingOffsets(maxLevel);
            if (tts == null || offs.Count == 0) return;   // TextSeek beeps for us
            int cur = tts.CharPosition;
            if (dir > 0)
            {
                foreach (int o in offs)
                    if (o > cur + 1) { tts.SeekToChar(o); return; }
            }
            else
            {
                int idx = -1;
                for (int i = offs.Count - 1; i >= 0; i--)
                    if (offs[i] <= cur) { idx = i; break; }
                if (idx < 0) return;
                // >~50 chars into the heading rewinds to its start, else previous.
                tts.SeekToChar((idx == 0 || cur - offs[idx] > 50) ? offs[idx] : offs[idx - 1]);
            }
        }

        /// <summary>Go To for a structured text book â€” pick a heading from the
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
        /// them; Bookmark appears only while the book has â‰Ą1 bookmark. The
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
            // then Bookmark (dynamic â€” only when the book has bookmarks). Per type:
            //   Single audio:  30m,15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Multi audio:   Part, 30m,15m,10m,5m,60s,30s,15s,[Bookmark]
            //   DAISY:         H1,H2,â€¦,Page, 15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Flat text:     Standard page, 15m,10m,5m,60s,30s,15s,[Bookmark]
            //   Structured:    H1,H2,â€¦,Page, 15m,10m,5m,60s,30s,15s
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
                    // Chapter, then 15 min â†’ 15 s, then Bookmark (dynamic).
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

        /// <summary>Appends the shared time steps 15 min â†’ 15 s, then a Bookmark
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

        /// <summary>Distinct DAISY heading depths present, ascending â€” one seek
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
        /// position. False when there is none â€” the caller makes the sound.
        /// Positions are assumed ascending (reading order).</summary>
        private bool StructForward(System.Collections.Generic.List<double> positions)
        {
            if (positions == null || positions.Count == 0) return false;
            double pos = GetVirtualPosition();
            foreach (double p in positions)
                if (p > pos + 0.05) { SeekToVirtualPosition(p); return true; }
            return false;
        }

        /// <summary>Generic "previous structural mark" jump, mirroring
        /// BookmarkBack's 3-second grace: more than 3 s past the current mark
        /// rewinds to it, otherwise jumps to the one before.</summary>
        private bool StructBack(System.Collections.Generic.List<double> positions)
        {
            if (positions == null || positions.Count == 0) return false;
            double pos = GetVirtualPosition();
            int cur = -1;
            for (int i = positions.Count - 1; i >= 0; i--)
                if (positions[i] <= pos + 0.05) { cur = i; break; }
            if (cur < 0) return false;

            // Sitting exactly on the first mark with nothing before it: there is
            // nowhere to go, so say so rather than re-seeking to where we are.
            if (cur == 0 && pos - positions[0] <= 3.0) return false;
            SeekToVirtualPosition((cur == 0 || pos - positions[cur] > 3.0)
                                  ? positions[cur] : positions[cur - 1]);
            return true;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // ProcessCmdKey
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

                case Keys.F8:
                    // Read out fresh playback info from anywhere in the
                    // player, via the off-screen announcement label. The
                    // info box itself is not touched ďż˝ no text change, no
                    // echo.
                    //
                    // Pressed TWICE in quick succession it instead moves focus
                    // into the info box, and a third press brings it back where
                    // it came from. The box is parked off the client area and
                    // out of the tab order (ďż˝8k), so this is the only way in ďż˝
                    // and the way out matters just as much, or a reader who
                    // walked in has nowhere to walk back to.
                    if (DateTime.UtcNow - lastInfoKey < TimeSpan.FromMilliseconds(600))
                    {
                        lastInfoKey = DateTime.MinValue;   // a third press is a fresh single
                        ToggleInfoBoxFocus();
                        return true;
                    }
                    lastInfoKey = DateTime.UtcNow;
                    AnnounceToScreenReader(lblAnnounceInfo, BuildCurrentInfoText());
                    return true;

                // Speed â€” Ctrl+Left/Right (replaced Page Up/Down). Ctrl is
                // fine on Left/Right (unlike Up/Down, which the shell/JAWS grab
                // for vertical navigation).
                case Keys.Control | Keys.Left:
                    ChangeSpeed(-10);
                    return true;

                case Keys.Control | Keys.Right:
                    ChangeSpeed(+10);
                    return true;

                // Seek jump by the selected step â€” Shift+Left/Right.
                case Keys.Shift | Keys.Left:
                    SeekStepBackward();
                    return true;

                case Keys.Shift | Keys.Right:
                    SeekStepForward();
                    return true;

                // Change the seek step (the dropdown value) â€” Shift+Up/Down.
                // Down moves down the list (H1 â†’ â€¦ â†’ 15 sec), Up back toward H1,
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

                // Properties stays on Alt+Enter ďż˝ that is the Windows convention
                // for it and Gordan kept it deliberately.
                case Keys.Alt | Keys.Enter:
                    BtnProperties_Click(null, EventArgs.Empty);
                    return true;

                // The function-key set (Gordan, 2026-07-31). Letter keys had to
                // go: the seek combo swallows them as type-ahead whenever it has
                // focus, so `I` for the info box was unreliable in exactly the
                // situation a reader is most likely to be in. F1 is Help by
                // convention; F9 and F10 are left alone (F10 activates a menu
                // bar), as is Alt+F4.
                // Escape leaves the info box as well as a third F8 ďż˝ walking out
                // of somewhere with Escape is a habit, and a habit that fails is
                // worse than one that was never offered. Guarded on the box
                // actually having focus, so Escape means nothing elsewhere in
                // the player and cannot swallow a key some other control wants.
                case Keys.Escape:
                    if (infoBoxHasFocus) { ToggleInfoBoxFocus(); return true; }
                    break;

                case Keys.F1:
                    BtnHelp_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F2:
                    BtnSettings_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F3:
                    BtnLibrary_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F4:
                    // Swallowed before anything else can see it: F4 on a focused
                    // ComboBox drops its list open, and cmbSeek is focusable.
                    BtnGoTo_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F5:
                    BtnSetBookmark_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F6:
                    BtnManageBookmarks_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F7:
                    BtnTimer_Click(null, EventArgs.Empty);
                    return true;

                case Keys.F9:
                    ToggleReadingWindow();
                    return true;

                // TEMPORARY test aid â€” see ReadingDiagnostics. Deliberately on a
                // combination nothing else uses and nothing documents, so it
                // cannot be reached by accident and leaves no trace when the file
                // and these three lines go.
                // TEMPORARY: writes out the recorded audio-path timings. Separate
                // key from the highlight toggle so the recording can be taken
                // during ordinary reading, with no aid running and nothing else
                // in the way.
                // TEMPORARY: the tester marks the instant they HEARD something
                // wrong. The recording of the audio path came back completely
                // clean over a whole minute â€” every utterance played its full
                // length â€” so either the fault did not happen while it was being
                // recorded, or it happens somewhere the player cannot see. A mark
                // in the same timeline settles which, and points at the sentence.
                case Keys.Control | Keys.Shift | Keys.M:
                    ReadingDiagnostics.Note("******** HEARD A CUT HERE ********");
                    tones.Play(1200, 60);
                    return true;

                case Keys.Control | Keys.Shift | Keys.L:
                    string where = ReadingDiagnostics.Dump();
                    tones.Play(where != null ? 880 : 300, 150);
                    NvdaController.Speak(where != null ? "Timing written" : "Nothing recorded");
                    return true;

                case Keys.Control | Keys.Shift | Keys.H:
                    string diag = ReadingDiagnostics.Toggle();
                    // A tone as well as the words: NvdaController.Speak is NVDA's
                    // channel and says nothing under JAWS (Â§11), and JAWS is the
                    // primary reader here â€” so the state has to be audible either
                    // way. Rising for on, falling for off.
                    tones.Play(ReadingDiagnostics.Highlight ? 880 : 440, 120);
                    NvdaController.Speak(diag);
                    // Take effect now rather than at the next sentence: paused, or
                    // in a slow passage, nothing would call the surface for a
                    // while and the switch would seem not to have worked.
                    lastSurfaceStart = -1;
                    UpdateReadingSurface();
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // KeyDown â€” Space = Play/Pause
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                BtnPlayPause_Click(null, EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Volume
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void ChangeVolume(int delta)
        {
            int newVol = Math.Max(0, Math.Min(100, currentVolume + delta));
            currentVolume = newVol;

            if (currentBook != null && currentBook.IsTextBook)
            {
                if (tts != null) tts.SetVolume(currentVolume);
                // For a text book the Volume field IS the speech volume, so the
                // book's own TextVolume follows it â€” the two are one number, and
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
            // on every press â€” regardless of the field's accessible role
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Speed
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void ChangeSpeed(int delta)
        {
            // Text book: the speed control is words-per-minute, not an mpv
            // multiplier. Step Â±5 WPM, beep when passing the Settings default.
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
            // Focus echo guard â€” see ChangeVolume.
            if (!tbSpeed.Focused)
            {
                tbSpeed.Text = text;
                tbSpeed.AccessibleName = Localization.T("Player.Speed.Accessible", speedStr);
            }

            AnnounceToScreenReader(lblAnnounceSpeed, text);

            if (currentSpeed == 100)
                tones.Play(new[] { (800, 120), (800, 120) });
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Seek
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Seek arrows (Left/Right): 5 s in audio, one sentence in a text book.
        /// <summary>The plain arrows â€” five seconds of audio, one sentence of text.
        /// Deliberately SILENT at the edges: this is the small, constant nudge you
        /// use continuously while listening, and a beep every time you nudge past
        /// the end of the book would be noise. The audible "that is as far as it
        /// goes" belongs to the seek step (Shift+arrows), which is the deliberate
        /// jump.</summary>
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

        /// <summary>
        /// Seeks by a number of seconds. Returns whether the WHOLE step was
        /// available: a jump that runs into the beginning or the end of the book
        /// still moves there, but reports false, so the beep says "that is as far
        /// as it goes this way".
        ///
        /// <para>A time step is not like a heading: there is always somewhere to
        /// go until the very edge, so "it moved" is the wrong test â€” near the end
        /// the step is simply cut short, and without this the player would move
        /// two seconds and say nothing.</para>
        /// </summary>
        private bool SeekRelative(int seconds)
        {
            if (mpvHandle == IntPtr.Zero) return false;

            if (currentBook != null && currentBook.Chapters.Count > 0)
            {
                double from = GetVirtualPosition();
                double wanted = from + seconds;
                double target = wanted;
                if (target < 0) target = 0;
                if (currentBook.TotalDuration > 0 && target > currentBook.TotalDuration)
                    target = currentBook.TotalDuration;
                if (Math.Abs(target - from) > 0.05) SeekToVirtualPosition(target);
                return Math.Abs(target - wanted) < 0.05;
            }

            double pos = 0, duration = 0;
            mpv_get_property(mpvHandle, "time-pos", 5, ref pos);
            mpv_get_property(mpvHandle, "duration", 5, ref duration);
            double want = pos + seconds;
            double clamped = want < 0 ? 0 : (duration > 0 && want > duration ? duration : want);
            if (Math.Abs(clamped - pos) > 0.05)
                MpvCommand("seek", clamped.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                           "absolute");
            return Math.Abs(clamped - want) < 0.05;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Virtual timeline
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
                // Different file â€” playlist-play-index, then seek once it loads.
                // Pause *through* the switch even when playing: a freshly loaded
                // file starts at position 0 and would be audible for the ~300 ms
                // until the seek lands (the "blip" â€” you hear the file's opening
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Building the UI
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

            // Sleep timer â€” 1 s tick while a countdown is running.
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
            // control) â€” deliberately NOT a RichTextBox. JAWS handles rich
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
            // refresh is gone entirely â€” the text is rebuilt only at the
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
        /// Bottom panel â€” 3Ă—4 grid.
        /// Columns: A = x 0â€“160, B = x 160â€“480 (double width), C = x 480â€“640.
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

            // â”€â”€ Column A (x=10, width 140) â”€â”€
            btnLibrary = new Button();
            btnLibrary.Text = Localization.T("Btn.Library");
            btnLibrary.Size = new Size(140, 40);
            btnLibrary.Location = new Point(10, 8);
            btnLibrary.AccessibleName = Localization.T("Btn.Library.Accessible");
            btnLibrary.TabIndex = 0;
            btnLibrary.Click += BtnLibrary_Click;

            btnSettings = new Button();
            btnSettings.Text = Localization.T("Btn.Settings");
            btnSettings.Size = new Size(140, 40);
            btnSettings.Location = new Point(10, 61);
            btnSettings.AccessibleName = Localization.T("Btn.Settings.Accessible");
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
            btnHelp.AccessibleName = Localization.T("Btn.Help.Accessible");
            btnHelp.TabIndex = 3;
            btnHelp.Click += BtnHelp_Click;

            // â”€â”€ Column B (x=170, width 300) â”€â”€
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
            // Populate the steps for the current (no) book â€” time steps + Part.
            RebuildSeekSteps();
            // Keyboard-inert display: the step is changed only with
            // Shift+Up/Down (from anywhere) or the mouse. Swallowing KeyDown
            // (and the resulting KeyPress) stops the combo from reacting to
            // arrows, type-ahead letters, Home/End, or opening its dropdown
            // from the keyboard, so focusing it never steals those keys.
            cmbSeek.KeyDown += (s, e) => { e.Handled = true; e.SuppressKeyPress = true; };

            // Row 2: transport buttons
            btnBack = new Button();
            btnBack.Text = "âŹ®";
            btnBack.Size = new Size(95, 45);
            btnBack.Location = new Point(170, 58);
            btnBack.AccessibleName = Localization.T("Btn.Back.Accessible");
            btnBack.Font = new Font("Segoe UI", 14);
            btnBack.TabIndex = 5;
            btnBack.Click += BtnBack_Click;

            btnPlayPause = new Button();
            btnPlayPause.Text = "â–¶";
            btnPlayPause.Size = new Size(95, 45);
            btnPlayPause.Location = new Point(270, 58);
            btnPlayPause.AccessibleName = Localization.T("Btn.Play.Accessible");
            btnPlayPause.Font = new Font("Segoe UI", 14);
            btnPlayPause.TabIndex = 6;
            btnPlayPause.Click += BtnPlayPause_Click;

            btnForward = new Button();
            btnForward.Text = "âŹ­";
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
            // inert here and the announcement is the single feedback â€” the
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

            // â”€â”€ Column C (x=490, width 140) â”€â”€
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
            btnManageBookmarks.AccessibleName = Localization.T("Btn.ManageBookmarks.Accessible");
            btnManageBookmarks.TabIndex = 14;
            btnManageBookmarks.Click += BtnManageBookmarks_Click;

            // â”€â”€ Off-screen announcement labels â”€â”€
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

            // Tooltips â€” hover hints for mouse/visual mode, listing the
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

            // The look, last: the window has built itself the way it always has,
            // and the chosen theme restyles it. Classic does nothing here, so the
            // build regular testing runs on cannot drift while the new design is
            // being worked out. A theme that eventually brings its own LAYOUT
            // takes over above instead (UiTheme.BuildsOwnLayout).
            if (UiTheme.Current.BuildsOwnLayout) UiTheme.Current.BuildPlayerLayout(this);
            UiTheme.Current.Apply(this);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // MPV event timer
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void EventTimer_Tick(object sender, EventArgs e)
        {
            if (mpvHandle == IntPtr.Zero) return;

            while (true)
            {
                IntPtr eventPtr = mpv_wait_event(mpvHandle, 0);
                if (eventPtr == IntPtr.Zero) break;
                int eventId = Marshal.ReadInt32(eventPtr);
                if (eventId == 0) break;

                // Text book â†’ mpv is not the engine. Drain its events (idle,
                // end-of-file from a previous audio book, â€¦) but ignore them:
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

                    // Refresh the info box snapshot on part change â€” but
                    // never while the screen reader is reading it.
                    if (this.ActiveControl != tbInfo)
                        tbInfo.Text = BuildCurrentInfoText();
                }

                if (eventId == 7) // MPV_EVENT_END_FILE
                {
                    // Don't save if this is the END_FILE of the OLD book caused
                    // by "loadfile replace" when switching to a new book â€”
                    // at that point currentBook already points to the NEW book,
                    // so we'd write the old file's position/percent into it.
                    if (!isLoadingBook)
                        SaveCurrentBookProgress();
                }

                if (eventId == 11) // MPV_EVENT_IDLE
                {
                    // Natural end of the playlist while a book was playing â€”
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Progress timer
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

                // On a hybrid the narration is what moves the text, so the caret
                // is stepped from this same tick. Cheap: UpdateReadingSurface
                // returns immediately unless the mapped offset actually changed.
                if (currentBook != null && currentBook.IsHybrid && readingWindow != null)
                    UpdateReadingSurface();

                string posText = Localization.T("Player.Position.Text", FormatTime(virtualPos), FormatTime(totalDur));
                tbProgress.Text = posText;
                tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", percent);
                lblProgress.Text = posText;

                // Title bar counts down live (window caption â€” a sighted user
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Sleep timer
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Spec (Session 5 + playback-coupling revision, Session 8):
        // presets 15/30/45/60 min + custom; expiry actions Stop /
        // Stop+close / Stop+close+shutdown; pressing the button (Ctrl+T)
        // while a timer runs stops playback and cancels the timer;
        // countdown on the button text; audible signals: a beep series at
        // -5 min, then a volume fadeout over the last 45 seconds. See the
        // comment at the state fields for the playback-coupling rules.

        private void BtnTimer_Click(object sender, EventArgs e)
        {
            // The timer is tied to a listening session â€” with an empty
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
            // dialog opens â€” to set another timer, press the (now idle)
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
            // immediately after closing the dialog â€” pointless noise, skip it.
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

        /// <summary>Pauses whatever is playing â€” mpv for an audio book, the speech
        /// reader for a text one â€” WITHOUT the user-pause semantics: these are the
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
                // Resuming re-speaks the SAME sentence from its start, so the
                // position does not change and the surface's "has it moved?"
                // guard skips it â€” the sentence you are hearing again is the one
                // sentence not put out again. Harmless for the caret, which is
                // already in the right place, but it made the test aid look as
                // though it had switched itself off after every pause.
                lastSurfaceStart = -1;
                if (tts != null) tts.Play();
                return;
            }
            if (mpvHandle != IntPtr.Zero)
                mpv_set_property_string(mpvHandle, "pause", "no");
            SetPlayPauseState(true);
        }

        /// <summary>
        /// Stops the countdown, restores the button and â€” if the fadeout
        /// had already started â€” brings the mpv volume back to the user's
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
        /// The fadeout only ever touches mpv directly â€” currentVolume, the
        /// Volume field and Book.ini never see the faded values â€” so this
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
            // volume down to 0 at the deadline, applied straight to mpv â€”
            // currentVolume and the UI stay untouched, so the saved volume
            // and the Volume field never see the faded values. One step
            // per tick (1 s) is plenty smooth for a ~45-step ramp.
            // A text book fades the same way, through the speech engine. Speech
            // volume only takes effect on the NEXT sentence (changing it mid-
            // utterance would mean re-speaking the sentence), so there the fade
            // steps down sentence by sentence rather than second by second â€”
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
        /// skipped (force=false) â€” the same JAWS echo guard as the info box;
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
        /// ("1:29:59" â†’ ... â†’ "1:00:01", "60:00", "59:59", ...).
        /// </summary>
        private string FormatCountdown(TimeSpan t)
        {
            if (t.TotalMinutes > 60)
                return string.Format("{0}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
        }

        /// <summary>
        /// Executes the chosen expiry action â€” on the deadline, or earlier
        /// if the book ends by itself (see FinishCurrentBook). All three
        /// actions start the same way: pause playback (if anything is
        /// playing), then undo the fadeout and save progress. Pausing FIRST
        /// matters â€” restoring the volume while still audible would end the
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
                    // countdown by design â€” the user asked for this action.
                    try
                    {
                        Process.Start("shutdown", "/s /t 5");
                    }
                    catch
                    {
                        // If the shutdown command can't start (unlikely),
                        // still close the app â€” playback is already stopped
                        // and progress saved.
                    }
                    this.Close();
                    break;
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Info box
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Player book type â€” drives title bar, info box, seek steps and Go To.
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
            // DAISY with no headings, or plain audio â†’ single vs multi by parts.
            return currentBook.Chapters.Count > 1 ? PlayerType.MultiAudio : PlayerType.SingleAudio;
        }

        // Short format name for the info box (MP3 Audio / Daisy Audio 3 / Apple
        // Book M4B / EPUB / MS Word Docx â€¦).
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
            // "TAG â€” Official Name" shape as FriendlyFormatName produces.
            if (currentBook.IsDaisy && head.StartsWith("Daisy", StringComparison.OrdinalIgnoreCase))
                return "DAISY " + head.Substring(5).Trim() + " â€” Digital Accessible Information System";
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
        /// Builds the info text for the current playback moment â€” used by
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

            // Voice + reading speed, e.g. "Voice: Karmela, 250 WPM". It said
            // "Speech engine" until the engine stopped being a thing the reader
            // chooses; the value was always the voice.
            //
            // With no voice this is the line that would lie hardest â€” it would name
            // whatever spoke last â€” so it carries the reason instead. The dialog
            // is the moment; this is the STATE, and it stays for as long as the
            // book is loaded, for anyone who dismissed the dialog or never heard
            // it announced.
            if (textNoVoice)
                sb.Append(Localization.T("Player.Info.NoVoiceLabel")).Append(' ')
                  .Append(SettingsForm.LanguageName(LanguageDetector.Primary(
                      currentBook != null ? currentBook.TextLanguage : ""))).Append(nl).Append(nl);
            else
            {
                string voice = tts != null && !string.IsNullOrEmpty(tts.CurrentVoice) ? tts.CurrentVoice : dash;
                sb.Append(Localization.T("Player.Info.VoiceLabel")).Append(' ')
                  .Append(voice).Append(", ").Append(currentWpm).Append(" WPM").Append(nl).Append(nl);
            }

            int total = tts != null ? tts.TotalChars : 0;
            int at = tts != null ? tts.CharPosition : 0;
            double elapsed = TextSeconds(at);
            double totalSec = TextSeconds(total);

            // Elapsed/remaining carry no "â‰" â€” the approximation mark is reserved
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Title bar
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void UpdateTitleBar()
        {
            string appName = Localization.T("App.Name");

            if (currentBook == null)
            {
                if (currentFile != null)
                {
                    string st = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");
                    this.Text = appName + " â€” " + System.IO.Path.GetFileNameWithoutExtension(currentFile) + st;
                }
                else this.Text = appName;
                return;
            }

            string stateText = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");
            const string sep = " â€” ";
            PlayerType type = GetPlayerType();
            string title = currentBook.Title;
            string body;

            if (type == PlayerType.FlatText || type == PlayerType.StructuredText)
            {
                // Text: per TTS speed. Structured â†’ Title / Chapter / Page (or
                // -remaining if no pages); flat â†’ Title / X.Y% / -remaining.
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
                    // no chapters) the page is the natural locator â†’
                    // "Title â€” Page: N â€” X.Y%"; a plain .txt (no pages) keeps
                    // "Title â€” X.Y% â€” -remaining".
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

            this.Text = appName + " â€” " + body + stateText;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Saving progress
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void SaveCurrentBookProgress()
        {
            if (currentBook == null) return;
            try
            {
                // Text book â†’ remember the character offset and percentage.
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
                // index â€” the row layout varies per book (Part vs Heading/Page/
                // Bookmark come and go).
                currentBook.SeekStep = EncodeSeekStep(CurrentSeekStep());
                currentBook.Save();
            }
            catch (Exception)
            {
                // Silently ignore â€” e.g. if the book's folder was deleted while
                // it was active (Form1's background timers keep running while
                // the modal Library window is open).
            }
        }

        /// <summary>
        /// Called when a book plays to its natural end: marks it as finished
        /// (100%, position reset â€” the "Read" shelf group), unloads it from
        /// the player (so it's no longer "active" and can be deleted), and
        /// opens the Library so the next step â€” pick another book, delete
        /// the finished one â€” is right at hand.
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
                // Silently ignore â€” folder may have been deleted meanwhile.
            }

            // Unload â€” the player returns to its empty state. With
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
                    // naturally â€” quietly drop the timer and continue with
                    // the normal finish flow (library opens below).
                    CancelSleepTimer(false);
                }
                else
                {
                    // Close/shutdown: execute right away. The library is
                    // deliberately NOT opened first â€” no point in a modal
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
        /// Unloads the active book without touching its saved progress â€” used
        /// when the user marks the currently-playing book as read from the
        /// Library (which already persisted it at 100%). Stops playback and
        /// returns the player to its empty state.
        /// </summary>
        private void UnloadActiveBook()
        {
            SetPlayPauseState(false);
            if (tts != null) tts.Stop();
            // "stop" unloads the current file and clears the playlist, so mpv
            // releases its handle on the audio â€” otherwise the folder can't be
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Buttons
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void SetPlayPauseState(bool playing)
        {
            isPlaying = playing;
            btnPlayPause.Text = playing ? "âŹ¸" : "â–¶";
            btnPlayPause.AccessibleName = playing ? Localization.T("Btn.Pause.Accessible") : Localization.T("Btn.Play.Accessible");
            // The one place that knows whether anything is sounding, so the one
            // place the device keep-alive belongs (Â§10f). It is deliberately NOT
            // limited to text books: an audio book pauses too, and the same
            // endpoint sleeps the same way â€” the first seconds after a resume
            // would go the same way as the first word of a sentence.
            //
            // Stopped on pause rather than left running: holding a sound card
            // open for a book nobody is listening to would keep an amplifier
            // awake all night, and the reason to be careful here is the same
            // reason NBR is portable â€” it should not quietly change the machine
            // it is running on.
            try
            {
                if (playing)
                {
                    // Made here, not with the TTS reader: an audio book never
                    // creates one of those and would have gone without.
                    if (keepAlive == null)
                    {
                        keepAlive = new AudioKeepAlive();
                        keepAlive.SetDevice(appSettings.AudioDevice);
                    }
                    keepAlive.Start();

                    // The reading window comes up on PLAY, not on load (Gordan,
                    // 2026-08-01). Opening the player says nothing about what you
                    // mean to do — continue this book, pick another, or something
                    // else entirely — so it must not put a second window in front
                    // of you before you have decided. Play IS the decision, and it
                    // brings the book's properties with it, this one among them.
                    //
                    // Once per book: closing the window should not be undone by
                    // the next pause and resume.
                    ReadingDiagnostics.Note(string.Format("PLAY: offered={0} opens={1}",
                        readingWindowOffered,
                        currentBook != null && currentBook.OpensReadingWindow));
                    if (!readingWindowOffered && currentBook != null && currentBook.OpensReadingWindow)
                    {
                        readingWindowOffered = true;
                        OpenReadingWindowWhenReady();
                    }
                }
                else if (keepAlive != null) keepAlive.Stop();
            }
            catch { }
            UpdateTitleBar();
        }

        private void BtnPlayPause_Click(object sender, EventArgs e)
        {
            // Text book â†’ drive the TTS reader instead of mpv.
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
                    // Space would otherwise start it reading in whatever voice
                    // spoke last, which is the very thing this is here to prevent.
                    // Pressing Play on a book you were told cannot be read is a
                    // change of mind, so it gets the question again rather than
                    // the same refusal â€” and only the announcement if the dialog
                    // itself could not be put.
                    if (textNoVoice && !AskForVoice()) { AnnounceNoVoice(); return; }
                    lastSurfaceStart = -1;      // same reason as ResumePlaybackQuietly
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

                // A MANUAL pause ends the listening session â€” an active
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
        // equivalent of Shift+Left/Shift+Right and the media keys â€” all of
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
        /// <summary>True when it moved. At the very beginning of the first part
        /// there is nothing before it, and the caller says so with a beep.</summary>
        private bool PartBack()
        {
            if (mpvHandle == IntPtr.Zero) return false;
            double position = 0;
            mpv_get_property(mpvHandle, "time-pos", 5, ref position);
            if (position > 3.0)
            {
                MpvCommand("seek", "0", "absolute");
                return true;
            }
            if (currentPlaylistIndex <= 0) return false;
            MpvCommand("playlist-prev", "weak");
            if (!isPlaying)
                mpv_set_property_string(mpvHandle, "pause", "yes");
            return true;
        }

        private bool PartForward()
        {
            if (mpvHandle == IntPtr.Zero) return false;
            int parts = currentBook != null ? currentBook.Chapters.Count : 0;
            if (parts > 0 && currentPlaylistIndex >= parts - 1) return false;   // last part
            MpvCommand("playlist-next", "weak");
            if (!isPlaying)
                mpv_set_property_string(mpvHandle, "pause", "yes");
            return true;
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
        // â”€â”€ Positions in the book's own unit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
        /// into the book it is â€” and then **the words it sits on**, which is what
        /// actually identifies the place ("41,7 %, Tada je Perica shvatio daâ€¦").</summary>
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

        /// <summary>True when it moved; the caller makes the "nothing there"
        /// sound, so every seek step behaves the same way.</summary>
        private bool BookmarkBack()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0) return false;

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

            if (currentIndex < 0) return false;

            double grace = BookBackGrace();
            // On the first mark, with nothing before it and nothing to rewind:
            // there is nowhere to go.
            if (currentIndex == 0 && pos - currentBook.Bookmarks[0] <= grace) return false;

            SeekToBookPosition(currentIndex == 0 || pos - currentBook.Bookmarks[currentIndex] > grace
                               ? currentBook.Bookmarks[currentIndex]
                               : currentBook.Bookmarks[currentIndex - 1]);
            return true;
        }

        private bool BookmarkForward()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0) return false;

            double pos = BookPosition();
            foreach (double bookmark in currentBook.Bookmarks)
            {
                if (bookmark > pos)
                {
                    SeekToBookPosition(bookmark);
                    return true;
                }
            }
            return false;   // already past the last bookmark
        }

        private void BtnLibrary_Click(object sender, EventArgs e)
        {
            if (isLibraryOpen) return;

            SaveCurrentBookProgress();

            isLibraryOpen = true;
            try
            {
                // Declining a book with no voice for its language puts you back on
                // the SHELF, not into the player: nothing was loaded, so there is
                // nothing to be left looking at. Hence the loop â€” the Library
                // reopens and you pick something else. Asking here rather than
                // letting LoadBook do it costs nothing: a voice chosen is written
                // to the book, so LoadBook's own check then passes silently and
                // nobody is asked twice.
                bool backToShelf = true;
                while (backToShelf)
                {
                    backToShelf = false;
                    // The callback lets the Library unload the active book the moment
                    // it's marked read (so it can be deleted in the same session,
                    // with mpv's file handle released) rather than waiting for close.
                    using (LibraryForm libraryForm = new LibraryForm(appSettings,
                        currentBook != null ? currentBook.FolderPath : null, UnloadActiveBook))
                    {
                        libraryForm.ShowDialog(this);

                        if (libraryForm.DialogResult == DialogResult.OK && libraryForm.SelectedBook != null)
                        {
                            if (!EnsureVoiceForBook(libraryForm.SelectedBook)) { backToShelf = true; continue; }
                            LoadBook(libraryForm.SelectedBook, true);
                        }
                    }
                }
            }
            finally
            {
                isLibraryOpen = false;
            }
        }

        /// <summary>Walks focus into the playback info box and back out again.
        ///
        /// <para>The box is parked below the client area and kept out of the tab
        /// order (ďż˝8k), so there is no other way in ďż˝ and no way out either,
        /// which is why this remembers where focus came from rather than just
        /// focusing something sensible. It also refreshes the text first: the
        /// box is a snapshot, and arriving at a stale one is worse than not
        /// arriving at all.</para></summary>
        private ReadingWindow readingWindow;

        /// <summary>Opens or closes the on-screen reading view (F9).
        ///
        /// <para><b>The book's own setting is what decides whether the view
        /// appears</b>, at load. This key is the way BACK, not the way in: Escape
        /// closes the window by convention, and without F9 that would be a dead
        /// end reachable only through Properties (Gordan, 2026-08-01).</para>
        ///
        /// <para><b>It deliberately does NOT change the setting.</b> Opening the
        /// view by hand on a book with visual output off is a look, not a
        /// decision - the same rule 10c applies to borrowing a voice in
        /// NoVoiceForm: for this book, this time, and making it a rule is
        /// Settings' job and should take some effort.</para>
        ///
        /// <para>A toggle rather than two commands because it is one place you
        /// are either in or not ďż˝ Escape works too, but only
        /// once focus is inside it.</para>
        ///
        /// <para>The window BORROWS <see cref="tbReadingSurface"/> and returns it
        /// on close, so the text a screen reader tracks is the same control in
        /// both places. Nothing here duplicates it.</para></summary>
        private void ToggleReadingWindow()
        {
            // A window that exists but has not been SHOWN yet is not open. It is
            // made here and shown one message cycle later (ShowDialog blocks, so
            // it cannot be called from inside the play path), and in that gap
            // this used to treat it as open and close it — closing a form that
            // was never shown, which raises no FormClosed, so readingWindow
            // stayed non-null for good and every later F9 did nothing at all.
            if (readingWindow != null && !readingWindow.IsDisposed && readingWindow.Visible)
            {
                readingWindow.Close();
                return;
            }
            if (readingWindow != null && !readingWindow.IsDisposed)
            {
                ReadingDiagnostics.Note("F9 while the window was made but not yet shown — ignored");
                return;                       // it is on its way; let it arrive
            }
            // Same low "no go" beep the other book keys give on an empty player.
            // Tested on the TEXT, not on tts: a hybrid has words to show and no
            // synthesiser, and requiring one was what made F9 beep at the one
            // kind of book whose text is already joined to its audio.
            if (currentBook == null || readingText == null)
            {
                // The beep says "no" and nothing else, and there are four
                // different reasons for it. Recorded here rather than guessed at:
                // this happens on a key press, not per sentence, so it costs
                // nothing that matters.
                ReadingDiagnostics.Note(string.Format(
                    "F9 REFUSED: book={0} textbook={1} hybrid={2} textFile={3} sync={4} readingText={5}",
                    currentBook == null ? "null" : "ok",
                    currentBook != null && currentBook.IsTextBook,
                    currentBook != null && currentBook.IsHybrid,
                    currentBook == null || string.IsNullOrEmpty(currentBook.TextFilePath)
                        ? "none" : (System.IO.File.Exists(currentBook.TextFilePath) ? "exists" : "MISSING"),
                    currentBook == null || currentBook.Sync == null ? "null"
                        : (currentBook.Sync.IsEmpty ? "empty" : currentBook.Sync.Count.ToString()),
                    readingText == null ? "null" : readingText.Length.ToString()));
                tones.Play(300, 150);
                return;
            }
            EnsureReadingSurface();
            LoadReadingSurface();

            var mode = (VisualMode)(currentBook.TextVisualMode >= 0 && currentBook.TextVisualMode <= 2
                                    ? currentBook.TextVisualMode : 0);
            readingWindow = new ReadingWindow(this, tbReadingSurface, mode,
                () => DistinctBookChars(),
                k =>
                {
                    // Space is the exception, and it took sitting in the window to
                    // notice. The player handles Space in Form1_KeyDown, NOT in
                    // ProcessCmdKey â€” every other shortcut is in ProcessCmdKey,
                    // which is why forwarding worked for all of them and silently
                    // did nothing for this one. Play/pause from the reading
                    // window has therefore never worked, nor has the window's own
                    // Play button, which sends the same key. Gordan had to Escape
                    // out of the window to stop the reading.
                    //
                    // Handled HERE rather than by adding a Space case to
                    // ProcessCmdKey: that runs before normal key handling for the
                    // whole player, so a Space case there would stop Space
                    // activating whichever button has focus â€” trading one dead
                    // key for a broken convention everywhere else.
                    if (k == Keys.Space) { BtnPlayPause_Click(null, EventArgs.Empty); return; }
                    Message m = new Message(); ProcessCmdKey(ref m, k);
                });
            readingWindow.FormClosed += (s, e) =>
            {
                readingWindow = null;
                // Back on the player it is parked, not shown: ďż˝8l wants it out of
                // the way of the eye but still in the accessibility tree, which is
                // what the off-client-area trick gives (ďż˝8k).
                if (tbReadingSurface != null)
                    tbReadingSurface.SetBounds(12, ClientSize.Height + 4, ClientSize.Width - 24, 44);
                Activate();
            };
            // MODAL, like the Library, Settings and Properties dialogs — Gordan's
            // observation, and it is the answer. Those hold focus absolutely and
            // the player cannot be reached while they are up, because ShowDialog
            // runs its own message loop and the owner takes no input until it
            // ends. Every attempt with Show() was ASKING for focus and then
            // defending it against the play path. A modal window does not ask.
            //
            // Deferred, because this is called from the MIDDLE of the play path:
            // ShowDialog blocks its caller, so calling it here would leave the
            // play half-finished. Posted, it opens once that has returned.
            //
            // Playback is unaffected. The nested loop runs on the same thread,
            // so Form1's timers go on ticking, mpv goes on playing, and every key
            // the window forwards still reaches the player's own handlers.
            BeginInvoke((Action)(() =>
            {
                ReadingWindow modal = readingWindow;
                if (modal == null || modal.IsDisposed) return;
                ReadingDiagnostics.Note("READING WINDOW opening (modal)");
                try { modal.ShowDialog(this); }
                catch (Exception ex) { ReadingDiagnostics.Note("ShowDialog THREW " + ex.Message); }
                ReadingDiagnostics.Note("READING WINDOW closed");
                // ShowDialog does not dispose the form the way Show does.
                try { modal.Dispose(); } catch { }
            }));
        }


        /// <summary>The distinct characters of the book, for filtering the font
        /// list. Measured on the real text rather than derived from the language,
        /// because a Croatian book can quote Greek (ďż˝8l).</summary>
        private char[] DistinctBookChars()
        {
            var set = new System.Collections.Generic.HashSet<char>();
            try
            {
                string t = tts != null ? tts.FullText : null;
                if (string.IsNullOrEmpty(t)) return new char[0];
                // A sample is enough and keeps this instant on a long book; the
                // opening pages carry the script and the accented letters.
                int take = Math.Min(t.Length, 20000);
                for (int i = 0; i < take; i++)
                    if (!char.IsWhiteSpace(t[i]) && !char.IsControl(t[i])) set.Add(t[i]);
            }
            catch { }
            var arr = new char[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        private void ToggleInfoBoxFocus()
        {
            if (tbInfo == null) return;
            if (this.ActiveControl == tbInfo)
            {
                Control back = infoBoxCameFrom;
                infoBoxCameFrom = null;
                if (back != null && back.CanSelect && !back.IsDisposed) back.Focus();
                else SelectNextControl(tbInfo, true, true, true, true);
                return;
            }
            infoBoxCameFrom = this.ActiveControl;
            tbInfo.Text = BuildCurrentInfoText();
            tbInfo.TabStop = true;          // reachable while we are standing in it
            tbInfo.Select(0, 0);
            tbInfo.Focus();
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
            // a Settings change â€” that is the whole point of the per-book setting.
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
        /// touched â€” the player's own fields are settled when the dialog closes, so
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

        /// <summary>Switch libmpv's output device live (empty â†’ "auto", the
        /// system default). Used for the Settings â†’ Device live preview and to
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
            // The keep-alive follows the card too, or it would be holding the
            // one nobody is listening to open and letting the chosen one sleep.
            keepAlive?.SetDevice(device);
            tones.SetDevice(device);   // the app's own beeps follow the book
        }

        private void BtnHelp_Click(object sender, EventArgs e)
        {
            // One way in for the manual, shared with F1 everywhere else, so the
            // Help key and the Help key cannot come to mean different things.
            HintSystem.OpenManual(this);
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
            // saved, so hand the CURRENT values to the dialog â€” otherwise it would
            // show the last-saved ones and look stale.
            currentBook.Volume = currentVolume;
            currentBook.Speed = currentSpeed;
            // The speech settings live in the player's own fields until progress is
            // saved, so file the LIVE ones under the voice in use â€” that is what the
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
            // kept the old ones) â€” either way re-apply the book's settings.
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

            // Text book â†’ navigate by headings (structured) or a low beep (flat,
            // nothing to jump to).
            if (currentBook.IsTextBook)
            {
                if (currentBook.TextHeadings.Count > 0) TextGoTo();
                else tones.Play(300, 150);
                return;
            }

            // Single-file audio (and the M4B single-file fallback) have no parts
            // or chapters to list â€” Go To is inactive (low beep).
            if (GetPlayerType() == PlayerType.SingleAudio)
            {
                tones.Play(300, 150);
                return;
            }

            if (currentBook.Chapters.Count == 0)
            {
                // No playable content â€” a short low beep as audible feedback.
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
            // (no "N/M â€”" prefix â€” the name/file already self-numbers).
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

            // Stored in the book's own unit â€” seconds for audio, the character
            // offset for a text book, which is what its position is.
            currentBook.AddBookmark(BookPosition());
            RebuildSeekSteps();

            // Ascending series of five short beeps (~1 second total) â€” a
            // bit more attention-grabbing than the plain "no go" beep, since
            // this confirms a successful action rather than a blocked one.
            tones.Play(new[] { (500, 200), (650, 200), (800, 200), (950, 200), (1100, 200) });

            // Deliberately no position/percent details here â€” TMI for a
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
            // as the Sleep Timer dialog â€” a programmatic pause, so it does not
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Loading a book (from the library or at startup)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Text books (TTS)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void EnsureTts()
        {
            if (tts != null) return;
            tts = new TtsReader();
            // TEMPORARY: record the audio path's own timings, in memory. Costs a
            // null check per event when nobody has asked for them, and this is
            // the one part of the reading nobody has actually measured yet.
            SapiWavPlayer.Log = ReadingDiagnostics.Note;
            // SAPI raises SpeakCompleted on a background thread, so marshal UI
            // updates back to the form.
            tts.PositionChanged += () =>
            {
                if (IsDisposed) return;
                if (tts != null)
                    ReadingDiagnostics.Note(string.Format(
                        "SENTENCE #{0} at char {1}, {2} chars",
                        tts.CurrentSentence, tts.CharPosition,
                        (tts.CurrentText ?? "").Length));
                try { BeginInvoke((Action)UpdateTextPositionDisplay); } catch { }
                try { BeginInvoke((Action)UpdateReadingSurface); } catch { }
            };
            tts.Finished += () =>
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)(() => { SetPlayPauseState(false); FinishCurrentBook(); })); } catch { }
            };
            // Start on the output device chosen in Settings â†’ Device.
            tts.SetAudioDevice(appSettings.AudioDevice);
        }

        /// <summary>Raises the reading window for a book that asks for it — now,
        /// or as soon as the player is in a state to raise anything.
        ///
        /// <para>Both callers used to do this inline with a bare
        /// <c>BeginInvoke</c>, and that throws when the form has no handle yet,
        /// which is exactly the case while a book is being loaded at start-up.
        /// On the hybrid path the throw was caught by the surrounding handler and
        /// wiped the text that had just been read; the window then turned up
        /// later, when something else reloaded the book with a handle in place,
        /// which is the "it appeared on its own" Gordan described.</para>
        ///
        /// <para>Simply skipping when there is no handle would trade a visible
        /// fault for a silent one — the book that asked for a window would open
        /// without it and say nothing. So it waits for the handle instead, once,
        /// and unsubscribes itself.</para></summary>
        /// <summary>False until this book's window has been offered once, so
        /// closing it is not undone by the next pause and resume.</summary>
        private bool readingWindowOffered;

        private void OpenReadingWindowWhenReady()
        {
            // Every way out is recorded. "Nothing recorded" told us more than any
            // of the fixes did: the window code was never reached at all, so the
            // decision fails before it, and a silent early return cannot be told
            // from a decision that was never taken.
            ReadingDiagnostics.Note(string.Format(
                "OPEN? book={0} opens={1} window={2} handle={3}",
                currentBook == null ? "null" : "ok",
                currentBook != null && currentBook.OpensReadingWindow,
                readingWindow == null ? "null" : (readingWindow.Visible ? "visible" : "made"),
                IsHandleCreated));

            if (currentBook == null || !currentBook.OpensReadingWindow) return;
            if (readingWindow != null) return;

            if (!IsHandleCreated)
            {
                EventHandler once = null;
                once = (s, e) => { HandleCreated -= once; OpenReadingWindowWhenReady(); };
                HandleCreated += once;
                return;
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (currentBook != null && currentBook.OpensReadingWindow && readingWindow == null)
                        ToggleReadingWindow();
                }));
            }
            catch { }
        }

        /// <summary>Reads a hybrid's text in for the reading window and opens it
        /// if the book asks for it.
        ///
        /// <para>No <see cref="TtsReader"/> is made. That would start the 32-bit
        /// speech satellite for a book nothing is going to synthesise, and worse,
        /// it would put a second voice behind a narrator â€” the one thing Â§8c says
        /// a hybrid reader did not come for. The text is loaded, and the audio
        /// clock moves the caret through it.</para></summary>
        private void LoadHybridReadingText()
        {
            try
            {
                if (currentBook == null || string.IsNullOrEmpty(currentBook.TextFilePath)
                    || !System.IO.File.Exists(currentBook.TextFilePath)) return;
                readingText = System.IO.File.ReadAllText(currentBook.TextFilePath,
                                                         System.Text.Encoding.UTF8);
                // LOAD it. Sync is filled by LoadSyncMap and nothing else, and
                // nothing else was calling it — so it was null for every hybrid
                // ever opened, this method bailed every time, and the window
                // never came up. Testing the property without asking for the
                // data is a check that can only fail.
                SyncMap sync = currentBook.LoadSyncMap();
                // A hybrid with no sync map has text and audio that do not know
                // about each other; the caret would sit at nought while the
                // narrator read on, which is worse than no window at all.
                if (sync == null || sync.IsEmpty) { readingText = null; return; }

            }
            catch { readingText = null; return; }

            // The automatic open is a SEPARATE try, and this is why. It used to
            // sit inside the one above, and BeginInvoke throws when the form has
            // no handle yet — which is exactly the case while a book is being
            // loaded at start-up. The throw landed in that catch, which wiped
            // readingText, and F9 then refused with the text read, the sync map
            // loaded and 3 674 points in hand. The window only appeared later,
            // after something else had reloaded the book with a handle in place,
            // which is precisely the "it turned up on its own" Gordan saw.
            //
            // A failure to raise the window must not destroy the reading.
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
            // What the reading window will show. Taken from the reader rather
            // than from bookText because LoadText normalises, and the surface
            // offsets have to be the SAME offsets the reader reports.
            readingText = tts.FullText;
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

            // The book's own setting decides whether the reading view appears ďż˝
            // F9 is the way BACK after Escape, not the way in (Gordan). Deferred
            // a tick so the player has finished coming up before a second window
            // takes the foreground.
            //
            // Braille opens it too. A screen reader brailles whatever holds FOCUS,
            // so the text has to live in a control the user can be in; the reading
            // window IS that control. Asking for braille and getting no window
            // would be asking for braille and getting nothing.
            // Guarded like the hybrid one: BeginInvoke throws with no handle yet,
            // and a book loaded at start-up has none. Here the throw would have
            // escaped into whatever called this rather than merely losing the
            // window — the same trap, one step worse.

            // Cache the character count for the reading-time estimate.
            currentBook.TextChars = tts.TotalChars;

            // A book nothing can read does not start reading, however it was
            // opened. Autoplay from the Library is exactly how a Spanish book got
            // read aloud in Croatian before anyone could stop it.
            //
            EnsureReadingSurface();
            LoadReadingSurface();

            UpdateTitleBar();
            UpdateTextPositionDisplay();

            if (textNoVoice)
            {
                // Only reachable when a book that WAS readable stops being so â€”
                // the language was worked out just now, or a voice was uninstalled
                // mid-session. NOT the book you are now reading: it stays unread
                // in the Library and NBR does not resume it on the next start.
                SetPlayPauseState(false);
                return;
            }

            appSettings.SetLastOpenedBook(currentBook.FolderPath);

            if (autoPlay)
            {
                SetPlayPauseState(true);
                // Defer the first Play one tick so any pending SAPI cancel from
                // loading has settled â€” otherwise it swallows this utterance and
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
        /// the neutral default â€” never the settings of the voice used before it.</summary>
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
        private string DefaultVoiceForBook(out VoiceSource how)
        {
            how = VoiceSource.GlobalDefault;
            string settingsVoice = appSettings.TtsVoice ?? "";
            string lang = currentBook != null ? currentBook.TextLanguage : "";
            if (tts == null || string.IsNullOrEmpty(lang)) return settingsVoice;

            List<(string Name, string Vendor, string Language)> voices;
            try { voices = tts.GetVoiceInfos(); }
            catch { return settingsVoice; }

            // The whole rule lives in VoiceChooser, which Properties asks too â€”
            // this used to be a second copy of it, and the two had already begun
            // to differ. This is also where detection finally pays off: the
            // language worked out at import picks the voice the user chose for it
            // in Settings.
            return VoiceChooser.ForLanguage(appSettings, voices, lang, out how);
        }

        private string DefaultVoiceForBook()
        {
            VoiceSource how;
            return DefaultVoiceForBook(out how);
        }

        /// <summary>True while the loaded text book is in a language nothing
        /// installed speaks and has not been given a voice by hand. Not a failure
        /// to paper over by picking something: the book waits until the reader
        /// chooses, which they do in its Properties.</summary>
        private bool textNoVoice;

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The reading surface â€” an EXPERIMENT, not a feature yet (2026-07-29)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // The question it exists to answer: if the sentence being read sits in a
        // real, focusable, read-only control, does the SCREEN READER put it on the
        // braille display by its own ordinary tracking? If it does, braille output
        // costs no drivers and works with every display the reader supports, and
        // the visual and braille outputs turn out to be one feature rather than
        // two. See CLAUDE.md Â§8l for the whole model and for what has to be proved.
        //
        // How to run it: open a text book, Tab to the end of the player, open
        // NVDA's Braille Viewer (NVDA menu â†’ Tools) and press Play. Watch for the
        // sentence appearing there and changing as it reads â€” and listen for
        // whether Space and the arrows still work while this control has focus.
        //
        // It is PARKED below the client area, the way the announce labels are.
        //
        // An earlier note here said that had been tried and failed - "the first
        // run showed an empty braille viewer" - and concluded that braille goes
        // through the reader's screen model, which would not see an object
        // outside the visible area. MEASURED AGAIN 2026-08-01 and that is WRONG:
        // with the surface parked, NVDA put its text on the braille display,
        // plainly and completely.
        //
        // The old failure was almost certainly the trap this project has now
        // fallen into three separate times: braille follows FOCUS, and a window
        // that never took the foreground brailles nothing. Two runs today read
        // the braille viewer's own check box back while proving nothing at all,
        // and a third read a stale line because the harness was destroying and
        // recreating the control's window handle on every switch, which to a
        // reader is the object vanishing.
        //
        // How to re-run it: open a text book, open NVDA's Braille Viewer (NVDA
        // menu ďż˝ Tools), press Play, and watch the line change as it reads.
        // Confirm from the SAME frame that the player really has focus - a
        // measurement taken while something else does is worth nothing.
        private TextBox tbReadingSurface;

        private void EnsureReadingSurface()
        {
            if (tbReadingSurface != null) return;
            tbReadingSurface = new TextBox();
            tbReadingSurface.Multiline = true;
            tbReadingSurface.ReadOnly = true;
            tbReadingSurface.WordWrap = true;
            tbReadingSurface.ScrollBars = ScrollBars.None;
            // Parked below the client area: out of the eye's way, still in the
            // accessibility tree, and measured to reach braille (see above).
            tbReadingSurface.SetBounds(12, ClientSize.Height + 4, ClientSize.Width - 24, 44);
            tbReadingSurface.BackColor = NewPlayerSkin.Glass;
            tbReadingSurface.ForeColor = NewPlayerSkin.Lit;
            tbReadingSurface.BorderStyle = BorderStyle.FixedSingle;
            tbReadingSurface.TabStop = true;
            tbReadingSurface.TabIndex = 900;
            tbReadingSurface.AccessibleName = Localization.T("Player.ReadingSurface.Accessible");
            // A focused multiline TextBox selects everything, which a reader
            // announces as a selection and braille shows as a solid block. The
            // caret goes to the start instead â€” the lesson the info glass taught.
            // The selection IS the reading position now, so it must not be thrown
            // away on focus and must stay visible when focus is elsewhere.
            tbReadingSurface.HideSelection = false;
            tbReadingSurface.ScrollBars = ScrollBars.Vertical;
            // The arrows are GLOBAL in this player â€” up/down volume, left/right
            // seek â€” and an edit control claims them for the caret. Gordan's
            // report: left and right stopped navigating and the reader read out
            // gibberish instead, which is the caret crawling a character at a
            // time. Same rule the volume and speed fields already live by, and
            // the same reason the seek combo is keyboard-inert: nothing on this
            // window may swallow an arrow.
            //
            // Harmless where ProcessCmdKey already wins â€” it runs first, and when
            // it consumes the key this handler is never reached at all.
            tbReadingSurface.KeyDown += (s, e) =>
            {
                switch (e.KeyCode)
                {
                    case Keys.Left: case Keys.Right:
                    case Keys.Up: case Keys.Down:
                    case Keys.Home: case Keys.End:
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                }
            };
            Controls.Add(tbReadingSurface);
            tbReadingSurface.BringToFront();   // the skin's canvas is added last

            // Watches for a caret WE did not move. A routing key on a braille
            // display â€” or the viewer's "route to cell by hovering" â€” moves the
            // caret through the screen reader, so no mouse or key event ever
            // reaches us; polling is the only way to see it. This is how the
            // third open question gets answered without a display: if the caret
            // lands where the finger pointed, a routing key is a position in the
            // book, and the whole model works.
            var watch = new Timer();
            watch.Interval = 200;
            watch.Tick += (s, e) =>
            {
                if (tbReadingSurface == null) return;
                int at = tbReadingSurface.SelectionStart;
                if (at == lastCaretSet) return;
                lastCaretSet = at;
                SurfaceLog("ROUTED to " + at + "  " + tts.SnippetAt(at, 6));
            };
            watch.Start();
        }

        private int lastCaretSet = -1;
        /// <summary>How far through the book the test aid has already spoken, as
        /// a character offset. −1 before anything.</summary>
        private int announcedTo = -1;

        /// <summary>Everything the caret has PASSED since the aid last spoke,
        /// rather than the sentence it happens to be sitting in.
        ///
        /// <para>This is the difference between an instrument and an impression.
        /// Reporting the current sentence skips any sentence shorter than the gap
        /// between two sync points — and a hybrid's points are seconds apart, so
        /// short ones vanished. Gordan cannot check the caret any other way; the
        /// aid IS how he sees it, so it has to account for every character it
        /// travelled over, neither skipping nor repeating.</para>
        ///
        /// <para>Falls back to the sentence when the position has gone BACKWARDS
        /// — a seek, or a new book — since there is no span to report then, and
        /// resyncs from there.</para></summary>
        private string TraversedSince(int start, string current)
        {
            const int Sane = 2000;      // a seek should not read a chapter aloud
            try
            {
                if (readingText == null) return current;
                if (announcedTo < 0 || start <= announcedTo || start - announcedTo > Sane)
                {
                    announcedTo = start;
                    return current;
                }
                // Whole words only. A sync point lands wherever the producer put
                // it, which is often mid-word, and "je k" followed by "ratka."
                // is not something anyone can check a reading against. The tail
                // is left where it is and goes out with the next span, so nothing
                // is lost by waiting.
                int end = start;
                while (end > announcedTo && !char.IsWhiteSpace(readingText[end - 1])) end--;
                if (end <= announcedTo) return null;     // not a whole word yet

                string span = readingText.Substring(announcedTo, end - announcedTo).Trim();
                announcedTo = end;
                return span.Length == 0 ? null : span;
            }
            catch { announcedTo = start; return current; }
        }

        /// <summary>Speaks a sentence for the TEMPORARY test aid, from the window
        /// that actually has the user's attention.
        ///
        /// <para><b>Why this is not just AnnounceToScreenReader.</b> That one
        /// caches a UIA provider taken from the PLAYER's handle, which is right
        /// for what it was built for â€” volume and speed announced while the
        /// player is in front. During a reading test the front window is the
        /// reading window, a different top level, and a notification raised on a
        /// window that is not the focused one is not announced. That, and not the
        /// selection, is why the first two attempts at this aid were silent.</para>
        ///
        /// <para>The provider is cached PER WINDOW, not globally. Globally cached
        /// was the original bug â€” the player's provider used while the reading
        /// window had focus. Rebuilt every sentence was the overcorrection:
        /// UiaHostProviderFromHwnd is a call into the reader's world, and doing
        /// it once a sentence on the UI thread delayed the start of the next
        /// one, which is what Gordan heard as the speech being cut. Keyed on the
        /// handle it was made for, it is built once per window and correct.</para></summary>
        private IntPtr diagProviderHwnd;
        private object diagProvider;
        private bool diagAnnouncedOnce;

        private void DiagnosticAnnounce(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // OFF the UI thread. Both of these are calls into the screen reader's
            // own process â€” UiaRaiseNotificationEvent especially, which is
            // cross-process COM and can block for as long as the reader feels
            // like taking. Done inline, once per sentence, that stalls the
            // message pump; and NBR's speech comes from the 32-bit satellite over
            // IPC, which needs that pump. So the player's own voice was being
            // chopped by the act of telling the reader what it was saying.
            //
            // Nothing here touches a control: the surface has already been placed
            // by the caller, and these two calls take a string and a provider
            // pointer. UIA is free-threaded, so the provider travels.
            // The handle is read HERE, on the UI thread. Touching Control.Handle
            // from a pool thread is the classic way to turn one bug into two.
            bool own = readingWindow != null && !readingWindow.IsDisposed;
            IntPtr hwnd = own ? readingWindow.Handle : this.Handle;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => AnnounceOffThread(text, hwnd, own));
        }

        private void AnnounceOffThread(string text, IntPtr hwnd, bool ownWindow)
        {
            try { AnnounceOffThreadCore(text, hwnd, ownWindow); } catch { }
        }

        private void AnnounceOffThreadCore(string text, IntPtr hwnd, bool ownWindow)
        {
            // QUEUED, not cancelling. Both channels were inherited from the
            // volume/speed announcement, where replacing the previous utterance
            // is exactly right â€” stepping volume twice should say the second
            // number, not both. For consecutive sentences of a book it is the
            // opposite: each new sentence guillotines the one still being spoken.
            // That is what Gordan heard as chopping and lost words, and it was
            // the reader being cut off, not the player.
            bool nvda = NvdaController.SpeakQueued(text);   // NVDA; silent under JAWS
            if (uiaNotifyUnavailable) { ReadingDiagnostics.Trace("  UIA marked unavailable"); return; }
            try
            {
                if (diagProvider == null || diagProviderHwnd != hwnd)
                {
                    IRawElementProviderSimple made;
                    int mhr = UiaHostProviderFromHwnd(hwnd, out made);
                    if (mhr != 0 || made == null)
                    {
                        ReadingDiagnostics.Trace(string.Format("  provider FAILED hr=0x{0:x}", mhr));
                        return;
                    }
                    diagProvider = made;
                    diagProviderHwnd = hwnd;
                }
                IRawElementProviderSimple provider = (IRawElementProviderSimple)diagProvider;
                // All, not MostRecent: MostRecent tells the reader to DISCARD
                // notifications still pending, which for a run of sentences means
                // every sentence killing its predecessor mid-word.
                int raise = UiaRaiseNotificationEvent(provider, NotificationKind.Other,
                                          NotificationProcessing.All, text, string.Empty);
                // Only the FIRST success is recorded, and only if something looks
                // wrong afterwards do the failure traces above matter. Logging
                // every sentence is a disk write per sentence, which is the fault
                // this whole round has been chasing.
                if (!diagAnnouncedOnce)
                {
                    diagAnnouncedOnce = true;
                    ReadingDiagnostics.Trace(string.Format(
                        "  announcing: nvda={0} window={1} raiseHr=0x{2:x}",
                        nvda, ownWindow ? "reading" : "player", raise));
                }
            }
            catch (Exception ex)
            {
                uiaNotifyUnavailable = true;
                ReadingDiagnostics.Trace("  UIA THREW: " + ex.Message);
            }
        }

        /// <summary>Left over from the braille-lag measurement, and now behind the
        /// diagnostics switch.
        ///
        /// <para>It appends to a file on disk, synchronously, on the UI thread,
        /// once per sentence â€” for every reader, forever, whether anyone was
        /// measuring or not. Gordan heard the speech start to chop. Scaffolding
        /// from a finished experiment should not be doing I/O in everybody's
        /// playback.</para></summary>
        private void SurfaceLog(string line)
        {
            if (!ReadingDiagnostics.Highlight) return;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NBR-reading-surface.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Puts the WHOLE book in the surface, once. Everything after that
        /// is a change of selection, not of text.</summary>
        private void LoadReadingSurface()
        {
            if (tbReadingSurface == null || readingText == null) return;
            tbReadingSurface.Text = readingText;
            lastSurfaceStart = -1;
            UpdateReadingSurface();
        }

        private int lastSurfaceStart = -1;

        /// <summary>Moves the selection onto the sentence being read.
        /// <para><b>Rewriting Text does not reach braille â€” measured.</b> In 35
        /// seconds the surface went through some twenty sentences while the
        /// display sat on the one that was current when focus arrived, and never
        /// moved. So the text is now written once and only the SELECTION travels:
        /// that is the thing a screen reader is built to follow, and it is also
        /// what makes panning meaningful (the rest of the book is really there to
        /// pan into) and turns a routing key into a position in the book rather
        /// than an index into one lonely sentence.</para></summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>Keeps the book on the braille display when focus has wandered
        /// off the reading surface but is still somewhere in NBR.
        ///
        /// <para>Braille follows FOCUS â€” that is the platform's model, not our
        /// workaround, and it is what gives the surface panning and routing keys.
        /// But it means a stray Tab onto a player key leaves speech reading on
        /// while the display sits on "Forward, Shift+Right" (Gordan's example).
        /// While the surface holds focus this does nothing: the reader is already
        /// doing a better job of it and pushing over the top would only
        /// flicker.</para>
        ///
        /// <para><b>Only while NBR is the foreground application.</b> When a
        /// Windows Update prompt or anything else takes over, winning the display
        /// back would stop the reader reading the thing that just interrupted
        /// them â€” which is exactly what they need at that moment. So that case is
        /// deliberately left alone; it is not an oversight.</para>
        ///
        /// <para>NVDA only, and silently nothing on JAWS, which has no public
        /// braille call. There, focus tracking remains the whole story.</para></summary>
        private void PushBrailleIfFocusLeft(string sentence)
        {
            try
            {
                if (string.IsNullOrEmpty(sentence)) return;
                if (GetForegroundWindow() != Handle
                    && (readingWindow == null || readingWindow.IsDisposed
                        || GetForegroundWindow() != readingWindow.Handle)) return;
                if (tbReadingSurface != null && tbReadingSurface.Focused) return;
                // Off the UI thread, for the same reason the announcement is.
                // nvdaController_brailleMessage is an RPC into NVDA's process, and
                // this runs ONCE PER SENTENCE â€” with the aid off as well, which is
                // why turning the aid off did not stop the chopping. NBR's own
                // speech comes from the 32-bit satellite over IPC and needs the
                // message pump; anything that blocks the pump between sentences
                // comes out of the reading.
                System.Threading.ThreadPool.QueueUserWorkItem(
                    _ => { try { NvdaController.Braille(sentence); } catch { } });
            }
            catch { }
        }

        /// <summary>The sentence the given offset falls in. A hybrid has no TTS
        /// reader to ask, and the braille push and the log both want the words
        /// rather than the number. Sentence enough for that: the surface caret is
        /// what actually carries the reading, and this only has to describe
        /// it.</summary>
        private static string SentenceAround(string text, int at)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (at < 0) at = 0;
            if (at >= text.Length) at = text.Length - 1;
            int from = text.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, at) + 1;
            int to = text.IndexOfAny(new[] { '.', '!', '?', '\n' }, at);
            if (to < 0) to = text.Length - 1;
            return text.Substring(from, to - from + 1).Trim();
        }

        private void UpdateReadingSurface()
        {
            // TEMPORARY trace â€” the first link in the chain, and the one that
            // would explain total silence: if the surface does not exist, or the
            // book has no text loaded, this returns before anything can speak and
            // every fix downstream is a fix to something that never runs.
            if (tbReadingSurface == null || readingText == null || currentBook == null)
            {
                ReadingDiagnostics.Trace(string.Format(
                    "UpdateReadingSurface EARLY RETURN: surface={0} text={1} book={2}",
                    tbReadingSurface == null ? "null" : "ok",
                    readingText == null ? "null" : readingText.Length.ToString(),
                    currentBook == null ? "null" : "ok"));
                return;
            }

            // Where the reading IS, asked of whatever is doing the reading. For a
            // text book that is the TTS engine. For a hybrid it is the narration:
            // the audio clock through the book's own sync map, which is what that
            // map was built for (Â§8c) and the reason a hybrid can show text at
            // all without a synthesiser in the picture.
            string s; int start;
            if (currentBook.IsHybrid)
            {
                // Through LoadSyncMap, which returns the cached map after the
                // first call — reading the property alone was the bug above.
                SyncMap sync = currentBook.LoadSyncMap();
                if (sync == null || sync.IsEmpty) return;
                start = DaisySync.CharAt(sync, GetVirtualPosition());
                s = SentenceAround(readingText, start);
            }
            else if (currentBook.IsTextBook && tts != null)
            {
                s = tts.CurrentText ?? "";
                start = tts.CharPosition;
            }
            else return;
            if (start == lastSurfaceStart) return;
            lastSurfaceStart = start;
            if (start < 0 || start > tbReadingSurface.TextLength) return;
            // The CARET moves, nothing is selected. Selecting the sentence did
            // carry braille perfectly, but a screen reader treats a selection as
            // news: Gordan heard it reading the marked text out, announcing
            // "selected" and "not selected", and repeating pieces â€” over our own
            // speech. Braille shows the line around the caret either way, so the
            // output is unchanged and the commentary has nothing to report.
            // Normally the caret alone, exactly as before. ReadingDiagnostics is a
            // TEMPORARY test aid (see that file) which selects the sentence
            // instead, so a tester with no braille display can HEAR the mechanism
            // through their screen reader. Off unless switched on; delete the file
            // and this line to remove it.
            ReadingDiagnostics.Place(tbReadingSurface, start, s);
            // Selecting the sentence does NOT make a reader announce it â€” measured,
            // and the reason is that the text is written once now and only the
            // selection travels, so there is no change event of the kind that
            // spoke last time. The aid therefore SAYS the sentence, through the
            // same two channels Â§11 already uses (UIA notification for JAWS, the
            // NVDA client for NVDA), each a no-op under the other reader.
            //
            // Note what this does and does not prove: it is NBR pushing the text
            // to the reader, not the reader picking it up by following focus. It
            // shows WHAT would reach a display and WHEN â€” which is the question
            // being asked â€” but it is not itself the focus path.
            // No per-sentence trace any more. It found what it was for â€” that the
            // chain WAS being reached â€” and while the aid is on is precisely when
            // a disk write per sentence hurts, which is the whole complaint.
            // Announce a SENTENCE once, however many times the position moves
            // inside it. A hybrid's caret steps on every sync point — 3 674 of
            // them in Gordan's French book, one every second or two — and each
            // step was queueing the whole sentence again. Queued rather than
            // cancelling (which is right, or they cut each other off), the queue
            // simply grew, and what came out was a few words every few seconds,
            // further behind the narrator every time.
            //
            // The caret still moves on every point, which is what braille and the
            // reading position want. Only the speaking is deduplicated.
            if (ReadingDiagnostics.Highlight)
            {
                string say = TraversedSince(start, s);
                if (!string.IsNullOrEmpty(say)) DiagnosticAnnounce(say);
            }
            lastCaretSet = tbReadingSurface.SelectionStart;   // ours, not a routing key
            // The per-sentence "SENT" stamp is gone. It belonged to the braille-lag
            // experiment, which is finished, and it is a file opened, appended and
            // closed inside the sentence loop â€” the exact cost that has been
            // chopping the speech. SurfaceLog survives for routing keys, which
            // happen when a hand moves, not four times a minute.
            PushBrailleIfFocusLeft(s);
            // NOT Select(0, 0) here, deliberately. Putting the caret back on nought
            // after every sentence is a caret MOVE as far as the reader is
            // concerned, and it answers by speaking the character underneath â€”
            // which is the first letter of the new sentence, and always a capital.
            // Gordan heard it as "random capital letters" over the reading.
            //
            // It also happens to be the open question about the braille lag: if
            // braille was only refreshing on that caret event, dropping it will
            // stop braille following at all, and then we know exactly what drives
            // it. Either way this tells us something the guessing could not.
        }

        /// <summary>Says that this book cannot be read and what to do about it.
        /// The fallback for when the dialog is not the right answer â€” the reader
        /// has already declined once and is only being reminded.</summary>
        private void AnnounceNoVoice()
        {
            string lang = currentBook != null ? currentBook.TextLanguage : "";
            string name = SettingsForm.LanguageName(LanguageDetector.Primary(lang));
            tones.Play(300, 150);
            AnnounceToScreenReader(lblAnnounceInfo,
                Localization.T("Player.NoVoiceForLanguage", name));
        }

        /// <summary>Whether this book can be read, asking about it if it cannot.
        /// Returns false only when the reader declined â€” and then nothing has been
        /// touched, so the caller must simply not load it.
        /// <para>Answered without loading anything: the language comes off the
        /// shelf with the book. A book imported before languages were detected has
        /// none recorded yet, so it is let through and worked out during the load â€”
        /// once, since the load then saves it.</para></summary>
        private bool EnsureVoiceForBook(BookData book)
        {
            if (book == null || !book.IsTextBook) return true;
            if (!string.IsNullOrEmpty(book.TextVoice)) return true;
            string lang = book.TextLanguage;
            if (string.IsNullOrEmpty(lang)) return true;

            EnsureTts();
            List<(string Name, string Vendor, string Language)> voices;
            try { voices = tts.GetVoiceInfos(); }
            catch { return true; }

            VoiceSource how;
            VoiceChooser.ForLanguage(appSettings, voices, lang, out how);
            if (how != VoiceSource.NoVoice) return true;

            string chosen = "";
            using (var dlg = new NoVoiceForm(lang, voices))
                if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.ChosenVoice;

            // Declining writes nothing anywhere. The next activation asks again,
            // exactly as if it were the first, because it is another attempt to
            // read the book rather than a repeat of a decision already made.
            if (string.IsNullOrEmpty(chosen)) return false;

            book.TextVoice = chosen;
            try { book.Save(); } catch { }
            return true;
        }

        /// <summary>Puts the question for the book ALREADY loaded â€” the safety net
        /// for a voice that goes away mid-session, and for pressing Play on a book
        /// that cannot be read. Returns true when a voice was chosen.</summary>
        /// <para>A dialog rather than an announcement, because this is not news to
        /// be caught in passing â€” it is a state that has to be acknowledged, and
        /// one a reader who cannot hear the announcement would otherwise never
        /// learn about at all (Gordan, 2026-07-29: universal design).</para></summary>
        private bool AskForVoice()
        {
            if (currentBook == null || tts == null) return false;
            List<(string Name, string Vendor, string Language)> voices;
            try { voices = tts.GetVoiceInfos(); }
            catch { return false; }

            string chosen = "";
            using (var dlg = new NoVoiceForm(currentBook.TextLanguage, voices))
                if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.ChosenVoice;

            // Declining changes nothing and is written nowhere. The book stays on
            // the shelf as it was, and asking again next time is the point.
            if (string.IsNullOrEmpty(chosen)) return false;

            // Chosen for THIS book, which is the only scope this dialog has.
            currentBook.TextVoice = chosen;
            try { currentBook.Save(); } catch { }
            ApplyTtsSettings();
            UpdateTitleBar();
            if (textNoVoice) return false;

            // It can be read now, so it becomes the book you are reading â€”
            // the step the load skipped when it could not be read. Both routes
            // into here need it, which is why it lives here and not at either
            // call site.
            appSettings.SetLastOpenedBook(currentBook.FolderPath);
            return true;
        }

        private void ApplyTtsSettings()
        {
            if (tts == null) return;

            // Silent reading, chosen in Properties. Nothing about a voice applies,
            // and asking for one would start the 32-bit speech host to say
            // nothing. Speed still applies â€” it is what paces the reading now.
            if (currentBook != null && currentBook.TextNoSpeech)
            {
                tts.SilentWpm = currentBook.TextWpm >= 0 ? currentBook.TextWpm : appSettings.TtsWpm;
                tts.Silent = true;
                textNoVoice = false;      // not a failure to find a voice: a choice
                return;
            }
            tts.Silent = false;

            // A book can carry its own voice (its Properties); where it doesn't, the
            // Settings default applies â€” unless the book is in a language that
            // default doesn't speak. The speed/volume/pitch then follow THAT voice â€”
            // remembered per voice, so a change of voice or engine never drags the
            // previous one's numbers along.
            string voice;
            if (currentBook != null && !string.IsNullOrEmpty(currentBook.TextVoice))
            {
                voice = currentBook.TextVoice;      // chosen by hand for this book
                textNoVoice = false;
            }
            else
            {
                VoiceSource how;
                voice = DefaultVoiceForBook(out how);
                textNoVoice = how == VoiceSource.NoVoice;
            }

            // Nothing installed speaks this book's language. Leave the reader
            // exactly as it was and touch NOTHING: an empty name does not clear a
            // voice, it leaves whatever spoke last, which is how a Spanish book
            // came to be read aloud in Croatian the moment it was opened. The
            // book waits, and LoadTextBookPlayback says why.
            //
            // Unless the book has an output that is not speech. Then silence is a
            // working way to read rather than a dead end: the words are on the
            // screen or under the fingers, and all that was missing was something
            // to turn the page. So the player takes No speech itself, which is
            // the automatic half of what that option is for (Gordan).
            //
            // Deliberately NOT done when there is no such output: a book with no
            // voice and nothing else showing it would advance through silence
            // with nothing to show for it, and there the existing question â€”
            // borrow a voice? â€” is the right answer and stays.
            if (textNoVoice && currentBook != null && currentBook.OpensReadingWindow)
            {
                tts.SilentWpm = currentBook.TextWpm >= 0 ? currentBook.TextWpm : appSettings.TtsWpm;
                tts.Silent = true;
                return;
            }
            if (textNoVoice) return;

            VoicePrefs p = ResolveVoicePrefs(voice);
            currentWpm = p.Wpm;
            currentVolume = p.Volume;
            currentTextPitch = p.Pitch;

            tts.SetVoice(voice);
            // Whatever the user has put in their own dictionary for this voice and
            // this language â€” nothing at all unless they wrote it themselves.
            tts.Dictionaries = SpeechDictionaries.Active(
                !string.IsNullOrEmpty(tts.CurrentVoice) ? tts.CurrentVoice : voice,
                currentBook != null ? currentBook.TextLanguage : "");
            tts.SetPitch(currentTextPitch * 5); // -10..10 â†’ -50..50 %
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
        // reading speed (nominal words-per-minute â†’ characters per minute).
        private double TextSeconds(int chars)
        {
            int cpm = TtsReader.CharsPerMinute(currentWpm);
            return cpm > 0 ? chars * 60.0 / cpm : 0;
        }

        private DateTime lastTextDisplay = DateTime.MinValue;
        private string lastTextPosText;

        /// <summary>Progress, caption and info box for a text book.
        ///
        /// <para><b>Throttled, and it matters more than it looks.</b> This is
        /// driven by PositionChanged, so it used to run once per SENTENCE â€” and
        /// it does not merely read: it assigns to a text box, an accessible name,
        /// a label, the window caption, and it rebuilds the WHOLE info box and
        /// assigns that too. Every one of those raises a UI Automation property
        /// change that the screen reader collects, across a process boundary, in
        /// the same instant the next sentence is starting. NBR's own voice comes
        /// from the 32-bit satellite over IPC and needs the message pump right
        /// then; Gordan heard the beginning of sentences being stolen.</para>
        ///
        /// <para>The display is in whole seconds, so once a second is all it can
        /// show anyway â€” a sentence takes two or three. The identical-text guards
        /// are worth as much again: an assignment of the SAME string still raises
        /// the event, and the info box in particular is a long string that
        /// usually has not changed.</para></summary>
        private void UpdateTextPositionDisplay()
        {
            if (tts == null || currentBook == null || !currentBook.IsTextBook) return;
            DateTime now = DateTime.UtcNow;
            if ((now - lastTextDisplay).TotalMilliseconds < 900) return;
            lastTextDisplay = now;

            int percent = tts.TotalChars > 0 ? (int)(100.0 * tts.CharPosition / tts.TotalChars) : 0;
            string posText = Localization.T("Player.Position.Text",
                FormatTime(TextSeconds(tts.CharPosition)), FormatTime(TextSeconds(tts.TotalChars)));
            if (posText != lastTextPosText)
            {
                lastTextPosText = posText;
                tbProgress.Text = posText;
                tbProgress.AccessibleName = Localization.T("Player.Position.Accessible", percent);
                lblProgress.Text = posText;
                // Live title bar + info box (same rule as audio: caption always,
                // info box only while unfocused) so text progress advances visibly.
                UpdateTitleBar();
                if (this.ActiveControl != tbInfo)
                {
                    string info = BuildCurrentInfoText();
                    if (info != tbInfo.Text) tbInfo.Text = info;
                }
            }
        }

        private void LoadBook(BookData book, bool autoPlay)
        {
            // A book in a language nothing speaks is not put into the player AT
            // ALL (Gordan, 2026-07-29). The question therefore comes before
            // anything is swapped: declining leaves whatever was loaded exactly as
            // it was and the title stays on the shelf, which is what "not loaded"
            // has to mean. It used to be asked after the load, which left a book
            // nobody could read sitting in the player.
            if (!EnsureVoiceForBook(book)) return;

            // Changing the book ends the previous listening session â€” an
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
            // back to the first â€” and largest â€” step (H1 / Part).
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
            readingWindowOffered = false;

            // Whatever the last book left behind is not this book. Cleared before
            // either branch so no path can forget to.
            readingText = null;

            // Text book â†’ read it with TTS instead of building an mpv playlist.
            if (currentBook.IsTextBook)
            {
                LoadTextBookPlayback(autoPlay);
                return;
            }

            // A hybrid keeps the audio transport but has the words too, so the
            // reading window works here exactly as it does for a text book â€” the
            // narration drives it instead of a synthesiser.
            if (currentBook.IsHybrid) LoadHybridReadingText();

            string[] audioExts = { ".mp3", ".ogg", ".flac", ".m4a", ".m4b", ".wav", ".opus", ".aac", ".wma" };
            var playlist = new List<string>();

            // Play in the book's chapter order, not a fresh alphabetical sort:
            // for plain audiobooks the two match, but DAISY audio is ordered by
            // its navigation (BuildChaptersFromDaisy), which is not always the
            // filename order â€” and playback must match the virtual timeline the
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
            // Always start paused â€” prevents an audible "plays then jumps"
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Loading a playlist
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private string BuildFileFilter()
        {
            return
                Localization.T("Filter.Audiobooks") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc|" +
                Localization.T("Filter.TextBooks") + "|*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra;*.i55;*.dxb|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc;*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra;*.i55;*.dxb;*.zip;*.rar;*.7z;*.001;*.z01|" +
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
        /// archive's file name â€” no temp staging) and loaded like any other
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
                    // Text+audio DAISY: keep the text as a second output too.
                    DaisyTextExtractor.SetupHybrid(book, destFolder, daisy);
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
                // User cancelled the archive password prompt â€” undo the empty
                // folder, no error dialog.
                if (createdFolder) TryDeleteFolder(destFolder);
            }
            catch (Exception ex)
            {
                if (createdFolder) TryDeleteFolder(destFolder);
                MessageForm.ShowInfo(this, Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // MPV helpers
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
                // selection outright â€” otherwise mpv pops a window showing the
                // cover image.
                mpv_set_property_string(mpvHandle, "audio-display", "no");
                mpv_set_property_string(mpvHandle, "vid", "no");
                // Output device chosen in Settings â†’ Device (empty = mpv "auto").
                if (appSettings != null && !string.IsNullOrEmpty(appSettings.AudioDevice))
                    mpv_set_property_string(mpvHandle, "audio-device", appSettings.AudioDevice);
            }
            catch (Exception ex)
            {
                MessageForm.ShowInfo(this, Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
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



