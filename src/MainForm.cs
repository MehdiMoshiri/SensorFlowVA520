using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows.Forms;

namespace VA520Tester
{
    /// <summary>
    /// Main window. Split over several files (one per tab):
    ///   MainForm.cs            frame, connect/disconnect, timer, settings file
    ///   MainForm.Connection.cs "Connection" tab (all link / timing / logging variables)
    ///   MainForm.Monitor.cs    "Monitor" tab (live values, statistics, trend)
    ///   MainForm.Sensor.cs     "Sensor settings" tab (Modbus ID, baud rate, ... registers 2001-2005)
    ///   MainForm.Tools.cs      "Tools" tab (raw frame, custom read/write, scan, auto-detect, stress test, simulator)
    ///   MainForm.Calc.cs       "Calculators" tab (4..20 mA, pulse output)
    ///   MainForm.Log.cs        "Log" tab (error list, frame trace)
    /// </summary>
    public partial class MainForm : Form
    {
        readonly SettingsBinder binder = new SettingsBinder();
        string settingsPath;

        TabControl tabs;
        Panel toolbar, statusBar;
        Button btnConnect, btnDisconnect, btnStart, btnStop, btnReadOnce;
        CheckBox chkAutoReconnect;
        Label lblLink, lblClock, lblStatus;
        System.Windows.Forms.Timer uiTimer;

        ModbusClient client;
        Poller poller;
        string pollEndReason = "";
        readonly List<ValueSlot> slots = new List<ValueSlot>();
        volatile bool toolBusy;
        volatile bool toolCancel;
        readonly ConcurrentQueue<string> traceQueue = new ConcurrentQueue<string>();
        string lastErrorText = "";

        public MainForm()
        {
            Text = "VA 520 Flow Sensor Tester  -  Modbus RTU / TCP";
            AutoScaleMode = AutoScaleMode.None;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1130, wa.Width - 20), Math.Min(740, wa.Height - 70));
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterScreen;

            settingsPath = Path.Combine(Path.GetDirectoryName(ErrorLogger.FilePath), "VA520Tester.ini");

            BuildSlots();
            BuildToolbar();
            BuildStatusBar();

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            BuildConnectionTab();
            BuildMonitorTab();
            BuildSensorTab();
            BuildToolsTab();
            BuildCalcTab();
            BuildLogTab();

            // docking order: edges first, then the filling control
            Controls.Add(tabs);
            Controls.Add(toolbar);
            Controls.Add(statusBar);
            tabs.BringToFront();

            LoadSettings();
            ApplyLogSettings();

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += OnUiTick;
            uiTimer.Start();

