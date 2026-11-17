using System;
using System.Drawing;
using System.Windows.Forms;

namespace VA520Tester
{
    /// <summary>Simple line chart drawn with GDI+ (the System.Windows.Forms.DataVisualization chart does not exist on Mono).</summary>
    public class TrendPanel : Control
    {
        TrendPoint[] points = new TrendPoint[0];
        string title = "";
        string unit = "";
        int windowSeconds = 120;

        public TrendPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void SetData(TrendPoint[] pts, string title, string unit, int windowSeconds)
        {
            this.points = pts ?? new TrendPoint[0];
            this.title = title;
            this.unit = unit;
            this.windowSeconds = Math.Max(5, windowSeconds);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle r = ClientRectangle;
            if (r.Width < 80 || r.Height < 60) return;

            Font font = Font;
            using (Pen axis = new Pen(Color.Gray))
            using (Pen grid = new Pen(Color.Gainsboro))
            using (Pen line = new Pen(Color.RoyalBlue, 1.6f))
            using (Brush txt = new SolidBrush(Color.Black))
            {
                int left = 52, top = 22, right = 8, bottom = 22;
                Rectangle plot = new Rectangle(left, top, r.Width - left - right, r.Height - top - bottom);
                if (plot.Width < 10 || plot.Height < 10) return;

                g.DrawString(string.IsNullOrEmpty(title) ? "Trend (select a row)" : title + " [" + unit + "]", font, txt, 4, 3);

                DateTime now = DateTime.Now;
                DateTime t0 = now.AddSeconds(-windowSeconds);

                // y range
                double min = double.MaxValue, max = double.MinValue;
                int count = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i].Time < t0) continue;
                    double v = points[i].Value;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    count++;
                }
                if (count == 0) { min = 0; max = 1; }
                if (max - min < 1e-9) { min -= 0.5; max += 0.5; }
                double pad = (max - min) * 0.08;
                min -= pad; max += pad;

                // grid + labels
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Bottom - (int)(plot.Height * i / 4.0);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double val = min + (max - min) * i / 4.0;
                    g.DrawString(FormatTick(val, max - min), font, txt, 2, y - 7);
                }
                for (int i = 0; i <= 4; i++)
                {
                    int x = plot.Left + (int)(plot.Width * i / 4.0);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    int secAgo = (int)(windowSeconds * (4 - i) / 4.0);
                    string lab = secAgo == 0 ? "now" : "-" + secAgo + "s";
                    g.DrawString(lab, font, txt, x - 12, plot.Bottom + 3);
                }
                g.DrawRectangle(axis, plot);

                // data
                if (count >= 2)
                {
                    Point[] pp = new Point[count];
                    int k = 0;
                    for (int i = 0; i < points.Length; i++)
                    {
                        if (points[i].Time < t0) continue;
                        double fx = (points[i].Time - t0).TotalSeconds / windowSeconds;
                        double fy = (points[i].Value - min) / (max - min);
                        pp[k++] = new Point(plot.Left + (int)(fx * plot.Width), plot.Bottom - (int)(fy * plot.Height));
                    }
                    Region old = g.Clip;
                    g.SetClip(plot);
                    g.DrawLines(line, pp);
                    g.Clip = old;
                }
            }
        }

        static string FormatTick(double v, double range)
        {
            if (range >= 100) return v.ToString("0");
            if (range >= 10) return v.ToString("0.0");
            if (range >= 1) return v.ToString("0.00");
            return v.ToString("0.000");
        }
    }
}
