// Kysona-protocol mice (Kysona, Pulsar XS-1, ATK / VXE, Hitscan, G-Wolves, HECATE, Redragon Compx), Pulsar X2 V3,
// the ATK / VXE NK NANO dongle and ATK N9 headsets. Ported from Wireless Battery (kysona.cpp, pulsar.cpp,
// atk_bitmouse.cpp, atk_serial.cpp; GPL-3.0-or-later).
// Kysona: 17-byte report 08 [08][cmd][0 x14][checksum], checksum = 0x55 - sum(0..15); cmd 03 online, 04 battery
//   ([6] %, [7] charging, [8..9] mV). Linux hid-kysona (Lode Willems), vxe-r1-pro-max-battery, BatteryTray PR #1.
// Pulsar X2 V3: 64-byte feature packet, battery register 08 81 01 (darthsoup/PulsarBattery, pulsar-mouse-linux).
// ATK BITMOUSE (NK dongle 373B:1216): output report 08 on FF05:0001 (HaloBattery PR #350, qwr79/mouse-battery).
// ATK N9 Pro / N9 Ultra: USB virtual COM port, 9600 8N1, 64-byte frames (ATK HUB).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KomorebiTray
{
    static class KysonaDriver
    {
        // Redragon uses Compx's own ids, and its receiver only calls itself "2.4G Wireless Receiver"
        static bool IsRedragon(ushort vid, ushort pid) { return vid == 0x3554 && (pid == 0xF55D || pid == 0xF5D5 || pid == 0xF55E); }
        static bool IsVxeR1(ushort vid, ushort pid)
        {
            return vid == 0x3554 && (pid == 0xF58A || pid == 0xF58C || pid == 0xF58E || pid == 0xF58F || pid == 0xF505 || pid == 0xF506);
        }

        static bool IsCable(ushort vid, ushort pid)
        {
            bool hecateCable = vid == 0x35BB && (pid == 0xD3FA || pid == 0xD3FC || pid == 0xD3FD);
            return (vid == 0x33E4 && GloriousDriver.GWolvesIsCable(pid)) || hecateCable ||
                   (vid == 0x3554 && (pid == 0xF55E || pid == 0xF58C || pid == 0xF58F || pid == 0xF506));
        }

        static byte Checksum(byte[] b)
        {
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += b[i];
            return (byte)(0x55 - sum);
        }

        static bool Query(HidDev d, byte cmd, ref byte[] r)
        {
            var req = new byte[17];
            req[0] = 0x08;
            req[1] = cmd;
            req[16] = Checksum(req);
            d.Flush();
            if (!d.Write(req)) return false;
            long end = Bat.Now + 500;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref r, left) < 17) return false;
                if (r[0] == 0x08 && r[1] == cmd && r[16] == Checksum(r)) return true;
            }
        }

        static string CleanName(string n, ushort vid, ushort pid)
        {
            if (vid == 0x33E4) return GloriousDriver.GWolvesModelName(pid);
            if (IsRedragon(vid, pid)) return "Redragon wireless mouse";   // shared by several models
            if (IsVxeR1(vid, pid)) return "VXE Dragonfly R1";              // receivers call themselves "VXE NordicMouse 1K Dongle"
            foreach (string suffix in new[] { " Dongle", " Receiver", " 2.4G", " Wireless Receiver" })
                if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - suffix.Length);
            if (vid == 0x3770) return "Hitscan Hyperlight";   // the receiver only calls itself "Hitscan USB Receiver"
            if (vid == 0x35BB) return "HECATE mouse";
            if (n.Length == 0) n = vid == 0x373B ? "ATK wireless mouse" : "Wireless mouse";
            return n;
        }

        public static void Poll(PollContext ctx)
        {
            var seenNames = new List<string>();
            foreach (ushort vendor in new ushort[] { 0x3554, 0x373B, 0x3770, 0x33E4, 0x3710, 0x35BB })
                foreach (ushort[] vp in ctx.Products(vendor))
                {
                    ushort vid = vp[0], pid = vp[1];
                    // ATK N9 headsets speak a serial protocol: never send them mouse frames
                    if (vid == 0x373B && (pid == 0x11CA || pid == 0x11CB || pid == 0x1203 || pid == 0x1204)) continue;
                    var colls = ctx.Find(vid, pid, c => (c.OutLen == 17 && c.InLen == 17) || (c.Page >= 0xFF00 && c.OutLen >= 17 && c.InLen >= 17));
                    // The battery channel is FF02:0002 where it exists; try it first (stable order otherwise)
                    var ordered = new List<HidColl>();
                    foreach (HidColl c in colls) if (c.Page == 0xFF02 && c.Usage == 0x0002) ordered.Add(c);
                    foreach (HidColl c in colls) if (!(c.Page == 0xFF02 && c.Usage == 0x0002)) ordered.Add(c);
                    foreach (HidColl c in ordered)
                    {
                        var s = new DeviceStatus();
                        bool answered = false;
                        using (var d = new HidDev())
                        {
                            if (!d.Open(c)) continue;
                            byte[] r = null;
                            if (Query(d, 0x03, ref r)) { answered = true; s.Connected = r[6] != 0; }
                            if ((!answered || s.Connected) && Query(d, 0x04, ref r))
                            {
                                answered = true;
                                s.Connected = true;
                                if (r[6] <= 100) s.Percent = r[6];
                                s.Charging = r[7] != 0;
                                s.Millivolts = (r[8] << 8) | r[9];
                            }
                            else if (answered && s.Connected) s.Asleep = true;   // online but the mouse didn't answer
                        }
                        if (!answered) continue;
                        if (!s.Connected) { ctx.Claim(vid, pid); break; }
                        s.Key = "kysona:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid);
                        s.Name = CleanName(Hid.Product(c), vid, pid);
                        s.Kind = Bat.KindFromCollections(ctx.Colls, vid, pid);
                        if (s.Kind == DeviceKind.Other) s.Kind = DeviceKind.Mouse;
                        s.Via = IsCable(vid, pid) ? "USB cable" : "2.4 GHz receiver";
                        s.GraceSec = 1800;
                        // A mouse plugged in for charging can show up twice (cable + dongle); keep one
                        if (seenNames.Contains(s.Name))
                        {
                            ctx.Claim(vid, pid);
                            foreach (DeviceStatus o in ctx.Out) if (o.Name == s.Name && s.Charging) o.Charging = true;
                        }
                        else { seenNames.Add(s.Name); ctx.Add(s, vid, pid); }
                        break;
                    }
                }
        }
    }

    static class PulsarDriver
    {
        const ushort Vid = 0x3710;
        class Product { public ushort Pid; public bool Cable; public string Name; }
        static readonly Product[] Products =
        {
            new Product { Pid = 0x3402, Cable = true, Name = "Pulsar X2 V3" },
            new Product { Pid = 0x3406, Cable = true, Name = "Pulsar X2 V3 eS" },
            new Product { Pid = 0x5402, Cable = false },   // 1K dongle
            new Product { Pid = 0x5403, Cable = false },   // 8K dongle
        };

        enum Reply { Battery, Offline, None }

        static Reply ReadBattery(HidDev d, out int pct)
        {
            pct = -1;
            var req = new byte[65];   // [0] = report id 0, then the 64-byte packet
            req[1 + 1] = 0x08; req[1 + 2] = 0x81; req[1 + 3] = 0x01;
            int sum = 0;
            for (int i = 0; i < 62; i++) sum += req[1 + i];
            req[1 + 62] = (byte)(sum & 0xFF);
            req[1 + 63] = (byte)((sum >> 8) & 0xFF);
            bool sawOffline = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!d.SetFeature(req)) return Reply.None;
                for (int read = 0; read < 6; read++)
                {
                    Bat.Sleep(50);
                    byte[] r;
                    if (!d.GetFeature(0x00, out r) || r.Length < 65) return Reply.None;
                    if (r[1 + 1] != 0x08 || r[1 + 2] != 0x81 || r[1 + 3] != 0x01) continue;   // not our reply (yet)
                    if (r[1] == 0x01)
                    {
                        if (r[1 + 6] > 100) return Reply.None;   // FF = no reading
                        pct = r[1 + 6];
                        return Reply.Battery;
                    }
                    sawOffline = r[1] == 0x05;   // 02 (or 05) = pending: keep polling
                }
            }
            return sawOffline ? Reply.Offline : Reply.None;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (Product prod in Products)
            {
                if (ctx.IsClaimed(Vid, prod.Pid)) continue;
                var colls = ctx.Find(Vid, prod.Pid, c => c.FeatLen >= 65);
                colls.Sort((a, b) => b.Iface.CompareTo(a.Iface));   // interface 2 rejects SET_FEATURE: higher first
                foreach (HidColl c in colls)
                {
                    int pct;
                    Reply rep;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        rep = ReadBattery(d, out pct);
                    }
                    if (rep == Reply.None) continue;
                    var s = new DeviceStatus { Key = "pulsar:" + Bat.Hex4(prod.Pid), Kind = DeviceKind.Mouse };
                    if (prod.Cable) { s.Name = prod.Name; s.Via = "USB cable"; s.Charging = true; }
                    else
                    {
                        string n = Hid.Product(c);
                        bool generic = n.Length == 0 || n.Contains("Dongle") || n.Contains("Receiver");
                        s.Name = generic ? "Pulsar wireless mouse" : n;   // the dongles are shared by several models
                        s.Via = "2.4 GHz receiver";
                        s.Connected = rep == Reply.Battery && pct > 0;
                    }
                    if (rep == Reply.Battery && (pct > 0 || prod.Cable)) s.Percent = pct;
                    ctx.Add(s, Vid, prod.Pid);
                    break;
                }
            }
        }
    }

    static class AtkBitmouseDriver
    {
        const ushort Vid = 0x373B, NanoDongle = 0x1216;

        // Command cmd -> one-byte payload, or -1 without a valid reply
        static int Query(HidDev d, byte cmd)
        {
            var req = new byte[64];
            req[0] = 0x08; req[2] = 0x72; req[3] = 0x02; req[5] = 0x01; req[6] = cmd; req[7] = 0x01;
            int sum = 0;
            for (int i = 2; i < req.Length; i++) sum += req[i];
            req[1] = (byte)(sum & 0xFF);
            d.Flush();
            if (!d.Write(req)) return -1;
            long end = Bat.Now + 700;
            byte[] r = null;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0) return -1;
                if (d.Read(ref r, left) < 0) return -1;
                if (r.Length < 8 || r[0] != 0x08 || r[1] != 0x72) continue;
                if (r[5] != cmd) continue;   // a reply to another request
                if (r[2] == 0xFF) return -1;
                return r[7];
            }
        }

        public static void Poll(PollContext ctx)
        {
            if (ctx.IsClaimed(Vid, NanoDongle)) return;
            foreach (HidColl c in ctx.Find(Vid, NanoDongle, x => x.Page == 0xFF05 && x.Usage == 0x0001 && x.OutLen >= 64 && x.InLen >= 8))
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    int level = Query(d, 0x07);
                    if (level < 1 || level > 100) continue;   // asleep / off, or no valid reading
                    var s = new DeviceStatus { Key = "atknk:" + Bat.Hex4(NanoDongle), Name = "ATK / VXE NK mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
                    s.GraceSec = 1800;   // the dongle stops answering while the mouse sleeps: keep the last value
                    s.Percent = level;
                    s.Charging = Query(d, 0x0F) == 1;
                    ctx.Add(s, Vid, NanoDongle);
                    break;
                }
        }
    }

    static class AtkSerialDriver
    {
        class Model { public ushort Pid; public string Name; public bool Wired; }
        static readonly Model[] Models =
        {
            new Model { Pid = 0x11CB, Name = "ATK N9 Pro", Wired = false },
            new Model { Pid = 0x11CA, Name = "ATK N9 Pro", Wired = true },
            new Model { Pid = 0x1204, Name = "ATK N9 Ultra", Wired = false },
            new Model { Pid = 0x1203, Name = "ATK N9 Ultra", Wired = true },
        };

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DevProps.DEVINFO info, int scope, int profile, int keyType, int access);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool BuildCommDCB(string def, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle h, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
        [StructLayout(LayoutKind.Sequential)]
        struct DCB
        {
            public int DCBlength, BaudRate, Flags;
            public ushort wReserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits;
            public byte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort wReserved1;
        }
        [StructLayout(LayoutKind.Sequential)] struct COMMTIMEOUTS { public int ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant; }

        class Port { public string Name; public Model Model; }

        static List<Port> FindPorts()
        {
            var output = new List<Port>();
            var ports = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");   // GUID_DEVCLASS_PORTS
            IntPtr di = SetupDiGetClassDevs(ref ports, IntPtr.Zero, IntPtr.Zero, 0x2);
            if (di == new IntPtr(-1)) return output;
            try
            {
                var dd = new DevProps.DEVINFO();
                dd.cbSize = Marshal.SizeOf(dd);
                var sb = new StringBuilder(512);
                for (int i = 0; DevProps.SetupDiEnumDeviceInfo(di, i, ref dd); i++)
                {
                    int req;
                    sb.Length = 0;
                    if (!DevProps.SetupDiGetDeviceInstanceId(di, ref dd, sb, 512, out req)) continue;
                    string id = sb.ToString().ToUpperInvariant();
                    Model model = null;
                    foreach (Model m in Models) if (id.Contains("VID_373B&PID_" + Bat.Hex4(m.Pid))) model = m;
                    if (model == null) continue;
                    IntPtr k = SetupDiOpenDevRegKey(di, ref dd, 1, 0, 1, 0x20019);   // DICS_FLAG_GLOBAL, DIREG_DEV, KEY_READ
                    if (k == new IntPtr(-1)) continue;
                    using (var key = Microsoft.Win32.RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(k, true)))
                    {
                        string name = key.GetValue("PortName") as string;
                        if (!string.IsNullOrEmpty(name)) output.Add(new Port { Name = name, Model = model });
                    }
                }
            }
            finally { DevProps.SetupDiDestroyDeviceInfoList(di); }
            return output;
        }

        class Serial : IDisposable
        {
            SafeFileHandle h;

            public Serial(string port)
            {
                h = HidNative.CreateFile("\\\\.\\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) return;
                var dcb = new DCB();
                dcb.DCBlength = Marshal.SizeOf(dcb);
                GetCommState(h, ref dcb);
                var to = new COMMTIMEOUTS { ReadIntervalTimeout = 50, ReadTotalTimeoutConstant = 300, WriteTotalTimeoutConstant = 300 };
                // 9600 8N1, no flow control, DTR / RTS on
                if (!BuildCommDCB("baud=9600 parity=N data=8 stop=1 octs=off odsr=off dtr=on rts=on xon=off idsr=off", ref dcb) ||
                    !SetCommState(h, ref dcb) || !SetCommTimeouts(h, ref to)) Dispose();
                else PurgeComm(h, 0x0008 | 0x0004);   // PURGE_RXCLEAR | PURGE_TXCLEAR
            }

            public bool Ok { get { return h != null && !h.IsInvalid && !h.IsClosed; } }
            public void Dispose() { if (h != null) h.Dispose(); }

            // Sends (cmd, sub) and returns the data bytes of the matching 64-byte reply frame
            public bool Command(byte cmd, byte sub, out byte[] data)
            {
                data = null;
                var frame = new byte[64];
                frame[0] = 2; frame[1] = cmd; frame[2] = sub;
                int n;
                if (!WriteFile(h, frame, 64, out n, IntPtr.Zero) || n != 64) return false;
                var buf = new List<byte>();
                long end = Bat.Now + 800;
                var chunk = new byte[128];
                while (end - Bat.Now > 0)
                {
                    if (!ReadFile(h, chunk, chunk.Length, out n, IntPtr.Zero)) return false;
                    for (int i = 0; i < n; i++) buf.Add(chunk[i]);
                    while (buf.Count >= 64)   // the headset also pushes unsolicited frames; skip those
                    {
                        if (buf[1] == cmd && buf[2] == sub) { data = buf.GetRange(3, 61).ToArray(); return true; }
                        buf.RemoveRange(0, 64);
                    }
                }
                return false;
            }
        }

        public static void Poll(PollContext ctx)
        {
            foreach (Port p in FindPorts())
            {
                ushort pid = p.Model.Pid;
                if (ctx.IsClaimed(0x373B, pid)) continue;
                using (var port = new Serial(p.Name))
                {
                    if (!port.Ok) continue;   // in use (ATK app open) or not a working port
                    byte[] data;
                    if (!port.Command(0x00, 0x04, out data)) continue;
                    var s = new DeviceStatus { Key = "atk-serial:" + Bat.Hex4(pid), Name = p.Model.Name, Kind = DeviceKind.Headset };
                    s.Via = p.Model.Wired ? "USB cable" : "2.4 GHz receiver";
                    s.Connected = data[1] == 1;
                    if (s.Connected && port.Command(0x00, 0x03, out data))
                    {
                        s.Percent = Bat.Clamp100(data[1] & 0x7F);
                        s.Charging = (data[1] & 0x80) != 0;
                    }
                    ctx.Add(s, 0x373B, pid);
                }
            }
        }
    }
}
