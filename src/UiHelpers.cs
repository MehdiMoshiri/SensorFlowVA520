using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VA520Tester
{
    /// <summary>Tiny helpers to build the forms in code (no designer files, so it behaves the same on Windows and Mono).</summary>
    public static class UI
    {
        public static readonly Font Mono = new Font(FontFamily.GenericMonospace, 9f);

        public static Label AddLabel(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.SetBounds(x, y, w, 18);
            l.TextAlign = ContentAlignment.MiddleLeft;
            parent.Controls.Add(l);
            return l;
        }

        public static GroupBox AddGroup(Control parent, string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.SetBounds(x, y, w, h);
            parent.Controls.Add(g);
            return g;
        }

        public static Button AddButton(Control parent, string text, int x, int y, int w, int h, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.UseVisualStyleBackColor = true;
            if (click != null) b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        public static CheckBox AddCheck(Control parent, string text, int x, int y, int w, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            c.SetBounds(x, y, w, 20);
            c.Checked = value;
            parent.Controls.Add(c);
            return c;
        }

        public static CheckBox NewCheck(string text, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            c.Height = 20;
            c.Checked = value;
            return c;
        }

        public static NumericUpDown NewNum(decimal min, decimal max, decimal value, int decimals)
        {
            NumericUpDown n = new NumericUpDown();
            n.DecimalPlaces = decimals;
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Height = 22;
            return n;
        }

        public static ComboBox NewCombo(bool editable, params string[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            if (items.Length > 0 && !editable) c.SelectedIndex = 0;
            if (items.Length > 0 && editable) c.Text = items[0];
            return c;
        }

        public static TextBox NewText(string text)
        {
            TextBox t = new TextBox();
            t.Text = text;
            return t;
        }

        public static TextBox AddText(Control parent, int x, int y, int w, string text)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.SetBounds(x, y, w, 22);
            parent.Controls.Add(t);
            return t;
        }

        public static TextBox AddOutput(Control parent, DockStyle dock)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = false;
            t.Font = Mono;
            t.BackColor = Color.White;
            t.Dock = dock;
            parent.Controls.Add(t);
            return t;
        }

        /// <summary>Appends a line and keeps the text box below maxChars characters.</summary>
        public static void Append(TextBox tb, string line, int maxChars)
        {
            if (tb.IsDisposed) return;
            string cur = tb.Text;
            if (cur.Length > maxChars)
            {
                int cut = cur.IndexOf('\n', cur.Length - maxChars / 2);
                cur = cut >= 0 ? cur.Substring(cut + 1) : "";
            }
            tb.Text = cur + line + "\r\n";
            tb.SelectionStart = tb.Text.Length;
            tb.ScrollToCaret();
        }

        public static ListView NewList(Control parent, DockStyle dock, string[] cols, int[] widths)
        {
            ListView lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.Dock = dock;
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            parent.Controls.Add(lv);
            return lv;
        }

        public static string F(double v, string fmt)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "-";
            return v.ToString(fmt, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Places "label + control" rows below each other.</summary>
    public class Grid
    {
        readonly Control parent;
        readonly int x;
        readonly int labelW;
        readonly int ctrlW;
        readonly int rowH;
        public int Y;

        public Grid(Control parent, int x, int y, int labelW, int ctrlW)
        {
            this.parent = parent;
            this.x = x;
            Y = y;
            this.labelW = labelW;
            this.ctrlW = ctrlW;
            rowH = 27;
        }

        public T Add<T>(string label, T c) where T : Control
        {
            UI.AddLabel(parent, label, x, Y + 2, labelW);
            c.Left = x + labelW;
            c.Top = Y;
            c.Width = ctrlW;
            parent.Controls.Add(c);
            Y += rowH;
            return c;
        }

        /// <summary>Control spanning the label and the control column (check boxes).</summary>
        public T AddWide<T>(T c) where T : Control
        {
            c.Left = x;
            c.Top = Y;
            c.Width = labelW + ctrlW;
            parent.Controls.Add(c);
            Y += rowH;
            return c;
        }

        public void Gap(int h) { Y += h; }
    }

    /// <summary>Connects controls to keys of the settings file (plain "key=value" text file, no database).</summary>
    public class SettingsBinder
    {
        readonly List<KeyValuePair<string, Control>> items = new List<KeyValuePair<string, Control>>();

        public T Bind<T>(string key, T c) where T : Control
        {
            items.Add(new KeyValuePair<string, Control>(key, c));
            return c;
        }

        public Dictionary<string, string> Collect()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, Control> kv in items)
            {
                Control c = kv.Value;
                string v = "";
                NumericUpDown n = c as NumericUpDown;
                CheckBox cb = c as CheckBox;
                if (n != null) v = n.Value.ToString(CultureInfo.InvariantCulture);
                else if (cb != null) v = cb.Checked ? "1" : "0";
                else v = c.Text;
                d[kv.Key] = v;
            }
            return d;
        }

        public void Apply(Dictionary<string, string> d)
        {
            foreach (KeyValuePair<string, Control> kv in items)
            {
                string v;
                if (!d.TryGetValue(kv.Key, out v)) continue;
                SetValue(kv.Value, v);
            }
        }

        public static void SetValue(Control c, string v)
        {
            try
            {
                NumericUpDown n = c as NumericUpDown;
                CheckBox cb = c as CheckBox;
                ComboBox combo = c as ComboBox;
                if (n != null)
                {
                    decimal dv;
                    if (decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                        n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, dv));
                }
                else if (cb != null) cb.Checked = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (combo != null)
                {
                    int idx = combo.Items.IndexOf(v);
                    if (idx >= 0) combo.SelectedIndex = idx;
                    else if (combo.DropDownStyle == ComboBoxStyle.DropDown) combo.Text = v;
                }
                else c.Text = v;
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.Settings, "Cannot apply setting value '" + v + "': " + ex.Message);
            }
        }

        public static Dictionary<string, string> LoadFile(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (!File.Exists(path)) return d;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
            return d;
        }

        public static void SaveFile(string path, Dictionary<string, string> d)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# VA 520 tester settings");
            List<string> keys = new List<string>(d.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys) sb.Append(k).Append('=').AppendLine(d[k].Replace("\r", " ").Replace("\n", " "));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
