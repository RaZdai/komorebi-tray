// Apple AirPods / Beats over Bluetooth and phones linked with Microsoft Phone Link. Ported from Wireless Battery
// (airpods.cpp, phonelink.cpp; GPL-3.0-or-later).
// AirPods broadcast their battery in the BLE "proximity pairing" message (Apple company 0x004C, type 07): [3..4] model,
// [5] bit 5 = left bud broadcasting, [6] bud levels (nibbles, 0-10 = x10 %, 15 unknown), [7] charging bits + case level
// (layout from OpenPods / AirPodsDesktop / librepods). Only messages whose model matches the connected device are used,
// and the strongest recent signal wins (other people's AirPods broadcast the same message).
// Phone Link: the Windows 11 Start menu companion card Phone Link keeps in its package folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KomorebiTray
{
    static class AirPodsDriver
    {
        class Advert { public ushort Model; public int Left = -1, Right = -1, Box = -1; public bool LeftChg, RightChg, BoxChg; public int Rssi = -127; public long Tick; }

        static readonly object gate = new object();
        static readonly Dictionary<ulong, Advert> adverts = new Dictionary<ulong, Advert>();   // by (random, rotating) BLE address
        static Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher watcher;

        static int Level(int nibble) { return nibble <= 10 ? nibble * 10 : -1; }

        static bool Parse(byte[] d, Advert a)
        {
            if (d.Length < 9 || d[0] != 0x07 || d[1] < 0x09) return false;
            a.Model = (ushort)(d[3] | d[4] << 8);
            bool leftBroadcasts = (d[5] & 0x20) != 0;
            int cur = d[6] & 0x0F, other = d[6] >> 4, chg = d[7] >> 4;
            a.Left = Level(leftBroadcasts ? cur : other);
            a.Right = Level(leftBroadcasts ? other : cur);
            a.LeftChg = (chg & (leftBroadcasts ? 0x01 : 0x02)) != 0;
            a.RightChg = (chg & (leftBroadcasts ? 0x02 : 0x01)) != 0;
            a.BoxChg = (chg & 0x04) != 0;
            a.Box = Level(d[7] & 0x0F);
            return a.Left >= 0 || a.Right >= 0;
        }

        static void OnReceived(Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                foreach (var md in args.Advertisement.GetManufacturerDataByCompanyId(0x004C))
                {
                    var rd = Windows.Storage.Streams.DataReader.FromBuffer(md.Data);
                    byte[] p = new byte[rd.UnconsumedBufferLength];
                    rd.ReadBytes(p);
                    var a = new Advert();
                    if (!Parse(p, a)) continue;
                    a.Rssi = args.RawSignalStrengthInDBm;
                    a.Tick = Bat.Now;
                    lock (gate)
                    {
                        adverts[args.BluetoothAddress] = a;
                        var old = new List<ulong>();   // addresses rotate: drop old ones
                        foreach (var kv in adverts) if (a.Tick - kv.Value.Tick > 10 * 60 * 1000) old.Add(kv.Key);
                        foreach (ulong k in old) adverts.Remove(k);
                    }
                }
            }
            catch { }
        }

        // Starts (or restarts after the radio was switched off) the watcher. True if it was just started.
        static bool EnsureWatcher()
        {
            try
            {
                if (watcher == null)
                {
                    watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher();
                    watcher.Received += (s, e) => OnReceived(e);
                    watcher.Start();
                    return true;
                }
                var st = watcher.Status;
                if (st == Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcherStatus.Stopped ||
                    st == Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcherStatus.Aborted) { watcher.Start(); return true; }
            }
            catch (Exception e) { Log.Write("batteries airpods: " + e.Message); }
            return false;
        }

        class AppleDevice { public string Name = ""; public ushort Pid; public bool Connected, Apple; }

        static List<AppleDevice> ConnectedAppleAudio()
        {
            var byContainer = new Dictionary<string, AppleDevice>();
            IntPtr di = DevProps.SetupDiGetClassDevs(IntPtr.Zero, "BTHENUM", IntPtr.Zero, 0x6);
            if (di == new IntPtr(-1)) return new List<AppleDevice>();
            try
            {
                var dd = new DevProps.DEVINFO();
                dd.cbSize = Marshal.SizeOf(dd);
                var sb = new StringBuilder(512);
                for (int i = 0; DevProps.SetupDiEnumDeviceInfo(di, i, ref dd); i++)
                {
                    int req;
                    sb.Length = 0;
                    if (!DevProps.SetupDiGetDeviceInstanceId(di, ref dd, sb, 512, out req)) continue;
                    string inst = sb.ToString();
                    uint t;
                    byte[] cb = DevProps.Get(di, ref dd, DevProps.ContainerId, out t);
                    if (cb == null || cb.Length != 16) continue;
                    string g = DevProps.GuidString(new Guid(cb));
                    AppleDevice d;
                    if (!byContainer.TryGetValue(g, out d)) byContainer[g] = d = new AppleDevice();
                    string id = inst.ToUpperInvariant();
                    int v = id.IndexOf("_VID&0001004C");
                    if (v >= 0)
                    {
                        d.Apple = true;
                        int p = id.IndexOf("_PID&", v);
                        if (p >= 0)
                        {
                            int end = p + 5;
                            while (end < id.Length && Uri.IsHexDigit(id[end])) end++;
                            ushort pid;
                            if (end > p + 5 && ushort.TryParse(id.Substring(p + 5, end - p - 5), System.Globalization.NumberStyles.HexNumber, null, out pid)) d.Pid = pid;
                        }
                    }
                    if (inst.StartsWith("BTHENUM\\DEV_"))
                    {
                        d.Name = DevProps.Str(di, ref dd, DevProps.FriendlyName, false);
                        byte[] cn = DevProps.Get(di, ref dd, DevProps.IsConnected, out t);
                        if (cn != null && t == DevProps.TYPE_BOOLEAN && cn.Length >= 1) d.Connected = cn[0] != 0;
                    }
                }
            }
            finally { DevProps.SetupDiDestroyDeviceInfoList(di); }
            var output = new List<AppleDevice>();
            foreach (AppleDevice d in byContainer.Values)
            {
                string n = d.Name.ToUpperInvariant();
                bool looksApple = n.Contains("AIRPODS") || n.Contains("BEATS");
                bool appleAudio = d.Apple && (d.Pid & 0xFF00) == 0x2000;   // Apple audio model ids are 0x20xx
                if (d.Connected && (appleAudio || (looksApple && d.Pid == 0))) output.Add(d);
            }
            return output;
        }

        // Best message: the model must match when the PID is known; the strongest signal among the last 90 s wins
        // (or the latest one within 5 min if none is that recent)
        static Advert Pick(AppleDevice dev)
        {
            long now = Bat.Now;
            Advert best = null;
            lock (gate)
            {
                foreach (Advert a in adverts.Values)
                {
                    if (dev.Pid != 0 && a.Model != dev.Pid) continue;
                    long age = now - a.Tick;
                    if (age > 5 * 60 * 1000) continue;
                    bool fresh = age < 90 * 1000, bestFresh = best != null && now - best.Tick < 90 * 1000;
                    if (best == null || (fresh && !bestFresh) || (fresh && bestFresh && a.Rssi > best.Rssi) || (!fresh && !bestFresh && a.Tick > best.Tick)) best = a;
                }
            }
            return best;
        }

        static string Pct(int v) { return v < 0 ? "?" : v + "%"; }

        public static void Poll(PollContext ctx)
        {
            var devices = ConnectedAppleAudio();
            if (devices.Count == 0) return;
            if (EnsureWatcher())
                for (int i = 0; i < 25; i++)   // just started: give the buds a moment to broadcast
                {
                    bool all = true;
                    foreach (AppleDevice d in devices) all &= Pick(d) != null;
                    if (all) break;
                    Bat.Sleep(200);
                }
            foreach (AppleDevice d in devices)
            {
                Advert a = Pick(d);
                if (a == null) continue;
                var s = new DeviceStatus { Name = d.Name.Length == 0 ? "AirPods" : d.Name, Kind = DeviceKind.Headset };
                s.Key = "airpods:" + Bat.Hex4(d.Pid) + ":" + s.Name;
                // The lower bud runs out first; a bud sitting in the case reports as charging
                int lo = a.Left < 0 ? a.Right : a.Right < 0 ? a.Left : Math.Min(a.Left, a.Right);
                s.Percent = lo;
                s.Charging = (a.Left < 0 || a.LeftChg) && (a.Right < 0 || a.RightChg);
                s.Via = "Bluetooth · L " + Pct(a.Left) + (a.LeftChg ? "⚡" : "") + "  R " + Pct(a.Right) +
                        (a.RightChg ? "⚡" : "") + "  case " + Pct(a.Box) + (a.BoxChg ? "⚡" : "");
                s.GraceSec = 300;
                ctx.Out.Add(s);
            }
        }
    }

    static class PhoneLinkDriver
    {
        const long StaleSec = 3 * 60 * 60;   // a card not rewritten for this long is shown dimmed

        // Value of the first JSON string field "key":"..." (simple escapes only)
        static bool JsonString(string json, string key, out string value)
        {
            value = null;
            string pat = "\"" + key + "\":\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return false;
            var sb = new StringBuilder();
            for (i += pat.Length; i < json.Length && json[i] != '"'; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    char e = json[++i];
                    if (e == 'u' && i + 4 < json.Length)
                    {
                        int cp;
                        if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out cp)) sb.Append((char)cp);
                        i += 4;
                    }
                    else sb.Append(e == 'n' || e == 't' ? ' ' : e);
                }
                else sb.Append(json[i]);
            }
            value = sb.ToString();
            return i < json.Length;
        }

        // <n> from the first "Battery <n>% remaining"
        static int BatteryPercent(string json)
        {
            const string pat = "Battery ";
            for (int i = json.IndexOf(pat, StringComparison.Ordinal); i >= 0; i = json.IndexOf(pat, i + 1, StringComparison.Ordinal))
            {
                int j = i + pat.Length, k = j;
                while (k < json.Length && char.IsDigit(json[k]) && k - j < 3) k++;
                if (k > j && string.CompareOrdinal(json, k, "% remaining", 0, 11) == 0)
                {
                    int v = int.Parse(json.Substring(j, k - j));
                    if (v >= 0 && v <= 100) return v;
                }
            }
            return -1;
        }

        public static void Poll(PollContext ctx)
        {
            string baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(baseDir)) return;
            string path = Path.Combine(baseDir, @"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalState\StartMenu\StartMenuCompanion.json");
            string json;
            DateTime written;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= 0 || fi.Length >= 4 * 1024 * 1024) return;
                written = fi.LastWriteTimeUtc;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
                    json = sr.ReadToEnd();
            }
            catch { return; }
            string speak;
            if (!JsonString(json, "speak", out speak)) return;
            const string suffix = " - Connected to your PC";   // anything else (disconnected, setup cards) is skipped
            if (speak.Length <= suffix.Length || !speak.EndsWith(suffix, StringComparison.Ordinal)) return;
            int pct = BatteryPercent(json);
            if (pct < 0) return;
            double ageSec = Math.Max(0, (DateTime.UtcNow - written).TotalSeconds);
            ctx.Out.Add(new DeviceStatus
            {
                Key = "phonelink", Name = speak.Substring(0, speak.Length - suffix.Length), Via = "Phone Link", Kind = DeviceKind.Phone,
                Percent = pct, Asleep = ageSec > StaleSec,
            });
        }
    }
}
