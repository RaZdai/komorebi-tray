// Smaller mouse brands, each with a community-documented protocol. Ported from Wireless Battery (misc_mice.cpp;
// GPL-3.0-or-later): Motospeed X6 (LGSTrayBattery-MotospeedX6), Xenta receivers for Delux / Attack Shark
// (omarchy-delux-m900, qu1ncky/BatteryTray), Ajazz AJ179 (Rockeyxx/aj179-linux), AJAZZ AJ-series on 3151
// (Aiacos/ajazz-control-center), AJ139 V2 (werquess/ajazz-aj139-macos, nico2525nn/AjazzAJ139V2), Incott G23 V2 / G24
// (Incott-Manager, incott-mouse-app, MouseBattery), Delux M800 Pro, Wraith W1 (w1.software web driver).
using System;
using System.Collections.Generic;
using System.Text;

namespace KomorebiTray
{
    static class MiscMiceDriver
    {
        static bool ReadUntil(HidDev d, ref byte[] r, int timeoutMs, Func<byte[], bool> match)
        {
            long end = Bat.Now + timeoutMs;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref r, left) < 0) return false;
                if (match(r)) return true;
            }
            return false;
        }

        // Motospeed X6 (0BDA): output B5 [06] -> report B4, byte 20 = level (+128 while charging); the cable reports nothing
        static void PollMotospeed(PollContext ctx)
        {
            foreach (ushort pid in new ushort[] { 0xFFE0, 0xFFF2, 0xFFF1 })
            {
                if (ctx.IsClaimed(0x0BDA, pid)) continue;
                foreach (HidColl c in ctx.Find(0x0BDA, pid, x => x.Iface == 4 && x.OutLen >= 21))
                {
                    var s = new DeviceStatus { Key = "motospeed:" + Bat.Hex4(pid), Name = "Motospeed X6", Kind = DeviceKind.Mouse };
                    if (pid == 0xFFF1) { s.Via = "USB cable"; s.Charging = true; ctx.Add(s, 0x0BDA, pid); break; }
                    byte[] r = null;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        d.Flush();
                        if (!d.Write(Bat.B(0xB5, 0x06))) continue;
                        if (!ReadUntil(d, ref r, 1500, x => x.Length > 20 && x[0] == 0xB4)) continue;
                    }
                    int raw = r[20];
                    s.Via = "2.4 GHz receiver";
                    s.Charging = raw >= 128;
                    s.Percent = Bat.Clamp100(s.Charging ? raw - 128 : raw);
                    ctx.Add(s, 0x0BDA, pid);
                    break;
                }
            }
        }

        // Xenta receiver (1D57:FA60, also FA65; cable FA55 / 2155). The receiver broadcasts [03][model][40][state][level]
        // every ~2 s; nothing is sent to it. The handle stays open between polls so these frames queue up. A reading
        // counts once two frames agree, because every link-up starts with a false 100%.
        class XentaModel { public byte Code; public string Name; public bool Tens; }
        static readonly XentaModel[] XentaModels =
        {
            new XentaModel { Code = 0x3D, Name = "Delux M900 Pro" },
            new XentaModel { Code = 0x55, Name = "Attack Shark X11" },
            new XentaModel { Code = 0x4E, Name = "Attack Shark X3 Max" },
            new XentaModel { Code = 0xE5, Name = "Attack Shark V3 Pro" },
            new XentaModel { Code = 0x10, Name = "Attack Shark X3 / X6 / R1", Tens = true },   // level in tens (0-10); the X3 reports this code too
        };

        class XentaListener { public HidDev Dev; public byte[] Last, Prev; public long LastFrameAt; }
        static readonly Dictionary<string, XentaListener> xenta = new Dictionary<string, XentaListener>();

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return a == b;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void PollXenta(PollContext ctx)
        {
            var present = new HashSet<string>();
            foreach (ushort pid in new ushort[] { 0xFA60, 0xFA65, 0xFA55, 0x2155 })
            {
                if (ctx.IsClaimed(0x1D57, pid)) continue;
                foreach (HidColl c in ctx.Find(0x1D57, pid, x => x.Page == 0x000A && x.InLen >= 5))
                {
                    present.Add(c.Path);
                    XentaListener l;
                    bool fresh = !xenta.TryGetValue(c.Path, out l);
                    if (fresh)
                    {
                        l = new XentaListener { Dev = new HidDev() };
                        if (!l.Dev.OpenReadOnly(c)) { l.Dev.Dispose(); continue; }
                        xenta[c.Path] = l;
                    }
                    // Take everything queued since the last poll; a new listener waits for two frames
                    long until = Bat.Now + (fresh ? 5000 : 0);
                    byte[] r = null;
                    int got = 0;
                    for (;;)
                    {
                        int wait = fresh && got < 2 ? (int)Math.Max(1, until - Bat.Now) : 30;
                        if (fresh && got < 2 && until - Bat.Now <= 0) break;
                        if (l.Dev.Read(ref r, wait) < 5) break;
                        if (r[0] != 0x03 || r[2] != 0x40) continue;
                        l.Prev = l.Last;
                        l.Last = new[] { r[0], r[1], r[2], r[3], r[4] };
                        l.LastFrameAt = Bat.Now;
                        got++;
                    }
                    if (l.Last == null || Bat.Now - l.LastFrameAt > 15000) break;   // mouse off: no frames
                    bool cable = pid == 0xFA55 || pid == 0x2155;
                    byte code = l.Last[1], st = l.Last[3], level = l.Last[4];
                    XentaModel model = null;
                    foreach (XentaModel m in XentaModels) if (m.Code == code) model = m;
                    var s = new DeviceStatus { Key = "xenta:" + Bat.Hex4(pid), Name = model != null ? model.Name : "Wireless mouse", Kind = DeviceKind.Mouse };
                    s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                    bool agreed = Same(l.Prev, l.Last);
                    if (cable || st == 0x02 || st == 0x03)
                    {
                        s.Charging = true;   // the level is frozen on external power
                        if (st == 0x02 && !cable) s.Percent = 100;
                    }
                    else if (agreed)
                    {
                        if (model != null && model.Tens) { if (level >= 1 && level <= 10) s.Percent = level * 10; }
                        else if (st == 0x01 && level >= 1 && level <= 100) s.Percent = level;
                    }
                    if (s.Percent < 0 && !s.Charging) s.Asleep = true;   // shown with the last known value
                    ctx.Add(s, 0x1D57, pid);
                    break;
                }
            }
            var gone = new List<string>();   // forget receivers that were unplugged
            foreach (var kv in xenta) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string k in gone) { xenta[k].Dev.Dispose(); xenta.Remove(k); }
        }

        // For --dump: the raw input reports each Xenta status collection sends during ~6 s
        public static string DumpXentaFrames()
        {
            var output = new StringBuilder();
            foreach (HidColl c in Hid.Enumerate())
            {
                if (c.Vid != 0x1D57 || c.Page != 0x000A || c.InLen < 5) continue;
                output.AppendFormat("\nXenta {0:X4}:{1:X4} status frames (6 s):\n", c.Vid, c.Pid);
                using (var d = new HidDev())
                {
                    if (!d.OpenReadOnly(c)) { output.Append("  cannot open (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")\n"); continue; }
                    byte[] r = null;
                    int frames = 0;
                    long end = Bat.Now + 6000;
                    while (end - Bat.Now > 0 && frames < 16)
                    {
                        if (d.Read(ref r, (int)Math.Max(1, end - Bat.Now)) < 0) break;
                        output.Append(" ");
                        foreach (byte b in r) output.AppendFormat(" {0:X2}", b);
                        output.Append("\n");
                        frames++;
                    }
                    if (frames == 0) output.Append("  none (mouse off, asleep or on Bluetooth?)\n");
                }
            }
            return output.ToString();
        }

        // Ajazz AJ179 (249A receiver, 248A cable, interface 2): command 10 (device info)
        static void PollAjazz(PollContext ctx)
        {
            foreach (ushort vendor in new ushort[] { 0x249A, 0x248A })
                foreach (ushort[] vp in ctx.Products(vendor))
                {
                    ushort vid = vp[0], pid = vp[1];
                    foreach (HidColl c in ctx.Find(vid, pid, x => x.Iface == 2 && x.OutLen == 33 && x.InLen == 33))
                    {
                        byte[] r = null;
                        bool ok;
                        using (var d = new HidDev())
                        {
                            if (!d.Open(c)) continue;
                            d.Flush();
                            if (!d.Write(Bat.B(0x00, 0x10))) continue;   // report id 0, command 10, zero payload (checksum 0)
                            ok = ReadUntil(d, ref r, 1500, x =>
                            {
                                if (x.Length < 32 || x[0] != 0x10 || x[1] != 0x00) return false;
                                int sum = 0;
                                for (int i = 4; i <= 30; i++) sum += x[i];
                                return (byte)sum == x[31];
                            });
                        }
                        if (!ok || r[13] > 100) continue;
                        var model = new StringBuilder();
                        for (int i = 4; i < 8 && r[i] >= 0x20 && r[i] < 0x7F; i++) model.Append((char)r[i]);
                        string m = model.ToString();
                        bool receiver = vid == 0x249A;
                        var s = new DeviceStatus { Key = "ajazz:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Kind = DeviceKind.Mouse };
                        s.Name = m.Length == 4 && m[0] == 'M' ? "Ajazz AJ" + m.Substring(1) : "Ajazz mouse";
                        s.Via = receiver ? "2.4 GHz receiver" : "USB cable";
                        s.Connected = !receiver || r[14] != 0;
                        s.Charging = r[12] != 0;
                        if (s.Connected) s.Percent = r[13];
                        ctx.Add(s, vid, pid);
                        break;
                    }
                }
        }

        // AJAZZ AJ-series on 3151: SET_FEATURE F7 brings the telemetry link up; ~30 ms later GET_FEATURE 05, byte 3 = charge
        static void PollAjazzAjSeries(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x3151))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFFFF && x.Usage == 0x02 && x.FeatLen >= 65))
                {
                    byte[] r;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        var req = new byte[c.FeatLen];
                        req[1] = 0xF7;
                        if (!d.SetFeature(req)) continue;
                        Bat.Sleep(30);
                        if (!d.GetFeature(0x05, out r) || r.Length < 4) continue;
                    }
                    if (r[0] != 0x05 || r[1] != 0 || r[2] != 0) continue;   // malformed / reconnect frame
                    int pct = r[3];
                    if (pct > 100) continue;
                    var s = new DeviceStatus { Key = "ajazzaj:" + Bat.Hex4(vid) + ":" + Bat.Hex4(pid), Name = Hid.Product(c), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
                    if (s.Name.Length == 0) s.Name = "AJAZZ mouse";
                    s.Connected = pct > 0;   // 0 means the link is up but the mouse is off
                    if (s.Connected) s.Percent = pct;
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }

        // AJAZZ AJ139 V2 on PID 2255 (A8A5 dongle, A8A4 cable): [55][30][A5][chk][2E]..., reply [AA][30] [8] % [9] USB power
        static void PollAjazzAj139(PollContext ctx)
        {
            const ushort pid = 0x2255;
            foreach (ushort vendor in new ushort[] { 0xA8A5, 0xA8A4 })
            {
                if (ctx.IsClaimed(vendor, pid)) continue;
                foreach (HidColl c in ctx.Find(vendor, pid, x => x.Page == 0xFF01 && x.Usage == 0x0010 && x.OutLen >= 65 && x.InLen >= 65))
                {
                    byte[] r = null;
                    bool ok;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        Func<byte[], bool> isBattery = x => x.Length > 9 && x[0] == 0xAA && x[1] == 0x30;
                        d.Flush();
                        ok = d.Write(Bat.B(0x00, 0x55, 0x30, 0xA5, 0x2E, 0x2E)) && ReadUntil(d, ref r, 700, isBattery);
                        if (!ok) ok = d.Write(Bat.B(0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01)) && ReadUntil(d, ref r, 800, isBattery);
                    }
                    if (!ok || r[8] > 100) continue;
                    bool cable = vendor == 0xA8A4;
                    var s = new DeviceStatus { Key = "ajazz139:" + Bat.Hex4(vendor) + ":" + Bat.Hex4(pid), Name = "AJAZZ wireless mouse", Kind = DeviceKind.Mouse };
                    s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                    s.Charging = r[9] != 0;
                    s.Connected = cable || r[8] > 0;   // 0 from the dongle: the mouse is off or out of range
                    if (s.Connected) s.Percent = r[8];
                    ctx.Add(s, vendor, pid);
                    break;
                }
            }
        }

        // Wraith W1: 8F device info (model 18 / 20), then a read-only 86 sweep and keep-alive while the status input
        // report 09 is awaited. Once a dongle answered as a W1 it stays one, so a sleeping W1 isn't handed to Incott.
        static readonly int[] knownModel = { 0, 0 };   // 522C, 622C

        static void PollWraithW1(PollContext ctx)
        {
            foreach (ushort pid in new ushort[] { 0x522C, 0x622C })
            {
                if (ctx.IsClaimed(0x093A, pid)) continue;
                int mi = pid == 0x622C ? 1 : 0;
                var colls = ctx.Find(0x093A, pid, c => c.Page == 0xFF05 && c.FeatLen >= 9);
                if (colls.Count == 0) continue;
                colls.Reverse();   // on the cable the first FF05 collection rejects SET_FEATURE
                var d = new HidDev();
                try
                {
                    int replied = 0;   // model code from the device-info read, -1 = answered but not a W1
                    foreach (HidColl c in colls)
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        if (!d.SetFeature(Bat.B(0x09, 0x8F))) { d.Close(); continue; }
                        byte[] r;
                        for (int i = 0; i < 10 && replied == 0; i++)
                        {
                            Bat.Sleep(50);
                            if (!d.GetFeature(0x09, out r)) break;
                            if (r.Length >= 4 && r[0] == 0x09 && r[1] == 0x8F) replied = r[3] == 18 || r[3] == 20 ? r[3] : -1;
                        }
                        if (replied != 0) break;
                        d.Close();
                    }
                    if (replied < 0) { knownModel[mi] = 0; continue; }   // another PixArt mouse (Incott)
                    if (replied != 0) knownModel[mi] = replied;
                    if (knownModel[mi] == 0) continue;   // no answer and never seen as a W1

                    bool cable = pid == 0x622C;
                    var s = new DeviceStatus { Key = "wraithw1:" + Bat.Hex4(pid), Name = knownModel[mi] == 20 ? "Wraith W1 Freestyle" : "Wraith W1", Kind = DeviceKind.Mouse };
                    s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                    if (!cable) s.GraceSec = 1800;
                    s.Connected = false;
                    if (d.Ok)
                    {
                        // Listen on every FF05 collection with an input report: the status push may arrive on any of them
                        var ins = new List<HidDev>();
                        try
                        {
                            foreach (HidColl c in ctx.Find(0x093A, pid, x => x.Page == 0xFF05 && x.InLen >= 2))
                            {
                                var inp = new HidDev();
                                if (!inp.OpenReadOnly(c)) { inp.Dispose(); continue; }
                                inp.Flush();
                                ins.Add(inp);
                            }
                            byte[] r = null;
                            bool got = false;
                            // One 86 the way the web driver's _i() does it: SET_FEATURE, 150 ms, GET_FEATURE, again 20 ms later
                            Func<byte, bool> readButton = button =>
                            {
                                if (!d.SetFeature(Bat.B(0x09, 0x86, button))) return false;
                                byte[] f;
                                for (int t = 0; t < 2; t++)
                                {
                                    Bat.Sleep(t == 0 ? 150 : 20);
                                    if (!d.GetFeature(0x09, out f)) return true;
                                    if (f.Length >= 4 && f[1] == 0x86) break;
                                }
                                return true;
                            };
                            Action<int> listen = ms =>
                            {
                                long end = Bat.Now + ms;
                                while (!got && end - Bat.Now > 0)
                                    foreach (HidDev inp in ins)
                                        if (inp.Read(ref r, 50) >= 2 && r[0] == 0x09 && (r[1] & 0x7F) <= 100) { got = true; break; }
                            };
                            if (ins.Count > 0)
                            {
                                Bat.Sleep(200);   // the W1 Service waits 200 ms after 8F
                                bool sent = false;
                                foreach (byte b in new byte[] { 0, 1, 2, 4, 3 }) if (readButton(b)) sent = true;
                                if (sent)
                                {
                                    listen(400);
                                    for (int beat = 0; beat < 4 && !got; beat++)
                                    {
                                        if (!readButton(0)) break;
                                        listen(550);
                                    }
                                }
                            }
                            if (got)
                            {
                                int pct = r[1] & 0x7F;
                                s.Charging = (r[1] & 0x80) != 0;
                                s.Connected = cable || pct > 0 || s.Charging;   // 0 from the dongle: the mouse is off
                                if (s.Connected) s.Percent = pct;
                            }
                        }
                        finally { foreach (HidDev inp in ins) inp.Dispose(); }
                    }
                    ctx.Add(s, 0x093A, pid);   // claimed even when asleep, so Incott never queries a W1
                }
                finally { d.Dispose(); }
            }
        }

        // Incott G23 V2 / G24 (093A:522C dongle, 622C cable): SET_FEATURE 09 [09][89], GET_FEATURE 09 until [09][89][battery]
        static void PollIncott(PollContext ctx)
        {
            foreach (ushort pid in new ushort[] { 0x522C, 0x622C })
            {
                if (ctx.IsClaimed(0x093A, pid)) continue;
                var colls = ctx.Find(0x093A, pid, c => c.Page == 0xFF05 && c.FeatLen >= 9);
                colls.Reverse();
                foreach (HidColl c in colls)
                {
                    byte[] r = null;
                    bool ok = false;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        if (!d.SetFeature(Bat.B(0x09, 0x89))) continue;
                        for (int i = 0; i < 10 && !ok; i++)
                        {
                            Bat.Sleep(50);
                            if (!d.GetFeature(0x09, out r)) break;
                            ok = r.Length >= 4 && r[0] == 0x09 && r[1] == 0x89 && (r[2] | r[3]) != 0;
                        }
                    }
                    if (!ok) continue;
                    int pct = r[2] & 0x7F;
                    if (pct > 100) continue;
                    bool cable = pid == 0x622C;
                    var s = new DeviceStatus { Key = "incott:" + Bat.Hex4(pid), Name = "Incott mouse", Kind = DeviceKind.Mouse };
                    s.Via = cable ? "USB cable" : "2.4 GHz receiver";
                    s.Charging = (r[2] & 0x80) != 0;
                    s.Connected = cable || pct > 0;
                    if (s.Connected) s.Percent = pct;
                    if (!cable) s.GraceSec = 1800;
                    ctx.Add(s, 0x093A, pid);
                    break;
                }
            }
        }

        // Delux M800 Pro (248A receiver 5B2F, cable 5B2E): feature report 0C, incrementing sequence in byte 4
        static byte deluxSeq;

        static void PollDeluxM800(PollContext ctx)
        {
            foreach (ushort pid in new ushort[] { 0x5B2E, 0x5B2F })
            {
                if (ctx.IsClaimed(0x248A, pid)) continue;
                foreach (HidColl c in ctx.Find(0x248A, pid, x => x.Page == 0xFF00 && x.Usage == 0x0001 && x.FeatLen >= 33))
                {
                    bool receiver = pid == 0x5B2F;
                    byte[] r;
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        var req = new byte[c.FeatLen];
                        req[0] = 0x0C; req[1] = 0x01; req[2] = 0x20; req[4] = ++deluxSeq;
                        if (!d.SetFeature(req)) continue;
                        Bat.Sleep(20);
                        if (!d.GetFeature(0x0C, out r) || r.Length < 20) continue;
                        if (r[0] != 0x0C || r[2] != 0x20) continue;
                        if (receiver && r[6] == 0)
                        {
                            req[4] = ++deluxSeq;
                            if (d.SetFeature(req))
                            {
                                Bat.Sleep(20);
                                byte[] r2;
                                if (d.GetFeature(0x0C, out r2) && r2.Length >= 20 && r2[0] == 0x0C && r2[2] == 0x20) r = r2;
                            }
                        }
                    }
                    int pct = r[18];
                    if (pct > 100) continue;
                    byte st = r[19];
                    var s = new DeviceStatus { Key = "delux:m800", Name = "Delux M800 Pro", Kind = DeviceKind.Mouse, Via = receiver ? "2.4 GHz receiver" : "USB cable" };
                    s.Connected = r[6] != 0 || pct > 0;
                    if (st == 2) { s.Percent = 100; s.Charging = true; }
                    else if (st == 1) { s.Charging = true; if (s.Connected && pct > 0) s.Percent = Bat.Clamp100(pct); }
                    else if (s.Connected && pct > 0) s.Percent = Bat.Clamp100(pct);
                    if (!receiver && s.Connected) ctx.Claim(0x248A, 0x5B2F);
                    ctx.Add(s, 0x248A, pid);
                    break;
                }
            }
        }

        public static void Poll(PollContext ctx)
        {
            PollMotospeed(ctx);
            PollXenta(ctx);
            PollDeluxM800(ctx);
            PollAjazz(ctx);
            PollAjazzAjSeries(ctx);
            PollAjazzAj139(ctx);
            PollWraithW1(ctx);   // before Incott: same ids, different commands
            PollIncott(ctx);
        }

        public static void Close()
        {
            foreach (var l in xenta.Values) l.Dev.Dispose();
            xenta.Clear();
        }
    }
}
