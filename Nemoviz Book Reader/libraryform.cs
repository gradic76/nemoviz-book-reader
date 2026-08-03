using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    public class LibraryForm : Form
    {
        private MenuStrip menuStrip;
        // The shelf's right-click menu, a real Windows menu. Held as fields
        // because Popup adjusts which items apply to the book under the cursor.
        private ContextMenu bookMenu;
        private MenuItem ctxOpen, ctxMarkRead, ctxMarkUnread, ctxAddFav,
                         ctxRemoveFav, ctxRename, ctxDelete, ctxProperties;
        private ToolStripMenuItem menuFile;
        private ToolStripMenuItem menuFileOpenFile;
        private ToolStripMenuItem menuFileOpenFolder;
        private ToolStripMenuItem menuSort;
        private ToolStripMenuItem menuSortAlpha;
        private ToolStripMenuItem menuSortDate;
        private ToolStripMenuItem menuSortFormat;
        private ToolStripMenuItem menuSortStatus;
        private ToolStripMenuItem menuSortAsc;
        private ToolStripMenuItem menuSortDesc;
        private ToolStripMenuItem menuHelp;
        private ToolStripMenuItem menuHelpHelp;
        private ToolStripMenuItem menuHelpAbout;

        private Panel panelSearch;
        private TextBox tbSearch;
        private ComboBox cbFilter;

        private SplitContainer splitContainer;

        // The shelf is a ListView with native groups (like Explorer's grouped
        // view): group headers are NOT list items, so a screen reader counts
        // only the books ("3 of 5") and announces the group name as context
        // when arrowing into a new group.
        private ListView listBooks;

        private ListView listNowReading;
        private Panel nowReadingRule;
        private Panel panelDetails;
        private ListView listViewDetails;

        private Panel panelBottom;
        private Button btnRefresh;
        private Button btnOK;
        private Button btnCancel;

        private List<BookData> books;          // all scanned books
        // Two independent choices, not one of six combinations: what to sort by,
        // and which way round. Both come from the settings file and go back to it
        // the moment they change — the shelf opens the way it was left.
        private string sortKey = "alpha";
        private bool sortAscending = true;

        private AppSettings appSettings;
        private string activeBookFolderPath;

        // Set when the user marks the currently-playing book as read; the owner
        // (Form1) checks this after the dialog closes and unloads that book.
        public bool ActiveBookMarkedRead { get; private set; }

        // Callback into the player to stop + unload the active book immediately
        // (so it can be deleted in the same Library session).
        private readonly Action unloadActiveBook;

        // Shelf categories (also the status codes; NowReading is a 4th status
        // layered on top of Reading for the last-opened book).
        private const int CatReading = 0;
        private const int CatUnread = 1;
        private const int CatRead = 2;
        private const int StatusNowReading = 3;

        // Per-item status badge icons (colored dots) + bold font for Now reading.
        private ImageList statusIcons;
        private Font boldFont;

        // Filter combo indices (must match the order items are added)
        private const int FilterAll = 0;
        private const int FilterReading = 1;
        private const int FilterUnread = 2;
        private const int FilterRead = 3;
        private const int FilterFavorites = 4;

        // The details ListView rows are built fresh per selection (see
        // ShowDetails): the Author row only appears for books that carry an
        // author (DAISY), so plain audio isn't cluttered with an empty field.

        public BookData SelectedBook { get; private set; }

        public LibraryForm(AppSettings settings, string activeBookFolderPath = null,
            Action unloadActiveBook = null)
        {
            appSettings = settings;
            // Before BuildUI, so the menu opens with the right two ticks rather
            // than showing the default and then correcting itself.
            if (settings != null)
            {
                sortKey = settings.ShelfSortKey;
                sortAscending = settings.ShelfSortAscending;
            }
            this.activeBookFolderPath = activeBookFolderPath;
            this.unloadActiveBook = unloadActiveBook;
            books = new List<BookData>();
            BuildUI();
            LoadBooks();
        }

        /// <summary>What to start doing the moment the shelf is up, if anything.
        ///
        /// <para>Ctrl+O and Ctrl+Shift+O in the PLAYER open a book by putting it
        /// on the shelf first, rather than loading it straight into the transport
        /// (Gordan, 2026-08-02) — "as though it had been opened from here". This
        /// is how the player says so: it raises the Library and hands over the
        /// job. Nothing about importing is reimplemented, so archives, DRM,
        /// progress and the notice at the end all behave exactly as they do when
        /// the shelf is used directly, because they ARE that.</para></summary>
        public enum StartWith { Nothing, OpenFile, OpenFolder }
        public StartWith StartAction { get; set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Default tab order would land on the search box first. The library
            // opens on NOW READING (Gordan, 2026-08-03) — the question a reader
            // most often comes here with is "carry on with what I was reading",
            // and that is answered by pressing Enter where focus already is. The
            // shelf is one Tab away for everything else.
            //
            // POSTED, not called. Focusing from inside OnShown does not stick:
            // the form's own activation puts focus on the first control in the
            // tab order afterwards, and the search box won every time. The same
            // reason the file already posts the start-up action below.
            BeginInvoke((Action)(() =>
            {
                if (listNowReading == null) { listBooks.Focus(); return; }
                listNowReading.Focus();
                if (listNowReading.Items.Count > 0)
                {
                    listNowReading.Items[0].Selected = true;
                    listNowReading.Items[0].Focused = true;
                }
            }));

            // Posted, not called: the shelf must finish coming up before a modal
            // file dialog opens on top of it, or focus lands somewhere neither
            // window expects — the same reason the player defers opening this
            // window at start-up.
            if (StartAction == StartWith.Nothing) return;
            StartWith what = StartAction;
            StartAction = StartWith.Nothing;          // once, not on every Shown
            BeginInvoke((Action)(() =>
            {
                if (what == StartWith.OpenFile) MenuFileOpenFile_Click(null, EventArgs.Empty);
                else MenuFileOpenFolder_Click(null, EventArgs.Empty);
            }));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Manually-created GDI resources (not owned by any control).
            if (boldFont != null) { boldFont.Dispose(); boldFont = null; }
            if (statusIcons != null) { statusIcons.Dispose(); statusIcons = null; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+F — jump to the search box from anywhere in the library
            if (keyData == (Keys.Control | Keys.F))
            {
                tbSearch.Focus();
                return true;
            }

            // Alt+Enter — properties of the selected book (shelf only)
            if (listBooks.Focused && keyData == (Keys.Alt | Keys.Enter))
            {
                ShowProperties();
                return true;
            }

            if (keyData == Keys.Tab || keyData == (Keys.Tab | Keys.Shift))
                return StepTabRing(keyData == (Keys.Tab | Keys.Shift) ? -1 : +1);

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>The Library's tab order, written down once (Gordan,
        /// 2026-08-03): <b>Now reading, Bookshelf, Infobox, Search, Filter,
        /// Refresh, Load, Close</b> — and the exact reverse with Shift+Tab.
        ///
        /// <para>It was a handful of separate "if this has focus and Tab is
        /// pressed" rules before, which is why it was neither symmetric nor
        /// closed: the way back was not the way out reversed, and once the ring
        /// handed over to the default order Now reading fell out of it for good —
        /// docking makes it the LAST child of its panel, so by TabIndex it comes
        /// after the shelf rather than before it. Ordering the controls in one
        /// place removes the whole class of mistake.</para></summary>
        private Control[] TabRing()
        {
            return new Control[] { listNowReading, listBooks, listViewDetails,
                                   tbSearch, cbFilter, btnRefresh, btnOK, btnCancel };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>The control that really has the focus — asked of Windows,
        /// not of WinForms.
        ///
        /// <para>Two other answers were tried and both were wrong.
        /// <c>Form.ActiveControl</c> gives the CONTAINER, here the
        /// <c>SplitContainer</c>, so a ring asking "is this the focused one?"
        /// recognised the buttons and neither list — which is exactly why Tab and
        /// Shift+Tab walked two different, half-empty rings. Descending through
        /// each container's own <c>ActiveControl</c> gets one level further and
        /// then stops dead at the <c>SplitterPanel</c>, because <c>Panel</c> does
        /// not implement <c>IContainerControl</c>; that left the first Tab of the
        /// session skipping the shelf while every later lap was right, which is
        /// the kind of bug that gets called intermittent and is not.</para>
        ///
        /// <para><c>GetFocus</c> has no such gaps. The managed descent stays as a
        /// fallback for the case where the focus window belongs to no WinForms
        /// control at all.</para></summary>
        private Control FocusedControl()
        {
            Control c = Control.FromHandle(GetFocus());
            if (c != null) return c;

            c = this;
            IContainerControl container = c as IContainerControl;
            while (container != null && container.ActiveControl != null)
            {
                c = container.ActiveControl;
                container = c as IContainerControl;
            }
            return c;
        }

        /// <summary>Moves one stop round the ring, skipping anything a look has
        /// hidden or disabled, and wrapping at both ends so the order closes.
        /// Returns false when focus is somewhere the ring does not know about —
        /// a menu, say — and leaves Tab to Windows.</summary>
        private bool StepTabRing(int direction)
        {
            Control[] ring = TabRing();
            Control focused = FocusedControl();
            int at = -1;
            for (int i = 0; i < ring.Length && at < 0; i++)
                for (Control c = focused; c != null; c = c.Parent)
                    if (ReferenceEquals(c, ring[i])) { at = i; break; }
            if (at < 0) return false;

            for (int step = 1; step <= ring.Length; step++)
            {
                int i = ((at + direction * step) % ring.Length + ring.Length) % ring.Length;
                Control next = ring[i];
                if (next == null || !next.Visible || !next.Enabled || !next.CanSelect) continue;
                next.Focus();
                // Arriving at a list with nothing chosen leaves a reader with
                // nothing announced; the info box in particular is read row by
                // row, so it needs a row to start on.
                ListView lv = next as ListView;
                if (lv != null && lv.Items.Count > 0 && lv.SelectedItems.Count == 0)
                {
                    lv.Items[0].Selected = true;
                    lv.Items[0].Focused = true;
                }
                return true;
            }
            return false;
        }

        private void BuildUI()
        {
            this.Text = Localization.T("Library.Title");
            this.ClientSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimizeBox = false;
            this.MaximizeBox = false;

            BuildMenuStrip();
            BuildSearchRow();
            BuildSplitContainer();
            BuildBottomPanel();

            // Built exactly as before, then handed over — the classic path does
            // nothing here, the new look restyles and relays out what was built.
            if (UiTheme.Current.BuildsOwnLayout) LibrarySkin.Apply(this);
        }

        /// <summary>What the skin is allowed to move and repaint.</summary>
        internal LibraryParts SkinParts
        {
            get
            {
                return new LibraryParts
                {
                    Menu = menuStrip,
                    SearchRow = panelSearch,
                    Search = tbSearch,
                    Filter = cbFilter,
                    Split = splitContainer,
                    Books = listBooks,
                    NowReading = listNowReading,
                    NowReadingRule = nowReadingRule,
                    Details = listViewDetails,
                    BottomPanel = panelBottom,
                    Refresh = btnRefresh,
                    Load = btnOK,
                    Close = btnCancel,
                };
            }
        }

        private void BuildMenuStrip()
        {
            menuStrip = new MenuStrip();

            menuFile = new ToolStripMenuItem(Localization.T("Menu.File"));

            menuFileOpenFile = new ToolStripMenuItem(Localization.T("Menu.File.OpenFile"));
            menuFileOpenFile.ShortcutKeys = Keys.Control | Keys.O;
            menuFileOpenFile.Click += MenuFileOpenFile_Click;

            menuFileOpenFolder = new ToolStripMenuItem(Localization.T("Menu.File.OpenFolder"));
            menuFileOpenFolder.ShortcutKeys = Keys.Control | Keys.Shift | Keys.O;
            menuFileOpenFolder.Click += MenuFileOpenFolder_Click;

            menuFile.DropDownItems.Add(menuFileOpenFile);
            menuFile.DropDownItems.Add(menuFileOpenFolder);
            menuFile.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem menuFileClear = new ToolStripMenuItem(Localization.T("Menu.File.ClearLibrary"));
            menuFileClear.Click += (s, e) => ClearLibrary();
            menuFile.DropDownItems.Add(menuFileClear);

            menuFile.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem menuFileExit = new ToolStripMenuItem(Localization.T("Menu.File.Exit")) { ShortcutKeys = Keys.Alt | Keys.F4 };
            menuFileExit.Click += (s, e) => this.Close();
            menuFile.DropDownItems.Add(menuFileExit);

            menuSort = new ToolStripMenuItem(Localization.T("Menu.Sort"));

            // WHAT to sort by and WHICH WAY are two questions, so the menu asks
            // them separately and shows two ticks at once (Gordan, 2026-08-03).
            // Combined entries meant a new key cost two lines every time, and
            // four keys would have been eight of them.
            menuSortAlpha = new ToolStripMenuItem(Localization.T("Menu.Sort.Alphabetically"));
            menuSortAlpha.Click += (s, e) => SortBy("alpha");

            menuSortDate = new ToolStripMenuItem(Localization.T("Menu.Sort.DateAdded"));
            menuSortDate.Click += (s, e) => SortBy("date");

            menuSortFormat = new ToolStripMenuItem(Localization.T("Menu.Sort.Format"));
            menuSortFormat.Click += (s, e) => SortBy("format");

            // Status is the reading lifecycle — unread, then reading, then read.
            // NOT "now reading", which is a place of its own above the shelf, and
            // NOT favourite, which is a mark a book wears on top of its status
            // (Gordan).
            menuSortStatus = new ToolStripMenuItem(Localization.T("Menu.Sort.Status"));
            menuSortStatus.Click += (s, e) => SortBy("status");

            menuSortAsc = new ToolStripMenuItem(Localization.T("Menu.Sort.Ascending"));
            menuSortAsc.Click += (s, e) => SortDirection(true);

            menuSortDesc = new ToolStripMenuItem(Localization.T("Menu.Sort.Descending"));
            menuSortDesc.Click += (s, e) => SortDirection(false);

            menuSort.DropDownItems.Add(menuSortAlpha);
            menuSort.DropDownItems.Add(menuSortDate);
            menuSort.DropDownItems.Add(menuSortFormat);
            menuSort.DropDownItems.Add(menuSortStatus);
            menuSort.DropDownItems.Add(new ToolStripSeparator());
            menuSort.DropDownItems.Add(menuSortAsc);
            menuSort.DropDownItems.Add(menuSortDesc);

            // Help, standing where a Windows menu bar always ends, and wired —
            // both items lead somewhere real even though neither has its content
            // yet (Gordan, 2026-08-03: "da ne ostaju repovi"). The manual is a
            // page that says it is coming; About is the window it will be, empty
            // and waiting for its words. A key that leads to a short page is
            // fine; a key that does nothing is the failure people notice.
            //
            // F1 means Help here exactly as it does in the player: one function,
            // two ways in — the menu for the mouse, the key from wherever the
            // reader is.
            menuHelp = new ToolStripMenuItem(Localization.T("Menu.Help"));
            menuHelpHelp = new ToolStripMenuItem(Localization.T("Menu.Help.Help"));
            menuHelpHelp.ShortcutKeys = Keys.F1;
            menuHelpHelp.Click += (s, e) => HintSystem.OpenManual(this);
            menuHelpAbout = new ToolStripMenuItem(Localization.T("Menu.Help.About"));
            menuHelpAbout.Click += (s, e) => HintSystem.ShowAbout(this);
            menuHelp.DropDownItems.Add(menuHelpHelp);
            menuHelp.DropDownItems.Add(new ToolStripSeparator());
            menuHelp.DropDownItems.Add(menuHelpAbout);

            menuStrip.Items.Add(menuFile);
            menuStrip.Items.Add(menuSort);
            menuStrip.Items.Add(menuHelp);

            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);

            UpdateSortMenuChecks();
        }

        /// <summary>
        /// Marks the active sort mode in the View menu: a visual checkmark
        /// plus a localized text suffix (e.g. "(active)"). The suffix is
        /// there because screen readers don't reliably announce the check
        /// state of MenuStrip items — text is always read. If the suffix
        /// is unwanted, empty the "Menu.Sort.ActiveMark" value in the
        /// .lang file and only the checkmark remains.
        /// </summary>
        private void UpdateSortMenuChecks()
        {
            ApplySortMark(menuSortAlpha, "Menu.Sort.Alphabetically", sortKey == "alpha");
            ApplySortMark(menuSortDate, "Menu.Sort.DateAdded", sortKey == "date");
            ApplySortMark(menuSortFormat, "Menu.Sort.Format", sortKey == "format");
            ApplySortMark(menuSortStatus, "Menu.Sort.Status", sortKey == "status");
            ApplySortMark(menuSortAsc, "Menu.Sort.Ascending", sortAscending);
            ApplySortMark(menuSortDesc, "Menu.Sort.Descending", !sortAscending);
        }

        private void ApplySortMark(ToolStripMenuItem item, string langKey, bool active)
        {
            if (item == null) return;
            item.Checked = active;

            string mark = Localization.T("Menu.Sort.ActiveMark");
            item.Text = active && mark.Length > 0 && mark != "Menu.Sort.ActiveMark"
                ? Localization.T(langKey) + " " + mark
                : Localization.T(langKey);
        }

        private void BuildSearchRow()
        {
            panelSearch = new Panel();
            panelSearch.Location = new Point(0, menuStrip.Height);
            panelSearch.Size = new Size(800, 32);

            tbSearch = new TextBox();
            tbSearch.Location = new Point(10, 4);
            tbSearch.Size = new Size(380, 24);
            tbSearch.AccessibleName = Localization.T("Library.Search.Accessible");
            tbSearch.TextChanged += (s, e) => RebuildShelf(GetSelectedBook());
            // Select the existing text whenever the box gains focus (Tab,
            // Ctrl+F, mouse), so a new entry or Del replaces the old query
            // instead of appending to it. BeginInvoke survives the mouse
            // click that would otherwise reset the selection to a caret.
            tbSearch.Enter += (s, e) => BeginInvoke((Action)(() => tbSearch.SelectAll()));

            cbFilter = new ComboBox();
            cbFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            cbFilter.Location = new Point(400, 4);
            cbFilter.Size = new Size(390, 24);
            cbFilter.AccessibleName = Localization.T("Library.Filter.Accessible");
            cbFilter.Items.Add(Localization.T("Shelf.Filter.All"));
            cbFilter.Items.Add(Localization.T("Shelf.Filter.Reading"));
            cbFilter.Items.Add(Localization.T("Shelf.Filter.Unread"));
            cbFilter.Items.Add(Localization.T("Shelf.Filter.Read"));
            cbFilter.Items.Add(Localization.T("Shelf.Filter.Favorites"));
            cbFilter.SelectedIndex = FilterAll;
            // Subscribe only after the initial SelectedIndex is set,
            // so building the UI doesn't trigger a premature rebuild.
            cbFilter.SelectedIndexChanged += (s, e) => RebuildShelf(GetSelectedBook());

            panelSearch.Controls.Add(tbSearch);
            panelSearch.Controls.Add(cbFilter);
            this.Controls.Add(panelSearch);
        }

        private void BuildSplitContainer()
        {
            splitContainer = new SplitContainer();
            splitContainer.Location = new Point(0, menuStrip.Height + panelSearch.Height);
            splitContainer.Size = new Size(800, 540 - panelSearch.Height);
            splitContainer.SplitterDistance = 350;
            splitContainer.Panel1MinSize = 200;
            splitContainer.Panel2MinSize = 200;
            splitContainer.TabStop = false;

            // The book being read is its own place now (Gordan, 2026-08-03), not
            // a bold row pinned to the top of the shelf. Two lists, a hairline
            // between them, and each its own stop on the way round with Tab — so
            // "what am I reading" is answered by arriving somewhere, not by
            // trusting that the first row of a long list is the right one.
            // What stands here does NOT also stand on the shelf.
            listNowReading = new ListView();
            listNowReading.Dock = DockStyle.Top;
            listNowReading.View = View.Details;
            listNowReading.HeaderStyle = ColumnHeaderStyle.None;
            listNowReading.FullRowSelect = true;
            listNowReading.MultiSelect = false;
            listNowReading.HideSelection = false;
            listNowReading.ShowGroups = false;
            listNowReading.Scrollable = false;
            listNowReading.Font = new Font("Segoe UI", 11);
            listNowReading.AccessibleName = Localization.T("Library.NowReading.Accessible");
            listNowReading.Columns.Add("", 320);
            listNowReading.SelectedIndexChanged += ListBooks_SelectedIndexChanged;
            listNowReading.DoubleClick += ListBooks_DoubleClick;
            listNowReading.KeyDown += ListBooks_KeyDown;
            listNowReading.SizeChanged += (s, e) =>
            {
                if (listNowReading.Columns.Count > 0)
                    listNowReading.Columns[0].Width = Math.Max(50, listNowReading.ClientSize.Width - 4);
            };

            // A hairline, not a splitter: there is nothing to drag here, and a
            // one-pixel panel cannot take focus, so it separates the two lists for
            // the eye without adding anything for Tab to land on.
            nowReadingRule = new Panel();
            nowReadingRule.Dock = DockStyle.Top;
            nowReadingRule.Height = 1;
            nowReadingRule.BackColor = SystemColors.ControlDark;
            nowReadingRule.TabStop = false;

            listBooks = new ListView();
            listBooks.Dock = DockStyle.Fill;
            listBooks.View = View.Details;
            listBooks.HeaderStyle = ColumnHeaderStyle.None;
            listBooks.FullRowSelect = true;
            listBooks.MultiSelect = false;
            listBooks.HideSelection = false;
            listBooks.ShowGroups = false;
            listBooks.Font = new Font("Segoe UI", 11);
            // Status badges: a small colored dot per item (red = unread,
            // yellow = reading, green = read, blue = now reading). Purely
            // visual — the same status is also spoken as a text flag on the
            // item name, so screen-reader users lose nothing.
            statusIcons = new ImageList();
            // 20 rather than 16: at 16 there is no room for both a dot big enough
            // to read its colour and a heart big enough to read its shape — the
            // first attempt had the heart swallowing the badge, and the COLOUR is
            // what carries the status.
            statusIcons.ImageSize = new Size(20, 20);
            statusIcons.ColorDepth = ColorDepth.Depth32Bit;
            // Each status twice: plain, and with the favorite heart on it.
            var badges = new (string Key, Color Colour)[]
            {
                ("reading",    Color.FromArgb(222, 170, 40)),   // yellow
                ("unread",     Color.FromArgb(210, 66, 66)),    // red
                ("read",       Color.FromArgb(70, 160, 74)),    // green
                ("nowreading", Color.FromArgb(58, 120, 214)),   // blue
            };
            foreach (var b in badges)
            {
                statusIcons.Images.Add(b.Key, MakeStatusDot(b.Colour, false));
                statusIcons.Images.Add(b.Key + "+fav", MakeStatusDot(b.Colour, true));
            }
            listBooks.SmallImageList = statusIcons;
            boldFont = new Font(listBooks.Font, FontStyle.Bold);
            listBooks.AccessibleName = Localization.T("Library.List.Accessible");
            listBooks.Columns.Add("", 320);
            // Keep the single column matched to the shelf width (the splitter
            // can be moved), so there's no horizontal scrollbar.
            listBooks.SizeChanged += (s, e) =>
            {
                if (listBooks.Columns.Count > 0)
                    listBooks.Columns[0].Width = Math.Max(50, listBooks.ClientSize.Width - 4);
            };
            listBooks.SelectedIndexChanged += ListBooks_SelectedIndexChanged;
            listBooks.DoubleClick += ListBooks_DoubleClick;
            listBooks.KeyDown += ListBooks_KeyDown;

            // A REAL Windows menu (ContextMenu → HMENU), not a ContextMenuStrip.
            // The strip is a ToolStrip that .NET paints itself, and both readers
            // announced it as a drop-down list with nothing selected until the
            // user arrowed onto something — Gordan's report, 2026-07-29. A real
            // menu is announced as a menu, highlights its first item the moment it
            // opens, and behaves like every other menu in Windows because it IS
            // one. The trade is the shortcut column: MenuItem has no display-only
            // shortcut text, so the keys go in the label, which a reader reads out
            // anyway and a ShortcutKeyDisplayString never was.
            bookMenu = new ContextMenu();

            ctxOpen = new MenuItem(Localization.T("Context.Open"));
            ctxOpen.Click += (s, e) => OpenSelectedBook();

            ctxMarkRead = new MenuItem(Localization.T("Context.MarkRead"));
            ctxMarkRead.Click += (s, e) => MarkSelected(true);

            ctxMarkUnread = new MenuItem(Localization.T("Context.MarkUnread"));
            ctxMarkUnread.Click += (s, e) => MarkSelected(false);

            ctxAddFav = new MenuItem(Localization.T("Context.AddFavorite"));
            ctxAddFav.Click += (s, e) => SetSelectedFavorite(true);

            ctxRemoveFav = new MenuItem(Localization.T("Context.RemoveFavorite"));
            ctxRemoveFav.Click += (s, e) => SetSelectedFavorite(false);

            ctxRename = new MenuItem(Localization.T("Context.Rename"));
            ctxRename.Click += (s, e) => RenameSelectedBook();

            ctxDelete = new MenuItem(Localization.T("Context.Delete"));
            ctxDelete.Click += (s, e) => DeleteSelectedBook();

            ctxProperties = new MenuItem(Localization.T("Context.Properties"));
            ctxProperties.Click += (s, e) => ShowProperties();

            bookMenu.MenuItems.Add(ctxOpen);
            bookMenu.MenuItems.Add(new MenuItem("-"));
            bookMenu.MenuItems.Add(ctxMarkRead);
            bookMenu.MenuItems.Add(ctxMarkUnread);
            bookMenu.MenuItems.Add(ctxAddFav);
            bookMenu.MenuItems.Add(ctxRemoveFav);
            bookMenu.MenuItems.Add(new MenuItem("-"));
            bookMenu.MenuItems.Add(ctxRename);
            bookMenu.MenuItems.Add(ctxDelete);
            bookMenu.MenuItems.Add(new MenuItem("-"));
            bookMenu.MenuItems.Add(ctxProperties);

            // Which items apply to the book under the cursor. Popup fires before
            // the menu is shown, the same moment ContextMenuStrip.Opening did —
            // but it cannot cancel, so an empty shelf is caught at the call site.
            bookMenu.Popup += (s, e) =>
            {
                BookData b = GetSelectedBook();
                if (b == null) return;
                bool active = PathsEqual(b.FolderPath, activeBookFolderPath);
                int cat = GetCategory(b);
                // "Mark as read" — everywhere except books already in Read.
                ctxMarkRead.Visible = cat != CatRead;
                // "Mark as unread" — not on the active book, not on Unread books.
                ctxMarkUnread.Visible = !active && cat != CatUnread;
                ctxAddFav.Visible = !b.Favorite;
                ctxRemoveFav.Visible = b.Favorite;
            };

            // A ContextMenu is not attached the way a strip was — it is shown on
            // demand, so the right-click has to be caught here. MouseUp, not
            // MouseDown, so the click that selects a row lands first.
            listBooks.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowBookMenu(listBooks, new Point(e.X, e.Y));
            };
            listNowReading.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowBookMenu(listNowReading, new Point(e.X, e.Y));
            };

            // The FILL one goes in first: WinForms docks in reverse order of the
            // collection, so whatever is added last ends up outermost. Added the
            // other way round, Now reading would have been laid out inside the
            // shelf's leftovers and come out at the bottom.
            splitContainer.Panel1.Controls.Add(listBooks);
            splitContainer.Panel1.Controls.Add(nowReadingRule);
            splitContainer.Panel1.Controls.Add(listNowReading);
            SizeNowReading();

            panelDetails = new Panel();
            panelDetails.Dock = DockStyle.Fill;
            panelDetails.Padding = new Padding(10);

            listViewDetails = new ListView();
            listViewDetails.Dock = DockStyle.Fill;
            listViewDetails.View = View.Details;
            listViewDetails.FullRowSelect = true;
            listViewDetails.HeaderStyle = ColumnHeaderStyle.None;
            listViewDetails.MultiSelect = false;
            listViewDetails.Font = new Font("Segoe UI", 10);
            listViewDetails.AccessibleName = Localization.T("Library.Details.Accessible");
            listViewDetails.TabStop = true;
            listViewDetails.GridLines = false;
            listViewDetails.BorderStyle = BorderStyle.None;

            listViewDetails.Columns.Add("Field", 120);
            listViewDetails.Columns.Add("Value", 280);

            // Rows are populated per selection in ShowDetails (nothing selected
            // yet at construction, so the panel starts empty).

            panelDetails.Controls.Add(listViewDetails);

            splitContainer.Panel2.Controls.Add(panelDetails);
            this.Controls.Add(splitContainer);
        }

        private void BuildBottomPanel()
        {
            panelBottom = new Panel();
            panelBottom.Location = new Point(0, 540);
            panelBottom.Size = new Size(800, 60);
            panelBottom.BorderStyle = BorderStyle.FixedSingle;

            btnRefresh = new Button();
            btnRefresh.Text = Localization.T("Btn.Refresh");
            btnRefresh.Size = new Size(100, 35);
            btnRefresh.Location = new Point(10, 12);
            btnRefresh.AccessibleName = Localization.T("Btn.Refresh.Accessible");
            btnRefresh.Click += (s, e) => LoadBooks();

            // Load and Close, not OK and Cancel — the same words the new look
            // already used (LibrarySkin renamed them), so the two agree and a
            // reader hears what the button DOES rather than which dialog
            // convention it belongs to.
            btnOK = new Button();
            btnOK.Text = Localization.T("Library.Btn.Load");
            btnOK.Size = new Size(100, 35);
            btnOK.Location = new Point(580, 12);
            btnOK.AccessibleName = Localization.T("Library.Btn.Load.Accessible");
            btnOK.Click += (s, e) => OpenSelectedBook();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Library.Btn.Close");
            btnCancel.Size = new Size(100, 35);
            btnCancel.Location = new Point(690, 12);
            btnCancel.AccessibleName = Localization.T("Library.Btn.Close.Accessible");
            btnCancel.Click += (s, e) => this.Close();

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            panelBottom.Controls.Add(btnRefresh);
            panelBottom.Controls.Add(btnOK);
            panelBottom.Controls.Add(btnCancel);

            this.Controls.Add(panelBottom);
        }

        // ──────────────────────────────────────────────
        // Loading and rebuilding the shelf
        // ──────────────────────────────────────────────
        private void LoadBooks()
        {
            LibraryScanner scanner = new LibraryScanner(appSettings.LibraryPath, true);
            // The ONE call in NBR that may delete a user's file, and the only
            // place allowed to make it: an archive dropped into the library
            // through Explorer is unpacked there and the original removed,
            // because that folder is NBR's own. Scanning itself never writes.
            scanner.AbsorbArchives();
            books = scanner.Scan();
            RebuildShelf(null);
        }

        /// <summary>
        /// Rebuilds the shelf from `books` as one flat, sorted list (no group
        /// headers). Each item carries its status as a spoken text flag plus a
        /// colored badge icon; the Now-reading book is bold and pinned to the
        /// top. Search + the status/Favorites filter narrow the list. Tries to
        /// keep `keepSelected` selected; otherwise selects the first book.
        /// </summary>
        private void RebuildShelf(BookData keepSelected)
        {
            string query = NormalizeForSearch(tbSearch.Text.Trim());
            int filter = cbFilter.SelectedIndex;
            if (filter < 0) filter = FilterAll;

            var list = new List<BookData>();
            foreach (BookData b in books)
            {
                if (query.Length > 0 && !NormalizeForSearch(b.Title).Contains(query))
                    continue;
                int cat = GetCategory(b);
                bool include;
                switch (filter)
                {
                    case FilterReading: include = cat == CatReading; break;
                    case FilterUnread: include = cat == CatUnread; break;
                    case FilterRead: include = cat == CatRead; break;
                    case FilterFavorites: include = b.Favorite; break;
                    default: include = true; break; // All
                }
                if (include) list.Add(b);
            }

            // Order follows the sort menu. The book being read is then TAKEN OUT
            // of the shelf entirely rather than pinned to the top of it: it has
            // its own list above the rule, and a book in two places at once is a
            // book you can lose track of.
            list.Sort(GetComparer());
            BookData nowReading = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (IsNowReading(list[i]))
                {
                    nowReading = list[i];
                    list.RemoveAt(i);
                    break;
                }
            }
            // Off the shelf, but still the answer to "what am I reading" even when
            // the search box or the filter would have hidden it.
            if (nowReading == null)
                foreach (BookData b in books)
                    if (IsNowReading(b)) { nowReading = b; break; }
            FillNowReading(nowReading);

            listBooks.BeginUpdate();
            listBooks.Items.Clear();
            listBooks.Groups.Clear();
            listBooks.ShowGroups = false;
            foreach (BookData b in list)
                listBooks.Items.Add(BuildShelfItem(b));
            listBooks.EndUpdate();

            ListViewItem toSelect = null;
            if (keepSelected != null)
            {
                foreach (ListViewItem item in listBooks.Items)
                    if (item.Tag == keepSelected) { toSelect = item; break; }
            }
            if (toSelect == null && listBooks.Items.Count > 0)
                toSelect = listBooks.Items[0];

            if (toSelect != null)
            {
                toSelect.Selected = true;
                toSelect.Focused = true;
                toSelect.EnsureVisible();
            }
            else
            {
                ClearDetails();
            }
        }

        /// <summary>Builds one shelf row: "Author — Title" + spoken status flag
        /// (+ ", Favorite"), a colored status badge, and a bold font for the
        /// Now-reading book.</summary>
        private ListViewItem BuildShelfItem(BookData b)
        {
            string name = string.IsNullOrWhiteSpace(b.Author) ? b.Title : b.Author + " — " + b.Title;
            int status = GetShelfStatus(b);
            // The TITLE starts the row, and that is not a presentation choice —
            // it is what makes first-letter navigation work. A list view jumps to
            // the next item beginning with the typed letter, so with the status in
            // front every row began with R, U or N and typing a letter did nothing
            // useful. On a shelf of thousands that aid matters far more than
            // hearing "unread" a second earlier, and nothing is lost: the status
            // is still spoken, at the end of the line, and the coloured badge
            // still carries it at a glance.
            // Favorite is NOT in the text — it is the heart on the badge. The word
            // was one more tail to listen past on every favorite row, and the
            // Favorites filter already answers "which are mine" properly.
            string text = name + ", " + Localization.T(StatusTextKey(status));
            ListViewItem item = new ListViewItem(text);
            item.Tag = b;
            item.ImageKey = StatusIconKey(status) + (b.Favorite ? "+fav" : "");
            if (status == StatusNowReading)
                item.Font = boldFont;
            return item;
        }

        // The last-opened book counts as "Now reading" only while it's still
        // in progress (Reading category) — a finished or rewound book isn't.
        /// <summary>The book the PLAYER currently has loaded — nothing else.
        ///
        /// <para>It used to be worked out here instead: the last opened book, and
        /// only if it had been listened to at all. Those are two different
        /// questions and they gave two different answers. Gordan had *Test
        /// rječnik* loaded and playable in the player while the Library said "No
        /// book loaded", because he had not yet played a second of it, so the
        /// shelf filed it under Unread and refused to call it the book being
        /// read. Loading a book IS reading it; the progress bar decides how far
        /// in, not whether.</para>
        ///
        /// <para>The player hands its answer in on the way (<c>currentBook</c>,
        /// or null when it holds nothing), so the two windows now say the same
        /// thing by construction rather than by two rules kept in step. And when
        /// the player holds nothing it is because it started empty — which is
        /// exactly when it opens this window by itself.</para></summary>
        private bool IsNowReading(BookData b)
        {
            return b != null
                && !string.IsNullOrEmpty(activeBookFolderPath)
                && PathsEqual(b.FolderPath, activeBookFolderPath);
        }

        private int GetShelfStatus(BookData b)
        {
            return IsNowReading(b) ? StatusNowReading : GetCategory(b);
        }

        private static string StatusTextKey(int status)
        {
            switch (status)
            {
                case StatusNowReading: return "Shelf.Status.NowReading";
                case CatUnread: return "Shelf.Status.Unread";
                case CatRead: return "Shelf.Status.Read";
                default: return "Shelf.Status.Reading";
            }
        }

        private static string StatusIconKey(int status)
        {
            switch (status)
            {
                case StatusNowReading: return "nowreading";
                case CatUnread: return "unread";
                case CatRead: return "read";
                default: return "reading";
            }
        }

        // A 16×16 transparent bitmap with a filled colored circle — the status
        // badge shown at the left of each shelf row — and, for a favorite, a small
        // heart sitting on it.
        //
        // The heart is IN THE PICTURE on purpose. Gordan's idea, and it is the
        // only place it can go: an image is not announced, so a favorite is seen
        // and not said, while ", Favorite" on the end of every favorite row was
        // one more thing to listen past. It could not be a character in the text
        // either — the item's text IS what a screen reader reads, so a heart there
        // would come out as "black heart suit".
        private static Bitmap MakeStatusDot(Color color, bool favorite)
        {
            Bitmap bmp = new Bitmap(20, 20);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                if (!favorite)
                {
                    using (Brush br = new SolidBrush(color))
                        g.FillEllipse(br, 2, 3, 14, 14);
                    return bmp;
                }

                // A favorite is the SAME badge in the SAME colour, drawn as a
                // heart instead of a circle (Gordan, 2026-07-29). The shape
                // carries "favorite", the colour still carries the status, and
                // neither has to make room for the other — which the first
                // attempt, a small mark tucked into the corner, could not manage.
                // A shape filling the badge also reads far better than one
                // squeezed beside it.
                using (var path = HeartPath(1.5f, 3f, 17f, 15f))
                {
                    using (Brush br = new SolidBrush(color))
                        g.FillPath(br, path);
                    // A darker edge of the same hue, so the shape holds against a
                    // selected row's highlight without introducing a new colour.
                    using (var pen = new Pen(Darken(color, 0.55f), 1.2f))
                        g.DrawPath(pen, path);
                }
            }
            return bmp;
        }

        private static Color Darken(Color c, float f)
        {
            return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        private static System.Drawing.Drawing2D.GraphicsPath HeartPath(float x, float y, float w, float h)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float lobe = w / 2f;
            p.AddArc(x, y, lobe, lobe * 0.9f, 180, 180);                 // left lobe
            p.AddArc(x + lobe, y, lobe, lobe * 0.9f, 180, 180);          // right lobe
            p.AddLine(x + w, y + lobe * 0.45f, x + w / 2f, y + h);       // down to the point
            p.AddLine(x + w / 2f, y + h, x, y + lobe * 0.45f);           // and back up
            p.CloseFigure();
            return p;
        }

        /// <summary>Puts the book being read in its own list, or says plainly
        /// that there is none. The empty row is a ROW rather than a blank box:
        /// arriving at an empty list and being told nothing is the state that
        /// leaves a reader wondering whether something failed to load.</summary>
        private void FillNowReading(BookData b)
        {
            if (listNowReading == null) return;
            listNowReading.BeginUpdate();
            listNowReading.Items.Clear();
            if (b != null)
            {
                listNowReading.SmallImageList = statusIcons;
                listNowReading.Items.Add(BuildShelfItem(b));
            }
            else
            {
                listNowReading.SmallImageList = null;
                var empty = new ListViewItem(Localization.T("Library.NowReading.Empty"));
                empty.Tag = null;
                empty.ForeColor = SystemColors.GrayText;
                listNowReading.Items.Add(empty);
            }
            listNowReading.EndUpdate();
            SizeNowReading();
        }

        /// <summary>One row tall, measured rather than guessed — the row height
        /// follows the font, and this list must not grow a scroll bar or eat the
        /// shelf's height.</summary>
        private void SizeNowReading()
        {
            if (listNowReading == null) return;
            int row = listNowReading.Items.Count > 0
                ? listNowReading.Items[0].Bounds.Height : 0;
            if (row <= 0) row = listNowReading.Font.Height + 8;
            listNowReading.Height = row + 6;
        }

        /// <summary>Returns the selected book, or null if nothing is selected.
        ///
        /// <para>Two lists can each hold a selection, and they both keep it while
        /// focus is elsewhere (that is what shows the reader where they were). So
        /// the one that ANSWERS is the one with focus; failing that, the shelf,
        /// which is where the work is done.</para></summary>
        private BookData GetSelectedBook()
        {
            if (listNowReading != null && listNowReading.Focused &&
                listNowReading.SelectedItems.Count > 0)
                return listNowReading.SelectedItems[0].Tag as BookData;
            if (listBooks == null || listBooks.SelectedItems.Count == 0)
                return null;
            return listBooks.SelectedItems[0].Tag as BookData;
        }

        // ──────────────────────────────────────────────
        // Categories, sorting, search normalization
        // ──────────────────────────────────────────────
        private int GetCategory(BookData b)
        {
            if (b.PercentListened >= 100) return CatRead;

            double seconds = 0;
            TimeSpan t;
            if (TimeSpan.TryParse(b.LastPosition, out t))
                seconds = t.TotalSeconds;

            if (b.PercentListened <= 0 && seconds < 0.5) return CatUnread;
            return CatReading;
        }

        /// <summary>The key decides what is compared; the direction is applied
        /// once, at the end, by flipping the sign. Writing each key twice was
        /// what made six menu entries out of three ideas — and it is also where a
        /// descending order quietly loses its tie-break, since the second key has
        /// to stay ascending to be any use.</summary>
        private Comparison<BookData> GetComparer()
        {
            Comparison<BookData> byTitle =
                (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);

            Comparison<BookData> key;
            switch (sortKey)
            {
                case "date":
                    key = (a, b) => a.DateAdded.CompareTo(b.DateAdded);
                    break;
                case "format":
                    key = (a, b) => string.Compare(a.Format, b.Format,
                                                   StringComparison.CurrentCultureIgnoreCase);
                    break;
                case "status":
                    key = (a, b) => StatusRank(a).CompareTo(StatusRank(b));
                    break;
                default:
                    key = byTitle;
                    break;
            }

            int sign = sortAscending ? 1 : -1;
            if (key == byTitle) return (a, b) => sign * byTitle(a, b);
            // Everything else falls back to the title, and that tie-break stays
            // ASCENDING however the main key runs: within one format, or one
            // status, a reader is looking a title up, not admiring the order.
            return (a, b) =>
            {
                int c = key(a, b);
                return c != 0 ? sign * c : byTitle(a, b);
            };
        }

        /// <summary>Where a book stands in the reading lifecycle: unread, then
        /// being read, then read. Not "now reading" — that is a place of its own
        /// above the shelf — and not favourite, which is a mark worn on top of a
        /// status rather than one of them (Gordan, 2026-08-03).</summary>
        private int StatusRank(BookData b)
        {
            switch (GetCategory(b))
            {
                case CatUnread: return 0;
                case CatReading: return 1;
                default: return 2;      // CatRead
            }
        }

        private void SortBy(string key)
        {
            sortKey = key;
            ApplySort();
        }

        private void SortDirection(bool ascending)
        {
            sortAscending = ascending;
            ApplySort();
        }

        private void ApplySort()
        {
            if (appSettings != null) appSettings.SetShelfSort(sortKey, sortAscending);
            UpdateSortMenuChecks();
            RebuildShelf(GetSelectedBook());
        }

        /// <summary>
        /// Case-insensitive, diacritics-insensitive normalization for search.
        /// Both the query and the titles pass through it, so matching works
        /// in both directions: "c" finds "č"/"ć" and "č" finds "c"/"ć".
        /// Uses Unicode decomposition, so it also covers non-Croatian
        /// accents (ü→u, é→e, ...); "đ" is special-cased since it doesn't
        /// decompose to "d".
        /// </summary>
        private static string NormalizeForSearch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            s = s.ToLowerInvariant().Replace('đ', 'd');
            string formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static bool PathsEqual(string pathA, string pathB)
        {
            if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB))
                return false;
            try
            {
                string a = System.IO.Path.GetFullPath(pathA).TrimEnd('\\', '/');
                string b = System.IO.Path.GetFullPath(pathB).TrimEnd('\\', '/');
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ──────────────────────────────────────────────
        // Selection and details
        // ──────────────────────────────────────────────
        private void ListBooks_SelectedIndexChanged(object sender, EventArgs e)
        {
            BookData book = GetSelectedBook();
            if (book == null)
            {
                ClearDetails();
                return;
            }
            ShowDetails(book);
        }

        private void ShowDetails(BookData book)
        {
            // Lazy one-time upgrade of old plain format labels ("MP3 Audio")
            // to the detailed ones ("MP3 Audio, 44.1 kHz, 128 kbps, stereo").
            // Persists in Book.ini, so it's a no-op on every later selection.
            book.EnsureFormatDetails();
            // Build the duration up front for scan-added plain audio books, so
            // the details show a real length before first playback (DAISY books
            // already have theirs from import). One-time, cached in Book.ini.
            book.EnsureDurationDetails();

            string dash = Localization.T("Common.Dash");

            listViewDetails.BeginUpdate();
            listViewDetails.Items.Clear();

            // What this book has to say, in whatever order is convenient here —
            // BookInfoBuilder puts it in the canonical one (see BookInfo.cs).
            // Before this, the Library, the audio Properties and the reading
            // Properties each had an order of their own.
            var info = new BookInfoBuilder();
            info.AddAlways(BookInfoField.Title, book.Title, dash);
            info.AddAlways(BookInfoField.Author, book.Author, dash);
            info.AddAlways(BookInfoField.Added,
                book.DateAdded.ToString(Localization.T("Common.DateFormatLong")), dash);

            if (book.IsTextBook) AddTextDetails(info, book);
            else AddAudioDetails(info, book);

            foreach (InfoRow r in info.Rows()) AddDetailRow(r.Label, r.Value);
            listViewDetails.EndUpdate();
        }

        // The audio / DAISY book's own fields. Producer keeps its row even when
        // empty (unknown, per spec); Publisher appears only when there is one —
        // DAISY has both, plain audio has neither.
        private void AddAudioDetails(BookInfoBuilder info, BookData book)
        {
            info.AddAlways(BookInfoField.Producer, BookData.NormalizeProducer(book.Producer), "");
            info.Add(BookInfoField.Publisher, BookData.NormalizeProducer(book.Publisher));

            double totalSec = ParseDetailTime(book.Duration);
            double elapsedSec = ParseDetailTime(book.LastPosition);
            double remaining = totalSec - elapsedSec;
            if (remaining < 0) remaining = 0;

            info.Add(BookInfoField.Time, book.Duration);
            info.Add(BookInfoField.Elapsed, FormatDetailTime(elapsedSec));
            info.Add(BookInfoField.Remaining, "-" + FormatDetailTime(remaining));
            info.Add(BookInfoField.Read, (totalSec > 0
                ? (100.0 * elapsedSec / totalSec).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : book.PercentListened.ToString()) + "%");
            info.Add(BookInfoField.Format, book.Format);
            // Empty (unknown), not "0", when a DAISY book declares no pages —
            // "0" would wrongly read as "zero pages".
            if (book.IsDaisy && book.DaisyPages.Count > 0)
                info.Add(BookInfoField.Pages, book.DaisyPages.Count.ToString());
            info.Add(BookInfoField.SoundProcessing,
                Localization.T(book.Sound != null && book.Sound.Enabled ? "Details.Sound.On" : "Details.Sound.Off"));
        }

        // The text book's own fields: real source format, estimated reading time
        // and the speed that estimate is based on.
        private void AddTextDetails(BookInfoBuilder info, BookData book)
        {
            info.Add(BookInfoField.Producer, BookData.NormalizeProducer(book.Producer));
            info.Add(BookInfoField.Publisher, BookData.NormalizeProducer(book.Publisher));

            int wpm = book.TextWpm >= 0 ? book.TextWpm : appSettings.TtsWpm;
            info.Add(BookInfoField.Time, "≈" + book.EstimatedReadingTime(wpm));
            info.Add(BookInfoField.Read, book.PercentListened + "%");
            info.Add(BookInfoField.Format, book.Format);
            if (book.TextPages.Count > 0)
                info.Add(BookInfoField.Pages, book.TextPages.Count.ToString());
            info.Add(BookInfoField.Speed, Localization.T("Details.Speed.Wpm", wpm));
        }

        // Fills a book's title/author from an audio file's Album/Artist tags
        // (Album = book title, Artist/AlbumArtist = author). Best-effort — any
        // read error leaves the folder-name title in place.
        /// <summary>Brings a CUE sheet along with the audio file it describes. Only
        /// the sheet that actually names this file (or shares its name) — a folder
        /// can hold several, each belonging to a different rip.</summary>
        private static void CopyCueSheet(string sourceAudioFile, string destFolder)
        {
            try
            {
                string srcDir = System.IO.Path.GetDirectoryName(sourceAudioFile);
                if (string.IsNullOrEmpty(srcDir)) return;
                string audioName = System.IO.Path.GetFileName(sourceAudioFile);

                foreach (string cue in System.IO.Directory.GetFiles(srcDir, "*.cue"))
                {
                    CueSheet sheet = CueParser.TryParse(cue);
                    bool named = sheet != null && !string.IsNullOrEmpty(sheet.AudioFile)
                        && string.Equals(System.IO.Path.GetFileName(sheet.AudioFile), audioName,
                                         StringComparison.OrdinalIgnoreCase);
                    bool sameName = string.Equals(System.IO.Path.GetFileNameWithoutExtension(cue),
                                                  System.IO.Path.GetFileNameWithoutExtension(sourceAudioFile),
                                                  StringComparison.OrdinalIgnoreCase);
                    if (!named && !sameName) continue;

                    string dest = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(cue));
                    if (!System.IO.File.Exists(dest)) System.IO.File.Copy(cue, dest);
                    return;
                }
            }
            catch { /* the sheet is a bonus; a failure must not stop the import */ }
        }

        private void ApplyAudioMetadata(BookData book, string audioFile)
        {
            try
            {
                using (var tf = TagLib.File.Create(audioFile))
                {
                    string album = tf.Tag.Album;
                    string artist = tf.Tag.FirstPerformer;
                    if (string.IsNullOrWhiteSpace(artist)) artist = tf.Tag.FirstAlbumArtist;
                    if (!string.IsNullOrWhiteSpace(album)) book.Title = album.Trim();
                    if (!string.IsNullOrWhiteSpace(artist)) book.Author = artist.Trim();
                }
            }
            catch { }
        }

        private static double ParseDetailTime(string hhmmss)
        {
            TimeSpan t;
            return TimeSpan.TryParse(hhmmss, out t) ? t.TotalSeconds : 0;
        }

        private static string FormatDetailTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private void AddDetailRow(string field, string value)
        {
            string dash = Localization.T("Common.Dash");
            listViewDetails.Items.Add(new ListViewItem(
                new string[] { field, string.IsNullOrEmpty(value) ? dash : value }));
        }

        private void ClearDetails()
        {
            listViewDetails.Items.Clear();
        }

        // ──────────────────────────────────────────────
        // Shelf input
        // ──────────────────────────────────────────────
        private void ListBooks_DoubleClick(object sender, EventArgs e)
        {
            OpenSelectedBook();
        }

        private void ListBooks_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return && !e.Alt)
            {
                OpenSelectedBook();
            }
            else if (e.KeyCode == Keys.F2)
            {
                RenameSelectedBook();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedBook();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
            {
                // At the selected row, not at the corner: a menu that opens where
                // the selection is, is where a sighted user expects it, and it
                // costs a keyboard user nothing.
                // Whichever list the key came from — the menu acts on the book
                // that answers, so it must open beside that book.
                ListView from = sender as ListView ?? listBooks;
                ListViewItem sel = from.SelectedItems.Count > 0 ? from.SelectedItems[0] : null;
                ShowBookMenu(from, sel != null
                    ? new Point(sel.Bounds.Left + 20, sel.Bounds.Bottom)
                    : new Point(0, 0));
                e.Handled = true;
            }
        }

        /// <summary>Opens the shelf's menu, or does nothing when there is no book
        /// to act on. A ContextMenu cannot cancel its own Popup the way a strip
        /// could, so the empty shelf is caught here instead.</summary>
        private void ShowBookMenu(Control over, Point at)
        {
            if (bookMenu == null || GetSelectedBook() == null) return;
            bookMenu.Show(over ?? listBooks, at);
        }

        // ──────────────────────────────────────────────
        // Actions on the selected book
        // ──────────────────────────────────────────────
        private void OpenSelectedBook()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;
            SelectedBook = book;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        // Mark the selected book as read (100%) or unread (0%, rewound). Marking
        // the *active* book as read deactivates it in the player: we flag it so the
        // owner (Form1) can unload it once the Library dialog closes.
        private void MarkSelected(bool read)
        {
            BookData book = GetSelectedBook();
            if (book == null) return;

            if (read)
            {
                book.PercentListened = 100;
                if (PathsEqual(book.FolderPath, activeBookFolderPath))
                {
                    ActiveBookMarkedRead = true;
                    // Unload it from the player right now (releases mpv's file
                    // handle) and drop our "active" reference, so it can be
                    // deleted immediately without loading another book first.
                    unloadActiveBook?.Invoke();
                    activeBookFolderPath = null;
                }
            }
            else
            {
                book.PercentListened = 0;
                book.LastPosition = "00:00:00";
                book.TextPosition = 0;
            }
            book.Save();
            // The book just changed group (Read / Unread) — rebuild and follow it.
            RebuildShelf(book);
        }

        private void SetSelectedFavorite(bool favorite)
        {
            BookData book = GetSelectedBook();
            if (book == null) return;
            book.Favorite = favorite;
            book.Save();
            RebuildShelf(book);
        }

        private void RenameSelectedBook()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;

            // DAISY carries a separate author + title (both drive the shelf
            // "Author — Title" line), so it gets two edit boxes. Plain audio
            // has only a single display name — one box, as before.
            string newAuthor = book.Author ?? "";
            string newTitle = book.Title ?? "";
            if (!ShowRenameDialog(book.IsDaisy, ref newAuthor, ref newTitle))
                return; // cancelled

            newAuthor = newAuthor.Trim();
            newTitle = newTitle.Trim();
            if (newTitle.Length == 0) return; // a title is required

            if (newTitle == (book.Title ?? "") && newAuthor == (book.Author ?? ""))
                return; // nothing changed

            // Rename changes only the metadata in Book.ini —
            // the folder on disk is untouched by design.
            book.Title = newTitle;
            if (book.IsDaisy) book.Author = newAuthor;
            book.Save();
            RebuildShelf(book);
        }

        /// <summary>Rename editor. With includeAuthor (DAISY) it shows Author +
        /// Title boxes; otherwise a single name box. Returns false if cancelled;
        /// on OK writes the edited values back through the ref parameters.</summary>
        private bool ShowRenameDialog(bool includeAuthor, ref string author, ref string title)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = Localization.T("Dialog.Rename.Title");
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;

                int y = 10;
                TextBox tbAuthor = null;

                if (includeAuthor)
                {
                    Label lblAuthor = new Label();
                    lblAuthor.Text = Localization.T("Dialog.Rename.AuthorLabel");
                    lblAuthor.Location = new Point(10, y);
                    lblAuthor.Size = new Size(400, 18);
                    y += 22;

                    tbAuthor = new TextBox();
                    tbAuthor.Location = new Point(10, y);
                    tbAuthor.Size = new Size(400, 24);
                    tbAuthor.Text = author;
                    tbAuthor.AccessibleName = Localization.T("Dialog.Rename.AuthorLabel");
                    y += 34;

                    dlg.Controls.Add(lblAuthor);
                    dlg.Controls.Add(tbAuthor);
                }

                Label lblTitle = new Label();
                lblTitle.Text = Localization.T(includeAuthor ? "Dialog.Rename.TitleLabel" : "Dialog.Rename.Prompt");
                lblTitle.Location = new Point(10, y);
                lblTitle.Size = new Size(400, 18);
                y += 22;

                TextBox tbTitle = new TextBox();
                tbTitle.Location = new Point(10, y);
                tbTitle.Size = new Size(400, 24);
                tbTitle.Text = title;
                tbTitle.AccessibleName = Localization.T(includeAuthor ? "Dialog.Rename.TitleLabel" : "Dialog.Rename.Prompt");
                tbTitle.SelectAll();
                y += 36;

                Button ok = new Button();
                ok.Text = Localization.T("Btn.OK");
                ok.Size = new Size(100, 30);
                ok.Location = new Point(200, y);
                ok.DialogResult = DialogResult.OK;

                Button cancel = new Button();
                cancel.Text = Localization.T("Btn.Cancel");
                cancel.Size = new Size(100, 30);
                cancel.Location = new Point(310, y);
                cancel.DialogResult = DialogResult.Cancel;
                y += 40;

                dlg.Controls.Add(lblTitle);
                dlg.Controls.Add(tbTitle);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.ClientSize = new Size(420, y);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return false;

                if (tbAuthor != null) author = tbAuthor.Text;
                title = tbTitle.Text;
                return true;
            }
        }

        /// <summary>Removes every book from the library (folders and their files),
        /// after a strong confirmation. The book currently open in the player is
        /// left in place — it can't be deleted while active.</summary>
        private void ClearLibrary()
        {
            int count = books.Count;
            if (count == 0)
            {
                MessageForm.ShowInfo(this, Localization.T("Dialog.ClearLibrary.Empty"), Localization.T("Dialog.ClearLibrary.Title"));
                return;
            }

            // Default No — this is destructive.
            bool yes = MessageForm.ShowConfirm(this,
                Localization.T("Dialog.ClearLibrary.Message", count),
                Localization.T("Dialog.ClearLibrary.Title"), defaultToNo: true);
            if (!yes) return;

            int deleted = 0, skipped = 0;
            foreach (BookData book in books.ToList())
            {
                // Never delete the book currently open in the player.
                if (PathsEqual(book.FolderPath, activeBookFolderPath)) { skipped++; continue; }
                try
                {
                    if (System.IO.Directory.Exists(book.FolderPath))
                        System.IO.Directory.Delete(book.FolderPath, true);
                    deleted++;
                }
                catch { skipped++; }   // locked/in-use folder — leave it, report it
            }

            LoadBooks();
            string msg = Localization.T("Dialog.ClearLibrary.Done", deleted);
            if (skipped > 0)
                msg += " " + Localization.T("Dialog.ClearLibrary.Skipped", skipped);
            MessageForm.ShowInfo(this, msg, Localization.T("Dialog.ClearLibrary.Title"));
        }

        private void DeleteSelectedBook()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;

            string title = book.Title;
            string folderPath = book.FolderPath;

            if (PathsEqual(folderPath, activeBookFolderPath))
            {
                MessageForm.ShowInfo(this,
                    Localization.T("Dialog.ActiveBook.Message", title),
                    Localization.T("Dialog.ActiveBook.Title"));
                return;
            }

            // No explicit default here in the original either — Enter deletes.
            // Preserved as-is rather than quietly making it safer; see CLAUDE.md.
            bool yes = MessageForm.ShowConfirm(this,
                Localization.T("Dialog.ConfirmDelete.Message", title),
                Localization.T("Dialog.ConfirmDelete.Title"));
            if (!yes) return;

            int oldIdx = listBooks.SelectedItems.Count > 0 ? listBooks.SelectedItems[0].Index : 0;

            try
            {
                if (System.IO.Directory.Exists(folderPath))
                    System.IO.Directory.Delete(folderPath, true);

                books.Remove(book);
                RebuildShelf(null);
                SelectNearestBook(oldIdx);
            }
            catch (Exception ex)
            {
                MessageForm.ShowInfo(this, Localization.T("Dialog.DeleteError.Message", ex.Message), Localization.T("Common.Error"));
            }
        }

        /// <summary>
        /// Selects the book nearest to the given index. Used after deleting,
        /// so focus stays in place instead of jumping back to the top.
        /// </summary>
        private void SelectNearestBook(int preferredIdx)
        {
            if (listBooks.Items.Count == 0)
            {
                ClearDetails();
                return;
            }

            if (preferredIdx < 0) preferredIdx = 0;
            if (preferredIdx >= listBooks.Items.Count) preferredIdx = listBooks.Items.Count - 1;

            ListViewItem item = listBooks.Items[preferredIdx];
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }

        private void ShowProperties()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;
            using (PropertiesForm dlg = new PropertiesForm(book))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    RebuildShelf(book); // title/author may have changed elsewhere; keep selection
            }
        }

        // ──────────────────────────────────────────────
        // Import
        // ──────────────────────────────────────────────
        private string BuildFileFilter()
        {
            return
                Localization.T("Filter.Audiobooks") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc|" +
                // Documents only. The braille formats have their own entry below
                // — the groups are disjoint, or the split would say nothing.
                Localization.T("Filter.TextBooks") + "|*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3|" +
                Localization.T("Filter.BrailleBooks") + "|*.brf;*.brl;*.bra;*.i55;*.dxb|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc;*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra;*.i55;*.dxb;*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllFiles") + "|*.*";
        }

        private void MenuFileOpenFile_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = BuildFileFilter();
                // 1 Audiobooks, 2 Text books, 3 Braille books, 4 Archives,
                // 5 All supported files, 6 All files. One-based, and it moved
                // when braille was given an entry of its own — a stale index
                // here silently opens the dialog on the wrong kind.
                ofd.FilterIndex = 5;
                ofd.Title = Localization.T("Library.ImportFile.Title");
                // Reopen where the user last browsed, the same as Open folder.
                // Windows usually does this by itself, but only usually, and the
                // two dialogs disagreeing was the only reason for the difference.
                if (!string.IsNullOrEmpty(appSettings.LastImportFileFolder)
                    && System.IO.Directory.Exists(appSettings.LastImportFileFolder))
                    ofd.InitialDirectory = appSettings.LastImportFileFolder;
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try { appSettings.SetLastImportFileFolder(
                        System.IO.Path.GetDirectoryName(ofd.FileName)); }
                    catch { }
                    ImportFile(ofd.FileName);
                }
            }
        }

        private void MenuFileOpenFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = Localization.T("Library.ImportFolder.Description");
                // Reopen where the user last browsed.
                if (!string.IsNullOrEmpty(appSettings.LastImportFolder)
                    && System.IO.Directory.Exists(appSettings.LastImportFolder))
                    fbd.SelectedPath = appSettings.LastImportFolder;
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    appSettings.SetLastImportFolder(fbd.SelectedPath);
                    ImportFolder(fbd.SelectedPath);
                }
            }
        }

        /// <summary>Unpacks an EPUB that carries media overlays and sets it up as
        /// a hybrid. False — having changed nothing — for an ordinary EPUB, which
        /// then takes the plain document path.
        ///
        /// <para>It has to be unpacked rather than read in place: the audio is
        /// inside the zip, and mpv plays files. Everything comes out, not only
        /// the sound, because the SMILs and the XHTML are what the join is built
        /// from and a reader may want the rest of the package later.</para></summary>
        private bool ImportNarratedEpub(string filePath, string destFolder, BookData imported)
        {
            if (!string.Equals(System.IO.Path.GetExtension(filePath), ".epub",
                               StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                // Peek before unpacking eighty megabytes to find out it was a
                // novel: an overlay book has SMIL beside its audio.
                bool narrated = false;
                using (var z = System.IO.Compression.ZipFile.OpenRead(filePath))
                {
                    bool smil = false, audio = false;
                    foreach (var e in z.Entries)
                    {
                        string x = System.IO.Path.GetExtension(e.FullName).ToLowerInvariant();
                        if (x == ".smil") smil = true;
                        else if (x == ".mp3" || x == ".m4a" || x == ".mp4" || x == ".ogg") audio = true;
                        if (smil && audio) { narrated = true; break; }
                    }
                }
                if (!narrated) return false;

                System.IO.Compression.ZipFile.ExtractToDirectory(filePath, destFolder);
                if (EpubOverlayImporter.Setup(imported, destFolder)) return true;

                // Unpacked but not joinable — a book with overlays we could not
                // follow. Leave the folder as it is and let the document path
                // have it, so at least the words arrive.
                return false;
            }
            catch { return false; }
        }

        private void ImportFile(string filePath)
        {
            ImportFileCore(filePath, false);
        }

        /// <summary>Imports one file as its own book. Returns true on success.
        /// When <paramref name="quiet"/> is true (batch folder import) it shows no
        /// success/error dialog and does not refresh the shelf — the caller does
        /// that once for the whole batch.</summary>
        private bool ImportFileCore(string filePath, bool quiet)
        {
            string ignored;
            return ImportFileCore(filePath, quiet, out ignored);
        }

        /// <param name="why">Why it did not go in, when it did not: something
        /// short and already known at the point of failure. A batch import shows
        /// these beside the file names, because a count of what is missing is not
        /// something a reader can act on.</param>
        private bool ImportFileCore(string filePath, bool quiet, out string why)
        {
            why = null;
            string destFolder = null;
            bool createdFolder = false;
            try
            {
                string sourceName = System.IO.Path.GetFileName(filePath);
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                // Opening a DAISY navigation file (ncc.html / .opf / .ncx)
                // imports the WHOLE book from its containing folder — otherwise
                // ncc.html would be mistaken for a plain HTML text book. Falls
                // through when the folder isn't actually DAISY.
                string lower = sourceName.ToLower();
                if (lower == "ncc.html" || lower == "ncc.htm" || ext == ".opf" || ext == ".ncx")
                {
                    if (ImportDaisyFolder(System.IO.Path.GetDirectoryName(filePath))) return true;
                }

                bool isArchive = LibraryScanner.IsExtractableArchive(sourceName);
                // A .zip that wraps an epub (how most libraries package them) is a
                // text import, not a generic archive.
                bool isTextImport = TextExtractor.IsTextImport(filePath);

                // Multi-volume sets fold to one clean folder name (name.7z.001
                // → name, name.part1.rar → name).
                string bookName = isArchive
                    ? LibraryScanner.BaseArchiveName(filePath)
                    : System.IO.Path.GetFileNameWithoutExtension(filePath);
                // Batch folder import: give each file its own folder even when two
                // files share a base name (e.g. "02 Exile.pdf" + "02 Exile.azw3"),
                // disambiguating by format, so neither book is silently overwritten.
                // Single Add File keeps reusing the folder (re-import = update).
                destFolder = quiet
                    ? MakeUniqueBookFolder(bookName, ext)
                    : System.IO.Path.Combine(appSettings.LibraryPath, bookName);

                createdFolder = !System.IO.Directory.Exists(destFolder);
                // Importing over a book that is already on the shelf. Nobody
                // remembered deciding this (Gordan, 2026-08-03), and what it
                // actually does is worth being asked about: the reading position
                // and the bookmarks SURVIVE, audio files already there are left
                // alone — but content.txt is written again. For a text book that
                // means the words can move while the position stays where it was,
                // and the reader lands somewhere they never stopped.
                if (!createdFolder && !quiet)
                {
                    if (!MessageForm.ShowConfirm(this,
                            Localization.T("Dialog.ReimportExisting.Message", bookName),
                            Localization.T("Dialog.ReimportExisting.Title"), true))
                        return false;
                }
                if (createdFolder)
                    System.IO.Directory.CreateDirectory(destFolder);

                BookData imported = new BookData(destFolder);

                if (isArchive && !isTextImport)
                {
                    // Extract straight into the book's permanent library
                    // folder — no temp staging. Multi-volume sets are pulled
                    // together from the first part; if the archive is encrypted
                    // the user is prompted for a password (held in memory only).
                    // Archives commonly wrap their content in a single
                    // subfolder; flatten that up so the book still lands exactly
                    // at destFolder regardless of how it was packed. The source
                    // archive itself is left untouched (it usually lives outside
                    // the library).
                    int pwAttempts = 0;
                    // Extract on a background thread behind a progress dialog so
                    // the window stays responsive; surface the outcome through the
                    // same OperationCanceledException / Exception paths as before.
                    using (ExtractProgressForm prog = new ExtractProgressForm(filePath, destFolder,
                        owner => ArchivePasswordPrompt.Show(owner, sourceName, pwAttempts++ > 0)))
                    {
                        prog.ShowDialog(this);
                        if (prog.Cancelled) throw new OperationCanceledException();
                        if (prog.Error != null) throw prog.Error;
                    }
                    // Name the book after the folder closest to the files (the
                    // wrapper the archive packed everything into), not the
                    // archive file itself.
                    destFolder = LibraryScanner.ResolveBookFolder(destFolder, appSettings.LibraryPath);
                    imported = new BookData(destFolder);

                    // DAISY book? Build the timeline in reading order (from the
                    // navigation), flattening any nested export folder to root
                    // first, and take the title from the DAISY metadata.
                    DaisyBook daisy = DaisyParser.TryParse(destFolder);
                    if (daisy != null && DaisyTextExtractor.IsTextDaisy(daisy))
                    {
                        // Text-only DAISY → read by TTS like any text book.
                        DaisyTextExtractor.SetupTextBook(imported, destFolder, daisy, appSettings.UseMetadata);
                    }
                    else if (daisy != null)
                    {
                        LibraryScanner.FlattenDaisyToRoot(destFolder, daisy.ContentRoot);
                        imported.BuildChaptersFromDaisy(DaisyParser.TryParse(destFolder));
                        // Text+audio DAISY: keep the text as a second output too.
                        DaisyTextExtractor.SetupHybrid(imported, destFolder, daisy);
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
                            imported.BuildChaptersFromFolder(audioFiles.ToArray());
                        }
                        else
                        {
                            // Reserved slot for other archived formats. Downloads
                            // often arrive zipped and may hold formats we don't
                            // fully handle yet (text books — .epub/.pdf/… ). The
                            // content is already extracted; for now the book
                            // still lands in the library with a detected format
                            // label. Future format handlers (e.g. a text-book
                            // branch) plug in here.
                            imported.Format = LibraryScanner.DetectFormat(destFolder);
                        }
                    }
                }
                else if (Array.IndexOf(LibraryScanner.AudioExtensions, ext) >= 0)
                {
                    string destFile = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(filePath));
                    if (!System.IO.File.Exists(destFile))
                        System.IO.File.Copy(filePath, destFile);
                    // A CUE sheet lying beside the chosen file belongs to it: bring
                    // it along, so the book keeps its own copy and its track marks
                    // become chapters (BuildChaptersFromFolder reads it).
                    CopyCueSheet(filePath, destFolder);
                    // Build duration/chapters up front so it isn't 00:00:00 until
                    // first played; also stores the detailed format string.
                    imported.BuildChaptersFromFolder(new string[] { destFile });
                    // Title/author from the Album/Artist tags when the user opted
                    // in — the plain Title tag is unreliable for audiobooks (track
                    // ranges, or empty), so Album is the clean book title.
                    if (appSettings.UseMetadata)
                        ApplyAudioMetadata(imported, destFile);
                    // M4B: parse the embedded chapter marks (title + time) so the
                    // player can navigate by chapter. Falls back to plain single-
                    // file audio when the file has no chapters.
                    if (M4bParser.IsM4bFile(destFile))
                    {
                        M4bBook m4b = M4bParser.TryParse(destFile);
                        if (m4b != null && m4b.HasChapters)
                            imported.SetM4bChapters(m4b.Chapters);
                    }
                }
                else if (isTextImport && ImportNarratedEpub(filePath, destFolder, imported))
                {
                    // An EPUB with media overlays is a narrated book, not a
                    // document: it carries the recording AND the words, joined
                    // point by point, exactly as a text+audio DAISY does. Read as
                    // a document it would have come in as text and been given a
                    // synthesiser, with the narrator left sealed in the zip — the
                    // one thing its reader came for. Handled above the plain text
                    // branch for that reason; everything after this is done.
                }
                else if (isTextImport)
                {
                    // Document (or a zip-wrapped epub) → extract to content.txt
                    // (the reader's input; TtsReader cleans it on load).
                    // Structured formats also carry a heading list + metadata.
                    TextDoc doc = TextExtractor.Extract(filePath);
                    if (doc.DrmProtected)
                    {
                        if (createdFolder) TryDeleteFolder(destFolder);
                        why = Localization.T("Dialog.Skipped.Drm");
                        if (!quiet)
                            MessageForm.ShowInfo(this, Localization.T("Dialog.DrmProtected.Message"),
                                Localization.T("Dialog.DrmProtected.Title"));
                        return false;
                    }
                    // Clean here, once, with the heading and page offsets moving
                    // with the text — what is written is exactly what the reader
                    // will read, so no mark can drift under it later.
                    TextCleaner.CleanDoc(doc);
                    imported.TextCleaned = true;
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(destFolder, "content.txt"),
                        doc.Text ?? "", new System.Text.UTF8Encoding(false));
                    // Title/author from embedded metadata only when the user
                    // opted in (else the file name stands). Producer has no
                    // name-based fallback, so it always comes from metadata.
                    if (appSettings.UseMetadata)
                    {
                        if (!string.IsNullOrWhiteSpace(doc.Title)) imported.Title = doc.Title;
                        if (!string.IsNullOrWhiteSpace(doc.Author)) imported.Author = doc.Author;
                    }
                    imported.Producer = BookData.NormalizeProducer(doc.Producer);
                    imported.Publisher = BookData.NormalizeProducer(doc.Publisher);
                    // What language it is in, so it gets read by a voice that
                    // speaks it. The file's own claim is only a claim — the text
                    // overrules it when it is sure (see LanguageDetector.Resolve).
                    imported.TextLanguage = LanguageDetector.Resolve(doc.Language, doc.Text);
                    imported.SetTextHeadings(doc.Headings);
                    imported.SetTextPages(doc.Pages);
                    // Braille: remember which table produced the text, and keep the
                    // original cells beside it so the reading can be redone with a
                    // different table if the auto-detected one was wrong.
                    if (!string.IsNullOrEmpty(doc.BrailleTable))
                    {
                        imported.BrailleTable = doc.BrailleTable;
                        try
                        {
                            string keep = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(filePath));
                            if (!System.IO.File.Exists(keep)) System.IO.File.Copy(filePath, keep);
                        }
                        catch { }
                    }
                    // Record the real source format (MS Word Docx / EPUB / RTF …),
                    // not the extracted content.txt. A .zip that actually wraps an
                    // epub reports as EPUB.
                    string srcExt = System.IO.Path.GetExtension(filePath);
                    if (string.Equals(srcExt, ".zip", StringComparison.OrdinalIgnoreCase)
                        && EpubParser.WrapsEpub(filePath))
                        srcExt = ".epub";
                    imported.Format = BookData.FriendlyFormatName(srcExt);
                }
                else
                {
                    string destFile = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(filePath));
                    if (!System.IO.File.Exists(destFile))
                        System.IO.File.Copy(filePath, destFile);
                    imported.Format = BookData.FriendlyFormatName(ext);
                }

                imported.Save();
                if (!quiet)
                {
                    LoadBooks();
                    MessageForm.ShowInfo(this, Localization.T("Dialog.ImportSuccess.Message"), Localization.T("Dialog.ImportSuccess.Title"));
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                // User cancelled the archive password prompt — quietly undo the
                // empty folder we just made, no error dialog.
                if (createdFolder) TryDeleteFolder(destFolder);
                why = Localization.T("Dialog.Skipped.Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                if (createdFolder) TryDeleteFolder(destFolder);
                // The exception's own message, not a category: it is already the
                // most specific thing anyone knows, and it costs no extra work.
                why = ex.Message;
                if (!quiet)
                    MessageForm.ShowInfo(this, Localization.T("Dialog.ImportError.Message", ex.Message), Localization.T("Common.Error"));
                return false;
            }
        }

        /// <summary>
        /// Imports an already-extracted DAISY book folder as a single book:
        /// copies the whole tree into the library, flattens the DAISY export to
        /// the book root, and builds the reading-order timeline + Author/Title
        /// from the navigation (the same finish as the archive-import path).
        /// Returns false when the folder is not a DAISY book (caller falls back
        /// to the generic import); true when it handled it (success or error).
        /// </summary>
        private bool ImportDaisyFolder(string sourceFolder)
        {
            if (string.IsNullOrEmpty(sourceFolder) || !System.IO.Directory.Exists(sourceFolder))
                return false;
            if (DaisyParser.TryParse(sourceFolder) == null)
                return false;   // not DAISY — let the caller handle it normally

            string name = System.IO.Path.GetFileName(sourceFolder.TrimEnd('\\', '/'));
            string destFolder = System.IO.Path.Combine(appSettings.LibraryPath, name);
            bool created = false;
            try
            {
                // Copy into the library unless the folder already IS the target
                // (e.g. re-importing something already inside the library).
                if (!PathsEqual(sourceFolder, destFolder))
                {
                    created = !System.IO.Directory.Exists(destFolder);
                    CopyTree(sourceFolder, destFolder);
                }

                DaisyBook daisy = DaisyParser.TryParse(destFolder);
                if (daisy == null) throw new Exception("DAISY navigation not found after copy.");

                BookData imported = new BookData(destFolder);
                if (DaisyTextExtractor.IsTextDaisy(daisy))
                    DaisyTextExtractor.SetupTextBook(imported, destFolder, daisy, appSettings.UseMetadata);
                else
                {
                    LibraryScanner.FlattenDaisyToRoot(destFolder, daisy.ContentRoot);
                    imported.BuildChaptersFromDaisy(DaisyParser.TryParse(destFolder));
                    DaisyTextExtractor.SetupHybrid(imported, destFolder, daisy);
                }
                imported.Save();

                LoadBooks();
                MessageForm.ShowInfo(this, Localization.T("Dialog.ImportSuccess.Message"), Localization.T("Dialog.ImportSuccess.Title"));
            }
            catch (Exception ex)
            {
                if (created) TryDeleteFolder(destFolder);
                MessageForm.ShowInfo(this, Localization.T("Dialog.ImportError.Message", ex.Message), Localization.T("Common.Error"));
            }
            return true;   // handled either way — don't fall through to generic import
        }

        /// <summary>Recursively copies a folder tree (skipping any Book.ini).</summary>
        private static void CopyTree(string src, string dst)
        {
            System.IO.Directory.CreateDirectory(dst);
            foreach (string f in System.IO.Directory.GetFiles(src))
            {
                if (System.IO.Path.GetFileName(f).ToLower() == "book.ini") continue;
                string d = System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f));
                if (!System.IO.File.Exists(d)) System.IO.File.Copy(f, d);
            }
            foreach (string sub in System.IO.Directory.GetDirectories(src))
                CopyTree(sub, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(sub)));
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

        private void ImportFolder(string folderPath)
        {
            // Archives used to make this refuse the whole folder. They are
            // imported now, one book each, through exactly the path Open file
            // uses — into the library, with the source left alone.
            //
            // The old refusal was not about multi-volume paths, whatever its
            // comment said: the recognition (IsExtractableArchive picks the
            // entry point, IsVolumeContinuation skips the rest, GetFileParts
            // gathers the set) has always worked, and the background scan uses
            // it on every start. What it was really protecting against is one
            // line that used to sit inside the scan — LibraryScanner unpacked an
            // archive INTO the folder it was scanning and then deleted every
            // volume of it. Right for library-owned space, destruction of the
            // user's own disk here. That is no longer something a caller has to
            // avoid: scanning cannot write at all now, and the unpacking lives
            // in LibraryScanner.AbsorbArchives, which is called by name and only
            // ever on the library.
            try
            {
                // Everything is worked out first, and nothing is copied until the
                // reader has seen the numbers and said yes. PlanImport carries the
                // whole of the agreed grouping — including DAISY at every level,
                // which the old single call at the top of this method missed.
                var plan = new ImportPlan();
                PlanImport(folderPath, plan);
                List<string> entryPoints, orphanVolumes;
                CollectArchives(folderPath, out entryPoints, out orphanVolumes);
                plan.Archives.Clear();
                plan.Archives.AddRange(entryPoints);

                int total = plan.Books;
                if (total == 0)
                {
                    MessageForm.ShowInfo(this, Localization.T("Dialog.NoBooksFound.Message"), Localization.T("Dialog.NoBooksFound.Title"));
                    return;
                }
                // ALWAYS ask, whatever the number (Gordan, 2026-08-03). A
                // threshold nobody can see is worse than no threshold: under it
                // the reader was told nothing at all, and over it a question
                // appeared out of nowhere. The count is the useful part, and it
                // is useful at three books as much as at three hundred.
                //
                // More than one book is a BULK import, and that gets the full
                // warning: the breakdown by kind, where the books are going,
                // that the source is left alone, that the separation is not
                // guaranteed perfect, and that it takes a while. Gordan's
                // words. One book keeps the one-line question — there is
                // nothing to weigh.
                if (total == 1)
                {
                    if (!MessageForm.ShowConfirm(this, Localization.T("Dialog.ConfirmImport.One"),
                            Localization.T("Dialog.ConfirmImport.Title")))
                        return;
                }
                else
                {
                    // Special formats are DAISY, a narrated EPUB and braille —
                    // the three that are not plain documents and not plain audio.
                    int special = plan.Daisy.Count + CountSpecialFormats(plan.TextFiles);
                    if (!MessageForm.ShowContinue(this,
                            Localization.T("Dialog.ConfirmImport.Message", total,
                                plan.AudioBooks, plan.TextFiles.Count - (special - plan.Daisy.Count),
                                special, plan.Archives.Count, appSettings.LibraryPath),
                            Localization.T("Dialog.ConfirmImport.Title")))
                        return;
                }

                int imported = 0;
                var skipped = new List<string>();

                // A continuation volume whose first part is not there. Nothing
                // can start it, so the set would simply never appear — and the
                // reader would be left with a book missing and no word about it.
                // This is the one thing the blanket refusal was catching, and it
                // is worth keeping as a NAMED line rather than as a wall.
                foreach (string v in orphanVolumes)
                    skipped.Add(System.IO.Path.GetFileName(v) + " — " +
                                Localization.T("Dialog.Skipped.MissingFirstVolume"));

                // Archives and single files, each its own book, through the same
                // path Open file uses. Entry points only — the volumes behind
                // them are pulled in by SharpCompress from the first part.
                foreach (string f in plan.Archives)
                    if (ImportOne(f, skipped)) imported++;
                foreach (string f in plan.TextFiles)
                    if (ImportOne(f, skipped)) imported++;
                foreach (string f in plan.AudioOrphans)
                    if (ImportOne(f, skipped)) imported++;

                // A DAISY book comes in whole, with its navigation. Found at
                // every level now, not only on the folder that was picked.
                foreach (string d in plan.Daisy)
                    if (ImportDaisyFolder(d)) imported++;
                    else skipped.Add(System.IO.Path.GetFileName(d));

                // A folder of audio is one book: its own files.
                foreach (string bookFolder in plan.AudioFolders)
                    if (CopyAudioInto(BookFolderFor(bookFolder), new[] { bookFolder })) imported++;

                // A book split across discs is ALSO one book — every disc's files
                // into a single folder, named after the folder that holds them.
                foreach (string[] discs in plan.DiscSets)
                {
                    string parent = System.IO.Path.GetDirectoryName(discs[0]);
                    if (CopyAudioInto(BookFolderFor(parent), discs)) imported++;
                }

                LoadBooks();
                string msg = Localization.T("Dialog.ImportFolderSuccess.Message", imported);
                if (skipped.Count == 0)
                {
                    MessageForm.ShowInfo(this, msg, Localization.T("Dialog.ImportFolderSuccess.Title"));
                }
                else
                {
                    // NAMES, not a number (Gordan, 2026-08-03). "3 skipped" tells
                    // a reader that something is missing and nothing about which
                    // book or what to do; the name and the reason are what makes
                    // it actionable. A list is material to read, not an event to
                    // dismiss, so it goes in the readable box rather than a
                    // message box (§ a hint is material; a notice is an event).
                    msg += " " + Localization.T("Dialog.ImportFolderSuccess.Skipped", skipped.Count)
                         + "\r\n\r\n" + Localization.T("Dialog.ImportFolderSuccess.SkippedList")
                         + "\r\n" + string.Join("\r\n", skipped.ToArray());
                    MessageForm.ShowHint(this, msg, Localization.T("Dialog.ImportFolderSuccess.Title"));
                }
            }
            catch (Exception ex)
            {
                MessageForm.ShowInfo(this, Localization.T("Dialog.ImportFolderError.Message", ex.Message), Localization.T("Common.Error"));
            }
        }

        /// <summary>One file, one book, quietly — and the reason it did not go in
        /// added to the list by name when it did not.</summary>
        private bool ImportOne(string file, List<string> skipped)
        {
            string why;
            if (ImportFileCore(file, true, out why)) return true;
            skipped.Add(System.IO.Path.GetFileName(file) +
                        (string.IsNullOrEmpty(why) ? "" : " — " + why));
            return false;
        }

        private string BookFolderFor(string sourceFolder)
        {
            return System.IO.Path.Combine(appSettings.LibraryPath,
                                          System.IO.Path.GetFileName(sourceFolder));
        }

        /// <summary>Copies the audio of one or more source folders into a single
        /// book folder. More than one source is a book split across discs; the
        /// discs' files land side by side and the order comes from their names,
        /// which is why a disc set names its files with the disc in them.
        ///
        /// <para>Text files are left behind on purpose: beside audio they are a
        /// note about the book, not part of it.</para></summary>
        private bool CopyAudioInto(string destFolder, string[] sources)
        {
            try
            {
                bool any = false;
                if (!System.IO.Directory.Exists(destFolder))
                    System.IO.Directory.CreateDirectory(destFolder);
                foreach (string src in sources)
                    foreach (string file in System.IO.Directory.GetFiles(src))
                    {
                        string fn = System.IO.Path.GetFileName(file);
                        if (string.Equals(fn, "book.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsTextBookFile(file)) continue;
                        string destFile = System.IO.Path.Combine(destFolder, fn);
                        if (!System.IO.File.Exists(destFile)) System.IO.File.Copy(file, destFile);
                        any = true;
                    }
                return any;
            }
            catch { return false; }
        }

        /// <summary>What a folder import found, sorted into what each thing will
        /// become. Worked out in full BEFORE anything is copied, because the
        /// warning has to be able to say how many of each kind there are, and
        /// because a reader who sees a number they did not expect must be able
        /// to cancel before a single file moves.</summary>
        private sealed class ImportPlan
        {
            public readonly List<string> Daisy = new List<string>();       // folder → one book
            public readonly List<string> AudioFolders = new List<string>();// folder → one book
            public readonly List<string[]> DiscSets = new List<string[]>();// folders → ONE book
            public readonly List<string> AudioOrphans = new List<string>();// file → one book
            public readonly List<string> TextFiles = new List<string>();   // file → one book
            public readonly List<string> Archives = new List<string>();    // file → one book
            public readonly List<string> OrphanVolumes = new List<string>();

            public int Books
            {
                get
                {
                    return Daisy.Count + AudioFolders.Count + DiscSets.Count
                         + AudioOrphans.Count + TextFiles.Count + Archives.Count;
                }
            }
            public int AudioBooks
            {
                get { return AudioFolders.Count + DiscSets.Count + AudioOrphans.Count; }
            }
        }

        /// <summary>Reads a folder tree and decides what is a book, by the rules
        /// settled with Gordan on 2026-08-03 and measured against a real disk of
        /// 360 top-level items, five levels deep (docs/Open file i Open folder.txt
        /// §3.4b).
        ///
        /// <para><b>Audio.</b> A folder that holds audio IS a book. A folder that
        /// holds audio-bearing SUBFOLDERS is a shelf, and then each of its own
        /// loose files is a book of its own. The one exception is a book split
        /// across discs: subfolders count as parts of a single book only when
        /// every one of them carries a disc WORD beside its number — "Disc 3",
        /// "Disk 16", "D01 Title". A bare number is not enough, because "Wheel of
        /// Time 01" and "Disc 1" have the same shape and opposite meanings.
        /// Without that exception three real books on the sample disk came apart
        /// into 8, 16 and 10 shelf entries.</para>
        ///
        /// <para><b>Text.</b> One book per file — but only where the folder holds
        /// no audio. A text file sitting beside audio is a note ABOUT the book,
        /// not a book: 170 of them on the sample disk, 190 files named
        /// <c>Info.txt</c>. It costs one pdf and one doc that might have been
        /// books, and it keeps about 170 pieces of rubbish off the shelf.</para>
        ///
        /// <para><b>DAISY is checked at EVERY level</b>, not only on the folder
        /// the user picked. All 83 DAISY books on the sample disk sit two or
        /// three levels down; checking only the top would have brought them in as
        /// plain audio folders with their navigation thrown away.</para></summary>
        private static void PlanImport(string folder, ImportPlan plan, int depth = 0)
        {
            if (depth > 8) return;
            string[] files, subs;
            try
            {
                files = System.IO.Directory.GetFiles(folder);
                subs = System.IO.Directory.GetDirectories(folder);
            }
            catch { return; }

            // A DAISY book is one book, whole, and nothing inside it is looked at
            // again — its audio is chapters and its HTML is the text.
            //
            // The test has to be LOCAL. DaisyParser.TryParse searches the whole
            // tree beneath a folder, which is right when you already believe you
            // are standing on a book and catastrophic when you are standing on a
            // shelf: pointed at a disk holding 83 DAISY books it answered "yes"
            // for the ROOT, and the entire import came out as one book. Measured,
            // not imagined.
            if (IsDaisyFolder(files)) { plan.Daisy.Add(folder); return; }

            bool hasAudio = false;
            foreach (string f in files)
            {
                string fn = System.IO.Path.GetFileName(f);
                if (LibraryScanner.IsExtractableArchive(fn)) plan.Archives.Add(f);
                else if (IsAudioFile(f)) hasAudio = true;
            }

            var audioSubs = new List<string>();
            foreach (string s in subs) if (HasAudioAnywhere(s, 0)) audioSubs.Add(s);

            if (audioSubs.Count > 0)
            {
                // Discs of one book, or a shelf of several?
                bool allDiscs = subs.Length > 1 && audioSubs.Count == subs.Length;
                if (allDiscs)
                    foreach (string s in subs)
                        if (!IsDiscMarked(System.IO.Path.GetFileName(s))) { allDiscs = false; break; }

                if (allDiscs) { plan.DiscSets.Add(subs); return; }

                // A shelf: its own loose audio files are each a book.
                foreach (string f in files) if (IsAudioFile(f)) plan.AudioOrphans.Add(f);
            }
            else if (hasAudio)
            {
                plan.AudioFolders.Add(folder);
            }

            // Text is a book only where there is no audio for it to be a note
            // about — and that means no audio in the folder AND none in the
            // folders under it. A note sitting at the head of a shelf ("Info.txt"
            // beside the discs' folders) is still a note; the first version of
            // this rule looked only at the folder's own files and let 38 of them
            // onto the shelf as books.
            if (!hasAudio && audioSubs.Count == 0)
                foreach (string f in files)
                    if (IsTextBookFile(f) && !IsNoteFile(f)) plan.TextFiles.Add(f);

            foreach (string s in subs) PlanImport(s, plan, depth + 1);
        }

        /// <summary>A file that is a note ABOUT a book rather than a book, told
        /// apart by its name because nothing in the structure gives it away.
        ///
        /// <para>Every rule here would rather be structural, and this one cannot
        /// be: a text book folder on the sample disk holds
        /// <c>&lt;Title&gt;.epub</c> and <c>Info.txt</c> side by side, and a note
        /// beside a book looks exactly like a second book. So the list is by
        /// NAME, and it is deliberately tiny — an exact <c>info.txt</c>, and the
        /// leavings of a torrent client. 190 files on that one disk are called
        /// Info.txt; without this they arrive as 190 books.</para>
        ///
        /// <para>Anything longer than a handful of names would be guessing at
        /// what a reader meant to keep. When in doubt it stays a book: an unwanted
        /// entry is deleted in a second, a missing one is never noticed.</para></summary>
        private static bool IsNoteFile(string path)
        {
            string n = System.IO.Path.GetFileName(path).ToLowerInvariant();
            if (n == "info.txt" || n == "info.nfo" || n == "readme.txt") return true;
            if (n.StartsWith("torrent downloaded from")) return true;
            return false;
        }

        /// <summary>Is the navigation of a DAISY book lying in THIS folder — an
        /// <c>ncc.html</c>, or an <c>.opf</c> together with an <c>.ncx</c>? Only
        /// the folder's own files are looked at; whatever is further down belongs
        /// to some other book.</summary>
        private static bool IsDaisyFolder(string[] files)
        {
            bool opf = false, ncx = false;
            foreach (string f in files)
            {
                string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                if (n == "ncc.html" || n == "ncc.htm") return true;
                if (n.EndsWith(".opf")) opf = true;
                else if (n.EndsWith(".ncx")) ncx = true;
            }
            return opf && ncx;
        }

        private static bool IsAudioFile(string path)
        {
            return Array.IndexOf(LibraryScanner.AudioExtensions,
                                 System.IO.Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        private static bool HasAudioAnywhere(string folder, int depth)
        {
            if (depth > 6) return false;
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(folder))
                    if (IsAudioFile(f)) return true;
                foreach (string d in System.IO.Directory.GetDirectories(folder))
                    if (HasAudioAnywhere(d, depth + 1)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>A folder name that says "this is disc N of something", rather
        /// than "this is volume N of a series". The word is what carries it —
        /// see the note on <see cref="PlanImport"/>.</summary>
        private static bool IsDiscMarked(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^(cd|disc|disk|dvd)[\s._-]*\d+$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^d\d+[\s._-]")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"[\s._-](cd|disc|disk)[\s._-]*\d+$")) return true;
            return false;
        }

        /// <summary>How many of the text files are a SPECIAL format rather than a
        /// plain document — braille, or an EPUB that carries its own narration
        /// (Gordan's third column). DAISY does not appear here: a DAISY book is a
        /// folder, and it is taken by <c>ImportDaisyFolder</c> before any of this
        /// runs.
        ///
        /// <para>The narrated EPUB is worth the look inside the zip: it is the
        /// one that arrives believing it is a document and turns out to be a
        /// recording. Only the central directory is read — entry names, not
        /// content — so it costs little even over a folder of them.</para></summary>
        private static int CountSpecialFormats(List<string> textFiles)
        {
            int n = 0;
            foreach (string f in textFiles)
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".brf" || ext == ".brl" || ext == ".bra") { n++; continue; }
                if (ext != ".epub") continue;
                try
                {
                    using (var z = System.IO.Compression.ZipFile.OpenRead(f))
                    {
                        bool smil = false, audio = false;
                        foreach (var e in z.Entries)
                        {
                            string x = System.IO.Path.GetExtension(e.FullName).ToLowerInvariant();
                            if (x == ".smil") smil = true;
                            else if (x == ".mp3" || x == ".m4a" || x == ".mp4" || x == ".ogg") audio = true;
                            if (smil && audio) { n++; break; }
                        }
                    }
                }
                catch { }
            }
            return n;
        }

        /// <summary>Every archive in the folder that is a starting point, and
        /// separately every continuation volume that has no starting point to be
        /// pulled in by.
        ///
        /// <para>The distinction is the whole of it, and it is easy to get
        /// backwards. <b>Starting points</b> are a plain <c>.zip</c> / <c>.7z</c>
        /// / <c>.rar</c>, a <c>.part1.rar</c>, and a <c>.001</c>.
        /// <b>Continuations</b> are <c>.part2.rar</c> upwards, <c>.r00</c>
        /// upwards, <c>.z01</c> upwards and <c>.002</c> upwards — note that the
        /// old RAR scheme numbers its continuations from <c>r00</c> because the
        /// first volume has already used the name <c>.rar</c>, and that a spanned
        /// zip is opened at its <c>.zip</c> while <c>.z01</c> is a part.</para></summary>
        private static void CollectArchives(string folder, out List<string> entryPoints,
                                            out List<string> orphanVolumes)
        {
            entryPoints = new List<string>();
            orphanVolumes = new List<string>();
            CollectArchives(folder, entryPoints, orphanVolumes, 0);
        }

        private static void CollectArchives(string folder, List<string> entryPoints,
                                            List<string> orphanVolumes, int depth)
        {
            if (depth > 4) return;
            try
            {
                string[] files = System.IO.Directory.GetFiles(folder);
                var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                    if (LibraryScanner.IsExtractableArchive(System.IO.Path.GetFileName(f)))
                    {
                        entryPoints.Add(f);
                        stems.Add(LibraryScanner.BaseArchiveName(f));
                    }
                foreach (string f in files)
                {
                    string fn = System.IO.Path.GetFileName(f);
                    if (!LibraryScanner.IsVolumeContinuation(fn)) continue;
                    // Only the FIRST orphan of a set is worth naming; the rest
                    // say the same thing about the same missing file.
                    string stem = LibraryScanner.BaseArchiveName(f);
                    if (stems.Contains(stem)) continue;
                    stems.Add(stem);
                    orphanVolumes.Add(f);
                }
                foreach (string d in System.IO.Directory.GetDirectories(folder))
                    CollectArchives(d, entryPoints, orphanVolumes, depth + 1);
            }
            catch { }
        }

        /// <summary>A free library folder path for a book, disambiguating a
        /// base-name collision by format then a number, so importing two files
        /// that share a name (different formats) keeps both books.</summary>
        private string MakeUniqueBookFolder(string bookName, string ext)
        {
            string lib = appSettings.LibraryPath;
            string path = System.IO.Path.Combine(lib, bookName);
            if (!System.IO.Directory.Exists(path)) return path;

            string e = (ext ?? "").TrimStart('.').ToLowerInvariant();
            string baseName = string.IsNullOrEmpty(e) ? bookName : bookName + " (" + e + ")";
            path = System.IO.Path.Combine(lib, baseName);
            int n = 2;
            while (System.IO.Directory.Exists(path))
                path = System.IO.Path.Combine(lib, baseName + " (" + (n++) + ")");
            return path;
        }

        /// <summary>True if the file is a text-book format we import one-per-file.</summary>
        private static bool IsTextBookFile(string path)
        {
            string fn = System.IO.Path.GetFileName(path).ToLower();
            if (fn == "content.txt" || fn == "book.ini") return false;
            return TextExtractor.IsTextFormat(System.IO.Path.GetExtension(path));
        }

        private static bool FolderHasAudio(string folder)
        {
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(folder))
                    if (Array.IndexOf(LibraryScanner.AudioExtensions, System.IO.Path.GetExtension(f).ToLower()) >= 0)
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>Every loose text-book file under the folder (recursively), each
        /// of which becomes its own book. Skips DAISY subfolders (ncc.html / .opf /
        /// .ncx) — those are whole-book units handled elsewhere.</summary>
        private static List<string> EnumerateTextBookFiles(string folder)
        {
            var result = new List<string>();
            CollectTextBookFiles(folder, result);
            return result;
        }

        private static void CollectTextBookFiles(string folder, List<string> result)
        {
            try
            {
                string[] files = System.IO.Directory.GetFiles(folder);
                // A DAISY/structured folder is one book, not a bag of text files.
                bool daisyLike = files.Any(f =>
                {
                    string n = System.IO.Path.GetFileName(f).ToLower();
                    string e = System.IO.Path.GetExtension(f).ToLower();
                    return n == "ncc.html" || e == ".opf" || e == ".ncx";
                });
                if (!daisyLike)
                    foreach (string f in files)
                        if (IsTextBookFile(f)) result.Add(f);

                foreach (string sub in System.IO.Directory.GetDirectories(folder))
                    CollectTextBookFiles(sub, result);
            }
            catch { }
        }
    }

    /// <summary>
    /// Modal progress dialog that runs an archive extraction on a background
    /// thread so the UI stays responsive (extraction of a large book used to
    /// freeze the window for its whole duration). Shows a determinate bar when
    /// the file count is known (7z/zip) or an indeterminate marquee (RAR,
    /// streamed). Auto-closes when extraction finishes; Error/Cancelled expose
    /// the outcome to the caller, which keeps the original import error handling.
    /// </summary>
    internal class ExtractProgressForm : Form
    {
        public Exception Error { get; private set; }
        public bool Cancelled { get; private set; }

        private readonly string archivePath;
        private readonly string destFolder;
        private readonly Func<IWin32Window, string> passwordProvider;
        private readonly ProgressBar bar;
        private readonly Label status;

        public ExtractProgressForm(string archivePath, string destFolder,
            Func<IWin32Window, string> passwordProvider)
        {
            this.archivePath = archivePath;
            this.destFolder = destFolder;
            this.passwordProvider = passwordProvider;

            Text = Localization.T("Extract.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 96);

            status = new Label
            {
                Location = new Point(12, 14),
                Size = new Size(396, 24),
                Text = Localization.T("Extract.Progress.Preparing"),
                TabStop = true
            };
            status.AccessibleName = status.Text;

            bar = new ProgressBar
            {
                Location = new Point(12, 46),
                Size = new Size(396, 24),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 40
            };

            Controls.Add(status);
            Controls.Add(bar);
            AcceptButton = null;
            CancelButton = null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    LibraryScanner.ExtractArchive(archivePath, destFolder,
                        // Password prompt must run on the UI thread.
                        () => (string)Invoke(new Func<string>(() => passwordProvider(this))),
                        // Progress → marshal onto the UI thread.
                        (done, total) => { try { BeginInvoke(new Action(() => Report(done, total))); } catch { } });
                }
                catch (OperationCanceledException) { Cancelled = true; }
                catch (Exception ex) { Error = ex; }
                finally
                {
                    try { BeginInvoke(new Action(() => { DialogResult = DialogResult.OK; Close(); })); } catch { }
                }
            });
        }

        private void Report(int done, int total)
        {
            if (total > 0)
            {
                if (bar.Style != ProgressBarStyle.Blocks) bar.Style = ProgressBarStyle.Blocks;
                bar.Maximum = total;
                bar.Value = Math.Min(done, total);
                status.Text = Localization.T("Extract.Progress.Determinate", done, total);
            }
            else
            {
                status.Text = Localization.T("Extract.Progress.Indeterminate", done);
            }
            status.AccessibleName = status.Text;
        }
    }
}
