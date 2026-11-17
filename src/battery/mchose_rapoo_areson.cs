// MCHOSE (L7 / M7 / A7 / K7 and G3 V2), Rapoo VT and Areson (Redragon M913 / M915) mice. Ported from Wireless
// Battery (mchose.cpp, mchose_g3.cpp, rapoo.cpp, rapoo_protocol.h, areson.cpp; GPL-3.0-or-later).
// MCHOSE: inverted-payload feature report 12 (alexfrih/mchose-linux); G3 V2 (Jstdi001/MCHOSE-Monitor).
// Rapoo: passive report 07 / legacy BB B0 on FF00:0002 (hub.rapoo.cn, PyFlat/Device-Battery-Info).
// Areson 25A7:FA07 / FA08: read-only GetPower (Qehbr/m913-ctl, redragon-m913-battery-tray).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class MchoseDriver
    {
        const ushort Vid = 0x5253;
        const byte Report = 0x12, CmdStatus = 0x06;

        // 1 = status reply, 0 = valid echo with an empty payload (mouse asleep), -1 otherwise
        static int ParseStatus(byte[] r, ref int level, ref int charging, ref int flags)
        {
            if (r == null || r.Length < 13 || r[0] != Report || r[1] != (CmdStatus ^ 0xFF)) return -1;
            var p = new byte[11];
            for (int i = 0; i < 11; i++) p[i] = (byte)(r[2 + i] ^ 0xFF);
            bool allZero = true;
            foreach (byte b in p) if (b != 0) allZero = false;
            if (allZero) return 0;
            if ((p[0] | (p[1] << 8)) != Vid) return -1;
            level = p[9];
            charging = p[10];
            flags = p[8];
            return level <= 100 ? 1 : -1;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(Vid))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFF01 && x.Usage == 0x0001 && x.FeatLen >= 65))
                {
                    int state = -1, level = 0, charging = 0, flags = 0;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        var req = new byte[c.FeatLen];
                        for (int i = 0; i < req.Length; i++) req[i] = 0xFF;   // inverted zero padding
                        req[0] = Report;
                        req[1] = CmdStatus ^ 0xFF;
                        for (int attempt = 0; attempt < 5 && state != 1; attempt++)
                        {
                            if (attempt > 0) Bat.Sleep(100);
                            if (!d.SetFeature(req)) break;   // the request must be sent again for every read
                            Bat.Sleep(120);
                            byte[] r;
                            if (!d.GetFeature(Report, out r)) continue;
                            int st = ParseStatus(r, ref level, ref charging, ref flags);
                            if (st > state) state = st;
                        }
                    }
                    if (state < 0) continue;   // not answering this protocol
                    var s = new DeviceStatus { Key = "mchose:" + Bat.Hex4(pid), Name = Hid.Product(c), Kind = DeviceKind.Mouse };
                    if (s.Name.Length == 0) s.Name = "MCHOSE mouse";
                    if (state == 1)
                    {
                        s.Percent = Bat.Clamp100(level);
                        s.Charging = charging != 0;
                        s.Via = flags == 0 && charging != 0 ? "USB cable" : "2.4 GHz receiver";
                    }
                    else { s.Asleep = true; s.Via = "2.4 GHz receiver"; }
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }

        // G3 V2 (VID 3837): output [00][55 30 A5 0B 2E 01 01 01 00 00], reply [AA][30] .. [8] %, [9] charging
        const ushort G3Vid = 0x3837, G3Wired = 0x418C, G3Wireless = 0x4245;

        static bool QueryG3V2(HidColl c, out int percent, out bool charging)
        {
            percent = -1; charging = false;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                d.Flush();
                if (!d.Write(Bat.B(0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01, 0x00, 0x00))) return false;
                byte[] r = null;
                long end = Bat.Now + 1000;
                while (end - Bat.Now > 0)
                {
                    int left = (int)(end - Bat.Now);
                    int n = d.Read(ref r, left);
                    if (n < 0) break;
                    if (n >= 10 && r[0] == 0xAA && r[1] == 0x30)
                    {
                        if (r[8] < 1 || r[8] > 100) return false;
                        percent = r[8];
                        charging = r[9] == 1;
                        return true;
                    }
                }
                return false;
            }
        }

        public static void PollG3(PollContext ctx)
        {
            bool found = false;   // prefer wired over 2.4 GHz when both are connected
            foreach (ushort pid in new[] { G3Wired, G3Wireless })
            {
                if (ctx.IsClaimed(G3Vid, pid)) continue;
                foreach (HidColl c in ctx.Find(G3Vid, pid, x => x.Page == 0xFF01 && x.Usage == 0x0010 && x.OutLen >= 65 && x.InLen >= 65))
                {
                    if (found) { ctx.Claim(G3Vid, pid); break; }
                    int pct;
                    bool chg;
                    if (!QueryG3V2(c, out pct, out chg)) continue;
                    var s = new DeviceStatus { Key = "mchose:g3v2", Name = Hid.Product(c), Kind = DeviceKind.Mouse };
                    if (s.Name.Length == 0) s.Name = "MCHOSE G3 V2";
                    s.Via = pid == G3Wired ? "USB cable" : "2.4 GHz receiver";
                    s.Percent = pct;
                    s.Charging = chg;
                    s.GraceSec = 1800;
                    ctx.Add(s, G3Vid, pid);
                    found = true;
                    break;
                }
            }
        }
    }

    static class RapooDriver
    {
        enum Connection { Unknown, Receiver, Bluetooth, Cable }
        class Battery { public int Percent = -1; public bool Charging; public Connection Connection; public ushort MousePid; }

        static bool DecodeBattery(byte[] data, Battery outB, bool legacy)
        {
            if (data == null || data.Length == 0) return false;
            var result = new Battery();
            int state, percent;
            if (!legacy && data[0] == 0x07)
            {
                // Nordic 54L: mode, DPI gear, X/Y DPI, battery state, percentage
                if (data.Length < 9 || (data[1] & 0x0F) > 2) return false;
                switch (data[1] & 0x0F)
                {
                    case 0: result.Connection = Connection.Receiver; break;
                    case 1: result.Connection = Connection.Bluetooth; break;
                    case 2: result.Connection = Connection.Cable; break;
                }
                state = data[7];
                percent = data[8];
                if (data.Length >= 15) result.MousePid = (ushort)(data[13] | (data[14] << 8));
            }
            else if (legacy && data[0] == 0xBB)
            {
                // VT3 Pro firmware: BB B0 mode DPI-lo DPI-hi battery-state percentage
                if (data.Length < 7 || data[1] != 0xB0) return false;
                state = data[5];
                percent = data[6];
            }
            else return false;
            // 0 = invalid / transition; some older firmware uses 7F instead of a level while charging
            if (state != 1 && state != 2) return false;
            if (percent > 100 && !(legacy && state == 2 && percent == 0x7F)) return false;
            result.Charging = state == 2;
            if (percent <= 100) result.Percent = percent;
            outB.Percent = result.Percent; outB.Charging = result.Charging; outB.Connection = result.Connection; outB.MousePid = result.MousePid;
            return true;
        }

        class Model { public ushort Cable, Receiver; public string Name; public Model(ushort c, ushort r, string n) { Cable = c; Receiver = r; Name = n; } }
        static readonly Model[] Models =
        {
            new Model(0x4610, 0x1410, "Rapoo VT0"), new Model(0x4620, 0x1420, "Rapoo VT0 Max"),
            new Model(0x4615, 0x1415, "Rapoo VT1"), new Model(0x4644, 0x1444, "Rapoo VT1 Max"),
            new Model(0x4622, 0x1422, "Rapoo VT2"), new Model(0x4623, 0x1423, "Rapoo VT2 Max"),
            new Model(0x4612, 0x1412, "Rapoo VT3"), new Model(0x4617, 0x1417, "Rapoo VT3 Max (Gen 2)"),
            new Model(0x4B06, 0x1B06, "Rapoo VT3 Max V2"), new Model(0x4611, 0x1411, "Rapoo VT3S"),
            new Model(0x4621, 0x1421, "Rapoo VT3S Max"), new Model(0x4613, 0x1413, "Rapoo VT7"),
            new Model(0x4616, 0x1416, "Rapoo VT7 Max"), new Model(0x4614, 0x1414, "Rapoo VT9"),
            new Model(0x4618, 0x1418, "Rapoo VT9 Max"), new Model(0x4415, 0x1215, "Rapoo VT3 Pro"),
            new Model(0x4433, 0x1233, "Rapoo VT1 Pro Max"),
        };

        static Model FindModel(ushort pid)
        {
            foreach (Model m in Models) if (pid == m.Cable || pid == m.Receiver) return m;
            return null;
        }

        static bool IsLegacyVT3Pro(ushort pid) { return pid == 0x1215 || pid == 0x4415 || pid == 0x1233 || pid == 0x4433; }

        // A passive legacy report can take ~3-4 s: wait 5 s. Not flushed: a queued status report is already useful
        static bool ReadBattery(HidDev d, Battery battery, bool legacy)
        {
            long end = Bat.Now + 5000;
            do
            {
                int left = (int)(end - Bat.Now);
                byte[] r = null;
                if (left <= 0 || d.Read(ref r, left) < 0) return false;
                if (DecodeBattery(r, battery, legacy)) return true;
            } while (Bat.Now < end);
            return false;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x24AE))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFF00 && x.Usage == 0x0002 && (x.InLen == 19 || (x.InLen == 7 && IsLegacyVT3Pro(pid)))))
                {
                    var battery = new Battery();
                    using (var d = new HidDev())
                        if (!d.Open(c) || !ReadBattery(d, battery, c.InLen == 7)) continue;
                    Model model = FindModel(battery.MousePid != 0 ? battery.MousePid : pid);
                    ushort identity = model != null ? model.Cable : (battery.MousePid != 0 ? battery.MousePid : pid);
                    var s = new DeviceStatus { Key = "rapoo:" + Bat.Hex4(identity), Name = model != null ? model.Name : Hid.Product(c) };
                    if (s.Name.Length == 0 || s.Name == "Rapoo Gaming Device") s.Name = "Rapoo wireless mouse";
                    s.Kind = DeviceKind.Mouse;
                    if (battery.Connection == Connection.Cable || (battery.Connection == Connection.Unknown && model != null && pid == model.Cable)) s.Via = "USB cable";
                    else if (battery.Connection == Connection.Bluetooth) s.Via = "Bluetooth";
                    else s.Via = "2.4 GHz receiver";
                    s.Percent = battery.Percent;
                    s.Charging = battery.Charging;
                    s.GraceSec = 1800;
                    // A charging mouse can report through both cable and receiver: keep one entry, the cable's state
                    int existing = ctx.Out.FindIndex(o => o.Key == s.Key);
                    if (existing < 0) ctx.Add(s, vid, pid);
                    else
                    {
                        if (s.Via == "USB cable")
                        {
                            int receiverPercent = ctx.Out[existing].Percent;
                            ctx.Out[existing] = s;
                            if (s.Percent < 0) s.Percent = receiverPercent;
                        }
                        else if (ctx.Out[existing].Percent < 0 && s.Percent >= 0) ctx.Out[existing].Percent = s.Percent;
                        ctx.Claim(vid, pid);
                    }
                    break;
                }
            }
        }
    }

    static class AresonDriver
    {
        // One GetPower exchange: SET_FEATURE [08][04][00..][checksum] on FF02:0002, reply input report on FF01:0000
        static bool QueryPower(HidDev cmd, HidDev reply, out int percent, out bool charging)
        {
            percent = -1; charging = false;
            var req = new byte[17];
            req[0] = 0x08; req[1] = 0x04;
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += req[i];
            req[16] = (byte)(0x55 - sum);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) Bat.Sleep(200);
                reply.Flush();   // a report queued before the request would be mistaken for the answer
                if (!cmd.SetFeature(req)) return false;   // receiver gone; retrying won't help
                byte[] r = null;
                if (reply.Read(ref r, 1800) < 17) continue;   // asleep or out of range
                int total = 0;
                foreach (byte b in r) total += b;
                if ((total & 0xFF) != 0x55 || r[0] != 0x09 || r[1] != 0x04 || r[2] != 0x00 || r[6] > 10) continue;
                percent = Bat.Clamp100(r[6] * 10);
                charging = r[7] != 0;
                return true;
            }
            return false;
        }

        static bool Probe(PollContext ctx, HidColl cmdColl, HidColl replyColl, ushort vid, ushort pid)
        {
            int pct;
            bool chg;
            using (var cmd = new HidDev())
            using (var reply = new HidDev())
            {
                if (!cmd.OpenFeatureOnly(cmdColl)) return false;
                if (!reply.Open(replyColl)) return false;
                if (!QueryPower(cmd, reply, out pct, out chg)) return false;
            }
            ctx.Add(new DeviceStatus
            {
                Key = "areson:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = "Redragon wireless mouse", Kind = DeviceKind.Mouse,
                Via = "2.4 GHz receiver", Percent = pct, Charging = chg, GraceSec = 1800,
            }, vid, pid);
            return true;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x25A7))
            {
                ushort vid = vp[0], pid = vp[1];
                if (pid != 0xFA07 && pid != 0xFA08) continue;   // FA7B / FA7C report the level on another scale
                bool done = false;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Iface == 1 && x.Page == 0xFF02 && x.Usage == 0x0002 && x.FeatLen >= 17))
                {
                    foreach (HidColl i in ctx.Find(vid, pid, x => x.Iface == 1 && x.Page == 0xFF01 && x.Usage == 0x0000 && x.InLen >= 17))
                    {
                        if (Probe(ctx, c, i, vid, pid)) done = true;
                        if (done) break;
                    }
                    if (done) break;
                }
            }
        }
    }
}
