using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        // link
        ComboBox cmbTransport, cmbFc, cmbOrder;
        NumericUpDown numUnit, numOffset;
        // serial
        ComboBox cmbPort, cmbBaud, cmbDataBits, cmbParity, cmbStop;
        CheckBox chkDtr, chkRts, chkRtsHigh, chkRtsStatic, chkEcho;
        NumericUpDown numRtsPre, numRtsPost;
        // tcp
        TextBox txtHost;
        NumericUpDown numTcpPort, numTcpConn;
        // timing
        NumericUpDown numTimeout, numRetries, numGap, numInterChar, numTurn;
        CheckBox chkSkipTimeout;
        // files
        NumericUpDown numLogMax, numLogSuppress;
        CheckBox chkCsv;
        TextBox txtLogPath;
        // custom channels / limits
        readonly CheckBox[] chkCustom = new CheckBox[4];
        readonly TextBox[] txtCustomName = new TextBox[4];
        readonly NumericUpDown[] numCustomAddr = new NumericUpDown[4];
        readonly ComboBox[] cmbCustomType = new ComboBox[4];
        readonly CheckBox[] chkLim = new CheckBox[3];
        readonly NumericUpDown[] numLimMin = new NumericUpDown[3];
        readonly NumericUpDown[] numLimMax = new NumericUpDown[3];
        static readonly string[] LimitKeys = new string[] { "flow_m3h", "vel_ms", "temp_C" };
        static readonly string[] LimitNames = new string[] { "Flow [m3/h]", "Velocity [m/s]", "Gas temperature [°C]" };

        readonly List<Control> linkGroups = new List<Control>();

        static int GroupHeight(int rows) { return 26 + rows * 27 + 8; }

        void BuildConnectionTab()
        {
            TabPage page = AddPage("Connection", true);

            // ---------------------------------------------------------------- Link
            GroupBox gLink = UI.AddGroup(page, "Link", 8, 8, 350, GroupHeight(5));
            Grid g = new Grid(gLink, 10, 22, 130, 200);
            cmbTransport = binder.Bind("transport", g.Add("Transport", UI.NewCombo(false, "Serial RTU", "Modbus TCP", "RTU over TCP")));
            cmbTransport.SelectedIndexChanged += delegate { SetLinkGroupsEnabled(client == null || !client.IsOpen); };
            numUnit = binder.Bind("unit", g.Add("Slave ID (1..247)", UI.NewNum(0, 255, 1, 0)));
            cmbFc = binder.Bind("readFc", g.Add("Read function", UI.NewCombo(false, "03 Read holding registers", "04 Read input registers")));
            cmbOrder = binder.Bind("order", g.Add("Word / byte order", UI.NewCombo(false, "ABCD (big endian)", "CDAB (middle endian)", "BADC (byte swap)", "DCBA (little endian)")));
            numOffset = binder.Bind("addrOffset", g.Add("Address offset (+/-)", UI.NewNum(-10, 10, 0, 0)));

            // ---------------------------------------------------------------- Serial
            GroupBox gSer = UI.AddGroup(page, "Serial port (RS485)", 8, 8 + GroupHeight(5) + 8, 350, GroupHeight(12) + 6);
            linkGroups.Add(gSer);
            g = new Grid(gSer, 10, 22, 130, 135);
            cmbPort = binder.Bind("port", g.Add("Port", UI.NewCombo(true, DefaultPortName())));
            Button refresh = UI.AddButton(gSer, "Refresh", 280, g.Y - 27, 62, 23, delegate { RefreshPorts(); });
            cmbBaud = binder.Bind("baud", g.Add("Baud rate", UI.NewCombo(false, "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200")));
            cmbBaud.SelectedIndex = 4;
            cmbDataBits = binder.Bind("dataBits", g.Add("Data bits", UI.NewCombo(false, "8", "7")));
            cmbParity = binder.Bind("parity", g.Add("Parity", UI.NewCombo(false, "None", "Even", "Odd")));
            cmbParity.SelectedIndex = 1;
            cmbStop = binder.Bind("stopBits", g.Add("Stop bits", UI.NewCombo(false, "1", "2")));
            g = new Grid(gSer, 10, g.Y + 2, 215, 110);
            chkDtr = binder.Bind("dtr", g.AddWide(UI.NewCheck("Set DTR high (many tools do this)", false)));
            chkRtsStatic = binder.Bind("rtsStatic", g.AddWide(UI.NewCheck("Keep RTS high all the time", false)));
            chkRts = binder.Bind("rtsToggle", g.AddWide(UI.NewCheck("RTS controls RS485 direction (TX enable)", false)));
            chkRtsHigh = binder.Bind("rtsHigh", g.AddWide(UI.NewCheck("RTS active level = high while sending", true)));
            numRtsPre = binder.Bind("rtsPre", g.Add("RTS delay before TX [ms]", UI.NewNum(0, 1000, 1, 0)));
            numRtsPost = binder.Bind("rtsPost", g.Add("RTS delay after TX [ms]", UI.NewNum(0, 1000, 1, 0)));
            chkEcho = binder.Bind("stripEcho", g.AddWide(UI.NewCheck("Converter echoes my request: remove the echo", false)));
            cmbPort.Width = 135;

            // ---------------------------------------------------------------- TCP
            int col2 = 366;
            GroupBox gTcp = UI.AddGroup(page, "TCP (Modbus TCP / RTU over TCP)", col2, 8, 340, GroupHeight(3));
            linkGroups.Add(gTcp);
            g = new Grid(gTcp, 10, 22, 130, 190);
            txtHost = binder.Bind("host", g.Add("Host / IP", UI.NewText("127.0.0.1")));
            numTcpPort = binder.Bind("tcpPort", g.Add("TCP port", UI.NewNum(1, 65535, 502, 0)));
            numTcpConn = binder.Bind("tcpConnTimeout", g.Add("Connect timeout [ms]", UI.NewNum(100, 60000, 3000, 0)));

            // ---------------------------------------------------------------- Timing
            GroupBox gTime = UI.AddGroup(page, "Timing and retries", col2, 8 + GroupHeight(3) + 8, 340, GroupHeight(6));
            g = new Grid(gTime, 10, 22, 190, 130);
            numTimeout = binder.Bind("timeout", g.Add("Response timeout [ms]", UI.NewNum(20, 60000, 1000, 0)));
            numRetries = binder.Bind("retries", g.Add("Retries per request", UI.NewNum(0, 20, 0, 0)));
            numGap = binder.Bind("gap", g.Add("Silent gap before request [ms]", UI.NewNum(0, 2000, 10, 0)));
            numInterChar = binder.Bind("interChar", g.Add("Frame-end silence [ms]", UI.NewNum(2, 2000, 30, 0)));
            numTurn = binder.Bind("turnaround", g.Add("Pause after answer [ms]", UI.NewNum(0, 5000, 0, 0)));
            g = new Grid(gTime, 10, g.Y, 190, 130);
            chkSkipTimeout = binder.Bind("skipAfterTimeout", g.AddWide(UI.NewCheck("Skip rest of the cycle after a timeout", false)));
            chkSkipTimeout.Width = 320;

            // ---------------------------------------------------------------- Files
            int yFiles = 8 + GroupHeight(3) + 8 + GroupHeight(6) + 8;
            GroupBox gFile = UI.AddGroup(page, "Error log, CSV and settings file", col2, yFiles, 340, GroupHeight(5) + 14);
            g = new Grid(gFile, 10, 22, 130, 190);
            txtLogPath = g.Add("Error log file", UI.NewText(ErrorLogger.FilePath));
            txtLogPath.ReadOnly = true;
            numLogMax = binder.Bind("logMaxMb", g.Add("Max log size [MB]", UI.NewNum(0, 500, 5, 0)));
            numLogSuppress = binder.Bind("logSuppress", g.Add("Repeat suppression [s]", UI.NewNum(0, 3600, 0, 0)));
            numLogMax.ValueChanged += delegate { ApplyLogSettings(); };
            numLogSuppress.ValueChanged += delegate { ApplyLogSettings(); };
            chkCsv = binder.Bind("csvLog", g.AddWide(UI.NewCheck("Write readings to a CSV file (next to the exe)", false)));
            UI.AddButton(gFile, "Save settings", 10, g.Y + 4, 100, 26, delegate
            {
                if (SaveSettings()) SetStatus("Settings saved to " + settingsPath);
                else Warn("Cannot save settings - see error log.");
            });
            UI.AddButton(gFile, "Reload", 116, g.Y + 4, 80, 26, delegate { LoadSettings(); SetStatus("Settings reloaded"); });
            UI.AddButton(gFile, "Defaults", 202, g.Y + 4, 80, 26, delegate
            {
                if (!Confirm("Delete the settings file and restore factory defaults after the next start?")) return;
                try { if (File.Exists(settingsPath)) File.Delete(settingsPath); Info("Settings file deleted. Restart the program."); }
                catch (Exception ex) { ErrorLogger.Log(ErrCodes.Settings, "Cannot delete " + settingsPath + ": " + ex.Message); }
            });

            // ---------------------------------------------------------------- Custom channels
            int col3 = 714;
            GroupBox gCust = UI.AddGroup(page, "Custom channels (appear in the Monitor list)", col3, 8, 395, GroupHeight(5));
            UI.AddLabel(gCust, "On", 8, 20, 26);
            UI.AddLabel(gCust, "Label", 38, 20, 100);
            UI.AddLabel(gCust, "Address", 168, 20, 60);
            UI.AddLabel(gCust, "Type", 244, 20, 60);
            int[] defAddr = new int[] { 1100, 1346, 1418, 1268 };
            string[] defType = new string[] { "Float32", "Float32", "Float32", "UInt32" };
            for (int i = 0; i < 4; i++)
            {
                int y = 42 + i * 27;
                chkCustom[i] = binder.Bind("cust" + i + "_en", UI.AddCheck(gCust, "", 10, y + 2, 22, false));
                txtCustomName[i] = binder.Bind("cust" + i + "_name", new TextBox());
                txtCustomName[i].SetBounds(38, y, 124, 22);
                txtCustomName[i].Text = "Custom " + (i + 1);
                gCust.Controls.Add(txtCustomName[i]);
                numCustomAddr[i] = binder.Bind("cust" + i + "_addr", UI.NewNum(0, 65535, defAddr[i], 0));
                numCustomAddr[i].SetBounds(168, y, 70, 22);
                gCust.Controls.Add(numCustomAddr[i]);
                cmbCustomType[i] = binder.Bind("cust" + i + "_type", UI.NewCombo(false, "Float32", "UInt32", "Int32", "UInt16", "Int16"));
                cmbCustomType[i].SetBounds(244, y, 100, 22);
                cmbCustomType[i].Text = defType[i];
                gCust.Controls.Add(cmbCustomType[i]);
                chkCustom[i].CheckedChanged += delegate { UpdateCustomRows(); };
                txtCustomName[i].TextChanged += delegate { UpdateCustomRows(); };
                numCustomAddr[i].ValueChanged += delegate { UpdateCustomRows(); };
                cmbCustomType[i].SelectedIndexChanged += delegate { UpdateCustomRows(); };
            }

            // ---------------------------------------------------------------- Limits
            int yLim = 8 + GroupHeight(5) + 8;
            GroupBox gLim = UI.AddGroup(page, "Limit checks (a violation is written to the error log)", col3, yLim, 395, GroupHeight(4));
            UI.AddLabel(gLim, "Min", 192, 20, 60);
            UI.AddLabel(gLim, "Max", 298, 20, 60);
            decimal[] mins = new decimal[] { 0, 0, -30 };
            decimal[] maxs = new decimal[] { 1000, 200, 80 };
            for (int i = 0; i < 3; i++)
            {
                int y = 42 + i * 27;
                chkLim[i] = binder.Bind("lim" + i + "_en", UI.AddCheck(gLim, LimitNames[i], 10, y + 1, 180, false));
                numLimMin[i] = binder.Bind("lim" + i + "_min", UI.NewNum(-1000000, 1000000, mins[i], 3));
                numLimMin[i].SetBounds(192, y, 98, 22);
                gLim.Controls.Add(numLimMin[i]);
                numLimMax[i] = binder.Bind("lim" + i + "_max", UI.NewNum(-1000000, 1000000, maxs[i], 3));
                numLimMax[i].SetBounds(298, y, 90, 22);
                gLim.Controls.Add(numLimMax[i]);
            }

            // ---------------------------------------------------------------- Factory defaults (from the manual)
            int yInfo = yLim + GroupHeight(4) + 8;
            GroupBox gInfo = UI.AddGroup(page, "VA 520 factory defaults (manual V1.26)", col3, yInfo, 395, 130);
            Label info = UI.AddLabel(gInfo, "", 10, 20, 375);
            info.Height = 104;
            info.Text = "Modbus ID 1, 19200 baud, 8 data bits, even parity, 1 stop bit,\r\n"
                      + "word order ABCD (big endian). Function 03 = read, 16 = write.\r\n"
                      + "Supply 18..36 VDC. M12 plug A: pin1 +VB (brown), pin2 RS485 A (white),\r\n"
                      + "pin3 -VB (blue), pin4 RS485 B (black), pin5 4..20 mA (grey).\r\n"
                      + "Bus end: switch the internal 120 ohm termination ON (or 120R between pin 2 and 4).";
        }

        static string DefaultPortName()
        {
            List<string> ports = SerialChannel.ListPorts();
            if (ports.Count > 0) return ports[0];
            return Path.DirectorySeparatorChar == '/' ? "/dev/ttyUSB0" : "COM1";
        }

        void RefreshPorts()
        {
            string cur = cmbPort.Text;
            cmbPort.Items.Clear();
            List<string> ports = SerialChannel.ListPorts();
            foreach (string p in ports) cmbPort.Items.Add(p);
            cmbPort.Text = cur.Length > 0 ? cur : (ports.Count > 0 ? ports[0] : DefaultPortName());
            SetStatus(ports.Count + " serial port(s) found");
        }

        void SetLinkGroupsEnabled(bool enabled)
        {
            bool serial = cmbTransport.Text == "Serial RTU";
            foreach (Control c in linkGroups)
            {
                bool isSerial = c.Text.StartsWith("Serial");
                c.Enabled = enabled && (isSerial ? serial : !serial);
            }
            cmbTransport.Enabled = enabled;
        }

        // ------------------------------------------------------------------ slots (rows of the Monitor list)

        void BuildSlots()
        {
            slots.Clear();
            foreach (RegDef d in Va520Map.Values)
            {
                ValueSlot s = new ValueSlot();
                s.Def = d;
                s.Enabled = d.DefaultEnabled;
                slots.Add(s);
            }
            for (int i = 0; i < 4; i++)
            {
                RegDef d = new RegDef();
                d.Key = "custom" + (i + 1);
                d.Name = "Custom " + (i + 1);
                d.Unit = "";
                d.Address = 1100;
                d.Type = RegType.Float32;
                d.Group = "Custom";
                d.IsCustom = true;
                ValueSlot s = new ValueSlot();
                s.Def = d;
                slots.Add(s);
            }
        }

        /// <summary>Copies the custom channel settings into the slots and the list rows.</summary>
        void UpdateCustomRows()
        {
            if (chkCustom[3] == null || lvValues == null) return;
            int baseIdx = Va520Map.Values.Count;
            for (int i = 0; i < 4; i++)
            {
                ValueSlot s = slots[baseIdx + i];
                RegDef d = s.Def;
                d.Name = txtCustomName[i].Text;
                d.Address = (int)numCustomAddr[i].Value;
                switch (cmbCustomType[i].Text)
                {
                    case "UInt32": d.Type = RegType.UInt32; break;
                    case "Int32": d.Type = RegType.Int32; break;
                    case "UInt16": d.Type = RegType.UInt16; break;
                    case "Int16": d.Type = RegType.Int16; break;
                    default: d.Type = RegType.Float32; break;
                }
                if (baseIdx + i < lvValues.Items.Count)
                {
                    ListViewItem it = lvValues.Items[baseIdx + i];
                    it.Text = d.Name;
                    it.SubItems[2].Text = d.ModbusRegister + " (" + d.Address + ")";
                    if (it.Checked != chkCustom[i].Checked) it.Checked = chkCustom[i].Checked;
                }
            }
        }

        /// <summary>Transfers the limit settings into the slots before polling.</summary>
        void ApplyLimits()
        {
            foreach (ValueSlot s in slots) s.LimitEnabled = false;
            for (int i = 0; i < 3; i++)
            {
                RegDef d = Va520Map.Find(LimitKeys[i]);
                foreach (ValueSlot s in slots)
                {
                    if (s.Def != d) continue;
                    s.LimitEnabled = chkLim[i].Checked;
                    s.LimitMin = (double)numLimMin[i].Value;
                    s.LimitMax = (double)numLimMax[i].Value;
                }
            }
        }
    }
}
