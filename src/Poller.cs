using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VA520Tester
{
    public struct TrendPoint
    {
        public DateTime Time;
        public double Value;
        public TrendPoint(DateTime t, double v) { Time = t; Value = v; }
    }

    /// <summary>A value that is polled and displayed (one row of the Monitor list).</summary>
    public class ValueSlot
    {
        public readonly object Sync = new object();
        public RegDef Def;
        public bool Enabled;

        public double Value = double.NaN;
        public bool Valid;
        public double Min = double.NaN, Max = double.NaN, Sum;
        public long Samples, Errors;
        public DateTime Updated = DateTime.MinValue;
        public string Status = "";
        public readonly List<TrendPoint> History = new List<TrendPoint>();
        public const int MaxHistory = 3600;

        // limit check
        public bool LimitEnabled;
        public double LimitMin, LimitMax;
        public bool OutOfLimit;

        public double Avg { get { return Samples == 0 ? double.NaN : Sum / Samples; } }

        public void ResetStats()
        {
            lock (Sync)
            {
                Min = Max = double.NaN; Sum = 0; Samples = 0; Errors = 0;
                History.Clear();
                OutOfLimit = false;
            }
        }

        public void SetValue(double v)
        {
            lock (Sync)
            {
                Value = v; Valid = true; Status = "OK"; Updated = DateTime.Now;
                if (double.IsNaN(Min) || v < Min) Min = v;
                if (double.IsNaN(Max) || v > Max) Max = v;
                Sum += v; Samples++;
                History.Add(new TrendPoint(Updated, v));
                if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
            }
        }

        public void SetError(string status)
        {
            lock (Sync)
            {
                Valid = false; Status = status; Errors++; Updated = DateTime.Now;
            }
        }

        public TrendPoint[] CopyHistory()
        {
            lock (Sync) { return History.ToArray(); }
        }
    }

    public class PollSettings
    {
        public byte Unit = 1;
        public byte Function = 3;
        public int IntervalMs = 1000;
        public bool SingleShot;
        public bool AutoReconnect;
        /// <summary>
        /// false (default): every ticked value is requested in every cycle, even after a timeout.
        /// true: after a timeout the rest of that cycle is skipped (faster cycles when the sensor is silent).
        /// </summary>
        public bool SkipAfterTimeout;
        public bool CsvLog;
        public string CsvFolder;
    }

    /// <summary>Background thread that reads the selected values cyclically.</summary>
    public class Poller
    {
        readonly ModbusClient client;
        readonly List<ValueSlot> slots;
        readonly PollSettings cfg;
        Thread thread;
        volatile bool stop;
        StreamWriter csv;
        string csvPath = "";
        bool csvHeaderWritten;

        public long Cycles;
        public double LastCycleMs;
        public string CsvPath { get { return csvPath; } }
        public volatile string LinkState = "";

        /// <summary>Raised (from the worker thread) when the poll loop ends by itself (single shot, or fatal link error).</summary>
        public event Action<string> Finished;

        public Poller(ModbusClient client, List<ValueSlot> slots, PollSettings cfg)
        {
            this.client = client;
            this.slots = slots;
            this.cfg = cfg;
        }

        public bool IsRunning { get { return thread != null && thread.IsAlive; } }

        public void Start()
        {
            if (IsRunning) return;
            stop = false;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "VA520 poller";
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            Thread t = thread;
            if (t != null && t.IsAlive && Thread.CurrentThread != t)
            {
                if (!t.Join(client.ResponseTimeoutMs * (client.Retries + 1) + 2000))
                {
                    try { t.Abort(); } catch { }
                }
            }
            thread = null;
            CloseCsv();
        }

        void Run()
        {
            string endReason = "stopped";
            client.CancelCheck = delegate { return stop; };
            try
            {
                while (!stop)
                {
                    Stopwatch cycle = Stopwatch.StartNew();
                    bool linkLost = PollOnce();
                    cycle.Stop();
                    LastCycleMs = cycle.Elapsed.TotalMilliseconds;
                    Cycles++;
                    WriteCsvRow();

                    if (cfg.SingleShot) { endReason = "single read finished"; break; }

                    if (linkLost)
                    {
                        if (!cfg.AutoReconnect) { endReason = "link lost - polling stopped"; break; }
                        if (!Reconnect()) continue;
                    }

                    int remaining = cfg.IntervalMs - (int)cycle.ElapsedMilliseconds;
                    while (remaining > 0 && !stop)
                    {
                        Thread.Sleep(Math.Min(remaining, 20));
                        remaining -= 20;
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ex, "Poller crashed");
                endReason = "poller crashed: " + ex.Message;
            }
            finally
            {
                client.CancelCheck = null;
                CloseCsv();
            }
            Action<string> h = Finished;
            if (h != null) { try { h(endReason); } catch { } }
        }

        /// <summary>Returns true when the link itself is broken.</summary>
        bool PollOnce()
        {
            bool skipRest = false;
            foreach (ValueSlot s in slots)
            {
                if (stop) break;
                if (!s.Enabled) continue;
                if (skipRest) { s.SetError("skipped"); continue; }
                RegDef d = s.Def;
                try
                {
                    byte[] data = client.ReadRegisters(cfg.Unit, cfg.Function, d.Address, d.RegCount);
                    double v = ModbusCodec.Decode(d.Type, data, 0, client.Order);
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        s.SetError("invalid (NaN/Inf)");
                        ErrorLogger.Log(ErrCodes.InvalidValue, d.Name + " [" + d.Unit + "] reg " + d.ModbusRegister + ": value is " + v.ToString(CultureInfo.InvariantCulture) + " (raw " + ModbusCodec.Hex(data) + ")");
                        continue;
                    }
                    s.SetValue(v);
                    CheckLimit(s, v);
                }
                catch (ModbusException ex)
                {
                    if (ex.Code == ErrCodes.Cancelled) break;
                    s.SetError(ex.Code == ErrCodes.Timeout ? "timeout" : "E" + ex.Code);
                    ErrorLogger.Log(ex, d.Name + " [" + d.Unit + "] addr " + d.Address);
                    if (ex.Code == ErrCodes.Timeout && cfg.SkipAfterTimeout) skipRest = true;   // optional: don't wait N timeouts per cycle
                    if (ex.IsLinkError) return true;
                    if (ex.Code == ErrCodes.PortIo) return true;
                }
                catch (Exception ex)
                {
                    s.SetError("error");
                    ErrorLogger.Log(ErrCodes.Unhandled, d.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            return false;
        }

        void CheckLimit(ValueSlot s, double v)
        {
            if (!s.LimitEnabled) return;
            bool outside = v < s.LimitMin || v > s.LimitMax;
            if (outside && !s.OutOfLimit)
            {
                ErrorLogger.Log(ErrCodes.OutOfLimit, s.Def.Name + " = " + v.ToString("0.###", CultureInfo.InvariantCulture) + " " + s.Def.Unit
                    + " outside limits [" + s.LimitMin.ToString(CultureInfo.InvariantCulture) + " .. " + s.LimitMax.ToString(CultureInfo.InvariantCulture) + "]");
            }
            s.OutOfLimit = outside;
        }

        bool Reconnect()
        {
            LinkState = "reconnecting...";
            try { client.Close(); } catch { }
            for (int i = 0; i < 30 && !stop; i++) Thread.Sleep(100);   // wait 3 s
            if (stop) return false;
            try
            {
                client.Open();
                LinkState = "reconnected";
                return true;
            }
            catch (ModbusException ex)
            {
                ErrorLogger.Log(ex, "Reconnect");
                return false;
            }
        }

        // ------------------------------------------------------------------ CSV

        void WriteCsvRow()
        {
            if (!cfg.CsvLog) return;
            try
            {
                if (csv == null)
                {
                    string folder = string.IsNullOrEmpty(cfg.CsvFolder) ? AppDomain.CurrentDomain.BaseDirectory : cfg.CsvFolder;
                    csvPath = Path.Combine(folder, "readings_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                    csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
                    csvHeaderWritten = false;
                }
                CultureInfo ci = CultureInfo.InvariantCulture;
                if (!csvHeaderWritten)
                {
                    StringBuilder h = new StringBuilder("time");
                    foreach (ValueSlot s in slots) if (s.Enabled) h.Append(',').Append(s.Def.Name.Replace(',', ' ')).Append(" [").Append(s.Def.Unit).Append(']');
                    csv.WriteLine(h.ToString());
                    csvHeaderWritten = true;
                }
                StringBuilder row = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                foreach (ValueSlot s in slots)
                {
                    if (!s.Enabled) continue;
                    row.Append(',');
                    if (s.Valid) row.Append(s.Value.ToString("R", ci));
                }
                csv.WriteLine(row.ToString());
                csv.Flush();
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ErrCodes.CsvLog, "CSV logging stopped: " + ex.Message);
                cfg.CsvLog = false;
                CloseCsv();
            }
        }

        void CloseCsv()
        {
            StreamWriter w = csv;
            csv = null;
            if (w != null) { try { w.Close(); } catch { } }
        }
    }
}
