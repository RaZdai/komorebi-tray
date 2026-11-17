// SteelSeries and Corsair, ported from Wireless Battery (steelseries.cpp, corsair.cpp; GPL-3.0-or-later).
// SteelSeries headset protocols follow github.com/Sapd/HeadsetControl, mouse protocols github.com/flozz/rivalcfg.
// Corsair: Void / HS family (HeadsetControl) and "Bragi" devices (Virtuoso, Void V2, HS80 MAX, Slipstream mice and
// keyboards): 02 <target> 02 <property> = GET; property 0F battery (0..1000), 10 charge state, 12 product id.
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class SteelSeriesDriver
    {
        static bool In(ushort pid, params ushort[] list) { return Array.IndexOf(list, pid) >= 0; }

        static bool WriteRead(HidDev d, byte[] req, ref byte[] resp, int timeoutMs = 1000)
        {
            d.Flush();
            return d.Write(req) && d.Read(ref resp, timeoutMs) > 0;
        }

        static DeviceStatus Headset(ushort pid, string name)
        {
            return new DeviceStatus { Key = "steelseries:" + Bat.Hex4(pid), Name = name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
        }

        // Arctis 1 / 1 Xbox / 7X: 06 12 -> [2]==01 offline, [3] %
        static bool Arctis1(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x06, 0x12), ref r) || r.Length < 4) return false;
            s = Headset(pid, "SteelSeries Arctis 1 Wireless");
            s.Connected = r[2] != 0x01;
            if (s.Connected) s.Percent = Bat.Clamp100(r[3]);
            return true;
        }

        // Arctis 7 / 7 2019 / Pro 2019: 06 18 -> [2] %
        static bool Arctis7(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x06, 0x18), ref r) || r.Length < 3) return false;
            s = Headset(pid, pid == 0x1252 ? "SteelSeries Arctis Pro" : "SteelSeries Arctis 7");
            s.Percent = Bat.Clamp100(r[2]);
            s.Connected = r[2] != 0;
            return true;
        }

        // Arctis 9: 00 20 -> [3] raw (0x64..0x9A), [4]==01 charging
        static bool Arctis9(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x00, 0x20), ref r) || r.Length < 5) return false;
            s = Headset(pid, "SteelSeries Arctis 9");
            s.Percent = Bat.MapTo100(r[3], 0x64, 0x9A);
            s.Charging = r[4] == 0x01;
            return true;
        }

        // Arctis Pro Wireless: 41 AA -> [0]==02 offline; 40 AA -> [0] level 0..4. Interface 0 doesn't number its
        // reports, so on Windows the command goes behind a 0 report id
        static bool ArctisProCmd(HidDev d, byte cmd, ref byte[] r)
        {
            if (WriteRead(d, Bat.B(0x00, cmd, 0xAA), ref r) && r.Length > 0) return true;
            return WriteRead(d, Bat.B(cmd, 0xAA), ref r) && r.Length > 0;   // in case a firmware numbers its reports
        }

        static bool ArctisProWireless(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!ArctisProCmd(d, 0x41, ref r)) return false;
            s = Headset(pid, "SteelSeries Arctis Pro Wireless");
            s.Connected = r[0] != 0x02;
            if (s.Connected && ArctisProCmd(d, 0x40, ref r) && r[0] <= 4) s.Percent = Bat.MapTo100(r[0], 0, 4);
            return true;
        }

        // Arctis Nova Pro Wireless: 06 B0 -> [15] status (01 offline, 02 charging), [6] level 0..8
        static bool NovaProWireless(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x06, 0xB0), ref r) || r.Length < 16) return false;
            s = Headset(pid, "SteelSeries Arctis Nova Pro Wireless");
            s.Connected = r[15] != 0x01;
            s.Charging = r[15] == 0x02;
            if (s.Connected) s.Percent = Bat.MapTo100(r[6], 0, 8);
            return true;
        }

        // Nova family: 00 B0 -> status block; layout depends on the model
        static bool NovaFamily(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x00, 0xB0), ref r) || r.Length < 7) return false;
            if (In(pid, 0x220E, 0x2212, 0x2216, 0x2236))   // Arctis 7+
            {
                s = Headset(pid, "SteelSeries Arctis 7+");
                s.Connected = r[1] != 0x01;
                s.Percent = Bat.MapTo100(r[2], 0, 4);
                s.Charging = r[3] == 0x01;
            }
            else if (In(pid, 0x2269, 0x226D, 0x2232, 0x2253))   // Nova 3P Wireless, Nova 5
            {
                s = Headset(pid, In(pid, 0x2232, 0x2253) ? "SteelSeries Arctis Nova 5" : "SteelSeries Arctis Nova 3P Wireless");
                s.Connected = r[1] != 0x02;
                s.Percent = Bat.Clamp100(r[3]);
                s.Charging = r[4] == 0x01;
            }
            else if (pid == 0x230A)   // Arctis GameBuds: lowest of the buds that are out of the case
            {
                s = Headset(pid, "SteelSeries Arctis GameBuds");
                int lvl = -1;
                if (r[3] == 0x03) lvl = r[5];
                if (r[4] == 0x03) lvl = lvl < 0 ? r[6] : Math.Min(lvl, (int)r[6]);
                s.Connected = lvl >= 0;
                s.Percent = lvl >= 0 ? Bat.Clamp100(lvl) : -1;
            }
            else   // Arctis Nova 7 / 7X / 7P and variants
            {
                bool discrete = In(pid, 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4);
                s = Headset(pid, "SteelSeries Arctis Nova 7");
                s.Connected = r[3] != 0x00;
                s.Charging = r[3] == 0x01 || r[3] == 0x02;
                s.Percent = discrete ? Bat.MapTo100(r[2], 0, 4) : Bat.Clamp100(r[2]);
            }
            if (!s.Connected) s.Percent = -1;
            return true;
        }

        static DeviceStatus Mouse(ushort pid, string name, bool wired)
        {
            return new DeviceStatus { Key = "steelseries:" + Bat.Hex4(pid), Name = name, Kind = DeviceKind.Mouse, Via = wired ? "USB cable" : "2.4 GHz receiver" };
        }

        // Aerox 3/5/9 Wireless, Prime Wireless: 92 (wired) / D2 (wireless) -> [1] = (level/5 + 1) | charging<<7
        static bool Aerox(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            bool wired = In(pid, 0x183A, 0x1854, 0x185A, 0x1842);
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x00, wired ? 0x92 : 0xD2), ref r, 300) || r.Length < 2) return false;
            string name = In(pid, 0x1838, 0x183A) ? "SteelSeries Aerox 3 Wireless"
                        : In(pid, 0x1852, 0x1854) ? "SteelSeries Aerox 5 Wireless"
                        : In(pid, 0x1858, 0x185A) ? "SteelSeries Aerox 9 Wireless" : "SteelSeries Prime Wireless";
            s = Mouse(pid, name, wired);
            int raw = r[1] & 0x7F;
            if (raw == 0) s.Connected = false;   // mouse off / out of range
            else if (raw <= 21) { s.Percent = (raw - 1) * 5; s.Charging = (r[1] & 0x80) != 0; }   // only 1..21 are valid
            return true;
        }

        // Rival 3 Wireless (+ Gen 2), Rival 650: AA 01 -> [0] %, [2] charging
        static bool Rival(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            byte[] r = null;
            if (!WriteRead(d, Bat.B(0x00, 0xAA, 0x01), ref r, 300) || r.Length < 3) return false;
            bool rival650 = pid == 0x172B || pid == 0x1726;
            s = Mouse(pid, rival650 ? "SteelSeries Rival 650 Wireless" : pid == 0x1872 ? "SteelSeries Rival 3 Wireless Gen 2" : "SteelSeries Rival 3 Wireless", pid == 0x172B);
            if (rival650) s.Key = "steelseries:172B";   // one entry whether it answers on the cable or the receiver
            s.Percent = Bat.Clamp100(r[0]);
            s.Charging = r[2] != 0;
            return true;
        }

        // Rival Pro (UWB, hot-swappable batteries): 92 -> 92 00 <mouse %> <flag> <spare battery %> (rivalcfg PR #294)
        static bool RivalPro(HidDev d, ushort pid, out DeviceStatus s)
        {
            s = null;
            d.Flush();
            if (!d.Write(Bat.B(0x00, 0x92))) return false;
            byte[] r = null;
            long end = Bat.Now + 500;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref r, left) < 3) return false;
                if (r[0] == 0x92 && r[1] == 0x00) break;
            }
            if (r[2] > 100) return false;
            s = Mouse(pid, "SteelSeries Rival Pro", false);
            s.Via = "UWB base station";
            s.Percent = r[2];
            return true;
        }

        delegate bool PollFn(HidDev d, ushort pid, out DeviceStatus s);
        class Model { public ushort[] Pids; public Func<HidColl, bool> Accept; public PollFn Poll; }

        static bool PageFF43(HidColl c) { return c.Page == 0xFF43 && c.Usage == 0x0202; }
        static bool PageFFC0(HidColl c) { return c.Page == 0xFFC0 && c.Usage == 0x0001; }
        static bool Iface0(HidColl c) { return c.Iface == 0 && c.OutLen > 0; }
        static bool Iface2(HidColl c) { return c.Iface == 2 && c.OutLen > 0; }
        static bool Iface3(HidColl c) { return c.Iface == 3 && c.OutLen > 0; }
        static bool Iface4(HidColl c) { return c.Iface == 4 && c.OutLen > 0; }

        static readonly Model[] Models =
        {
            new Model { Pids = new ushort[] { 0x12B3, 0x12B6, 0x12D7 }, Accept = PageFF43, Poll = Arctis1 },
            new Model { Pids = new ushort[] { 0x1260, 0x12AD, 0x1252 }, Accept = PageFF43, Poll = Arctis7 },
            new Model { Pids = new ushort[] { 0x12C2 }, Accept = Iface0, Poll = Arctis9 },
            new Model { Pids = new ushort[] { 0x1290 }, Accept = Iface0, Poll = ArctisProWireless },
            new Model { Pids = new ushort[] { 0x12E0, 0x225D }, Accept = Iface4, Poll = NovaProWireless },
            new Model { Pids = new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236, 0x2269, 0x226D, 0x2232, 0x2253, 0x230A, 0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E,
                0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7 }, Accept = PageFFC0, Poll = NovaFamily },
            new Model { Pids = new ushort[] { 0x1838, 0x183A, 0x1852, 0x1854, 0x1858, 0x185A, 0x1840, 0x1842 }, Accept = Iface3, Poll = Aerox },
            new Model { Pids = new ushort[] { 0x1830, 0x1872 }, Accept = Iface3, Poll = Rival },
            new Model { Pids = new ushort[] { 0x172B, 0x1726 }, Accept = Iface0, Poll = Rival },   // Rival 650: cable, 2.4 GHz receiver
            new Model { Pids = new ushort[] { 0x1880 }, Accept = Iface2, Poll = RivalPro },
        };

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x1038))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (Model m in Models)
                {
                    if (Array.IndexOf(m.Pids, pid) < 0) continue;
                    foreach (HidColl c in ctx.Find(vid, pid, m.Accept))
                    {
                        DeviceStatus s;
                        using (var d = new HidDev())
                            if (!d.Open(c) || !m.Poll(d, pid, out s)) continue;
                        // A Rival 650 charging on its cable can answer on both products; keep the cable's reading
                        int dup = ctx.Out.FindIndex(o => o.Key == s.Key);
                        if (dup < 0) ctx.Add(s, vid, pid);
                        else
                        {
                            if (s.Via == "USB cable") ctx.Out[dup] = s;
                            ctx.Claim(vid, pid);
                        }
                        break;
                    }
                    break;
                }
            }
        }
    }

    static class CorsairDriver
    {
        static readonly ushort[] VoidPids = { 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23,
                                              0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 };
        static readonly ushort[] BragiPids = { 0x2A08, 0x2A02, 0x0A97, 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x2B00 };

        // Void / Void Pro / Void Elite / HS70 / HS80: C9 64 -> [2] % (bit 7 = mic up), [4] status (0 off, 1-2 ok, 4-5 charging)
        static bool PollVoid(HidColl c, out DeviceStatus s)
        {
            s = null;
            using (var d = new HidDev())
            {
                if (!d.Open(c)) return false;
                d.Flush();
                byte[] r = null;
                if (!d.Write(Bat.B(0xC9, 0x64))) return false;
                long end = Bat.Now + 1000;
                int left = (int)(end - Bat.Now);
                if (left <= 0 || d.Read(ref r, left) < 5) return false;
                byte st = r[4];
                s = new DeviceStatus { Key = "corsair:" + Bat.Hex4(c.Pid), Name = Hid.Product(c) };
                if (s.Name.Length == 0) s.Name = "Corsair wireless headset";
                s.Kind = DeviceKind.Headset;
                s.Via = "2.4 GHz receiver";
                s.Connected = st != 0;
                s.Charging = st == 4 || st == 5;
                if (st == 1 || st == 2 || s.Charging) s.Percent = Bat.Clamp100(r[2] & 0x7F);
                return true;
            }
        }

        // Two framings: "prefixed" (older Bragi) 02 <target> 02 <prop>, reply 02 <source> 02 <status> <value>; "plain"
        // (newer Slipstream receivers such as 1B1C:2B00) 00 <target> 02 <prop>, reply <source> 02 <status> <value>.
        // The first framing that answers is remembered for this handle
        class Bragi : IDisposable
        {
            readonly HidDev d = new HidDev();
            readonly bool numbered, ok;
            int mode;   // 0 unknown, 1 prefixed, 2 plain

            public Bragi(HidColl c) { numbered = c.OutLen == 64; ok = d.Open(c); }
            public bool Ok { get { return ok; } }
            public void Dispose() { d.Dispose(); }

            public bool Get(byte target, byte prop, out uint value, int timeoutMs = 300)
            {
                value = 0;
                if (mode != 2 && GetFramed(false, target, prop, out value, timeoutMs)) { mode = 1; return true; }
                if (mode == 1) return false;
                if (GetFramed(true, target, prop, out value, timeoutMs)) { mode = 2; return true; }
                return false;
            }

            bool GetFramed(bool plain, byte target, byte prop, out uint value, int timeoutMs)
            {
                value = 0;
                byte[] req = plain ? Bat.B(0x00, target, 0x02, prop) : numbered ? Bat.B(0x02, target, 0x02, prop) : Bat.B(0x00, 0x02, target, 0x02, prop);
                d.Flush();
                if (!d.Write(req)) return false;
                byte source = (byte)(target == 0x08 ? 0x00 : 0x01);
                int o = plain ? 0 : 1;   // offset of the source byte in the reply
                long end = Bat.Now + timeoutMs;
                byte[] r = null;
                for (;;)
                {
                    int left = (int)(end - Bat.Now);
                    if (left <= 0) return false;
                    int n = d.Read(ref r, left);
                    if (n < 0) return false;
                    if (n < o + 7) continue;
                    if (r[o + 1] != 0x02 || r[o] != source) continue;   // notification or another app's reply
                    if (r[o + 2] != 0x00) return false;
                    value = (uint)(r[o + 3] | (r[o + 4] << 8) | (r[o + 5] << 16)) | ((uint)r[o + 6] << 24);
                    return true;
                }
            }
        }

        // Devices seen behind a receiver, identified by the product id they report (property 12)
        static string PairedName(uint pid, out DeviceKind kind)
        {
            kind = DeviceKind.Other;
            if (pid == 0x1B7E) { kind = DeviceKind.Mouse; return "Corsair Dark Core RGB Pro SE"; }   // tested on Slipstream receiver 1BDC
            return null;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x1B1C))
            {
                ushort vid = vp[0], pid = vp[1];
                if (Array.IndexOf(VoidPids, pid) >= 0)
                {
                    foreach (HidColl c in ctx.Find(vid, pid, x => (x.Page == 0xFFC5 || x.Page == 0xFF00) && x.OutLen > 0))
                    {
                        DeviceStatus s;
                        if (PollVoid(c, out s)) { ctx.Add(s, vid, pid); break; }
                    }
                    continue;
                }
                bool knownBragi = Array.IndexOf(BragiPids, pid) >= 0;
                var colls = ctx.Find(vid, pid, x =>
                {
                    bool sizes = (x.OutLen == 64 || x.OutLen == 65) && (x.InLen == 64 || x.InLen == 65);
                    return sizes && (x.Page == 0xFF42 || (knownBragi && x.Page >= 0xFF00) || (knownBragi && x.Iface == 4));
                });
                foreach (HidColl c in colls)
                {
                    using (var b = new Bragi(c))
                    {
                        if (!b.Ok) continue;
                        bool any = false;
                        string product = Hid.Product(c);
                        // Paired devices behind a receiver first, then the device itself (wired / Bluetooth)
                        foreach (byte target in new byte[] { 0x09, 0x0A, 0x0B, 0x08 })
                        {
                            if (target == 0x08 && any) break;
                            uint level;
                            bool ok = b.Get(target, 0x0F, out level);
                            for (int retry = 0; ok && (level == 0 || level > 1000) && retry < 2; retry++) ok = b.Get(target, 0x0F, out level);
                            if (!ok || level > 1000) continue;
                            var s = new DeviceStatus { Key = "corsair:" + Bat.Hex4(pid) + ":" + target, Name = product.Length == 0 ? "Corsair wireless device" : product };
                            s.Kind = Bat.GuessKind(s.Name);
                            // A receiver names itself, not the device behind it: property 12 is the paired device's product id
                            uint devPid;
                            if (target != 0x08 && b.Get(target, 0x12, out devPid))
                            {
                                DeviceKind k;
                                string nm = PairedName(devPid, out k);
                                if (nm != null) { s.Name = nm; s.Kind = k; }
                                else if ((devPid >> 8) == 0x0A)
                                {
                                    s.Kind = DeviceKind.Headset;
                                    if (product.Contains("Receiver")) s.Name = "Corsair wireless headset";
                                }
                            }
                            // Corsair headsets and their receivers use product ids 0Axx
                            if (s.Kind == DeviceKind.Other && (pid >> 8) == 0x0A) s.Kind = DeviceKind.Headset;
                            if (s.Kind == DeviceKind.Other) s.Kind = Bat.KindFromCollections(ctx.Colls, vid, pid);
                            s.Via = target == 0x08 ? "USB / Bluetooth" : "Corsair receiver";
                            s.Percent = Bat.Clamp100((int)(level + 5) / 10);
                            uint charge;
                            if (b.Get(target, 0x10, out charge)) s.Charging = charge == 1 || charge == 3;
                            ctx.Out.Add(s);
                            any = true;
                        }
                        if (any) { ctx.Claim(vid, pid); break; }
                    }
                }
            }
        }
    }
}
