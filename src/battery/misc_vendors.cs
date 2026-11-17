// WLMouse, Ninjutso, Sprime, Rongyuan and SONiX dongles. Ported from Wireless Battery (wlmouse.cpp, ninjutso.cpp,
// sprime.cpp, rongyuan.cpp, sonix.cpp; GPL-3.0-or-later).
// WLMouse: djnawara/mouse-gauge, Waturus/HaloBattery. Ninjutso / Sprime: NinjaForce and sprime.pro web drivers.
// Rongyuan receivers (Attack Shark, Ajazz, some MSI): qu1ncky/BatteryTray (RongyuanDongleProvider).
// SONiX 2.4 GHz keyboard dongles (AJAZZ AK820 MAX, AK980 V2, AULA F108 Pro): donglover69/sonix-keyboard-studio.
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class WlmouseDriver
    {
        const ushort Vid = 0x36A7;
        // Cables come before receivers: a cable that answers claims its receiver and the mouse is listed once
        class Model { public ushort Pid; public bool Receiver; public string Name; public ushort ReceiverPid; }
        static readonly Model[] Models =
        {
            new Model { Pid = 0xA879, Receiver = false, Name = "WLMouse Sword X 8K", ReceiverPid = 0xA878 },
            new Model { Pid = 0xA884, Receiver = false, Name = "WLMouse Beast X 8K", ReceiverPid = 0xA883 },
            new Model { Pid = 0xA860, Receiver = false, Name = "WLMouse mouse" },
            new Model { Pid = 0xA866, Receiver = false, Name = "WLMouse mouse" },
            new Model { Pid = 0xA867, Receiver = false, Name = "WLMouse mouse" },
            new Model { Pid = 0xA856, Receiver = true, Name = "WLMouse Beast X V2" },
            new Model { Pid = 0xA868, Receiver = true, Name = "WLMouse Beast X Mini Pro" },
            new Model { Pid = 0xA870, Receiver = true, Name = "WLMouse Beast X Pro" },
            new Model { Pid = 0xA878, Receiver = true, Name = "WLMouse Sword X 8K" },
            new Model { Pid = 0xA880, Receiver = true, Name = "WLMouse Beast MAX" },
            new Model { Pid = 0xA883, Receiver = true, Name = "WLMouse Beast X 8K" },
            new Model { Pid = 0xA885, Receiver = true, Name = "WLMouse Beast X Mini" },
        };

        enum Reply { Battery, Asleep, None }

        static Reply Query(HidDev d, out int pct, out bool charging)
        {
            pct = -1; charging = false;
            var req = new byte[65];   // [0] = report id 0
            req[3] = 0x02; req[4] = 0x02; req[6] = 0x83;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!d.SetFeature(req)) return Reply.None;
                for (int read = 0; read < 6; read++)
                {
                    Bat.Sleep(60);
                    byte[] r;
                    if (!d.GetFeature(0x00, out r) || r.Length < 9) return Reply.None;
                    if (r[3] != 0x02 || r[4] != 0x02 || r[6] != 0x83) continue;   // not the reply (yet)
                    if (r[1] == 0xA0) return Reply.Asleep;
                    if (r[1] != 0xA1 && r[1] != 0xA2) continue;   // still our own request echoed back
                    if (r[8] > 100) return Reply.None;
                    pct = r[8];
                    charging = r[7] == 0x01;
                    return Reply.Battery;
                }
            }
            return Reply.None;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (Model m in Models)
            {
                if (ctx.IsClaimed(Vid, m.Pid)) continue;
                var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFFFF && c.FeatLen >= 65);
                var ordered = new List<HidColl>();   // usage 0 answers in both sources: try it first
                foreach (HidColl c in colls) if (c.Usage == 0) ordered.Add(c);
                foreach (HidColl c in colls) if (c.Usage != 0) ordered.Add(c);
                foreach (HidColl c in ordered)
                {
                    int pct;
                    bool charging;
                    Reply rep;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        rep = Query(d, out pct, out charging);
                    }
                    if (rep == Reply.None) continue;
                    var s = new DeviceStatus { Key = "wlmouse:" + Bat.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Mouse, Via = m.Receiver ? "2.4 GHz receiver" : "USB cable" };
                    if (m.Receiver) s.GraceSec = 1800;   // a sleeping mouse can leave the receiver silent
                    if (rep == Reply.Asleep) s.Asleep = true;
                    else { s.Percent = pct; s.Charging = charging; }
                    ctx.Add(s, Vid, m.Pid);
                    if (m.ReceiverPid != 0) ctx.Claim(Vid, m.ReceiverPid);   // same mouse, already listed via its cable
                    break;
                }
            }
        }
    }

    static class NordicStatusDrivers
    {
        // Feature report 05, command 15: [9] battery, [10] charging, [11] full (Sprime), [12] online
        static bool StatusQuery(HidDev d, bool sprime, bool cable, DeviceStatus s)
        {
            var req = new byte[32];
            req[0] = 0x05; req[1] = 0x15; req[4] = 0x01;
            bool answered = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) Bat.Sleep(90);
                if (!d.SetFeature(req)) break;
                Bat.Sleep(15);
                byte[] r;
                if (!d.GetFeature(0x05, out r) || r.Length < 13 || r[0] != 0x05 || r[1] != 0x15 || r[9] > 100) continue;
                answered = true;
                if (sprime)
                {
                    bool full = r[11] != 0;
                    s.Connected = cable || r[12] != 0;   // receiver present, mouse off or out of range
                    s.Percent = !s.Connected ? -1 : full ? 100 : r[9];
                    s.Charging = s.Connected && r[10] != 0 && !full;
                }
                else
                {
                    s.Connected = r[12] != 0;
                    s.Percent = s.Connected ? (r[9] == 0 ? 100 : r[9]) : -1;   // 0 means 100%
                    s.Charging = s.Connected && r[10] != 0;
                }
                if (s.Connected) break;
            }
            return answered;
        }

        public static void PollNinjutso(PollContext ctx)
        {
            const ushort vid = 0x1915, pid = 0xAE1C;
            if (ctx.IsClaimed(vid, pid)) return;
            foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFFA0 && x.Usage == 1 && x.FeatLen >= 32))
            {
                var s = new DeviceStatus();
                using (var d = new HidDev())
                    if (!d.OpenFeatureOnly(c) || !StatusQuery(d, false, false, s)) continue;
                s.Key = "ninjutso:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid);
                s.Name = "Ninjutso Sora V2";
                s.Kind = DeviceKind.Mouse;
                s.Via = "2.4 GHz receiver";
                ctx.Add(s, vid, pid);
                break;
            }
        }

        public static void PollSprime(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x1915))
            {
                ushort vid = vp[0], pid = vp[1];
                bool cable = pid == 0xAC11 || pid == 0xAC12 || pid == 0xAC13;
                bool receiver = pid == 0xAC1C || pid == 0xAD8C;
                if (!cable && !receiver) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFFA0 && x.Usage == 1 && x.FeatLen >= 32))
                {
                    var s = new DeviceStatus();
                    using (var d = new HidDev())
                        if (!d.OpenFeatureOnly(c) || !StatusQuery(d, true, cable, s)) continue;
                    s.Key = "sprime:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid);
                    s.Name = "Sprime PM1";
                    s.Kind = DeviceKind.Mouse;
                    s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }
    }

    static class RongyuanDriver
    {
        static readonly uint[] Receivers =
        {
            0x0DB0197E, 0x0DB08E9A, 0x145F0376, 0x145F038D, 0x14A5C045, 0x25AA2008, 0x25AA4008, 0x25AA4018, 0x31514011,
            0x31514014, 0x31514017, 0x31514021, 0x31514027, 0x3151402D, 0x31514038, 0x31515006, 0x31515007, 0x31515008,
            0x31515020, 0x31515026, 0x3151502B, 0x31515031, 0x31515037, 0x31515038, 0x3151503D, 0x3151503F, 0x31515040,
            0x31515042, 0x31515045, 0x3151504B, 0x3151504E, 0x31515050, 0x31515052, 0x31515055, 0x3151505A, 0x342DE3E0,
            0x342DE3E1, 0x342DE3E2, 0x342DE3E6, 0x342DE3E7, 0x342DE3E8, 0x342DE3F5, 0x342DE3F6, 0x342DE3F7, 0x342DE3FB,
            0x342DE3FC, 0x342DE3FD, 0x347AA302, 0x347AA305, 0x374AA205, 0x374AA217, 0x374AA220, 0x374AA223, 0x374AA226,
            0x374AA229, 0x374AA234, 0x374AA237, 0x374AA251, 0x374AA263, 0x374AA268, 0x374AA311, 0x374AA314, 0x374AA319,
            0x374AA322, 0x374AA329, 0x374AA355, 0x379A1804, 0x379A1807, 0x38A90002, 0x38EE0000, 0x38EE0002, 0x38EE0039,
            0x391D1A04, 0x391D1A08, 0x391D1A0E, 0x391D1A13, 0x391D1A25, 0x391D1A2B, 0x391D1A34, 0x391D2017, 0x391D2A05,
        };

        static bool IsReceiver(ushort vid, ushort pid) { return Array.IndexOf(Receivers, ((uint)vid << 16) | pid) >= 0; }

        static bool QueryStatus(HidDev d, out byte[] reply)
        {
            reply = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Bat.Sleep(100);
                var cmd = new byte[d.Coll.FeatLen];
                cmd[1] = 0xF7;
                if (!d.SetFeature(cmd)) return false;
                Bat.Sleep(20);
                if (d.GetFeature(0, out reply) && reply.Length >= 7 && (reply[2] | reply[3] | reply[4] | reply[5] | reply[6]) != 0) return true;
            }
            return false;
        }

        public static void Poll(PollContext ctx)
        {
            var products = new List<uint>();
            foreach (HidColl c in ctx.Colls)
            {
                uint id = ((uint)c.Vid << 16) | c.Pid;
                if (IsReceiver(c.Vid, c.Pid) && !ctx.IsClaimed(c.Vid, c.Pid) && !products.Contains(id)) products.Add(id);
            }
            foreach (uint id in products)
            {
                ushort vid = (ushort)(id >> 16), pid = (ushort)(id & 0xFFFF);
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Iface == 2 && x.Page == 0xFFFF && x.Usage == 0x0002 && x.FeatLen >= 7))
                {
                    byte[] r;
                    using (var d = new HidDev())
                        if (!d.OpenFeatureOnly(c) || !QueryStatus(d, out r)) continue;
                    string product = Hid.Product(c);
                    bool generic = product.Length == 0 || product.Contains("Receiver") || product.Contains("2.4G");
                    // Slot layout: [2] keyboard %, [3] mouse %, [4] keyboard offline, [5] mouse offline
                    for (int slot = 0; slot < 2; slot++)
                    {
                        bool keyboard = slot == 0;
                        if (r[keyboard ? 4 : 5] != 0) continue;   // nothing connected in this slot
                        byte level = r[keyboard ? 2 : 3];
                        var s = new DeviceStatus { Key = "rongyuan:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid) + (keyboard ? ":kb" : ":ms") };
                        if (vid == 0x3151 && pid == 0x402D && !keyboard) s.Name = "Attack Shark R3";
                        else if (generic) s.Name = keyboard ? "Wireless keyboard" : "Wireless mouse";
                        else s.Name = product;
                        s.Kind = keyboard ? DeviceKind.Keyboard : DeviceKind.Mouse;
                        s.Via = "2.4 GHz receiver";
                        if (level >= 1 && level <= 100) s.Percent = level;
                        ctx.Out.Add(s);
                    }
                    ctx.Claim(vid, pid);
                    break;
                }
            }
        }
    }

    static class SonixDriver
    {
        static readonly ushort[][] Dongles = { new ushort[] { 0x05AC, 0x024F }, new ushort[] { 0x0C45, 0xFEFE } };
        const int Len = 32;   // payload bytes after the report id; the last one is the checksum

        static byte Sum(byte[] b, int from, int n)
        {
            int s = 0;
            for (int i = from; i < from + n; i++) s += b[i];
            return (byte)(s & 0xFF);
        }

        // The level byte, or -1 when the dongle didn't answer
        static int QueryLevel(HidDev d)
        {
            var req = new byte[1 + Len];   // [0] = report id 0
            req[1] = 0x20; req[2] = 0x01;
            req[Len] = Sum(req, 1, Len - 1);
            d.Flush();
            if (!d.Write(req)) return -1;
            long end = Bat.Now + 500;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0) return -1;
                byte[] r = null;
                int n = d.Read(ref r, left);   // report id 0 is stripped: r[0] is the first payload byte
                if (n < 0) return -1;
                if (n < Len || r[0] != 0x20 || r[1] != 0x01) continue;
                if (r[Len - 1] != Sum(r, 0, Len - 1)) continue;
                return r[3];
            }
        }

        static string KeyboardName(HidColl c)
        {
            string name = Hid.Product(c);
            if (name.Length > 5 && name.EndsWith(" 2.4G", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 5);
            return name.Length == 0 ? "Wireless keyboard" : name;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in Dongles)
            {
                ushort vid = vp[0], pid = vp[1];
                if (ctx.IsClaimed(vid, pid)) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Iface == 3 && x.Page == 0xFF60 && x.Usage == 0x0061 && x.OutLen == 1 + Len && x.InLen == 1 + Len))
                {
                    int level;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        level = QueryLevel(d);   // the first request can go unanswered while the link wakes up; retry once
                        if (level < 0) level = QueryLevel(d);
                    }
                    if (level < 0) continue;
                    var s = new DeviceStatus { Key = "sonix:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = KeyboardName(c), Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver" };
                    s.Connected = level >= 1 && level <= 100;   // 0: keyboard off, out of range or in another mode
                    if (s.Connected) s.Percent = level;
                    s.GraceSec = 1800;   // keep the last value if a sleeping keyboard stops answering
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }
    }
}
