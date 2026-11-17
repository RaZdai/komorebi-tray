// AULA / MCHOSE "BY" keyboard receivers, DAREU EM901X, Fantech Helios Go XD5, GameSir Nova / Cyclone 2, Flydigi
// Vader 5 Pro and MOONDROP GM01 Pro. Ported from Wireless Battery (aula.cpp, dareu.cpp, fantech.cpp, gamesir.cpp,
// flydigi.cpp, moondrop.cpp; GPL-3.0-or-later).
// AULA: PyFlat/Device-Battery-Info PR 6, womier-l65-linux, sanshoku #36; MCHOSE M HUB web driver. DAREU: AIM-WL
// driver GetPower. Fantech: jarjut/xd5-go-web. GameSir: GamesirBatteryTray, Gamesir-Nova-2-Lite-Battery-Detection,
// vdemonchy/cyclone2-linux. Flydigi: SDL hidapi_flydigi, HaloBattery #191. MOONDROP: devicePEQ (WalkPlay framing).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class AulaDriver
    {
        static readonly ushort[][] Receivers = { new ushort[] { 0x3554, 0xFA09 }, new ushort[] { 0x41E4, 0x2001 } };

        class Model { public byte[] Code; public string Name; public Model(byte[] c, string n) { Code = c; Name = n; } }
        static readonly Model[] Models =
        {
            new Model(Bat.B(0x03, 0, 0, 0, 0x02, 0x1C), "MCHOSE G87"), new Model(Bat.B(0x03, 0, 0, 0, 0x00, 0x77), "MCHOSE K99"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0xDF), "MCHOSE K99 V2"), new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0x32), "MCHOSE G98"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x00, 0xD6), "MCHOSE X75"), new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x03), "MCHOSE G98 Pro"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0xC6), "MCHOSE X75 V2"), new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0x15), "MCHOSE G75 Pro"),
            new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x18), "MCHOSE G98 V2"), new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x2B), "MCHOSE UT98"),
            new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x01), "MCHOSE Z75"), new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x09), "MCHOSE Z75S"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0xC7), "MCHOSE KX75"), new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0x74), "MCHOSE GX87"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0xED), "MCHOSE GX87 V2"), new Model(Bat.B(0x03, 0, 0, 0, 0x01, 0x35), "MCHOSE K87"),
            new Model(Bat.B(0x03, 0, 0, 0, 0x03, 0x35), "MCHOSE G75 V2"), new Model(Bat.B(0x0A, 0, 0, 0, 0x00, 0x34), "MCHOSE G87 V2"),
        };

        static byte Sum(byte[] b, int n) { int s = 0; for (int i = 0; i < n; i++) s += b[i]; return (byte)(s & 0xFF); }

        static bool SendRequest(HidDev d, byte cmd, byte packets)
        {
            var req = new byte[20];
            req[0] = 0x13; req[1] = cmd; req[2] = packets;
            req[19] = Sum(req, 19);
            return d.Write(req);
        }

        // 13 <cmd> ... and the matching reply, resending every 1.3 s within totalMs
        static bool Exchange(HidDev d, byte cmd, ref byte[] r, int totalMs)
        {
            long start = Bat.Now, end = start + totalMs;
            const int ResendMs = 1300;
            int sent = 0;
            d.Flush();
            for (;;)
            {
                long now = Bat.Now;
                if (now >= end) return false;
                if (now >= start + (long)sent * ResendMs)
                {
                    if (!SendRequest(d, cmd, (byte)(sent & 1))) return false;
                    ++sent;
                }
                long nextSend = start + (long)sent * ResendMs;
                int wait = (int)(Math.Min(nextSend, end) - now);
                if (wait < 1) wait = 1;
                long before = Bat.Now;
                int n = d.Read(ref r, wait);
                if (n < 0 && (int)(Bat.Now - before) + 20 < wait) return false;   // fails at once: receiver unplugged
                if (n < 20) continue;
                if (r[0] != 0x13) continue;
                if ((r[1] & 0x7F) != cmd) continue;   // the receiver also pushes unrelated 0x13 frames
                bool allZero = true;
                for (int i = 4; i < 19; i++) if (r[i] != 0) { allZero = false; break; }
                if (allZero) continue;   // echo of a request
                bool checksum = r[19] == Sum(r, 19);
                bool mchose = r[19] == 0 && r[2] == 1 && r[3] == 0;   // MCHOSE leaves the trailer at 0
                if (!checksum && !mchose) continue;
                return true;
            }
        }

        static string IdentifyModel(HidDev d)
        {
            byte[] r = null;
            if (!Exchange(d, 0x05, ref r, 1300)) return null;
            foreach (int off in new[] { 5, 4 })
                foreach (Model m in Models)
                {
                    bool eq = true;
                    for (int i = 0; i < 6; i++) if (r[off + i] != m.Code[i]) { eq = false; break; }
                    if (eq) return m.Name;
                }
            return null;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] rc in Receivers)
            {
                ushort vid = rc[0], pid = rc[1];
                if (ctx.IsClaimed(vid, pid)) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => (x.Page == 0xFF02 || x.Page == 0xFF04) && x.Usage == 0x0002 && x.OutLen >= 20 && x.InLen >= 20))
                {
                    byte[] r = null;
                    string model;
                    bool mchose;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        // Only the generic receiver names carry a model code (M HUB asks no other device)
                        string product = Hid.Product(c);
                        bool generic = product.Contains("2.4G Wireless Receiver") || product.Contains("MCHOSE 2.4G Wireless");
                        model = generic ? IdentifyModel(d) : null;
                        mchose = vid == 0x41E4 || model != null;
                        if (!Exchange(d, 0x4A, ref r, 4000)) continue;
                    }
                    int level = r[5];
                    if (level < 1 || level > 100) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "aula:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid),
                        Name = model ?? (mchose ? "MCHOSE wireless keyboard" : "AULA wireless keyboard"),
                        Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver", Percent = level,
                        Charging = (r[6] >> 4) == 1,   // high nibble: AULA's 0x10 cable flag, M HUB's chargeStatus
                    }, vid, pid);
                    break;
                }
            }
        }
    }

    static class DareuDriver
    {
        // Feature 08 on FF02:0002, input 09 on the sibling FF01:0000; only command 04
        public static void Poll(PollContext ctx)
        {
            const ushort vid = 0x260D, pid = 0x1074;
            if (ctx.IsClaimed(vid, pid)) return;
            var inputs = ctx.Find(vid, pid, c => c.Iface == 1 && c.Page == 0xFF01 && c.Usage == 0 && c.InLen == 17);
            var features = ctx.Find(vid, pid, c => c.Iface == 1 && c.Page == 0xFF02 && c.Usage == 2 && c.FeatLen == 17);
            if (inputs.Count != 1 || features.Count != 1) return;   // don't pair arbitrary collections of several receivers
            using (var input = new HidDev())
            using (var feature = new HidDev())
            {
                if (!input.Open(inputs[0]) || !feature.OpenFeatureOnly(features[0])) return;
                var req = new byte[17];
                req[0] = 0x08; req[1] = 0x04; req[16] = 0x49;   // 0x55 minus the sum of bytes 0..15
                input.Flush();
                if (!feature.SetFeature(req)) return;
                long end = Bat.Now + 1000;
                byte[] r = null;
                for (int reports = 0; reports < 32; reports++)
                {
                    int left = (int)(end - Bat.Now);
                    if (left <= 0 || input.Read(ref r, left) < 0) return;
                    if (r.Length != 17 || r[0] != 0x09 || r[1] != 0x04 || r[2] != 0 || r[6] > 100) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "dareu:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = "DAREU EM901X", Kind = DeviceKind.Mouse,
                        Via = "2.4 GHz receiver", Percent = r[6], Charging = r[7] != 0,
                    }, vid, pid);
                    return;
                }
            }
        }
    }

    static class FantechDriver
    {
        const ushort Vid = 0x25A7, PidWired = 0xFA7B, PidDongle = 0xFA7C;
        const byte ReportId = 0x08;
        const int PacketLen = 17;

        static byte[] MakePacket(byte cmd)
        {
            var p = new byte[PacketLen];
            p[0] = ReportId; p[1] = cmd;
            int sum = 0;
            for (int i = 0; i < PacketLen - 1; i++) sum += p[i];
            p[16] = (byte)(0x55 - sum);
            return p;
        }

        class BatteryReply { public int Pct = -1; public bool Charging, ChecksumOk; }

        // The command byte may sit at index 0 or 1 depending on whether the report id stayed in the buffer
        static bool ParseBattery(byte[] r, BatteryReply output)
        {
            int sum = 0;
            for (int i = 0; i < r.Length && i < PacketLen; i++) sum += r[i];
            bool chk = r.Length >= PacketLen && (sum & 0xFF) == 0x55;
            Func<int, int, bool> plausible = (power, charge) =>
            {
                if (r.Length <= charge) return false;
                int p = r[power], c = r[charge];
                if (p < 1 || p > 100 || c > 1) return false;
                output.Pct = p; output.Charging = c != 0; output.ChecksumOk = chk;
                return true;
            };
            if (r.Length >= 7 && r[0] == 0x04)
            {
                if (plausible(5, 6)) return true;
                if (plausible(4, 5)) return true;
            }
            if (r.Length >= 8 && r[1] == 0x04) return plausible(6, 7);
            return false;
        }

        static void Remember(BatteryReply c, BatteryReply found)
        {
            if (found.Pct < 0 || (c.ChecksumOk && !found.ChecksumOk)) { found.Pct = c.Pct; found.Charging = c.Charging; found.ChecksumOk = c.ChecksumOk; }
        }

        static bool QueryBattery(HidDev cmd, List<HidDev> readers, out int pct, out bool charging)
        {
            pct = -1; charging = false;
            cmd.Flush();
            if (!cmd.SetFeature(MakePacket(0x04))) return false;
            var found = new BatteryReply();
            byte[] r = null;
            long end = Bat.Now + 500;
            while (end - Bat.Now > 0)
            {
                foreach (HidDev d in readers)
                {
                    int left = (int)(end - Bat.Now);
                    if (left <= 0 || d.Read(ref r, Math.Min(left, 40)) < 6) continue;
                    var c = new BatteryReply();
                    if (ParseBattery(r, c)) Remember(c, found);
                }
                if (found.ChecksumOk) break;
            }
            byte[] f;   // some firmware answers with the feature report instead of an input report
            if (cmd.GetFeature(ReportId, out f))
            {
                var c = new BatteryReply();
                if (ParseBattery(f, c)) Remember(c, found);
            }
            if (found.Pct < 0) return false;
            pct = found.Pct;
            charging = found.Charging;
            return true;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(Vid))
            {
                ushort vid = vp[0], pid = vp[1];
                if (pid != PidWired && pid != PidDongle) continue;
                var all = ctx.Find(vid, pid, null);
                HidColl feat = null;
                foreach (HidColl c in all) if (c.FeatLen >= PacketLen) { feat = c; break; }
                if (feat == null) continue;
                var cmd = new HidDev();
                var opened = new List<HidDev>();
                try
                {
                    if (!cmd.Open(feat) && !cmd.OpenFeatureOnly(feat)) continue;
                    // The reply arrives on another collection than the one the feature report is written to
                    var readers = new List<HidDev>();
                    if (feat.InLen >= 6) readers.Add(cmd);
                    foreach (HidColl c in all)
                    {
                        if (c == feat || c.InLen < 6) continue;
                        var d = new HidDev();
                        if (d.Open(c)) { opened.Add(d); readers.Add(d); } else d.Dispose();
                    }
                    int pct;
                    bool charging;
                    if (!QueryBattery(cmd, readers, out pct, out charging)) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "fantech:" + Bat.Hex4(pid), Name = "Fantech Helios Go XD5", Kind = DeviceKind.Mouse,
                        Via = pid == PidWired ? "USB cable" : "2.4 GHz receiver", Percent = pct, Charging = charging, GraceSec = 1800,
                    }, vid, pid);
                    break;
                }
                finally
                {
                    cmd.Dispose();
                    foreach (HidDev d in opened) d.Dispose();
                }
            }
        }
    }

    static class GameSirDriver
    {
        const ushort Vid = 0x3537;
        static bool IsNova(ushort pid) { return pid == 0x1098 || pid == 0x100F || pid == 0x1040; }

        // Nova: vendor collection FF7A, request 01 01, reply [0]=01 .. [18] battery (FF = charging)
        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(Vid))
            {
                ushort vid = vp[0], pid = vp[1];
                if (!IsNova(pid)) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFF7A && x.OutLen >= 3))
                {
                    byte[] r = null;
                    bool found = false;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        d.Flush();
                        if (!d.Write(Bat.B(0x00, 0x01, 0x01))) continue;
                        long end = Bat.Now + 800;
                        while (!found && end - Bat.Now > 0)
                        {
                            if (d.Read(ref r, 200) < 19) continue;
                            if (r[0] != 0x01) continue;   // state frame (F4 00 ...) or anything else
                            found = true;
                        }
                    }
                    if (!found || (r[18] > 100 && r[18] != 0xFF)) continue;
                    var s = new DeviceStatus { Key = "gamesir:" + Bat.Hex4(pid), Name = "GameSir Nova", Kind = DeviceKind.Gamepad, Via = pid == 0x100F ? "USB cable" : "2.4 GHz receiver" };
                    if (r[18] == 0xFF) s.Charging = true;   // percent stays -1: the last known value is kept
                    else s.Percent = r[18];
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }

        // Cyclone 2 (XInput mode): FFF0:0040, request 0F 03, input report 12: [35] cable, [36] percent
        public static void PollCyclone(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(Vid))
            {
                ushort vid = vp[0], pid = vp[1];
                if (pid != 0x100B) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFFF0 && x.Usage == 0x0040 && x.OutLen >= 3 && x.InLen >= 37))
                {
                    byte[] r = null;
                    bool found = false;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        d.Flush();
                        if (!d.Write(Bat.B(0x0F, 0x03))) continue;
                        long end = Bat.Now + 1500;
                        while (!found && end - Bat.Now > 0)
                        {
                            if (d.Read(ref r, 200) < 37) continue;
                            found = r[0] == 0x12 && r[36] <= 100;   // the 0x10 event reports carry no battery
                        }
                    }
                    if (!found) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "gamesir:" + Bat.Hex4(pid), Name = "GameSir Cyclone 2", Kind = DeviceKind.Gamepad,
                        Via = "2.4 GHz receiver or USB", Percent = r[36], Charging = r[35] == 1,
                    }, vid, pid);
                    break;
                }
            }
        }
    }

    static class FlydigiDriver
    {
        const ushort Vid = 0x37D7, Pid = 0x2401;

        static bool Reply(HidDev d, byte command, ref byte[] reply)
        {
            long end = Bat.Now + 800;
            while (Bat.Now < end)
            {
                int remaining = (int)(end - Bat.Now);
                if (remaining <= 0 || d.Read(ref reply, remaining) < 0) return false;
                // HidDev strips a zero report id; some firmware retains a numbered input id
                if (reply.Length >= 4 && reply[0] == 0x04 && reply[1] == 0x5A && reply[2] == 0xA5)
                {
                    var t = new byte[reply.Length - 1];
                    Array.Copy(reply, 1, t, 0, t.Length);
                    reply = t;
                }
                if (reply.Length >= 5 && reply[0] == 0x5A && reply[1] == 0xA5 && reply[2] == command) return true;
            }
            return false;
        }

        public static void Poll(PollContext ctx)
        {
            if (ctx.IsClaimed(Vid, Pid)) return;
            foreach (HidColl c in ctx.Find(Vid, Pid, x => x.Page == 0xFFA0 && x.Usage == 1 && x.InLen >= 12 && x.OutLen >= 6))
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    d.Flush();
                    byte[] r = null;
                    if (!d.Write(Bat.B(0, 0x5A, 0xA5, 0x07, 0, 0))) continue;   // Vader 5 Pro: unnumbered output report
                    bool modern = Reply(d, 0x07, ref r) && r[3] == 1 && r[4] == 0;
                    if (!d.Write(Bat.B(0, 0x5A, 0xA5, 0x01, 2, 0)) || !Reply(d, 0x01, ref r)) continue;
                    if (r.Length < 12 || r[5] != 130) continue;   // confirmed Vader 5 Pro identity
                    int level = r[11] & 0x0F, state = r[11] >> 4;
                    if (state > 2 || level > 5) continue;
                    int connection = r[6];
                    if (modern ? connection > 1 : (connection != 1 && connection != 2)) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "flydigi:" + c.Path, Name = "Flydigi Vader 5 Pro", Kind = DeviceKind.Gamepad,
                        Via = connection == (modern ? 1 : 2) ? "2.4 GHz receiver" : "USB cable",
                        Percent = state == 2 ? 100 : level * 20, Charging = state == 1,
                    }, Vid, Pid);
                }
        }
    }

    static class MoondropDriver
    {
        const ushort Vid = 0x35D8, GM01Pro = 0x0133;
        const byte Report = 0x4B, Read = 0x80, CmdBattery = 0x27, CmdLink = 0x31;

        static bool Query(HidDev d, byte cmd, ref byte[] r)
        {
            d.Flush();
            if (!d.Write(Bat.B(Report, Read, cmd, 0x00))) return false;
            long end = Bat.Now + 400;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref r, left) < 5) return false;
                if (r[0] == Report && r[1] == Read && r[2] == cmd) return true;
            }
        }

        // Windows adds &colNN only for devices with several top-level collections: the dongle has four, the cable one
        static bool IsSingleCollection(HidColl c) { return !c.Path.ToLowerInvariant().Contains("&col"); }

        public static void Poll(PollContext ctx)
        {
            if (ctx.IsClaimed(Vid, GM01Pro)) return;
            var s = new DeviceStatus { Key = "moondrop:" + Bat.Hex4(GM01Pro), Name = "MOONDROP GM01 Pro", Kind = DeviceKind.Headset, GraceSec = 1800 };
            bool found = false, dongleUnlinked = false;
            foreach (HidColl c in ctx.Find(Vid, GM01Pro, x => x.Iface == 3 && x.Page == 0x000C))
            {
                found = true;
                if (c.OutLen < 4 || c.InLen < 10) continue;   // only the dongle's control channel
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    byte[] r = null;
                    if (Query(d, CmdBattery, ref r) && r.Length >= 10 && r[3] >= 6 && r[8] != 0 && r[9] <= 100)
                    {
                        s.Via = "2.4 GHz receiver";
                        s.Percent = r[9];
                        ctx.Add(s, Vid, GM01Pro);
                        return;
                    }
                    if (Query(d, CmdLink, ref r) && r[3] >= 1 && r[4] == 0) dongleUnlinked = true;
                }
            }
            if (!found) return;
            bool onCable = false;
            foreach (HidColl c in ctx.Find(Vid, GM01Pro, x => x.Iface == 3)) onCable |= IsSingleCollection(c);
            if (onCable) { s.Via = "USB cable"; s.Charging = true; ctx.Add(s, Vid, GM01Pro); }   // level unknown on the cable
            else if (dongleUnlinked) { s.Via = "2.4 GHz receiver"; s.Connected = false; ctx.Add(s, Vid, GM01Pro); }
            else ctx.Claim(Vid, GM01Pro);   // no answer: keep the last value (graceSec)
        }
    }
}
