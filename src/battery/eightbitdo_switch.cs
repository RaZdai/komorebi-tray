// Controllers that speak the Nintendo Switch Pro protocol (8BitDo pads in Switch mode, Pro Controller, Joy-Con),
// and 8BitDo pads on their own 2.4 GHz dongle. Not part of Wireless Battery; written for komorebi-tray from
// github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering (bluetooth_hid_notes, bluetooth_hid_subcommands_notes).
using System;
using System.Collections.Generic;

namespace KomorebiTray
{
    static class SwitchDriver
    {
        static readonly ushort[] Pids = { 0x2009, 0x2006, 0x2007 };   // Pro Controller (8BitDo pads in Switch mode), Joy-Con L / R
        const int AskEverySec = 180;   // one tiny request every 3 minutes; listening is free

        class Cached { public DeviceStatus S; public long At; }
        static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        static int counter;

        // Every input report a pad sends in full mode (30, 31) or as a subcommand reply (21) carries the battery in the
        // high nibble of byte 2: level 0 / 2 / 4 / 6 / 8 (empty .. full), bit 0 = charging
        static int BatteryOf(byte[] r, int n) { return n >= 3 && (r[0] == 0x21 || r[0] == 0x30 || r[0] == 0x31) ? r[2] >> 4 : -1; }

        static int Listen(HidDev d)
        {
            byte[] r = null;
            for (int i = 0; i < 4; i++)   // a game or Steam that put the pad in full mode already gets reports 60+ times a second
            {
                int n = d.Read(ref r, 40);
                if (n <= 0) break;
                int b = BatteryOf(r, n);
                if (b >= 0) return b;
            }
            return -1;
        }

        // Subcommand 02 (device info) only asks a question: it doesn't change the pad's input mode for games.
        // Output report 01: packet counter, neutral rumble for both sides, subcommand.
        static int Ask(HidDev d)
        {
            var cmd = new byte[11];
            cmd[0] = 0x01; cmd[1] = (byte)(counter++ & 0x0F);
            cmd[2] = 0x00; cmd[3] = 0x01; cmd[4] = 0x40; cmd[5] = 0x40;
            cmd[6] = 0x00; cmd[7] = 0x01; cmd[8] = 0x40; cmd[9] = 0x40;
            cmd[10] = 0x02;
            if (!d.Write(cmd)) return -1;
            long end = Bat.Now + 700;
            int battery = -1;
            byte[] r = null;
            while (end - Bat.Now > 0)
            {
                int n = d.Read(ref r, (int)Math.Max(1, end - Bat.Now));
                if (n <= 0) break;
                int b = BatteryOf(r, n);
                if (b >= 0) { battery = b; if (r[0] == 0x21) break; }
            }
            return battery;
        }

        public static void Poll(PollContext ctx)
        {
            var seen = new HashSet<string>();
            foreach (ushort pid in Pids)
            {
                if (ctx.IsClaimed(0x057E, pid)) continue;
                foreach (HidColl c in ctx.Find(0x057E, pid, x => x.Page == 0x01 && (x.Usage == 0x04 || x.Usage == 0x05) && x.OutLen >= 11 && x.InLen >= 3))
                {
                    string key = "switch:" + Bat.Hex4(pid) + ":" + Fnv.Hash(c.Path).ToString("X8");
                    if (!seen.Add(key)) continue;
                    Cached cached;
                    if (cache.TryGetValue(key, out cached) && Bat.Now - cached.At < AskEverySec * 1000L)
                    {
                        ctx.Add(cached.S.Copy(), 0x057E, pid);
                        continue;
                    }
                    int battery;
                    using (var d = new HidDev())
                    {
                        if (!d.Open(c)) continue;
                        battery = Listen(d);
                        if (battery < 0) battery = Ask(d);
                    }
                    if (battery < 0) continue;
                    string product = Hid.Product(c);
                    string name = pid == 0x2006 ? "Joy-Con (L)" : pid == 0x2007 ? "Joy-Con (R)" :
                        product.IndexOf("8BitDo", StringComparison.OrdinalIgnoreCase) >= 0 ? product : "Pro Controller";
                    var s = new DeviceStatus
                    {
                        Key = key, Name = name, Kind = DeviceKind.Gamepad,
                        Via = c.Path.IndexOf("{00001124-", StringComparison.OrdinalIgnoreCase) >= 0 ? "Bluetooth (Switch mode)" : "Switch mode",
                    };
                    int level = battery & 0x0E;   // 0, 2, 4, 6, 8
                    s.Percent = level * 100 / 8;
                    s.LevelText = level >= 8 ? "Full" : level >= 6 ? "Medium" : level >= 4 ? "Low" : level >= 2 ? "Critical" : "Empty";
                    s.Detail = "4 steps";
                    s.Charging = (battery & 1) != 0;
                    s.GraceSec = AskEverySec + 60;
                    cache[key] = new Cached { S = s.Copy(), At = Bat.Now };
                    ctx.Add(s, 0x057E, pid);
                }
            }
            var gone = new List<string>();
            foreach (string k in cache.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (string k in gone) cache.Remove(k);
        }
    }

