using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        TabControl toolTabs;
        readonly List<Button> toolStartButtons = new List<Button>();
        readonly List<Button> toolStopButtons = new List<Button>();

        // raw
        TextBox txtRawHex, txtRawOut;
        CheckBox chkRawWrap;
        NumericUpDown numRawTimeout;
        // read / write
        NumericUpDown numRwAddr, numRwCount, numRwRepeat, numRwInterval, numRwWAddr;
        ComboBox cmbRwType;
        TextBox txtRwValues, txtRwOut;
        Label lblRwHint;
        // scan
        NumericUpDown numScanStart, numScanEnd, numScanStep, numScanTimeout;
        ComboBox cmbScanQty;
        CheckBox chkScanExc;
        TextBox txtScanOut;
        Label lblScanProg;
        volatile string scanProgress = "";
        // auto detect
        readonly List<CheckBox> chkAutoBaud = new List<CheckBox>();
        readonly List<CheckBox> chkAutoParity = new List<CheckBox>();
        readonly List<CheckBox> chkAutoStop = new List<CheckBox>();
        NumericUpDown numAutoIdFrom, numAutoIdTo, numAutoTimeout, numAutoAddr;
        ListView lvAuto;
        Label lblAutoProg;
        volatile string autoProgress = "";
        // stress
        ComboBox cmbStressValue;
        NumericUpDown numStressCount, numStressDelay;
        TextBox txtStressOut;
        Label lblStressProg;
        volatile string stressProgress = "";
        string lastReport = "";
        // simulator
        Va520Simulator sim;
        NumericUpDown numSimPort, numSimUnit, numSimDrop, numSimCrc, numSimDelay, numSimFlow;
        ComboBox cmbSimFraming, cmbSimOrder;
        Label lblSimStatus;
        Button btnSimStart, btnSimStop;
        CheckBox chkSimEcho;

        void BuildToolsTab()
        {
            TabPage page = AddPage("Tools", false);
            toolTabs = new TabControl();
            toolTabs.Dock = DockStyle.Fill;
            page.Controls.Add(toolTabs);

            BuildRawPage();
            BuildReadWritePage();
            BuildScanPage();
            BuildAutoDetectPage();
            BuildStressPage();
            BuildSimulatorPage();
        }

        TabPage AddToolPage(string title)
        {
            TabPage p = new TabPage(title);
            toolTabs.TabPages.Add(p);
            return p;
        }

        Button ToolStart(Control parent, string text, int x, int y, int w, EventHandler click)
        {
            Button b = UI.AddButton(parent, text, x, y, w, 27, click);
            toolStartButtons.Add(b);
            return b;
        }

        Button ToolStop(Control parent, string text, int x, int y, int w)
        {
            Button b = UI.AddButton(parent, text, x, y, w, 27, delegate { toolCancel = true; });
            b.Enabled = false;
            toolStopButtons.Add(b);
            return b;
        }

        void UpdateToolButtons()
        {
            foreach (Button b in toolStartButtons) b.Enabled = !toolBusy;
            foreach (Button b in toolStopButtons) b.Enabled = toolBusy;
            if (btnSimStart != null)
            {
                btnSimStart.Enabled = sim == null || !sim.IsRunning;
                btnSimStop.Enabled = sim != null && sim.IsRunning;
            }
        }

        void Out(TextBox tb, string line)
        {
            Ui(delegate { UI.Append(tb, line, 200000); });
        }

        void RefreshToolStatus()
        {
            if (lblScanProg == null) return;
            if (lblScanProg.Text != scanProgress) lblScanProg.Text = scanProgress;
            if (lblAutoProg.Text != autoProgress) lblAutoProg.Text = autoProgress;
            if (lblStressProg.Text != stressProgress) lblStressProg.Text = stressProgress;
            if (sim != null && sim.IsRunning)
                lblSimStatus.Text = "Simulator running on 127.0.0.1:" + sim.Port + "   requests: " + sim.RequestCount + "   injected faults: " + sim.ErrorCount;
            else if (lblSimStatus.Text.StartsWith("Simulator running"))
                lblSimStatus.Text = "Simulator stopped";
        }

        // ================================================================== 1. raw frame

        void BuildRawPage()
        {
            TabPage p = AddToolPage("Raw frame");
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 112;

            UI.AddLabel(top, "Bytes (hex)", 8, 10, 80);
            txtRawHex = UI.AddText(top, 92, 8, 520, "01 03 04 4C 00 02");
            txtRawHex.Font = UI.Mono;
            chkRawWrap = UI.AddCheck(top, "Add CRC (RTU) / MBAP header (TCP) automatically  -  type only: slave id, function, data", 92, 36, 620, true);
            UI.AddLabel(top, "Timeout [ms]", 8, 66, 80);
            numRawTimeout = UI.NewNum(20, 60000, 1000, 0);
            numRawTimeout.SetBounds(92, 64, 80, 22);
            top.Controls.Add(numRawTimeout);
            ToolStart(top, "Send", 190, 61, 80, delegate { SendRaw(); });
            UI.AddButton(top, "Clear", 276, 61, 70, 27, delegate { txtRawOut.Text = ""; });
            UI.AddLabel(top, "Example", 360, 66, 60);
            ComboBox ex = UI.NewCombo(false, "Read flow m3/h (reg 1101)", "Read velocity m/s (reg 1347)", "Read gas temp C (reg 1419)",
                "Read consumption m3 (reg 1269)", "Read ID..word order (2001..2005)");
            ex.SetBounds(424, 63, 240, 22);
            ex.SelectedIndexChanged += delegate
            {
                int[] addr = new int[] { 1100, 1346, 1418, 1268, 2000 };
                int[] qty = new int[] { 2, 2, 2, 2, 5 };
                int i = ex.SelectedIndex;
                if (i < 0) return;
                txtRawHex.Text = ModbusCodec.Hex(new byte[] { Unit, ReadFc, (byte)(addr[i] >> 8), (byte)addr[i], 0, (byte)qty[i] });
            };
            top.Controls.Add(ex);
            ex.SelectedIndex = 0;

            txtRawOut = UI.AddOutput(p, DockStyle.Fill);
            p.Controls.Add(top);
            txtRawOut.BringToFront();
        }

        void SendRaw()
        {
            byte[] bytes = ModbusCodec.ParseHex(txtRawHex.Text);
            if (bytes == null || bytes.Length < 2) { Warn("Please enter valid hex bytes, e.g. 01 03 04 4C 00 02"); return; }
            bool wrap = chkRawWrap.Checked;
            int timeout = (int)numRawTimeout.Value;
            RunTool(true, delegate(ModbusClient cl)
            {
                byte[] frame = wrap ? cl.WrapForSend(bytes) : bytes;
                Out(txtRawOut, DateTime.Now.ToString("HH:mm:ss.fff") + "  TX " + ModbusCodec.Hex(frame));
                byte[] rx = cl.SendRaw(frame, timeout);
                if (rx.Length == 0)
                {
                    Out(txtRawOut, "                  RX (nothing received within " + timeout + " ms)");
                    ErrorLogger.Log(ErrCodes.Timeout, "Raw frame: no response to " + ModbusCodec.Hex(frame));
                }
                else
                {
                    Out(txtRawOut, DateTime.Now.ToString("HH:mm:ss.fff") + "  RX " + ModbusCodec.Hex(rx));
                    Out(txtRawOut, "                  " + DescribeFrame(cl, rx));
                }
            });
        }

        static string DescribeFrame(ModbusClient cl, byte[] b)
        {
            try
            {
                int unitIdx, pduIdx, pduLen;
                StringBuilder sb = new StringBuilder();
                if (cl.Framing == Framing.Rtu)
                {
                    if (b.Length < 4) return "frame too short";
                    ushort calc = ModbusCodec.Crc16(b, 0, b.Length - 2);
                    ushort got = (ushort)(b[b.Length - 2] | (b[b.Length - 1] << 8));
                    sb.Append(calc == got ? "CRC ok; " : "CRC ERROR (calculated " + calc.ToString("X4") + ", received " + got.ToString("X4") + "); ");
                    unitIdx = 0; pduIdx = 1; pduLen = b.Length - 3;
                }
                else
                {
                    if (b.Length < 8) return "frame too short";
                    sb.Append("MBAP transaction " + ((b[0] << 8) | b[1]) + "; ");
                    unitIdx = 6; pduIdx = 7; pduLen = b.Length - 7;
                }
                sb.Append("slave " + b[unitIdx] + ", function " + (b[pduIdx] & 0x7F));
                if ((b[pduIdx] & 0x80) != 0)
                {
                    int code = pduLen > 1 ? b[pduIdx + 1] : 0;
                    sb.Append(" -> EXCEPTION " + code + " (" + ModbusCodec.ExceptionText(code) + ")");
                }
                else if ((b[pduIdx] == 3 || b[pduIdx] == 4) && pduLen >= 2)
                {
                    int n = b[pduIdx + 1];
                    sb.Append(", " + n + " data bytes");
                    if (n == 4 && pduLen >= 6)
                    {
                        sb.Append(": float32 = " + ModbusCodec.ToFloat(b, pduIdx + 2, cl.Order).ToString("0.####", CultureInfo.InvariantCulture));
                        sb.Append(", uint32 = " + ModbusCodec.Decode(RegType.UInt32, b, pduIdx + 2, cl.Order).ToString("0", CultureInfo.InvariantCulture));
                    }
                    else if (n == 2 && pduLen >= 4)
                    {
                        sb.Append(": uint16 = " + ModbusCodec.ToUInt16(b, pduIdx + 2));
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "cannot decode: " + ex.Message;
            }
        }

        // ================================================================== 2. read / write registers

        void BuildReadWritePage()
        {
            TabPage p = AddToolPage("Read / write registers");
            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 360;

            GroupBox gr = UI.AddGroup(left, "Read (slave id and function from the Connection tab)", 4, 4, 350, 232);
            Grid g = new Grid(gr, 8, 22, 140, 185);
            numRwAddr = g.Add("Address (0-based)", UI.NewNum(0, 65535, 1100, 0));
            numRwCount = g.Add("Number of registers", UI.NewNum(1, 125, 2, 0));
            cmbRwType = g.Add("Show as", UI.NewCombo(false, "Raw registers", "Float32", "UInt32", "Int32", "UInt16", "Int16"));
            cmbRwType.SelectedIndex = 1;
            numRwRepeat = g.Add("Repeat (0 = until Stop)", UI.NewNum(0, 1000000, 1, 0));
            numRwInterval = g.Add("Interval [ms]", UI.NewNum(0, 600000, 500, 0));
            lblRwHint = UI.AddLabel(gr, "", 8, g.Y + 2, 335);
            numRwAddr.ValueChanged += delegate { lblRwHint.Text = "Modbus register number in the manual = " + (numRwAddr.Value + 1); };
            lblRwHint.Text = "Modbus register number in the manual = " + (numRwAddr.Value + 1);
            ToolStart(gr, "Read", 8, 198, 90, delegate { ReadRegistersTool(); });
            ToolStop(gr, "Stop", 104, 198, 80);

            GroupBox gw = UI.AddGroup(left, "Write registers (function 16)", 4, 244, 350, 130);
            Grid g2 = new Grid(gw, 8, 22, 100, 235);
            numRwWAddr = g2.Add("Address", UI.NewNum(0, 65535, 2000, 0));
            txtRwValues = g2.Add("Values", UI.NewText("1"));
            UI.AddLabel(gw, "decimal or 0x hex, separated by comma/space", 8, g2.Y - 2, 335);
            ToolStart(gw, "Write", 8, 96, 90, delegate { WriteRegistersTool(); });

            txtRwOut = UI.AddOutput(p, DockStyle.Fill);
            p.Controls.Add(left);
            txtRwOut.BringToFront();
        }

        void ReadRegistersTool()
        {
            int addr = (int)numRwAddr.Value, count = (int)numRwCount.Value;
            int repeat = (int)numRwRepeat.Value, interval = (int)numRwInterval.Value;
            string show = cmbRwType.Text;
            byte unit = Unit, fc = ReadFc;
            RunTool(true, delegate(ModbusClient cl)
            {
                Out(txtRwOut, "--- read FC" + fc.ToString("00") + " unit " + unit + " address " + addr + " count " + count + " ---");
                for (int n = 0; (repeat == 0 || n < repeat) && !toolCancel; n++)
                {
                    try
                    {
                        byte[] d = cl.ReadRegisters(unit, fc, addr, count);
                        string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        if (show == "Raw registers")
                        {
                            for (int k = 0; k < count; k++)
                            {
                                ushort u = ModbusCodec.ToUInt16(d, k * 2);
                                Out(txtRwOut, stamp + "  addr " + (addr + k) + " (reg " + (addr + k + 1) + "): 0x" + u.ToString("X4") + " = " + u + " / " + (short)u);
                            }
                        }
                        else
                        {
                            RegType t = (RegType)Enum.Parse(typeof(RegType), show);
                            int step = ModbusCodec.RegisterCount(t);
                            for (int k = 0; k + step <= count; k += step)
                            {
                                double v = ModbusCodec.Decode(t, d, k * 2, cl.Order);
                                Out(txtRwOut, stamp + "  addr " + (addr + k) + " (reg " + (addr + k + 1) + "): " + v.ToString("0.######", CultureInfo.InvariantCulture)
                                    + "   [" + ModbusCodec.Hex(d, k * 2, step * 2) + "]");
                            }
                        }
                    }
                    catch (ModbusException ex)
                    {
                        if (ex.Code == ErrCodes.Cancelled) break;
                        ErrorLogger.Log(ex, "Read addr " + addr + " x" + count);
                        Out(txtRwOut, DateTime.Now.ToString("HH:mm:ss.fff") + "  ERROR E" + ex.Code + ": " + ex.Message);
                    }
                    for (int w = 0; w < interval && !toolCancel; w += 20) Thread.Sleep(Math.Min(20, interval - w));
                }
                Out(txtRwOut, "--- done ---");
            });
        }

        static bool TryParseRegValue(string s, out ushort v)
        {
            s = s.Trim();
            int i;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out i) && i >= 0 && i <= 0xFFFF) { v = (ushort)i; return true; }
            }
            else if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
            {
                if (i < 0 && i >= -32768) i += 65536;
                if (i >= 0 && i <= 0xFFFF) { v = (ushort)i; return true; }
            }
            v = 0;
            return false;
        }

        void WriteRegistersTool()
        {
            List<ushort> vals = new List<ushort>();
            foreach (string part in txtRwValues.Text.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ushort u;
                if (!TryParseRegValue(part, out u)) { Warn("Cannot read '" + part + "' as a 16 bit value."); return; }
                vals.Add(u);
            }
            if (vals.Count == 0) { Warn("Enter at least one value."); return; }
            int addr = (int)numRwWAddr.Value;
            if (!Confirm("Write " + vals.Count + " register(s) starting at address " + addr + "?\r\n\r\nThis changes the sensor.")) return;
            byte unit = Unit;
            ushort[] arr = vals.ToArray();
            RunTool(true, delegate(ModbusClient cl)
            {
                try
                {
                    cl.WriteRegisters(unit, addr, arr);
                    Out(txtRwOut, DateTime.Now.ToString("HH:mm:ss.fff") + "  WROTE address " + addr + ": " + string.Join(", ", Array.ConvertAll(arr, delegate(ushort x) { return "0x" + x.ToString("X4"); })));
                }
                catch (ModbusException ex)
                {
                    ErrorLogger.Log(ErrCodes.WriteFailed, "Write addr " + addr + ": E" + ex.Code + " " + ex.Message);
                    Out(txtRwOut, DateTime.Now.ToString("HH:mm:ss.fff") + "  WRITE ERROR E" + ex.Code + ": " + ex.Message);
                }
            });
        }

        // ================================================================== 3. register scan

        void BuildScanPage()
        {
            TabPage p = AddToolPage("Register scan");
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 150;
            Grid g = new Grid(top, 8, 6, 150, 90);
            numScanStart = g.Add("First address", UI.NewNum(0, 65535, 1000, 0));
            numScanEnd = g.Add("Last address", UI.NewNum(0, 65535, 1500, 0));
            numScanStep = g.Add("Step", UI.NewNum(1, 1000, 1, 0));
            numScanTimeout = g.Add("Timeout per request [ms]", UI.NewNum(20, 10000, 300, 0));
            Grid g2 = new Grid(top, 300, 6, 130, 90);
            cmbScanQty = g2.Add("Registers per request", UI.NewCombo(false, "1", "2"));
            ToolStart(top, "Start scan", 300, 62, 100, delegate { ScanTool(); });
            ToolStop(top, "Stop", 406, 62, 70);
            UI.AddButton(top, "Preset: values 1000..1500", 300, 94, 170, 26, delegate { numScanStart.Value = 1000; numScanEnd.Value = 1500; cmbScanQty.SelectedIndex = 0; });
            UI.AddButton(top, "Preset: settings 2000..2010", 476, 94, 180, 26, delegate { numScanStart.Value = 2000; numScanEnd.Value = 2010; cmbScanQty.SelectedIndex = 0; });
            chkScanExc = UI.AddCheck(top, "also list addresses answered with a Modbus exception", 8, 124, 400, false);
            lblScanProg = UI.AddLabel(top, "", 420, 124, 500);

            txtScanOut = UI.AddOutput(p, DockStyle.Fill);
            p.Controls.Add(top);
            txtScanOut.BringToFront();
        }

        void ScanTool()
        {
            int first = (int)numScanStart.Value, last = (int)numScanEnd.Value, step = (int)numScanStep.Value;
            int qty = cmbScanQty.SelectedIndex == 1 ? 2 : 1;
            int timeout = (int)numScanTimeout.Value;
            bool listExc = chkScanExc.Checked;
            if (last < first) { Warn("Last address must not be smaller than the first address."); return; }
            byte unit = Unit, fc = ReadFc;
            txtScanOut.Text = "";
            RunTool(true, delegate(ModbusClient cl)
            {
                int oldTimeout = cl.ResponseTimeoutMs, oldRetries = cl.Retries;
                cl.ResponseTimeoutMs = timeout;
                cl.Retries = 0;
                List<int> found = new List<int>();
                int total = (last - first) / step + 1, done = 0, exceptions = 0, errors = 0, logged = 0, consecutiveTimeouts = 0;
                Stopwatch sw = Stopwatch.StartNew();
                Out(txtScanOut, "--- scan addresses " + first + ".." + last + " step " + step + ", " + qty + " register(s) per request, FC" + fc.ToString("00") + " ---");
                try
                {
                    for (int a = first; a <= last && !toolCancel; a += step)
                    {
                        done++;
                        scanProgress = "address " + a + "   (" + done + " / " + total + ")   found: " + found.Count;
                        try
                        {
                            byte[] d = cl.ReadRegisters(unit, fc, a, qty);
                            consecutiveTimeouts = 0;
                            found.Add(a);
                            string line = "addr " + a + " (reg " + (a + 1) + "):  " + ModbusCodec.Hex(d);
                            line += "   u16 = " + ModbusCodec.ToUInt16(d, 0);
                            if (qty == 2) line += "   float32 = " + ModbusCodec.ToFloat(d, 0, cl.Order).ToString("0.####", CultureInfo.InvariantCulture)
                                + "   uint32 = " + ModbusCodec.Decode(RegType.UInt32, d, 0, cl.Order).ToString("0", CultureInfo.InvariantCulture);
                            Out(txtScanOut, line);
                        }
                        catch (ModbusException ex)
                        {
                            if (ex.Code == ErrCodes.Cancelled) break;
                            if (ex.IsModbusException)
                            {
                                consecutiveTimeouts = 0;
                                exceptions++;
                                if (listExc) Out(txtScanOut, "addr " + a + ": exception " + ex.ExceptionCode + " (" + ModbusCodec.ExceptionText(ex.ExceptionCode) + ")");
                            }
                            else
                            {
                                errors++;
                                if (ex.Code == ErrCodes.Timeout) consecutiveTimeouts++; else consecutiveTimeouts = 0;
                                if (logged < 50) { ErrorLogger.Log(ex, "Scan addr " + a); logged++; }
                                Out(txtScanOut, "addr " + a + ": E" + ex.Code + " " + ex.Message);
                                if (ex.IsLinkError) { Out(txtScanOut, "link lost - scan aborted"); break; }
                                if (consecutiveTimeouts >= 30) { Out(txtScanOut, "30 timeouts in a row - the sensor does not answer, scan aborted"); break; }
                            }
                        }
                    }
                }
                finally
                {
                    cl.ResponseTimeoutMs = oldTimeout;
                    cl.Retries = oldRetries;
                }
                sw.Stop();
                Out(txtScanOut, "--- finished in " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s: " + found.Count + " readable, " + exceptions + " exception answers, " + errors + " communication errors"
                    + (logged >= 50 ? " (only the first 50 errors were written to the error log)" : "") + " ---");
                if (found.Count > 0) Out(txtScanOut, "readable address ranges: " + FormatRanges(found, step));
                scanProgress = "finished";
            });
        }

        static string FormatRanges(List<int> a, int step)
        {
            StringBuilder sb = new StringBuilder();
            int start = a[0], prev = a[0];
            for (int i = 1; i <= a.Count; i++)
            {
                if (i < a.Count && a[i] == prev + step) { prev = a[i]; continue; }
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(start == prev ? start.ToString() : start + ".." + prev);
                if (i < a.Count) { start = a[i]; prev = a[i]; }
            }
            return sb.ToString();
        }

        // ================================================================== 4. auto-detect serial parameters

        void BuildAutoDetectPage()
        {
            TabPage p = AddToolPage("Auto-detect (serial)");
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 190;

            GroupBox gb = UI.AddGroup(top, "Baud rates", 6, 4, 150, 178);
            int[] bauds = new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
            for (int i = 0; i < bauds.Length; i++)
            {
                CheckBox c = UI.AddCheck(gb, bauds[i].ToString(), 10, 20 + i * 19, 120, i < 6);
                c.Tag = bauds[i];
                chkAutoBaud.Add(c);
            }
            GroupBox gp = UI.AddGroup(top, "Parity", 162, 4, 100, 80);
            foreach (string s in new string[] { "None", "Even", "Odd" })
            {
                CheckBox c = UI.AddCheck(gp, s, 10, 20 + chkAutoParity.Count * 19, 80, true);
                c.Tag = s;
                chkAutoParity.Add(c);
            }
            GroupBox gs = UI.AddGroup(top, "Stop bits", 162, 90, 100, 60);
            foreach (string s in new string[] { "1", "2" })
            {
                CheckBox c = UI.AddCheck(gs, s, 10, 20 + chkAutoStop.Count * 19, 80, true);
                c.Tag = s;
                chkAutoStop.Add(c);
            }

            Grid g = new Grid(top, 276, 8, 150, 80);
            numAutoIdFrom = g.Add("Slave ID from", UI.NewNum(0, 255, 1, 0));
            numAutoIdTo = g.Add("Slave ID to", UI.NewNum(0, 255, 1, 0));
            numAutoTimeout = g.Add("Timeout [ms]", UI.NewNum(30, 5000, 300, 0));
            numAutoAddr = g.Add("Register address to read", UI.NewNum(0, 65535, 2000, 0));
            ToolStart(top, "Start search", 276, 124, 110, delegate { AutoDetectTool(); });
            ToolStop(top, "Stop", 392, 124, 70);
            UI.AddButton(top, "Apply selected result", 468, 124, 150, 27, delegate { ApplyAutoResult(); });
            lblAutoProg = UI.AddLabel(top, "", 276, 156, 600);
            UI.AddLabel(top, "Uses the port selected in the Connection tab; disconnect first. Reads register 2001 (Modbus ID) - read only, harmless.", 276, 174, 700);

            lvAuto = UI.NewList(p, DockStyle.Fill, new string[] { "Baud", "Parity", "Stop bits", "Slave ID", "Answer" }, new int[] { 80, 80, 80, 80, 500 });
            p.Controls.Add(top);
            lvAuto.BringToFront();
        }

        void AutoDetectTool()
        {
            if (cmbTransport.Text != "Serial RTU") { Warn("Auto-detect works with transport \"Serial RTU\" only."); return; }
            if (client != null && client.IsOpen) { Warn("Disconnect first - the serial port must be free for the search."); return; }
            List<int> bauds = new List<int>();
            foreach (CheckBox c in chkAutoBaud) if (c.Checked) bauds.Add((int)c.Tag);
            List<string> pars = new List<string>();
            foreach (CheckBox c in chkAutoParity) if (c.Checked) pars.Add((string)c.Tag);
            List<string> stops = new List<string>();
            foreach (CheckBox c in chkAutoStop) if (c.Checked) stops.Add((string)c.Tag);
            if (bauds.Count == 0 || pars.Count == 0 || stops.Count == 0) { Warn("Select at least one baud rate, parity and stop bit setting."); return; }
            int idFrom = (int)numAutoIdFrom.Value, idTo = (int)numAutoIdTo.Value, timeout = (int)numAutoTimeout.Value, addr = (int)numAutoAddr.Value;
            if (idTo < idFrom) { Warn("Slave ID to must not be smaller than Slave ID from."); return; }
            SerialSettings baseSettings = ReadSerialSettings();
            byte fc = ReadFc;
            int gapMs = (int)numGap.Value, offset = (int)numOffset.Value;
            bool stripEcho = chkEcho.Checked;
            lvAuto.Items.Clear();

            RunTool(false, delegate(ModbusClient unused)
            {
                int total = bauds.Count * pars.Count * stops.Count * (idTo - idFrom + 1), n = 0, foundCount = 0;
                foreach (int baud in bauds)
                foreach (string par in pars)
                foreach (string stop in stops)
                {
                    if (toolCancel) break;
                    SerialSettings s = new SerialSettings();
                    s.PortName = baseSettings.PortName;
                    s.BaudRate = baud;
                    s.DataBits = 8;
                    s.Parity = ParityFromText(par);
                    s.StopBits = stop == "2" ? StopBits.Two : StopBits.One;
                    s.DtrEnable = baseSettings.DtrEnable;
                    s.RtsStatic = baseSettings.RtsStatic;
                    s.RtsToggle = baseSettings.RtsToggle;
                    s.RtsActiveHigh = baseSettings.RtsActiveHigh;
                    s.RtsPreDelayMs = baseSettings.RtsPreDelayMs;
                    s.RtsPostDelayMs = baseSettings.RtsPostDelayMs;
                    ModbusClient c = new ModbusClient(new SerialChannel(s), Framing.Rtu);
                    c.ResponseTimeoutMs = timeout;
                    c.Retries = 0;
                    c.InterFrameDelayMs = gapMs;
                    c.AddressOffset = offset;
                    c.StripEcho = stripEcho;
                    c.CancelCheck = delegate { return toolCancel; };
                    try
                    {
                        c.Open();
                    }
                    catch (ModbusException ex)
                    {
                        ErrorLogger.Log(ex, "Auto-detect");
                        autoProgress = "cannot open port: " + ex.Message;
                        return;
                    }
                    try
                    {
                        for (int id = idFrom; id <= idTo && !toolCancel; id++)
                        {
                            n++;
                            autoProgress = "trying " + baud + " baud, " + par + " parity, " + stop + " stop bit(s), slave " + id + "   (" + n + " / " + total + ")   found: " + foundCount;
                            try
                            {
                                byte[] d = c.ReadRegisters((byte)id, fc, addr, 1);
                                foundCount++;
                                AddAutoResult(baud, par, stop, id, "answered: register " + addr + " = " + ModbusCodec.ToUInt16(d, 0));
                            }
                            catch (ModbusException ex)
                            {
                                if (ex.Code == ErrCodes.Cancelled) break;
                                if (ex.IsModbusException)
                                {
                                    foundCount++;
                                    AddAutoResult(baud, par, stop, id, "answered with Modbus exception " + ex.ExceptionCode + " (" + ModbusCodec.ExceptionText(ex.ExceptionCode) + ")");
                                }
                                // timeouts / CRC errors are the normal "no" answer here and are not written to the error log
                            }
                        }
                    }
                    finally
                    {
                        c.Close();
                    }
                }
                autoProgress = toolCancel ? "stopped - " + foundCount + " combination(s) answered" : "finished - " + foundCount + " combination(s) answered";
            });
        }

        void AddAutoResult(int baud, string par, string stop, int id, string text)
        {
            Ui(delegate
            {
                ListViewItem it = new ListViewItem(baud.ToString());
                it.SubItems.Add(par);
                it.SubItems.Add(stop);
                it.SubItems.Add(id.ToString());
                it.SubItems.Add(text);
                lvAuto.Items.Add(it);
            });
        }

        void ApplyAutoResult()
        {
            if (lvAuto.SelectedItems.Count == 0) { Warn("Select a result row first."); return; }
            ListViewItem it = lvAuto.SelectedItems[0];
            cmbBaud.Text = it.SubItems[0].Text;
            cmbParity.Text = it.SubItems[1].Text;
            cmbStop.Text = it.SubItems[2].Text;
            numUnit.Value = Math.Max(numUnit.Minimum, Math.Min(numUnit.Maximum, int.Parse(it.SubItems[3].Text)));
            SetStatus("Connection settings updated from the auto-detect result");
        }

        // ================================================================== 5. stress / stability test

        void BuildStressPage()
        {
            TabPage p = AddToolPage("Stress / stability test");
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 124;
            Grid g = new Grid(top, 8, 6, 200, 250);
            string[] names = new string[Va520Map.Values.Count];
            for (int i = 0; i < names.Length; i++)
            {
                RegDef d = Va520Map.Values[i];
                names[i] = d.Name + " [" + d.Unit + "]  reg " + d.ModbusRegister;
            }
            cmbStressValue = g.Add("Value to read", UI.NewCombo(false, names));
            numStressCount = g.Add("Number of requests (0 = until Stop)", UI.NewNum(0, 100000000, 500, 0));
            numStressDelay = g.Add("Pause between requests [ms]", UI.NewNum(0, 600000, 0, 0));
            ToolStart(top, "Start test", 470, 6, 100, delegate { StressTool(); });
            ToolStop(top, "Stop", 576, 6, 70);
            UI.AddButton(top, "Save report", 652, 6, 100, 27, delegate { SaveReport(); });
            lblStressProg = UI.AddLabel(top, "", 470, 40, 520);
            UI.AddLabel(top, "Reads one value as fast as possible (or with the pause above), counts every error type and measures response times.", 8, 92, 900);
            txtStressOut = UI.AddOutput(p, DockStyle.Fill);
            p.Controls.Add(top);
            txtStressOut.BringToFront();
        }

        void StressTool()
        {
            RegDef def = Va520Map.Values[Math.Max(0, cmbStressValue.SelectedIndex)];
            int count = (int)numStressCount.Value, delay = (int)numStressDelay.Value;
            byte unit = Unit, fc = ReadFc;
            string setup = DescribeSetup();
            txtStressOut.Text = "";
            RunTool(true, delegate(ModbusClient cl)
            {
                List<double> rtt = new List<double>();
                Dictionary<int, int> errorsByCode = new Dictionary<int, int>();
                long ok = 0, bad = 0, logged = 0;
                double mean = 0, m2 = 0, vMin = double.MaxValue, vMax = double.MinValue;
                long vn = 0;
                DateTime started = DateTime.Now;
                Stopwatch sw = Stopwatch.StartNew();
                long lastUi = 0;
                for (long i = 0; (count == 0 || i < count) && !toolCancel; i++)
                {
                    Stopwatch one = Stopwatch.StartNew();
                    try
                    {
                        byte[] d = cl.ReadRegisters(unit, fc, def.Address, def.RegCount);
                        one.Stop();
                        ok++;
                        rtt.Add(one.Elapsed.TotalMilliseconds);
                        double v = ModbusCodec.Decode(def.Type, d, 0, cl.Order);
                        if (!double.IsNaN(v) && !double.IsInfinity(v))
                        {
                            vn++;
                            double delta = v - mean;
                            mean += delta / vn;
                            m2 += delta * (v - mean);
                            if (v < vMin) vMin = v;
                            if (v > vMax) vMax = v;
                        }
                    }
                    catch (ModbusException ex)
                    {
                        if (ex.Code == ErrCodes.Cancelled) break;
                        bad++;
                        int c;
                        errorsByCode.TryGetValue(ex.Code, out c);
                        errorsByCode[ex.Code] = c + 1;
                        if (logged < 1000) { ErrorLogger.Log(ex, "Stress test " + def.Name); logged++; }
                        if (ex.IsLinkError) break;
                    }
                    if (sw.ElapsedMilliseconds - lastUi > 200)
                    {
                        lastUi = sw.ElapsedMilliseconds;
                        stressProgress = "requests " + (ok + bad) + (count > 0 ? " / " + count : "") + "   ok " + ok + "   errors " + bad
                            + "   " + ((ok + bad) / Math.Max(0.001, sw.Elapsed.TotalSeconds)).ToString("0.0") + " req/s";
                    }
                    for (int w = 0; w < delay && !toolCancel; w += 20) Thread.Sleep(Math.Min(20, delay - w));
                }
                sw.Stop();

                // ---- report
                StringBuilder r = new StringBuilder();
                r.AppendLine("VA 520 stress / stability test report");
                r.AppendLine("Started : " + started.ToString("yyyy-MM-dd HH:mm:ss"));
                r.AppendLine("Duration: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                r.AppendLine("Setup   : " + setup);
                r.AppendLine("Value   : " + def.Name + " [" + def.Unit + "], register " + def.ModbusRegister + " (address " + def.Address + "), " + def.Type);
                r.AppendLine();
                long total = ok + bad;
                r.AppendLine("Requests          : " + total);
                r.AppendLine("Successful        : " + ok + "  (" + (total == 0 ? 0 : 100.0 * ok / total).ToString("0.00") + " %)");
                r.AppendLine("Errors            : " + bad);
                List<int> codes = new List<int>(errorsByCode.Keys);
                codes.Sort();
                foreach (int code in codes) r.AppendLine("   E" + code + " " + ErrCodes.Describe(code) + ": " + errorsByCode[code]);
                r.AppendLine("Retries used      : " + cl.Stats.Retries);
                r.AppendLine("Throughput        : " + (total / Math.Max(0.001, sw.Elapsed.TotalSeconds)).ToString("0.0") + " requests/s");
                if (rtt.Count > 0)
                {
                    rtt.Sort();
                    double sum = 0;
                    foreach (double x in rtt) sum += x;
                    r.AppendLine("Response time [ms]: min " + rtt[0].ToString("0.0") + "   avg " + (sum / rtt.Count).ToString("0.0")
                        + "   median " + rtt[rtt.Count / 2].ToString("0.0") + "   p95 " + rtt[Math.Min(rtt.Count - 1, (int)Math.Ceiling(0.95 * rtt.Count) - 1)].ToString("0.0")
                        + "   max " + rtt[rtt.Count - 1].ToString("0.0"));
                }
                if (vn > 0)
                {
                    double sd = vn > 1 ? Math.Sqrt(m2 / (vn - 1)) : 0;
                    r.AppendLine("Value statistics : min " + vMin.ToString("0.####", CultureInfo.InvariantCulture) + "   max " + vMax.ToString("0.####", CultureInfo.InvariantCulture)
                        + "   mean " + mean.ToString("0.####", CultureInfo.InvariantCulture) + "   std dev " + sd.ToString("0.####", CultureInfo.InvariantCulture) + "  " + def.Unit);
                }
                lastReport = r.ToString();
                Out(txtStressOut, lastReport);
                if (logged >= 1000) Out(txtStressOut, "(only the first 1000 errors were written to the error log)");
                stressProgress = toolCancel ? "stopped" : "finished";
            });
        }

        string DescribeSetup()
        {
            string t = cmbTransport.Text;
            string s = t + ", ";
            if (t == "Serial RTU") s += cmbPort.Text + " " + cmbBaud.Text + " 8" + cmbParity.Text.Substring(0, 1) + cmbStop.Text;
            else s += txtHost.Text + ":" + numTcpPort.Value;
            s += ", slave " + numUnit.Value + ", FC" + ReadFc.ToString("00") + ", order " + SelectedOrder() + ", timeout " + numTimeout.Value + " ms, retries " + numRetries.Value
                + ", gap " + numGap.Value + " ms";
            return s;
        }

        void SaveReport()
        {
            if (lastReport.Length == 0) { Warn("Run a test first."); return; }
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(ErrorLogger.FilePath), "TestReport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, lastReport, new UTF8Encoding(false));
                SetStatus("Report saved: " + path);
                Info("Report saved:\r\n" + path);
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.General, "Cannot save report: " + ex.Message);
                Warn("Cannot save the report: " + ex.Message);
            }
        }

        // ================================================================== 6. built-in simulator

        void BuildSimulatorPage()
        {
            TabPage p = AddToolPage("Simulator");
            Grid g = new Grid(p, 8, 8, 200, 190);
            numSimPort = g.Add("TCP port (127.0.0.1)", UI.NewNum(1024, 65535, 5020, 0));
            cmbSimFraming = g.Add("Framing", UI.NewCombo(false, "Modbus TCP (MBAP)", "RTU over TCP"));
            numSimUnit = g.Add("Slave ID", UI.NewNum(1, 247, 1, 0));
            cmbSimOrder = g.Add("Word order", UI.NewCombo(false, "ABCD (big endian)", "CDAB (middle endian)"));
            numSimFlow = g.Add("Base flow [m3/h]", UI.NewNum(0, 100000, 40, 1));
            numSimDrop = g.Add("Drop answers [%]", UI.NewNum(0, 100, 0, 0));
            numSimCrc = g.Add("Corrupt CRC [%] (RTU only)", UI.NewNum(0, 100, 0, 0));
            numSimDelay = g.Add("Answer delay [ms]", UI.NewNum(0, 5000, 0, 0));
            chkSimEcho = g.AddWide(UI.NewCheck("Echo every request first (RTU over TCP only)", false));
            chkSimEcho.CheckedChanged += delegate { PushSimSettings(); };
            binder.Bind("simPort", numSimPort);
            binder.Bind("simFraming", cmbSimFraming);
            binder.Bind("simUnit", numSimUnit);
            binder.Bind("simFlow", numSimFlow);
            numSimDrop.ValueChanged += delegate { PushSimSettings(); };
            numSimCrc.ValueChanged += delegate { PushSimSettings(); };
            numSimDelay.ValueChanged += delegate { PushSimSettings(); };
            numSimFlow.ValueChanged += delegate { PushSimSettings(); };

            btnSimStart = UI.AddButton(p, "Start simulator", 8, g.Y + 6, 130, 28, delegate { StartSimulator(); });
            btnSimStop = UI.AddButton(p, "Stop simulator", 144, g.Y + 6, 120, 28, delegate { StopSimulator(); UpdateButtons(); });
            btnSimStop.Enabled = false;
            UI.AddButton(p, "Point connection at simulator", 270, g.Y + 6, 200, 28, delegate { PointAtSimulator(); });
            lblSimStatus = UI.AddLabel(p, "Simulator stopped", 8, g.Y + 42, 700);

            Label info = UI.AddLabel(p, "", 8, g.Y + 70, 760);
            info.Height = 90;
            info.Text = "A VA 520 look-alike (registers 1101..1427 and 2001..2005 as in the manual) for trying out this program without the sensor.\r\n"
                      + "Start it, press \"Point connection at simulator\", then Connect and Start polling. With \"Drop answers\" and \"Corrupt CRC\" you can\r\n"
                      + "force timeouts and CRC errors to see how they appear in the statistics and in the error log file.\r\n"
                      + "It listens on 127.0.0.1 only (not reachable from the network).";
        }

        void PushSimSettings()
        {
            Va520Simulator s = sim;
            if (s == null) return;
            s.DropPercent = (int)numSimDrop.Value;
            s.BadCrcPercent = (int)numSimCrc.Value;
            s.DelayMs = (int)numSimDelay.Value;
            s.EchoRequest = chkSimEcho.Checked;
            s.BaseFlow = (double)numSimFlow.Value;
        }

        void StartSimulator()
        {
            try
            {
                if (sim != null && sim.IsRunning) return;
                Va520Simulator s = new Va520Simulator();
                s.Port = (int)numSimPort.Value;
                s.Framing = cmbSimFraming.SelectedIndex == 1 ? Framing.Rtu : Framing.Mbap;
                s.UnitId = (byte)numSimUnit.Value;
                s.Order = cmbSimOrder.SelectedIndex == 1 ? WordOrder.CDAB : WordOrder.ABCD;
                sim = s;
                PushSimSettings();
                s.Start();
                SetStatus("Simulator started on 127.0.0.1:" + s.Port);
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Simulator, "Cannot start simulator: " + ex.Message);
                sim = null;
                Warn("Cannot start the simulator: " + ex.Message);
            }
            UpdateButtons();
        }

        void StopSimulator()
        {
            Va520Simulator s = sim;
            if (s != null) { try { s.Stop(); } catch { } }
            if (lblSimStatus != null) lblSimStatus.Text = "Simulator stopped";
        }

        void PointAtSimulator()
        {
            if (client != null && client.IsOpen) { Warn("Disconnect first."); return; }
            cmbTransport.Text = cmbSimFraming.SelectedIndex == 1 ? "RTU over TCP" : "Modbus TCP";
            txtHost.Text = "127.0.0.1";
            numTcpPort.Value = numSimPort.Value;
            numUnit.Value = numSimUnit.Value;
            cmbOrder.SelectedIndex = cmbSimOrder.SelectedIndex == 1 ? 1 : 0;
            SetStatus("Connection settings point to the simulator - press Connect");
        }
    }
}
