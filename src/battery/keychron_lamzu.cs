// Keychron mice (classic, 8K, 4K, Nape Pro), LAMZU, Endgame Gear 4K and VAXEE. Ported from Wireless Battery
// (keychron.cpp, lamzu.cpp, endgame.cpp, vaxee.cpp; GPL-3.0-or-later).
// Keychron: Launcher protocol (launcher.keychron.com); Nape Pro via the Link-KM raw HID channel (nvalis/nape_pro,
// gtun/KeychronNapeBatteryWidget, ky0209/NapeBar). LAMZU: Aurora web driver (lamzu.net). Endgame Gear: johanneszab/vole,
// Janlhin/Endgame_Gear_Web_Configurator. VAXEE: stuffz/mouse-battery-monitor (docs/VAXEE.md).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class KeychronDriver
    {
        static void SetChecksum(byte[] payload)   // 64 bytes without the report id
        {
            int sum = 0;
            for (int i = 0; i < 63; i++) sum += payload[i];
            payload[63] = (byte)(0xA1 - sum);
        }

        static string CleanName(string n)
        {
            if (n.Length == 0 || n.Contains("Link") || n.Contains("Receiver")) return "Keychron mouse";
            return n;
        }

        // [cmd][00][81][sub]; waits up to 600 ms for a reply starting with cmd whose byte 3 is sub
        static bool Query(HidDev d, byte cmd, ref byte[] r, byte sub = 0x01)
        {
            var payload = new byte[64];
            payload[0] = cmd; payload[2] = 0x81; payload[3] = sub;
            SetChecksum(payload);
            var req = new byte[65];   // report id 0 (unnumbered)
            Array.Copy(payload, 0, req, 1, 64);
            d.Flush();
            if (!d.Write(req)) return false;
            long end = Bat.Now + 600;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref r, left) < 13) return false;
                if (r[0] == cmd && r[3] == sub) return true;
            }
            return false;
        }

        const ushort LinkKm = 0xD026, NapeUsb = 0x0440, NapeInSlot = 0x4004;

        // Raw HID (FF60:61) request: report id 0 + 32 bytes; skips unsolicited notifications
        static bool RawQuery(HidDev d, byte[] cmd, ref byte[] r)
        {
            var req = new byte[33];
            Array.Copy(cmd, 0, req, 1, cmd.Length);
            d.Flush();
            if (!d.Write(req)) return false;
            long end = Bat.Now + 600;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                int n = d.Read(ref r, left);
                if (n < 0) return false;
                if (n < cmd.Length + 2) continue;
                bool match = true;
                for (int i = 0; i < cmd.Length; i++) match = match && r[i] == cmd[i];
                if (match) return true;
            }
            return false;
        }

        static void PollNape(PollContext ctx)
        {
            foreach (ushort pid in new[] { LinkKm, NapeUsb })
                foreach (HidColl c in ctx.Find(0x3434, pid, x => x.Page == 0xFF60 && x.Usage == 0x61 && x.OutLen == 33))
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        byte[] r = null;
                        if (pid == LinkKm)
                        {
                            // Only ask when a Nape Pro sits in a receiver slot and is connected
                            if (!RawQuery(d, Bat.B(0xB2), ref r) || r.Length < 17) continue;
                            bool connected = false;
                            foreach (int off in new[] { 2, 7, 12 })
                                if (((r[off + 2] << 8) | r[off + 3]) == NapeInSlot) connected = r[off + 4] == 1;
                            if (!connected) continue;
                        }
                        if (!RawQuery(d, Bat.B(0xA7, 0x31), ref r) || r[2] > 100) continue;
                        ctx.Add(new DeviceStatus
                        {
                            Key = "keychron:nape", Name = "Keychron Nape Pro", Kind = DeviceKind.Mouse,
                            Via = pid == LinkKm ? "2.4 GHz receiver" : "USB cable", Percent = r[2], Charging = r[3] != 0,
                        }, 0x3434, pid);
                        return;
                    }
        }

        static string Name4k(ushort productId)
        {
            switch (productId)
            {
                case 0x0620: return "Keychron M3 mini 4K";
                case 0x0621: return "Keychron M4 4K";
                case 0x0622: return "Keychron M3 mini 4K";
                case 0x0624: return "Keychron M6 4K";
                case 0x07A0: return "Keychron M3 4K";
            }
            return null;
        }

        public static void Poll(PollContext ctx)
        {
            PollNape(ctx);
            foreach (ushort[] vp in ctx.Products(0x3434))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page >= 0xFF00 && x.OutLen == 65 && x.InLen == 65))
                {
                    DeviceStatus found = null;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        byte[] r = null, r1 = null;
                        int percent = -1;
                        bool charging = false, wireless = true;
                        bool got1 = Query(d, 0x01, ref r1);
                        ushort tag = got1 ? (ushort)(r1[6] | (r1[7] << 8)) : (ushort)0;
                        if (got1 && c.Page == 0xFF0A && c.Usage == 0x01 && tag != 0x3434 && tag != 0x362D)
                        {
                            // 4K mouse on the 4K Link receiver (Launcher protocol "4k")
                            if (!Query(d, 0x41, ref r) || r[6] < 1 || r[6] > 100) continue;
                            var s4 = new DeviceStatus { Key = "keychron:" + Bat.Hex4(pid), Name = CleanName(Hid.Product(c)) };
                            byte[] st = null;
                            if (Query(d, 0x40, ref st, 0x00))
                            {
                                string n = Name4k((ushort)(st[4] | (st[5] << 8)));
                                if (n != null) s4.Name = n;
                            }
                            s4.Kind = DeviceKind.Mouse;
                            s4.Via = "2.4 GHz receiver";
                            s4.Percent = r[6];
                            s4.Charging = r[5] != 0;
                            found = s4;
                        }
                        else
                        {
                            if (got1 && tag == 0x3434 && r1[11] <= 100) { percent = r1[11]; charging = r1[10] != 0; }   // classic protocol
                            else if (Query(d, 0x41, ref r) && r[10] <= 3 && r[11] <= 100) { percent = r[11]; charging = r[10] == 1; }   // 8K on its receiver
                            else if (got1 && r1[10] <= 3 && r1[11] >= 1 && r1[11] <= 100 && r1[5] >= 1 && r1[5] <= 8)
                            {
                                percent = r1[11];   // 8K mouse on the USB cable
                                charging = r1[10] == 1;
                                wireless = false;
                            }
                            if (percent < 0) continue;
                            found = new DeviceStatus
                            {
                                Key = "keychron:" + Bat.Hex4(pid), Name = CleanName(Hid.Product(c)), Kind = DeviceKind.Mouse,
                                Via = wireless ? "2.4 GHz receiver" : "USB cable", Percent = percent, Charging = charging,
                            };
                        }
                    }
                    ctx.Add(found, vid, pid);
                    break;
                }
            }
        }
    }

    static class LamzuDriver
    {
        class M { public ushort Vid; public ushort[] Pids; public string Name; }
        static readonly M[] Models =
        {
            new M { Vid = 0x373E, Pids = new ushort[] { 0x001C, 0x001D, 0x001E }, Name = "LAMZU Maya X" },
            new M { Vid = 0x373E, Pids = new ushort[] { 0x0016 }, Name = "LAMZU Maya X V2" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x001F }, Name = "LAMZU Maya X V2" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0005, 0x000B, 0x000C }, Name = "LAMZU Tachi" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x001C, 0x001E }, Name = "LAMZU Tachi Lite" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0009, 0x000F, 0x0010 }, Name = "LAMZU Inca" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0011, 0x0013, 0x0015 }, Name = "LAMZU Maya" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0007, 0x000D, 0x000E }, Name = "LAMZU Paro" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0017, 0x0019, 0x001B }, Name = "LAMZU Thorn" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0021, 0x0003, 0x0023 }, Name = "LAMZU Thorn V2" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0030, 0x0032, 0x0040 }, Name = "LAMZU Thorn V2" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0025, 0x0027 }, Name = "LAMZU Atlantis OG Champion" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0028, 0x002B, 0x002A }, Name = "LAMZU Atlantis Mini" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x003E, 0x003F }, Name = "LAMZU Atlantis Mini" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x002C, 0x0052 }, Name = "LAMZU Maya M" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x003A, 0x003B, 0x0044 }, Name = "LAMZU Maya X" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x003C, 0x0050 }, Name = "LAMZU Maya" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0042, 0x0056 }, Name = "LAMZU Orcus" },
            new M { Vid = 0x37B0, Pids = new ushort[] { 0x0046, 0x0054 }, Name = "LAMZU Mini" },
        };

        static string ModelName(ushort vid, ushort pid)
        {
            foreach (M m in Models) if (m.Vid == vid && Array.IndexOf(m.Pids, pid) >= 0) return m.Name;
            return null;
        }

        static readonly ushort[] CablePids = { 0x0005, 0x0007, 0x0009, 0x0011, 0x0017, 0x001C, 0x001F, 0x0021, 0x0025, 0x0028, 0x002C, 0x0030,
                                               0x0034, 0x0038, 0x003A, 0x003C, 0x003E, 0x0040, 0x0042, 0x0044, 0x0046, 0x0050, 0x0052, 0x0056 };
        static bool IsCable(ushort vid, ushort pid) { return vid == 0x373E ? pid == 0x001C : Array.IndexOf(CablePids, pid) >= 0; }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort vendor in new ushort[] { 0x373E, 0x37B0 })
                foreach (ushort[] vp in ctx.Products(vendor))
                {
                    ushort vid = vp[0], pid = vp[1];
                    foreach (HidColl c in ctx.Find(vid, pid, x => x.FeatLen == 65))
                    {
                        byte[] r;
                        using (var d = new HidDev())
                        {
                            if (!d.OpenFeatureOnly(c)) continue;
                            var req = new byte[65];   // [0] = report id 0
                            req[3] = 0x02; req[4] = 0x02; req[6] = 0x83;
                            if (!d.SetFeature(req)) continue;
                            Bat.Sleep(100);
                            if (!d.GetFeature(0x00, out r) || r.Length < 10) continue;
                        }
                        int chg, pct;
                        if (r[1] == 0xA1 && r[4] == 0x02 && r[6] == 0x83) { chg = r[7]; pct = r[8]; }
                        else if (r[0] == 0xA1 && r[3] == 0x02 && r[5] == 0x83) { chg = r[6]; pct = r[7]; }
                        else continue;
                        if (pct > 100) continue;
                        bool cable = IsCable(vid, pid);
                        var s = new DeviceStatus { Key = "lamzu:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = ModelName(vid, pid) ?? "LAMZU wireless mouse", Kind = DeviceKind.Mouse };
                        s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                        s.Connected = pct > 0 || cable;   // 0 from a dongle = mouse off or out of range
                        if (pct > 0) s.Percent = pct;
                        s.Charging = chg == 1;
                        ctx.Add(s, vid, pid);
                        break;
                    }
                }
        }
    }

    static class EndgameDriver
    {
        const ushort Vid = 0x3367, Dongle4K = 0x1970;
        static readonly Dictionary<string, string> lastName = new Dictionary<string, string>();   // the mouse only names itself while awake

        static string MouseName(ushort pid)
        {
            switch (pid)
            {
                case 0x1972: return "Endgame Gear OP1w 4k";
                case 0x1984: return "Endgame Gear OP1w 4k v2";
                case 0x1968: return "Endgame Gear XM2w 4k";
                case 0x1982: return "Endgame Gear XM2w 4k v2";
                default: return null;
            }
        }

        // Feature report A1: [A1][cmd][target][len][00...]; returns the status byte, or -1 on I/O error
        static int Command(HidDev d, byte cmd, byte target, out byte[] r)
        {
            r = null;
            var req = new byte[64];
            req[0] = 0xA1; req[1] = cmd; req[2] = target;
            if (!d.SetFeature(req)) return -1;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Bat.Sleep(150 + attempt * 100);
                if (!d.GetFeature(0xA1, out r) || r.Length < 20 || r[0] != 0xA1) return -1;
                if (r[1] != 0x03) return r[1];   // 03 = busy, read again
            }
            return 0x03;
        }

        public static void Poll(PollContext ctx)
        {
            if (ctx.IsClaimed(Vid, Dongle4K)) return;
            foreach (HidColl c in ctx.Find(Vid, Dongle4K, x => x.Page == 0xFF01 && x.Usage == 0x0002 && x.FeatLen >= 64))
                using (var d = new HidDev())
                {
                    if (!d.OpenFeatureOnly(c)) continue;
                    byte[] r, p;
                    int st = Command(d, 0xB4, 0x00, out r);
                    if (st != 0x01 && st != 0x08 && st != 0x11)
                    {
                        // Unexpected status or no answer: probe the dongle and the mouse once, then retry
                        Command(d, 0x0F, 0x01, out p);
                        Command(d, 0x0F, 0x0F, out p);
                        st = Command(d, 0xB4, 0x00, out r);
                    }
                    bool awake = st == 0x01 && r[16] <= 100;
                    bool asleep = st == 0x08 || st == 0x11;
                    if (!awake && !asleep) continue;
                    var s = new DeviceStatus { Key = "endgame:" + Bat.Hex4(Dongle4K), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Asleep = asleep };
                    if (awake)
                    {
                        s.Percent = r[16];
                        byte[] info;
                        if (Command(d, 0x0E, 0x00, out info) == 0x01 && info[16] == 0x67 && info[17] == 0x33)
                        {
                            string n = MouseName((ushort)(info[18] | (info[19] << 8)));
                            if (n != null) lastName[c.Path] = n;
                        }
                    }
                    string nm;
                    s.Name = lastName.TryGetValue(c.Path, out nm) ? nm : "Endgame Gear 4k wireless mouse";
                    ctx.Add(s, Vid, Dongle4K);
                    break;
                }
        }
    }

    static class VaxeeDriver
    {
        const ushort Vid = 0x3057, Receiver = 0x1002, Cable = 0x1011;
        static readonly Dictionary<string, DeviceStatus> lastGood = new Dictionary<string, DeviceStatus>();

        static bool Query(HidDev d, byte command, out byte value)
        {
            value = 0;
            var request = new byte[64];
            request[0] = 0x0E; request[1] = 0xA5; request[2] = command; request[3] = 0x01; request[4] = 0x01;   // READ, never WRITE (02)
            if (!d.SetFeature(request)) return false;
            Bat.Sleep(100);
            byte[] reply;
            if (!d.GetFeature(0x0E, out reply) || reply.Length < 6 || reply[0] != 0x0E || reply[1] != 0xA5 || reply[2] != command || reply[4] != 1) return false;
            value = reply[5];
            return true;
        }

        public static void Poll(PollContext ctx)
        {
            // End the sleeping-device grace period at once when the physical USB device disappears
            var gone = new List<string>();
            foreach (var kv in lastGood)
            {
                bool present = false;
                foreach (HidColl c in ctx.Colls) if (c.Path == kv.Key) { present = true; break; }
                if (present) continue;
                DeviceStatus g = kv.Value.Copy();
                g.Connected = false; g.Percent = -1; g.Charging = false;
                ctx.Out.Add(g);
                gone.Add(kv.Key);
            }
            foreach (string k in gone) lastGood.Remove(k);
            foreach (ushort[] vp in ctx.Products(Vid))
            {
                ushort pid = vp[1];
                if (pid != Receiver && pid != Cable) continue;
                foreach (HidColl c in ctx.Find(Vid, pid, x => x.Page == 0xFF05 && x.Usage == 1 && x.FeatLen == 64))
                {
                    DeviceStatus s;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        byte battery = 0;
                        bool valid = false;
                        for (int attempt = 0; attempt < 2 && !valid; attempt++) valid = Query(d, 0x0B, out battery) && battery <= 20;
                        if (!valid) continue;   // the UI keeps the last reading dimmed during the grace period
                        s = new DeviceStatus
                        {
                            Key = "vaxee:" + c.Path, Name = pid == Cable ? "ZYGEN NP-01S V2 Wireless" : "VAXEE wireless mouse", Kind = DeviceKind.Mouse,
                            Via = pid == Cable ? "USB cable" : "2.4 GHz receiver", Percent = battery * 5, GraceSec = 1800,
                        };
                        byte charging;
                        if (Query(d, 0x10, out charging)) s.Charging = charging != 0;
                    }
                    lastGood[c.Path] = s.Copy();
                    ctx.Add(s, Vid, pid);
                    break;
                }
            }
        }
    }
}
