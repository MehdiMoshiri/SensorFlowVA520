using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VA520Tester
{
    /// <summary>Self test of the protocol core against the built-in simulator. Exit code 0 = all passed.</summary>
    static class Program
    {
        static int passed, failed;

        static void Check(bool ok, string name)
        {
            if (ok) { passed++; Console.WriteLine("  PASS  " + name); }
            else { failed++; Console.WriteLine("  FAIL  " + name); }
        }

        static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        static ModbusClient Connect(int port, Framing framing)
        {
            ModbusClient c = new ModbusClient(new TcpChannel("127.0.0.1", port, 2000), framing);
            c.ResponseTimeoutMs = 500;
            c.InterFrameDelayMs = 0;
            c.Open();
            return c;
        }

        static int Main()
        {
            Console.WriteLine("VA520 tester self test");
            ErrorLogger.MaxFileMb = 1;

            // ---------------------------------------------------------------- codec
            Console.WriteLine("[codec]");
            byte[] req = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
            ushort crc = ModbusCodec.Crc16(req, 0, req.Length);
            Check(crc == 0xCDC5, "CRC16 of 01 03 00 00 00 0A is C5 CD (low byte first)");
            Check(ModbusCodec.Hex(ModbusCodec.FromFloat(1.0f, WordOrder.ABCD)) == "3F 80 00 00", "1.0f ABCD = 3F 80 00 00");
            Check(ModbusCodec.Hex(ModbusCodec.FromFloat(1.0f, WordOrder.CDAB)) == "00 00 3F 80", "1.0f CDAB = 00 00 3F 80");
            Check(ModbusCodec.Hex(ModbusCodec.FromFloat(1.0f, WordOrder.BADC)) == "80 3F 00 00", "1.0f BADC = 80 3F 00 00");
            Check(ModbusCodec.Hex(ModbusCodec.FromFloat(1.0f, WordOrder.DCBA)) == "00 00 80 3F", "1.0f DCBA = 00 00 80 3F");
            foreach (WordOrder o in new WordOrder[] { WordOrder.ABCD, WordOrder.CDAB, WordOrder.BADC, WordOrder.DCBA })
            {
                float f = ModbusCodec.ToFloat(ModbusCodec.FromFloat(-123.456f, o), 0, o);
                Check(f == -123.456f, "float round trip " + o);
                uint u = (uint)ModbusCodec.Decode(RegType.UInt32, ModbusCodec.FromUInt(3000000000u, o), 0, o);
                Check(u == 3000000000u, "uint32 round trip " + o);
            }
            byte[] h = ModbusCodec.ParseHex("01 03 0x04, 4c");
            Check(h != null && h.Length == 4 && h[3] == 0x4C, "ParseHex");
            Check(ModbusCodec.ParseHex("01 0") == null && ModbusCodec.ParseHex("zz") == null, "ParseHex rejects bad input");

            // ---------------------------------------------------------------- MBAP (Modbus TCP) against simulator
            Console.WriteLine("[Modbus TCP]");
            Va520Simulator simTcp = new Va520Simulator();
            simTcp.Port = 15020;
            simTcp.Framing = Framing.Mbap;
            simTcp.Start();
            ModbusClient tcp = Connect(15020, Framing.Mbap);
            try
            {
                RegDef flow = Va520Map.Find("flow_m3h");
                byte[] d = tcp.ReadRegisters(1, 3, flow.Address, flow.RegCount);
                double fl = ModbusCodec.Decode(flow.Type, d, 0, WordOrder.ABCD);
                Check(fl > 0 && fl < 100, "flow m3/h plausible (" + fl.ToString("0.00") + ")");

                RegDef temp = Va520Map.Find("temp_C");
                double tc = ModbusCodec.Decode(temp.Type, tcp.ReadRegisters(1, 3, temp.Address, 2), 0, WordOrder.ABCD);
                Check(Near(tc, 21.5, 2), "temperature plausible (" + tc.ToString("0.00") + ")");

                RegDef cons = Va520Map.Find("cons_m3");
                double cm = ModbusCodec.Decode(cons.Type, tcp.ReadRegisters(1, 3, cons.Address, 2), 0, WordOrder.ABCD);
                Check(cm >= 123456 && cm < 123457, "consumption UInt32 (" + cm + ")");

                byte[] st = tcp.ReadRegisters(1, 3, Va520Map.RegId, 5);
                Check(ModbusCodec.ToUInt16(st, 0) == 1 && ModbusCodec.ToUInt16(st, 2) == 4 && ModbusCodec.ToUInt16(st, 4) == 1
                      && ModbusCodec.ToUInt16(st, 6) == 0 && ModbusCodec.ToUInt16(st, 8) == 0xABCD, "settings registers 2000..2004 = factory defaults");

                // illegal data address -> Modbus exception 2
                try { tcp.ReadRegisters(1, 3, 1102, 2); Check(false, "illegal address raises exception"); }
                catch (ModbusException ex) { Check(ex.IsModbusException && ex.ExceptionCode == 2 && ex.Code == 1102, "illegal address -> exception 2 / code 1102"); }

                // unsupported function -> exception 1
                try { tcp.Transact(1, new byte[] { 0x2B, 0x0E, 0x01, 0x00 }); Check(false, "illegal function raises exception"); }
                catch (ModbusException ex) { Check(ex.ExceptionCode == 1, "illegal function -> exception 1"); }

                // wrong slave id -> no answer -> timeout
                try { tcp.ReadRegisters(7, 3, flow.Address, 2); Check(false, "wrong unit raises timeout"); }
                catch (ModbusException ex) { Check(ex.Code == ErrCodes.Timeout, "wrong unit id -> timeout"); }

                // write word order CDAB, then values must be read with CDAB
                tcp.WriteRegisters(1, Va520Map.RegWordOrder, new ushort[] { Va520Map.WordOrderCDAB });
                tcp.Order = WordOrder.CDAB;
                double fl2 = ModbusCodec.Decode(flow.Type, tcp.ReadRegisters(1, 3, flow.Address, 2), 0, WordOrder.CDAB);
                Check(fl2 > 0 && fl2 < 100, "after writing 0xCDAB the value decodes with CDAB (" + fl2.ToString("0.00") + ")");
                double wrong = ModbusCodec.Decode(flow.Type, tcp.ReadRegisters(1, 3, flow.Address, 2), 0, WordOrder.ABCD);
                Check(!(wrong > 0 && wrong < 100) || double.IsNaN(wrong) || Math.Abs(wrong - fl2) > 1, "decoding with the wrong order gives garbage");
                tcp.WriteRegisters(1, Va520Map.RegWordOrder, new ushort[] { Va520Map.WordOrderABCD });

                // write rejected
                try { tcp.WriteRegisters(1, Va520Map.RegBaud, new ushort[] { 9 }); Check(false, "bad baud rejected"); }
                catch (ModbusException ex) { Check(ex.ExceptionCode == 3, "write of illegal baud code -> exception 3"); }

                // raw frame
                byte[] raw = tcp.WrapForSend(new byte[] { 0x01, 0x03, 0x07, 0xD0, 0x00, 0x01 });
                byte[] rr = tcp.SendRaw(raw, 500);
                Check(rr.Length == 11 && rr[7] == 3 && rr[8] == 2, "raw MBAP frame answered (" + ModbusCodec.Hex(rr) + ")");

                Check(tcp.Stats.Success > 5 && tcp.Stats.SlaveExceptions >= 3 && tcp.Stats.Timeouts == 1, "statistics counted");
            }
            finally { tcp.Close(); simTcp.Stop(); }

            // ---------------------------------------------------------------- RTU frames (over TCP) against simulator
            Console.WriteLine("[RTU framing]");
            Va520Simulator simRtu = new Va520Simulator();
            simRtu.Port = 15021;
            simRtu.Framing = Framing.Rtu;
            simRtu.Start();
            ModbusClient rtu = Connect(15021, Framing.Rtu);
            try
            {
                RegDef vel = Va520Map.Find("vel_ms");
                double v = ModbusCodec.Decode(vel.Type, rtu.ReadRegisters(1, 3, vel.Address, 2), 0, WordOrder.ABCD);
                Check(v >= 0, "RTU read velocity (" + v.ToString("0.00") + " m/s)");

                try { rtu.ReadRegisters(1, 3, 1102, 2); Check(false, "RTU exception expected"); }
                catch (ModbusException ex) { Check(ex.ExceptionCode == 2, "RTU illegal address -> exception 2"); }

                byte[] wr = rtu.WrapForSend(new byte[] { 0x01, 0x03, 0x04, 0x4C, 0x00, 0x02 });
                Check(wr.Length == 8 && ModbusCodec.Crc16(wr, 0, 8) == 0, "WrapForSend appends a valid CRC");
                byte[] ra = rtu.SendRaw(wr, 500);
                Check(ra.Length == 9 && ra[1] == 3 && ra[2] == 4, "raw RTU frame answered (" + ModbusCodec.Hex(ra) + ")");

                // CRC fault injection
                simRtu.BadCrcPercent = 100;
                try { rtu.ReadRegisters(1, 3, vel.Address, 2); Check(false, "CRC error expected"); }
                catch (ModbusException ex) { Check(ex.Code == ErrCodes.Crc, "bad CRC detected -> E1003"); }
                simRtu.BadCrcPercent = 0;

                // drop fault injection + retries
                simRtu.DropPercent = 100;
                rtu.ResponseTimeoutMs = 150;
                try { rtu.ReadRegisters(1, 3, vel.Address, 2); Check(false, "timeout expected"); }
                catch (ModbusException ex) { Check(ex.Code == ErrCodes.Timeout, "dropped answer -> timeout E1002"); }
                rtu.Retries = 2;
                long before = rtu.Stats.Retries;
                try { rtu.ReadRegisters(1, 3, vel.Address, 2); } catch (ModbusException) { }
                Check(rtu.Stats.Retries - before == 2, "2 retries performed");
                rtu.Retries = 0;
                simRtu.DropPercent = 0;
                rtu.ResponseTimeoutMs = 500;
                double v2 = ModbusCodec.Decode(vel.Type, rtu.ReadRegisters(1, 3, vel.Address, 2), 0, WordOrder.ABCD);
                Check(v2 >= 0, "link works again after faults");

                // converter that echoes the request
                simRtu.EchoRequest = true;
                try { rtu.ReadRegisters(1, 3, vel.Address, 2); Check(false, "echo without removal should fail"); }
                catch (ModbusException ex) { Check(ex.Code == ErrCodes.Crc || ex.Code == ErrCodes.ShortFrame || ex.Code == ErrCodes.BadFunction, "echo without StripEcho -> error E" + ex.Code + " (like QMaster 'Invalid PDU')"); }
                rtu.StripEcho = true;
                double ve = ModbusCodec.Decode(vel.Type, rtu.ReadRegisters(1, 3, vel.Address, 2), 0, WordOrder.ABCD);
                Check(ve >= 0, "StripEcho removes the echo and the answer is read (" + ve.ToString("0.00") + " m/s)");
                try { rtu.ReadRegisters(1, 3, 1102, 2); Check(false, "exception expected"); }
                catch (ModbusException ex) { Check(ex.ExceptionCode == 2, "StripEcho also works for exception answers"); }
                byte[] rawE = rtu.WrapForSend(new byte[] { 0x01, 0x03, 0x04, 0x4C, 0x00, 0x02 });
                byte[] rxE = rtu.SendRaw(rawE, 500);
                Check(rxE.Length == 9 && rxE[1] == 3, "StripEcho with raw frames (" + ModbusCodec.Hex(rxE) + ")");
                rtu.StripEcho = false;
                simRtu.EchoRequest = false;
                double vn2 = ModbusCodec.Decode(vel.Type, rtu.ReadRegisters(1, 3, vel.Address, 2), 0, WordOrder.ABCD);
                Check(vn2 >= 0, "no echo, StripEcho off: still fine");
                rtu.StripEcho = true;
                double vn3 = ModbusCodec.Decode(vel.Type, rtu.ReadRegisters(1, 3, vel.Address, 2), 0, WordOrder.ABCD);
                Check(vn3 >= 0, "StripEcho on but converter does not echo: answer is still read");
                rtu.StripEcho = false;

                // poller
                List<ValueSlot> slots = new List<ValueSlot>();
                foreach (RegDef def in Va520Map.Values)
                {
                    ValueSlot s = new ValueSlot();
                    s.Def = def;
                    s.Enabled = def.DefaultEnabled;
                    slots.Add(s);
                }
                slots[0].LimitEnabled = true;
                slots[0].LimitMin = 1000;      // flow is certainly below -> limit violation must be logged
                slots[0].LimitMax = 2000;
                PollSettings ps = new PollSettings();
                ps.IntervalMs = 100;
                Poller poller = new Poller(rtu, slots, ps);
                poller.Start();
                Thread.Sleep(1200);
                poller.Stop();
                int valid = 0;
                foreach (ValueSlot s in slots) if (s.Enabled && s.Valid) valid++;
                Check(poller.Cycles >= 5, "poller ran " + poller.Cycles + " cycles");
                Check(valid == 4, "all 4 enabled values valid (" + valid + ")");
                Check(slots[0].Samples >= 5 && !double.IsNaN(slots[0].Min) && slots[0].History.Count >= 5, "min/max/avg/history collected");
                Check(slots[0].OutOfLimit, "limit violation detected");

                // silent sensor: every ticked value must still be tried in the cycle (default), or skipped when the option is on
                simRtu.DropPercent = 100;
                rtu.ResponseTimeoutMs = 120;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool skip = pass == 1;
                    List<ValueSlot> s2 = new List<ValueSlot>();
                    foreach (RegDef def in Va520Map.Values)
                    {
                        ValueSlot s = new ValueSlot();
                        s.Def = def;
                        s.Enabled = def.DefaultEnabled;       // 4 values
                        s2.Add(s);
                    }
                    PollSettings p2 = new PollSettings();
                    p2.SingleShot = true;
                    p2.SkipAfterTimeout = skip;
                    long reqBefore = rtu.Stats.Requests;
                    Poller poller2 = new Poller(rtu, s2, p2);
                    poller2.Start();
                    for (int w = 0; w < 100 && poller2.IsRunning; w++) Thread.Sleep(50);
                    int tried = 0, skipped = 0;
                    foreach (ValueSlot s in s2)
                    {
                        if (!s.Enabled) continue;
                        if (s.Status == "skipped") skipped++;
                        else if (s.Status == "timeout") tried++;
                    }
                    long requests = rtu.Stats.Requests - reqBefore;
                    if (!skip) Check(tried == 4 && skipped == 0 && requests == 4, "silent sensor, default: all 4 ticked values are requested in the cycle (requests " + requests + ")");
                    else Check(tried == 1 && skipped == 3 && requests == 1, "silent sensor, 'skip after timeout' on: rest of the cycle skipped (requests " + requests + ")");
                }
                simRtu.DropPercent = 0;
                rtu.ResponseTimeoutMs = 500;
            }
            finally { rtu.Close(); simRtu.Stop(); }

            // ---------------------------------------------------------------- timing
            Console.WriteLine("[timing]");
            Va520Simulator simT = new Va520Simulator();
            simT.Port = 15022;
            simT.Start();
            ModbusClient tm = Connect(15022, Framing.Mbap);
            for (int i = 0; i < 20; i++) tm.ReadRegisters(1, 3, 1100, 2);
            tm.Stats.Reset();
            for (int i = 0; i < 200; i++) tm.ReadRegisters(1, 3, 1100, 2);
            Console.WriteLine("  info  200 reads over loopback: avg " + tm.Stats.Snapshot().RttAvg.ToString("0.00") + " ms, min " + tm.Stats.Snapshot().RttMin.ToString("0.00") + " ms, max " + tm.Stats.Snapshot().RttMax.ToString("0.00") + " ms");
            tm.Close();
            simT.Stop();

            // ---------------------------------------------------------------- connection failures
            Console.WriteLine("[connection errors]");
            string missing = Path.DirectorySeparatorChar == '/' ? "/dev/ttyNONE99" : "COM99";
            SerialSettings ss = new SerialSettings();
            ss.PortName = missing;
            try { new SerialChannel(ss).Open(); Check(false, "missing serial port raises an error"); }
            catch (ModbusException ex) { Check(ex.Code == ErrCodes.PortOpen, "missing serial port -> E1001 (" + ex.Message + ")"); }
            Check(SerialChannel.ListPorts() != null, "serial port enumeration works");
            try { new TcpChannel("127.0.0.1", 15099, 500).Open(); Check(false, "closed TCP port raises an error"); }
            catch (ModbusException ex) { Check(ex.Code == ErrCodes.TcpConnect, "closed TCP port -> E1008"); }
            ModbusClient closed = new ModbusClient(new TcpChannel("127.0.0.1", 15099, 500), Framing.Mbap);
            try { closed.ReadRegisters(1, 3, 1100, 2); Check(false, "not connected raises an error"); }
            catch (ModbusException ex) { Check(ex.Code == ErrCodes.NotConnected, "request while not connected -> E1016"); }

            // ---------------------------------------------------------------- error log
            Console.WriteLine("[error log]");
            string path = ErrorLogger.FilePath;
            ErrorEntry e1 = ErrorLogger.Log(ErrCodes.Timeout, "self test error one");
            ErrorEntry e2 = ErrorLogger.Log(ErrCodes.Crc, "self test\r\nerror two");
            Check(e1 != null && e2 != null && e2.No == e1.No + 1, "error numbers increase");
            string[] lines = File.ReadAllLines(path);
            string last = lines[lines.Length - 1];
            string[] parts = last.Split('|');
            Check(parts.Length == 5, "line has 5 columns: " + last);
            Check(parts[0].Trim() == e2.No.ToString("0000") && parts[1].Trim() == "E1003" && parts[2].Contains("self test error two"), "number | code | text");
            DateTime dt;
            Check(DateTime.TryParseExact(parts[3].Trim(), "HH:mm:ss.fff", null, System.Globalization.DateTimeStyles.None, out dt)
                  && DateTime.TryParseExact(parts[4].Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out dt), "time | date columns");
            Check(lines[0].StartsWith("No | Code"), "file starts with a header line");
            Check(path.StartsWith(AppDomain.CurrentDomain.BaseDirectory), "log file is next to the executable (" + path + ")");

            Console.WriteLine();
            Console.WriteLine("passed: " + passed + "   failed: " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
