using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VA520Tester
{
    public partial class MainForm
    {
        ComboBox cmbScale;
        NumericUpDown numFs, numMa, numFlowRef, numPulseCustom, numPulseFlow, numPulseVolume, numPulseSeconds;
        ComboBox cmbPulse;
        Label lblScaleUnit, lblMaResult, lblPulseResult;
        bool calcLock;

        void BuildCalcTab()
        {
            TabPage page = AddPage("Calculators", true);

            // ---------------------------------------------------------------- 4..20 mA
            GroupBox ga = UI.AddGroup(page, "4..20 mA analogue output  (4 mA = 0, 20 mA = full scale; manual chapter 6 / 11.3.6)", 8, 8, 640, 306);
            Grid g = new Grid(ga, 10, 22, 250, 330);
            cmbScale = g.Add("Range (compressed air, 20 C / 1000 mbar)", UI.NewCombo(false, ScaleNames()));
            cmbScale.SelectedIndex = 4;
            numFs = g.Add("Full scale = 20 mA", UI.NewNum(0.001m, 100000, 45, 3));
            lblScaleUnit = UI.AddLabel(ga, "m3/h", 596, g.Y - 25, 40);
            numMa = g.Add("Measured current [mA] (multimeter)", UI.NewNum(0, 30, 12, 3));
            numFlowRef = g.Add("Flow shown by Modbus (reference)", UI.NewNum(0, 1000000, 0, 3));
            UI.AddButton(ga, "Use last Modbus flow reading", 260, g.Y, 220, 26, delegate { UseLastFlow(); });
            lblMaResult = UI.AddLabel(ga, "", 10, g.Y + 34, 620);
            lblMaResult.Height = 130;
            lblMaResult.Font = UI.Mono;

            cmbScale.SelectedIndexChanged += delegate
            {
                if (cmbScale.SelectedIndex < 0) return;
                VA520Tester.Va520Map.Scale sc = Va520Map.Scales[cmbScale.SelectedIndex];
                calcLock = true;
                numFs.Value = (decimal)sc.FullScale;
                calcLock = false;
                lblScaleUnit.Text = sc.Unit;
                RecalcMa();
            };
            numFs.ValueChanged += delegate { RecalcMa(); };
            numMa.ValueChanged += delegate { RecalcMa(); };
            numFlowRef.ValueChanged += delegate { RecalcMa(); };

            // ---------------------------------------------------------------- pulse output
            GroupBox gp = UI.AddGroup(page, "Pulse output  (galvanically isolated, max 50 Hz, output is delayed by 1 s; manual chapter 11.3.3)", 8, 322, 640, 262);
            Grid g2 = new Grid(gp, 10, 22, 250, 330);
            cmbPulse = g2.Add("Pulse value", UI.NewCombo(false, "0.1 ltr / pulse", "1 ltr / pulse", "0.1 m3 / pulse", "1 m3 / pulse", "custom (below)"));
            cmbPulse.SelectedIndex = 1;
            numPulseCustom = g2.Add("Custom pulse value [ltr / pulse]", UI.NewNum(0.001m, 1000000, 10, 3));
            numPulseFlow = g2.Add("Flow [m3/h]", UI.NewNum(0, 10000000, 40, 3));
            numPulseVolume = g2.Add("Volume to count [m3]", UI.NewNum(0, 100000000, 1, 3));
            numPulseSeconds = g2.Add("Time at the flow above [s]", UI.NewNum(0, 100000000, 60, 0));
            UI.AddButton(gp, "Use last Modbus flow reading", 260, g2.Y, 220, 26, delegate { UseLastFlowForPulse(); });
            lblPulseResult = UI.AddLabel(gp, "", 10, g2.Y + 34, 620);
            lblPulseResult.Height = 70;
            lblPulseResult.Font = UI.Mono;
            cmbPulse.SelectedIndexChanged += delegate { RecalcPulse(); };
            numPulseCustom.ValueChanged += delegate { RecalcPulse(); };
            numPulseFlow.ValueChanged += delegate { RecalcPulse(); };
            numPulseVolume.ValueChanged += delegate { RecalcPulse(); };
            numPulseSeconds.ValueChanged += delegate { RecalcPulse(); };

            RecalcMa();
            RecalcPulse();
        }

        static string[] ScaleNames()
        {
            string[] n = new string[Va520Map.Scales.Length];
            for (int i = 0; i < n.Length; i++) n[i] = Va520Map.Scales[i].ToString();
            return n;
        }

        /// <summary>Last valid Flow m3/h reading from the Monitor list.</summary>
        bool TryLastFlowM3h(out double v)
        {
            v = 0;
            foreach (ValueSlot s in slots)
            {
                if (s.Def.Key != "flow_m3h") continue;
                lock (s.Sync)
                {
                    if (s.Samples == 0 || double.IsNaN(s.Value)) return false;
                    v = s.Value;
                    return true;
                }
            }
            return false;
        }

        void UseLastFlow()
        {
            double m3h;
            if (!TryLastFlowM3h(out m3h)) { Warn("No flow reading yet. Tick \"Flow m3/h\" in the Monitor tab and start polling."); return; }
            double v = lblScaleUnit.Text == "l/min" ? m3h * 1000.0 / 60.0 : m3h;
            numFlowRef.Value = Math.Max(numFlowRef.Minimum, Math.Min(numFlowRef.Maximum, (decimal)v));
        }

        void UseLastFlowForPulse()
        {
            double m3h;
            if (!TryLastFlowM3h(out m3h)) { Warn("No flow reading yet. Tick \"Flow m3/h\" in the Monitor tab and start polling."); return; }
            numPulseFlow.Value = Math.Max(numPulseFlow.Minimum, Math.Min(numPulseFlow.Maximum, (decimal)m3h));
        }

        void RecalcMa()
        {
            if (calcLock || lblMaResult == null) return;
            CultureInfo ci = CultureInfo.InvariantCulture;
            double fs = (double)numFs.Value, ma = (double)numMa.Value, reference = (double)numFlowRef.Value;
            string unit = lblScaleUnit.Text;
            double flowFromMa = (ma - 4.0) / 16.0 * fs;
            double expectedMa = 4.0 + 16.0 * reference / fs;
            double dev = flowFromMa - reference;
            double tol = 0.015 * Math.Abs(reference) + 0.003 * fs;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Flow from the measured current : " + flowFromMa.ToString("0.000", ci) + " " + unit);
            sb.AppendLine("Current for the reference flow : " + expectedMa.ToString("0.000", ci) + " mA");
            sb.AppendLine("Deviation (mA-flow - reference) : " + dev.ToString("+0.000;-0.000;0.000", ci) + " " + unit
                + (reference != 0 ? "  (" + (dev / reference * 100.0).ToString("+0.00;-0.00;0.00", ci) + " % rdg, " + (dev / fs * 100.0).ToString("+0.00;-0.00;0.00", ci) + " % FS)" : ""));
            sb.AppendLine("Accuracy +-1.5 % rdg +-0.3 % FS   : +-" + tol.ToString("0.000", ci) + " " + unit
                + "  ->  " + (Math.Abs(dev) <= tol ? "WITHIN tolerance" : "OUTSIDE tolerance"));
            if (ma < 3.8) sb.AppendLine("Note: below 3.8 mA = NAMUR under-range / error current (2 mA setting).");
            else if (ma < 4.0) sb.AppendLine("Note: 3.8 .. 4 mA = measuring range under-range (NAMUR).");
            else if (ma > 20.5) sb.AppendLine("Note: above 20.5 mA = error current (22 mA setting).");
            else if (ma > 20.0) sb.AppendLine("Note: 20 .. 20.5 mA = measuring range exceeded (NAMUR).");
            sb.Append("Accuracy is the technical-data figure of the manual; the analogue output may add its own error.");
            lblMaResult.Text = sb.ToString();
        }

        double LtrPerPulse()
        {
            switch (cmbPulse.SelectedIndex)
            {
                case 0: return 0.1;
                case 1: return 1.0;
                case 2: return 100.0;
                case 3: return 1000.0;
                default: return (double)numPulseCustom.Value;
            }
        }

        void RecalcPulse()
        {
            if (lblPulseResult == null) return;
            CultureInfo ci = CultureInfo.InvariantCulture;
            double ltr = LtrPerPulse();
            double flow = (double)numPulseFlow.Value;
            double rate = flow * 1000.0 / 3600.0 / ltr;                 // pulses per second
            double maxFlow = 50.0 * 3600.0 * ltr / 1000.0;              // m3/h at 50 Hz
            double volume = (double)numPulseVolume.Value;
            double secs = (double)numPulseSeconds.Value;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Pulse frequency at the flow : " + rate.ToString("0.000", ci) + " Hz"
                + (rate > 50.0 ? "  ->  TOO HIGH (max 50 Hz), use a larger pulse value" : "  ->  ok (max 50 Hz)"));
            sb.AppendLine("Max flow for this pulse value: " + maxFlow.ToString("0.###", ci) + " m3/h  (table 1: 0.1 ltr = 18, 1 ltr = 180)");
            sb.AppendLine("Pulses for the volume        : " + (volume * 1000.0 / ltr).ToString("0.##", ci));
            sb.Append("Pulses in the given time     : " + (rate * secs).ToString("0.##", ci));
            lblPulseResult.Text = sb.ToString();
        }
    }
}