            FormClosing += OnFormClosing;
            Shown += delegate
            {
                if (ErrorLogger.FallbackFolderUsed)
                    SetStatus("Exe folder is not writable - log and settings are in " + Path.GetDirectoryName(ErrorLogger.FilePath));
            };
            UpdateButtons();
        }

        // ------------------------------------------------------------------ small helpers

        void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }   // also covers ObjectDisposedException
        }

        void SetStatus(string text)
        {
            lastErrorText = text;
            if (lblStatus != null) lblStatus.Text = text;
        }

        void Info(string text)
        {
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void Warn(string text)
        {
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        bool Confirm(string text)
        {
            return MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        TabPage AddPage(string title, bool scroll)
        {
            TabPage p = new TabPage(title);
            p.AutoScroll = scroll;
            tabs.TabPages.Add(p);
            return p;
        }

        // ------------------------------------------------------------------ toolbar / status bar

        void BuildToolbar()
        {
            toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 46;

            btnConnect = UI.AddButton(toolbar, "Connect", 8, 8, 90, 30, delegate { DoConnect(); });
            btnDisconnect = UI.AddButton(toolbar, "Disconnect", 102, 8, 90, 30, delegate { DoDisconnect(); });
            btnStart = UI.AddButton(toolbar, "Start polling", 206, 8, 105, 30, delegate { StartPolling(false); });
            btnStop = UI.AddButton(toolbar, "Stop", 315, 8, 70, 30, delegate { StopPolling(); });
            btnReadOnce = UI.AddButton(toolbar, "Read once", 389, 8, 85, 30, delegate { StartPolling(true); });
            chkAutoReconnect = UI.AddCheck(toolbar, "Auto-reconnect", 484, 13, 125, false);
            binder.Bind("autoReconnect", chkAutoReconnect);

            lblLink = UI.AddLabel(toolbar, "Disconnected", 616, 14, 330);
            lblLink.Font = new Font(Font, FontStyle.Bold);
            lblClock = UI.AddLabel(toolbar, "", 950, 14, 160);
            lblClock.TextAlign = ContentAlignment.MiddleRight;
            lblClock.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }

        void BuildStatusBar()
        {
            statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = 24;
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Text = "Ready";
            statusBar.Controls.Add(lblStatus);
        }

        void UpdateButtons()
        {
            bool connected = client != null && client.IsOpen;
            bool polling = poller != null && poller.IsRunning;
            btnConnect.Enabled = !connected;
            btnDisconnect.Enabled = connected;
            btnStart.Enabled = connected && !polling && !toolBusy;
            btnReadOnce.Enabled = connected && !polling && !toolBusy;
            btnStop.Enabled = polling;
            SetLinkGroupsEnabled(!connected);
            UpdateToolButtons();
        }

        // ------------------------------------------------------------------ connect / disconnect

        void DoConnect()
        {
            if (client != null) DoDisconnect();      // dead client left over after a lost link
            try
            {
                ModbusClient c = CreateClient();
                c.Open();
                client = c;
                SetStatus("Connected to " + c.Channel.Description);
            }
            catch (ModbusException ex)
            {
                ErrorLogger.Log(ex, "Connect");
                SetStatus("Connect failed: " + ex.Message);
                Warn(ex.Message);
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.PortOpen, "Connect: " + ex.GetType().Name + ": " + ex.Message);
                Warn(ex.Message);
            }
            UpdateButtons();
        }

        void DoDisconnect()
        {
            StopPolling();
            CancelTool();
            ModbusClient c = client;
            client = null;
            if (c != null)
            {
                try { c.Close(); } catch { }
            }
            SetStatus("Disconnected");
            UpdateButtons();
        }

        ModbusClient CreateClient()
        {
            ModbusClient c;
            string t = cmbTransport.Text;
            if (t == "Serial RTU")
            {
                c = new ModbusClient(new SerialChannel(ReadSerialSettings()), Framing.Rtu);
            }
            else
            {
                TcpChannel ch = new TcpChannel(txtHost.Text.Trim(), (int)numTcpPort.Value, (int)numTcpConn.Value);
                c = new ModbusClient(ch, t == "Modbus TCP" ? Framing.Mbap : Framing.Rtu);
            }
            ConfigureClient(c);
            c.Trace += OnTrace;
            c.TraceEnabled = chkTrace.Checked;
            return c;
        }

        SerialSettings ReadSerialSettings()
        {
            SerialSettings s = new SerialSettings();
            s.PortName = cmbPort.Text.Trim();
            s.BaudRate = int.Parse(cmbBaud.Text);
            s.DataBits = int.Parse(cmbDataBits.Text);
            s.Parity = ParityFromText(cmbParity.Text);
            s.StopBits = cmbStop.Text == "2" ? StopBits.Two : StopBits.One;
            s.DtrEnable = chkDtr.Checked;
            s.RtsStatic = chkRtsStatic.Checked;
            s.RtsToggle = chkRts.Checked;
            s.RtsActiveHigh = chkRtsHigh.Checked;
            s.RtsPreDelayMs = (int)numRtsPre.Value;
            s.RtsPostDelayMs = (int)numRtsPost.Value;
            return s;
        }

        static Parity ParityFromText(string t)
        {
            if (t == "Even") return Parity.Even;
            if (t == "Odd") return Parity.Odd;
            return Parity.None;
        }

        /// <summary>Copies the timing / addressing variables of the form into the client (UI thread only).</summary>
        void ConfigureClient(ModbusClient c)
        {
            c.ResponseTimeoutMs = (int)numTimeout.Value;
            c.Retries = (int)numRetries.Value;
            c.InterFrameDelayMs = (int)numGap.Value;
            c.InterCharTimeoutMs = (int)numInterChar.Value;
            c.TurnaroundDelayMs = (int)numTurn.Value;
            c.AddressOffset = (int)numOffset.Value;
            c.Order = SelectedOrder();
            c.StripEcho = chkEcho.Checked && cmbTransport.Text == "Serial RTU";
            c.TraceEnabled = chkTrace.Checked;
        }

        byte Unit { get { return (byte)numUnit.Value; } }
        byte ReadFc { get { return (byte)(cmbFc.SelectedIndex == 1 ? 4 : 3); } }

        WordOrder SelectedOrder()
        {
            string t = cmbOrder.Text;
            if (t.StartsWith("CDAB")) return WordOrder.CDAB;
            if (t.StartsWith("BADC")) return WordOrder.BADC;
            if (t.StartsWith("DCBA")) return WordOrder.DCBA;
            return WordOrder.ABCD;
        }

        void OnTrace(string s)
        {
            if (traceQueue.Count < 5000)
                traceQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
        }

        // ------------------------------------------------------------------ tool helpers

        /// <summary>Starts a background tool. needClient: the tool uses the connected client.</summary>
        void RunTool(bool needClient, Action<ModbusClient> work)
        {
            if (toolBusy) { Warn("Another tool is still running. Press Stop first."); return; }
            if (needClient && (client == null || !client.IsOpen)) { Warn("Not connected. Press Connect first."); return; }
            if (poller != null && poller.IsRunning) StopPolling();

            ModbusClient cl = client;
            if (cl != null) ConfigureClient(cl);
            toolCancel = false;
            toolBusy = true;
            UpdateButtons();

            Thread t = new Thread(delegate()
            {
                try
                {
                    if (cl != null) cl.CancelCheck = delegate { return toolCancel; };
                    work(cl);
                }
                catch (ModbusException ex)
                {
                    if (ex.Code != ErrCodes.Cancelled)
                    {
                        ErrorLogger.Log(ex, "Tool");
                        string msg = ex.Message;
                        Ui(delegate { SetStatus("Tool error: " + msg); });
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log(ErrCodes.Unhandled, "Tool crashed: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    if (cl != null) cl.CancelCheck = null;
                    toolBusy = false;
                    Ui(delegate { UpdateButtons(); });
                }
            });
            t.IsBackground = true;
            t.Name = "VA520 tool";
            t.Start();
        }

        void CancelTool()
        {
            toolCancel = true;
            for (int i = 0; i < 40 && toolBusy; i++) Thread.Sleep(50);
        }

        // ------------------------------------------------------------------ UI timer

        void OnUiTick(object sender, EventArgs e)
        {
            try
            {
                lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

                bool connected = client != null && client.IsOpen;
                bool polling = poller != null && poller.IsRunning;
                string state;
                if (connected)
                {
                    state = "Connected: " + client.Channel.Description + (polling ? "   [polling]" : "");
                    lblLink.ForeColor = Color.DarkGreen;
                }
                else
                {
                    state = client != null ? "Link lost" : "Disconnected";
                    lblLink.ForeColor = Color.Firebrick;
                }
                if (polling && poller.LinkState.Length > 0) state += "  (" + poller.LinkState + ")";
                lblLink.Text = state;

                if (client != null && !client.IsOpen && !(polling)) UpdateButtons();

                DrainErrors();
                DrainTrace();
                RefreshMonitor();
                RefreshToolStatus();
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Unhandled, "UI refresh: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ settings file

        void LoadSettings()
        {
            try
            {
                Dictionary<string, string> d = SettingsBinder.LoadFile(settingsPath);
                binder.Apply(d);
                for (int i = 0; i < slots.Count; i++)
                {
                    string v;
                    if (d.TryGetValue("chk_" + slots[i].Def.Key, out v) && i < lvValues.Items.Count)
                        lvValues.Items[i].Checked = v == "1";
                }
                UpdateCustomRows();
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Settings, "Cannot read settings file " + settingsPath + ": " + ex.Message);
            }
        }

        bool SaveSettings()
        {
            try
            {
                Dictionary<string, string> d = binder.Collect();
                for (int i = 0; i < slots.Count && i < lvValues.Items.Count; i++)
                    d["chk_" + slots[i].Def.Key] = lvValues.Items[i].Checked ? "1" : "0";
                SettingsBinder.SaveFile(settingsPath, d);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Settings, "Cannot write settings file " + settingsPath + ": " + ex.Message);
                return false;
            }
        }

        void ApplyLogSettings()
        {
            ErrorLogger.MaxFileMb = (int)numLogMax.Value;
            ErrorLogger.SuppressSeconds = (int)numLogSuppress.Value;
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                uiTimer.Stop();
                StopPolling();
                CancelTool();
                StopSimulator();
                ModbusClient c = client;
                client = null;
                if (c != null) c.Close();
                SaveSettings();
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Unhandled, "Closing: " + ex.Message);
            }
        }
    }
}
