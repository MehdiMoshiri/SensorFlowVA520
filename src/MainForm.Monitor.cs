using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        ListView lvValues;
        TrendPanel trend;
        NumericUpDown numPollInterval, numTrendWindow;
        Label lblCsv;
        Label[] statValues;
        CommStats lastStats = new CommStats();
        string lastCsvShown = "";

        static readonly string[] StatNames = new string[]
        {
            "Requests", "Successful", "Timeouts", "CRC errors", "Modbus exceptions", "Other errors", "Retries",
            "Success rate", "Response last [ms]", "Response avg [ms]", "Response min / max [ms]",
            "Poll cycles", "Cycle time [ms]", "Errors logged (session)"
        };

        void BuildMonitorTab()
        {
            TabPage page = AddPage("Monitor", false);

            // ---------------------------------------------------------------- top bar
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 38;
            UI.AddLabel(top, "Poll interval [ms]", 6, 10, 110);
            numPollInterval = binder.Bind("pollInterval", UI.NewNum(0, 600000, 1000, 0));
            numPollInterval.SetBounds(118, 7, 80, 22);
            top.Controls.Add(numPollInterval);
            UI.AddLabel(top, "Trend window [s]", 210, 10, 110);
            numTrendWindow = binder.Bind("trendWindow", UI.NewNum(10, 3600, 120, 0));
            numTrendWindow.SetBounds(322, 7, 70, 22);
            top.Controls.Add(numTrendWindow);
            UI.AddButton(top, "Reset min/max/stats", 404, 5, 130, 27, delegate { ResetMonitorStats(); });
            UI.AddButton(top, "Check all", 540, 5, 75, 27, delegate { SetAllChecked(true); });
            UI.AddButton(top, "Uncheck all", 619, 5, 80, 27, delegate { SetAllChecked(false); });
            UI.AddButton(top, "Defaults", 703, 5, 70, 27, delegate { SetDefaultChecked(); });
            lblCsv = UI.AddLabel(top, "", 780, 10, 340);

            // ---------------------------------------------------------------- right panel: statistics + trend
            Panel right = new Panel();
            right.Dock = DockStyle.Right;
            right.Width = 330;

            GroupBox gStats = new GroupBox();
            gStats.Text = "Communication statistics";
            gStats.Dock = DockStyle.Top;
            gStats.Height = 26 + StatNames.Length * 17 + 8;
            statValues = new Label[StatNames.Length];
            for (int i = 0; i < StatNames.Length; i++)
            {
                int y = 20 + i * 17;
                UI.AddLabel(gStats, StatNames[i], 8, y, 170).Height = 17;
                statValues[i] = UI.AddLabel(gStats, "-", 180, y, 150);
                statValues[i].Height = 17;
                statValues[i].Font = new Font(Font, FontStyle.Bold);
            }

            trend = new TrendPanel();
            trend.Dock = DockStyle.Fill;
            right.Controls.Add(trend);
            right.Controls.Add(gStats);
            trend.BringToFront();

            // ---------------------------------------------------------------- value list
            lvValues = new ListView();
            lvValues.View = View.Details;
            lvValues.FullRowSelect = true;
            lvValues.GridLines = true;
            lvValues.HideSelection = false;
            lvValues.MultiSelect = false;
            lvValues.CheckBoxes = true;
            lvValues.Dock = DockStyle.Fill;
            string[] cols = new string[] { "Value (tick = poll)", "Unit", "Register (addr)", "Current", "Min", "Max", "Average", "Updated", "Status" };
            int[] widths = new int[] { 185, 58, 88, 88, 76, 76, 76, 66, 56 };
            for (int i = 0; i < cols.Length; i++) lvValues.Columns.Add(cols[i], widths[i]);
            foreach (ValueSlot s in slots)
            {
                ListViewItem it = new ListViewItem(s.Def.Name);
                it.SubItems.Add(s.Def.Unit);
                it.SubItems.Add(s.Def.ModbusRegister + " (" + s.Def.Address + ")");
                for (int k = 0; k < 6; k++) it.SubItems.Add("");
                it.Tag = s;
                it.Checked = s.Enabled;
                lvValues.Items.Add(it);
            }
            lvValues.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                ValueSlot s = e.Item.Tag as ValueSlot;
                if (s == null) return;
                s.Enabled = e.Item.Checked;
                int idx = slots.IndexOf(s) - Va520Map.Values.Count;
                if (idx >= 0 && idx < 4 && chkCustom[idx] != null && chkCustom[idx].Checked != e.Item.Checked)
                    chkCustom[idx].Checked = e.Item.Checked;
            };
            lvValues.SelectedIndexChanged += delegate { UpdateTrend(); };
            if (lvValues.Items.Count > 0) lvValues.Items[0].Selected = true;

            // z-order: edges first, fill last
            page.Controls.Add(lvValues);
            page.Controls.Add(right);
            page.Controls.Add(top);
            lvValues.BringToFront();
        }

        void SetAllChecked(bool v)
        {
            foreach (ListViewItem it in lvValues.Items) it.Checked = v;
        }

        void SetDefaultChecked()
        {
            for (int i = 0; i < slots.Count && i < lvValues.Items.Count; i++)
                lvValues.Items[i].Checked = slots[i].Def.DefaultEnabled;
        }

        void ResetMonitorStats()
        {
            foreach (ValueSlot s in slots) s.ResetStats();
            if (client != null) client.Stats.Reset();
            lastStats = new CommStats();
            SetStatus("Statistics reset");
        }

        // ------------------------------------------------------------------ polling control

        void StartPolling(bool single)
        {
            if (client == null || !client.IsOpen) { Warn("Not connected. Press Connect first."); return; }
            if (poller != null && poller.IsRunning) return;
            if (toolBusy) { Warn("A tool is running. Press Stop in the Tools tab first."); return; }
            bool any = false;
            foreach (ValueSlot s in slots) if (s.Enabled) any = true;
            if (!any) { Warn("Tick at least one value in the Monitor list."); return; }

            ConfigureClient(client);
            UpdateCustomRows();
            ApplyLimits();
            ApplyLogSettings();

            PollSettings ps = new PollSettings();
            ps.Unit = Unit;
            ps.Function = ReadFc;
            ps.IntervalMs = (int)numPollInterval.Value;
            ps.SingleShot = single;
            ps.AutoReconnect = chkAutoReconnect.Checked;
            ps.SkipAfterTimeout = chkSkipTimeout.Checked;
            ps.CsvLog = chkCsv.Checked && !single;

            poller = new Poller(client, slots, ps);
            poller.Finished += delegate(string reason)
            {
                Ui(delegate
                {
                    pollEndReason = reason;
                    SetStatus("Polling: " + reason);
                    UpdateButtons();
                });
            };
            poller.Start();
            SetStatus(single ? "Reading once..." : "Polling started");
            UpdateButtons();
        }

        void StopPolling()
        {
            Poller p = poller;
            if (p == null) return;
            p.Stop();
            UpdateButtons();
        }

        // ------------------------------------------------------------------ periodic refresh (UI timer)

        static void SetSub(ListViewItem it, int idx, string text)
        {
            if (it.SubItems[idx].Text != text) it.SubItems[idx].Text = text;
        }

        void RefreshMonitor()
        {
            if (lvValues == null) return;
            for (int i = 0; i < slots.Count && i < lvValues.Items.Count; i++)
            {
                ValueSlot s = slots[i];
                ListViewItem it = lvValues.Items[i];
                string fmt = s.Def.Type == RegType.Float32 ? "0.000" : "0";
                string cur, min, max, avg, upd, st;
                bool valid, outLim;
                lock (s.Sync)
                {
                    valid = s.Valid;
                    outLim = s.OutOfLimit;
                    cur = valid ? UI.F(s.Value, fmt) : (s.Samples > 0 ? "(" + UI.F(s.Value, fmt) + ")" : "");
                    min = UI.F(s.Min, fmt);
                    max = UI.F(s.Max, fmt);
                    avg = UI.F(s.Avg, s.Def.Type == RegType.Float32 ? "0.000" : "0.0");
                    upd = s.Updated == DateTime.MinValue ? "" : s.Updated.ToString("HH:mm:ss");
                    st = s.Status;
                }
                if (s.Def.IsCustom) SetSub(it, 1, s.Def.Unit);
                SetSub(it, 3, cur);
                SetSub(it, 4, min == "-" ? "" : min);
                SetSub(it, 5, max == "-" ? "" : max);
                SetSub(it, 6, avg == "-" ? "" : avg);
                SetSub(it, 7, upd);
                SetSub(it, 8, st);
                Color c = outLim ? Color.DarkOrange : (!valid && st.Length > 0 && st != "OK" ? Color.Firebrick : SystemColors.WindowText);
                if (it.ForeColor != c) it.ForeColor = c;
            }

            // statistics
            CommStats cs = client != null ? client.Stats.Snapshot() : lastStats;
            lastStats = cs;
            string[] v = new string[StatNames.Length];
            v[0] = cs.Requests.ToString();
            v[1] = cs.Success.ToString();
            v[2] = cs.Timeouts.ToString();
            v[3] = cs.CrcErrors.ToString();
            v[4] = cs.SlaveExceptions.ToString();
            v[5] = cs.OtherErrors.ToString();
            v[6] = cs.Retries.ToString();
            v[7] = cs.Requests == 0 ? "-" : cs.SuccessPercent.ToString("0.00") + " %";
            v[8] = cs.Success == 0 ? "-" : cs.RttLast.ToString("0.0");
            v[9] = cs.Success == 0 ? "-" : cs.RttAvg.ToString("0.0");
            v[10] = cs.Success == 0 ? "-" : cs.RttMin.ToString("0.0") + " / " + cs.RttMax.ToString("0.0");
            v[11] = poller != null ? poller.Cycles.ToString() : "0";
            v[12] = poller != null && poller.Cycles > 0 ? poller.LastCycleMs.ToString("0") : "-";
            v[13] = ErrorLogger.SessionCount.ToString();
            for (int i = 0; i < v.Length; i++)
                if (statValues[i].Text != v[i]) statValues[i].Text = v[i];

            if (poller != null && poller.CsvPath.Length > 0 && poller.CsvPath != lastCsvShown)
            {
                lastCsvShown = poller.CsvPath;
                lblCsv.Text = "CSV: " + lastCsvShown;
            }

            UpdateTrend();
        }

        void UpdateTrend()
        {
            if (trend == null || lvValues == null) return;
            if (lvValues.SelectedItems.Count == 0) { trend.SetData(null, "", "", (int)numTrendWindow.Value); return; }
            ValueSlot s = lvValues.SelectedItems[0].Tag as ValueSlot;
            if (s == null) return;
            trend.SetData(s.CopyHistory(), s.Def.Name, s.Def.Unit, (int)numTrendWindow.Value);
        }
    }
}
