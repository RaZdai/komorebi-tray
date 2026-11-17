// Battery levels of wireless mice, keyboards, controllers and headsets, read straight from the devices
// (no vendor software) and shown as small numbers in the notification area next to the komorebi icon.
//
// Each reader below is written from public protocol notes; the source is named above it:
//   * Xenta 2.4 GHz receivers (Attack Shark, Delux): status frames the receiver broadcasts by itself
//   * Switch Pro protocol (8BitDo pads in Switch mode, Pro Controller): subcommand replies over Bluetooth
//   * SONiX 2.4 GHz keyboard dongles (AJAZZ AK820 / AK980, AULA): the dongle's battery command
//   * Logitech HID++ 2.0 receivers and Bluetooth devices
//   * Razer wireless mice, keyboards and headsets
//   * DualShock 4 / DualSense: the status byte inside their normal input reports
//   * Xbox controllers through XInput (coarse levels)
//   * Any Bluetooth device Windows itself knows a battery level for
// C# 5 / .NET Framework 4.8, like the rest of the tray.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace KomorebiTray
{
    enum DeviceKind { Mouse, Keyboard, Gamepad, Headset, Other }

    class BatteryInfo
    {
        public string Key, Name, Via;
        public DeviceKind Kind = DeviceKind.Other;
        public int Percent = -1;        // -1: no level known
        public bool Approx;             // the device only reports steps (tens, quarters, low/medium/full)
        public bool Charging, Asleep;   // Asleep: not answering right now, Percent is the last known value
        public string Note;             // shown in the menu when there is no level to show

        public string LevelText()
        {
            if (Percent < 0) return Note ?? (Charging ? "charging" : "no reading");
            string s = (Approx ? "about " : "") + Percent + "%";
            if (Charging) s += Percent >= 100 ? ", full" : ", charging";
            if (Asleep) s += " (asleep)";
            return s;
        }
    }

    // ---- HID access

    class HidInfo
    {
        public string Path, Product = "";
        public ushort Vid, Pid, Page, Usage;
        public int InLen, OutLen, FeatLen, Interface = -1;
    }

    static class Hid
    {
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder b, int bytes);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)] struct INTERFACE_DATA { public int cbSize; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        struct CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        // Every HID collection that is present. Opened with no access rights just to read its description,
        // which Windows allows even for mice and keyboards it holds itself.
        public static List<HidInfo> Enumerate()
        {
            var list = new List<HidInfo>();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (set == new IntPtr(-1)) return list;
            try
            {
                var data = new INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(data);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
                {
                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(required);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out required, IntPtr.Zero))
                            path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    if (path == null) continue;
                    HidInfo info = Describe(path);
                    if (info != null) list.Add(info);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static HidInfo Describe(string path)
        {
            using (SafeFileHandle h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var a = new ATTRIBUTES();
                a.Size = Marshal.SizeOf(a);
                if (!HidD_GetAttributes(h, ref a)) return null;
                var info = new HidInfo { Path = path, Vid = a.VendorID, Pid = a.ProductID };
                IntPtr pre;
                if (HidD_GetPreparsedData(h, out pre))
                {
                    CAPS c;
                    if (HidP_GetCaps(pre, out c) == 0x00110000)   // HIDP_STATUS_SUCCESS
                    {
                        info.Page = c.UsagePage; info.Usage = c.Usage;
                        info.InLen = c.InputReportByteLength; info.OutLen = c.OutputReportByteLength; info.FeatLen = c.FeatureReportByteLength;
                    }
                    HidD_FreePreparsedData(pre);
                }
                var sb = new StringBuilder(128);
                if (HidD_GetProductString(h, sb, sb.Capacity * 2)) info.Product = sb.ToString().Trim();
                int mi = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
                if (mi >= 0 && mi + 6 <= path.Length)
                {
                    int n;
                    if (int.TryParse(path.Substring(mi + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out n)) info.Interface = n;
                }
                return info;
            }
        }
    }

    // One open HID collection. Reads and writes are overlapped so they can time out: a device that has
    // nothing to say must never hang the battery thread.
    class HidDevice : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initial, IntPtr name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, int ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

        readonly SafeFileHandle h;
        public readonly HidInfo Info;
        IntPtr readOv, writeOv, readEvent, writeEvent, readBuf, writeBuf;
        readonly int inLen, outLen;

        HidDevice(SafeFileHandle handle, HidInfo info)
        {
            h = handle; Info = info;
            inLen = Math.Max(info.InLen, 1); outLen = Math.Max(info.OutLen, 1);
            int ovSize = Marshal.SizeOf(typeof(NativeOverlapped));
            readOv = Marshal.AllocHGlobal(ovSize); writeOv = Marshal.AllocHGlobal(ovSize);
            readEvent = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            writeEvent = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            readBuf = Marshal.AllocHGlobal(inLen); writeBuf = Marshal.AllocHGlobal(outLen);
        }

        // access: 0 = features only, 1 = read, 3 = read + write. Always shared, so games and vendor apps keep working.
        public static HidDevice Open(HidInfo info, int access)
        {
            uint rights = ((access & 1) != 0 ? 0x80000000u : 0) | ((access & 2) != 0 ? 0x40000000u : 0);
            SafeFileHandle handle = Hid.CreateFile(info.Path, rights, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);   // FILE_FLAG_OVERLAPPED
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            return new HidDevice(handle, info);
        }

        static void Prepare(IntPtr ov, IntPtr ev)
        {
            int size = Marshal.SizeOf(typeof(NativeOverlapped));
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(ov, i, 0);
            Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, ev);   // NativeOverlapped.EventHandle
        }

        // Next input report into dst (report id first). Returns its length, 0 on timeout, -1 on error.
        public int Read(byte[] dst, int timeoutMs)
        {
            Prepare(readOv, readEvent);
            int n;
            if (!ReadFile(h, readBuf, inLen, IntPtr.Zero, readOv))
            {
                if (Marshal.GetLastWin32Error() != 997) return -1;   // ERROR_IO_PENDING
                if (WaitForSingleObject(readEvent, Math.Max(0, timeoutMs)) != 0)
                {
                    CancelIoEx(h, readOv);
                    if (!GetOverlappedResult(h, readOv, out n, true)) return 0;   // cancelled
                }
            }
            if (!GetOverlappedResult(h, readOv, out n, false)) return -1;
            Marshal.Copy(readBuf, dst, 0, Math.Min(n, dst.Length));
            return n;
        }

        // Output report; data is padded with zeros to the collection's report length.
        public bool Write(byte[] data, int timeoutMs)
        {
            var padded = new byte[outLen];
            Array.Copy(data, padded, Math.Min(data.Length, outLen));
            Marshal.Copy(padded, 0, writeBuf, outLen);
            Prepare(writeOv, writeEvent);
            int n;
            if (!WriteFile(h, writeBuf, outLen, IntPtr.Zero, writeOv))
            {
                if (Marshal.GetLastWin32Error() != 997) return false;
                if (WaitForSingleObject(writeEvent, timeoutMs) != 0)
                {
                    CancelIoEx(h, writeOv);
                    GetOverlappedResult(h, writeOv, out n, true);
                    return false;
                }
            }
            return GetOverlappedResult(h, writeOv, out n, false) && n > 0;
        }

        public bool SetFeature(byte[] buf) { return HidD_SetFeature(h, buf, buf.Length); }
        public bool GetFeature(byte[] buf) { return HidD_GetFeature(h, buf, buf.Length); }

        public void Dispose()
        {
            if (!h.IsClosed) { CancelIoEx(h, IntPtr.Zero); h.Dispose(); }
            if (readOv != IntPtr.Zero) { Marshal.FreeHGlobal(readOv); readOv = IntPtr.Zero; }
            if (writeOv != IntPtr.Zero) { Marshal.FreeHGlobal(writeOv); writeOv = IntPtr.Zero; }
            if (readBuf != IntPtr.Zero) { Marshal.FreeHGlobal(readBuf); readBuf = IntPtr.Zero; }
            if (writeBuf != IntPtr.Zero) { Marshal.FreeHGlobal(writeBuf); writeBuf = IntPtr.Zero; }
            if (readEvent != IntPtr.Zero) { CloseHandle(readEvent); readEvent = IntPtr.Zero; }
            if (writeEvent != IntPtr.Zero) { CloseHandle(writeEvent); writeEvent = IntPtr.Zero; }
        }
    }

    // ---- readers

    interface IBatteryReader
    {
        void Poll(List<HidInfo> hid, List<BatteryInfo> found);
    }

    static class Readers
    {
        public static string Hex4(int v) { return v.ToString("X4"); }

        public static DeviceKind KindFromName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("trackball") || n.Contains("mx master") || n.Contains("mx anywhere")) return DeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("keys") || n.Contains("mx mechanical")) return DeviceKind.Keyboard;
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("joy-con")) return DeviceKind.Gamepad;
            if (n.Contains("head") || n.Contains("buds") || n.Contains("airpods") || n.Contains("earbud") || n.Contains("bose") ||
                n.Contains("sony wh") || n.Contains("sony wf") || n.Contains("jabra") || n.Contains("speaker")) return DeviceKind.Headset;
            return DeviceKind.Other;
        }
    }

    // Xenta receivers (VID 1D57), used by Attack Shark and Delux mice. While the mouse is awake the receiver sends
    // a 5-byte input report on usage page 000A every few seconds: [03][model][40][state][level], state 1 = on
    // battery, 2 = full, 3 = charging; a sleeping mouse sends nothing. Nothing has to be sent, so the collection
    // is only ever opened for reading. Right after the mouse links up the first frame falsely claims a full
    // battery, so a full reading that follows a silence only counts once a second frame agrees.
    // Some models report the level in tens (0-10): those start on that scale and switch to 0-100 for good the
    // first time they report anything above 10.
    // Format from github.com/AeonArcHQ/omarchy-delux-m900 and github.com/qu1ncky/BatteryTray.
    class XentaReader : IBatteryReader
    {
        static readonly ushort[] Receivers = { 0xFA60, 0xFA65 };

        class Model { public byte Code; public string Name; public bool Tens; }
        static readonly Model[] Models =
        {
            new Model { Code = 0x10, Name = "Attack Shark X3 / X6 / R1", Tens = true },
            new Model { Code = 0x3D, Name = "Delux M900 Pro" },
            new Model { Code = 0x4E, Name = "Attack Shark X3 Max" },
            new Model { Code = 0x55, Name = "Attack Shark X11" },
            new Model { Code = 0xE5, Name = "Attack Shark V3 Pro" },
        };

        public const string HundredScalePref = "battery_xenta_full_scale";   // model codes seen above 10

        class Listener { public HidDevice Dev; public byte[] Last, Prev; public DateTime LastAt; public bool AfterSilence; }
        readonly Dictionary<string, Listener> listeners = new Dictionary<string, Listener>();
        readonly HashSet<int> fullScale = new HashSet<int>();
        bool scalesLoaded;

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            if (!scalesLoaded) LoadScales();
            var present = new HashSet<string>();
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x1D57 || Array.IndexOf(Receivers, c.Pid) < 0 || c.Page != 0x000A || c.InLen < 5) continue;
                present.Add(c.Path);
                Listener l;
                bool fresh = !listeners.TryGetValue(c.Path, out l);
                if (fresh)
                {
                    HidDevice dev = HidDevice.Open(c, 1);
                    if (dev == null) continue;
                    l = new Listener { Dev = dev };
                    listeners[c.Path] = l;
                }
                // The handle stays open between polls, so the frames sent meanwhile are queued: take them all.
                // A new listener gives an awake mouse a few seconds to send its first frames.
                var r = new byte[Math.Max(c.InLen, 8)];
                DateTime until = DateTime.UtcNow.AddSeconds(fresh ? 6 : 0);
                int got = 0;
                while (true)
                {
                    int wait = fresh && got < 2 ? (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds) : 20;
                    if (fresh && got < 2 && DateTime.UtcNow >= until) break;
                    int n = l.Dev.Read(r, wait);
                    if (n < 5) break;
                    if (r[0] != 0x03 || r[2] != 0x40) continue;
                    l.AfterSilence = l.Last == null || (DateTime.UtcNow - l.LastAt).TotalSeconds > 15;
                    l.Prev = l.AfterSilence ? null : l.Last;
                    l.Last = new[] { r[0], r[1], r[2], r[3], r[4] };
                    l.LastAt = DateTime.UtcNow;
                    got++;
                }
                var b = new BatteryInfo { Key = "xenta:" + Readers.Hex4(c.Pid), Name = "Wireless mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
                if (l.Last == null)
                {
                    // Not heard from since the tray started: asleep or off. The last level from before is shown.
                    string saved = BatteryMonitor.SavedName(b.Key);
                    if (saved != null) b.Name = saved;
                    b.Asleep = true;
                    b.Note = "asleep; the level shows when the mouse wakes up";
                    found.Add(b);
                    break;
                }

                byte code = l.Last[1], state = l.Last[3], level = l.Last[4];
                Model model = null;
                foreach (Model m in Models) if (m.Code == code) model = m;
                if (level > 10 && level <= 100 && !fullScale.Contains(code)) { fullScale.Add(code); SaveScales(); }
                bool tens = model != null && model.Tens && !fullScale.Contains(code);
                if (model != null) b.Name = model.Name;
                b.Approx = tens;

                bool agreed = l.Prev != null && l.Prev[1] == l.Last[1] && l.Prev[3] == l.Last[3] && l.Prev[4] == l.Last[4];
                bool full = tens ? level >= 10 : level >= 100;
                b.Charging = state == 0x03 || state == 0x02;
                if (state == 0x02) { b.Percent = 100; b.Approx = false; }
                else if (level >= 1 && (agreed || !(full && l.AfterSilence)))   // the link-up frame's false "full"
                {
                    if (tens && level <= 10) b.Percent = level * 10;
                    else if (!tens && level <= 100) b.Percent = level;
                }
                // No frames for a while: the mouse went to sleep or was switched off
                if ((DateTime.UtcNow - l.LastAt).TotalSeconds > 15) b.Asleep = true;
                found.Add(b);
                break;   // one battery collection per receiver
            }
            var gone = new List<string>();
            foreach (var kv in listeners) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string p in gone) { listeners[p].Dev.Dispose(); listeners.Remove(p); }
        }

        void LoadScales()
        {
            scalesLoaded = true;
            object v;
            if (!AccentBorder.LoadPrefs().TryGetValue(HundredScalePref, out v)) return;
            var list = v as System.Collections.IList;
            if (list != null) foreach (object o in list) try { fullScale.Add(Convert.ToInt32(o)); } catch { }
        }

        void SaveScales()
        {
            var prefs = AccentBorder.LoadPrefs();
            var list = new List<object>();
            foreach (int c in fullScale) list.Add(c);
            prefs[HundredScalePref] = list;
            AccentBorder.SavePrefs(prefs);
        }

        public void Close() { foreach (var l in listeners.Values) l.Dev.Dispose(); listeners.Clear(); }
    }

    // Switch Pro Controller protocol, also spoken by 8BitDo pads in Switch mode over Bluetooth. Output report
    // 01 carries a rumble frame plus a subcommand; subcommand 02 (device info) only asks a question, so it does
    // not change the pad's input mode for games. Every reply (input report 21, and the full-mode report 30)
    // has the battery in the high nibble of byte 2: level 0/2/4/6/8 (empty..full) and bit 0 = charging.
    // From github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering (bluetooth_hid_notes, subcommands).
    class SwitchReader : IBatteryReader
    {
        static readonly ushort[] Pids = { 0x2009, 0x2006, 0x2007 };   // Pro Controller, Joy-Con L / R
        int counter;

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            var done = new HashSet<string>();
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x057E || Array.IndexOf(Pids, c.Pid) < 0) continue;
                if (c.Page != 1 || (c.Usage != 4 && c.Usage != 5) || c.OutLen < 11 || c.InLen < 3) continue;
                using (HidDevice dev = HidDevice.Open(c, 3))
                {
                    if (dev == null) continue;
                    var cmd = new byte[11];
                    cmd[0] = 0x01; cmd[1] = (byte)(counter++ & 0x0F);
                    cmd[2] = 0x00; cmd[3] = 0x01; cmd[4] = 0x40; cmd[5] = 0x40;   // neutral rumble, left
                    cmd[6] = 0x00; cmd[7] = 0x01; cmd[8] = 0x40; cmd[9] = 0x40;   // neutral rumble, right
                    cmd[10] = 0x02;                                                // subcommand: request device info
                    if (!dev.Write(cmd, 500)) continue;
                    var r = new byte[Math.Max(c.InLen, 64)];
                    DateTime until = DateTime.UtcNow.AddMilliseconds(700);
                    int battery = -1;
                    while (DateTime.UtcNow < until)
                    {
                        int n = dev.Read(r, (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds));
                        if (n <= 0) break;
                        if (n >= 3 && (r[0] == 0x21 || r[0] == 0x30 || r[0] == 0x31)) { battery = r[2] >> 4; if (r[0] == 0x21) break; }
                    }
                    if (battery < 0) continue;
                    string name = c.Pid == 0x2006 ? "Joy-Con (L)" : c.Pid == 0x2007 ? "Joy-Con (R)" :
                        c.Product.IndexOf("8BitDo", StringComparison.OrdinalIgnoreCase) >= 0 ? c.Product : "Switch-mode controller";
                    string key = "switch:" + Readers.Hex4(c.Pid) + ":" + c.Path.GetHashCode().ToString("X");
                    if (!done.Add(key)) continue;
                    found.Add(new BatteryInfo
                    {
                        Key = key, Name = name, Kind = DeviceKind.Gamepad, Via = "Bluetooth (Switch mode)",
                        Percent = (battery & 0x0E) * 100 / 8, Approx = true, Charging = (battery & 1) != 0,
                    });
                }
            }
        }
    }

    // 8BitDo pads on their 2.4 GHz dongle pose as a wired Xbox 360 pad, and the dongle's own vendor channel has
    // no published battery command. Listed so the menu says why there is no number, rather than nothing.
    class EightBitDoNote : IBatteryReader
    {
        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            // Some of the dongle's interfaces report a status word ("IDLE") as their product name, so the name is
            // taken from whichever interface actually says 8BitDo
            // With the pad switched off the dongle re-appears under another id, named "IDLE"
            var names = new Dictionary<ushort, string>();
            var idle = new HashSet<ushort>();
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x2DC8) continue;
                if (c.Product.Equals("IDLE", StringComparison.OrdinalIgnoreCase)) { idle.Add(c.Pid); continue; }
                string n;
                if (!names.TryGetValue(c.Pid, out n)) names[c.Pid] = n = null;
                if (c.Product.IndexOf("8BitDo", StringComparison.OrdinalIgnoreCase) >= 0 && (n == null || c.Product.Length < n.Length))
                    names[c.Pid] = c.Product;
            }
            foreach (var kv in names)
            {
                string name = kv.Value ?? (kv.Key == 0x310A ? "8BitDo Ultimate 2C" : "8BitDo controller");
                int paren = name.IndexOf('(');   // "Controller (8BitDo …)"
                if (paren >= 0 && name.EndsWith(")")) name = name.Substring(paren + 1, name.Length - paren - 2);
                found.Add(new BatteryInfo
                {
                    Key = "8bitdo:" + Readers.Hex4(kv.Key),
                    Name = name.Replace(" Wireless Controller", "").Trim(),
                    Kind = DeviceKind.Gamepad, Via = "2.4 GHz dongle",
                    Note = "no reading on the 2.4 GHz dongle (Switch mode over Bluetooth has one)",
                });
            }
            if (names.Count == 0)
                foreach (ushort pid in idle)
                    found.Add(new BatteryInfo
                    {
                        Key = "8bitdo:idle", Name = "8BitDo controller", Kind = DeviceKind.Gamepad, Via = "2.4 GHz dongle",
                        Note = "switched off",
                    });
        }
    }

    // SONiX 2.4 GHz keyboard dongles (AJAZZ AK820 / AK980, AULA). Vendor collection on usage page FF60
    // (interface 3), 33-byte output report: report id 0, the command, and a byte sum of the command in the last
    // byte. Command 20 01 asks for the battery; the reply starts 20 01 00 and the 4th byte is the percent.
    // The keyboard only answers while it is switched to 2.4 GHz mode.
    // From github.com/donglover69/sonix-keyboard-studio (README, "2.4G dongles").
    class SonixDongleReader : IBatteryReader
    {
        static readonly int[][] Dongles = { new[] { 0x0C45, 0xFEFE }, new[] { 0x05AC, 0x024F } };

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            var done = new HashSet<string>();
            foreach (HidInfo c in hid)
            {
                bool known = false;
                foreach (int[] d in Dongles) if (c.Vid == d[0] && c.Pid == d[1]) known = true;
                if (!known || c.Page != 0xFF60 || c.OutLen < 33 || c.InLen < 5) continue;
                string key = "sonix:" + Readers.Hex4(c.Vid) + ":" + Readers.Hex4(c.Pid);
                if (done.Contains(key)) continue;
                var b = new BatteryInfo
                {
                    Key = key, Kind = DeviceKind.Keyboard, Via = "2.4 GHz dongle",
                    Name = c.Vid == 0x05AC ? "AJAZZ / AULA keyboard" : "AJAZZ keyboard",
                };
                string p = c.Product.Replace("2.4G", "").Trim();
                if (p.IndexOf("AK", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("AULA", StringComparison.OrdinalIgnoreCase) >= 0) b.Name = p;
                using (HidDevice dev = HidDevice.Open(c, 3))
                {
                    if (dev == null) continue;
                    var cmd = new byte[33];
                    cmd[1] = 0x20; cmd[2] = 0x01;
                    int sum = 0;
                    for (int i = 1; i < 32; i++) sum += cmd[i];
                    cmd[32] = (byte)sum;
                    int level = -1;
                    if (dev.Write(cmd, 500))
                    {
                        var r = new byte[Math.Max(c.InLen, 40)];
                        DateTime until = DateTime.UtcNow.AddMilliseconds(600);
                        while (level < 0 && DateTime.UtcNow < until)
                        {
                            int n = dev.Read(r, (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds));
                            if (n <= 0) break;
                            for (int o = 0; o <= 1 && level < 0; o++)   // with or without the report id in front
                                if (r[o] == 0x20 && r[o + 1] == 0x01 && r[o + 2] == 0x00 && r[o + 3] <= 100) level = r[o + 3];
                        }
                    }
                    if (level < 0) { b.Asleep = true; b.Note = "asleep, or not in 2.4 GHz mode"; }
                    else b.Percent = level;
                }
                done.Add(key);
                found.Add(b);
            }
        }
    }

    // Logitech HID++ 2.0, on Unifying / Lightspeed / Bolt receivers (devices 1-6) and on devices connected
    // directly (index FF). Long reports (11, 20 bytes) on vendor collection FF00:0002 (FF43:0202 on Bluetooth):
    // [11][device][feature index][function << 4 | software id][params...]. The root feature (index 0) maps a
    // feature id to its index. Battery comes from Unified Battery (1004, function 1: percent, level, charging),
    // Battery Status (1000, function 0) or Battery Voltage (1001, function 0: millivolts). The name and kind come
    // from Device Name & Type (0005).
    // From Logitech's HID++ 2.0 feature documents and github.com/pwr-Solaar/Solaar (lib/logitech_receiver).
    class LogitechReader : IBatteryReader
    {
        static readonly ushort[] Receivers = { 0xC52B, 0xC531, 0xC532, 0xC534, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548 };
        const byte SwId = 0x0A;

        class Known { public string Name; public DeviceKind Kind; public byte Unified, Status, Voltage; public bool Probed; }
        readonly Dictionary<string, Known> known = new Dictionary<string, Known>();
        DateTime nextPoll = DateTime.MinValue;
        readonly List<BatteryInfo> lastFound = new List<BatteryInfo>();

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            // Levels change slowly; asking every 2 minutes is plenty and keeps radio chatter low
            if (DateTime.UtcNow < nextPoll) { found.AddRange(lastFound); return; }
            nextPoll = DateTime.UtcNow.AddMinutes(2);
            lastFound.Clear();
            var done = new HashSet<string>();
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x046D || c.OutLen != 20) continue;
                if (!((c.Page == 0xFF00 && c.Usage == 0x0002) || (c.Page == 0xFF43 && c.Usage == 0x0202))) continue;
                string devKey = Readers.Hex4(c.Pid) + ":" + c.Path.GetHashCode().ToString("X");
                if (!done.Add(devKey)) continue;
                bool receiver = Array.IndexOf(Receivers, c.Pid) >= 0;
                using (HidDevice dev = HidDevice.Open(c, 3))
                {
                    if (dev == null) continue;
                    if (receiver) { for (byte i = 1; i <= 6; i++) Ask(dev, c, i, devKey, lastFound); }
                    else Ask(dev, c, 0xFF, devKey, lastFound);
                }
            }
            found.AddRange(lastFound);
        }

        void Ask(HidDevice dev, HidInfo c, byte index, string devKey, List<BatteryInfo> found)
        {
            string key = "logi:" + devKey + ":" + index;
            Known k;
            if (!known.TryGetValue(key, out k)) { k = new Known(); known[key] = k; }
            if (!k.Probed)
            {
                // Root ping (function 1) answers only when a device is in this slot and awake
                byte[] r = Request(dev, index, 0x00, 1, new byte[] { 0x00, 0x00, 0x5A });
                if (r == null) return;   // nothing paired here, or it is switched off: ask again next time
                r = Request(dev, index, 0x00, 0, new byte[] { 0x10, 0x04 });
                if (r != null) k.Unified = r[4];
                r = Request(dev, index, 0x00, 0, new byte[] { 0x10, 0x00 });
                if (r != null) k.Status = r[4];
                r = Request(dev, index, 0x00, 0, new byte[] { 0x10, 0x01 });
                if (r != null) k.Voltage = r[4];
                r = Request(dev, index, 0x00, 0, new byte[] { 0x00, 0x05 });
                if (r != null && r[4] != 0) ReadName(dev, index, r[4], k);
                if (string.IsNullOrEmpty(k.Name)) k.Name = c.Product.Length > 0 && index == 0xFF ? c.Product : "Logitech device";
                if (k.Kind == DeviceKind.Other) k.Kind = Readers.KindFromName(k.Name);
                k.Probed = true;
            }
            var b = new BatteryInfo { Key = key, Name = k.Name, Kind = k.Kind, Via = index == 0xFF ? "Logitech" : "Logitech receiver" };
            byte[] s;
            if (k.Unified != 0 && (s = Request(dev, index, k.Unified, 1, null)) != null)
            {
                b.Percent = s[4] > 0 ? Math.Min(100, (int)s[4]) : LevelFlags(s[5]);
                b.Approx = s[4] == 0;
                b.Charging = s[6] == 1 || s[6] == 2 || s[6] == 3;
                if (s[6] == 3) b.Percent = 100;
            }
            else if (k.Status != 0 && (s = Request(dev, index, k.Status, 0, null)) != null)
            {
                b.Percent = Math.Min(100, (int)s[4]);
                b.Charging = s[6] >= 1 && s[6] <= 4;
                if (s[6] == 3) b.Percent = 100;
            }
            else if (k.Voltage != 0 && (s = Request(dev, index, k.Voltage, 0, null)) != null)
            {
                b.Percent = FromMillivolts((s[4] << 8) | s[5]);
                b.Approx = true;
                b.Charging = (s[6] & 0x80) != 0;
            }
            else if (k.Unified == 0 && k.Status == 0 && k.Voltage == 0) return;   // no battery feature at all
            else b.Asleep = true;
            found.Add(b);
        }

        static int LevelFlags(byte f)
        {
            if ((f & 8) != 0) return 100;   // full
            if ((f & 4) != 0) return 60;    // good
            if ((f & 2) != 0) return 20;    // low
            if ((f & 1) != 0) return 5;     // critical
            return -1;
        }

        // Typical single-cell Li-ion discharge curve
        static readonly int[] Mv = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3500 };
        static int FromMillivolts(int mv)
        {
            if (mv >= Mv[0]) return 100;
            for (int i = 1; i < Mv.Length; i++)
                if (mv >= Mv[i]) return 100 - (i * 10) + (int)Math.Round(10.0 * (mv - Mv[i]) / (Mv[i - 1] - Mv[i]));
            return 0;
        }

        void ReadName(HidDevice dev, byte index, byte feature, Known k)
        {
            byte[] r = Request(dev, index, feature, 0, null);
            if (r == null) return;
            int len = Math.Min((int)r[4], 64);
            var name = new StringBuilder();
            while (name.Length < len)
            {
                r = Request(dev, index, feature, 1, new[] { (byte)name.Length });
                if (r == null) break;
                int before = name.Length;
                for (int i = 4; i < 20 && name.Length < len; i++) { if (r[i] == 0) break; name.Append((char)r[i]); }
                if (name.Length == before) break;
            }
            if (name.Length > 0) k.Name = name.ToString().Trim();
            r = Request(dev, index, feature, 2, null);
            if (r != null)
            {
                switch (r[4])
                {
                    case 0: case 2: k.Kind = DeviceKind.Keyboard; break;
                    case 3: case 4: case 5: k.Kind = DeviceKind.Mouse; break;
                    case 8: k.Kind = DeviceKind.Headset; break;
                }
            }
        }

        // Sends one long request and waits for its answer; null on an error reply or no answer
        static byte[] Request(HidDevice dev, byte index, byte feature, int function, byte[] args)
        {
            var q = new byte[20];
            q[0] = 0x11; q[1] = index; q[2] = feature; q[3] = (byte)((function << 4) | SwId);
            if (args != null) Array.Copy(args, 0, q, 4, Math.Min(args.Length, 16));
            if (!dev.Write(q, 500)) return null;
            var r = new byte[32];
            DateTime until = DateTime.UtcNow.AddMilliseconds(600);
            while (DateTime.UtcNow < until)
            {
                int n = dev.Read(r, (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds));
                if (n <= 0) return null;
                if (n < 7 || r[1] != index) continue;
                if (r[0] == 0x10 && r[2] == 0x8F && r[3] == feature) return null;    // HID++ 1.0 error (no device here)
                if (r[0] == 0x11 && r[2] == 0xFF && r[3] == feature) return null;    // HID++ 2.0 error
                if (r[0] == 0x11 && r[2] == feature && r[3] == q[3])
                {
                    if (feature == 0x00 && function == 0 && r[4] == 0) return null;  // feature not supported
                    var copy = new byte[20];
                    Array.Copy(r, copy, 20);
                    return copy;
                }
            }
            return null;
        }
    }

    // Razer wireless devices: a 90-byte feature report (report id 0):
    // [status][transaction id][remaining 2][protocol][data size][class][command][80 argument bytes][crc][0],
    // crc = XOR of bytes 2..87. Class 07 command 80 reads the battery (argument 1, 0-255), command 84 the
    // charging flag. Devices differ in the transaction id they accept, so the known ones are tried in turn.
    // Status 02 in the reply means success.
    // From github.com/openrazer/openrazer (driver/razercommon.h, razerchromacommon.c).
    class RazerReader : IBatteryReader
    {
        static readonly byte[] Transactions = { 0x1F, 0x3F, 0x9F, 0xFF };
        readonly Dictionary<ushort, byte> transactionFor = new Dictionary<ushort, byte>();
        readonly HashSet<ushort> noBattery = new HashSet<ushort>();

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            var done = new HashSet<ushort>();
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x1532 || c.FeatLen != 91 || noBattery.Contains(c.Pid) || done.Contains(c.Pid)) continue;
                using (HidDevice dev = HidDevice.Open(c, 0))
                {
                    if (dev == null) continue;
                    byte tid;
                    byte[] level = null;
                    if (transactionFor.TryGetValue(c.Pid, out tid)) level = Command(dev, tid, 0x80);
                    else
                        foreach (byte t in Transactions)
                            if ((level = Command(dev, t, 0x80)) != null) { transactionFor[c.Pid] = tid = t; break; }
                    if (level == null) { if (!transactionFor.ContainsKey(c.Pid)) noBattery.Add(c.Pid); continue; }
                    done.Add(c.Pid);
                    byte[] charge = Command(dev, tid, 0x84);
                    var b = new BatteryInfo
                    {
                        Key = "razer:" + Readers.Hex4(c.Pid),
                        Name = c.Product.Length > 0 ? c.Product : "Razer device",
                        Via = "Razer",
                        Percent = (int)Math.Round(level[10] * 100.0 / 255),
                        Charging = charge != null && charge[10] == 1,
                    };
                    b.Kind = Readers.KindFromName(b.Name);
                    string n = b.Name.ToLowerInvariant();
                    if (b.Kind == DeviceKind.Other)
                        foreach (string m in new[] { "deathadder", "viper", "basilisk", "naga", "orochi", "cobra", "mamba", "pro click", "atheris", "lancehead" })
                            if (n.Contains(m)) b.Kind = DeviceKind.Mouse;
                    if (b.Kind == DeviceKind.Other)
                        foreach (string m in new[] { "blackwidow", "huntsman", "deathstalker", "ornata" })
                            if (n.Contains(m)) b.Kind = DeviceKind.Keyboard;
                    if (b.Kind == DeviceKind.Other)
                        foreach (string m in new[] { "kraken", "barracuda", "blackshark", "nari", "hammerhead" })
                            if (n.Contains(m)) b.Kind = DeviceKind.Headset;
                    if (b.Percent == 0 && !b.Charging) continue;   // wired-only devices answer with an empty battery
                    found.Add(b);
                }
            }
        }

        // Returns the 91-byte reply (with the report id in front) or null
        static byte[] Command(HidDevice dev, byte transaction, byte command)
        {
            var q = new byte[91];
            q[2] = transaction; q[6] = 0x02; q[7] = 0x07; q[8] = command;
            byte crc = 0;
            for (int i = 3; i <= 88; i++) crc ^= q[i];
            q[89] = crc;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!dev.SetFeature(q)) return null;
                Thread.Sleep(60);
                var r = new byte[91];
                if (!dev.GetFeature(r)) return null;
                if (r[1] == 0x01) continue;                                      // busy: ask again
                if (r[1] == 0x02 && r[7] == 0x07 && r[8] == command) return r;  // success
                return null;
            }
            return null;
        }
    }

    // DualShock 4 and DualSense put their battery into every full input report. Status byte: low nibble
    // level 0-10. DualShock 4: bit 4 = cable connected; byte 30 of USB report 01, byte 32 of Bluetooth report 11.
    // DualSense: high nibble 0 = on battery, 1 = charging, 2 = full; byte 53 of USB report 01, byte 54 of
    // Bluetooth report 31. Over Bluetooth only the full reports carry it; until a game or Steam switches the
    // pad to them it sends short reports without a battery.
    // From the Linux kernel, drivers/hid/hid-playstation.c.
    class PlayStationReader : IBatteryReader
    {
        static readonly ushort[] Ds4 = { 0x05C4, 0x09CC, 0x0BA0 };
        static readonly ushort[] DualSense = { 0x0CE6, 0x0DF2 };

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            foreach (HidInfo c in hid)
            {
                if (c.Vid != 0x054C || c.Page != 1 || c.Usage != 5) continue;
                bool ds4 = Array.IndexOf(Ds4, c.Pid) >= 0, ds = Array.IndexOf(DualSense, c.Pid) >= 0;
                if (!ds4 && !ds) continue;
                var b = new BatteryInfo
                {
                    Key = "ps:" + Readers.Hex4(c.Pid) + ":" + c.Path.GetHashCode().ToString("X"),
                    Name = ds ? (c.Pid == 0x0DF2 ? "DualSense Edge" : "DualSense") : "DualShock 4",
                    Kind = DeviceKind.Gamepad,
                };
                using (HidDevice dev = HidDevice.Open(c, 1))
                {
                    if (dev == null) continue;
                    var r = new byte[Math.Max(c.InLen, 80)];
                    int status = -1;
                    for (int i = 0; i < 6 && status < 0; i++)
                    {
                        int n = dev.Read(r, 250);
                        if (n <= 0) break;
                        if (ds4 && r[0] == 0x01 && n >= 64) { status = r[30]; b.Via = "USB"; }
                        else if (ds4 && r[0] == 0x11 && n >= 64) { status = r[32]; b.Via = "Bluetooth"; }
                        else if (ds && r[0] == 0x01 && n >= 64) { status = r[53]; b.Via = "USB"; }
                        else if (ds && r[0] == 0x31 && n >= 64) { status = r[54]; b.Via = "Bluetooth"; }
                    }
                    if (status < 0) { b.Note = "battery shows once a game or Steam is using the pad"; b.Via = "Bluetooth"; found.Add(b); continue; }
                    int level = status & 0x0F;
                    if (ds4)
                    {
                        bool cable = (status & 0x10) != 0;
                        b.Percent = level >= 10 ? 100 : level * 10 + 5;
                        b.Charging = cable;
                    }
                    else
                    {
                        int state = status >> 4;
                        b.Percent = state == 2 ? 100 : Math.Min(100, level * 10 + 5);
                        b.Charging = state == 1 || state == 2;
                        if (state > 2) continue;   // error states: no trustworthy level
                    }
                    b.Approx = true;
                }
                found.Add(b);
            }
        }
    }

    // Xbox controllers through XInput (Xbox Wireless adapter, and Bluetooth when Windows has no level of its
    // own for them): only empty / low / medium / full. Pads that report a wired battery are skipped, which also
    // covers third-party pads posing as a wired Xbox 360 pad.
    class XInputReader : IBatteryReader
    {
        [StructLayout(LayoutKind.Sequential)] struct BATTERY { public byte Type, Level; }
        [StructLayout(LayoutKind.Sequential)] struct STATE { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint user, byte type, out BATTERY b);
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint user, out STATE s);
        bool unavailable;

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            if (unavailable) return;
            bool xboxOnBluetooth = false;
            foreach (BatteryInfo f in found)
                if (f.Via == "Bluetooth" && f.Name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0) xboxOnBluetooth = true;
            try
            {
                for (uint user = 0; user < 4; user++)
                {
                    STATE st;
                    if (XInputGetState(user, out st) != 0) continue;
                    BATTERY pad;
                    bool hasPad = XInputGetBatteryInformation(user, 0, out pad) == 0 && Wireless(pad.Type);
                    if (hasPad && !xboxOnBluetooth) found.Add(Make(user, pad, false));
                    BATTERY head;
                    if (XInputGetBatteryInformation(user, 1, out head) == 0 && Wireless(head.Type) &&
                        !(hasPad && head.Type == pad.Type && head.Level == pad.Level))   // pads echo their own level here
                        found.Add(Make(user, head, true));
                }
            }
            catch (DllNotFoundException) { unavailable = true; }
            catch (EntryPointNotFoundException) { unavailable = true; }
        }

        static bool Wireless(byte type) { return type == 2 || type == 3; }   // alkaline, NiMH (not disconnected, wired, unknown)

        static BatteryInfo Make(uint user, BATTERY b, bool headset)
        {
            var info = new BatteryInfo
            {
                Key = "xinput:" + user + (headset ? ":headset" : ""),
                Name = headset ? "Xbox headset" : "Xbox controller " + (user + 1),
                Kind = headset ? DeviceKind.Headset : DeviceKind.Gamepad,
                Via = "Xbox Wireless", Approx = true,
            };
            switch (b.Level)
            {
                case 0: info.Percent = 5; break;
                case 1: info.Percent = 25; break;
                case 2: info.Percent = 60; break;
                default: info.Percent = 100; break;
            }
            return info;
        }
    }

    // Bluetooth devices Windows knows a battery level for (headphones, earbuds, Bluetooth mice, keyboards and
    // pads): Windows keeps it in a device property (the one Settings > Bluetooth & devices shows) on the
    // Bluetooth device's nodes. The name comes from the node of the device itself, found by its container id.
    class BluetoothReader : IBatteryReader
    {
        [StructLayout(LayoutKind.Sequential)] struct DEVINFO { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DEVINFO info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DEVINFO info, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int required, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DEVINFO info, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static DEVPROPKEY BatteryKey = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static DEVPROPKEY FriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static DEVPROPKEY DeviceDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static DEVPROPKEY ContainerId = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);

        public void Poll(List<HidInfo> hid, List<BatteryInfo> found)
        {
            var names = new Dictionary<Guid, string>();
            var levels = new Dictionary<Guid, int>();
            foreach (string en in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" })
            {
                IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, en, IntPtr.Zero, 0x6);   // DIGCF_PRESENT | DIGCF_ALLCLASSES
                if (set == new IntPtr(-1)) continue;
                try
                {
                    var info = new DEVINFO();
                    info.cbSize = Marshal.SizeOf(info);
                    for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                    {
                        Guid container;
                        if (!GetGuid(set, ref info, ContainerId, out container)) continue;
                        var id = new StringBuilder(512);
                        int req;
                        SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out req);
                        string inst = id.ToString().ToUpperInvariant();
                        // The device's own node ("BTHENUM\DEV_<address>", "BTHLE\DEV_<address>") carries its name
                        if (inst.StartsWith("BTHENUM\\DEV_") || inst.StartsWith("BTHLE\\DEV_"))
                        {
                            string n = GetString(set, ref info, FriendlyName) ?? GetString(set, ref info, DeviceDesc);
                            if (!string.IsNullOrEmpty(n)) names[container] = n;
                        }
                        byte[] v = Get(set, ref info, BatteryKey);
                        if (v != null && v.Length >= 1 && v[0] <= 100)
                        {
                            int old;
                            if (!levels.TryGetValue(container, out old)) levels[container] = v[0];
                        }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            foreach (var kv in levels)
            {
                string name;
                if (!names.TryGetValue(kv.Key, out name)) name = "Bluetooth device";
                found.Add(new BatteryInfo
                {
                    Key = "bt:" + kv.Key.ToString("N"), Name = name, Kind = Readers.KindFromName(name),
                    Via = "Bluetooth", Percent = kv.Value,
                });
            }
        }

        static byte[] Get(IntPtr set, ref DEVINFO info, DEVPROPKEY key)
        {
            uint type;
            int req;
            SetupDiGetDevicePropertyW(set, ref info, ref key, out type, null, 0, out req, 0);
            if (req <= 0 || req > 4096) return null;
            var buf = new byte[req];
            return SetupDiGetDevicePropertyW(set, ref info, ref key, out type, buf, req, out req, 0) ? buf : null;
        }

        static string GetString(IntPtr set, ref DEVINFO info, DEVPROPKEY key)
        {
            byte[] b = Get(set, ref info, key);
            return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        }

        static bool GetGuid(IntPtr set, ref DEVINFO info, DEVPROPKEY key, out Guid g)
        {
            byte[] b = Get(set, ref info, key);
            g = Guid.Empty;
            if (b == null || b.Length != 16) return false;
            g = new Guid(b);
            return g != Guid.Empty;
        }
    }

    // ---- the poll thread

    static class BatteryMonitor
    {
        static readonly XentaReader xenta = new XentaReader();
        static readonly IBatteryReader[] readers =
        {
            xenta, new SwitchReader(), new SonixDongleReader(), new LogitechReader(), new RazerReader(),
            new PlayStationReader(), new BluetoothReader(), new XInputReader(), new EightBitDoNote(),
        };
        static readonly Dictionary<string, BatteryInfo> lastKnown = new Dictionary<string, BatteryInfo>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread thread;
        static volatile bool stopping;

        public static event Action<List<BatteryInfo>> Updated;   // raised on the battery thread

        public static void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "batteries", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        public static void RefreshSoon() { wake.Set(); }

        public static void Stop()
        {
            stopping = true;
            wake.Set();
            if (thread != null) thread.Join(3000);
            xenta.Close();
        }

        static void Loop()
        {
            while (!stopping)
            {
                List<BatteryInfo> list;
                try { list = PollOnce(); }
                catch (Exception e) { Log.Write("batteries: " + e.Message); list = null; }
                if (list != null && !stopping)
                {
                    var h = Updated;
                    if (h != null) try { h(list); } catch (Exception e) { Log.Write("batteries ui: " + e.Message); }
                }
                wake.WaitOne(30000);
            }
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        // komorebi-tray.exe --batteries: one reading of every device, printed to the console it was started from
        public static void PrintOnce()
        {
            AttachConsole(-1);
            List<BatteryInfo> list = PollOnce();
            Console.WriteLine();
            if (list.Count == 0) Console.WriteLine("No wireless devices found.");
            foreach (BatteryInfo b in list)
                Console.WriteLine(string.Format("{0,-34} {1,-30} {2}", b.Name, b.LevelText(), b.Via));
            xenta.Close();
        }

        static List<BatteryInfo> PollOnce()
        {
            List<HidInfo> hid = Hid.Enumerate();
            var found = new List<BatteryInfo>();
            foreach (IBatteryReader r in readers)
            {
                try { r.Poll(hid, found); }
                catch (Exception e) { Log.Write("batteries " + r.GetType().Name + ": " + e.Message); }
            }
            LoadSaved();
            var keys = new HashSet<string>();
            var result = new List<BatteryInfo>();
            bool changed = false;
            foreach (BatteryInfo b in found)
            {
                if (!keys.Add(b.Key)) continue;
                // No fresh level (asleep, or the reading isn't settled yet): keep showing the last one we had,
                // also from before the tray restarted. Only readers that know the device is asleep set Asleep;
                // a pending reading is not sleep.
                BatteryInfo old;
                if (b.Percent < 0 && lastKnown.TryGetValue(b.Key, out old) && old.Percent >= 0)
                {
                    b.Percent = old.Percent; b.Approx = old.Approx;
                    if (b.Asleep) b.Note = null;
                }
                else if (b.Percent >= 0 && !b.Asleep)
                {
                    if (!lastKnown.TryGetValue(b.Key, out old) || old.Percent != b.Percent || old.Name != b.Name) changed = true;
                    lastKnown[b.Key] = b;
                }
                result.Add(b);
            }
            if (changed) Save();
            return result;
        }

        // Last known levels, kept in komorebi-tray.json so a sleeping device still shows one after a restart
        const string SavedPref = "battery_last";
        static bool savedLoaded;

        static void LoadSaved()
        {
            if (savedLoaded) return;
            savedLoaded = true;
            object v;
            var map = AccentBorder.LoadPrefs().TryGetValue(SavedPref, out v) ? v as IDictionary<string, object> : null;
            if (map == null) return;
            foreach (var kv in map)
            {
                var e = kv.Value as IDictionary<string, object>;
                if (e == null) continue;
                try
                {
                    object p, n, a;
                    lastKnown[kv.Key] = new BatteryInfo
                    {
                        Key = kv.Key,
                        Percent = e.TryGetValue("percent", out p) ? Convert.ToInt32(p) : -1,
                        Name = e.TryGetValue("name", out n) ? Convert.ToString(n) : null,
                        Approx = e.TryGetValue("approx", out a) && Convert.ToBoolean(a),
                    };
                }
                catch { }
            }
        }

        static void Save()
        {
            var map = new Dictionary<string, object>();
            foreach (var kv in lastKnown)
                map[kv.Key] = new Dictionary<string, object> { { "percent", kv.Value.Percent }, { "name", kv.Value.Name ?? "" }, { "approx", kv.Value.Approx } };
            var prefs = AccentBorder.LoadPrefs();
            prefs[SavedPref] = map;
            AccentBorder.SavePrefs(prefs);
        }

        // The name a device had when it was last read (readers that can't name a sleeping device use it)
        public static string SavedName(string key)
        {
            LoadSaved();
            BatteryInfo b;
            return lastKnown.TryGetValue(key, out b) && !string.IsNullOrEmpty(b.Name) ? b.Name : null;
        }
    }

    // ---- notification area

    // One small icon per device with its level drawn as a number, plus a Batteries submenu in the tray menu.
    // Numbers turn red at the low mark, blue while charging, and grey while the device sleeps.
    class BatteryTray : IDisposable
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public const string PrefIcons = "battery_icons", PrefWarn = "battery_warn", PrefLow = "battery_low";

        readonly Control invoker;
        readonly NotifyIcon main;
        readonly ContextMenuStrip menu;
        public readonly ToolStripMenuItem MenuItem;
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, string> drawn = new Dictionary<string, string>();
        readonly HashSet<string> warned = new HashSet<string>();
        List<BatteryInfo> current = new List<BatteryInfo>();
        bool showIcons = true, warn = true;
        int low = 15;
        bool disposed;

        public BatteryTray(Control invoker, NotifyIcon main, ContextMenuStrip menu)
        {
            this.invoker = invoker; this.main = main; this.menu = menu;
            MenuItem = new ToolStripMenuItem("Batteries");
            MenuItem.DropDown.Renderer = menu.Renderer;
            MenuItem.DropDown.BackColor = menu.BackColor;
            MenuItem.DropDown.ForeColor = menu.ForeColor;
            ((ToolStripDropDownMenu)MenuItem.DropDown).ShowImageMargin = false;
            MenuItem.DropDownItems.Add("Looking for devices…").Enabled = false;
            MenuItem.DropDownOpening += (s, e) => FillMenu();
            LoadPrefs();
            BatteryMonitor.Updated += OnUpdated;
            BatteryMonitor.Start();
        }

        public void LoadPrefs()
        {
            var prefs = AccentBorder.LoadPrefs();
            object v;
            showIcons = !prefs.TryGetValue(PrefIcons, out v) || Convert.ToBoolean(v);
            warn = !prefs.TryGetValue(PrefWarn, out v) || Convert.ToBoolean(v);
            low = prefs.TryGetValue(PrefLow, out v) ? Math.Max(5, Math.Min(50, Convert.ToInt32(v))) : 15;
            if (!disposed) Apply(current);
        }

        void OnUpdated(List<BatteryInfo> list)
        {
            try { invoker.BeginInvoke((MethodInvoker)(() => { if (!disposed) Apply(list); })); } catch { }
        }

        void Apply(List<BatteryInfo> list)
        {
            current = list;
            var keep = new HashSet<string>();
            foreach (BatteryInfo b in list)
            {
                if (b.Percent < 0) continue;
                CheckLow(b);
                if (!showIcons) continue;
                keep.Add(b.Key);
                NotifyIcon ni;
                if (!icons.TryGetValue(b.Key, out ni))
                {
                    ni = new NotifyIcon { ContextMenuStrip = menu };
                    icons[b.Key] = ni;
                }
                string tip = b.Name + ": " + b.LevelText();
                ni.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
                string look = b.Percent + "|" + b.Charging + "|" + b.Asleep + "|" + (b.Percent <= low) + "|" + LightTaskbar() + "|" + SystemInformation.SmallIconSize.Width;
                string was;
                if (!drawn.TryGetValue(b.Key, out was) || was != look)
                {
                    Icon icon = Draw(b);
                    if (icon != null)
                    {
                        ni.Icon = icon;
                        IntPtr old;
                        if (handles.TryGetValue(b.Key, out old)) DestroyIcon(old);
                        handles[b.Key] = icon.Handle;
                        drawn[b.Key] = look;
                    }
                }
                ni.Visible = true;
            }
            var gone = new List<string>();
            foreach (string k in icons.Keys) if (!keep.Contains(k)) gone.Add(k);
            foreach (string k in gone) Remove(k);
        }

        void Remove(string key)
        {
            NotifyIcon ni;
            if (icons.TryGetValue(key, out ni)) { ni.Visible = false; ni.Dispose(); icons.Remove(key); }
            IntPtr h;
            if (handles.TryGetValue(key, out h)) { DestroyIcon(h); handles.Remove(key); }
            drawn.Remove(key);
        }

        // Once per drop below the mark; it re-arms after charging back above it
        void CheckLow(BatteryInfo b)
        {
            if (b.Charging || b.Percent > low + 5) { warned.Remove(b.Key); return; }
            if (!warn || b.Asleep || b.Percent > low || !warned.Add(b.Key)) return;
            main.ShowBalloonTip(5000, "Low battery", b.Name + " is at " + b.LevelText() + ".", ToolTipIcon.Warning);
        }

        void FillMenu()
        {
            MenuItem.DropDownItems.Clear();
            if (current.Count == 0) MenuItem.DropDownItems.Add("No wireless devices found").Enabled = false;
            foreach (BatteryInfo b in current)
            {
                var item = new ToolStripMenuItem(b.Name + "  —  " + b.LevelText());
                item.ToolTipText = b.Via;
                if (b.Percent < 0) item.ForeColor = Color.FromArgb(150, 150, 150);
                else if (b.Percent <= low && !b.Charging) item.ForeColor = Color.FromArgb(255, 110, 100);
                MenuItem.DropDownItems.Add(item);
            }
            MenuItem.DropDownItems.Add(new ToolStripSeparator());
            MenuItem.DropDownItems.Add("Check again now", null, (s, e) => BatteryMonitor.RefreshSoon());
        }

        static bool LightTaskbar()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("SystemUsesLightTheme") : null;
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        // The level as one bold number filling the icon; 100 is drawn as a full battery mark instead of three digits
        Icon Draw(BatteryInfo b)
        {
            try
            {
                int size = Math.Max(16, SystemInformation.SmallIconSize.Width), big = size * 4;
                bool light = LightTaskbar();
                Color fg = light ? Color.FromArgb(20, 20, 20) : Color.White;
                if (b.Asleep) fg = light ? Color.FromArgb(130, 130, 130) : Color.FromArgb(140, 140, 146);
                else if (b.Charging) fg = light ? Color.FromArgb(0, 110, 200) : Color.FromArgb(90, 190, 255);
                else if (b.Percent <= low) fg = light ? Color.FromArgb(200, 30, 30) : Color.FromArgb(255, 90, 80);
                using (var hi = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var final = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(hi))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.Clear(Color.Transparent);
                        // Charging shows as the blue colour (and in the tooltip): a bolt would cover the digits
                        if (b.Percent >= 100) FullMark(g, big, fg);
                        else Number(g, big, b.Percent.ToString(), fg);
                    }
                    using (var g = Graphics.FromImage(final))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(hi, new Rectangle(0, 0, size, size));
                    }
                    return Icon.FromHandle(final.GetHicon());
                }
            }
            catch (Exception e) { Log.Write("battery icon: " + e.Message); return null; }
        }

        static void Number(Graphics g, int big, string text, Color c)
        {
            // Largest bold size whose digits fit the square
            float em = big * 0.95f;
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            {
                path.AddString(text, family, (int)FontStyle.Bold, em, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF r = path.GetBounds();
                float scale = Math.Min((big * 0.98f) / r.Width, (big * 0.80f) / r.Height);
                using (var m = new Matrix())
                {
                    m.Translate(big / 2f, big / 2f);
                    m.Scale(scale, scale);
                    m.Translate(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
            }
        }

        static void FullMark(Graphics g, int big, Color c)
        {
            float u = big / 16f;
            var body = new RectangleF(1 * u, 4.5f * u, 12.5f * u, 7f * u);
            using (var pen = new Pen(c, 1.4f * u))
            using (var path = Theme.Round(body, 1.6f * u)) g.DrawPath(pen, path);
            using (var brush = new SolidBrush(c))
            {
                g.FillRectangle(brush, 13.8f * u, 6.5f * u, 1.6f * u, 3f * u);
                using (var fill = Theme.Round(new RectangleF(2.6f * u, 6.1f * u, 9.3f * u, 3.8f * u), 0.8f * u)) g.FillPath(brush, fill);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            BatteryMonitor.Updated -= OnUpdated;
            var keys = new List<string>(icons.Keys);
            foreach (string k in keys) Remove(k);
            BatteryMonitor.Stop();
        }
    }
}
