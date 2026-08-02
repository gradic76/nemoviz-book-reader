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

            Application.Run(new Form1());
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
