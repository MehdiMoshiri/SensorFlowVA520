using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VA520Tester
{
    /// <summary>One logged error.</summary>
    public class ErrorEntry
    {
        public int No;          // running error number (continues across program restarts)
        public int Code;        // error code (see ErrCodes)
        public string Text;
        public DateTime Time;

        public string TimeText { get { return Time.ToString("HH:mm:ss.fff"); } }
        public string DateText { get { return Time.ToString("yyyy-MM-dd"); } }

        /// <summary>The line that is written to the text file: No | Code | Text | Time | Date</summary>
        public string ToLine()
        {
            return No.ToString("0000") + " | E" + Code + " | " + Text + " | " + TimeText + " | " + DateText;
        }
    }

    /// <summary>
    /// Writes every error to a plain text file next to the executable:
    ///   0001 | E1002 | Timeout ... | 14:32:10.123 | 2026-11-07
    /// (error number | error code | text | time | date). No database is used.
    /// Thread safe, never throws.
    /// </summary>
    public static class ErrorLogger
    {
        public const string FileName = "ErrorLog.txt";
        const string Header = "No | Code | Text | Time | Date";

        static readonly object Sync = new object();
        static bool initialised;
        static string path;
        static int counter;
        static int total;           // errors written in this session
        static string lastWriteError = "";
        static bool fallbackFolder;

        // repeat suppression
        static string lastKey = "";
        static DateTime lastKeyTime = DateTime.MinValue;
        static int suppressedCount;

        /// <summary>Entries waiting to be shown in the UI (drained by the UI timer).</summary>
        public static readonly ConcurrentQueue<ErrorEntry> UiQueue = new ConcurrentQueue<ErrorEntry>();

        /// <summary>Rotate the file when it grows over this size (MB). 0 = never.</summary>
        public static int MaxFileMb = 5;
        /// <summary>Same code+text is written at most once per N seconds (0 = write everything).</summary>
        public static int SuppressSeconds = 0;

        public static string FilePath { get { Init(); return path; } }
        public static bool FallbackFolderUsed { get { Init(); return fallbackFolder; } }
        public static string LastWriteError { get { return lastWriteError; } }
        public static int SessionCount { get { return total; } }

        static void Init()
        {
            if (initialised) return;
            lock (Sync)
            {
                if (initialised) return;
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string p = Path.Combine(dir, FileName);
                if (!CanWrite(p))
                {
                    // exe folder not writable (e.g. read-only share on the Pi) -> use a per-user folder
                    string alt = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(alt)) alt = Path.GetTempPath();
                    alt = Path.Combine(alt, "VA520Tester");
                    try { Directory.CreateDirectory(alt); } catch { }
                    p = Path.Combine(alt, FileName);
                    fallbackFolder = true;
                }
                path = p;
                counter = ReadLastNumber(path);
                initialised = true;
            }
        }

        static bool CanWrite(string file)
        {
            try
            {
                using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return true;
            }
            catch { return false; }
        }

        static int ReadLastNumber(string file)
        {
            try
            {
                if (!File.Exists(file)) return 0;
                int last = 0;
                foreach (string line in File.ReadLines(file))
                {
                    int bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    int n;
                    if (int.TryParse(line.Substring(0, bar).Trim(), out n)) last = n;
                }
                return last;
            }
            catch { return 0; }
        }

        public static ErrorEntry Log(int code, string text)
        {
            try
            {
                Init();
                if (text == null) text = "";
                text = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();

                lock (Sync)
                {
                    string key = code + "|" + text;
                    DateTime now = DateTime.Now;
                    if (SuppressSeconds > 0 && key == lastKey && (now - lastKeyTime).TotalSeconds < SuppressSeconds)
                    {
                        suppressedCount++;
                        return null;
                    }
                    if (suppressedCount > 0)
                    {
                        WriteEntry(new ErrorEntry
                        {
                            No = ++counter,
                            Code = ErrCodes.General,
                            Text = "(" + suppressedCount + " identical errors suppressed)",
                            Time = now
                        });
                        suppressedCount = 0;
                    }
                    lastKey = key;
                    lastKeyTime = now;

                    ErrorEntry e = new ErrorEntry { No = ++counter, Code = code, Text = text, Time = now };
                    WriteEntry(e);
                    return e;
                }
            }
            catch (Exception ex)
            {
                lastWriteError = ex.Message;
                return null;
            }
        }

        public static ErrorEntry Log(Exception ex, string context)
        {
            ModbusException me = ex as ModbusException;
            int code = me != null ? me.Code : ErrCodes.Unhandled;
            string msg = (string.IsNullOrEmpty(context) ? "" : context + ": ") + ex.Message;
            return Log(code, msg);
        }

        // caller holds Sync
        static void WriteEntry(ErrorEntry e)
        {
            total++;
            UiQueue.Enqueue(e);
            try
            {
                Rotate();
                bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                StringBuilder sb = new StringBuilder();
                if (isNew) sb.Append(Header).Append(Environment.NewLine);
                sb.Append(e.ToLine()).Append(Environment.NewLine);
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                lastWriteError = "";
            }
            catch (Exception ex)
            {
                lastWriteError = ex.Message;
            }
        }

        static void Rotate()
        {
            if (MaxFileMb <= 0) return;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > (long)MaxFileMb * 1024 * 1024)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch { }
        }

        /// <summary>Returns the last bytes of the log file as text (for the in-app viewer).</summary>
        public static string ReadTail(int maxChars)
        {
            try
            {
                Init();
                if (!File.Exists(path)) return "(log file does not exist yet - no error has been logged)";
                string all;
                lock (Sync)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        all = sr.ReadToEnd();
                    }
                }
                if (all.Length > maxChars) all = "...\r\n" + all.Substring(all.Length - maxChars);
                return all;
            }
            catch (Exception ex)
            {
                return "Cannot read log file: " + ex.Message;
            }
        }
    }
}
