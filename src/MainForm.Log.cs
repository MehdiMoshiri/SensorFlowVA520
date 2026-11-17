using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        ListView lvErrors;
        CheckBox chkTrace;
        TextBox txtTrace;
        TabPage logPage;

        void BuildLogTab()
        {
            logPage = AddPage("Log", false);

            // ---------------------------------------------------------------- top bar
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 38;
            UI.AddButton(bar, "View error log file", 6, 5, 140, 27, delegate { ShowLogFile(); });
            UI.AddButton(bar, "Write test error", 152, 5, 120, 27, delegate
            {
                ErrorLogger.Log(ErrCodes.TestFailed, "Manual test entry written from the Log tab");
            });
            UI.AddButton(bar, "Clear list", 278, 5, 80, 27, delegate { lvErrors.Items.Clear(); });
            Label path = UI.AddLabel(bar, "File: " + ErrorLogger.FilePath, 368, 10, 760);
            path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // ---------------------------------------------------------------- frame trace (bottom)
            Panel trace = new Panel();
            trace.Dock = DockStyle.Bottom;
            trace.Height = 250;
            Panel traceBar = new Panel();
            traceBar.Dock = DockStyle.Top;
            traceBar.Height = 30;
            chkTrace = binder.Bind("trace", UI.AddCheck(traceBar, "Trace every TX / RX frame (hex)", 6, 5, 240, false));
            chkTrace.CheckedChanged += delegate { if (client != null) client.TraceEnabled = chkTrace.Checked; };
            UI.AddButton(traceBar, "Clear", 256, 2, 70, 25, delegate { txtTrace.Text = ""; });
            txtTrace = UI.AddOutput(trace, DockStyle.Fill);
            trace.Controls.Add(traceBar);
            txtTrace.BringToFront();

            // ---------------------------------------------------------------- error list
            lvErrors = new ListView();
            lvErrors.View = View.Details;
            lvErrors.FullRowSelect = true;
            lvErrors.GridLines = true;
            lvErrors.HideSelection = false;
            lvErrors.Dock = DockStyle.Fill;
            lvErrors.Columns.Add("No", 60);
            lvErrors.Columns.Add("Code", 65);
            lvErrors.Columns.Add("Time", 95);
            lvErrors.Columns.Add("Date", 85);
            lvErrors.Columns.Add("Text", 800);

            logPage.Controls.Add(lvErrors);
            logPage.Controls.Add(trace);
            logPage.Controls.Add(bar);
            lvErrors.BringToFront();
        }

        void DrainErrors()
        {
            if (lvErrors == null) return;
            ErrorEntry e;
            int n = 0;
            ErrorEntry lastEntry = null;
            lvErrors.BeginUpdate();
            try
            {
                while (n < 300 && ErrorLogger.UiQueue.TryDequeue(out e))
                {
                    ListViewItem it = new ListViewItem(e.No.ToString("0000"));
                    it.SubItems.Add("E" + e.Code);
                    it.SubItems.Add(e.TimeText);
                    it.SubItems.Add(e.DateText);
                    it.SubItems.Add(e.Text);
                    lvErrors.Items.Insert(0, it);
                    lastEntry = e;
                    n++;
                }
                while (lvErrors.Items.Count > 2000) lvErrors.Items.RemoveAt(lvErrors.Items.Count - 1);
            }
            finally
            {
                lvErrors.EndUpdate();
            }
            if (n > 0)
            {
                logPage.Text = "Log (" + ErrorLogger.SessionCount + ")";
                if (lastEntry != null) SetStatus("Error " + lastEntry.No.ToString("0000") + "  E" + lastEntry.Code + ": " + lastEntry.Text);
            }
            if (ErrorLogger.LastWriteError.Length > 0)
                lblStatus.Text = "!! Cannot write the error log file: " + ErrorLogger.LastWriteError;
        }

        void DrainTrace()
        {
            if (txtTrace == null || traceQueue.IsEmpty) return;
            StringBuilder sb = new StringBuilder();
            string line;
            int n = 0;
            while (n < 300 && traceQueue.TryDequeue(out line))
            {
                sb.Append(line).Append("\r\n");
                n++;
            }
            if (sb.Length == 0) return;
            string cur = txtTrace.Text;
            if (cur.Length > 150000)
            {
                int cut = cur.IndexOf('\n', cur.Length - 75000);
                cur = cut >= 0 ? cur.Substring(cut + 1) : "";
            }
            txtTrace.Text = cur + sb.ToString();
            txtTrace.SelectionStart = txtTrace.Text.Length;
            txtTrace.ScrollToCaret();
        }

        /// <summary>Shows the end of the error log file in a small window (no external program needed - works the same on Mono).</summary>
        void ShowLogFile()
        {
            Form f = new Form();
            f.Text = "Error log file - " + ErrorLogger.FilePath;
            f.StartPosition = FormStartPosition.CenterParent;
            f.Size = new Size(Math.Min(1000, Width - 40), Math.Min(600, Height - 40));
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Both;
            tb.WordWrap = false;
            tb.Font = UI.Mono;
            tb.BackColor = Color.White;
            tb.Dock = DockStyle.Fill;
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 34;
            UI.AddButton(bar, "Refresh", 6, 4, 80, 26, delegate { tb.Text = ErrorLogger.ReadTail(300000); tb.SelectionStart = tb.Text.Length; tb.ScrollToCaret(); });
            UI.AddButton(bar, "Close", 92, 4, 80, 26, delegate { f.Close(); });
            f.Controls.Add(tb);
            f.Controls.Add(bar);
            tb.BringToFront();
            tb.Text = ErrorLogger.ReadTail(300000);
            tb.SelectionStart = tb.Text.Length;
            f.ShowDialog(this);
            f.Dispose();
        }
    }
}