    // 8BitDo pads on their 2.4 GHz dongle (Ultimate 2C, Ultimate, Ultimate 2 ...). The dongle tells Windows it is a
    // wired Xbox 360 pad (XInput reports "wired", Windows.Gaming.Input a constant full battery) and its vendor channel
    // has no battery command (github.com/ascendedent/8bitdo-Linux-Software documents every command the firmware
    // takes), so no reading exists on this link. The pad is listed so the Batteries window and the menu say whether
    // it's on and where its level can be read (checked on an Ultimate 2C: the identify reply carries the firmware
    // version, not a level, and nothing is pushed on the vendor channel); with nothing to show it stays off the taskbar. With the pad switched
    // off the dongle comes back under another id named "IDLE" (Ultimate 2C: 2DC8:301C).
    static class EightBitDoDriver
    {
        // For --battery-dump: what the dongle's vendor channel sends by itself during ~5 s (nothing is written to it),
        // and what Windows.Gaming.Input reports for the pad
        public static string DumpFrames()
        {
            var output = new System.Text.StringBuilder();
            foreach (HidColl c in Hid.Enumerate())
            {
                if (c.Vid != 0x2DC8 || c.Page < 0xFF00 || c.InLen == 0) continue;
                output.AppendFormat("\n8BitDo {0:X4}:{1:X4} {2:X4}:{3:X4} frames (5 s):\n", c.Vid, c.Pid, c.Page, c.Usage);
                using (var d = new HidDev())
                {
                    if (!d.OpenReadOnly(c)) { output.Append("  cannot open\n"); continue; }
                    byte[] r = null;
                    int frames = 0;
                    long end = Bat.Now + 5000;
                    while (end - Bat.Now > 0 && frames < 12)
                    {
                        if (d.Read(ref r, (int)Math.Max(1, end - Bat.Now)) < 0) break;
                        output.Append(" ");
                        for (int i = 0; i < r.Length && i < 40; i++) output.AppendFormat(" {0:X2}", r[i]);
                        output.Append("\n");
                        frames++;
                    }
                    if (frames == 0) output.Append("  none\n");
                }
            }
            try
            {
                var list = Windows.Gaming.Input.RawGameController.RawGameControllers;
                System.Threading.Thread.Sleep(300);
                list = Windows.Gaming.Input.RawGameController.RawGameControllers;
                foreach (var raw in list)
                {
                    var rep = ((Windows.Gaming.Input.IGameControllerBatteryInfo)raw).TryGetBatteryReport();
                    output.AppendFormat("\nWindows.Gaming.Input {0:X4}:{1:X4} {2}: ", raw.HardwareVendorId, raw.HardwareProductId, raw.DisplayName);
                    if (rep == null) output.Append("no battery report\n");
                    else output.AppendFormat("{0} / {1} mWh, {2}\n", rep.RemainingCapacityInMilliwattHours, rep.FullChargeCapacityInMilliwattHours, rep.Status);
                }
            }
            catch (Exception e) { output.Append("\nWindows.Gaming.Input: " + e.Message + "\n"); }
            return output.ToString();
        }

        public static void Poll(PollContext ctx)
        {
            var names = new SortedDictionary<ushort, string>();
            bool idle = false;
            foreach (HidColl c in ctx.Colls)
            {
                if (c.Vid != 0x2DC8 || ctx.IsClaimed(0x2DC8, c.Pid)) continue;
                string product = Hid.Product(c);
                if (product.Equals("IDLE", StringComparison.OrdinalIgnoreCase)) { idle = true; ctx.Claim(0x2DC8, c.Pid); continue; }
                string n;
                if (!names.TryGetValue(c.Pid, out n)) names[c.Pid] = n = null;
                // Some interfaces carry a status word as their name: take the one that says 8BitDo
                if (product.IndexOf("8BitDo", StringComparison.OrdinalIgnoreCase) >= 0 && (n == null || product.Length < n.Length)) names[c.Pid] = product;
            }
            foreach (var kv in names)
            {
                string name = kv.Value ?? (kv.Key == 0x310A ? "8BitDo Ultimate 2C" : "8BitDo controller");
                int paren = name.IndexOf('(');   // "Controller (8BitDo Ultimate 2C Wireless)"
                if (paren >= 0 && name.EndsWith(")")) name = name.Substring(paren + 1, name.Length - paren - 2);
                name = name.Replace(" Wireless Controller", "").Replace(" Wireless", "").Trim();
                ctx.Add(new DeviceStatus
                {
                    Key = "8bitdo:dongle", Name = name, Kind = DeviceKind.Gamepad, Via = "2.4 GHz dongle", NeedsLevel = true,
                    Detail = "none on the 2.4 GHz dongle; Switch mode over Bluetooth has one",
                }, 0x2DC8, kv.Key);
            }
            if (names.Count == 0 && idle)
                ctx.Out.Add(new DeviceStatus { Key = "8bitdo:dongle", Name = "8BitDo controller", Kind = DeviceKind.Gamepad, Via = "2.4 GHz dongle", Connected = false });
        }
    }
}
