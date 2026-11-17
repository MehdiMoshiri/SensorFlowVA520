using System;
using System.Text;

namespace VA520Tester
{
    /// <summary>Error numbers used in the error log (column "Code").</summary>
    public static class ErrCodes
    {
        public const int General = 1000;
        public const int PortOpen = 1001;
        public const int Timeout = 1002;
        public const int Crc = 1003;
        public const int BadSlave = 1004;
        public const int BadFunction = 1005;
        public const int ShortFrame = 1006;
        public const int PortIo = 1007;
        public const int TcpConnect = 1008;
        public const int BadTransaction = 1009;
        public const int InvalidValue = 1010;
        public const int OutOfLimit = 1011;
        public const int WriteFailed = 1012;
        public const int Unhandled = 1013;
        public const int Settings = 1014;
        public const int CsvLog = 1015;
        public const int NotConnected = 1016;
        public const int Cancelled = 1017;
        public const int TestFailed = 1018;
        public const int Simulator = 1019;
        /// <summary>1100 + Modbus exception code (1101 = illegal function, 1102 = illegal data address, ...)</summary>
        public const int ModbusExceptionBase = 1100;

        public static string Describe(int code)
        {
            switch (code)
            {
                case General: return "General error";
                case PortOpen: return "Cannot open port";
                case Timeout: return "Timeout (no response)";
                case Crc: return "CRC error";
                case BadSlave: return "Response from wrong slave address";
                case BadFunction: return "Wrong function code in response";
                case ShortFrame: return "Short / invalid frame";
                case PortIo: return "Port I/O error";
                case TcpConnect: return "TCP connect error";
                case BadTransaction: return "Bad MBAP transaction/protocol id";
                case InvalidValue: return "Invalid value (NaN/Infinity)";
                case OutOfLimit: return "Value outside limit";
                case WriteFailed: return "Write failed";
                case Unhandled: return "Unhandled exception";
                case Settings: return "Settings file error";
                case CsvLog: return "CSV log error";
                case NotConnected: return "Not connected";
                case Cancelled: return "Cancelled";
                case TestFailed: return "Self test failed";
                case Simulator: return "Simulator error";
            }
            if (code > ModbusExceptionBase && code < ModbusExceptionBase + 100)
                return "Modbus exception " + (code - ModbusExceptionBase);
            return "Error " + code;
        }
    }

    /// <summary>Communication error. Code is one of ErrCodes.</summary>
    public class ModbusException : Exception
    {
        public readonly int Code;
        /// <summary>1..11 when the slave answered with a Modbus exception response, otherwise 0.</summary>
        public readonly int ExceptionCode;

        public ModbusException(int code, string message) : base(message)
        {
            Code = code;
        }

        public ModbusException(int code, string message, Exception inner) : base(message, inner)
        {
            Code = code;
        }

        ModbusException(int code, int exceptionCode, string message)
            : base(message)
        {
            Code = code;
            ExceptionCode = exceptionCode;
        }

        /// <summary>The slave answered with a Modbus exception response.</summary>
        public static ModbusException FromSlaveException(int exceptionCode, string message)
        {
            return new ModbusException(ErrCodes.ModbusExceptionBase + exceptionCode, exceptionCode, message);
        }

        public bool IsModbusException { get { return ExceptionCode != 0; } }

        /// <summary>True when sending the same request again may help.</summary>
        public bool IsRetryable
        {
            get
            {
                return Code == ErrCodes.Timeout || Code == ErrCodes.Crc || Code == ErrCodes.ShortFrame
                    || Code == ErrCodes.BadSlave || Code == ErrCodes.BadFunction || Code == ErrCodes.BadTransaction;
            }
        }

        /// <summary>True when the link itself looks broken (port gone, socket closed).</summary>
        public bool IsLinkError
        {
            get { return Code == ErrCodes.PortIo || Code == ErrCodes.TcpConnect || Code == ErrCodes.NotConnected; }
        }
    }

    /// <summary>Order of the 4 bytes of a 32 bit value on the wire (A is the most significant byte).</summary>
    public enum WordOrder
    {
        ABCD = 0,   // big endian  (sensor setting 0xABCD, factory default)
        CDAB = 1,   // middle endian / word swapped (sensor setting 0xCDAB)
        BADC = 2,   // byte swapped
        DCBA = 3    // little endian
    }

    public enum RegType
    {
        Float32,
        UInt32,
        Int32,
        UInt16,
        Int16
    }

