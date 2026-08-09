using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>One line of a book's information: what it is called and what it
    /// says. The label carries no colon — the caller decides whether it is a
    /// column heading (the Library's list) or a line of running text (the info
    /// glass), and only the latter needs one.</summary>
    public struct InfoRow
    {
        public string Label;
        public string Value;
        public InfoRow(string label, string value) { Label = label; Value = value; }
    }

    /// <summary>
    /// THE ORDER a book's information is presented in, in one place.
    ///
    /// <para>Gordan's report (2026-07-30): the info boxes did not agree with each
    /// other. Measured across the three, they did not — Format was third in
    /// Properties, ninth in the Library's audio details and fifth in its text
    /// details; Publisher and Producer sat third and fourth in the Library and
    /// were absent from Properties altogether; Pages came after Format in one
    /// place and after Headings in another. Each box had grown its own order by
    /// being written on a different day.</para>
    ///
    /// <para><b>The fix is not to re-sort three lists but to remove the
    /// opportunity.</b> A caller no longer decides where a field goes — it says
    /// which fields it has and this decides the order, so the boxes cannot drift
    /// again even when someone adds a field to only one of them.</para>
    ///
    /// <para><b>The order follows the player's glass</b> (§8k), which was settled
    /// first and cannot change: identity, then where you are in the book, then
    /// where the book came from and what it is made of, then how it is being
    /// read. The player's own live-only slots (chapter, page, bookmarks) have no
    /// equivalent here and simply do not appear.</para>
    ///
    /// <para><b>A field with nothing to say is not shown</b> — the player's rule,
    /// and the reason a fixed order matters more to a screen reader than to the
    /// eye: a value is always in the same place, so it is found by counting
    /// rather than by reading everything above it. The one exception is a field a
    /// box has decided is always worth a line even when empty (the Library shows
    /// Producer as a dash), which is the caller's business, not this class's:
    /// pass the dash as the value.</para>
    /// </summary>
    public enum BookInfoField
    {
        // Identity — what the book IS.
        Title = 0,
        Author,

        // Where you are in it. Total first, because the two below are read
        // against it.
        Time,
        Elapsed,
        Remaining,
        Read,

        // Where it came from and what it is made of.
        Publisher,
        Producer,
        /// <summary>The year, on a line of its own — used ONLY when there is no
        /// publisher to hang it on. §8k asks for "Publisher (year)", and that is
        /// what a book with a publisher gets; but a year is just as often the only
        /// thing a book has, sniffed out of the end of its title, and dropping it
        /// because there is nowhere tidy to put it would lose the commonest
        /// case.</summary>
        Year,
        Format,
        Pages,
        Headings,
        Characters,
        Language,

        /// <summary>The DOORWAY, not the text. A description runs to 935
        /// characters at the median and this pane is a two-column grid — 120 px
        /// of label, 280 px of value — so the paragraph would not wrap, would be
        /// cut at the column edge, and a screen reader would read it as one
        /// unbroken sub-item with no way to move inside it. The row says there is
        /// one and Enter opens it in a window built for prose.</summary>
        Description,

        // How it is being read.
        //
        // Volume joined Speed here on 2026-08-09, when Properties gave up its
        // Playback controls to make room for the tone bands. Until then neither
        // number appeared in ANY info box for an audio book — they could be
        // changed from the player and heard, but not read — so removing the
        // controls without this would have taken the only place they were
        // legible.
        Volume,
        Speed,
        SoundProcessing,

        // Last, always: this is about the library entry, not about the book.
        Added
    }

    /// <summary>Collects a book's information and hands it back in the canonical
    /// order, whatever order it was put in.</summary>
    public sealed class BookInfoBuilder
    {
        private readonly SortedDictionary<int, InfoRow> rows = new SortedDictionary<int, InfoRow>();

        /// <summary>Adds a field. An empty value is DROPPED rather than shown as a
        /// blank line — pass a dash explicitly if the box wants to keep the row.
        /// Adding the same field twice keeps the last value.</summary>
        public BookInfoBuilder Add(BookInfoField field, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows[(int)field] = new InfoRow(LabelOf(field), value);
            return this;
        }

        /// <summary>Adds a field that keeps its row even with nothing to say, for
        /// the boxes that would rather show a dash than a gap.</summary>
        public BookInfoBuilder AddAlways(BookInfoField field, string value, string whenEmpty)
        {
            rows[(int)field] = new InfoRow(LabelOf(field),
                string.IsNullOrWhiteSpace(value) ? whenEmpty : value);
            return this;
        }

        public List<InfoRow> Rows()
        {
            var list = new List<InfoRow>(rows.Count);
            foreach (var kv in rows) list.Add(kv.Value);
            return list;
        }

        /// <summary>The rows as text, one per line, "Label: value".
        ///
        /// <para>The separator is <c>": "</c> and that is not cosmetic: the
        /// player's glass renderer splits a line on it to tell the silkscreened
        /// label from the lit value, so a line without one has no label at all.
        /// The <c>Details.Field.*</c> captions therefore carry no colon of their
        /// own — the Library uses the same strings as column headings.</para></summary>
        public string ToText(string newLine)
        {
            var sb = new System.Text.StringBuilder();
            foreach (InfoRow r in Rows())
                sb.Append(r.Label).Append(": ").Append(r.Value).Append(newLine);
            return sb.ToString();
        }

        /// <summary>How wide the label column has to be to hold the longest
        /// caption there is, in the given font.
        ///
        /// <para>Measured over EVERY field rather than over the rows on screen,
        /// and that is the point: the column is sized once, while the list is
        /// still empty, but the rows change with every book selected. A column
        /// fitted to one book's labels would cut another's — which is what "Sound
        /// proc…" was.</para></summary>
        public static int WidestLabel(System.Drawing.Font font)
        {
            int w = 0;
            foreach (BookInfoField field in Enum.GetValues(typeof(BookInfoField)))
                w = Math.Max(w, System.Windows.Forms.TextRenderer.MeasureText(
                    Localization.T("Details.Field." + field), font).Width);
            return w;
        }

        /// <summary>One caption per field, from one family of keys. Two pairs used
        /// to say the same thing under different names — Duration/Time and
        /// Listened/Elapsed — which is how the boxes came to disagree about what
        /// to call the same number.</summary>
        private static string LabelOf(BookInfoField field)
        {
            return Localization.T("Details.Field." + field);
        }
    }
}
