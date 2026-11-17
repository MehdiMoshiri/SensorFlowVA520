using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VA520Tester
{
    /// <summary>
    /// A small VA 520 look-alike (Modbus slave over TCP) so that the tester can be tried without the sensor:
    /// connect the tester with transport "Modbus TCP" (or "RTU over TCP") to 127.0.0.1.
    /// Optional fault injection (dropped answers, bad CRC, delay) to exercise the error log.
    /// </summary>
    public class Va520Simulator
    {
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;
        readonly List<TcpClient> clients = new List<TcpClient>();
        readonly object sync = new object();
        readonly object dataLock = new object();
        readonly Random rnd = new Random();
        readonly Dictionary<int, ushort> settings = new Dictionary<int, ushort>();
        readonly DateTime start = DateTime.Now;
        double totalM3 = 123456.0;
        DateTime lastTotalUpdate = DateTime.Now;

        public int Port = 5020;
        public Framing Framing = Framing.Mbap;
        public byte UnitId = 1;
        public WordOrder Order = WordOrder.ABCD;
        public int DropPercent;
        public int BadCrcPercent;
        public int DelayMs;
        /// <summary>RTU framing only: send every request back first (echo), then the answer.</summary>
        public bool EchoRequest;
        public double BaseFlow = 40.0;      // m3/h
        public double BaseTemp = 21.5;      // degC

        public long RequestCount;
        public long ErrorCount;

        public bool IsRunning { get { return running; } }

        public Va520Simulator()
        {
            settings[Va520Map.RegId] = 1;
            settings[Va520Map.RegBaud] = 4;
            settings[Va520Map.RegParity] = 1;
            settings[Va520Map.RegStopBits] = 0;
            settings[Va520Map.RegWordOrder] = Va520Map.WordOrderABCD;
        }

        public void Start()
        {
            if (running) return;
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "VA520 simulator accept";
            acceptThread.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (sync)
            {
                foreach (TcpClient c in clients) { try { c.Close(); } catch { } }
                clients.Clear();
            }
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    lock (sync) { clients.Add(c); }
                    Thread t = new Thread(delegate() { ClientLoop(c); });
                    t.IsBackground = true;
                    t.Start();
                }
                catch (Exception)
                {
                    if (!running) return;
                    Thread.Sleep(50);
                }
            }
        }

        void ClientLoop(TcpClient c)
        {
            byte[] buf = new byte[512];
            int n = 0;
            try
            {
                NetworkStream st = c.GetStream();
                while (running)
                {
                    if (!c.Client.Poll(100000, SelectMode.SelectRead)) continue;
                    int r = st.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                    // process every complete frame in the buffer
                    while (n > 0)
                    {
                        int len;
                        if (Framing == Framing.Mbap)
                        {
                            if (n < 7) break;
                            len = 6 + ((buf[4] << 8) | buf[5]);
                        }
                        else
                        {
                            len = ModbusCodec.ExpectedRtuRequestLength(buf, n);
                            if (len == 0) break;
                            if (len < 0) { n = 0; break; }      // unknown function: drop
                        }
                        if (len > buf.Length) { n = 0; break; }
                        if (n < len) break;
                        byte[] req = new byte[len];
                        Buffer.BlockCopy(buf, 0, req, 0, len);
                        Buffer.BlockCopy(buf, len, buf, 0, n - len);
                        n -= len;
                        if (EchoRequest && Framing == Framing.Rtu)
                        {
                            st.Write(req, 0, req.Length);       // like a converter that loops its own transmission back
                            st.Flush();
                        }
                        byte[] resp = Process(req);
                        if (resp != null)
                        {
                            if (DelayMs > 0) Thread.Sleep(DelayMs);
                            st.Write(resp, 0, resp.Length);
                            st.Flush();
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                try { c.Close(); } catch { }
                lock (sync) { clients.Remove(c); }
            }
        }

        // returns the complete answer frame or null (no answer)
        byte[] Process(byte[] req)
        {
            Interlocked.Increment(ref RequestCount);
            byte unit, fc;
            byte[] pdu;
            ushort txId = 0;
            if (Framing == Framing.Mbap)
            {
                txId = (ushort)((req[0] << 8) | req[1]);
                unit = req[6];
                pdu = new byte[req.Length - 7];
                Buffer.BlockCopy(req, 7, pdu, 0, pdu.Length);
            }
            else
            {
                ushort calc = ModbusCodec.Crc16(req, 0, req.Length - 2);
                ushort got = (ushort)(req[req.Length - 2] | (req[req.Length - 1] << 8));
                if (calc != got) return null;       // a real slave ignores frames with bad CRC
                unit = req[0];
                pdu = new byte[req.Length - 3];
                Buffer.BlockCopy(req, 1, pdu, 0, pdu.Length);
            }
            if (unit != UnitId && unit != 0) return null;
            if (pdu.Length < 1) return null;
            fc = pdu[0];

            // fault injection
            if (DropPercent > 0 && rnd.Next(100) < DropPercent) { Interlocked.Increment(ref ErrorCount); return null; }

            byte[] respPdu;
            try { lock (dataLock) { respPdu = HandlePdu(fc, pdu); } }
            catch (ModbusException ex) { respPdu = new byte[] { (byte)(fc | 0x80), (byte)ex.ExceptionCode }; }
            if (unit == 0) return null;     // broadcast

            byte[] frame;
            if (Framing == Framing.Mbap)
            {
                frame = new byte[7 + respPdu.Length];
                frame[0] = (byte)(txId >> 8); frame[1] = (byte)txId;
                frame[4] = (byte)((respPdu.Length + 1) >> 8); frame[5] = (byte)(respPdu.Length + 1);
                frame[6] = unit;
                Buffer.BlockCopy(respPdu, 0, frame, 7, respPdu.Length);
            }
            else
            {
                frame = new byte[respPdu.Length + 3];
                frame[0] = unit;
                Buffer.BlockCopy(respPdu, 0, frame, 1, respPdu.Length);
                ushort crc = ModbusCodec.Crc16(frame, 0, frame.Length - 2);
                if (BadCrcPercent > 0 && rnd.Next(100) < BadCrcPercent) { crc ^= 0x5A5A; Interlocked.Increment(ref ErrorCount); }
                frame[frame.Length - 2] = (byte)crc;
                frame[frame.Length - 1] = (byte)(crc >> 8);
            }
            return frame;
        }

        byte[] HandlePdu(byte fc, byte[] pdu)
        {
            if (fc == 3 || fc == 4)
            {
                if (pdu.Length < 5) throw ModbusException.FromSlaveException(3, "bad length");
                int addr = (pdu[1] << 8) | pdu[2];
                int qty = (pdu[3] << 8) | pdu[4];
                if (qty < 1 || qty > 125) throw ModbusException.FromSlaveException(3, "bad quantity");
                Dictionary<int, ushort> map = BuildMap();
                byte[] r = new byte[2 + qty * 2];
                r[0] = fc;
                r[1] = (byte)(qty * 2);
                for (int i = 0; i < qty; i++)
                {
                    ushort v;
                    if (!map.TryGetValue(addr + i, out v)) throw ModbusException.FromSlaveException(2, "illegal address");
                    r[2 + i * 2] = (byte)(v >> 8);
                    r[3 + i * 2] = (byte)v;
                }
                return r;
            }
            if (fc == 16)
            {
                if (pdu.Length < 6) throw ModbusException.FromSlaveException(3, "bad length");
                int addr = (pdu[1] << 8) | pdu[2];
                int qty = (pdu[3] << 8) | pdu[4];
                int bc = pdu[5];
                if (qty < 1 || bc != qty * 2 || pdu.Length < 6 + bc) throw ModbusException.FromSlaveException(3, "bad count");
                for (int i = 0; i < qty; i++)
                    if (!settings.ContainsKey(addr + i)) throw ModbusException.FromSlaveException(2, "illegal address");
                for (int i = 0; i < qty; i++)
                {
                    ushort v = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                    int a = addr + i;
                    if (a == Va520Map.RegId && (v < 1 || v > 247)) throw ModbusException.FromSlaveException(3, "bad id");
                    if (a == Va520Map.RegBaud && v > 5) throw ModbusException.FromSlaveException(3, "bad baud");
                    if (a == Va520Map.RegParity && v > 2) throw ModbusException.FromSlaveException(3, "bad parity");
                    if (a == Va520Map.RegStopBits && v > 1) throw ModbusException.FromSlaveException(3, "bad stop bits");
                    if (a == Va520Map.RegWordOrder && v != Va520Map.WordOrderABCD && v != Va520Map.WordOrderCDAB) throw ModbusException.FromSlaveException(3, "bad word order");
                    settings[a] = v;
                    if (a == Va520Map.RegWordOrder) Order = v == Va520Map.WordOrderCDAB ? WordOrder.CDAB : WordOrder.ABCD;
                    if (a == Va520Map.RegId) UnitId = (byte)v;
                }
                return new byte[] { 16, pdu[1], pdu[2], pdu[3], pdu[4] };
            }
            throw ModbusException.FromSlaveException(1, "illegal function");
        }

        Dictionary<int, ushort> BuildMap()
        {
            Dictionary<int, ushort> m = new Dictionary<int, ushort>();
            foreach (KeyValuePair<int, ushort> kv in settings) m[kv.Key] = kv.Value;

            double t = (DateTime.Now - start).TotalSeconds;
            double flow = Math.Max(0, BaseFlow + 12.0 * Math.Sin(t / 8.0) + 1.5 * Math.Sin(t * 1.7));   // m3/h
            double temp = BaseTemp + 1.2 * Math.Sin(t / 30.0);
            double vel = flow / 3600.0 / 0.0022;        // roughly a 53 mm pipe
            DateTime now = DateTime.Now;
            totalM3 += flow * (now - lastTotalUpdate).TotalHours;
            lastTotalUpdate = now;

            foreach (RegDef d in Va520Map.Values)
            {
                double v = ValueFor(d.Key, flow, temp, vel);
                byte[] b = d.Type == RegType.Float32 ? ModbusCodec.FromFloat((float)v, Order) : ModbusCodec.FromUInt((uint)Math.Max(0, v), Order);
                m[d.Address] = (ushort)((b[0] << 8) | b[1]);
                m[d.Address + 1] = (ushort)((b[2] << 8) | b[3]);
            }
            return m;
        }

        double ValueFor(string key, double flow, double temp, double vel)
        {
            const double Norm = 0.9195;     // Nm3 per m3 (about)
            switch (key)
            {
                case "flow_m3h": return flow;
                case "flow_Nm3h": return flow * Norm;
                case "flow_m3min": return flow / 60.0;
                case "flow_Nm3min": return flow * Norm / 60.0;
                case "flow_lh": return flow * 1000.0;
                case "flow_Nlh": return flow * 1000.0 * Norm;
                case "flow_lmin": return flow * 1000.0 / 60.0;
                case "flow_Nlmin": return flow * 1000.0 * Norm / 60.0;
                case "flow_ls": return flow * 1000.0 / 3600.0;
                case "flow_Nls": return flow * 1000.0 * Norm / 3600.0;
                case "flow_cfm": return flow * 0.58857;
                case "flow_Ncfm": return flow * 0.58857 * Norm;
                case "flow_kgh": return flow * 1.204;
                case "flow_kgmin": return flow * 1.204 / 60.0;
                case "flow_kgs": return flow * 1.204 / 3600.0;
                case "flow_kW": return flow * 0.0055;
                case "cons_m3": return totalM3;
                case "cons_Nm3": return totalM3 * Norm;
                case "cons_l": return totalM3 * 1000.0;
                case "cons_Nl": return totalM3 * 1000.0 * Norm;
                case "cons_cf": return totalM3 * 35.3147;
                case "cons_Ncf": return totalM3 * 35.3147 * Norm;
                case "cons_kg": return totalM3 * 1.204;
                case "cons_kWh": return totalM3 * 0.0055;
                case "vel_ms": return vel;
                case "vel_Nms": return vel * Norm;
                case "vel_ftmin": return vel * 196.85;
                case "vel_Nftmin": return vel * 196.85 * Norm;
                case "temp_C": return temp;
                case "temp_F": return temp * 1.8 + 32.0;
            }
            return 0;
        }
    }
}
