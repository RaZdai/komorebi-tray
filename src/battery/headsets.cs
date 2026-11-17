// Sony INZONE, Fractal Scape, Jabra (Link dongles) and JBL Quantum headsets. Ported from Wireless Battery
// (sony_inzone.cpp, fractal.cpp, jabra.cpp, jbl.cpp; GPL-3.0-or-later).
// INZONE: Sony command layer through HID report 02 on FF04 (HeadsetControl; Buds layout from
// kinako19/takamachi66 inzone-buds-battery-tray). Fractal: Adjust Pro web configurator, GET_DEVICE_STATE.
// Jabra: GNP protocol, class 12 / op 02 (Watchdog0x/jabridge). JBL: "HIDV3" device-info, feature 000D (QuantumENGINE).
using System;
using System.Collections.Generic;
using System.Text;

namespace KomorebiTray
{
    static class SonyInzoneDriver
    {
        class Event { public byte Id, Type; public ushort Tid; public byte[] Payload; }

        static bool ParseEvent(byte[] b, out Event e)
        {
            e = null;
            if (b.Length < 15 || b[0] != 0x02) return false;
            int len = b[1];
            if (len < 12 || len > 62 || b.Length < len + 2) return false;
            if (b[2] != 0x04 || b[3] != 0xFF || b[5] != 0x00 || b[6] != 0x96 || b[7] != 0xC3) return false;
            if ((b[8] >> 4) != 0x1) return false;   // must be addressed to the PC
            int sum = 0;
            for (int i = 5; i <= len; i++) sum += b[i];
            if ((byte)sum != b[len + 1]) return false;
            e = new Event { Id = b[9], Type = b[10], Tid = (ushort)(b[11] | (b[12] << 8)) };
            e.Payload = new byte[len + 1 - 13];
            Array.Copy(b, 13, e.Payload, 0, e.Payload.Length);
            return true;
        }

        enum Reply { Ok, NoReply, Failed }
        static ushort tid = 1;

