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
        // Handled locally: the keys work while any NBR window control has
        // focus. A future Settings option may add a global mode
        // (RegisterHotKey) and an off switch.
        private const int WM_APPCOMMAND = 0x0319;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        private const int APPCOMMAND_MEDIA_PLAY = 46;
        private const int APPCOMMAND_MEDIA_PAUSE = 47;

        private IntPtr mpvHandle = IntPtr.Zero;
        private bool isPlaying = false;
        private string currentFile = null;
        private BookData currentBook = null;
        private AppSettings appSettings;
        private System.Windows.Forms.Timer eventTimer;
        private System.Windows.Forms.Timer progressTimer;

        private int currentVolume = 100;
        private int currentSpeed = 100;
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
        // window. When no file is loaded the message is passed through to
        // the system (base.WndProc), so pressing Play/Pause with an empty
        // player doesn't pop up the Open File dialog and other apps can
        // still react.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APPCOMMAND && currentFile != null)
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
            base.WndProc(ref m);
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
        /// <summary>Seconds for the currently selected time step (indices 0–3).</summary>
        private int GetSeekStepSeconds()
        {
            switch (cmbSeek.SelectedIndex)
            {
                case 0: return 15;
                case 1: return 30;
                case 2: return 60;
                case 3: return 300;
                default: return 15;
            }
        }

        /// <summary>True when the "Part" step is selected in the seek dropdown.</summary>
        private bool IsSeekStepPart()
        {
            return cmbSeek.SelectedIndex == 4;
        }

        /// <summary>True when the "Bookmark" step is selected. That option
        /// only exists in the dropdown while the current book has at least
        /// one bookmark — see UpdateSeekStepBookmarkOption.</summary>
        private bool IsSeekStepBookmark()
        {
            return cmbSeek.SelectedIndex == 5;
        }

        private void SeekStepForward()
        {
            if (IsSeekStepPart())
                PartForward();
            else if (IsSeekStepBookmark())
                BookmarkForward();
            else
                SeekRelative(+GetSeekStepSeconds());
        }

        private void SeekStepBackward()
        {
            if (IsSeekStepPart())
                PartBack();
            else if (IsSeekStepBookmark())
                BookmarkBack();
            else
                SeekRelative(-GetSeekStepSeconds());
        }

        /// <summary>Shift+Up / Shift+Down: cycles the seek dropdown's selected
        /// step and announces the new value, from anywhere in the window. The
        /// dropdown can still be changed directly when it has focus.</summary>
        private void ChangeSeekStep(int delta)
        {
            int newIndex = Math.Max(0, Math.Min(cmbSeek.Items.Count - 1, cmbSeek.SelectedIndex + delta));
            cmbSeek.SelectedIndex = newIndex;
            AnnounceToScreenReader(lblAnnounceInfo, Localization.T("Player.Seek.Announce", cmbSeek.Text));
        }

        /// <summary>Adds or removes the "Bookmark" seek-step option (always
        /// the last item, right after "Part") to match whether the current
        /// book has any bookmarks. Called whenever the book or its bookmark
        /// list changes.</summary>
        private void UpdateSeekStepBookmarkOption()
        {
            bool shouldShow = currentBook != null && currentBook.Bookmarks.Count > 0;
            bool isShown = cmbSeek.Items.Count > 5;

            if (shouldShow && !isShown)
            {
                cmbSeek.Items.Add(Localization.T("Seek.Item.Bookmark"));
            }
            else if (!shouldShow && isShown)
            {
                if (cmbSeek.SelectedIndex == 5)
                    cmbSeek.SelectedIndex = 0;
                cmbSeek.Items.RemoveAt(5);
            }
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
                    if (!infoBoxHasFocus) { SeekRelative(+5); return true; }
                    break;

                case Keys.Left:
                    if (!infoBoxHasFocus) { SeekRelative(-5); return true; }
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
                case Keys.Shift | Keys.Up:
                    ChangeSeekStep(+1);
                    return true;

                case Keys.Shift | Keys.Down:
                    ChangeSeekStep(-1);
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

            if (mpvHandle != IntPtr.Zero)
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
                Console.Beep(300, 150);
            else if (currentVolume == 100)
                Console.Beep(1200, 150);
        }

        // ──────────────────────────────────────────────
        // Speed
        // ──────────────────────────────────────────────
        private void ChangeSpeed(int delta)
        {
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
            {
                Console.Beep(800, 120);
                Console.Beep(800, 120);
            }
        }

        // ──────────────────────────────────────────────
        // Seek
        // ──────────────────────────────────────────────
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
                // Different file — playlist-play-index + seek after it loads
                MpvCommand("playlist-play-index", targetIndex.ToString());
                if (!isPlaying)
                    mpv_set_property_string(mpvHandle, "pause", "yes");

                System.Windows.Forms.Timer seekTimer = new System.Windows.Forms.Timer();
                seekTimer.Interval = 300;
                seekTimer.Tick += (s, ev) =>
                {
                    seekTimer.Stop();
                    seekTimer.Dispose();
                    MpvCommand("seek", seekPos.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
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
            string zero = FormatTime(0);
            return
                Localization.T("Player.Info.TitleLabel") + " " + dash + "\r\n" +
                Localization.T("Player.Info.ChapterLabel") + " " + dash + "\r\n" +
                "\r\n" +
                Localization.T("Player.Info.ElapsedSegmentLabel") + " " + zero + "\r\n" +
                Localization.T("Player.Info.ElapsedTotalLabel") + " " + zero + "\r\n" +
                Localization.T("Player.Info.RemainingSegmentLabel") + " -" + zero + "\r\n" +
                Localization.T("Player.Info.RemainingTotalLabel") + " -" + zero;
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
            cmbSeek.Items.Add(Localization.T("Seek.Item.15s"));
            cmbSeek.Items.Add(Localization.T("Seek.Item.30s"));
            cmbSeek.Items.Add(Localization.T("Seek.Item.1min"));
            cmbSeek.Items.Add(Localization.T("Seek.Item.5min"));
            cmbSeek.Items.Add(Localization.T("Seek.Item.Part"));
            cmbSeek.SelectedIndex = 0;
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
            btnProperties.AccessibleName = Localization.T("Btn.Properties");
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

                // Note: the info box is deliberately NOT updated here — it's
                // an on-focus/on-demand snapshot (see BuildTopPanel).
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
            // Ctrl+G without a book: a short low beep.
            if (currentFile == null)
            {
                Console.Beep(300, 150);
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
                if (isPlaying)
                {
                    mpv_set_property_string(mpvHandle, "pause", "yes");
                    SetPlayPauseState(false);
                }
                CancelSleepTimer(true);
                return;
            }

            // Opening the dialog itself pauses playback, if it was running,
            // so the dialog is never modal-over-audible-playback. Confirming
            // always resumes via StartSleepTimer; cancelling resumes only if
            // playback was actually running before the dialog opened.
            bool wasPlaying = isPlaying;
            if (wasPlaying)
            {
                mpv_set_property_string(mpvHandle, "pause", "yes");
                SetPlayPauseState(false);
            }

            using (SleepTimerForm dlg = new SleepTimerForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    StartSleepTimer(dlg.SelectedMinutes, dlg.SelectedAction);
                }
                else if (wasPlaying)
                {
                    mpv_set_property_string(mpvHandle, "pause", "no");
                    SetPlayPauseState(true);
                }
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
            // paused, it begins now. (Direct mpv call — deliberately NOT
            // BtnPlayPause_Click, which is reserved for user-initiated
            // toggles and carries the cancel hook.)
            if (!isPlaying)
            {
                mpv_set_property_string(mpvHandle, "pause", "no");
                SetPlayPauseState(true);
            }

            AnnounceToScreenReader(lblAnnounceInfo,
                Localization.T("SleepTimer.Announce.Set", minutes));
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
                Console.Beep(900, 120);
                Console.Beep(900, 120);
                Console.Beep(900, 120);
            }

            // Final stretch: a smooth volume fadeout over the last
            // SleepFadeSeconds (45 s). Linear ramp from the user's set
            // volume down to 0 at the deadline, applied straight to mpv —
            // currentVolume and the UI stay untouched, so the saved volume
            // and the Volume field never see the faded values. One step
            // per tick (1 s) is plenty smooth for a ~45-step ramp.
            if (sec <= SleepFadeSeconds && isPlaying && mpvHandle != IntPtr.Zero)
            {
                int fadedVolume = (int)Math.Round(currentVolume * (sec / (double)SleepFadeSeconds));
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
            if (isPlaying)
            {
                mpv_set_property_string(mpvHandle, "pause", "yes");
                SetPlayPauseState(false);
            }

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
        private string BuildInfoBoxText(double segPosition, double segDuration, double segRemaining,
            double virtualPos, double totalDur, double virtualRemaining)
        {
            string dash = Localization.T("Common.Dash");
            string titleText = currentBook != null ? currentBook.Title :
                (currentFile != null ? System.IO.Path.GetFileNameWithoutExtension(currentFile) : dash);

            string chapterText = dash;
            if (currentBook != null && currentBook.Chapters.Count > 0
                && currentPlaylistIndex < currentBook.Chapters.Count)
            {
                chapterText = (currentPlaylistIndex + 1) + "/" + currentBook.Chapters.Count;

                // For classic multi-file audio, also show the current file
                // name (without extension) — info box only, the title bar
                // would get too crowded.
                if (currentBook.Chapters.Count > 1)
                {
                    string partName = System.IO.Path.GetFileNameWithoutExtension(
                        currentBook.Chapters[currentPlaylistIndex].FileName);
                    chapterText += " — " + partName;
                }
            }

            return
                Localization.T("Player.Info.TitleLabel") + " " + titleText + "\r\n" +
                Localization.T("Player.Info.ChapterLabel") + " " + chapterText + "\r\n" +
                "\r\n" +
                Localization.T("Player.Info.ElapsedSegmentLabel") + " " + FormatTime(segPosition) + "\r\n" +
                Localization.T("Player.Info.ElapsedTotalLabel") + " " + FormatTime(virtualPos) + "\r\n" +
                Localization.T("Player.Info.RemainingSegmentLabel") + " -" + FormatTime(segRemaining) + "\r\n" +
                Localization.T("Player.Info.RemainingTotalLabel") + " -" + FormatTime(virtualRemaining);
        }

        /// <summary>
        /// Builds the info text for the current playback moment — used by
        /// the on-focus snapshot and the I key. Returns the placeholder
        /// when nothing is loaded.
        /// </summary>
        private string BuildCurrentInfoText()
        {
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

            if (currentBook != null)
            {
                string chapterText = "";
                if (currentBook.Chapters.Count > 0 && currentPlaylistIndex < currentBook.Chapters.Count)
                    chapterText = Localization.T("Player.TitleBar.Chapter", currentPlaylistIndex + 1, currentBook.Chapters.Count);

                string stateText = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");

                this.Text = appName + " — " + currentBook.Title + chapterText + stateText;
            }
            else if (currentFile != null)
            {
                string stateText = isPlaying ? Localization.T("Player.TitleBar.Playing") : Localization.T("Player.TitleBar.Paused");
                this.Text = appName + " — " + System.IO.Path.GetFileNameWithoutExtension(currentFile) + stateText;
            }
            else
            {
                this.Text = appName;
            }
        }

        // ──────────────────────────────────────────────
        // Saving progress
        // ──────────────────────────────────────────────
        private void SaveCurrentBookProgress()
        {
            if (currentBook == null) return;
            try
            {
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
                currentBook.SeekStep = cmbSeek.SelectedIndex;
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
            UpdateSeekStepBookmarkOption();
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
        private void BookmarkBack()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                Console.Beep(300, 150);
                return;
            }

            double pos = GetVirtualPosition();
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
                Console.Beep(300, 150);
                return;
            }

            if (currentIndex == 0 || pos - currentBook.Bookmarks[currentIndex] > 3.0)
                SeekToVirtualPosition(currentBook.Bookmarks[currentIndex]);
            else
                SeekToVirtualPosition(currentBook.Bookmarks[currentIndex - 1]);
        }

        private void BookmarkForward()
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                Console.Beep(300, 150);
                return;
            }

            double pos = GetVirtualPosition();
            foreach (double bookmark in currentBook.Bookmarks)
            {
                if (bookmark > pos)
                {
                    SeekToVirtualPosition(bookmark);
                    return;
                }
            }

            // Already past the last bookmark.
            Console.Beep(300, 150);
        }

        private void BtnLibrary_Click(object sender, EventArgs e)
        {
            if (isLibraryOpen) return;

            SaveCurrentBookProgress();

            isLibraryOpen = true;
            try
            {
                using (LibraryForm libraryForm = new LibraryForm(appSettings, currentBook != null ? currentBook.FolderPath : null))
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
            using (SettingsForm dlg = new SettingsForm(appSettings))
            {
                dlg.ShowDialog(this);
            }
        }

        private void BtnHelp_Click(object sender, EventArgs e)
        {
            MessageBox.Show(Localization.T("Dialog.Help.ComingSoon"), Localization.T("Dialog.Help.Title"));
        }

        private void BtnProperties_Click(object sender, EventArgs e)
        {
            MessageBox.Show(Localization.T("Dialog.Properties.ComingSoon"), Localization.T("Dialog.Properties.Title"));
        }

        private void BtnGoTo_Click(object sender, EventArgs e)
        {
            // Plain audio: a list of the book's parts. DAISY/text structure
            // (headings, pages) will plug in here as a separate subsystem.
            if (currentBook == null || currentBook.Chapters.Count == 0)
            {
                // No book loaded — a short low beep as audible feedback.
                Console.Beep(300, 150);
                return;
            }

            string[] names = new string[currentBook.Chapters.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = System.IO.Path.GetFileNameWithoutExtension(
                    currentBook.Chapters[i].FileName);

            using (GoToForm dlg = new GoToForm(names, currentPlaylistIndex, appSettings.GoToAutoPlay))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPartIndex >= 0)
                {
                    // Jump to the start of the selected part. Default:
                    // playback state is preserved (paused stays paused,
                    // playing keeps playing). With the auto-play checkbox
                    // checked, playback starts after the jump. Starting is
                    // done in the onComplete callback — after the delayed
                    // cross-file seek — to avoid an audible blip of the
                    // part's beginning before the seek lands.
                    bool autoPlay = dlg.AutoPlayChecked;

                    // Remembered globally (Settings.ini): if auto-play suits
                    // the user on book A, it'll suit them on B and C too.
                    // Saved on confirm only — Cancel discards the change.
                    appSettings.SetGoToAutoPlay(autoPlay);
                    SeekToVirtualPosition(currentBook.Offsets[dlg.SelectedPartIndex], () =>
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
                Console.Beep(300, 150);
                return;
            }

            currentBook.AddBookmark(GetVirtualPosition());
            UpdateSeekStepBookmarkOption();

            // Ascending series of five short beeps (~1 second total) — a
            // bit more attention-grabbing than the plain "no go" beep, since
            // this confirms a successful action rather than a blocked one.
            int[] freqs = { 500, 650, 800, 950, 1100 };
            foreach (int freq in freqs)
                Console.Beep(freq, 200);

            // Deliberately no position/percent details here — TMI for a
            // one-key command; the Manage Bookmarks list is where that
            // detail belongs.
            AnnounceToScreenReader(lblAnnounceInfo, Localization.T("Bookmark.Announce.Set"));
        }

        private void BtnManageBookmarks_Click(object sender, EventArgs e)
        {
            if (currentBook == null || currentBook.Bookmarks.Count == 0)
            {
                Console.Beep(300, 150);
                return;
            }

            // Opening the dialog pauses playback (if running), same coupling
            // as the Sleep Timer dialog — a direct mpv call, so it does not
            // touch an active Sleep Timer.
            bool wasPlaying = isPlaying;
            if (wasPlaying)
            {
                mpv_set_property_string(mpvHandle, "pause", "yes");
                SetPlayPauseState(false);
            }

            using (ManageBookmarksForm dlg = new ManageBookmarksForm(currentBook.Bookmarks))
            {
                DialogResult result = dlg.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    currentBook.SetBookmarks(dlg.ResultBookmarks);
                    UpdateSeekStepBookmarkOption();

                    if (dlg.PlayIndex >= 0)
                    {
                        // OK confirmed with a bookmark selected: jump there
                        // and make sure playback continues from there.
                        double pos = dlg.ResultBookmarks[dlg.PlayIndex];
                        SeekToVirtualPosition(pos, () =>
                        {
                            if (!isPlaying)
                            {
                                mpv_set_property_string(mpvHandle, "pause", "no");
                                SetPlayPauseState(true);
                            }
                        });
                        return;
                    }
                }

                // Plain OK (edits only, no jump) or Cancel: restore exactly
                // the playback state from before the dialog opened.
                if (wasPlaying)
                {
                    mpv_set_property_string(mpvHandle, "pause", "no");
                    SetPlayPauseState(true);
                }
            }
        }

        // ──────────────────────────────────────────────
        // Loading a book (from the library or at startup)
        // ──────────────────────────────────────────────
        private void LoadBook(BookData book, bool autoPlay)
        {
            // Changing the book ends the previous listening session — an
            // active sleep timer is cancelled, with the same announcement
            // as a manual pause. (At startup no timer can be active, so
            // this only ever fires on a library pick.)
            if (sleepTimerActive)
                CancelSleepTimer(true);

            currentBook = book;
            UpdateSeekStepBookmarkOption();
            // Restore the book's saved seek step, clamped in case the range
            // shrank since it was saved (e.g. the Bookmark option is gone
            // because this book has no bookmarks).
            cmbSeek.SelectedIndex = Math.Max(0, Math.Min(cmbSeek.Items.Count - 1, currentBook.SeekStep));

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

            string[] audioExts = { ".mp3", ".ogg", ".flac", ".m4a", ".m4b", ".wav", ".opus", ".aac", ".wma" };
            var playlist = new List<string>();
            string[] allFiles = System.IO.Directory.GetFiles(currentBook.FolderPath);
            Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string f in allFiles)
            {
                string ext = System.IO.Path.GetExtension(f).ToLower();
                if (Array.IndexOf(audioExts, ext) >= 0)
                    playlist.Add(f);
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
                Localization.T("Filter.Audiobooks") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf|" +
                Localization.T("Filter.TextBooks") + "|*.epub;*.txt;*.pdf;*.djvu;*.fb2;*.mobi;*.azw;*.azw3;*.cbz;*.cbr|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.epub;*.txt;*.pdf;*.djvu;*.fb2;*.mobi;*.azw;*.azw3;*.cbz;*.cbr;*.zip;*.rar;*.7z|" +
                Localization.T("Filter.AllFiles") + "|*.*";
        }

        private void OpenFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = BuildFileFilter();
                ofd.Title = Localization.T("Player.OpenFile.Title");
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string ext = System.IO.Path.GetExtension(ofd.FileName).ToLower();
                    if (LibraryScanner.IsArchive(ext))
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
                    UpdateSeekStepBookmarkOption();
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
            try
            {
                string fileName = System.IO.Path.GetFileNameWithoutExtension(archivePath);
                string destFolder = System.IO.Path.Combine(appSettings.LibraryPath, fileName);

                if (!System.IO.Directory.Exists(destFolder))
                    System.IO.Directory.CreateDirectory(destFolder);

                LibraryScanner.ExtractArchive(archivePath, destFolder);
                LibraryScanner.FlattenSingleWrapperFolder(destFolder);

                List<string> audioFiles = new List<string>();
                foreach (string f in System.IO.Directory.GetFiles(destFolder))
                {
                    if (Array.IndexOf(LibraryScanner.AudioExtensions, System.IO.Path.GetExtension(f).ToLower()) >= 0)
                        audioFiles.Add(f);
                }

                BookData book = new BookData(destFolder);
                if (audioFiles.Count > 0)
                {
                    audioFiles.Sort(StringComparer.OrdinalIgnoreCase);
                    book.BuildChaptersFromFolder(audioFiles.ToArray());
                }
                else
                {
                    book.Format = LibraryScanner.DetectFormat(destFolder);
                }
                book.DateAdded = DateTime.Now;
                book.Save();

                LoadBook(book, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
            }
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
                mpv_set_property_string(mpvHandle, "audio-display", "no");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.T("Dialog.Error.General", ex.Message), Localization.T("Dialog.Error.Title"));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCurrentBookProgress();
            eventTimer?.Stop();
            progressTimer?.Stop();
            sleepTimer?.Stop();
            if (mpvHandle != IntPtr.Zero)
                mpv_terminate_destroy(mpvHandle);
            base.OnFormClosing(e);
        }
    }
}
