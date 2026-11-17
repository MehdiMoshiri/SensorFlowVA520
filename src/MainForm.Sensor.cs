using System;
using System.Drawing;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        readonly Label[] lblSensCur = new Label[5];
        NumericUpDown numSensId;
        ComboBox cmbSensBaud, cmbSensParity, cmbSensStop, cmbSensOrder;
        TextBox txtSensorOut;

        void BuildSensorTab()
        {
            TabPage page = AddPage("Sensor settings", true);

            GroupBox g = UI.AddGroup(page, "Modbus communication settings stored in the sensor (registers 2001..2005, function 03 read / 16 write)", 8, 8, 760, 236);
            UI.AddLabel(g, "Parameter", 10, 22, 140);
            UI.AddLabel(g, "Register (addr)", 160, 22, 100);
            UI.AddLabel(g, "Current value", 270, 22, 130);
            UI.AddLabel(g, "New value", 410, 22, 200);

            string[] names = new string[] { "Modbus ID (1..247)", "Baud rate", "Parity", "Number of stop bits", "Word order" };
            for (int i = 0; i < 5; i++)
            {
                int y = 46 + i * 30;
                int idx = i;
                UI.AddLabel(g, names[i], 10, y + 2, 145);
                UI.AddLabel(g, (2001 + i) + " (" + (2000 + i) + ")", 160, y + 2, 100);
                lblSensCur[i] = UI.AddLabel(g, "?", 270, y + 2, 130);
                lblSensCur[i].Font = new Font(Font, FontStyle.Bold);
                UI.AddButton(g, "Write", 640, y - 1, 70, 26, delegate { WriteSensorRegister(idx); });
            }
            numSensId = UI.NewNum(1, 247, 1, 0);
            numSensId.SetBounds(410, 46, 100, 22);
            g.Controls.Add(numSensId);
            cmbSensBaud = UI.NewCombo(false, "0 = 1200", "1 = 2400", "2 = 4800", "3 = 9600", "4 = 19200", "5 = 38400");
            cmbSensBaud.SelectedIndex = 4;
            cmbSensBaud.SetBounds(410, 76, 200, 22);
            g.Controls.Add(cmbSensBaud);
            cmbSensParity = UI.NewCombo(false, Va520Map.ParityNames);
            cmbSensParity.SelectedIndex = 1;
            cmbSensParity.SetBounds(410, 106, 200, 22);
            g.Controls.Add(cmbSensParity);
            cmbSensStop = UI.NewCombo(false, Va520Map.StopNames);
            cmbSensStop.SetBounds(410, 136, 200, 22);
            g.Controls.Add(cmbSensStop);
            cmbSensOrder = UI.NewCombo(false, "0xABCD = Big endian", "0xCDAB = Middle endian");
            cmbSensOrder.SetBounds(410, 166, 200, 22);
            g.Controls.Add(cmbSensOrder);

            UI.AddButton(g, "Read current values", 10, 198, 150, 28, delegate { ReadSensorSettings(); });
            UI.AddButton(g, "Write all 5 (one request)", 170, 198, 180, 28, delegate { WriteSensorAll(); });

            Label warn = UI.AddLabel(page, "", 8, 252, 760);
            warn.Height = 92;
            warn.ForeColor = Color.Firebrick;
            warn.Text = "Warning: changing ID, baud rate, parity or stop bits changes how the sensor talks - the connection may stop working until you\r\n"
                      + "use the same settings here (the program offers to do that for you after a successful write).\r\n"
                      + "The manual does not say whether new values are active immediately or only after they are saved / the sensor is restarted\r\n"
                      + "(the sensor display has a \"Save\" button in its Modbus menu) - this tab lets you find out. Factory defaults: ID 1, 19200, even, 1 stop bit, ABCD.\r\n"
                      + "To set a sensor back: Settings > Modbus Setup > \"Set to Default\" on the sensor display.";

            txtSensorOut = new TextBox();
            txtSensorOut.Multiline = true;
            txtSensorOut.ReadOnly = true;
            txtSensorOut.ScrollBars = ScrollBars.Both;
            txtSensorOut.Font = UI.Mono;
            txtSensorOut.BackColor = Color.White;
            txtSensorOut.SetBounds(8, 350, 760, 170);
            page.Controls.Add(txtSensorOut);
        }

        void SensorLog(string line)
        {
            string s = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            Ui(delegate { UI.Append(txtSensorOut, s, 60000); });
        }

        ushort SensorNewValue(int idx)
        {
            switch (idx)
            {
                case 0: return (ushort)numSensId.Value;
                case 1: return (ushort)cmbSensBaud.SelectedIndex;
                case 2: return (ushort)cmbSensParity.SelectedIndex;
                case 3: return (ushort)cmbSensStop.SelectedIndex;
                default: return cmbSensOrder.SelectedIndex == 1 ? Va520Map.WordOrderCDAB : Va520Map.WordOrderABCD;
            }
        }

        static string SensorValueText(int idx, ushort v)
        {
            switch (idx)
            {
                case 0: return v.ToString();
                case 1: return v < Va520Map.BaudValues.Length ? v + " = " + Va520Map.BaudValues[v] : v + " (unknown)";
                case 2: return v < Va520Map.ParityNames.Length ? Va520Map.ParityNames[v] : v + " (unknown)";
                case 3: return v < Va520Map.StopNames.Length ? Va520Map.StopNames[v] : v + " (unknown)";
                default:
                    if (v == Va520Map.WordOrderABCD) return "0xABCD = Big endian";
                    if (v == Va520Map.WordOrderCDAB) return "0xCDAB = Middle endian";
                    return "0x" + v.ToString("X4") + " (unknown)";
            }
        }

        void ReadSensorSettings()
        {
            byte unit = Unit, fc = ReadFc;
            RunTool(true, delegate(ModbusClient cl)
            {
                try
                {
                    byte[] d = cl.ReadRegisters(unit, fc, Va520Map.RegId, 5);
                    ushort[] v = new ushort[5];
                    for (int i = 0; i < 5; i++) v[i] = ModbusCodec.ToUInt16(d, i * 2);
                    Ui(delegate
                    {
                        for (int i = 0; i < 5; i++) lblSensCur[i].Text = SensorValueText(i, v[i]);
                        if (v[0] >= 1 && v[0] <= 247) numSensId.Value = v[0];
                        if (v[1] < cmbSensBaud.Items.Count) cmbSensBaud.SelectedIndex = v[1];
                        if (v[2] < cmbSensParity.Items.Count) cmbSensParity.SelectedIndex = v[2];
                        if (v[3] < cmbSensStop.Items.Count) cmbSensStop.SelectedIndex = v[3];
                        cmbSensOrder.SelectedIndex = v[4] == Va520Map.WordOrderCDAB ? 1 : 0;
                    });
                    SensorLog("Read registers 2001..2005: " + ModbusCodec.Hex(d));
                }
                catch (ModbusException ex)
                {
                    ErrorLogger.Log(ex, "Read sensor settings");
                    SensorLog("READ FAILED: E" + ex.Code + " " + ex.Message);
                }
            });
        }

        void WriteSensorRegister(int idx)
        {
            ushort value = SensorNewValue(idx);
            string[] names = new string[] { "Modbus ID", "Baud rate", "Parity", "Stop bits", "Word order" };
            if (!Confirm("Write " + names[idx] + " = " + SensorValueText(idx, value) + " to the sensor (register " + (2001 + idx) + ")?\r\n\r\n"
                       + "The sensor may stop answering with the current connection settings."))
                return;
            byte unit = Unit;
            RunTool(true, delegate(ModbusClient cl)
            {
                try
                {
                    cl.WriteRegisters(unit, Va520Map.RegId + idx, new ushort[] { value });
                    SensorLog("WROTE " + names[idx] + " = " + SensorValueText(idx, value) + " (register " + (2001 + idx) + ")");
                    Ui(delegate { OfferApplyLocal(idx, value); });
                }
                catch (ModbusException ex)
                {
                    ErrorLogger.Log(ErrCodes.WriteFailed, "Write " + names[idx] + ": E" + ex.Code + " " + ex.Message);
                    SensorLog("WRITE FAILED (" + names[idx] + "): E" + ex.Code + " " + ex.Message);
                    if (ex.Code == ErrCodes.Timeout && idx <= 3)
                        SensorLog("   (no answer - the sensor may already be using the new setting: change the connection settings and reconnect)");
                }
            });
        }

        void WriteSensorAll()
        {
            ushort[] v = new ushort[5];
            for (int i = 0; i < 5; i++) v[i] = SensorNewValue(i);
            if (!Confirm("Write all five communication registers in one request?\r\n\r\n"
                       + "ID " + v[0] + ", baud " + SensorValueText(1, v[1]) + ", parity " + SensorValueText(2, v[2])
                       + ", stop bits " + SensorValueText(3, v[3]) + ", word order " + SensorValueText(4, v[4])))
                return;
            byte unit = Unit;
            RunTool(true, delegate(ModbusClient cl)
            {
                try
                {
                    cl.WriteRegisters(unit, Va520Map.RegId, v);
                    SensorLog("WROTE registers 2001..2005 in one request");
                    Ui(delegate { OfferApplyAll(v); });
                }
                catch (ModbusException ex)
                {
                    ErrorLogger.Log(ErrCodes.WriteFailed, "Write all settings: E" + ex.Code + " " + ex.Message);
                    SensorLog("WRITE FAILED: E" + ex.Code + " " + ex.Message);
                    if (ex.Code == ErrCodes.Timeout)
                        SensorLog("   (no answer - the sensor may already be using the new settings: change the connection settings and reconnect)");
                }
            });
        }

        /// <summary>After a successful write: copy the value into the local connection settings (UI thread).</summary>
        void OfferApplyLocal(int idx, ushort value)
        {
            if (!Confirm("Use the new value in the local connection settings too" + (idx <= 3 ? " and reconnect" : "") + "?")) return;
            ApplyLocal(idx, value);
            if (idx <= 3) Reconnect();
        }

        void OfferApplyAll(ushort[] v)
        {
            if (!Confirm("Use the new values in the local connection settings too and reconnect?")) return;
            for (int i = 0; i < 5; i++) ApplyLocal(i, v[i]);
            Reconnect();
        }

        void ApplyLocal(int idx, ushort value)
        {
            switch (idx)
            {
                case 0: if (value >= 1 && value <= 247) numUnit.Value = value; break;
                case 1: if (value < Va520Map.BaudValues.Length) cmbBaud.Text = Va520Map.BaudValues[value].ToString(); break;
                case 2: cmbParity.Text = value == 1 ? "Even" : (value == 2 ? "Odd" : "None"); break;
                case 3: cmbStop.Text = value == 1 ? "2" : "1"; break;
                case 4: cmbOrder.SelectedIndex = value == Va520Map.WordOrderCDAB ? 1 : 0; break;
            }
        }

        void Reconnect()
        {
            // wait for a running tool (the write itself) to finish
            for (int i = 0; i < 20 && toolBusy; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            DoDisconnect();
            DoConnect();
        }
    }
}
