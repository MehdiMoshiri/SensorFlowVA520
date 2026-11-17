using System;
using System.Threading;
using System.Windows.Forms;

namespace VA520Tester
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Every unexpected exception ends up in the error log (and the program keeps running when possible).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                ErrorLogger.Log(ErrCodes.Unhandled, "UI thread exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message
                    + " | " + FirstStackLine(e.Exception));
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                ErrorLogger.Log(ErrCodes.Unhandled, "Unhandled exception: " + (ex != null ? ex.GetType().Name + ": " + ex.Message + " | " + FirstStackLine(ex) : "" + e.ExceptionObject));
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        static string FirstStackLine(Exception ex)
        {
            if (ex.StackTrace == null) return "";
            string[] lines = ex.StackTrace.Split('\n');
            return lines.Length > 0 ? lines[0].Trim() : "";
        }
    }
}
