using System;
using System.Collections.Generic;

namespace VA520Tester
{
    /// <summary>One readable value of the sensor.</summary>
    public class RegDef
    {
        public string Key;
        public string Name;
        public string Unit;
        /// <summary>Protocol address = column "Register Address" of the manual (Modbus register number - 1).</summary>
        public int Address;
        public RegType Type;
        public string Group;
        public bool DefaultEnabled;
        public bool IsCustom;

        public int RegCount { get { return ModbusCodec.RegisterCount(Type); } }
        /// <summary>Column "Modbus Register" of the manual.</summary>
        public int ModbusRegister { get { return Address + 1; } }
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// Register map of the CS Instruments VA 520 (manual V1.26, chapters 11.3.2.3 and 11.3.2.4).
    /// Function 03 (read holding registers) and 16 (write multiple registers).
    /// </summary>
    public static class Va520Map
    {
        public const int RegId = 2000;          // Modbus ID            UInt16 R/W  1..247 (default 1)
        public const int RegBaud = 2001;        // Baud rate            UInt16 R/W  0=1200 1=2400 2=4800 3=9600 4=19200 5=38400 (default 4)
        public const int RegParity = 2002;      // Parity               UInt16 R/W  0=none 1=even 2=odd (default 1)
        public const int RegStopBits = 2003;    // Number of stop bits  UInt16 R/W  0=1 stop bit 1=2 stop bits
        public const int RegWordOrder = 2004;   // Word order           UInt16 R/W  0xABCD=big endian, 0xCDAB=middle endian (default 0xABCD)

        public const ushort WordOrderABCD = 0xABCD;
        public const ushort WordOrderCDAB = 0xCDAB;

        public static readonly int[] BaudValues = new int[] { 1200, 2400, 4800, 9600, 19200, 38400 };
        public static readonly string[] ParityNames = new string[] { "0 = none", "1 = even", "2 = odd" };
        public static readonly string[] StopNames = new string[] { "0 = 1 stop bit", "1 = 2 stop bits" };

        public static readonly List<RegDef> Values = Build();

        static RegDef D(string key, string name, string unit, int addr, RegType type, string group, bool enabled)
        {
            RegDef d = new RegDef();
            d.Key = key; d.Name = name; d.Unit = unit; d.Address = addr; d.Type = type; d.Group = group; d.DefaultEnabled = enabled;
            return d;
        }

