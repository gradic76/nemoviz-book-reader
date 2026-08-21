using System;
using System.IO;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The program icon on every window, not just on the file. See AppIcon.
            AppIcon.Install();

            // Write down what went wrong, before the dialog asks the reader to
            // decide about it.
            //
            // NBR's user cannot read a stack trace out of a message box, and
            // "Object reference not set to an instance of an object" says nothing
            // about where. Every unhandled exception now lands in
            // %TEMP%\NBR-crash.log with its type, message and stack, so a report
            // of "it crashed while reading" can be answered instead of guessed
            // at. The dialog still appears — this only makes sure the evidence
            // survives whichever button is pressed.
            Application.ThreadException += (s, e) => Record("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Record("background", e.ExceptionObject as Exception);

            // RESTART HAPPENS HERE, AFTER THE MESSAGE LOOP HAS ENDED — never with
            // Application.Restart (Gordan, 2026-08-19: changing the look left BOTH
            // windows on screen, the old one and the new).
            //
            // Two reasons that call could not work, and the second is the one that
            // bit. Application.Restart STARTS THE NEW PROCESS FIRST and only then
            // tries to end this one — so anything that stops the exit leaves two
            // running. And it was being made from inside Settings, a MODAL dialog:
            // its nested message loop does not unwind on Application.Exit, so the
            // old player stayed exactly where it was while its replacement opened
            // in front of it.
            //
            // Doing it here also fixes a race nobody had hit yet. OnFormClosing
            // releases mpv, liblouis and the 32-bit speech host, and those hold
            // files and the sound card; a replacement launched before that finishes
            // is a second process reaching for what the first has not yet let go.
            // By this line the loop is over and the teardown is done.
            var player = new Form1();
            Application.Run(player);
            if (player.RestartOnExit)
            {
                try { System.Diagnostics.Process.Start(Application.ExecutablePath); }
                catch (Exception ex) { Record("restart", ex); }
            }
        }

        private static void Record(string where, Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "NBR-crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  "
                    + ex.GetType().FullName + ": " + ex.Message + Environment.NewLine
                    + ex.StackTrace + Environment.NewLine
                    + new string('-', 70) + Environment.NewLine);
            }
            catch { }
        }
    }
}
