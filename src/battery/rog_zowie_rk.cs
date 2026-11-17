// ASUS ROG (keyboards, mice, Omni Receiver, Pelta headset), BenQ ZOWIE and Royal Kludge MG6 Pro. Ported from
// Wireless Battery (rog.cpp, zowie.cpp, royal_kludge.cpp; GPL-3.0-or-later).
// ROG: [report id][12][sub], sub 01 keyboard / 07 mouse (SteveWang92/peripheral-battery-tray, G-Helper, 1gcat/BatteryTool);
// Pelta: report CC through HidD_SetOutputReport (jimm0thy/pelta-control, B1Chat/BatteryWatch).
// ZOWIE EC-CW [A5 03] (yleshchuk/zowie-battery), DW / U2 [08 B4 15] -> report 09 (IgnacioBarb/zowie-battery-tray).
// RK MG6 Pro: QiWang frame, CFG_SYS_CTRL_GET_BATTER_VOL (drive.rkgaming.com).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class RogDriver
    {
        class Model
        {
            public ushort Pid; public string Name; public bool Keyboard; public byte ReportId; public int Iface; public string Col; public int Offset, Scale;
            public Model(ushort pid, string name, bool kb, byte rid, int iface, string col, int offset, int scale)
            { Pid = pid; Name = name; Keyboard = kb; ReportId = rid; Iface = iface; Col = col; Offset = offset; Scale = scale; }
        }
        const bool KB = true, MS = false;

        static readonly Model[] Models =
        {
            // Keyboards (Omni = col02 report 02, receiver / wired = interface 1 unnumbered)
            new Model(0x1A85, "ROG Azoth", KB, 2, 2, "col02", 6, 1),
            new Model(0x1A85, "ROG Azoth", KB, 0, 1, null, 6, 1),
            new Model(0x1A83, "ROG Azoth", KB, 0, 1, null, 6, 1),
            new Model(0x1B42, "ROG Azoth Extreme", KB, 2, 2, "col02", 6, 1),
            new Model(0x1B3F, "ROG Azoth Extreme", KB, 0, 1, null, 6, 1),
            new Model(0x1CF1, "ROG Azoth Extreme SE", KB, 2, 2, "col02", 6, 1),
            new Model(0x1CEF, "ROG Azoth Extreme SE", KB, 0, 1, null, 6, 1),
            new Model(0x1C25, "ROG Azoth X", KB, 0, 1, null, 6, 1),
            new Model(0x1C24, "ROG Azoth X", KB, 0, 1, null, 6, 1),
            new Model(0x193E, "ROG Falchion", KB, 0, 1, null, 11, 1),
            new Model(0x193C, "ROG Falchion", KB, 0, 1, null, 11, 1),
            new Model(0x1B04, "ROG Falchion RX", KB, 0, -1, null, 11, 1),
            new Model(0x1B06, "ROG Falchion RX Low Profile", KB, 2, 2, "col02", 11, 1),
            new Model(0x1AB0, "ROG Strix Scope II 96", KB, 2, 2, "col02", 6, 1),
            new Model(0x1AAE, "ROG Strix Scope II 96", KB, 0, 1, null, 6, 1),
            new Model(0x1B7A, "ROG Strix Scope II 96 RX", KB, 2, 2, "col02", 6, 1),
            new Model(0x1B78, "ROG Strix Scope II 96 RX", KB, 0, 1, null, 6, 1),
            new Model(0x1A07, "ROG Strix Scope RX TKL", KB, 0, -1, null, 6, 1),
            // Mice (Omni = col03 report 03, receiver / wired = interface 0 unnumbered)
            new Model(0x18E5, "ROG Chakram", MS, 0, 0, null, 5, 25),
            new Model(0x18E3, "ROG Chakram", MS, 0, 0, null, 5, 25),
            new Model(0x1A1A, "ROG Chakram X", MS, 0, 0, null, 5, 1),
            new Model(0x1A18, "ROG Chakram X", MS, 0, 0, null, 5, 1),
            new Model(0x18A0, "ROG Gladius II Wireless", MS, 0, 2, null, 5, 25),
            new Model(0x197F, "ROG Gladius III Wireless", MS, 0, 0, null, 5, 1),
            new Model(0x197D, "ROG Gladius III Wireless", MS, 0, 0, null, 5, 1),
            new Model(0x1A72, "ROG Gladius III Aimpoint", MS, 3, 2, "col03", 5, 1),
            new Model(0x1A72, "ROG Gladius III Aimpoint", MS, 0, 0, null, 5, 1),
            new Model(0x1A70, "ROG Gladius III Aimpoint", MS, 0, 0, null, 5, 1),
            new Model(0x1B0C, "ROG Gladius III Aimpoint EVA-02", MS, 0, 0, null, 5, 1),
            new Model(0x1B0A, "ROG Gladius III Aimpoint EVA-02", MS, 0, 0, null, 5, 1),
            new Model(0x1A94, "ROG Harpe Ace Aim Lab Edition", MS, 3, 2, "col03", 5, 1),
            new Model(0x1A94, "ROG Harpe Ace Aim Lab Edition", MS, 0, 0, null, 5, 1),
            new Model(0x1A92, "ROG Harpe Ace Aim Lab Edition", MS, 0, 0, null, 5, 1),
            new Model(0x1B68, "ROG Harpe Ace Extreme", MS, 3, 2, "col03", 5, 1),
            new Model(0x1B69, "ROG Harpe Ace Extreme", MS, 3, 2, "col03", 5, 1),
            new Model(0x1B67, "ROG Harpe Ace Extreme", MS, 0, 0, null, 5, 1),
            new Model(0x1B65, "ROG Harpe Ace Mini", MS, 3, 2, "col03", 5, 1),
            new Model(0x1B63, "ROG Harpe Ace Mini", MS, 0, 0, null, 5, 1),
            new Model(0x1AD0, "ROG Harpe II Ace", MS, 3, 2, "col03", 5, 1),
            new Model(0x1C69, "ROG Harpe II Ace", MS, 0, 0, null, 5, 1),
            new Model(0x1960, "ROG Keris Wireless", MS, 0, 0, null, 5, 25),
            new Model(0x195E, "ROG Keris Wireless", MS, 0, 0, null, 5, 25),
            new Model(0x1A59, "ROG Keris Wireless EVA Edition", MS, 0, 0, null, 5, 25),
            new Model(0x1A57, "ROG Keris Wireless EVA Edition", MS, 0, 0, null, 5, 25),
            new Model(0x1A68, "ROG Keris Wireless Aimpoint", MS, 3, 2, "col03", 5, 1),
            new Model(0x1A6A, "ROG Keris Wireless Aimpoint", MS, 3, 2, "col03", 5, 1),
            new Model(0x1A68, "ROG Keris Wireless Aimpoint", MS, 0, 0, null, 5, 1),
            new Model(0x1A66, "ROG Keris Wireless Aimpoint", MS, 0, 0, null, 5, 1),
            new Model(0x1B1A, "ROG Keris II Ace", MS, 3, 2, "col03", 5, 1),
            new Model(0x1B18, "ROG Keris II Ace", MS, 3, 2, "col03", 5, 1),
            new Model(0x1B16, "ROG Keris II Ace", MS, 0, 0, null, 5, 1),
            new Model(0x1C0E, "ROG Keris II Origin", MS, 3, 2, "col03", 5, 1),
            new Model(0x1C0C, "ROG Keris II Origin", MS, 0, 0, null, 5, 1),
            new Model(0x1D4E, "ROG Keris II Origin KJP", MS, 3, 2, "col03", 5, 1),
            new Model(0x1D4C, "ROG Keris II Origin KJP", MS, 0, 0, null, 5, 1),
            new Model(0x1908, "ROG Pugio II", MS, 0, 0, null, 5, 25),
            new Model(0x1906, "ROG Pugio II", MS, 0, 0, null, 5, 25),
            new Model(0x1979, "ROG Spatha X", MS, 0, 0, null, 5, 1),
            new Model(0x1977, "ROG Spatha X", MS, 0, 0, null, 5, 1),
            new Model(0x18B4, "ROG Strix Carry", MS, 0, 1, null, 7, 25),
            new Model(0x1949, "ROG Strix Impact II Wireless", MS, 0, 0, null, 5, 25),
            new Model(0x1947, "ROG Strix Impact II Wireless", MS, 0, 0, null, 5, 25),
            new Model(0x1AD7, "ROG Strix Impact III Wireless", MS, 3, 2, "col03", 5, 1),
        };

        static bool PathHas(string path, string tag) { return path.ToLowerInvariant().Contains(tag); }

        enum Reply { Ok, Asleep, None }

        static Reply Query(HidDev d, byte rid, byte cmd, byte sub, out byte[] reply)
        {
            reply = null;
            d.Flush();
            if (!d.Write(Bat.B(rid, cmd, sub))) return Reply.None;
            long end = Bat.Now + 500;
            byte[] r = null;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref r, left) < 11) return Reply.None;
                if (rid == 0) { var t = new byte[r.Length + 1]; Array.Copy(r, 0, t, 1, r.Length); r = t; }   // restore the report id slot so offsets match
                if (r.Length < 12) continue;
                if (r[1] == 0xFF && r[2] == 0xAA) return Reply.Asleep;
                if (r[1] == cmd && r[2] == sub) { reply = r; return Reply.Ok; }
            }
            return Reply.None;
        }

        static bool ReadModel(HidDev d, Model m, out DeviceStatus s)
        {
            s = null;
            byte[] r;
            Reply res = Query(d, m.ReportId, 0x12, (byte)(m.Keyboard ? 0x01 : 0x07), out r);
            if (res == Reply.None) return false;
            s = new DeviceStatus { Key = "rog:" + Bat.Hex4(m.Pid) + (m.Keyboard ? ":kb" : ":ms"), Name = m.Name };
            s.Kind = m.Keyboard ? DeviceKind.Keyboard : DeviceKind.Mouse;
            s.Via = m.ReportId != 0 ? "ROG Omni Receiver" : "2.4 GHz receiver / USB";
            if (res == Reply.Asleep) s.Asleep = true;
            else if (m.Keyboard)
            {
                if (r[m.Offset] <= 100) s.Percent = r[m.Offset];
                s.Charging = r[9] == 1;
            }
            else
            {
                int v = r[m.Offset] * m.Scale;
                if (v > 0 && v <= 100) s.Percent = v;
                else s.Asleep = true;   // 0 means unavailable, not empty
                s.Charging = r[10] != 0;
            }
            return true;
        }

        static void AddOnce(PollContext ctx, List<string> seen, DeviceStatus s)
        {
            if (seen.Contains(s.Name)) return;
            seen.Add(s.Name);
            ctx.Out.Add(s);
        }

        // ROG Omni Receiver (0B05:1ACE): [01 A0 00] on col01 lists the paired product ids in 4-byte slots from byte 5
        static void PollOmni(PollContext ctx, List<string> seen)
        {
            if (ctx.IsClaimed(0x0B05, 0x1ACE)) return;
            var list = ctx.Find(0x0B05, 0x1ACE, c => c.Iface == 2 && c.OutLen >= 12 && PathHas(c.Path, "col01"));
            if (list.Count == 0) return;
            byte[] r;
            using (var d = new HidDev())
                if (!d.Open(list[0]) || Query(d, 0x01, 0xA0, 0x00, out r) != Reply.Ok) return;
            ctx.Claim(0x0B05, 0x1ACE);
            for (int at = 5; at + 1 < r.Length; at += 4)
            {
                ushort pid = (ushort)(r[at] | (r[at + 1] << 8));
                if (pid == 0) break;
                foreach (Model m in Models)
                {
                    if (m.Pid != pid || m.ReportId == 0) continue;   // only the Omni-channel entry of that model
                    foreach (HidColl c in ctx.Find(0x0B05, 0x1ACE, x => x.Iface == 2 && x.OutLen >= 12 && PathHas(x.Path, m.Col)))
                    {
                        DeviceStatus s = null;
                        bool ok;
                        using (var dev = new HidDev()) ok = dev.Open(c) && ReadModel(dev, m, out s);
                        if (ok) { AddOnce(ctx, seen, s); break; }
                    }
                    break;
                }
            }
        }

        // ROG Pelta headset (dongle 0B05:1B84): report CC through HidD_SetOutputReport (no interrupt OUT pipe)
        const ushort PeltaPid = 0x1B84;

        static Reply PeltaQuery(HidDev d, byte cmd, out byte[] reply)
        {
            reply = null;
            d.Flush();
            long end = Bat.Now + 1200;   // the first answer after the headset reconnects can take ~750 ms
            byte[] r = null;
            int n = d.SetOutputAndRead(Bat.B(0xCC, 0x12, cmd), ref r, 1200);
            while (n >= 8)
            {
                if (r[0] == 0xCC && r[1] == 0xFF && r[2] == 0xAA) return Reply.Asleep;
                if (r[0] == 0xCC && r[1] == 0x12)
                {
                    if (r[5] == 0xFF && r[6] == 0xAA) return Reply.Asleep;
                    if (r[2] == cmd) { reply = r; return Reply.Ok; }
                }
                int left = (int)(end - Bat.Now);
                if (left <= 0) break;
                n = d.Read(ref r, left);
            }
            return Reply.None;
        }

        static void PollPelta(PollContext ctx)
        {
            if (ctx.IsClaimed(0x0B05, PeltaPid)) return;
            foreach (HidColl c in ctx.Find(0x0B05, PeltaPid, x => x.Iface == 3 && x.Page == 0xFF00 && x.Usage == 0x0001 && x.InLen >= 8 && x.OutLen >= 8))
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    byte[] r;
                    Reply res = PeltaQuery(d, 0x07, out r);
                    if (res == Reply.None) continue;
                    var s = new DeviceStatus { Key = "rog:" + Bat.Hex4(PeltaPid) + ":hs", Name = "ROG Pelta", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
                    if (res == Reply.Asleep) s.Asleep = true;
                    else
                    {
                        if (r[6] <= 100) s.Percent = r[6];
                        else s.Asleep = true;   // FE = level not known yet
                        byte[] ch;
                        if (PeltaQuery(d, 0x08, out ch) == Reply.Ok) s.Charging = ch[5] == 1;
                    }
                    ctx.Out.Add(s);
                    ctx.Claim(0x0B05, PeltaPid);
                    return;
                }
        }

        static readonly HashSet<ushort> awake = new HashSet<ushort>();

        public static void Poll(PollContext ctx)
        {
            var seen = new List<string>();   // one entry per name (wired + receiver both present while charging)
            PollPelta(ctx);
            PollOmni(ctx, seen);
            foreach (Model m in Models)
            {
                if (ctx.IsClaimed(0x0B05, m.Pid)) continue;
                var colls = ctx.Find(0x0B05, m.Pid, c =>
                {
                    if (c.OutLen < 12 || c.Page < 0xFF00) return false;
                    if (m.Iface >= 0 && c.Iface != m.Iface) return false;
                    return m.Col == null || PathHas(c.Path, m.Col);
                });
                foreach (HidColl c in colls)
                {
                    DeviceStatus s = null;
                    bool ok;
                    using (var d = new HidDev()) ok = d.Open(c) && ReadModel(d, m, out s);
                    if (!ok) continue;
                    // A receiver channel answers "asleep" when nothing is paired too: show it only once it really answered
                    if (m.ReportId != 0 && s.Asleep && !awake.Contains(m.Pid)) continue;
                    if (m.ReportId != 0 && !s.Asleep) awake.Add(m.Pid);
                    AddOnce(ctx, seen, s);
                    ctx.Claim(0x0B05, m.Pid);
                    break;
                }
            }
        }
    }

    static class ZowieDriver
    {
        // Voltage -> % anchored on the mouse's own LED thresholds (10 / 25 / 50 %)
        static int ZowiePercent(int mv) { return Bat.Interpolate(mv, new[] { 4200, 4000, 3890, 3760, 3680, 3500 }, new[] { 100, 75, 50, 25, 10, 0 }); }

        // Charging guess from the voltage trend (the dock charges over contacts; only the voltage arrives)
        class ChargeTrend
        {
            bool has, charging;
            int peak, trough;
            public bool Update(int mv)
            {
                if (!has) { has = true; charging = mv >= 4250; peak = trough = mv; }   // a resting cell never sits above ~4.2 V
                else if (charging) { peak = Math.Max(peak, mv); if (mv <= peak - 20) { charging = false; trough = mv; } }
                else { trough = Math.Min(trough, mv); if (mv >= trough + 50) { charging = true; peak = mv; } }
                return charging;
            }
        }
        static readonly Dictionary<ushort, ChargeTrend> trend = new Dictionary<ushort, ChargeTrend>();

        static bool PollCw(PollContext ctx, ushort pid)
        {
            foreach (HidColl c in ctx.Find(0x04A5, pid, x => (x.Page >= 0xFF00 || x.Page == 0x00FF) && x.OutLen == 65))
            {
                byte[] r = null;
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    d.Flush();
                    if (!d.Write(Bat.B(0x00, 0xA5, 0x03))) continue;
                    if (d.Read(ref r, 1000) < 3) continue;
                }
                int mv = r[0] | (r[1] << 8);
                if (mv != 0 && (mv < 3000 || mv > 4500)) continue;   // not a battery reply
                var s = new DeviceStatus { Key = "zowie:" + Bat.Hex4(pid), Name = "ZOWIE EC-CW", Kind = DeviceKind.Mouse };
                s.Via = pid == 0x8005 ? "USB cable" : "2.4 GHz receiver";
                s.Charging = pid == 0x8005;
                if (mv == 0) s.Connected = false;
                else
                {
                    s.Millivolts = mv;
                    s.Percent = r[2] >= 1 && r[2] <= 100 ? r[2] : ZowiePercent(mv);
                }
                ctx.Add(s, 0x04A5, pid);
                return true;
            }
            return false;
        }

        static bool PollDw(PollContext ctx, ushort pid)
        {
            var cmd = ctx.Find(0x04A5, pid, c => c.Page == 0xFF03 && c.OutLen == 16);
            var rep = ctx.Find(0x04A5, pid, c => c.Page == 0xFF04 && c.InLen == 16);
            if (cmd.Count == 0 || rep.Count == 0) return false;
            byte[] r = null;
            bool ok = false;
            using (var inp = new HidDev())
            using (var output = new HidDev())
            {
                if (!inp.Open(rep[0]) || !output.Open(cmd[0])) return false;   // open the reply channel first
                inp.Flush();
                if (!output.Write(Bat.B(0x08, 0xB4, 0x15))) return false;
                long end = Bat.Now + 1500;
                while (end - Bat.Now > 0)
                {
                    int left = (int)(end - Bat.Now);
                    if (inp.Read(ref r, left) < 5) break;
                    if (r[0] == 0x09) { ok = true; break; }
                }
            }
            if (!ok || r[1] == 0xE6 || r[4] != 0xC8) return false;
            int mv = (r[1] * 256 + r[2]) * 10;
            bool cable = pid != 0x800A && pid != 0x8008;   // 800A: DW receiver, 8008: U2 receiver
            var s = new DeviceStatus { Key = "zowie:" + Bat.Hex4(pid), Name = Hid.Product(cmd[0]) };
            if (pid == 0x8008) s.Name = "ZOWIE U2";   // its receiver only reports "BenQ ZOWIE Gaming Mouse"
            else if (s.Name.Length == 0 || s.Name.Contains("Receiver")) s.Name = "ZOWIE DW mouse";
            s.Kind = DeviceKind.Mouse;
            s.Via = cable ? "USB cable" : "2.4 GHz receiver";
            if (mv == 0) s.Connected = false;
            else
            {
                s.Millivolts = mv;
                s.Percent = ZowiePercent(mv);
                ChargeTrend t;
                if (!trend.TryGetValue(pid, out t)) trend[pid] = t = new ChargeTrend();
                s.Charging = cable || t.Update(mv);
            }
            ctx.Add(s, 0x04A5, pid);
            return true;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x04A5))
            {
                ushort pid = vp[1];
                if (pid == 0x8005 || pid == 0x8006) PollCw(ctx, pid);
                else PollDw(ctx, pid);   // only acts on devices with the DW command / reply collections
            }
        }
    }

    static class RoyalKludgeDriver
    {
        static bool IsCable(ushort pid) { return pid == 0x00A5 || pid == 0x00E5; }

        static bool PollMg6(PollContext ctx, ushort vid, ushort pid)
        {
            ushort page = IsCable(pid) ? (ushort)0xFFF2 : (ushort)0xFF00;
            foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == page && x.Usage == 1 && x.OutLen == 65 && x.InLen == 65))
            {
                bool answered = false, online = false, plugged = false;
                int percent = -1, mv = 0;
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    for (int attempt = 0; attempt < 3 && !online; attempt++)
                    {
                        if (attempt > 0) Bat.Sleep(100);
                        d.Flush();
                        if (!d.Write(Bat.B(0x00, 0x01, 0x01, 0x02, 0x01))) break;
                        long end = Bat.Now + 600;   // other notifications on this collection are skipped
                        byte[] r = null;
                        while (end - Bat.Now > 0)
                        {
                            int left = (int)(end - Bat.Now);
                            if (d.Read(ref r, left) < 11) break;
                            if ((r[0] & 0x1F) != 0x01 || (r[1] & 0x0F) != 0x01 || r[2] != 0x02) continue;
                            answered = true;
                            if (((r[3] >> 4) & 1) == 0) break;   // access_res 0: the mouse didn't answer
                            int p = r[6] | (r[7] << 8);
                            if (p > 100) break;
                            online = true;
                            percent = p;
                            mv = r[4] | (r[5] << 8);
                            plugged = r[10] != 0;
                            break;
                        }
                    }
                }
                if (!answered) continue;
                var s = new DeviceStatus { Key = "rk:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = "RK MG6 Pro", Kind = DeviceKind.Mouse, Via = IsCable(pid) ? "USB cable" : "2.4 GHz receiver" };
                if (online)
                {
                    s.Percent = percent;
                    if (mv >= 3000 && mv <= 4500) s.Millivolts = mv;
                    s.Charging = plugged || IsCable(pid);
                }
                else s.Asleep = true;   // the receiver answered, the mouse didn't: keep the last value
                ctx.Add(s, vid, pid);
                return true;
            }
            return false;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x2E3C))
            {
                ushort pid = vp[1];
                if (pid == 0x00A5 || pid == 0x00A6 || pid == 0x00E5 || pid == 0x00E6) PollMg6(ctx, vp[0], pid);
            }
        }
    }
}