        static List<RegDef> Build()
        {
            List<RegDef> l = new List<RegDef>();
            const string F = "Flow", C = "Consumption", V = "Velocity", T = "Temperature";
            l.Add(D("flow_m3h", "Flow", "m3/h", 1100, RegType.Float32, F, true));
            l.Add(D("flow_Nm3h", "Flow (norm)", "Nm3/h", 1108, RegType.Float32, F, false));
            l.Add(D("flow_m3min", "Flow", "m3/min", 1116, RegType.Float32, F, false));
            l.Add(D("flow_Nm3min", "Flow (norm)", "Nm3/min", 1124, RegType.Float32, F, false));
            l.Add(D("flow_lh", "Flow", "ltr/h", 1132, RegType.Float32, F, false));
            l.Add(D("flow_Nlh", "Flow (norm)", "Nltr/h", 1140, RegType.Float32, F, false));
            l.Add(D("flow_lmin", "Flow", "ltr/min", 1148, RegType.Float32, F, false));
            l.Add(D("flow_Nlmin", "Flow (norm)", "Nltr/min", 1156, RegType.Float32, F, false));
            l.Add(D("flow_ls", "Flow", "ltr/s", 1164, RegType.Float32, F, false));
            l.Add(D("flow_Nls", "Flow (norm)", "Nltr/s", 1172, RegType.Float32, F, false));
            l.Add(D("flow_cfm", "Flow", "cfm", 1180, RegType.Float32, F, false));
            l.Add(D("flow_Ncfm", "Flow (norm)", "Ncfm", 1188, RegType.Float32, F, false));
            l.Add(D("flow_kgh", "Flow", "kg/h", 1196, RegType.Float32, F, false));
            l.Add(D("flow_kgmin", "Flow", "kg/min", 1204, RegType.Float32, F, false));
            l.Add(D("flow_kgs", "Flow", "kg/s", 1212, RegType.Float32, F, false));
            l.Add(D("flow_kW", "Flow", "kW", 1220, RegType.Float32, F, false));

            l.Add(D("cons_m3", "Consumption (before comma)", "m3", 1268, RegType.UInt32, C, true));
            l.Add(D("cons_Nm3", "Consumption (before comma)", "Nm3", 1274, RegType.UInt32, C, false));
            l.Add(D("cons_l", "Consumption (before comma)", "ltr", 1280, RegType.UInt32, C, false));
            l.Add(D("cons_Nl", "Consumption (before comma)", "Nltr", 1286, RegType.UInt32, C, false));
            l.Add(D("cons_cf", "Consumption (before comma)", "cf", 1292, RegType.UInt32, C, false));
            l.Add(D("cons_Ncf", "Consumption (before comma)", "Ncf", 1298, RegType.UInt32, C, false));
            l.Add(D("cons_kg", "Consumption (before comma)", "kg", 1304, RegType.UInt32, C, false));
            l.Add(D("cons_kWh", "Consumption (before comma)", "kWh", 1310, RegType.UInt32, C, false));

            l.Add(D("vel_ms", "Velocity", "m/s", 1346, RegType.Float32, V, true));
            l.Add(D("vel_Nms", "Velocity (norm)", "Nm/s", 1354, RegType.Float32, V, false));
            l.Add(D("vel_ftmin", "Velocity", "ft/min", 1362, RegType.Float32, V, false));
            l.Add(D("vel_Nftmin", "Velocity (norm)", "Nft/min", 1370, RegType.Float32, V, false));

            l.Add(D("temp_C", "Gas temperature", "°C", 1418, RegType.Float32, T, true));
            l.Add(D("temp_F", "Gas temperature", "°F", 1426, RegType.Float32, T, false));
            return l;
        }

        public static RegDef Find(string key)
        {
            foreach (RegDef d in Values) if (d.Key == key) return d;
            return null;
        }

        public static string BaudText(int code)
        {
            return code >= 0 && code < BaudValues.Length ? BaudValues[code].ToString() : "?(" + code + ")";
        }

        // ------------------------------------------------------------------ 4..20 mA scaling table (manual chapter 6)

        public class Scale
        {
            public string Name;
            public double FullScale;
            public string Unit;
            public Scale(string name, double fs, string unit) { Name = name; FullScale = fs; Unit = unit; }
            public override string ToString() { return Name + "  (20 mA = " + FullScale + " " + Unit + ")"; }
        }

        /// <summary>Analogue output scaling "Compressed air", reference 20 C / 1000 mbar, 4..20 mA = 0..full scale.</summary>
        public static readonly Scale[] Scales = BuildScales();

        static Scale[] BuildScales()
        {
            List<Scale> l = new List<Scale>();
            Action<string, double, double, double, double, string> add = delegate(string sz, double low, double std, double max, double high, string unit)
            {
                l.Add(new Scale(sz + " Standard", std, unit));
                l.Add(new Scale(sz + " Low speed", low, unit));
                l.Add(new Scale(sz + " Max", max, unit));
                l.Add(new Scale(sz + " High speed", high, unit));
            };
            add("1/4\"", 25, 50, 105, 130, "l/min");
            add("1/2\"", 20, 45, 90, 110, "m3/h");
            add("3/4\"", 45, 85, 175, 215, "m3/h");
            add("1\"", 75, 145, 290, 355, "m3/h");
            add("1 1/4\"", 140, 265, 530, 640, "m3/h");
            add("1 1/2\"", 195, 365, 730, 885, "m3/h");
            add("2\"", 320, 600, 1195, 1450, "m3/h");
            add("2 1/2\"", 550, 1025, 2050, 2480, "m3/h");
            add("3\"", 765, 1420, 2840, 3440, "m3/h");
            return l.ToArray();
        }
    }
}