        // NoReply and Failed are the same for a headset; for the Buds, NoReply can mean both buds are in the case
        static Reply GetBattery(HidDev d, int minPayload, out byte[] payload)
        {
            payload = null;
            if (++tid <= 1) tid = 2;   // tid 1 is used by the dongle's own notifications
            var req = new byte[64];
            req[0] = 0x02; req[1] = 12; req[2] = 0x01; req[3] = 0x00; req[4] = 0xFC; req[5] = 8;
            req[6] = 0x96; req[7] = 0xC3; req[8] = 0x41; req[9] = 0x04; req[10] = 0x01;
            req[11] = (byte)tid; req[12] = (byte)(tid >> 8);
            int sum = 0;
            for (int i = 6; i <= 12; i++) sum += req[i];
            req[13] = (byte)sum;
            d.Flush();
            if (!d.Write(req)) return Reply.Failed;
            long end = Bat.Now + 1500;
            byte[] r = null;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref r, left) < 15) return Reply.NoReply;
                Event e;
                if (!ParseEvent(r, out e) || e.Id != 0x04 || e.Tid != tid || e.Payload.Length < minPayload) continue;
                if (e.Type != 0x10 && e.Type != 0xA0) continue;   // RET or active notification
                payload = e.Payload;
                return Reply.Ok;
            }
            return Reply.NoReply;
        }

        // Lowest bud charge, or -1 when both are docked (FF = docked; > 100 invalid)
        static int LowestBudOut(byte[] p)
        {
            int lvl = -1;
            foreach (int i in new[] { 1, 3 })
            {
                if (p[i] > 100) continue;
                lvl = lvl < 0 ? p[i] : Math.Min(lvl, (int)p[i]);
            }
            return lvl;
        }

        class Model { public ushort Pid; public string Name; public bool Buds; public int MinPayload; }

        public static void Poll(PollContext ctx)
        {
            foreach (Model m in new[] {
                new Model { Pid = 0x0FA8, Name = "Sony INZONE H9 II", MinPayload = 2 },
                new Model { Pid = 0x0EBF, Name = "Sony INZONE H5", MinPayload = 2 },
                new Model { Pid = 0x0EC2, Name = "Sony INZONE Buds", Buds = true, MinPayload = 6 } })
            {
                if (ctx.IsClaimed(0x054C, m.Pid)) continue;
                Func<DeviceStatus> status = () => new DeviceStatus { Key = "inzone:" + Bat.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
                bool added = false, noReply = false;
                foreach (HidColl c in ctx.Find(0x054C, m.Pid, x => x.Page == 0xFF04 && x.OutLen >= 64))
                {
                    byte[] p;
                    Reply reply;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        reply = GetBattery(d, m.MinPayload, out p);
                    }
                    if (reply == Reply.NoReply) noReply = true;
                    if (reply != Reply.Ok) continue;
                    DeviceStatus s = status();
                    if (m.Buds)
                    {
                        int lvl = LowestBudOut(p);
                        s.Connected = lvl >= 0;
                        s.Percent = lvl;
                    }
                    else
                    {
                        bool offline = p[1] == 0xFF;
                        s.Connected = !offline;
                        s.Percent = offline ? -1 : (p[1] <= 100 ? p[1] : -1);
                        s.Charging = p[0] != 0;
                    }
                    ctx.Add(s, 0x054C, m.Pid);
                    added = true;
                    break;
                }
                // Buds: the dongle took the request but nothing answered, as with both buds in the case
                if (m.Buds && noReply && !added)
                {
                    DeviceStatus s = status();
                    s.Connected = false;
                    ctx.Add(s, 0x054C, m.Pid);
                }
            }
        }
    }

    static class FractalDriver
    {
        const byte ReportId = 0x02, TypeDongle = 0x11, TypeHeadset = 0xF1, GetDeviceState = 0x21;

        static bool GetState(HidDev d, byte type, ref byte[] r)
        {
            d.Flush();
            if (!d.Write(Bat.B(ReportId, type, GetDeviceState))) return false;
            long end = Bat.Now + 1000;
            for (;;)
            {
                int left = (int)(end - Bat.Now);
                if (left <= 0) return false;
                int n = d.Read(ref r, left);
                if (n < 0) return false;
                if (n >= 24 && r[0] == ReportId && r[1] == type && r[2] == GetDeviceState) return true;
            }
        }

        public static void Poll(PollContext ctx)
        {
            foreach (ushort[] vp in ctx.Products(0x36BC))
            {
                ushort vid = vp[0], pid = vp[1];
                if (pid != 0x0001 && pid != 0x2001) continue;
                bool cable = pid == 0x2001;
                foreach (HidColl c in ctx.Find(vid, pid, x => x.Page == 0xFF00 && x.Usage == 0x0001 && x.OutLen >= 4 && x.InLen >= 24))
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        var s = new DeviceStatus { Key = "fractal:scape", Name = "Fractal Scape", Kind = DeviceKind.Headset, Via = cable ? "USB cable" : "2.4 GHz dongle" };
                        byte[] r = null;
                        if (!cable)
                        {
                            if (!GetState(d, TypeDongle, ref r)) continue;
                            if (r[4] != 1) { s.Connected = false; ctx.Add(s, vid, pid); break; }   // headset off, out of range or on its stand
                        }
                        if (!GetState(d, TypeHeadset, ref r) || r[15] > 100) continue;
                        s.Percent = r[15];
                        s.Charging = r[23] == 1;
                        ctx.Add(s, vid, pid);
                        break;
                    }
            }
        }
    }

    static class JabraDriver
    {
        const ushort Jabra = 0x0B0E;
        const byte Headset = 0x04;
        enum Reply { Ok, Nak, None }
        static byte seq = 0x20;
        static readonly Dictionary<ushort, string> names = new Dictionary<ushort, string>();

        static Reply Query(HidDev d, byte dst, byte cls, byte op, out byte[] payload, int timeoutMs)
        {
            payload = null;
            byte s = seq++;
            d.Flush();
            if (!d.Write(Bat.B(0x05, dst, 0x00, s, 0x46, cls, op), 300)) return Reply.None;
            long end = Bat.Now + timeoutMs;
            byte[] r = null;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                int n = d.Read(ref r, left);
                if (n < 0) return Reply.None;
                if (n < 6) continue;   // unrelated short report
                if (r[0] != 0x05 || r[1] != 0x00 || r[2] != dst || r[3] != s || (r[4] & 0xC0) != 0xC0) continue;
                if (r[5] == 0xFE) return Reply.Nak;
                int len = r[4] & 0x3F;   // counts from r[1]
                if (r.Length < 7 || r[5] != cls || r[6] != op || len < 6 || len + 1 > r.Length) continue;
                payload = new byte[len + 1 - 7];
                Array.Copy(r, 7, payload, 0, payload.Length);
                return Reply.Ok;
            }
            return Reply.None;
        }

        static string HeadsetName(HidDev d)
        {
            byte[] p;
            if (Query(d, Headset, 0x02, 0x00, out p, 400) != Reply.Ok || p.Length == 0 || p[0] == 0 || p[0] + 1 > p.Length) return "";
            try { return Encoding.UTF8.GetString(p, 1, p[0]); } catch { return ""; }
        }

        public static void Poll(PollContext ctx)
        {
            // Link 380a / 380c and 390 USB-A / USB-C, UC and MS variants
            foreach (ushort pid in new ushort[] { 0x24C7, 0x24C8, 0x24C9, 0x24CA, 0x24E9, 0x2E50, 0x2E51, 0x2E56, 0x2E57 })
            {
                if (ctx.IsClaimed(Jabra, pid)) continue;
                foreach (HidColl c in ctx.Find(Jabra, pid, x => x.Page == 0xFF00 && x.OutLen >= 64 && x.InLen >= 64))
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        byte[] p;
                        Reply rep = Query(d, Headset, 0x12, 0x02, out p, 700);
                        if (rep == Reply.None || (rep == Reply.Ok && p.Length < 2)) continue;
                        var s = new DeviceStatus { Key = "jabra:" + Bat.Hex4(pid), Kind = DeviceKind.Headset, Via = "Jabra Link dongle" };
                        string nm;
                        if (rep == Reply.Ok)
                        {
                            s.Percent = p[1] <= 100 ? p[1] : -1;
                            s.Charging = (p[0] & 0x01) != 0;
                            if (!names.ContainsKey(pid))
                            {
                                string n = HeadsetName(d);
                                names[pid] = n.Length == 0 ? "Jabra headset" : n;
                            }
                            s.Name = names[pid];
                        }
                        else
                        {
                            s.Connected = false;   // NAK: dongle present, headset off or out of range
                            s.Name = names.TryGetValue(pid, out nm) ? nm : "Jabra headset";
                            names.Remove(pid);
                        }
                        ctx.Add(s, Jabra, pid);
                        break;
                    }
            }
        }
    }

    static class JblDriver
    {
        const ushort Vid = 0x0ECB, BatteryFeature = 0x000D;
        class Model { public ushort Pid; public string Name; public bool Dongle; }
        static readonly Model[] Models =
        {
            new Model { Pid = 0x2125, Name = "JBL Quantum 650 Wireless", Dongle = true }, new Model { Pid = 0x2126, Name = "JBL Quantum 650 Wireless" },
            new Model { Pid = 0x213B, Name = "JBL Quantum 650X Wireless", Dongle = true }, new Model { Pid = 0x213D, Name = "JBL Quantum 650X Wireless" },
            new Model { Pid = 0x2127, Name = "JBL Quantum 950 Wireless", Dongle = true }, new Model { Pid = 0x2128, Name = "JBL Quantum 950 Wireless" },
            new Model { Pid = 0x213E, Name = "JBL Quantum 950X Wireless", Dongle = true }, new Model { Pid = 0x2140, Name = "JBL Quantum 950X Wireless" },
            new Model { Pid = 0x218D, Name = "JBL Quantum 520 Wireless", Dongle = true }, new Model { Pid = 0x218E, Name = "JBL Quantum 520 Wireless" },
            new Model { Pid = 0x21A7, Name = "JBL Quantum 520X Wireless", Dongle = true }, new Model { Pid = 0x21A9, Name = "JBL Quantum 520X Wireless" },
            new Model { Pid = 0x219A, Name = "JBL Quantum 850 Wireless", Dongle = true }, new Model { Pid = 0x219B, Name = "JBL Quantum 850 Wireless" },
        };

        // With the headset off the dongle doesn't answer at all: a timeout means "not there"
        static bool GetBattery(HidDev d, out byte info)
        {
            info = 0;
            d.Flush();
            if (!d.SetFeature(Bat.B(0x88, 0x00, 0xDD, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, BatteryFeature & 0xFF, BatteryFeature >> 8))) return false;
            long end = Bat.Now + 800;
            byte[] r = null;
            while (end - Bat.Now > 0)
            {
                int left = (int)(end - Bat.Now);
                if (d.Read(ref r, left) < 16) return false;
                if (r[0] != 0x86 || r[1] != 0x00 || r[2] != 0xDD || r[3] != 0x01 || r[4] != 0x00 || r[5] != 0x01) continue;
                if ((r[10] & 0x80) != 0) return false;   // error status (bit 15)
                if (r[11] != (BatteryFeature & 0xFF) || r[12] != (BatteryFeature >> 8)) continue;
                if (r[13] == 0 && r[14] == 0) continue;   // empty feature value
                info = r[15];
                return (info & 0x7F) <= 100;
            }
            return false;
        }

        public static void Poll(PollContext ctx)
        {
            foreach (Model m in Models)
            {
                if (ctx.IsClaimed(Vid, m.Pid)) continue;
                foreach (HidColl c in ctx.Find(Vid, m.Pid, x => x.Page == 0xFF12 && x.FeatLen >= 11 && x.InLen >= 16))
                {
                    byte info;
                    using (var d = new HidDev())
                        if (!d.Open(c) || !GetBattery(d, out info)) continue;
                    ctx.Add(new DeviceStatus
                    {
                        Key = "jbl:" + Bat.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Headset, Via = m.Dongle ? "2.4 GHz receiver" : "USB cable",
                        Percent = info & 0x7F, Charging = (info & 0x80) != 0,
                    }, Vid, m.Pid);
                    break;
                }
            }
        }
    }
}
