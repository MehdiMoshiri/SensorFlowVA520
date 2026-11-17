using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VA520Tester
{
    public enum Framing
    {
        Rtu,    // address + PDU + CRC16   (serial, or "RTU over TCP")
        Mbap    // MBAP header + PDU       (Modbus TCP)
    }

    /// <summary>Communication statistics, thread safe.</summary>
    public class CommStats
    {
        readonly object sync = new object();
        public long Requests, Success, Timeouts, CrcErrors, SlaveExceptions, OtherErrors, Retries;
        public double RttLast, RttMin = double.MaxValue, RttMax, RttSum;
        public DateTime Started = DateTime.Now;

        public void Reset()
        {
            lock (sync)
            {
                Requests = Success = Timeouts = CrcErrors = SlaveExceptions = OtherErrors = Retries = 0;
                RttLast = RttMax = RttSum = 0;
                RttMin = double.MaxValue;
                Started = DateTime.Now;
            }
        }

        public void AddRetry() { lock (sync) { Retries++; } }

        public void AddSuccess(double rttMs)
        {
            lock (sync)
            {
                Requests++;
                Success++;
                RttLast = rttMs;
                RttSum += rttMs;
                if (rttMs < RttMin) RttMin = rttMs;
                if (rttMs > RttMax) RttMax = rttMs;
            }
        }

        public void AddFailure(ModbusException ex)
        {
            lock (sync)
            {
                Requests++;
                if (ex.Code == ErrCodes.Timeout) Timeouts++;
                else if (ex.Code == ErrCodes.Crc) CrcErrors++;
                else if (ex.IsModbusException) SlaveExceptions++;
                else OtherErrors++;
            }
        }

        public CommStats Snapshot()
        {
            lock (sync)
            {
                CommStats c = new CommStats();
                c.Requests = Requests; c.Success = Success; c.Timeouts = Timeouts; c.CrcErrors = CrcErrors;
                c.SlaveExceptions = SlaveExceptions; c.OtherErrors = OtherErrors; c.Retries = Retries;
                c.RttLast = RttLast; c.RttMin = RttMin; c.RttMax = RttMax; c.RttSum = RttSum; c.Started = Started;
                return c;
            }
        }

        public double SuccessPercent { get { return Requests == 0 ? 0 : 100.0 * Success / Requests; } }
        public double RttAvg { get { return Success == 0 ? 0 : RttSum / Success; } }
        public long Failures { get { return Requests - Success; } }
    }

    /// <summary>
    /// Modbus master (function 03/04 read, 16 write, raw frames) over a byte channel.
    /// One request at a time (internal lock).
    /// </summary>
    public class ModbusClient
    {
        readonly IByteChannel channel;
        readonly Framing framing;
        readonly object sync = new object();
        ushort transactionId;
        double lastRttMs;

        public int ResponseTimeoutMs = 1000;
        public int Retries = 0;
        /// <summary>Silent time before every request (RTU: at least 3.5 characters).</summary>
        public int InterFrameDelayMs = 10;
        /// <summary>A gap this long inside a frame means the frame has ended.</summary>
        public int InterCharTimeoutMs = 30;
        /// <summary>Pause after every answer before the next request.</summary>
        public int TurnaroundDelayMs = 0;
        public int AddressOffset = 0;
        public WordOrder Order = WordOrder.ABCD;
        /// <summary>RTU only: the converter sends our own request back to us - remove it before reading the answer.</summary>
        public bool StripEcho;

        public readonly CommStats Stats = new CommStats();
        /// <summary>Set to a function that returns true to abort a running transaction.</summary>
        public Func<bool> CancelCheck;
        /// <summary>Called for every frame: "TX ..." / "RX ..." (may be called from any thread).</summary>
        public event Action<string> Trace;
        public bool TraceEnabled;

        public ModbusClient(IByteChannel channel, Framing framing)
        {
            this.channel = channel;
            this.framing = framing;
        }

        public IByteChannel Channel { get { return channel; } }
        public Framing Framing { get { return framing; } }
        public bool IsOpen { get { return channel.IsOpen; } }

        public void Open() { channel.Open(); }
        public void Close() { channel.Close(); }

        void DoTrace(string dir, byte[] b, int n)
        {
            Action<string> h = Trace;
            if (!TraceEnabled || h == null) return;
            try { h(n == 0 ? dir + " (no bytes received)" : dir + " " + ModbusCodec.Hex(b, 0, n)); } catch { }
        }

        void CheckCancel()
        {
            Func<bool> c = CancelCheck;
            if (c != null && c()) throw new ModbusException(ErrCodes.Cancelled, "Cancelled");
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Reads quantity registers. Returns quantity*2 data bytes (as sent by the slave).</summary>
        public byte[] ReadRegisters(byte unit, byte function, int address, int quantity)
        {
            int wire = address + AddressOffset;
            if (wire < 0 || wire > 0xFFFF) throw new ModbusException(ErrCodes.General, "Address out of range: " + wire);
            if (quantity < 1 || quantity > 125) throw new ModbusException(ErrCodes.General, "Quantity must be 1..125");
            byte[] pdu = new byte[5];
            pdu[0] = function;
            pdu[1] = (byte)(wire >> 8);
            pdu[2] = (byte)wire;
            pdu[3] = (byte)(quantity >> 8);
            pdu[4] = (byte)quantity;
            byte[] resp = Transact(unit, pdu);
            if (resp.Length < 2 || resp[1] != quantity * 2)
                throw new ModbusException(ErrCodes.ShortFrame, "Unexpected byte count in response (" + (resp.Length > 1 ? resp[1].ToString() : "?") + ", expected " + (quantity * 2) + ")");
            byte[] data = new byte[quantity * 2];
            Buffer.BlockCopy(resp, 2, data, 0, data.Length);
            return data;
        }

        /// <summary>Function 16 (write multiple registers).</summary>
        public void WriteRegisters(byte unit, int address, ushort[] values)
        {
            int wire = address + AddressOffset;
            if (wire < 0 || wire > 0xFFFF) throw new ModbusException(ErrCodes.General, "Address out of range: " + wire);
            if (values.Length < 1 || values.Length > 123) throw new ModbusException(ErrCodes.General, "Quantity must be 1..123");
            byte[] pdu = new byte[6 + values.Length * 2];
            pdu[0] = 16;
            pdu[1] = (byte)(wire >> 8);
            pdu[2] = (byte)wire;
            pdu[3] = (byte)(values.Length >> 8);
            pdu[4] = (byte)values.Length;
            pdu[5] = (byte)(values.Length * 2);
            for (int i = 0; i < values.Length; i++)
            {
                pdu[6 + i * 2] = (byte)(values[i] >> 8);
                pdu[7 + i * 2] = (byte)values[i];
            }
            byte[] resp = Transact(unit, pdu);
            if (resp.Length < 5 || resp[1] != pdu[1] || resp[2] != pdu[2] || resp[3] != pdu[3] || resp[4] != pdu[4])
                throw new ModbusException(ErrCodes.WriteFailed, "Write acknowledge does not match the request");
        }

        /// <summary>Sends a request PDU and returns the response PDU (function code + data).</summary>
        public byte[] Transact(byte unit, byte[] pdu)
        {
            lock (sync)
            {
                if (!channel.IsOpen)
                {
                    ModbusException nc = new ModbusException(ErrCodes.NotConnected, "Not connected");
                    Stats.AddFailure(nc);
                    throw nc;
                }
                ModbusException last = null;
                for (int attempt = 0; attempt <= Retries; attempt++)
                {
                    CheckCancel();
                    if (attempt > 0) Stats.AddRetry();
                    try
                    {
                        byte[] resp = Exchange(unit, pdu);
                        Stats.AddSuccess(lastRttMs);
                        if (TurnaroundDelayMs > 0) Thread.Sleep(TurnaroundDelayMs);
                        return resp;
                    }
                    catch (ModbusException ex)
                    {
                        last = ex;
                        if (ex.Code == ErrCodes.Cancelled)
                            throw;
                        if (!ex.IsRetryable) break;
                    }
                }
                Stats.AddFailure(last);
                throw last;
            }
        }

        /// <summary>
        /// Sends the given bytes exactly as they are (for RTU the caller adds the CRC) and returns whatever arrives
        /// (empty array on timeout). Used by the "raw frame" tool.
        /// </summary>
        public byte[] SendRaw(byte[] frame, int timeoutMs)
        {
            lock (sync)
            {
                if (!channel.IsOpen) throw new ModbusException(ErrCodes.NotConnected, "Not connected");
                channel.DiscardInput();
                if (InterFrameDelayMs > 0) Thread.Sleep(InterFrameDelayMs);
                DoTrace("TX", frame, frame.Length);
                try { channel.Write(frame, 0, frame.Length); }
                catch (Exception ex) { throw new ModbusException(ErrCodes.PortIo, "Write failed: " + ex.Message, ex); }
                byte[] buf = new byte[512];
                int n = framing == Framing.Rtu ? ReceiveRtu(buf, timeoutMs, true, StripEcho ? frame : null) : ReceiveMbap(buf, timeoutMs);
                DoTrace("RX", buf, n);
                byte[] r = new byte[n];
                Buffer.BlockCopy(buf, 0, r, 0, n);
                return r;
            }
        }

        /// <summary>Builds a complete frame for the raw tool: RTU = adds CRC, MBAP = adds the MBAP header.</summary>
        public byte[] WrapForSend(byte[] unitAndPdu)
        {
            if (framing == Framing.Rtu)
            {
                byte[] f = new byte[unitAndPdu.Length + 2];
                Buffer.BlockCopy(unitAndPdu, 0, f, 0, unitAndPdu.Length);
                ushort crc = ModbusCodec.Crc16(unitAndPdu, 0, unitAndPdu.Length);
                f[f.Length - 2] = (byte)crc;
                f[f.Length - 1] = (byte)(crc >> 8);
                return f;
            }
            else
            {
                ushort id = NextId();
                byte[] f = new byte[6 + unitAndPdu.Length];
                f[0] = (byte)(id >> 8);
                f[1] = (byte)id;
                f[4] = (byte)(unitAndPdu.Length >> 8);
                f[5] = (byte)unitAndPdu.Length;
                Buffer.BlockCopy(unitAndPdu, 0, f, 6, unitAndPdu.Length);
                return f;
            }
        }

        // ------------------------------------------------------------------ internals

        ushort NextId()
        {
            unchecked { transactionId++; }
            return transactionId;
        }

        byte[] Exchange(byte unit, byte[] pdu)
        {
            channel.DiscardInput();
            if (InterFrameDelayMs > 0) Thread.Sleep(InterFrameDelayMs);
            CheckCancel();

            byte[] frame;
            ushort txId = 0;
            if (framing == Framing.Rtu)
            {
                frame = new byte[pdu.Length + 3];
                frame[0] = unit;
                Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
                ushort crc = ModbusCodec.Crc16(frame, 0, pdu.Length + 1);
                frame[frame.Length - 2] = (byte)crc;
                frame[frame.Length - 1] = (byte)(crc >> 8);
            }
            else
            {
                txId = NextId();
                frame = new byte[pdu.Length + 7];
                frame[0] = (byte)(txId >> 8);
                frame[1] = (byte)txId;
                frame[4] = (byte)((pdu.Length + 1) >> 8);
                frame[5] = (byte)(pdu.Length + 1);
                frame[6] = unit;
                Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            }

            DoTrace("TX", frame, frame.Length);
            Stopwatch rtt = Stopwatch.StartNew();      // response time = request sent -> answer complete (without the silent gap)
            try
            {
                channel.Write(frame, 0, frame.Length);
            }
            catch (ModbusException) { throw; }
            catch (Exception ex)
            {
                throw new ModbusException(ErrCodes.PortIo, "Write failed: " + ex.Message, ex);
            }

            byte[] buf = new byte[512];
            int n;
            if (framing == Framing.Rtu)
            {
                n = ReceiveRtu(buf, ResponseTimeoutMs, false, StripEcho ? frame : null);
                lastRttMs = rtt.Elapsed.TotalMilliseconds;
                DoTrace("RX", buf, n);
                return ParseRtu(buf, n, unit, pdu[0]);
            }
            n = ReceiveMbap(buf, ResponseTimeoutMs);
            lastRttMs = rtt.Elapsed.TotalMilliseconds;
            DoTrace("RX", buf, n);
            return ParseMbap(buf, n, txId, unit, pdu[0]);
        }

        int ReadSlice(byte[] buf, int n, int timeoutMs)
        {
            try
            {
                return channel.Read(buf, n, buf.Length - n, timeoutMs);
            }
            catch (ModbusException) { throw; }
            catch (Exception ex)
            {
                throw new ModbusException(ErrCodes.PortIo, "Read failed: " + ex.Message, ex);
            }
        }

        /// <summary>Collects an RTU frame. Returns the number of bytes in buf (0 = nothing received).</summary>
        int ReceiveRtu(byte[] buf, int timeoutMs, bool raw, byte[] echo)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int n = 0;
            long lastByteAt = 0;
            int gap = Math.Max(2, InterCharTimeoutMs);
            bool echoPending = echo != null;
            while (true)
            {
                CheckCancel();
                long el = sw.ElapsedMilliseconds;
                if (el >= timeoutMs) break;
                int slice = (int)Math.Min(timeoutMs - el, n == 0 ? 20 : gap);
                if (slice < 1) slice = 1;
                int r = ReadSlice(buf, n, slice);
                if (r > 0)
                {
                    n += r;
                    lastByteAt = sw.ElapsedMilliseconds;
                    if (echoPending)
                    {
                        int cmp = Math.Min(n, echo.Length);
                        bool same = true;
                        for (int i = 0; i < cmp; i++) if (buf[i] != echo[i]) { same = false; break; }
                        if (!same) echoPending = false;                  // does not start like our request: no echo
                        else if (n < echo.Length) continue;              // echo still arriving
                        else
                        {
                            DoTrace("RX echo (removed)", echo, echo.Length);
                            Buffer.BlockCopy(buf, echo.Length, buf, 0, n - echo.Length);
                            n -= echo.Length;
                            echoPending = false;
                            if (n == 0) continue;                        // wait for the real answer
                        }
                    }
                    int len = ModbusCodec.ExpectedRtuResponseLength(buf, n);
                    if (!raw && len > 0 && n >= len) break;
                    if (raw && len > 0 && n >= len) { /* complete; give a short moment for trailing junk then stop */ break; }
                    if (n >= buf.Length) break;
                }
                else if (n > 0 && sw.ElapsedMilliseconds - lastByteAt >= gap)
                {
                    break;      // silence: frame ended (possibly truncated)
                }
            }
            return n;
        }

        byte[] ParseRtu(byte[] b, int n, byte unit, byte function)
        {
            if (n == 0)
                throw new ModbusException(ErrCodes.Timeout, "No response from slave " + unit + " within " + ResponseTimeoutMs + " ms");
            int len = ModbusCodec.ExpectedRtuResponseLength(b, n);
            if (len > 0 && n > len) n = len;      // ignore trailing junk
            if (n < 5 || (len > 0 && n < len))
                throw new ModbusException(ErrCodes.ShortFrame, "Short response (" + n + " bytes): " + ModbusCodec.Hex(b, 0, n));
            ushort calc = ModbusCodec.Crc16(b, 0, n - 2);
            ushort got = (ushort)(b[n - 2] | (b[n - 1] << 8));
            if (calc != got)
                throw new ModbusException(ErrCodes.Crc, "CRC error: received " + got.ToString("X4") + ", calculated " + calc.ToString("X4") + " (" + ModbusCodec.Hex(b, 0, n) + ")");
            if (b[0] != unit)
                throw new ModbusException(ErrCodes.BadSlave, "Response from slave " + b[0] + ", expected " + unit);
            return CheckPdu(b, 1, n - 3, function);
        }

        byte[] CheckPdu(byte[] b, int offset, int length, byte function)
        {
            byte fc = b[offset];
            if ((fc & 0x80) != 0)
            {
                if ((fc & 0x7F) != function)
                    throw new ModbusException(ErrCodes.BadFunction, "Exception response for function " + (fc & 0x7F) + ", expected " + function);
                int code = length > 1 ? b[offset + 1] : 0;
                throw ModbusException.FromSlaveException(code, "Slave exception " + code + " (" + ModbusCodec.ExceptionText(code) + ") for function " + function);
            }
            if (fc != function)
                throw new ModbusException(ErrCodes.BadFunction, "Function " + fc + " in response, expected " + function);
            byte[] pdu = new byte[length];
            Buffer.BlockCopy(b, offset, pdu, 0, length);
            return pdu;
        }

        int ReceiveMbap(byte[] buf, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int n = 0;
            int need = 7;
            long lastByteAt = 0;
            while (true)
            {
                CheckCancel();
                long el = sw.ElapsedMilliseconds;
                if (el >= timeoutMs) break;
                int slice = (int)Math.Min(timeoutMs - el, 20);
                if (slice < 1) slice = 1;
                int r = ReadSlice(buf, n, slice);
                if (r > 0)
                {
                    n += r;
                    lastByteAt = sw.ElapsedMilliseconds;
                    if (n >= 6)
                    {
                        int length = (buf[4] << 8) | buf[5];
                        need = 6 + length;
                        if (need > buf.Length) need = buf.Length;
                    }
                    if (n >= need && n >= 7) break;
                    if (n >= buf.Length) break;
                }
                else if (n > 0 && sw.ElapsedMilliseconds - lastByteAt >= Math.Max(200, InterCharTimeoutMs))
                {
                    break;
                }
            }
            return n;
        }

        byte[] ParseMbap(byte[] b, int n, ushort txId, byte unit, byte function)
        {
            if (n == 0)
                throw new ModbusException(ErrCodes.Timeout, "No response from slave " + unit + " within " + ResponseTimeoutMs + " ms");
            if (n < 8)
                throw new ModbusException(ErrCodes.ShortFrame, "Short MBAP response (" + n + " bytes): " + ModbusCodec.Hex(b, 0, n));
            int id = (b[0] << 8) | b[1];
            int proto = (b[2] << 8) | b[3];
            int length = (b[4] << 8) | b[5];
            if (proto != 0 || id != txId)
                throw new ModbusException(ErrCodes.BadTransaction, "MBAP mismatch: transaction " + id + " (expected " + txId + "), protocol " + proto);
            if (n < 6 + length)
                throw new ModbusException(ErrCodes.ShortFrame, "Incomplete MBAP response: got " + n + " bytes, header says " + (6 + length));
            if (b[6] != unit)
                throw new ModbusException(ErrCodes.BadSlave, "Response from unit " + b[6] + ", expected " + unit);
            return CheckPdu(b, 7, length - 1, function);
        }
    }
}
