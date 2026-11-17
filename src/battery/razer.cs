// Razer, ported from Wireless Battery (razer.cpp, razer_headset.cpp; GPL-3.0-or-later).
//
// Wireless mice, keyboards and dongles (VID 1532): 90-byte feature report (report id 0)
//   [0] status [1] transaction id [2..3] remaining packets [4] protocol type [5] data size
//   [6] command class [7] command id [8..87] arguments [88] crc (xor of 2..87) [89] reserved
// Battery: class 07 id 80 -> args[1] = 0..255. Charging: class 07 id 84 -> args[1] = 1.
// Reply status: 01 busy, 02 ok, 03 failed, 04 timeout, 05 not supported. Reference: OpenRazer.
//
// Headsets with their own protocols: BlackShark V3 / V3 Pro (OpenRazer PR #2794, omarchy-blackshark-v3),
// V2 HyperSpeed (razer-blackshark-v2-hyperspeed-webhid), V2 Pro 2023 "PA" envelope (OpenRazer PR #2862,
// HaloBattery), Barracuda X 2022 YS-Tech / Airoha bridge (razer-barracuda-x-battery, barracuda-x-battery),
// BlackShark V3 X (OpenRazer PR #2899).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class RazerDriver
    {
        static byte KeyboardBatteryTid(ushort pid)
        {
            switch (pid)
            {
                case 0x025C: case 0x0271: case 0x0290: case 0x0296:
                case 0x02BA: case 0x02D2: case 0x02D5: return 0x9F;   // wireless keyboards
                case 0x0258: case 0x0292: case 0x0298: case 0x02B9:
                case 0x02CD: case 0x02D4: case 0x02D7: return 0x1F;   // wired keyboards / Joro
                case 0x025A: return 0x3F;                             // BlackWidow V3 Pro wired
                default: return 0;
            }
        }

        enum Rz { Ok, Offline, Unsupported, NoReply }

        static Rz Command(HidDev d, byte tid, byte cls, byte id, byte size, out byte[] args)
        {
            args = null;
            var rep = new byte[91];   // [0] = report id 0, then the 90-byte report
            rep[1 + 1] = tid;
            rep[1 + 5] = size;
            rep[1 + 6] = cls;
            rep[1 + 7] = id;
            byte crc = 0;
            for (int i = 2; i < 88; i++) crc ^= rep[1 + i];
            rep[1 + 88] = crc;
            bool send = true;
            Rz res = Rz.NoReply;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (send && !d.SetFeature(rep)) return Rz.NoReply;
                // Wireless keyboards need about 5 ms (OpenRazer); a long wait lets Synapse replace the reply
                Bat.Sleep(KeyboardBatteryTid(d.Coll.Pid) != 0 ? 5 : (attempt == 0 ? 15 : 40));
                send = true;
                res = Rz.NoReply;
                byte[] resp;
                if (!d.GetFeature(0, out resp) || resp.Length < 91) continue;
                byte replyCrc = 0;
                for (int i = 2; i < 88; i++) replyCrc ^= resp[1 + i];
                // Synapse shares the feature-report mailbox: a different reply needs a fresh request
                if (resp[1 + 1] != tid || resp[1 + 2] != 0 || resp[1 + 3] != 0 || resp[1 + 4] != 0 || resp[1 + 5] != size ||
                    resp[1 + 6] != cls || resp[1 + 7] != id || resp[1 + 88] != replyCrc) continue;
                byte status = resp[1];
                if (status == 0x01) { send = false; continue; }   // busy: wait for this request to finish
                if (status == 0x02) { args = new byte[80]; Array.Copy(resp, 1 + 8, args, 0, 80); return Rz.Ok; }
                if (status == 0x04) { res = Rz.Offline; continue; }   // retry a transient receiver timeout
                if (status == 0x05 || status == 0x03) return Rz.Unsupported;
            }
            return res;
        }

        static string CleanName(string n)
        {
            foreach (string suffix in new[] { " Dongle", " (Wired)", " (Wireless)", " Wireless Receiver", " Receiver", " HyperSpeed Dongle" })
                if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - suffix.Length);
            if (n.Length == 0) n = "Razer device";
            return n;
        }

        // Exact keyboard/keypad/laptop and mouse/receiver ids from OpenRazer
        static readonly ushort[] KeyboardPids =
        {
            0x010D, 0x010E, 0x010F, 0x0111, 0x0113, 0x0118, 0x011A, 0x011B, 0x011C, 0x0201,
            0x0202, 0x0203, 0x0204, 0x0205, 0x0207, 0x0208, 0x0209, 0x020F, 0x0210, 0x0211,
            0x0214, 0x0216, 0x0217, 0x021A, 0x021E, 0x021F, 0x0220, 0x0221, 0x0224, 0x0225,
            0x0226, 0x0227, 0x0228, 0x022A, 0x022B, 0x022C, 0x022D, 0x022F, 0x0232, 0x0233,
            0x0234, 0x0235, 0x0237, 0x0239, 0x023A, 0x023B, 0x023F, 0x0240, 0x0241, 0x0243,
            0x0244, 0x0245, 0x0246, 0x024A, 0x024B, 0x024C, 0x024D, 0x024E, 0x0252, 0x0253,
            0x0255, 0x0256, 0x0257, 0x0258, 0x0259, 0x025A, 0x025C, 0x025D, 0x025E, 0x0266,
            0x0268, 0x0269, 0x026A, 0x026B, 0x026C, 0x026D, 0x026E, 0x026F, 0x0270, 0x0271,
            0x0276, 0x0279, 0x027A, 0x0282, 0x0287, 0x028A, 0x028B, 0x028C, 0x028D, 0x028F,
            0x0290, 0x0292, 0x0293, 0x0294, 0x0295, 0x0296, 0x0298, 0x029D, 0x029E, 0x029F,
            0x02A0, 0x02A1, 0x02A2, 0x02A3, 0x02A5, 0x02A6, 0x02A7, 0x02B0, 0x02B6, 0x02B8,
            0x02B9, 0x02BA, 0x02C5, 0x02C6, 0x02C7, 0x02CD, 0x02CF, 0x02D2, 0x02D4, 0x02D5,
            0x02D7, 0x02E0, 0x02E1, 0x02EA, 0x0A24,
        };

        static readonly ushort[] MousePids =
        {
            0x0013, 0x0015, 0x0016, 0x001F, 0x0020, 0x0024, 0x0025, 0x0029, 0x002E, 0x002F,
            0x0032, 0x0034, 0x0036, 0x0037, 0x0038, 0x0039, 0x003E, 0x003F, 0x0040, 0x0041,
            0x0042, 0x0043, 0x0044, 0x0045, 0x0046, 0x0048, 0x004C, 0x004F, 0x0050, 0x0053,
            0x0054, 0x0059, 0x005A, 0x005B, 0x005C, 0x005E, 0x005F, 0x0060, 0x0062, 0x0064,
            0x0065, 0x0067, 0x006A, 0x006B, 0x006C, 0x006E, 0x006F, 0x0070, 0x0071, 0x0072,
            0x0073, 0x0077, 0x0078, 0x007A, 0x007B, 0x007C, 0x007D, 0x0080, 0x0083, 0x0084,
            0x0085, 0x0086, 0x0088, 0x008A, 0x008C, 0x008D, 0x008F, 0x0090, 0x0091, 0x0094,
            0x0095, 0x0096, 0x0098, 0x0099, 0x009A, 0x009C, 0x009E, 0x009F, 0x00A1, 0x00A3,
            0x00A5, 0x00A6, 0x00A7, 0x00A8, 0x00AA, 0x00AB, 0x00AF, 0x00B0, 0x00B2, 0x00B3,
            0x00B4, 0x00B6, 0x00B7, 0x00B8, 0x00B9, 0x00BE, 0x00BF, 0x00C0, 0x00C1, 0x00C2,
            0x00C3, 0x00C4, 0x00C5, 0x00C7, 0x00C8, 0x00CB, 0x00CC, 0x00CD, 0x00D0, 0x00D1,
            0x00D3, 0x00D4, 0x00D6, 0x00D7, 0x00DA, 0x00DB, 0x00DE, 0x00DF, 0x00E0, 0x00E5,
            0x00E6,
        };

        static DeviceKind Kind(List<HidColl> colls, ushort vid, ushort pid)
        {
            // Both keyboards and mice can expose keyboard AND mouse collections for macros / buttons
            if (vid == 0x1532)
            {
                if (Array.IndexOf(KeyboardPids, pid) >= 0) return DeviceKind.Keyboard;
                if (Array.IndexOf(MousePids, pid) >= 0) return DeviceKind.Mouse;
            }
            return Bat.KindFromCollections(colls, vid, pid);
        }

        public static void Poll(PollContext ctx)
        {
            var seenNames = new List<string>();
            foreach (ushort[] vp in ctx.Products(0x1532))
            {
                ushort vid = vp[0], pid = vp[1];
                foreach (HidColl c in ctx.Find(vid, pid, x => x.FeatLen == 91))
                {
                    using (var d = new HidDev())
                    {
                        if (!d.OpenFeatureOnly(c)) continue;
                        Rz res = Rz.NoReply;
                        byte tid = 0;
                        byte[] args = null;
                        bool sawOffline = false;
                        byte keyboardTid = KeyboardBatteryTid(pid);
                        byte[] tids = keyboardTid != 0 ? new[] { keyboardTid } : Bat.B(0x1F, 0x3F, 0xFF, 0x08, 0x9F);
                        foreach (byte t in tids)
                        {
                            byte[] a;
                            Rz r = Command(d, t, 0x07, 0x80, 0x02, out a);
                            if (r == Rz.Ok) { res = r; tid = t; args = a; break; }
                            sawOffline |= r == Rz.Offline;   // may also mean "wrong transaction id": keep trying
                        }
                        if (res != Rz.Ok && sawOffline) res = Rz.Offline;
                        if (res != Rz.Ok && res != Rz.Offline) continue;

                        var s = new DeviceStatus { Key = "razer:" + Bat.Hex4(pid), Name = CleanName(Hid.Product(c)) };
                        s.Kind = Kind(ctx.Colls, vid, pid);
                        if (s.Kind == DeviceKind.Other) s.Kind = Bat.GuessKind(s.Name);
                        s.Via = "Razer receiver";
                        s.GraceSec = 120;   // retain the last reading briefly when Synapse prevents a valid reply
                        if (res == Rz.Offline) s.Connected = false;
                        else
                        {
                            s.Percent = Bat.Clamp100((args[1] * 100 + 127) / 255);
                            byte[] ch;
                            if (Command(d, tid, 0x07, 0x84, 0x02, out ch) == Rz.Ok) s.Charging = ch[1] == 1;
                        }
                        // A wireless mouse plugged in for charging shows up twice (cable + dongle); keep one
                        bool dup = seenNames.Contains(s.Name);
                        if (!dup) { seenNames.Add(s.Name); ctx.Add(s, vid, pid); }
                        else
                        {
                            ctx.Claim(vid, pid);
                            foreach (DeviceStatus o in ctx.Out) if (o.Name == s.Name && s.Charging) o.Charging = true;
                        }
                        break;
                    }
                }
            }
        }

        // ---- headsets

        const long MinIntervalMs = 5 * 60 * 1000;
        class Cached { public long At; public DeviceStatus Status; }
        static readonly Dictionary<ushort, Cached> cache = new Dictionary<ushort, Cached>();

        static byte[] Packet(byte dir, byte cls)
        {
            var b = new byte[64];
            b[0] = 0x02; b[2] = 0x60; b[6] = 0x04; b[9] = dir; b[10] = cls;
            byte crc = 0;
            for (int i = 0; i < 62; i++) crc ^= b[i];
            b[62] = crc;
            return b;
        }

        static bool Get(HidDev d, byte cls, ref byte[] reply)
        {
            if (!d.Write(Packet(0x80, cls))) return false;
            long end = Bat.Now + 600;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref reply, left) < 15) return false;
                if (reply[0] == 0x02 && reply[2] == 0x60 && reply[10] == cls) return true;
            }
            return false;
        }

        static string V3Name(ushort pid) { return pid == 0x0576 || pid == 0x0577 ? "Razer BlackShark V3 Pro" : "Razer BlackShark V3"; }

        // BlackShark V2 HyperSpeed (dongle 1532:0565): MediaTek vendor HID on FF14 (FF00 fallback). Only GET 21 / 2A
        static byte hsSeq;
        static byte NextHsSeq() { return (byte)(0x60 | ((hsSeq++) & 0x1F)); }

        static byte[] HsQueryFrame(byte cmd, byte seq)
        {
            var b = new byte[64];
            b[0] = 0x02; b[2] = seq; b[6] = 0x04; b[9] = 0x80; b[10] = cmd;
            byte checksum = 0;
            for (int i = 0; i < 62; i++) checksum ^= b[i];
            b[62] = checksum;
            return b;
        }

        static bool ParseHsReply(byte[] r, byte cmd, byte seq, out int value)
        {
            value = -1;
            if (r.Length < 64 || r[0] != 0x02 || r[2] != seq || r[10] != cmd || r[12] < 1) return false;
            byte checksum = 0;
            for (int i = 1; i < 62; i++) checksum ^= r[i];
            if (checksum != r[62]) return false;
            value = r[13];
            return true;
        }

        static int HsQuery(HidDev d, byte cmd)
        {
            byte seq = NextHsSeq();
            d.Flush();
            if (!d.Write(HsQueryFrame(cmd, seq))) return -1;
            long end = Bat.Now + 650;
            byte[] r = null;
            while (Bat.Now < end)
            {
                int remaining = (int)(end - Bat.Now);
                if (d.Read(ref r, remaining) < 0) break;
                int value;
                if (ParseHsReply(r, cmd, seq, out value)) return value;
            }
            return -1;
        }

        static void PollBlackSharkV2HyperSpeed(PollContext ctx)
        {
            const ushort pid = 0x0565;   // 2.4 GHz dongle, not the wired 056E variant
            if (ctx.IsClaimed(0x1532, pid)) return;
            var colls = ctx.Find(0x1532, pid, c => (c.Page == 0xFF14 || c.Page == 0xFF00) && c.Usage == 0x01 && c.OutLen >= 64 && c.InLen >= 64);
            // Prefer the specific vendor control interface so audio / telephony endpoints are not touched
            foreach (ushort page in new ushort[] { 0xFF14, 0xFF00 })
                foreach (HidColl c in colls)
                {
                    if (c.Page != page) continue;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        int level = HsQuery(d, 0x21);
                        if (level < 0 || level > 100) continue;
                        int charging = HsQuery(d, 0x2A);
                        ctx.Add(new DeviceStatus
                        {
                            Key = "blackshark:" + Bat.Hex4(pid), Name = "Razer BlackShark V2 HyperSpeed", Kind = DeviceKind.Headset,
                            Via = "2.4 GHz receiver", Percent = level, Charging = charging > 0,
                        }, 0x1532, pid);
                        return;
                    }
                }
        }

        // BlackShark V2 Pro (2023): "PA" envelope
        static byte[] PaFrame(byte totalLen, byte innerLen, byte type, byte cmd, byte flag)
        {
            var b = new byte[64];
            b[0] = 0x02; b[1] = 0x80; b[2] = totalLen; b[5] = (byte)'P'; b[6] = (byte)'A'; b[7] = innerLen; b[9] = type; b[10] = cmd; b[11] = flag;
            return b;
        }

        static bool PaRemote(HidDev d, bool on)
        {
            bool ok = d.Write(PaFrame(0x07, 0x0E, 0x02, 0xE1, (byte)(on ? 1 : 0)));
            Bat.Sleep(35);
            return ok;
        }

        static int PaQuery(HidDev d, byte cmd)
        {
            d.Flush();
            PaRemote(d, true);   // sacrificial: wakes a dozing link
            int value = -1;
            for (int attempt = 0; attempt < 3 && value < 0; attempt++)
            {
                PaRemote(d, true);
                if (!d.Write(PaFrame(0x08, 0x08, 0x03, cmd, 0))) break;
                long end = Bat.Now + 150;
                byte[] r = null;
                while (end - Bat.Now > 0)
                {
                    int left = (int)(end - Bat.Now);
                    if (d.Read(ref r, left) < 16) break;
                    if (r[0] == 0x02 && r[12] == cmd && r[13] == 0x01 && r[14] >= 1) { value = r[15]; break; }
                }
            }
            PaRemote(d, false);
            return value;
        }

        static void PollBlackSharkV2Pro2023(PollContext ctx)
        {
            const ushort pid = 0x0555;
            if (ctx.IsClaimed(0x1532, pid)) return;
            foreach (HidColl c in ctx.Find(0x1532, pid, x => x.Page == 0xFF00 && x.OutLen >= 64 && x.InLen >= 16))
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    int level = PaQuery(d, 0x21);
                    if (level < 0 || level > 100) continue;
                    int charging = PaQuery(d, 0x2A);
                    ctx.Add(new DeviceStatus
                    {
                        Key = "blackshark:" + Bat.Hex4(pid), Name = "Razer BlackShark V2 Pro (2023)", Kind = DeviceKind.Headset,
                        Via = "2.4 GHz receiver", Percent = level, Charging = charging > 0,
                    }, 0x1532, pid);
                    return;
                }
        }

        // Barracuda X (2022), YS-Tech receiver 1532:0550
        static ushort Crc16Xmodem(byte[] data)
        {
            ushort crc = 0;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        class BarracudaLink : IDisposable
        {
            readonly HidDev dev = new HidDev();
            readonly List<byte> stream = new List<byte>();
            byte seq;

            public bool Open(HidColl c) { return dev.Open(c); }
            public void Dispose() { dev.Dispose(); }
            public byte Seq { get { return seq; } }

            // Sends one Airoha command [50 41][group][seq][body..]
            public bool Command(byte group, byte[] body)
            {
                seq = (byte)(seq % 127 + 1);
                var payload = new List<byte> { 0x50, 0x41, group, seq };
                payload.AddRange(body);
                var f = new byte[64];
                f[0] = 0x02; f[1] = 0x80; f[2] = (byte)payload.Count;
                for (int i = 0; i < payload.Count; i++) f[5 + i] = payload[i];
                ushort crc = Crc16Xmodem(f);
                f[3] = (byte)(crc & 0xFF);
                f[4] = (byte)(crc >> 8);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (attempt > 0) Bat.Sleep(50);
                    byte[] status;
                    if (!dev.Write(f) || !dev.GetInputReport(0x02, out status) || status.Length < 2) return false;
                    if (status[1] == 'O') return true;
                    if (status[1] != 'F') return false;   // anything but "taken" / "busy": the bridge refused it
                }
                return false;
            }

            // Waits for the first reply frame accepted by match; unrelated frames are skipped
            public bool Await(Func<byte[], bool> match, int timeoutMs, out byte[] frame)
            {
                frame = null;
                long end = Bat.Now + timeoutMs;
                for (;;)
                {
                    while (stream.Count >= 2)
                    {
                        int start = -1;
                        for (int i = 0; i + 1 < stream.Count; i++) if (stream[i] == 0x50 && stream[i + 1] == 0x49) { start = i; break; }
                        if (start < 0) { stream.Clear(); break; }
                        stream.RemoveRange(0, start);
                        if (stream.Count < 10) break;
                        int len = 10 + (stream[8] | (stream[9] << 8));
                        if (stream.Count < len) break;
                        frame = stream.GetRange(0, len).ToArray();
                        stream.RemoveRange(0, len);
                        if (match(frame)) return true;
                    }
                    int left = (int)(end - Bat.Now);
                    byte[] r = null;
                    if (left <= 0 || dev.Read(ref r, left) < 2) return false;
                    if (r[0] == 0x02 && r[1] + 2 <= r.Length) for (int i = 0; i < r[1]; i++) stream.Add(r[2 + i]);
                }
            }

            public bool RemoteMode(bool on)
            {
                byte[] f;
                if (!Command(0x0E, Bat.B(0x02, 0xE1, on ? 1 : 0))) return false;
                byte s = seq;
                return Await(r => r.Length >= 13 && r[10] == 0x0E && r[11] == (byte)(0x80 | s), 800, out f) && f[12] == 0;
            }

            public int Get(byte cmd)
            {
                byte[] f;
                if (!Command(0x08, Bat.B(0x03, cmd, 0x00, 0x00))) return -1;
                bool ok = Await(r => r.Length >= 13 && r[2] == 0x08 && r[10] == cmd && r[11] == 0x01, 1000, out f);
                return ok && f[12] >= 1 && f.Length >= 14 ? f[13] : -1;
            }
        }

        static void PollBarracudaX(PollContext ctx)
        {
            const ushort pid = 0x0550;
            if (ctx.IsClaimed(0x1532, pid)) return;
            foreach (HidColl c in ctx.Find(0x1532, pid, x => x.Page == 0xFF00 && x.Usage == 0x0001 && x.OutLen >= 64 && x.InLen >= 64))
                using (var link = new BarracudaLink())
                {
                    if (!link.Open(c)) continue;
                    if (!link.RemoteMode(true)) continue;   // no connected headset (off, out of range, Bluetooth mode)
                    int level = link.Get(0x21);
                    int cable = level >= 0 ? link.Get(0x2A) : -1;
                    link.RemoteMode(false);
                    if (level < 0 || level > 100) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "barracuda:" + Bat.Hex4(pid), Name = "Razer Barracuda X (2022)", Kind = DeviceKind.Headset,
                        Via = "2.4 GHz receiver", Percent = level, Charging = cable == 1,
                    }, 0x1532, pid);
                    return;
                }
        }

        // BlackShark V3 X (057D dongle, 057C cable): 64-byte feature report 07 on interface 0
        static byte v3xTxid = 0x40;

        static bool V3xGet(HidDev d, byte cmd, out byte value)
        {
            value = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var req = new byte[64];
                req[0] = 0x07; req[2] = ++v3xTxid; req[6] = 0x02; req[7] = 0x07; req[8] = cmd;
                byte crc = 0;
                for (int i = 3; i <= 60; i++) crc ^= req[i];
                req[62] = crc;
                if (!d.SetFeature(req)) return false;
                Bat.Sleep(60);
                byte[] r;
                if (!d.GetFeature(0x07, out r) || r.Length < 63) continue;
                if (r[2] != v3xTxid || r[7] != 0x07 || r[8] != cmd || r[6] < 2) continue;
                if (r[1] != 0x02 && r[1] != 0x01) return false;
                value = r[10];
                return true;
            }
            return false;
        }

        static void PollBlackSharkV3X(PollContext ctx)
        {
            foreach (ushort pid in new ushort[] { 0x057D, 0x057C })
            {
                if (ctx.IsClaimed(0x1532, pid)) continue;
                var colls = ctx.Find(0x1532, pid, x => x.Iface == 0 && x.FeatLen >= 64);
                if (colls.Count == 0) continue;
                Cached cc;
                if (cache.TryGetValue(pid, out cc) && Bat.Now - cc.At < MinIntervalMs) { ctx.Add(cc.Status.Copy(), 0x1532, pid); continue; }
                using (var d = new HidDev())
                {
                    if (!d.OpenFeatureOnly(colls[0])) continue;
                    byte level, charging;
                    if (!V3xGet(d, 0x80, out level) || level > 100) continue;
                    V3xGet(d, 0x84, out charging);
                    var s = new DeviceStatus
                    {
                        Key = "blackshark:" + Bat.Hex4(pid), Name = "Razer BlackShark V3 X", Kind = DeviceKind.Headset,
                        Via = pid == 0x057D ? "2.4 GHz receiver" : "USB cable", Percent = level, Charging = charging == 1,
                    };
                    cache[pid] = new Cached { At = Bat.Now, Status = s.Copy() };
                    ctx.Add(s, 0x1532, pid);
                }
            }
        }

        public static void PollHeadsets(PollContext ctx)
        {
            PollBlackSharkV2HyperSpeed(ctx);
            PollBarracudaX(ctx);
            PollBlackSharkV2Pro2023(ctx);
            PollBlackSharkV3X(ctx);
            // BlackShark V3 / V3 Pro: asked at most every 5 minutes (it drops its link when queried often)
            foreach (ushort pid in new ushort[] { 0x0576, 0x0577, 0x0579, 0x057A })
            {
                if (ctx.IsClaimed(0x1532, pid)) continue;
                var colls = ctx.Find(0x1532, pid, x => x.Iface == 5 && x.OutLen >= 64 && x.InLen >= 15);
                if (colls.Count == 0) continue;
                Cached cc;
                if (cache.TryGetValue(pid, out cc) && Bat.Now - cc.At < MinIntervalMs) { ctx.Add(cc.Status.Copy(), 0x1532, pid); continue; }
                using (var d = new HidDev())
                {
                    if (!d.Open(colls[0])) continue;
                    // Handshake the headset expects before reads (init, charging state on both links)
                    d.Write(Packet(0x00, 0x02));
                    d.Write(Packet(0x00, 0x2A));
                    d.Write(Packet(0x80, 0x2A));
                    Bat.Sleep(250);
                    d.Flush();
                    byte[] r = null;
                    if (!Get(d, 0x21, ref r) || r[13] > 100) continue;
                    var s = new DeviceStatus
                    {
                        Key = "blackshark:" + Bat.Hex4(pid), Name = V3Name(pid), Kind = DeviceKind.Headset,
                        Via = "2.4 GHz receiver", Percent = r[13], Charging = r[14] != 0,
                    };
                    cache[pid] = new Cached { At = Bat.Now, Status = s.Copy() };
                    ctx.Add(s, 0x1532, pid);
                }
            }
        }
    }
}
