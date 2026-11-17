// Battery readout core, ported from Wireless Battery (github.com/krisgfx9/Wireless-Device-Battery-Monitor,
// GPL-3.0-or-later): the device model, the Win32 HID layer and the poll context every driver works on.
//
// Report buffers follow hidapi conventions, like the original, so protocol code maps 1:1:
//  * Write / SetFeature: data[0] is the report id (0 when the device doesn't number its reports); the buffer
//    is zero-padded to the report length.
//  * Read: a leading 0 report id is stripped, so data[0] is the first payload byte for unnumbered reports and
//    the report id for numbered ones.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KomorebiTray
{
    enum DeviceKind { Other, Mouse, Headset, Keyboard, Gamepad, Phone }

    // One battery-powered device, as seen during a single poll
    class DeviceStatus
    {
        public string Key = "";        // stable identity across polls
        public string Name = "";       // human readable name
        public string Via = "";        // "2.4 GHz receiver", "Bluetooth", "USB cable", ...
        public DeviceKind Kind = DeviceKind.Other;
        public int Percent = -1;       // 0..100, or -1 when unknown right now
        public string Detail = "";     // extra breakdown, e.g. "L 80%, R 75%, case 40%"
        public string LevelText = "";  // set when the device only reports coarse levels ("Low", "Full"...)
        public bool Charging;
        public bool Asleep;            // receiver answered but the device is sleeping (keep last value)
        public bool Connected = true;  // false: receiver present, device switched off (hidden)
        public int Millivolts;         // battery voltage when reported (0 = unknown)
        public int GraceSec;           // keep showing the last value this long if the device stops answering
        public bool NeedsLevel;        // komorebi-tray: listed in the menu and window, but on the taskbar only once a level is known

        public DeviceStatus Copy() { return (DeviceStatus)MemberwiseClone(); }
    }

    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    class HidColl
    {
        public string Path = "";
        public ushort Vid, Pid;
        public int Iface = -1;       // USB interface number (MI_xx in the path), -1 when unknown
        public HIDP_CAPS Caps;
        public ushort Page { get { return Caps.UsagePage; } }
        public ushort Usage { get { return Caps.Usage; } }
        public int InLen { get { return Caps.InputReportByteLength; } }
        public int OutLen { get { return Caps.OutputReportByteLength; } }
        public int FeatLen { get { return Caps.FeatureReportByteLength; } }
    }

    static class HidNative
    {
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder b, int bytes);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetManufacturerString(SafeFileHandle h, StringBuilder b, int bytes);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, StringBuilder b, int bytes);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, int len);
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIo(SafeFileHandle h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initial, IntPtr name);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, int ms);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        // HIDP_VALUE_CAPS (72 bytes); Range and NotRange share the union at the end
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
            public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
            public byte Reserved;
            public ushort BitSize, ReportCount;
            public ushort R1, R2, R3, R4, R5;
            public uint UnitsExp, Units;
            public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }
    }

    static class Hid
    {
        public static List<HidColl> Enumerate()
        {
            var output = new List<HidColl>();
            Guid g;
            HidNative.HidD_GetHidGuid(out g);
            IntPtr di = HidNative.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);   // PRESENT | DEVICEINTERFACE
            if (di == new IntPtr(-1)) return output;
            try
            {
                var ifd = new HidNative.SP_DEVICE_INTERFACE_DATA();
                ifd.cbSize = Marshal.SizeOf(ifd);
                for (int i = 0; HidNative.SetupDiEnumDeviceInterfaces(di, IntPtr.Zero, ref g, i, ref ifd); i++)
                {
                    int need;
                    HidNative.SetupDiGetDeviceInterfaceDetail(di, ref ifd, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (HidNative.SetupDiGetDeviceInterfaceDetail(di, ref ifd, buf, need, out need, IntPtr.Zero))
                            path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    if (path == null) continue;
                    using (SafeFileHandle h = HidNative.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var c = new HidColl { Path = path };
                        var a = new HidNative.HIDD_ATTRIBUTES();
                        a.Size = Marshal.SizeOf(a);
                        if (HidNative.HidD_GetAttributes(h, ref a)) { c.Vid = a.VendorID; c.Pid = a.ProductID; }
                        IntPtr pp;
                        if (HidNative.HidD_GetPreparsedData(h, out pp)) { HidNative.HidP_GetCaps(pp, out c.Caps); HidNative.HidD_FreePreparsedData(pp); }
                        string lower = path.ToLowerInvariant();
                        int mi = lower.IndexOf("&mi_");
                        if (mi >= 0) c.Iface = ParseHexPrefix(lower, mi + 4);
                        output.Add(c);
                    }
                }
            }
            finally { HidNative.SetupDiDestroyDeviceInfoList(di); }
            return output;
        }

        static int ParseHexPrefix(string s, int at)
        {
            int v = 0, n = 0;
            for (int i = at; i < s.Length; i++, n++)
            {
                int d = Uri.IsHexDigit(s[i]) ? Convert.ToInt32(s[i].ToString(), 16) : -1;
                if (d < 0) break;
                v = v * 16 + d;
            }
            return n == 0 ? -1 : v;
        }

        public static string Product(HidColl c)
        {
            using (SafeFileHandle h = HidNative.CreateFile(c.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return "";
                var sb = new StringBuilder(128);
                if (!HidNative.HidD_GetProductString(h, sb, 254)) return "";
                return sb.ToString().TrimEnd();
            }
        }

        public static string Manufacturer(HidColl c)
        {
            using (SafeFileHandle h = HidNative.CreateFile(c.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return "";
                var sb = new StringBuilder(128);
                if (!HidNative.HidD_GetManufacturerString(h, sb, 254)) return "";
                return sb.ToString().TrimEnd();
            }
        }
    }

    // One open HID collection
    class HidDev : IDisposable
    {
        SafeFileHandle h;
        bool overlapped;
        HidColl c = new HidColl();

        public bool Ok { get { return h != null && !h.IsInvalid && !h.IsClosed; } }
        public HidColl Coll { get { return c; } }

        bool OpenWith(HidColl coll, uint access, bool ov)
        {
            Close();
            c = coll;
            h = HidNative.CreateFile(coll.Path, access, 3, IntPtr.Zero, 3, ov ? 0x40000000u : 0, IntPtr.Zero);
            overlapped = ov;
            return Ok;
        }

        public bool Open(HidColl coll) { return OpenWith(coll, 0xC0000000, true); }              // read/write, overlapped
        public bool OpenReadOnly(HidColl coll) { return OpenWith(coll, 0x80000000, true); }      // push-only collections another app may hold
        public bool OpenFeatureOnly(HidColl coll) { return OpenWith(coll, 0, false); }           // feature reports; works on mice / keyboards

        public void Close() { if (h != null) { h.Dispose(); h = null; } }
        public void Dispose() { Close(); }

        public bool Write(byte[] data, int timeoutMs = 500)
        {
            if (!Ok || !overlapped || c.OutLen == 0 || data == null || data.Length == 0) return false;
            var buf = new byte[c.OutLen];
            Array.Copy(data, buf, Math.Min(buf.Length, data.Length));
            int n;
            return Io(false, buf, timeoutMs, out n);
        }

        // Reads one input report into buf. Returns the byte count, or -1 on timeout / error.
        public int Read(ref byte[] buf, int timeoutMs)
        {
            if (!Ok || !overlapped || c.InLen == 0) return -1;
            var tmp = new byte[c.InLen];
            int got;
            if (!Io(true, tmp, timeoutMs, out got) || got == 0) return -1;
            int off = tmp[0] == 0 ? 1 : 0;   // hidapi semantics: drop the "no report id" byte
            buf = new byte[got - off];
            Array.Copy(tmp, off, buf, 0, got - off);
            return buf.Length;
        }

        // For collections without an interrupt OUT pipe: arms a read, sends data with HidD_SetOutputReport,
        // and returns the first input report like Read does
        public int SetOutputAndRead(byte[] data, ref byte[] buf, int timeoutMs)
        {
            if (!Ok || !overlapped || c.InLen == 0 || c.OutLen == 0 || data == null || data.Length == 0) return -1;
            var outBuf = new byte[c.OutLen];
            Array.Copy(data, outBuf, Math.Min(outBuf.Length, data.Length));
            var tmp = new byte[c.InLen];
            int got;
            bool sent = false;
            bool done = Io(true, tmp, timeoutMs, out got, () => { sent = HidNative.HidD_SetOutputReport(h, outBuf, outBuf.Length); return sent; });
            if (!done || !sent || got == 0) return -1;
            int off = tmp[0] == 0 ? 1 : 0;
            buf = new byte[got - off];
            Array.Copy(tmp, off, buf, 0, got - off);
            return buf.Length;
        }

        // One overlapped ReadFile / WriteFile. `between` runs after the I/O is armed (SetOutputAndRead)
        bool Io(bool read, byte[] data, int timeoutMs, out int transferred, Func<bool> between = null)
        {
            transferred = 0;
            int ovSize = Marshal.SizeOf(typeof(NativeOverlapped));
            IntPtr ov = Marshal.AllocHGlobal(ovSize), mem = Marshal.AllocHGlobal(data.Length);
            IntPtr ev = HidNative.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                for (int i = 0; i < ovSize; i++) Marshal.WriteByte(ov, i, 0);
                Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, ev);
                if (!read) Marshal.Copy(data, 0, mem, data.Length);
                bool started = read ? HidNative.ReadFile(h, mem, data.Length, IntPtr.Zero, ov) : HidNative.WriteFile(h, mem, data.Length, IntPtr.Zero, ov);
                if (!started && Marshal.GetLastWin32Error() != 997) return false;   // ERROR_IO_PENDING
                bool go = between == null || between();
                bool done;
                if (go && timeoutMs > 0 && HidNative.WaitForSingleObject(ev, timeoutMs) == 0)
                    done = HidNative.GetOverlappedResult(h, ov, out transferred, false);
                else
                {
                    HidNative.CancelIo(h);
                    done = HidNative.GetOverlappedResult(h, ov, out transferred, true) && go;   // it may have completed meanwhile
                }
                if (done && read && transferred > 0) Marshal.Copy(mem, data, 0, Math.Min(transferred, data.Length));
                return done;
            }
            finally
            {
                HidNative.CloseHandle(ev);
                Marshal.FreeHGlobal(mem);
                Marshal.FreeHGlobal(ov);
            }
        }

        public void Flush() { if (Ok) HidNative.HidD_FlushQueue(h); }

        public bool SetFeature(byte[] data)
        {
            if (!Ok || c.FeatLen == 0) return false;
            var buf = new byte[c.FeatLen];
            Array.Copy(data, buf, Math.Min(buf.Length, data.Length));
            return HidNative.HidD_SetFeature(h, buf, buf.Length);
        }

        // out[0] = report id
        public bool GetFeature(byte reportId, out byte[] output)
        {
            output = null;
            if (!Ok || c.FeatLen == 0) return false;
            output = new byte[c.FeatLen];
            output[0] = reportId;
            return HidNative.HidD_GetFeature(h, output, output.Length);
        }

        // Current input report via the control pipe; out[0] = report id
        public bool GetInputReport(byte reportId, out byte[] output)
        {
            output = null;
            if (!Ok || c.InLen == 0) return false;
            output = new byte[c.InLen];
            output[0] = reportId;
            return HidNative.HidD_GetInputReport(h, output, output.Length);
        }
    }

    // What every driver works on: the enumerated collections, and the devices already handled (claimed) so the
    // generic fallbacks further down the chain don't report them twice
    class PollContext
    {
        public List<HidColl> Colls = new List<HidColl>();
        public HashSet<uint> Claimed = new HashSet<uint>();
        public HashSet<string> BtContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<DeviceStatus> Out = new List<DeviceStatus>();

        static uint Id(ushort vid, ushort pid) { return ((uint)vid << 16) | pid; }
        public bool IsClaimed(ushort vid, ushort pid) { return Claimed.Contains(Id(vid, pid)); }
        public void Claim(ushort vid, ushort pid) { Claimed.Add(Id(vid, pid)); }
        public void Add(DeviceStatus s, ushort vid, ushort pid) { Out.Add(s); Claim(vid, pid); }

        // Distinct (vid, pid) pairs of a vendor that are not claimed yet, in ascending order
        public List<ushort[]> Products(ushort vid)
        {
            var set = new SortedSet<uint>();
            foreach (HidColl c in Colls)
                if (c.Vid == vid && !IsClaimed(c.Vid, c.Pid)) set.Add(Id(c.Vid, c.Pid));
            var list = new List<ushort[]>();
            foreach (uint id in set) list.Add(new[] { (ushort)(id >> 16), (ushort)(id & 0xFFFF) });
            return list;
        }

        public List<HidColl> Find(ushort vid, ushort pid, Func<HidColl, bool> pred)
        {
            var r = new List<HidColl>();
            foreach (HidColl c in Colls)
                if (c.Vid == vid && c.Pid == pid && (pred == null || pred(c))) r.Add(c);
            return r;
        }
    }

    static class Bat
    {
        public static string Hex4(int v) { return (v & 0xFFFF).ToString("X4"); }
        public static string Hex2(int v) { return (v & 0xFF).ToString("X2"); }
        public static int Clamp100(int v) { return v < 0 ? 0 : v > 100 ? 100 : v; }
        public static int MapTo100(int v, int lo, int hi) { return hi <= lo ? Clamp100(v) : Clamp100((v - lo) * 100 / (hi - lo)); }

        // Piecewise-linear interpolation through (mv[i], pct[i]); tables sorted by descending voltage
        public static int Interpolate(int mv, int[] mvTable, int[] pctTable)
        {
            int n = mvTable.Length;
            if (mv >= mvTable[0]) return pctTable[0];
            for (int i = 1; i < n; i++)
                if (mv >= mvTable[i])
                {
                    int span = mvTable[i - 1] - mvTable[i];
                    return pctTable[i] + (pctTable[i - 1] - pctTable[i]) * (mv - mvTable[i]) / (span != 0 ? span : 1);
                }
            return pctTable[n - 1];
        }

        public static DeviceKind GuessKind(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            Func<string, bool> has = s => n.Contains(s);
            if (has("mouse") || has("mice") || has("trackball")) return DeviceKind.Mouse;
            if (has("keyboard") || has("keys")) return DeviceKind.Keyboard;
            if (has("controller") || has("gamepad") || has("xbox") || has("dualsense") || has("dualshock") || has("joy-con")) return DeviceKind.Gamepad;
            if (has("head") || has("buds") || has("pods") || has("ear") || has("wh-") || has("wf-") || has("audio") ||
                has("speaker") || has("arctis") || has("cloud") || has("void") || has("virtuoso")) return DeviceKind.Headset;
            return DeviceKind.Other;
        }

        // Device type from the top-level collections a device exposes (mouse > gamepad > keyboard)
        public static DeviceKind KindFromCollections(List<HidColl> all, ushort vid, ushort pid)
        {
            bool mouse = false, pad = false, kbd = false;
            foreach (HidColl c in all)
            {
                if (c.Vid != vid || c.Pid != pid || c.Page != 0x01) continue;
                mouse |= c.Usage == 0x02;
                pad |= c.Usage == 0x04 || c.Usage == 0x05;
                kbd |= c.Usage == 0x06;
            }
            if (mouse) return DeviceKind.Mouse;
            if (pad) return DeviceKind.Gamepad;
            if (kbd) return DeviceKind.Keyboard;
            return DeviceKind.Other;
        }

        // Byte-array literal: B(0x11, 0xFF, 0x00)
        public static byte[] B(params int[] v)
        {
            var r = new byte[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = (byte)v[i];
            return r;
        }

        public static byte At(byte[] b, int i) { return b != null && i >= 0 && i < b.Length ? b[i] : (byte)0; }

        public static uint Tick { get { return (uint)Environment.TickCount; } }
        public static long Now { get { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency; } }   // ms, monotonic
        public static void Sleep(int ms) { Thread.Sleep(ms); }
    }
}
