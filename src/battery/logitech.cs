// Logitech, ported from Wireless Battery (logitech.cpp, logitech_g435_0adf.*; GPL-3.0-or-later).
//
// HID++ (VID 046D): Unifying / Bolt / Lightspeed / Nano receivers, wired or Bluetooth devices, and G headsets.
// Features are discovered through the root feature, so devices are supported without a per-model table.
//   Message: [report][device index][feature index][function << 4 | sw id][params...]
//   report 0x10 = short (7 bytes), 0x11 = long (20 bytes)
//   HID++ 2.0 error: [.., 0xFF, feature, fn|sw, code]   HID++ 1.0 error: [.., 0x8F, sub id, address, code]
// Battery features: 0x1004 unified battery, 0x1000 battery level, 0x1001 battery voltage, 0x1F20 ADC
// measurement (headsets). HID++ 1.0 devices: registers 0x0D and 0x07.
// References: Logitech HID++ 2.0 specification drafts, github.com/pwr-Solaar/Solaar, github.com/Sapd/HeadsetControl.
//
// G435 on the newer 046D:0ADF dongle: CRC-16 framed vendor reports carrying "PA" commands to the headset's radio
// SoC; the battery is read with the OTA GET_BATTERY command (millivolts). github.com/portyporty/g435-battery-percentage.
using System;
using System.Collections.Generic;
using System.Text;

namespace KomorebiTray
{
    static class LogitechDriver
    {
        const byte SwId = 0x0D;   // our software id; replies to other apps (G HUB, Options+) carry theirs

        // LIGHTSPEED dongles that speak only HID++ 2.0 and relay one paired device on index 1 (PRO X3 SUPERSTRIKE)
        static bool IsPassthroughDongle(ushort pid) { return pid == 0xC54F; }

        class Hidpp : IDisposable
        {
            readonly HidDev s = new HidDev(), l = new HidDev();
            bool hasShort, hasLong;

            public bool Open(HidColl shortC, HidColl longC)
            {
                if (shortC != null) hasShort = s.Open(shortC);
                if (longC != null) hasLong = l.Open(longC);
                return hasShort || hasLong;
            }

            // 1 = reply (params in p), 0 = error reply (code in err), -1 = no answer
            public int Request(byte idx, byte b2, byte b3, byte[] prms, out byte[] p, out byte err, bool forceShort = false, int timeoutMs = 500)
            {
                p = new byte[0];
                err = 0;
                bool useLong = hasLong && !forceShort;
                if (!useLong && !hasShort) return -1;
                var msg = new byte[useLong ? 20 : 7];
                msg[0] = (byte)(useLong ? 0x11 : 0x10);
                msg[1] = idx; msg[2] = b2; msg[3] = b3;
                for (int i = 0; prms != null && i < prms.Length && 4 + i < msg.Length; i++) msg[4 + i] = prms[i];
                if (!(useLong ? l : s).Write(msg)) return -1;
                long end = Bat.Now + timeoutMs;
                byte[] r = null;
                while (end - Bat.Now > 0)
                {
                    foreach (HidDev d in new[] { l, s })
                    {
                        if (!d.Ok || d.Read(ref r, 15) < 7) continue;
                        if ((r[0] != 0x10 && r[0] != 0x11) || r[1] != idx) continue;
                        if (r[2] == b2 && r[3] == b3)
                        {
                            p = new byte[r.Length - 4];
                            Array.Copy(r, 4, p, 0, p.Length);
                            return 1;
                        }
                        if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == b2 && r[4] == b3)
                        {
                            err = r[5];
                            return 0;
                        }
                    }
                }
                return -1;
            }

            // HID++ 2.0 call: feature index + function number
            public bool Call(byte idx, byte feature, int fn, byte[] prms, out byte[] p)
            {
                byte err;
                return Request(idx, feature, (byte)((fn << 4) | SwId), prms, out p, out err) == 1;
            }

            public byte FeatureIndex(byte idx, ushort featureId)
            {
                byte[] p;
                if (Call(idx, 0x00, 0, Bat.B(featureId >> 8, featureId & 0xFF), out p) && p.Length > 0) return p[0];
                return 0;
            }

            public void Dispose() { s.Dispose(); l.Dispose(); }
        }

