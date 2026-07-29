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
        private ToolStripMenuItem menuView;
        private ToolStripMenuItem menuViewAlphaAsc;
        private ToolStripMenuItem menuViewAlphaDesc;
        private ToolStripMenuItem menuViewDateAsc;
        private ToolStripMenuItem menuViewDateDesc;
        private ToolStripMenuItem menuViewFormatAsc;
        private ToolStripMenuItem menuViewFormatDesc;

        private Panel panelSearch;
        private TextBox tbSearch;
        private ComboBox cbFilter;

        private SplitContainer splitContainer;

        // The shelf is a ListView with native groups (like Explorer's grouped
        // view): group headers are NOT list items, so a screen reader counts
        // only the books ("3 of 5") and announces the group name as context
        // when arrowing into a new group.
        private ListView listBooks;

        private Panel panelDetails;
        private ListView listViewDetails;

        private Panel panelBottom;
        private Button btnRefresh;
        private Button btnOK;
        private Button btnCancel;

        private List<BookData> books;          // all scanned books
        private string currentSortMode = "alpha_asc";

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
            this.activeBookFolderPath = activeBookFolderPath;
            this.unloadActiveBook = unloadActiveBook;
            books = new List<BookData>();
            BuildUI();
            LoadBooks();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Default tab order would land on the search box first;
            // the shelf is the natural starting point.
            listBooks.Focus();
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

            if (listBooks.Focused && keyData == Keys.Tab)
            {
                listViewDetails.Focus();
                if (listViewDetails.Items.Count > 0)
                    listViewDetails.Items[0].Selected = true;
                return true;
            }

            if (listViewDetails.Focused)
            {
                if (keyData == (Keys.Tab | Keys.Shift))
                {
                    listBooks.Focus();
                    return true;
                }
                if (keyData == Keys.Tab)
                {
                    btnRefresh.Focus();
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
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

            menuView = new ToolStripMenuItem(Localization.T("Menu.View"));

            menuViewAlphaAsc = new ToolStripMenuItem(Localization.T("Menu.View.AlphaAsc"));
            menuViewAlphaAsc.Click += (s, e) => SortBooks("alpha_asc");

            menuViewAlphaDesc = new ToolStripMenuItem(Localization.T("Menu.View.AlphaDesc"));
            menuViewAlphaDesc.Click += (s, e) => SortBooks("alpha_desc");

            menuViewDateAsc = new ToolStripMenuItem(Localization.T("Menu.View.DateAsc"));
            menuViewDateAsc.Click += (s, e) => SortBooks("date_asc");

            menuViewDateDesc = new ToolStripMenuItem(Localization.T("Menu.View.DateDesc"));
            menuViewDateDesc.Click += (s, e) => SortBooks("date_desc");

            menuViewFormatAsc = new ToolStripMenuItem(Localization.T("Menu.View.FormatAsc"));
            menuViewFormatAsc.Click += (s, e) => SortBooks("format_asc");

            menuViewFormatDesc = new ToolStripMenuItem(Localization.T("Menu.View.FormatDesc"));
            menuViewFormatDesc.Click += (s, e) => SortBooks("format_desc");

            menuView.DropDownItems.Add(menuViewAlphaAsc);
            menuView.DropDownItems.Add(menuViewAlphaDesc);
            menuView.DropDownItems.Add(new ToolStripSeparator());
            menuView.DropDownItems.Add(menuViewDateAsc);
            menuView.DropDownItems.Add(menuViewDateDesc);
            menuView.DropDownItems.Add(new ToolStripSeparator());
            menuView.DropDownItems.Add(menuViewFormatAsc);
            menuView.DropDownItems.Add(menuViewFormatDesc);

            menuStrip.Items.Add(menuFile);
            menuStrip.Items.Add(menuView);

            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);

            UpdateSortMenuChecks();
        }

        /// <summary>
        /// Marks the active sort mode in the View menu: a visual checkmark
        /// plus a localized text suffix (e.g. "(active)"). The suffix is
        /// there because screen readers don't reliably announce the check
        /// state of MenuStrip items — text is always read. If the suffix
        /// is unwanted, empty the "Menu.View.ActiveMark" value in the
        /// .lang file and only the checkmark remains.
        /// </summary>
        private void UpdateSortMenuChecks()
        {
            ApplySortMark(menuViewAlphaAsc, "Menu.View.AlphaAsc", "alpha_asc");
            ApplySortMark(menuViewAlphaDesc, "Menu.View.AlphaDesc", "alpha_desc");
            ApplySortMark(menuViewDateAsc, "Menu.View.DateAsc", "date_asc");
            ApplySortMark(menuViewDateDesc, "Menu.View.DateDesc", "date_desc");
            ApplySortMark(menuViewFormatAsc, "Menu.View.FormatAsc", "format_asc");
            ApplySortMark(menuViewFormatDesc, "Menu.View.FormatDesc", "format_desc");
        }

        private void ApplySortMark(ToolStripMenuItem item, string langKey, string mode)
        {
            bool active = currentSortMode == mode;
            item.Checked = active;

            string mark = Localization.T("Menu.View.ActiveMark");
            item.Text = active && mark.Length > 0 && mark != "Menu.View.ActiveMark"
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
                if (e.Button == MouseButtons.Right) ShowBookMenu(new Point(e.X, e.Y));
            };

            splitContainer.Panel1.Controls.Add(listBooks);

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

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.Size = new Size(100, 35);
            btnOK.Location = new Point(580, 12);
            btnOK.AccessibleName = Localization.T("Btn.OK.Accessible");
            btnOK.Click += (s, e) => OpenSelectedBook();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.Size = new Size(100, 35);
            btnCancel.Location = new Point(690, 12);
            btnCancel.AccessibleName = Localization.T("Btn.Cancel.Accessible");
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

            // Order follows the sort menu; the Now-reading book is then pinned
            // to the very top (it can appear under All / Reading / Favorites).
            list.Sort(GetComparer());
            for (int i = 0; i < list.Count; i++)
            {
                if (IsNowReading(list[i]))
                {
                    BookData nr = list[i];
                    list.RemoveAt(i);
                    list.Insert(0, nr);
                    break;
                }
            }

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
        private bool IsNowReading(BookData b)
        {
            string last = appSettings.LastOpenedBookPath;
            return !string.IsNullOrEmpty(last)
                && PathsEqual(b.FolderPath, last)
                && GetCategory(b) == CatReading;
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

        /// <summary>Returns the selected book, or null if nothing is selected.</summary>
        private BookData GetSelectedBook()
        {
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

        private Comparison<BookData> GetComparer()
        {
            switch (currentSortMode)
            {
                case "alpha_desc":
                    return (a, b) => string.Compare(b.Title, a.Title, StringComparison.CurrentCultureIgnoreCase);
                case "date_asc":
                    return (a, b) => a.DateAdded.CompareTo(b.DateAdded);
                case "date_desc":
                    return (a, b) => b.DateAdded.CompareTo(a.DateAdded);
                case "format_asc":
                    return (a, b) =>
                    {
                        int c = string.Compare(a.Format, b.Format, StringComparison.CurrentCultureIgnoreCase);
                        return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                    };
                case "format_desc":
                    return (a, b) =>
                    {
                        int c = string.Compare(b.Format, a.Format, StringComparison.CurrentCultureIgnoreCase);
                        return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                    };
                default: // alpha_asc
                    return (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        private void SortBooks(string mode)
        {
            currentSortMode = mode;
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
            AddDetailRow(Localization.T("Details.Field.Title"), book.Title);

            if (book.IsTextBook)
                ShowTextDetails(book, dash);
            else
                ShowAudioDetails(book, dash);

            listViewDetails.EndUpdate();
        }

        // Library details for an audio / DAISY book:
        // TITLE / AUTHOR / PRODUCER / TIME / ELAPSED / REMAINING / READ /
        // FORMAT / [PAGES for DAISY] / SOUND PROCESSING / ADDED.
        private void ShowAudioDetails(BookData book, string dash)
        {
            AddDetailRow(Localization.T("Details.Field.Author"),
                string.IsNullOrWhiteSpace(book.Author) ? dash : book.Author);
            // Producer always shown (empty = unknown, per spec); Publisher only
            // when present (DAISY has both; plain audio has neither).
            AddDetailRow(Localization.T("Details.Field.Producer"),
                BookData.NormalizeProducer(book.Producer));
            string pub = BookData.NormalizeProducer(book.Publisher);
            if (!string.IsNullOrEmpty(pub))
                AddDetailRow(Localization.T("Details.Field.Publisher"), pub);

            double totalSec = ParseDetailTime(book.Duration);
            double elapsedSec = ParseDetailTime(book.LastPosition);
            double remaining = totalSec - elapsedSec;
            if (remaining < 0) remaining = 0;

            AddDetailRow(Localization.T("Details.Field.Time"), book.Duration);
            AddDetailRow(Localization.T("Details.Field.Elapsed"), FormatDetailTime(elapsedSec));
            AddDetailRow(Localization.T("Details.Field.Remaining"), "-" + FormatDetailTime(remaining));
            string readPct = totalSec > 0
                ? (100.0 * elapsedSec / totalSec).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : book.PercentListened.ToString();
            AddDetailRow(Localization.T("Details.Field.Read"), readPct + "%");
            AddDetailRow(Localization.T("Details.Field.Format"), book.Format);
            if (book.IsDaisy)
                // Empty (unknown), not "0", when the book declares no pages —
                // "0" would wrongly read as "zero pages".
                AddDetailRow(Localization.T("Details.Field.Pages"),
                    book.DaisyPages.Count > 0 ? book.DaisyPages.Count.ToString() : "");
            AddDetailRow(Localization.T("Details.Field.SoundProcessing"),
                Localization.T(book.Sound != null && book.Sound.Enabled ? "Details.Sound.On" : "Details.Sound.Off"));
            AddDetailRow(Localization.T("Details.Field.Added"),
                book.DateAdded.ToString(Localization.T("Common.DateFormatLong")));
        }

        // Library details for a text book: real source format, reading speed in
        // WPM, estimated reading time, and author/producer when present.
        private void ShowTextDetails(BookData book, string dash)
        {
            AddDetailRow(Localization.T("Details.Field.Author"),
                string.IsNullOrWhiteSpace(book.Author) ? dash : book.Author);
            string prod = BookData.NormalizeProducer(book.Producer);
            if (!string.IsNullOrEmpty(prod))
                AddDetailRow(Localization.T("Details.Field.Producer"), prod);
            string pub = BookData.NormalizeProducer(book.Publisher);
            if (!string.IsNullOrEmpty(pub))
                AddDetailRow(Localization.T("Details.Field.Publisher"), pub);

            int wpm = book.TextWpm >= 0 ? book.TextWpm : appSettings.TtsWpm;
            AddDetailRow(Localization.T("Details.Field.Format"), book.Format);
            // Page count between Format and Time, when the book has page markers.
            if (book.TextPages.Count > 0)
                AddDetailRow(Localization.T("Details.Field.Pages"), book.TextPages.Count.ToString());
            AddDetailRow(Localization.T("Details.Field.Time"),
                "≈" + book.EstimatedReadingTime(wpm));
            AddDetailRow(Localization.T("Details.Field.Speed"), Localization.T("Details.Speed.Wpm", wpm));
            AddDetailRow(Localization.T("Details.Field.Read"), book.PercentListened + "%");
            AddDetailRow(Localization.T("Details.Field.Added"),
                book.DateAdded.ToString(Localization.T("Common.DateFormatLong")));
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
                ListViewItem sel = listBooks.SelectedItems.Count > 0 ? listBooks.SelectedItems[0] : null;
                ShowBookMenu(sel != null
                    ? new Point(sel.Bounds.Left + 20, sel.Bounds.Bottom)
                    : new Point(0, 0));
                e.Handled = true;
            }
        }

        /// <summary>Opens the shelf's menu, or does nothing when there is no book
        /// to act on. A ContextMenu cannot cancel its own Popup the way a strip
        /// could, so the empty shelf is caught here instead.</summary>
        private void ShowBookMenu(Point at)
        {
            if (bookMenu == null || GetSelectedBook() == null) return;
            bookMenu.Show(listBooks, at);
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
                Localization.T("Filter.TextBooks") + "|*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.aiff;*.aif;*.ac3;*.amr;*.weba;*.webm;*.au;*.voc;*.txt;*.rtf;*.doc;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.mobi;*.azw;*.azw3;*.brf;*.brl;*.bra;*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllFiles") + "|*.*";
        }

        private void MenuFileOpenFile_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = BuildFileFilter();
                ofd.FilterIndex = 4; // default to "All supported files"
                ofd.Title = Localization.T("Library.ImportFile.Title");
                if (ofd.ShowDialog() == DialogResult.OK)
                    ImportFile(ofd.FileName);
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
                else if (isTextImport)
                {
                    // Document (or a zip-wrapped epub) → extract to content.txt
                    // (the reader's input; TtsReader cleans it on load).
                    // Structured formats also carry a heading list + metadata.
                    TextDoc doc = TextExtractor.Extract(filePath);
                    if (doc.DrmProtected)
                    {
                        if (createdFolder) TryDeleteFolder(destFolder);
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
                return false;
            }
            catch (Exception ex)
            {
                if (createdFolder) TryDeleteFolder(destFolder);
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
            // Archives (especially multi-volume) are unreliable through folder
            // import — steer the user to Open File, which handles them properly.
            if (LibraryScanner.ContainsArchiveFiles(folderPath))
            {
                MessageForm.ShowInfo(this,
                    Localization.T("Dialog.ArchiveInFolder.Message"),
                    Localization.T("Dialog.ArchiveInFolder.Title"));
                return;
            }

            try
            {
                // A DAISY book (ncc.html / OPF+NCX anywhere under the folder) is
                // imported as ONE book with its navigation, not as loose audio.
                if (ImportDaisyFolder(folderPath)) return;

                // Two kinds of content need opposite grouping:
                //  • loose TEXT-book files (pdf/mobi/epub/doc/…) — a folder of
                //    ebooks is a COLLECTION, so each file is its own book;
                //  • loose AUDIO files — a folder of audio is ONE book (its parts).
                var textFiles = EnumerateTextBookFiles(folderPath);
                var audioBooks = new LibraryScanner(folderPath, false).Scan()
                    .Where(b => FolderHasAudio(b.FolderPath)).ToList();

                int total = textFiles.Count + audioBooks.Count;
                if (total == 0)
                {
                    MessageForm.ShowInfo(this, Localization.T("Dialog.NoBooksFound.Message"), Localization.T("Dialog.NoBooksFound.Title"));
                    return;
                }
                if (total > 50)
                {
                    bool proceed = MessageForm.ShowConfirm(this,
                        Localization.T("Dialog.ConfirmManyBooks.Message", total),
                        Localization.T("Dialog.ConfirmManyBooks.Title"));
                    if (!proceed) return;
                }

                int imported = 0, skipped = 0;

                // Each text-book file → its own book (quiet, no per-file dialogs).
                // A file that can't be imported (DRM-protected, unreadable) is
                // counted so the summary can tell the user some were left out.
                foreach (string tf in textFiles)
                    if (ImportFileCore(tf, true)) imported++; else skipped++;

                // Each audio folder → one book (copy its files, as before, but not
                // the text files handled above).
                foreach (BookData book in audioBooks)
                {
                    string destFolder = System.IO.Path.Combine(
                        appSettings.LibraryPath,
                        System.IO.Path.GetFileName(book.FolderPath));
                    if (!System.IO.Directory.Exists(destFolder))
                        System.IO.Directory.CreateDirectory(destFolder);
                    foreach (string file in System.IO.Directory.GetFiles(book.FolderPath))
                    {
                        string fn = System.IO.Path.GetFileName(file);
                        if (fn.ToLower() == "book.ini") continue;
                        if (IsTextBookFile(file)) continue;   // it's its own book
                        string destFile = System.IO.Path.Combine(destFolder, fn);
                        if (!System.IO.File.Exists(destFile))
                            System.IO.File.Copy(file, destFile);
                    }
                    imported++;
                }

                LoadBooks();
                string msg = Localization.T("Dialog.ImportFolderSuccess.Message", imported);
                if (skipped > 0)
                    msg += " " + Localization.T("Dialog.ImportFolderSuccess.Skipped", skipped);
                MessageForm.ShowInfo(this, msg, Localization.T("Dialog.ImportFolderSuccess.Title"));
            }
            catch (Exception ex)
            {
                MessageForm.ShowInfo(this, Localization.T("Dialog.ImportFolderError.Message", ex.Message), Localization.T("Common.Error"));
            }
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