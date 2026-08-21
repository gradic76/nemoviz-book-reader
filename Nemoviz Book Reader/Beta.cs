namespace Nemoviz_Book_Reader
{
    /// <summary>What the first public beta does not ship with — and how to put it
    /// back.
    ///
    /// <para><b>This is a temporary gate, not a decision about the feature.</b>
    /// Gordan, 2026-08-21: the first beta goes out in about ten days to get the
    /// first feedback, and the reading window — the on-screen text and the braille
    /// that rides on it — still needs testing and changes that will not fit in
    /// that time. Shipping it half-tested would collect reports about a thing we
    /// already know is unfinished, and drown the reports about everything that is
    /// finished. So it is switched off for one release.</para>
    ///
    /// <para><b>The on-screen output and braille are ONE feature, which is why one
    /// flag covers both.</b> There is no separate braille switch and has not been
    /// since 2026-08-04: braille output IS the reading window being open, because
    /// the display is fed by the screen reader tracking focus into the reading
    /// surface. Close the window and there is nothing for it to follow.</para>
    ///
    /// <para><b>What this does NOT touch, deliberately: reading braille FILES.</b>
    /// A .brf, .brl or .dxb book and its Input Braille Table are the opposite
    /// direction — braille coming IN, back-translated to text at import — verified
    /// on 19 real books across three languages, and nothing to do with a display.
    /// Switching that off would take away a working feature and leave a
    /// misdetected table with no way to correct it.</para>
    ///
    /// <para><b>To restore:</b> set <see cref="ReadingWindow"/> to true. Nothing
    /// else is conditional on it — the window, its modes, its colours and the
    /// braille push are all still built and still compiled in.</para></summary>
    internal static class Beta
    {
        /// <summary>False while the reading window is held back from release.</summary>
        public const bool ReadingWindow = false;
    }
}