        // Voltage -> % for a typical Li-ion cell (mice / keyboards)
        static int LiIonPercent(int mv)
        {
            return Bat.Interpolate(mv, new[] { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 },
                new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 });
        }

        // Voltage -> % for G headsets (calibration from HeadsetControl)
        static int HeadsetPercent(ushort pid, int mv)
        {
            switch (pid)
            {
                case 0x0A66: return Bat.Interpolate(mv, new[] { 4200, 3850, 3790, 3750, 3680, 3330 }, new[] { 100, 50, 30, 20, 5, 0 });
                case 0x0AC4: return Bat.Interpolate(mv, new[] { 4175, 3817, 3766, 3730, 3664, 3310 }, new[] { 100, 50, 30, 20, 5, 0 });
                case 0x0A5C: case 0x0A89: case 0x0A5B: case 0x0A87: case 0x0AB5: case 0x0AFE:
                    return Bat.Interpolate(mv, new[] { 4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150 }, new[] { 100, 80, 60, 40, 20, 10, 5, 0 });
                case 0x0AA7: case 0x0AAA: case 0x0ABA: case 0x0AFB:
                    return Bat.Interpolate(mv, new[] { 4150, 3830, 3780, 3740, 3670, 3320 }, new[] { 100, 50, 30, 20, 5, 0 });
                default: return Bat.Interpolate(mv, new[] { 4100, 3850, 3780, 3730, 3650, 3300 }, new[] { 100, 50, 30, 20, 5, 0 });
            }
        }

        static DeviceKind KindFromType(byte t)
        {
            switch (t)
            {
                case 0x00: case 0x02: return DeviceKind.Keyboard;
                case 0x03: case 0x04: case 0x05: return DeviceKind.Mouse;
                case 0x08: return DeviceKind.Headset;
                case 0x0B: case 0x0C: return DeviceKind.Gamepad;
                default: return DeviceKind.Other;
            }
        }

        // Fills battery fields from whichever battery feature the device has. Returns false if none.
        static bool ReadBattery20(Hidpp h, byte idx, ushort pid, DeviceStatus s)
        {
            byte[] p;
            byte f;
            if ((f = h.FeatureIndex(idx, 0x1004)) != 0)   // UNIFIED_BATTERY: get_status (fn 1)
            {
                if (h.Call(idx, f, 1, null, out p) && p.Length >= 3)
                {
                    int soc = p[0], level = p[1], st = p[2];
                    if (soc > 0 && soc <= 100) s.Percent = soc;
                    else if (level != 0)
                    {
                        s.Percent = (level & 8) != 0 ? 100 : (level & 4) != 0 ? 60 : (level & 2) != 0 ? 20 : 5;
                        s.LevelText = (level & 8) != 0 ? "Full" : (level & 4) != 0 ? "Good" : (level & 2) != 0 ? "Low" : "Critical";
                    }
                    s.Charging = st == 1 || st == 2 || st == 3;
                    return true;
                }
            }
            if ((f = h.FeatureIndex(idx, 0x1000)) != 0)   // BATTERY_STATUS: GetBatteryLevelStatus (fn 0)
            {
                if (h.Call(idx, f, 0, null, out p) && p.Length >= 3)
                {
                    int st = p[2];
                    s.Charging = st >= 1 && st <= 4;
                    s.Percent = p[0] <= 100 ? p[0] : -1;
                    if (s.Percent == 0 && st == 3) s.Percent = 100;
                    return true;
                }
            }
            if ((f = h.FeatureIndex(idx, 0x1001)) != 0)   // BATTERY_VOLTAGE: GetBatteryInfo (fn 0)
            {
                if (h.Call(idx, f, 0, null, out p) && p.Length >= 3)
                {
                    s.Millivolts = (p[0] << 8) | p[1];
                    s.Charging = (p[2] & 0x80) != 0;
                    if (s.Millivolts != 0) s.Percent = LiIonPercent(s.Millivolts);
                    return true;
                }
            }
            if ((f = h.FeatureIndex(idx, 0x1F20)) != 0)   // ADC_MEASUREMENT (headsets)
            {
                if (h.Call(idx, f, 0, null, out p) && p.Length >= 3)
                {
                    s.Millivolts = (p[0] << 8) | p[1];
                    s.Charging = p[2] == 0x03;
                    if (s.Millivolts != 0) s.Percent = HeadsetPercent(pid, s.Millivolts);
                    return true;
                }
            }
            return false;
        }

        static void ReadNameAndType20(Hidpp h, byte idx, DeviceStatus s)
        {
            byte f = h.FeatureIndex(idx, 0x0005);   // DEVICE_NAME
            if (f == 0) return;
            byte[] p;
            if (h.Call(idx, f, 0, null, out p) && p.Length > 0)
            {
                int len = p[0];
                var name = new StringBuilder();
                while (name.Length < len)
                {
                    if (!h.Call(idx, f, 1, Bat.B(name.Length), out p) || p.Length == 0) break;
                    int before = name.Length;
                    foreach (byte ch in p)
                    {
                        if (name.Length >= len || ch == 0) break;
                        name.Append((char)ch);
                    }
                    if (name.Length == before) break;
                }
                if (name.Length > 0)
                {
                    string w = name.ToString();
                    s.Name = w.StartsWith("Logitech") || w.StartsWith("G ") || w.StartsWith("MX") ? w : "Logitech " + w;
                }
            }
            if (h.Call(idx, f, 2, null, out p) && p.Length > 0) s.Kind = KindFromType(p[0]);
        }

        // DEVICE_INFORMATION (0x0003): the unit id identifies the physical device, so a mouse keeps one entry
        // whether it's reached through its receiver or over its charging cable
        static string ReadUnitId20(Hidpp h, byte idx)
        {
            byte f = h.FeatureIndex(idx, 0x0003);
            if (f == 0) return "";
            byte[] p;
            if (!h.Call(idx, f, 0, null, out p) || p.Length < 5) return "";
            if (p[1] == 0 && p[2] == 0 && p[3] == 0 && p[4] == 0) return "";
            return Bat.Hex4((p[1] << 8) | p[2]) + Bat.Hex4((p[3] << 8) | p[4]);
        }

        // HID++ 1.0 devices: register 0x0D (battery %) or 0x07 (battery level 1..7)
        static bool ReadBattery10(Hidpp h, byte idx, DeviceStatus s)
        {
            byte[] p;
            byte err;
            if (h.Request(idx, 0x81, 0x0D, null, out p, out err, true) == 1 && p.Length >= 3 && p[0] != 0 && p[0] <= 100)
            {
                s.Percent = p[0];
                s.Charging = p[2] >= 0x21 && p[2] <= 0x26;
                return true;
            }
            if (h.Request(idx, 0x81, 0x07, null, out p, out err, true) == 1 && p.Length >= 2 && p[0] != 0)
            {
                int[] pct = { 0, 5, 10, 20, 35, 50, 75, 100 };
                s.Percent = pct[p[0] > 7 ? 7 : p[0]];
                s.LevelText = p[0] >= 7 ? "Full" : p[0] >= 5 ? "Good" : p[0] >= 3 ? "Low" : "Critical";
                s.Charging = p[1] >= 0x21 && p[1] <= 0x26;
                return true;
            }
            return false;
        }

        // The physical device a collection belongs to: collections of one interface differ only in "&colNN" and
        // the last "&NNNN" of the instance part, so two identical receivers are polled separately
        static string PhysicalKey(HidColl c)
        {
            string p = c.Path.ToLowerInvariant();
            int a = p.IndexOf('#');
            int b = a < 0 ? -1 : p.IndexOf('#', a + 1);
            int e = b < 0 ? -1 : p.IndexOf('#', b + 1);
            if (e < 0) return "";
            string hw = p.Substring(a + 1, b - a - 1), inst = p.Substring(b + 1, e - b - 1);
            int col = hw.IndexOf("&col");
            if (col >= 0) hw = hw.Remove(col, Math.Min(6, hw.Length - col));
            int amp = inst.LastIndexOf('&');
            if (amp >= 0) inst = inst.Substring(0, amp);
            return hw + "#" + inst;
        }

        static bool PollDevice(PollContext ctx, ushort pid, List<HidColl> shorts, List<HidColl> longs, string suffix)
        {
            if (shorts.Count == 0 && longs.Count == 0) return false;
            using (var h = new Hidpp())
            {
                if (!h.Open(shorts.Count == 0 ? null : shorts[0], longs.Count == 0 ? null : longs[0])) return false;
                string product = Hid.Product(longs.Count == 0 ? shorts[0] : longs[0]);
                bool isReceiver = product.Contains("Receiver");

                // A device answering a HID++ 2.0 ping at index 0xFF is connected directly (USB cable, Bluetooth, or a
                // receiver-less headset); otherwise it is a receiver with paired devices. A Bluetooth HID path (HOGP
                // 1812, classic HID 1124) is always a direct link
                byte[] p;
                byte err;
                byte[] indices;
                string path = (longs.Count == 0 ? shorts[0] : longs[0]).Path.ToLowerInvariant();
                bool bluetooth = path.Contains("{00001812-") || path.Contains("{00001124-");
                if (IsPassthroughDongle(pid)) indices = new byte[] { 1 };
                else if (bluetooth || (!isReceiver && h.Request(0xFF, 0x00, 0x10 | SwId, Bat.B(0, 0, 0x5A), out p, out err, false, 400) == 1 && p.Length >= 3 && p[0] >= 2))
                    indices = new byte[] { 0xFF };
                else indices = new byte[] { 1, 2, 3, 4, 5, 6 };

                bool any = false;
                foreach (byte idx in indices)
                {
                    var s = new DeviceStatus();
                    s.Key = "logi:" + Bat.Hex4(pid) + suffix + ":" + idx;
                    s.Name = idx == 0xFF && product.Length > 0 ? product : "Logitech wireless device";
                    s.Via = idx == 0xFF ? "USB / Bluetooth" : "Logitech receiver";
                    s.GraceSec = idx == 0xFF ? 0 : 1800;   // receiver devices drop off the link while sleeping
                    s.Kind = Bat.GuessKind(s.Name);
                    int r = h.Request(idx, 0x00, 0x10 | SwId, Bat.B(0, 0, 0x5A), out p, out err, false, 400);
                    bool found = false;
                    if (r == 1 && p.Length >= 3 && p[0] >= 2)
                    {
                        ReadNameAndType20(h, idx, s);
                        found = ReadBattery20(h, idx, pid, s);
                        string unit = found ? ReadUnitId20(h, idx) : "";
                        if (unit.Length > 0)
                        {
                            s.Key = "logi:unit:" + unit;
                            if (idx == 0xFF) s.GraceSec = 60;   // unplugging the cable hands the device back to its receiver
                        }
                        if (found && s.Kind == DeviceKind.Other && longs.Count > 0 && longs[0].Page == 0xFF43) s.Kind = DeviceKind.Headset;
                    }
                    else if (r == 0 && err == 0x01) found = ReadBattery10(h, idx, s);   // "invalid sub id": a HID++ 1.0 device
                    if (found)
                    {
                        if (s.Kind == DeviceKind.Other) s.Kind = Bat.GuessKind(s.Name);
                        ctx.Out.Add(s);
                        any = true;
                    }
                }
                return any;
            }
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x046D))
            {
                ushort vid = vp[0], pid = vp[1];
                var allShorts = ctx.Find(vid, pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.OutLen == 7);
                var allLongs = ctx.Find(vid, pid, c => ((c.Page == 0xFF00 && c.Usage == 0x0002) || (c.Page == 0xFF43 && c.Usage == 0x0202)) && c.OutLen == 20);
                var groups = new List<string>();
                foreach (var list in new[] { allShorts, allLongs })
                    foreach (HidColl c in list)
                    {
                        string k = PhysicalKey(c);
                        if (!groups.Contains(k)) groups.Add(k);
                    }
                bool any = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    var shorts = new List<HidColl>();
                    var longs = new List<HidColl>();
                    foreach (HidColl c in allShorts) if (PhysicalKey(c) == groups[g]) shorts.Add(c);
                    foreach (HidColl c in allLongs) if (PhysicalKey(c) == groups[g]) longs.Add(c);
                    string suffix = g > 0 ? "#" + (g + 1) : "";   // a second identical receiver gets "#2", ...
                    if (PollDevice(ctx, pid, shorts, longs, suffix)) any = true;
                }
                if (any) ctx.Claim(vid, pid);
            }
        }

        // ---- G435 on 046D:0ADF

        static ushort Crc16(byte[] data)
        {
            ushort crc = 0;
            foreach (byte d in data)
            {
                crc ^= (ushort)(d << 8);
                for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        // 64-byte frame: <cmd> <body length> <crc lo> <crc hi> <body...>, zero-padded; the CRC covers the frame with the CRC field zeroed
        static byte[] Frame(byte cmd, byte[] body)
        {
            var f = new byte[64];
            f[0] = cmd;
            f[1] = (byte)body.Length;
            for (int i = 0; i < body.Length && i + 4 < f.Length; i++) f[4 + i] = body[i];
            ushort crc = Crc16(f);
            f[2] = (byte)(crc & 0xFF);
            f[3] = (byte)(crc >> 8);
            return f;
        }

        static int ParseMillivolts(byte[] d)
        {
            for (int i = 0; i + 8 <= d.Length; i++)
                if (d[i] == 0x00 && d[i + 1] == 0x07 && d[i + 2] == 0x00 && d[i + 3] == 0x06 && d[i + 5] == 0x00)
                {
                    int mv = d[i + 6] | (d[i + 7] << 8);
                    if (mv >= 2500 && mv <= 4500) return mv;
                }
            return -1;
        }

        static int G435Percent(int mv)
        {
            int[] v = { 4200, 4150, 4100, 4050, 4000, 3950, 3900, 3850, 3800, 3750, 3700, 3650, 3600, 3550, 3500, 3400 };
            int[] p = { 100, 95, 90, 85, 80, 75, 70, 62, 55, 45, 35, 25, 15, 10, 5, 0 };
            if (mv >= v[0]) return 100;
            for (int i = 1; i < 16; i++)
                if (mv >= v[i]) return p[i] + ((p[i - 1] - p[i]) * (mv - v[i]) + (v[i - 1] - v[i]) / 2) / (v[i - 1] - v[i]);
            return 0;
        }

        // Sends one frame (report id 0) and collects the input reports that arrive within waitMs; with wantMv,
        // returns as soon as a battery reply is seen
        static bool Transact(HidDev d, byte cmd, byte[] body, int waitMs, ref int mv, bool wantMv)
        {
            byte[] f = Frame(cmd, body);
            var output = new byte[65];
            Array.Copy(f, 0, output, 1, 64);
            d.Flush();
            if (!d.Write(output)) return false;
            long end = Bat.Now + waitMs;
            byte[] r = null;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0) return true;
                if (d.Read(ref r, left) < 0) continue;
                if (wantMv && (mv = ParseMillivolts(r)) >= 0) return true;
            }
        }

        static bool T(HidDev d, byte cmd, byte[] body, int waitMs) { int x = -1; return Transact(d, cmd, body, waitMs, ref x, false); }

        static readonly byte[] SessionInit = Bat.B(0x50, 0x41, 0x06, 0x01, 0x0D, 0x00, 0x25, 0x34, 0x12, 0x5A, 0x5A, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x00);

        static int ReadMillivolts(HidDev d)
        {
            if (!T(d, 0x84, Bat.B(0x01), 50)) return -1;
            try
            {
                T(d, 0x85, Bat.B(0x01), 50);
                T(d, 0x80, SessionInit, 450);
                byte seq = 2;
                Func<byte, byte, byte[], byte[]> pa = (cat, len, payload) =>
                {
                    var cmd = new List<byte> { 0x50, 0x41, cat, seq, len };
                    cmd.AddRange(payload);
                    seq = (byte)(seq % 127 + 1);
                    return cmd.ToArray();
                };
                T(d, 0x80, pa(0x0E, 1, Bat.B(0xE0)), 120);         // REMOTE_GET_MODE
                T(d, 0x80, pa(0x0E, 2, Bat.B(0xE1, 0x01)), 120);   // REMOTE_SET_MODE 01: route to the headset
                T(d, 0x80, pa(0x0E, 1, Bat.B(0xE0)), 120);
                int mv = -1;
                Transact(d, 0x80, pa(0x06, 1, Bat.B(0x00, 0x31)), 250, ref mv, true);   // OTA GET_BATTERY
                return mv;
            }
            finally { T(d, 0x85, Bat.B(0x02), 50); }   // always close the session
        }

        public static void PollG435(PollContext ctx)
        {
            const ushort vid = 0x046D, pid = 0x0ADF;
            if (ctx.IsClaimed(vid, pid)) return;
            var colls = ctx.Find(vid, pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.InLen == 65 && c.OutLen == 65);
            if (colls.Count == 0) return;
            using (var d = new HidDev())
            {
                if (!d.Open(colls[0])) return;
                var s = new DeviceStatus { Key = "g435:" + Bat.Hex4(pid), Name = "Logitech G435", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
                int mv = ReadMillivolts(d);
                if (mv < 0) { s.Connected = false; ctx.Add(s, vid, pid); return; }   // headset off or out of range
                s.Millivolts = mv;
                s.Percent = G435Percent(mv);
                ctx.Add(s, vid, pid);
            }
        }
    }
}
