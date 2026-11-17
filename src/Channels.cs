using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace VA520Tester
{
    /// <summary>Raw byte pipe to the sensor (serial port or TCP socket).</summary>
    public interface IByteChannel : IDisposable
    {
        string Description { get; }
        bool IsOpen { get; }
        void Open();
        void Close();
        void Write(byte[] buf, int offset, int count);
        /// <summary>Reads up to count bytes, waiting at most timeoutMs. Returns 0 when nothing arrived.</summary>
        int Read(byte[] buf, int offset, int count, int timeoutMs);
        void DiscardInput();
    }

    public class SerialSettings
    {
        public string PortName = "COM1";
        public int BaudRate = 19200;
        public int DataBits = 8;
        public Parity Parity = Parity.Even;
        public StopBits StopBits = StopBits.One;
        public bool DtrEnable;
        /// <summary>Keep RTS asserted (high) all the time - some converters (and Qt based tools) do that.</summary>
        public bool RtsStatic;
        /// <summary>RS485 direction control through RTS (adapters that need it).</summary>
        public bool RtsToggle;
        public bool RtsActiveHigh = true;
        public int RtsPreDelayMs = 1;
        public int RtsPostDelayMs = 1;
    }

    /// <summary>
    /// Serial port. Uses only the basic SerialPort API (synchronous Read/Write, no DataReceived event)
    /// because that is the part that behaves identically on Windows and on Mono (Linux).
    /// </summary>
    public class SerialChannel : IByteChannel
    {
        readonly SerialSettings s;
        SerialPort port;

        public SerialChannel(SerialSettings settings)
        {
            s = settings;
        }

        public string Description
        {
            get
            {
                string p = s.Parity == Parity.None ? "N" : (s.Parity == Parity.Even ? "E" : (s.Parity == Parity.Odd ? "O" : s.Parity.ToString()));
                string sb = s.StopBits == StopBits.Two ? "2" : (s.StopBits == StopBits.OnePointFive ? "1.5" : "1");
                return s.PortName + " " + s.BaudRate + " " + s.DataBits + p + sb;
            }
        }

        public bool IsOpen { get { return port != null && port.IsOpen; } }

        public void Open()
        {
            Close();
            try
            {
                SerialPort p = new SerialPort(s.PortName, s.BaudRate, s.Parity, s.DataBits, s.StopBits);
                p.Handshake = Handshake.None;
                p.ReadTimeout = 20;
                p.WriteTimeout = 1000;
                p.DtrEnable = s.DtrEnable;
                p.Open();
                if (s.RtsToggle)
                {
                    p.RtsEnable = !s.RtsActiveHigh;   // idle = receive
                }
                else if (s.RtsStatic)
                {
                    p.RtsEnable = true;
                }
                port = p;
                try { p.DiscardInBuffer(); p.DiscardOutBuffer(); } catch { }
            }
            catch (Exception ex)
            {
                port = null;
                throw new ModbusException(ErrCodes.PortOpen, "Cannot open " + s.PortName + ": " + ex.Message, ex);
            }
        }

        public void Close()
        {
            SerialPort p = port;
            port = null;
            if (p == null) return;
            try { if (p.IsOpen) p.Close(); } catch { }
            try { p.Dispose(); } catch { }
        }

        public void Write(byte[] buf, int offset, int count)
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) throw new IOException("Port is not open");
            if (s.RtsToggle)
            {
                p.RtsEnable = s.RtsActiveHigh;
                if (s.RtsPreDelayMs > 0) Thread.Sleep(s.RtsPreDelayMs);
            }
            p.Write(buf, offset, count);
            if (s.RtsToggle)
            {
                // wait until the last bit has left the UART (11 bits per character, generous)
                double ms = count * 11.0 * 1000.0 / Math.Max(300, s.BaudRate);
                Thread.Sleep((int)Math.Ceiling(ms) + Math.Max(0, s.RtsPostDelayMs));
                p.RtsEnable = !s.RtsActiveHigh;
            }
        }

        public int Read(byte[] buf, int offset, int count, int timeoutMs)
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) throw new IOException("Port is not open");
            try
            {
                p.ReadTimeout = Math.Max(1, timeoutMs);
                return p.Read(buf, offset, count);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void DiscardInput()
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) return;
            try { p.DiscardInBuffer(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>Serial port names available on this machine (COMx on Windows, /dev/tty* on Linux/Mono).</summary>
        public static List<string> ListPorts()
        {
            List<string> r = new List<string>();
            try
            {
                foreach (string n in SerialPort.GetPortNames())
                    if (!r.Contains(n)) r.Add(n);
            }
            catch { }
            try
            {
                if (Path.DirectorySeparatorChar == '/')
                {
                    // Mono does not list every device (e.g. /dev/serial0) - add what exists
                    string[] prefixes = new string[] { "ttyUSB", "ttyACM", "ttyAMA", "ttyS", "serial", "rfcomm" };
                    foreach (string f in Directory.GetFileSystemEntries("/dev"))
                    {
                        string name = Path.GetFileName(f);
                        foreach (string pre in prefixes)
                        {
                            if (name.StartsWith(pre, StringComparison.Ordinal) && !r.Contains(f))
                            {
                                r.Add(f);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
            r.Sort(StringComparer.Ordinal);
            return r;
        }
    }

    /// <summary>TCP socket (Modbus TCP, or RTU frames through a serial-to-ethernet gateway).</summary>
    public class TcpChannel : IByteChannel
    {
        readonly string host;
        readonly int tcpPort;
        readonly int connectTimeoutMs;
        TcpClient client;
        NetworkStream stream;

        public TcpChannel(string host, int port, int connectTimeoutMs)
        {
            this.host = host;
            tcpPort = port;
            this.connectTimeoutMs = connectTimeoutMs;
        }

        public string Description { get { return host + ":" + tcpPort; } }

        public bool IsOpen { get { return client != null && client.Connected; } }

        public void Open()
        {
            Close();
            try
            {
                TcpClient c = new TcpClient();
                IAsyncResult ar = c.BeginConnect(host, tcpPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(Math.Max(100, connectTimeoutMs)))
                {
                    try { c.Close(); } catch { }
                    throw new TimeoutException("connect timeout");
                }
                c.EndConnect(ar);
                c.NoDelay = true;
                client = c;
                stream = c.GetStream();
            }
            catch (Exception ex)
            {
                client = null;
                stream = null;
                throw new ModbusException(ErrCodes.TcpConnect, "Cannot connect to " + host + ":" + tcpPort + ": " + ex.Message, ex);
            }
        }

        public void Close()
        {
            TcpClient c = client;
            client = null;
            stream = null;
            if (c == null) return;
            try { c.Close(); } catch { }
        }

        public void Write(byte[] buf, int offset, int count)
        {
            NetworkStream st = stream;
            if (st == null) throw new IOException("Socket is not connected");
            st.Write(buf, offset, count);
            st.Flush();
        }

        public int Read(byte[] buf, int offset, int count, int timeoutMs)
        {
            TcpClient c = client;
            NetworkStream st = stream;
            if (c == null || st == null) throw new IOException("Socket is not connected");
            if (!c.Client.Poll(Math.Max(1, timeoutMs) * 1000, SelectMode.SelectRead))
                return 0;
            int n = st.Read(buf, offset, count);
            if (n == 0) throw new IOException("Connection closed by the remote side");
            return n;
        }

        public void DiscardInput()
        {
            TcpClient c = client;
            NetworkStream st = stream;
            if (c == null || st == null) return;
            try
            {
                byte[] tmp = new byte[256];
                while (c.Available > 0) st.Read(tmp, 0, tmp.Length);
            }
            catch { }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
