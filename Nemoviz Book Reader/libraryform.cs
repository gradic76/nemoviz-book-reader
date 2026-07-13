using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    public class LibraryForm : Form
    {
        private MenuStrip menuStrip;
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

        // Shelf categories
        private const int CatReading = 0;
        private const int CatUnread = 1;
        private const int CatRead = 2;

        // Filter combo indices (must match the order items are added)
        private const int FilterAll = 0;
        private const int FilterReading = 1;
        private const int FilterUnread = 2;
        private const int FilterRead = 3;

        // The details ListView rows are built fresh per selection (see
        // ShowDetails): the Author row only appears for books that carry an
        // author (DAISY), so plain audio isn't cluttered with an empty field.

        public BookData SelectedBook { get; private set; }

        public LibraryForm(AppSettings settings, string activeBookFolderPath = null)
        {
            appSettings = settings;
            this.activeBookFolderPath = activeBookFolderPath;
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
            menuFile.DropDownItems.Add(new ToolStripMenuItem(Localization.T("Menu.File.Exit")) { ShortcutKeys = Keys.Alt | Keys.F4 });
            ((ToolStripMenuItem)menuFile.DropDownItems[3]).Click += (s, e) => this.Close();

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
            listBooks.ShowGroups = true;
            listBooks.Font = new Font("Segoe UI", 11);
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

            ContextMenuStrip ctx = new ContextMenuStrip();

            ToolStripMenuItem ctxOpen = new ToolStripMenuItem(Localization.T("Context.Open"));
            ctxOpen.ShortcutKeyDisplayString = "Enter";
            ctxOpen.Click += (s, e) => OpenSelectedBook();

            ToolStripMenuItem ctxRestart = new ToolStripMenuItem(Localization.T("Context.Restart"));
            ctxRestart.Click += (s, e) => RestartSelectedBook();

            ToolStripMenuItem ctxRename = new ToolStripMenuItem(Localization.T("Context.Rename"));
            ctxRename.ShortcutKeyDisplayString = "F2";
            ctxRename.Click += (s, e) => RenameSelectedBook();

            ToolStripMenuItem ctxDelete = new ToolStripMenuItem(Localization.T("Context.Delete"));
            ctxDelete.ShortcutKeyDisplayString = "Del";
            ctxDelete.Click += (s, e) => DeleteSelectedBook();

            ToolStripMenuItem ctxProperties = new ToolStripMenuItem(Localization.T("Context.Properties"));
            ctxProperties.ShortcutKeyDisplayString = "Alt+Enter";
            ctxProperties.Click += (s, e) => ShowProperties();

            ctx.Items.Add(ctxOpen);
            ctx.Items.Add(ctxRestart);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(ctxRename);
            ctx.Items.Add(ctxDelete);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(ctxProperties);

            // No book selected (empty shelf) — nothing for the menu to act on.
            ctx.Opening += (s, e) =>
            {
                if (GetSelectedBook() == null)
                    e.Cancel = true;
            };

            listBooks.ContextMenuStrip = ctx;

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
        /// Rebuilds the shelf from `books`, applying search text, the status
        /// filter, group headers (Now Reading / Reading / Unread / Read) and
        /// the current sort. Tries to keep `keepSelected` selected; otherwise
        /// selects the first book.
        /// </summary>
        private void RebuildShelf(BookData keepSelected)
        {
            string query = NormalizeForSearch(tbSearch.Text.Trim());
            int filter = cbFilter.SelectedIndex;
            if (filter < 0) filter = FilterAll;

            var reading = new List<BookData>();
            var unread = new List<BookData>();
            var read = new List<BookData>();

            foreach (BookData b in books)
            {
                if (query.Length > 0 && !NormalizeForSearch(b.Title).Contains(query))
                    continue;

                switch (GetCategory(b))
                {
                    case CatReading: reading.Add(b); break;
                    case CatUnread: unread.Add(b); break;
                    default: read.Add(b); break;
                }
            }

            Comparison<BookData> cmp = GetComparer();
            reading.Sort(cmp);
            unread.Sort(cmp);
            read.Sort(cmp);

            // The last-listened book gets its own "Now Reading" group at the
            // top — but only while it's actually being read. Once finished
            // (or rewound to zero), it sits in its natural group.
            BookData nowReading = null;
            string lastPath = appSettings.LastOpenedBookPath;
            if (!string.IsNullOrEmpty(lastPath))
            {
                for (int i = 0; i < reading.Count; i++)
                {
                    if (PathsEqual(reading[i].FolderPath, lastPath))
                    {
                        nowReading = reading[i];
                        reading.RemoveAt(i);
                        break;
                    }
                }
            }

            bool showAll = filter == FilterAll;

            listBooks.BeginUpdate();
            listBooks.Items.Clear();
            listBooks.Groups.Clear();
            listBooks.ShowGroups = showAll;

            if (showAll)
            {
                if (nowReading != null)
                    AddGroup(new List<BookData> { nowReading }, Localization.T("Shelf.Group.NowReading"));
                AddGroup(reading, Localization.T("Shelf.Group.Reading"));
                AddGroup(unread, Localization.T("Shelf.Group.Unread"));
                AddGroup(read, Localization.T("Shelf.Group.Read"));
            }
            else if (filter == FilterReading)
            {
                // The now-reading book belongs to this category — pinned first.
                if (nowReading != null)
                    reading.Insert(0, nowReading);
                AddGroup(reading, null);
            }
            else if (filter == FilterUnread)
            {
                AddGroup(unread, null);
            }
            else
            {
                AddGroup(read, null);
            }

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

        private void AddGroup(List<BookData> group, string headerText)
        {
            if (group.Count == 0) return;

            ListViewGroup lvg = null;
            if (headerText != null)
            {
                lvg = new ListViewGroup(headerText);
                listBooks.Groups.Add(lvg);
            }

            foreach (BookData b in group)
            {
                // Show "Author — Title" when the book carries a separate author
                // (produced formats like DAISY); plain audiobooks show the
                // single merged Title.
                string shelfText = string.IsNullOrWhiteSpace(b.Author)
                    ? b.Title : b.Author + " — " + b.Title;
                ListViewItem item = new ListViewItem(shelfText);
                item.Tag = b;
                if (lvg != null)
                    item.Group = lvg;
                listBooks.Items.Add(item);
            }
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

            string speedStr = (book.Speed / 100.0).ToString("0.0");
            string dash = Localization.T("Common.Dash");

            listViewDetails.BeginUpdate();
            listViewDetails.Items.Clear();
            AddDetailRow(Localization.T("Details.Field.Title"), book.Title);
            // Author row only for books that carry one (DAISY) — shown even if
            // empty (a dash), a cue to fill it in via F2. Plain audio has no
            // author, so the row is omitted entirely.
            if (book.IsDaisy)
                AddDetailRow(Localization.T("Details.Field.Author"),
                    string.IsNullOrWhiteSpace(book.Author) ? dash : book.Author);
            // Text book: show the official format name and an estimated reading
            // time from the effective reading speed (per-book override, else the
            // Settings default). Audio/DAISY show their real format and length.
            if (book.IsTextBook)
            {
                int wpm = book.TextWpm >= 0 ? book.TextWpm : appSettings.TtsWpm;
                AddDetailRow(Localization.T("Details.Field.Format"), Localization.T("Details.Format.PlainText"));
                AddDetailRow(Localization.T("Details.Field.Duration"),
                    book.EstimatedReadingTime(wpm) + " " + Localization.T("Details.Estimated"));
            }
            else
            {
                AddDetailRow(Localization.T("Details.Field.Format"), book.Format);
                AddDetailRow(Localization.T("Details.Field.Duration"), book.Duration);
            }
            AddDetailRow(Localization.T("Details.Field.Listened"), book.PercentListened + "%");
            AddDetailRow(Localization.T("Details.Field.Speed"), Localization.T("Details.Speed.Value", speedStr));
            AddDetailRow(Localization.T("Details.Field.Added"), book.DateAdded.ToString(Localization.T("Common.DateFormat")));
            listViewDetails.EndUpdate();
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
                if (GetSelectedBook() != null)
                    listBooks.ContextMenuStrip.Show(listBooks, new Point(0, 0));
            }
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

        private void RestartSelectedBook()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;
            book.LastPosition = "00:00:00";
            book.PercentListened = 0;
            book.Save();
            // The book just moved to the "Unread" group — rebuild and follow it.
            RebuildShelf(book);
            MessageBox.Show(Localization.T("Dialog.Restart.Message"), Localization.T("Dialog.Restart.Title"));
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

        private void DeleteSelectedBook()
        {
            BookData book = GetSelectedBook();
            if (book == null) return;

            string title = book.Title;
            string folderPath = book.FolderPath;

            if (PathsEqual(folderPath, activeBookFolderPath))
            {
                MessageBox.Show(
                    Localization.T("Dialog.ActiveBook.Message", title),
                    Localization.T("Dialog.ActiveBook.Title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult result = MessageBox.Show(
                Localization.T("Dialog.ConfirmDelete.Message", title),
                Localization.T("Dialog.ConfirmDelete.Title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

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
                MessageBox.Show(Localization.T("Dialog.DeleteError.Message", ex.Message), Localization.T("Common.Error"));
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
                Localization.T("Filter.Audiobooks") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf|" +
                Localization.T("Filter.TextBooks") + "|*.txt;*.rtf;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.djvu;*.mobi;*.azw;*.azw3;*.cbz;*.cbr|" +
                Localization.T("Filter.Archives") + "|*.zip;*.rar;*.7z;*.001;*.z01|" +
                Localization.T("Filter.AllSupported") + "|*.mp3;*.ogg;*.flac;*.m4a;*.m4b;*.wav;*.opus;*.aac;*.wma;*.ape;*.mka;*.spx;*.oga;*.dsf;*.dff;*.caf;*.txt;*.rtf;*.docx;*.odt;*.epub;*.fb2;*.htm;*.html;*.pdf;*.djvu;*.mobi;*.azw;*.azw3;*.cbz;*.cbr;*.zip;*.rar;*.7z;*.001;*.z01|" +
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
            string destFolder = null;
            bool createdFolder = false;
            try
            {
                string sourceName = System.IO.Path.GetFileName(filePath);
                string ext = System.IO.Path.GetExtension(filePath).ToLower();
                bool isArchive = LibraryScanner.IsExtractableArchive(sourceName);
                // A .zip that wraps an epub (how most libraries package them) is a
                // text import, not a generic archive.
                bool isTextImport = TextExtractor.IsTextImport(filePath);

                // Multi-volume sets fold to one clean folder name (name.7z.001
                // → name, name.part1.rar → name).
                string bookName = isArchive
                    ? LibraryScanner.BaseArchiveName(filePath)
                    : System.IO.Path.GetFileNameWithoutExtension(filePath);
                destFolder = System.IO.Path.Combine(appSettings.LibraryPath, bookName);

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
                    LibraryScanner.ExtractArchive(filePath, destFolder,
                        () => ArchivePasswordPrompt.Show(this, sourceName, pwAttempts++ > 0));
                    // Name the book after the folder closest to the files (the
                    // wrapper the archive packed everything into), not the
                    // archive file itself.
                    destFolder = LibraryScanner.ResolveBookFolder(destFolder, appSettings.LibraryPath);
                    imported = new BookData(destFolder);

                    // DAISY book? Build the timeline in reading order (from the
                    // navigation), flattening any nested export folder to root
                    // first, and take the title from the DAISY metadata.
                    DaisyBook daisy = DaisyParser.TryParse(destFolder);
                    if (daisy != null)
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
                    // Build duration/chapters up front so it isn't 00:00:00 until
                    // first played; also stores the detailed format string.
                    imported.BuildChaptersFromFolder(new string[] { destFile });
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
                        MessageBox.Show(Localization.T("Dialog.DrmProtected.Message"),
                            Localization.T("Dialog.DrmProtected.Title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(destFolder, "content.txt"),
                        doc.Text ?? "", new System.Text.UTF8Encoding(false));
                    if (!string.IsNullOrWhiteSpace(doc.Title)) imported.Title = doc.Title;
                    if (!string.IsNullOrWhiteSpace(doc.Author)) imported.Author = doc.Author;
                    imported.SetTextHeadings(doc.Headings);
                    imported.Format = Localization.T("Details.Format.PlainText");
                }
                else
                {
                    string destFile = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(filePath));
                    if (!System.IO.File.Exists(destFile))
                        System.IO.File.Copy(filePath, destFile);
                    imported.Format = BookData.FriendlyFormatName(ext);
                }

                imported.Save();

                LoadBooks();
                MessageBox.Show(Localization.T("Dialog.ImportSuccess.Message"), Localization.T("Dialog.ImportSuccess.Title"));
            }
            catch (OperationCanceledException)
            {
                // User cancelled the archive password prompt — quietly undo the
                // empty folder we just made, no error dialog.
                if (createdFolder) TryDeleteFolder(destFolder);
            }
            catch (Exception ex)
            {
                if (createdFolder) TryDeleteFolder(destFolder);
                MessageBox.Show(Localization.T("Dialog.ImportError.Message", ex.Message), Localization.T("Common.Error"));
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

        private void ImportFolder(string folderPath)
        {
            // Archives (especially multi-volume) are unreliable through folder
            // import — steer the user to Open File, which handles them properly.
            if (LibraryScanner.ContainsArchiveFiles(folderPath))
            {
                MessageBox.Show(
                    Localization.T("Dialog.ArchiveInFolder.Message"),
                    Localization.T("Dialog.ArchiveInFolder.Title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                LibraryScanner scanner = new LibraryScanner(folderPath, false);
                List<BookData> found = scanner.Scan();

                if (found.Count == 0)
                {
                    MessageBox.Show(Localization.T("Dialog.NoBooksFound.Message"), Localization.T("Dialog.NoBooksFound.Title"));
                    return;
                }

                if (found.Count > 50)
                {
                    DialogResult result = MessageBox.Show(
                        Localization.T("Dialog.ConfirmManyBooks.Message", found.Count),
                        Localization.T("Dialog.ConfirmManyBooks.Title"),
                        MessageBoxButtons.YesNo);
                    if (result == DialogResult.No) return;
                }

                int imported = 0;
                foreach (BookData book in found)
                {
                    string destFolder = System.IO.Path.Combine(
                        appSettings.LibraryPath,
                        System.IO.Path.GetFileName(book.FolderPath));

                    if (!System.IO.Directory.Exists(destFolder))
                        System.IO.Directory.CreateDirectory(destFolder);

                    foreach (string file in System.IO.Directory.GetFiles(book.FolderPath))
                    {
                        if (System.IO.Path.GetFileName(file).ToLower() == "book.ini")
                            continue;

                        string destFile = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(file));
                        if (!System.IO.File.Exists(destFile))
                            System.IO.File.Copy(file, destFile);
                    }
                    imported++;
                }

                LoadBooks();
                MessageBox.Show(Localization.T("Dialog.ImportFolderSuccess.Message", imported), Localization.T("Dialog.ImportFolderSuccess.Title"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.T("Dialog.ImportFolderError.Message", ex.Message), Localization.T("Common.Error"));
            }
        }
    }
}