// HyperX, Cooler Master and Glorious-protocol mice, ported from Wireless Battery (hyperx.cpp, coolermaster.cpp,
// glorious.cpp; GPL-3.0-or-later).
// HyperX: Cloud III Wireless (Medprj/HyperXWM), Cloud III S (LennardKittner/HyperHeadset), Cloud Stinger Core
// (davidsantanaalves/HyperBatteryMonitor), the others HeadsetControl.
// Cooler Master MM712 / MM731: syntactic-spice/coolermaster-usb-protocol, riVFerd/MM731-Controller.
// Glorious / Hitscan / G-Wolves / WLMouse YING: 64-byte feature report, [3]=02 [4]=02 [6]=83; reply [1] status
// (A1 awake, A4 asleep, A0 waking), [7] charging, [8] percent (glorious-indicator, glorious-ctl, mouse.xyz).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class HyperXDriver
    {
        static bool Query(HidDev d, byte[] req, ref byte[] resp, int timeoutMs, Func<byte[], bool> match)
        {
            if (!d.Write(req)) return false;
            long end = Bat.Now + timeoutMs;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref resp, left) < 0) return false;
                if (match(resp)) return true;
            }
        }

        static DeviceStatus Base(ushort pid, string name)
        {
            return new DeviceStatus { Key = "hyperx:" + Bat.Hex4(pid), Name = name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
        }

        // Cloud III Wireless: report 66 <cmd>; 82 -> [2] connected, 8A -> [2] charging, 89 -> [2..3] mV, [4] %
        static bool Cloud3(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, bool> q = cmd => Query(d, Bat.B(0x66, cmd), ref r, 800, x => x.Length >= 5 && x[0] == 0x66 && x[1] == cmd);
                if (!q(0x82)) return false;
                s = Base(c.Pid, c.Pid == 0x069F || c.Pid == 0x0995 ? "HyperX Cloud II Core Wireless" : "HyperX Cloud III Wireless");
                s.Connected = r[2] != 0;
                if (s.Connected)
                {
                    if (q(0x8A)) s.Charging = r[2] == 1;
                    if (q(0x89) && r[4] <= 100) { s.Percent = r[4]; s.Millivolts = (r[2] << 8) | r[3]; }
                }
                return true;
            }
        }

        // Cloud II Wireless (HP): report 06 FF BB <cmd>; 02 -> [7] %, 03 -> [4] charging
        static bool Cloud2(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, bool> q = cmd => Query(d, Bat.B(0x06, 0xFF, 0xBB, cmd), ref r, 1000, x => x.Length >= 8 && x[0] == 0x06 && x[1] == 0xFF && x[2] == 0xBB && x[3] == cmd);
                if (!q(0x02) || r[7] > 100) return false;
                s = Base(c.Pid, "HyperX Cloud II Wireless");
                s.Percent = r[7];
                s.Millivolts = (r[5] << 8) | r[6];
                if (q(0x03)) s.Charging = r[4] == 1;
                return true;
            }
        }

        // Cloud II Wireless (Kingston 0951:1718): fixed frame, reply 0B 00 BB <cmd>
        static bool Cloud2Kingston(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, bool> q = cmd => Query(d, Bat.B(0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, cmd, 0x00),
                    ref r, 1000, x => x.Length >= 8 && x[0] == 0x0B && x[2] == 0xBB && x[3] == cmd);
                if (!q(0x02) || r[7] > 100) return false;
                s = Base(c.Pid, "HyperX Cloud II Wireless");
                s.Percent = r[7];
                if (q(0x03)) s.Charging = r[4] == 1;
                return true;
            }
        }

        // Cloud Alpha Wireless: 21 BB <cmd>; 03 -> [3]==1 offline, 0C -> [3]==1 charging, 0B -> [3] %
        static bool CloudAlpha(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, bool> q = cmd => Query(d, Bat.B(0x21, 0xBB, cmd), ref r, 1000, x => x.Length >= 4 && x[0] == 0x21 && x[2] == cmd);
                if (!q(0x03)) return false;
                s = Base(c.Pid, "HyperX Cloud Alpha Wireless");
                s.Connected = r[3] != 0x01;
                if (!s.Connected) return true;
                if (q(0x0C)) s.Charging = r[3] == 0x01;
                if (q(0x0B) && r[3] <= 100) s.Percent = r[3];
                return true;
            }
        }

        // Cloud Alpha 2 Wireless: [cmd][sub]; reply [cmd+1][sub]...; 52 01 -> [2] connected, 50 02 -> [2] %, [3] charging
        static bool CloudAlpha2(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, byte, bool> q = (cmd, sub) => Query(d, Bat.B(cmd, sub), ref r, 1000, x => x.Length >= 8 && x[0] == cmd + 1 && x[1] == sub);
                if (!q(0x52, 0x01)) return false;
                s = Base(c.Pid, "HyperX Cloud Alpha 2 Wireless");
                s.Connected = r[2] != 0;
                if (s.Connected && q(0x50, 0x02) && r[2] <= 100)
                {
                    s.Percent = r[2];
                    s.Charging = r[3] == 0x01;
                    s.Millivolts = r[6] | (r[7] << 8);
                }
                return true;
            }
        }

        // Cloud Flight: 21 FF 05 -> [3..4] mV (above 0x100B means charging)
        static bool CloudFlight(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                if (!Query(d, Bat.B(0x21, 0xFF, 0x05), ref r, 1000, x => x.Length >= 5)) return false;
                int mv = (r[3] << 8) | r[4];
                s = Base(c.Pid, "HyperX Cloud Flight");
                s.Millivolts = mv;
                if (mv > 0x100B) s.Charging = true;
                else if (mv <= 3648) s.Percent = Bat.Clamp100((int)(0.00125 * mv));
                else if (mv > 3975) s.Percent = 100;
                else
                {
                    double v = mv;
                    s.Percent = Bat.Clamp100((int)Math.Round(0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3) +
                        2.238321 * v * v - 5706.256 * v + 5452299.0, MidpointRounding.AwayFromZero));
                }
                return true;
            }
        }

        // Cloud III S Wireless: report 0C [02 03 01 00 <cmd>]; reply [5] = cmd, [6] = value (FF = invalid)
        static bool Cloud3S(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                byte[] r = null;
                Func<byte, bool> q = cmd => Query(d, Bat.B(0x0C, 0x02, 0x03, 0x01, 0x00, cmd), ref r, 1000, x => x.Length >= 7 && x[0] == 0x0C && x[5] == cmd && x[6] != 0xFF);
                if (!q(0x02)) return false;
                s = Base(c.Pid, "HyperX Cloud III S Wireless");
                s.Connected = r[6] == 2;
                if (!s.Connected) return true;
                if (q(0x06) && r[6] <= 100) s.Percent = r[6];
                if (q(0x48)) s.Charging = r[6] == 1 || r[6] == 2;
                return true;
            }
        }

        // Cloud Stinger Core Wireless (+ 7.1): feature report FF on FF00:0001. SetFeature(command), wait 20 ms,
        // GetFeature(FF): connection reply [11] == 1 on; battery reply [9] = 03 / 05 charging / 06 full, [11..12] mV
        static readonly byte[] StingerConnection = Bat.B(0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00);
        static readonly byte[] StingerBattery = Bat.B(0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C);
        static readonly int[] StingerPct = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };
        static readonly int[] StingerChargingMv = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
        static readonly int[] StingerDischargingMv = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };

        static bool StingerCore(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                bool ps = c.Pid == 0x0A93;
                byte[] r = null;
                Func<byte[], Func<byte[], bool>, bool> exchange = (cmd, good) =>
                {
                    if (!d.SetFeature(cmd)) return false;
                    for (int i = 0; i < (ps ? 10 : 1); i++)
                    {
                        Bat.Sleep(20);
                        if (!d.GetFeature(0xFF, out r) || r.Length < 13) return false;
                        if (!ps || good(r)) return true;
                    }
                    return false;
                };
                Func<byte[], bool> fresh = x => !(x[1] == 0x01 && x[2] == 0x00);
                Func<byte[], bool> battery = x => x[0] == 0xFF && x[1] == 0x12 && x[2] == 0x05;
                // The connection reply's header isn't documented: after 10 stale-looking reads the last one is used
                if (!exchange(StingerConnection, fresh) && !(ps && r != null && r.Length >= 13 && !fresh(r))) return false;
                s = Base(c.Pid, "HyperX Cloud Stinger Core Wireless");
                s.Connected = r[11] == 0x01;
                if (!s.Connected || !exchange(StingerBattery, battery)) return true;
                byte code = r[9];
                if (code != 0x03 && code != 0x05 && code != 0x06) return true;
                s.Millivolts = r[11] | (r[12] << 8);
                s.Charging = code == 0x05;
                s.Percent = code == 0x06 ? 100 : Bat.Interpolate(s.Millivolts, code == 0x05 ? StingerChargingMv : StingerDischargingMv, StingerPct);
                return true;
            }
        }

        delegate bool PollFn(HidColl c, out DeviceStatus s);
        class Model { public ushort Vid; public ushort[] Pids; public Func<HidColl, bool> Accept; public PollFn Poll; }

        static readonly Model[] Models =
        {
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x05B7, 0x0C9D }, Accept = c => c.OutLen == 62 && c.InLen == 62, Poll = Cloud3 },
            // Cloud II Core Wireless: same 66 <cmd> protocol as Cloud III Wireless, 64-byte reports
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x069F, 0x0995 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 62 && c.InLen >= 62, Poll = Cloud3 },
            // Cloud III S: report 0C lives on the 64/64-byte collection (usage page 01C0)
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x06BE, 0x02CC }, Accept = c => c.OutLen == 64 && c.InLen == 64, Poll = Cloud3S },
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x0696, 0x018B, 0x0D93 }, Accept = c => c.Page == 0xFF90 && c.Usage == 0x0303, Poll = Cloud2 },
            new Model { Vid = 0x0951, Pids = new ushort[] { 0x1718 }, Accept = c => c.OutLen == 62, Poll = Cloud2Kingston },
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x098D }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Poll = CloudAlpha },
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x08BE }, Accept = c => c.Page == 0xFF13 && c.Usage == 0xFF00, Poll = CloudAlpha2 },
            new Model { Vid = 0x0951, Pids = new ushort[] { 0x16C4, 0x1723, 0x1749 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Poll = CloudFlight },
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x0E90 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Poll = CloudFlight },   // Cloud Flight (HP)
            new Model { Vid = 0x03F0, Pids = new ushort[] { 0x0A93 }, Accept = c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen >= 32, Poll = StingerCore },
            new Model { Vid = 0x0951, Pids = new ushort[] { 0x170B }, Accept = c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.FeatLen >= 32, Poll = StingerCore },
        };

        public static void Poll(PollContext ctx)
        {
            foreach (Model m in Models)
                foreach (ushort pid in m.Pids)
                {
                    if (ctx.IsClaimed(m.Vid, pid)) continue;
                    foreach (HidColl c in ctx.Find(m.Vid, pid, m.Accept))
                    {
                        DeviceStatus s;
                        if (m.Poll(c, out s)) { ctx.Add(s, m.Vid, pid); break; }
                    }
                }
        }
    }

    static class CoolerMasterDriver
    {
        const ushort Dongle = 0x01B7, MM712Cable = 0x0169, MM731Cable = 0x0167;

        static string ModelName(ushort pid, byte model)
        {
            if (pid == MM712Cable || (pid == Dongle && model == 0x50)) return "Cooler Master MM712";
            if (pid == MM731Cable || (pid == Dongle && model == 0x70)) return "Cooler Master MM731";
            return "Cooler Master wireless mouse";
        }

        // [46][81][00] -> status report [47][81][model] ... [10] percent, [11] charging, [12] link (0 cable, 1 dongle)
        static bool QueryStatus(HidDev d, ref byte[] r)
        {
            d.Flush();
            if (!d.Write(Bat.B(0x00, 0x46, 0x81, 0x00))) return false;
            long end = Bat.Now + 500;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref r, left) < 13) return false;
                if (r[0] == 0x47 && r[1] == 0x81) return true;
            }
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x2516))   // sorted: the cable ids come before the dongle
            {
                ushort vid = vp[0], pid = vp[1];
                if (pid != Dongle && pid != MM712Cable && pid != MM731Cable) continue;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Iface == 3 && x.Page == 0xFF0A))
                {
                    byte[] r = null;
                    using (var d = new HidDev())
                        if (!d.Open(c) || !QueryStatus(d, ref r)) continue;
                    var s = new DeviceStatus { Key = "coolermaster:" + Bat.Hex4(pid), Name = ModelName(pid, r[2]), Kind = DeviceKind.Mouse };
                    s.Via = r[12] == 0 ? "USB cable" : "2.4 GHz receiver";
                    if (r[10] >= 1 && r[10] <= 100) s.Percent = r[10];
                    s.Charging = r[11] != 0;
                    s.GraceSec = 1800;
                    // With the cable and the dongle both plugged in the mouse shows up twice; keep one
                    bool dup = false;
                    foreach (DeviceStatus o in ctx.Out)
                    {
                        if (o.Name != s.Name) continue;
                        dup = true;
                        o.Charging |= s.Charging;
                    }
                    if (dup) ctx.Claim(vid, pid);
                    else ctx.Add(s, vid, pid);
                    break;
                }
            }
        }
    }

    static class GloriousDriver
    {
        // G-Wolves model names and cable product ids from mouse.xyz/Config/env-models.json
        public static string GWolvesModelName(ushort pid)
        {
            switch (pid)
            {
                case 0x3808: case 0x3817: return "G-Wolves HTM Plus";
                case 0x6808: case 0x6817: return "G-Wolves HSK Pro 2.0";
                case 0x5608: case 0x5617: case 0x5618: case 0x5619: return "G-Wolves HTXU";
                case 0x3608: case 0x3617: case 0x3619: return "G-Wolves Fenrir Pro";
                case 0x3908: case 0x3917: return "G-Wolves VUK";
                case 0x7904: case 0x7913: return "G-Wolves HT-S2";
                case 0x3708: case 0x3717: return "G-Wolves Fenrir Max";
                case 0x5308: case 0x5317: return "G-Wolves HTS Ultra";
                case 0x7704: case 0x7713: return "G-Wolves HTR";
                case 0x3508: case 0x3517: return "G-Wolves Fenrir";
                case 0x7908: case 0x7917: return "G-Wolves HT-S2 Pro";
                case 0x5808: case 0x5817: return "G-Wolves HSK Pro";
                case 0x2708: case 0x2717: case 0x2719: return "G-Wolves HTX Mini";
                case 0x5408: case 0x5417: case 0x5418: case 0x5419: return "G-Wolves HTS Plus";
                case 0x5708: case 0x5717: return "G-Wolves HTX";
                case 0x5908: case 0x5917: return "G-Wolves HSK Plus";
                case 0x7204: case 0x7203: return "G-Wolves HSK Lite";
                case 0x7708: case 0x7717: return "G-Wolves HTR Pro";
                case 0x5804: case 0x5803: return "G-Wolves HSK Pro ACE";
                case 0x5404: case 0x5403: return "G-Wolves HTS Plus ACE";
                case 0x5704: case 0x5703: return "G-Wolves HTX ACE";
                case 0x5904: case 0x5903: return "G-Wolves HSK Plus ACE";
                case 0x4718: case 0x4719: return "G-Wolves Lycan";
                case 0x4219: return "G-Wolves WARG";
                case 0x5219: return "G-Wolves HTS Plus Pro";
                case 0x3519: return "G-Wolves Fenrir Asym";
                case 0x2619: return "G-Wolves HTX Mini Asym";
                case 0x1619: return "G-Wolves Fenrir Max Asym";
                case 0x2119: return "G-Wolves VUK Mini";
                default: return "G-Wolves wireless mouse";
            }
        }

        static readonly ushort[] GWolvesWired = { 0x1619, 0x2119, 0x2619, 0x2708, 0x2719, 0x3508, 0x3519, 0x3608, 0x3619, 0x3708, 0x3808, 0x3908,
                                                  0x4219, 0x4718, 0x4719, 0x5219, 0x5308, 0x5404, 0x5408, 0x5418, 0x5419, 0x5608, 0x5618, 0x5619,
                                                  0x5704, 0x5708, 0x5804, 0x5808, 0x5904, 0x5908, 0x6808, 0x7204, 0x7704, 0x7708, 0x7904, 0x7908 };
        public static bool GWolvesIsCable(ushort pid) { return Array.IndexOf(GWolvesWired, pid) >= 0; }

        class Vendor
        {
            public ushort Vid; public string KeyPrefix; public int ReplyDelayMs;
            public Func<ushort, string> Name; public Func<ushort, bool> IsCable; public Func<HidColl, bool> Matches;
        }

        static string GloriousName(ushort pid)
        {
            switch (pid)
            {
                case 0x2022: case 0x2011: return "Glorious Model O Wireless";
                case 0x2023: case 0x2012: return "Glorious Model D Wireless";
                case 0x2024: case 0x2013: return "Glorious Model O- Wireless";
                case 0x2025: case 0x2014: return "Glorious Model D- Wireless";
                case 0x2033: return "Glorious Model O 2 Wireless";
                case 0x2034: case 0x2036: case 0x201A: return "Glorious Model D 2 PRO";
                default: return null;   // another brand on the same Sinowealth chip (e.g. Redragon keyboards)
            }
        }

        // 258A is Sinowealth's vendor id, shared by many brands: work out keyboard vs mouse instead of assuming a mouse
        static DeviceKind SinowealthKind(List<HidColl> all, ushort vid, ushort pid, string name)
        {
            DeviceKind k = Bat.GuessKind(name);
            if (k != DeviceKind.Other) return k;
            int keyboards = 0;
            foreach (HidColl c in all) if (c.Vid == vid && c.Pid == pid && c.Page == 0x01 && c.Usage == 0x06) keyboards++;
            if (keyboards >= 2) return DeviceKind.Keyboard;   // keyboards expose boot + NKRO keyboard collections
            return Bat.KindFromCollections(all, vid, pid);
        }

        static bool GloriousCable(ushort pid) { return pid == 0x2011 || pid == 0x2012 || pid == 0x2013 || pid == 0x2014 || pid == 0x201A; }

        static string HitscanName(ushort pid)
        {
            switch (pid)
            {
                case 0x0136: return "Hitscan Hyperlight X";
                case 0x0194: return "Hitscan Hyperlight N";
                default: return "Hitscan Hyperlight";
            }
        }
        static bool HitscanCable(ushort pid) { return pid == 0x0136 || pid == 0x0194; }

        static readonly Vendor[] Vendors =
        {
            new Vendor { Vid = 0x258A, KeyPrefix = "glorious:", ReplyDelayMs = 50, Name = GloriousName, IsCable = GloriousCable },
            new Vendor { Vid = 0x3770, KeyPrefix = "hitscan:", ReplyDelayMs = 200, Name = HitscanName, IsCable = HitscanCable },
            new Vendor { Vid = 0x33E4, KeyPrefix = "gwolves:", ReplyDelayMs = 100, Name = GWolvesModelName, IsCable = GWolvesIsCable },
            new Vendor { Vid = 0x36A7, KeyPrefix = "wlmouse:", ReplyDelayMs = 100, Name = p => "WLMouse YING", IsCable = p => false,
                Matches = c => c.Pid == 0xA874 && c.Page == 0xFFFF && c.Usage == 0 },
        };

        public static void Poll(PollContext ctx)
        {
            foreach (Vendor v in Vendors)
                foreach (ushort[] vp in ctx.Products(v.Vid))
                {
                    ushort vid = vp[0], pid = vp[1];
                    foreach (HidColl c in ctx.Find(vid, pid, x => x.FeatLen == 65 && (v.Matches == null || v.Matches(x))))
                    {
                        byte[] r = null;
                        bool ok = false;
                        using (var d = new HidDev())
                        {
                            if (!d.OpenFeatureOnly(c)) continue;
                            for (int attempt = 0; attempt < 2; attempt++)
                            {
                                var w = new byte[65];
                                w[3] = 0x02; w[4] = 0x02; w[6] = 0x83;
                                if (!d.SetFeature(w)) break;
                                Bat.Sleep(v.ReplyDelayMs);
                                if (!d.GetFeature(0, out r) || r.Length < 9) break;
                                if (r[6] != 0x83 || (r[1] & 0xF0) != 0xA0) break;   // not this protocol
                                if (r[1] == 0xA0 && attempt == 0) { Bat.Sleep(400); continue; }   // waking up: ask again
                                ok = true;
                                break;
                            }
                        }
                        if (!ok) continue;
                        var s = new DeviceStatus { Key = v.KeyPrefix + Bat.Hex4(pid) };
                        string known = v.Name(pid);
                        if (known != null) { s.Name = known; s.Kind = DeviceKind.Mouse; }
                        else
                        {
                            s.Name = Hid.Product(c);
                            if (s.Name.Length == 0) s.Name = "Wireless device";
                            s.Kind = SinowealthKind(ctx.Colls, vid, pid, s.Name);
                        }
                        s.Via = v.IsCable(pid) ? "USB cable" : "2.4 GHz receiver";
                        s.Charging = v.IsCable(pid) || r[7] == 0x01;
                        if (r[1] == 0xA1 && r[8] <= 100) s.Percent = Bat.Clamp100(r[8] != 0 ? r[8] : 1);
                        else s.Asleep = true;
                        ctx.Add(s, vid, pid);
                        break;
                    }
                }
        }
    }
}
