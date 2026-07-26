using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// A CUE sheet beside a single long audio file — the way a ripped album, and
    /// very often a whole audiobook, records where each track begins. It gives NBR
    /// exactly what an M4B's embedded chapter marks give it: titled positions
    /// inside one file, so the book navigates by chapter instead of being one
    /// undivided block.
    ///
    /// <para>Only the SINGLE-file case is used. A CUE that lists several FILEs is
    /// describing a folder of separate tracks, and NBR already navigates that by
    /// Part — reading it again would only duplicate what is there.</para>
    ///
    /// <para>Times are <c>MM:SS:FF</c> where FF is CD frames, 75 to the second;
    /// the sheet may also carry the album title and performer, which are the
    /// book's title and author when the user reads metadata.</para>
    /// </summary>
    public class CueSheet
    {
        /// <summary>The audio file the sheet points at (name as written in it).</summary>
        public string AudioFile = "";
        /// <summary>Track title + start position in seconds, in sheet order.</summary>
        public List<(string Title, double Position)> Chapters = new List<(string, double)>();
        public string AlbumTitle = "";
        public string Performer = "";
        public bool HasChapters { get { return Chapters.Count > 0; } }
    }

    public static class CueParser
    {
        private static readonly Regex RxIndex1 =
            new Regex(@"^\s*INDEX\s+0*1\s+(\d+):(\d{1,2}):(\d{1,2})\s*$", RegexOptions.IgnoreCase);

        /// <summary>The .cue file in a folder, or null. Only one is expected; the
        /// first in name order wins if a folder has several.</summary>
        public static string FindCueFile(string folder)
        {
            try
            {
                string[] cues = Directory.GetFiles(folder, "*.cue", SearchOption.TopDirectoryOnly);
                if (cues.Length == 0) return null;
                Array.Sort(cues, StringComparer.OrdinalIgnoreCase);
                return cues[0];
            }
            catch { return null; }
        }

        /// <summary>Reads a CUE sheet. Returns null when it isn't one, when it
        /// describes several files (see the class note), or when it has no usable
        /// track positions. Never throws.</summary>
        public static CueSheet TryParse(string cuePath)
        {
            try
            {
                if (string.IsNullOrEmpty(cuePath) || !File.Exists(cuePath)) return null;
                // Sheets are written by all sorts of rippers; the reader's decoder
                // (UTF-8, else Windows-1250) is the same problem and the same fix.
                string[] lines = TtsReader.ReadFile(cuePath)
                    .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                var sheet = new CueSheet();
                int fileCount = 0;
                string pendingTitle = null;
                bool inTrack = false;
                bool sawIndexForTrack = false;

                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                    {
                        fileCount++;
                        if (fileCount > 1) return null;      // multi-file sheet — not ours
                        sheet.AudioFile = FirstQuoted(line);
                        continue;
                    }
                    if (Regex.IsMatch(line, @"^TRACK\s+\d+", RegexOptions.IgnoreCase))
                    {
                        inTrack = true;
                        sawIndexForTrack = false;
                        pendingTitle = null;
                        continue;
                    }
                    if (line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                    {
                        string t = FirstQuoted(line);
                        if (inTrack) pendingTitle = t;
                        else sheet.AlbumTitle = t;           // header TITLE = the album
                        continue;
                    }
                    if (line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!inTrack) sheet.Performer = FirstQuoted(line);
                        continue;
                    }

                    Match idx = RxIndex1.Match(line);
                    if (idx.Success && inTrack && !sawIndexForTrack)
                    {
                        // INDEX 00 is the pre-gap; INDEX 01 is where the track really
                        // starts, which is the only one worth navigating to.
                        sawIndexForTrack = true;
                        int min = int.Parse(idx.Groups[1].Value);
                        int sec = int.Parse(idx.Groups[2].Value);
                        int frames = int.Parse(idx.Groups[3].Value);
                        double pos = min * 60 + sec + frames / 75.0;
                        string title = !string.IsNullOrWhiteSpace(pendingTitle)
                            ? pendingTitle
                            : Localization.T("Cue.Track", sheet.Chapters.Count + 1);
                        sheet.Chapters.Add((title, pos));
                    }
                }

                if (!sheet.HasChapters) return null;
                sheet.Chapters.Sort((a, b) => a.Position.CompareTo(b.Position));
                return sheet;
            }
            catch { return null; }
        }

        /// <summary>Reads a CUE sheet sitting beside exactly ONE audio file in a
        /// folder — the case where its track marks add something. Returns null
        /// otherwise (no sheet, several audio files, or a sheet that points at a
        /// file that isn't there).</summary>
        public static CueSheet TryParseForFolder(string folder, string[] audioFiles)
        {
            if (audioFiles == null || audioFiles.Length != 1) return null;
            string cue = FindCueFile(folder);
            if (cue == null) return null;

            CueSheet sheet = TryParse(cue);
            if (sheet == null) return null;

            // The sheet should name the file it describes; a mismatch means it was
            // copied in from elsewhere, and its times would be meaningless.
            string named = Path.GetFileName(sheet.AudioFile ?? "");
            string actual = Path.GetFileName(audioFiles[0]);
            if (!string.IsNullOrEmpty(named)
                && !string.Equals(named, actual, StringComparison.OrdinalIgnoreCase))
                return null;
            return sheet;
        }

        /// <summary>The text inside the first pair of quotes, or everything after
        /// the keyword when the writer left the quotes out.</summary>
        private static string FirstQuoted(string line)
        {
            int a = line.IndexOf('"');
            int b = a >= 0 ? line.IndexOf('"', a + 1) : -1;
            if (a >= 0 && b > a) return line.Substring(a + 1, b - a - 1).Trim();
            int sp = line.IndexOf(' ');
            if (sp < 0) return "";
            string rest = line.Substring(sp + 1).Trim();
            // FILE lines end with the format word (WAVE / MP3 / BINARY).
            Match m = Regex.Match(rest, @"^(.*?)\s+(WAVE|MP3|AIFF|BINARY|MOTOROLA|FLAC)$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : rest;
        }
    }
}