    public static class ModbusCodec
    {
        public static int RegisterCount(RegType t)
        {
            return (t == RegType.UInt16 || t == RegType.Int16) ? 1 : 2;
        }

        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 1) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc = (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        /// <summary>Total length of an RTU response frame, 0 = need more bytes, -1 = unknown function (rely on silence).</summary>
        public static int ExpectedRtuResponseLength(byte[] b, int count)
        {
            if (count < 2) return 0;
            byte fc = b[1];
            if ((fc & 0x80) != 0) return 5;
            switch (fc)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                case 23:
                    if (count < 3) return 0;
                    return 5 + b[2];
                case 5:
                case 6:
                case 15:
                case 16:
                    return 8;
                default:
                    return -1;
            }
        }

        /// <summary>Total length of an RTU request frame (used by the simulator).</summary>
        public static int ExpectedRtuRequestLength(byte[] b, int count)
        {
            if (count < 2) return 0;
            byte fc = b[1];
            switch (fc)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                    return 8;
                case 15:
                case 16:
                    if (count < 7) return 0;
                    return 9 + b[6];
                default:
                    return -1;
            }
        }

        // ----- 32 bit value <-> 4 wire bytes ----------------------------------------------

        static uint ToUInt(byte[] w, int o, WordOrder order)
        {
            byte a, b, c, d;
            switch (order)
            {
                case WordOrder.CDAB: a = w[o + 2]; b = w[o + 3]; c = w[o]; d = w[o + 1]; break;
                case WordOrder.BADC: a = w[o + 1]; b = w[o]; c = w[o + 3]; d = w[o + 2]; break;
                case WordOrder.DCBA: a = w[o + 3]; b = w[o + 2]; c = w[o + 1]; d = w[o]; break;
                default: a = w[o]; b = w[o + 1]; c = w[o + 2]; d = w[o + 3]; break;
            }
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
        }

        public static byte[] FromUInt(uint v, WordOrder order)
        {
            byte a = (byte)(v >> 24), b = (byte)(v >> 16), c = (byte)(v >> 8), d = (byte)v;
            switch (order)
            {
                case WordOrder.CDAB: return new byte[] { c, d, a, b };
                case WordOrder.BADC: return new byte[] { b, a, d, c };
                case WordOrder.DCBA: return new byte[] { d, c, b, a };
                default: return new byte[] { a, b, c, d };
            }
        }

        public static float ToFloat(byte[] w, int o, WordOrder order)
        {
            uint u = ToUInt(w, o, order);
            return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        }

        public static byte[] FromFloat(float f, WordOrder order)
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return FromUInt(u, order);
        }

        public static ushort ToUInt16(byte[] w, int o)
        {
            return (ushort)((w[o] << 8) | w[o + 1]);
        }

        /// <summary>Decodes the register data (big endian words as sent by Modbus) into a number.</summary>
        public static double Decode(RegType t, byte[] data, int offset, WordOrder order)
        {
            switch (t)
            {
                case RegType.Float32: return ToFloat(data, offset, order);
                case RegType.UInt32: return ToUInt(data, offset, order);
                case RegType.Int32: return (int)ToUInt(data, offset, order);
                case RegType.UInt16: return ToUInt16(data, offset);
                case RegType.Int16: return (short)ToUInt16(data, offset);
            }
            return double.NaN;
        }

        public static string Hex(byte[] b, int offset, int count)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[offset + i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static string Hex(byte[] b)
        {
            return b == null ? "" : Hex(b, 0, b.Length);
        }

        /// <summary>Parses "01 03 04 4C", "0x01,0x03" or "0103044C" into bytes. Returns null when invalid.</summary>
        public static byte[] ParseHex(string text)
        {
            if (text == null) return null;
            StringBuilder clean = new StringBuilder();
            string t = text.Replace("0x", " ").Replace("0X", " ");
            foreach (char ch in t)
            {
                if (Uri.IsHexDigit(ch)) clean.Append(ch);
                else if (ch == ' ' || ch == ',' || ch == ';' || ch == '-' || ch == ':' || ch == '\t' || ch == '\r' || ch == '\n') { /* separator */ }
                else return null;
            }
            if (clean.Length == 0 || clean.Length % 2 != 0) return null;
            byte[] r = new byte[clean.Length / 2];
            for (int i = 0; i < r.Length; i++)
                r[i] = Convert.ToByte(clean.ToString(i * 2, 2), 16);
            return r;
        }

        public static string ExceptionText(int code)
        {
            switch (code)
            {
                case 1: return "Illegal function";
                case 2: return "Illegal data address";
                case 3: return "Illegal data value";
                case 4: return "Slave device failure";
                case 5: return "Acknowledge";
                case 6: return "Slave device busy";
                case 8: return "Memory parity error";
                case 10: return "Gateway path unavailable";
                case 11: return "Gateway target device failed to respond";
            }
            return "Unknown exception";
        }
    }
}
