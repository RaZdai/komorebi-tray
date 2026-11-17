// Fallbacks that work for any vendor, and the controller drivers. Ported from Wireless Battery
// (generic.cpp, headsetcontrol.cpp, xinput.cpp, playstation.cpp, steam_controller.cpp; GPL-3.0-or-later).
//  * HID devices exposing a standard battery usage (Generic Device Controls "Battery Strength", Battery
//    System "Absolute State Of Charge") in a feature or input report.
//  * Bluetooth devices: Windows keeps the level it reads over HFP (classic) or the GATT Battery Service (LE)
//    in a device property, the same value Settings > Bluetooth & devices shows.
//  * HeadsetControl (github.com/Sapd/HeadsetControl), bundled next to the tray, for every headset it supports.
//  * Xbox controllers through Windows.Gaming.Input (real %) with XInput as the fallback.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KomorebiTray
{
    static class Fnv
    {
        // FNV-1a over the UTF-16 code units, as the original's path hashes (keys stay identical)
        public static uint Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in s) { h ^= ch; h *= 16777619u; }
                return h;
            }
        }
    }

    static class DevProps
    {
        [StructLayout(LayoutKind.Sequential)] public struct DEVINFO { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] public struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }
        [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr parent);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DEVINFO info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DEVINFO info, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int required, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DEVINFO info, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, int flags, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, IntPtr required, ref DEVINFO info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")] public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);

        public static DEVPROPKEY BtBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        public static DEVPROPKEY IsConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        public static DEVPROPKEY ClassOfDevice = new DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);
        public static DEVPROPKEY FriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        public static DEVPROPKEY DeviceDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        public static DEVPROPKEY ContainerId = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        public static DEVPROPKEY ContainerCategory = new DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);

        public const uint TYPE_BYTE = 3, TYPE_UINT32 = 7, TYPE_GUID = 0x0D, TYPE_BOOLEAN = 0x11, TYPE_STRING = 0x12, TYPE_STRING_LIST = 0x2012;

        public static byte[] Get(IntPtr set, ref DEVINFO info, DEVPROPKEY key, out uint type)
        {
            int req;
            type = 0;
            SetupDiGetDevicePropertyW(set, ref info, ref key, out type, null, 0, out req, 0);
            if (req <= 0 || req > 8192) return null;
            var buf = new byte[req];
            return SetupDiGetDevicePropertyW(set, ref info, ref key, out type, buf, req, out req, 0) ? buf : null;
        }

        public static string Str(IntPtr set, ref DEVINFO info, DEVPROPKEY key, bool firstOfList)
        {
            uint t;
            byte[] b = Get(set, ref info, key, out t);
            if (b == null || !(t == TYPE_STRING || (firstOfList && t == TYPE_STRING_LIST))) return "";
            string s = Encoding.Unicode.GetString(b);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        public static string GuidString(Guid g) { return g.ToString("B").ToUpperInvariant(); }   // "{...}" like StringFromGUID2

        // Container id of the physical device an HID interface belongs to, or "" if unknown
        public static string ContainerOf(string path)
        {
            IntPtr di = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (di == new IntPtr(-1)) return "";
            try
            {
                var ifd = new SP_DEVICE_INTERFACE_DATA();
                ifd.cbSize = Marshal.SizeOf(ifd);
                var dd = new DEVINFO();
                dd.cbSize = Marshal.SizeOf(dd);
                if (!SetupDiOpenDeviceInterface(di, path, 0, ref ifd)) return "";
                SetupDiGetDeviceInterfaceDetail(di, ref ifd, IntPtr.Zero, 0, IntPtr.Zero, ref dd);
                if (dd.DevInst == 0) return "";
                uint t;
                byte[] b = Get(di, ref dd, ContainerId, out t);
                if (b == null || t != TYPE_GUID || b.Length != 16) return "";
                return GuidString(new Guid(b));
            }
            finally { SetupDiDestroyDeviceInfoList(di); }
        }
    }

    static class GenericDrivers
    {
        static bool IsBatteryUsage(ushort page, ushort usage) { return (page == 0x06 && usage == 0x20) || (page == 0x85 && usage == 0x65); }

        public static void PollGenericHid(PollContext ctx)
        {
            // Drawing tablets and pen displays (any collection on the Digitizers page 0D) are never read: polling
            // one for a battery report flipped the Y axis of an XP-Pen Deco Fun S
            var digitizers = new HashSet<uint>();
            foreach (HidColl c in ctx.Colls) if (c.Page == 0x0D) digitizers.Add(((uint)c.Vid << 16) | c.Pid);
            foreach (HidColl c in ctx.Colls)
            {
                if (ctx.IsClaimed(c.Vid, c.Pid)) continue;
                if (c.Page == 0x84 || c.Page == 0x85) continue;   // UPS / power supplies
                if (digitizers.Contains(((uint)c.Vid << 16) | c.Pid)) continue;
                if (c.Caps.NumberFeatureValueCaps == 0 && c.Caps.NumberInputValueCaps == 0) continue;
                // Same physical device already shown with Windows' Bluetooth level (its HID field can read 0)
                if (ctx.BtContainers.Count > 0 && ctx.BtContainers.Contains(DevProps.ContainerOf(c.Path))) continue;
                int pct = -1;
                using (SafeFileHandle h = HidNative.CreateFile(c.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    IntPtr pp;
                    if (!HidNative.HidD_GetPreparsedData(h, out pp)) continue;
                    try
                    {
                        foreach (int type in new[] { 2, 0 })   // HidP_Feature, HidP_Input
                        {
                            ushort n = type == 2 ? c.Caps.NumberFeatureValueCaps : c.Caps.NumberInputValueCaps;
                            int len = type == 2 ? c.FeatLen : c.InLen;
                            if (n == 0 || len == 0) continue;
                            var vc = new HidNative.HIDP_VALUE_CAPS[n];
                            if (HidNative.HidP_GetValueCaps(type, vc, ref n, pp) != HidNative.HIDP_STATUS_SUCCESS) continue;
                            for (int i = 0; i < n && pct < 0; i++)
                            {
                                var v = vc[i];
                                ushort u = v.UsageMin;   // NotRange.Usage shares the slot
                                if (!IsBatteryUsage(v.UsagePage, u)) continue;
                                var buf = new byte[len];
                                buf[0] = v.ReportID;
                                bool got = type == 2 ? HidNative.HidD_GetFeature(h, buf, len) : HidNative.HidD_GetInputReport(h, buf, len);
                                if (!got) continue;
                                uint val;
                                if (HidNative.HidP_GetUsageValue(type, v.UsagePage, v.LinkCollection, u, out val, pp, buf, len) != HidNative.HIDP_STATUS_SUCCESS) continue;
                                // Apple devices report a plain percentage while declaring a 0-255 range
                                bool percentQuirk = c.Vid == 0x05AC && val <= 100;
                                if (percentQuirk) pct = (int)val;
                                else if (v.LogicalMax > v.LogicalMin) pct = Bat.Clamp100((int)(((long)val - v.LogicalMin) * 100 / (v.LogicalMax - v.LogicalMin)));
                                else if (val <= 100) pct = (int)val;
                            }
                            if (pct >= 0) break;
                        }
                    }
                    finally { HidNative.HidD_FreePreparsedData(pp); }
                }
                if (pct < 0) continue;
                var s = new DeviceStatus();
                s.Name = Hid.Product(c);
                if (s.Name.Length == 0) s.Name = "Wireless device";
                s.Key = "hid:" + Bat.Hex4(c.Vid) + ":" + Bat.Hex4(c.Pid);
                s.Via = "HID";
                s.Kind = Bat.KindFromCollections(ctx.Colls, c.Vid, c.Pid);
                if (s.Kind == DeviceKind.Other) s.Kind = Bat.GuessKind(s.Name);
                s.Percent = pct;
                ctx.Add(s, c.Vid, c.Pid);
            }
        }

        static DeviceKind KindFromClassOfDevice(uint cod)
        {
            uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
            if (major == 0x04) return DeviceKind.Headset;
            if (major == 0x05)
            {
                if ((minor & 0x0F) == 0x01 || (minor & 0x0F) == 0x02) return DeviceKind.Gamepad;
                if ((minor >> 4) == 1) return DeviceKind.Keyboard;
                if ((minor >> 4) == 2) return DeviceKind.Mouse;
            }
            return DeviceKind.Other;
        }

        // LE devices usually have no class of device; Windows still files them under a category
        static DeviceKind KindFromCategory(string c)
        {
            if (c == "Input.Keyboard") return DeviceKind.Keyboard;
            if (c == "Input.Mouse") return DeviceKind.Mouse;
            if (c == "Input.Gaming") return DeviceKind.Gamepad;
            if (c.StartsWith("Communication.Headset") || c == "Audio.Headphones") return DeviceKind.Headset;
            return DeviceKind.Other;
        }

        // Bluetooth LE keyboards that also expose a mouse collection: Attack Shark K86 (Rongyuan) over Bluetooth
        static DeviceKind KindFromBleIds(ushort vid, ushort pid)
        {
            if (vid == 0x3151 && pid == 0x4013) return DeviceKind.Keyboard;
            return DeviceKind.Other;
        }

        static string StripServiceSuffix(string n)
        {
            foreach (string suf in new[] { " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo", " Audio Gateway" })
                if (n.Length > suf.Length && n.EndsWith(suf, StringComparison.Ordinal)) return n.Substring(0, n.Length - suf.Length);
            return n;
        }

        class Found { public int Pct; public string Name; public bool Connected; }

        public static void PollBluetooth(PollContext ctx)
        {
            IntPtr di = DevProps.SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x6);   // ALLCLASSES | PRESENT
            if (di == new IntPtr(-1)) return;
            var containerName = new Dictionary<string, string>();
            var containerCod = new Dictionary<string, uint>();
            var containerCat = new Dictionary<string, string>();
            var containerConn = new Dictionary<string, bool>();
            var battery = new SortedDictionary<string, Found>(StringComparer.Ordinal);
            try
            {
                var dd = new DevProps.DEVINFO();
                dd.cbSize = Marshal.SizeOf(dd);
                var instSb = new StringBuilder(512);
                for (int i = 0; DevProps.SetupDiEnumDeviceInfo(di, i, ref dd); i++)
                {
                    int req;
                    instSb.Length = 0;
                    if (!DevProps.SetupDiGetDeviceInstanceId(di, ref dd, instSb, 512, out req)) continue;
                    string inst = instSb.ToString();
                    if (!inst.StartsWith("BTHENUM\\") && !inst.StartsWith("BTHLE\\") && !inst.StartsWith("BTHLEDEVICE\\")) continue;
                    uint t;
                    string cid = inst;
                    byte[] cb = DevProps.Get(di, ref dd, DevProps.ContainerId, out t);
                    if (cb != null && cb.Length == 16) cid = DevProps.GuidString(new Guid(cb));
                    string name = DevProps.Str(di, ref dd, DevProps.FriendlyName, false);
                    if (name.Length == 0) name = DevProps.Str(di, ref dd, DevProps.DeviceDesc, false);
                    if (inst.StartsWith("BTHENUM\\DEV_") || inst.StartsWith("BTHLE\\DEV_"))
                    {
                        containerName[cid] = name;
                        byte[] cod = DevProps.Get(di, ref dd, DevProps.ClassOfDevice, out t);
                        if (cod != null && t == DevProps.TYPE_UINT32 && cod.Length >= 4) containerCod[cid] = BitConverter.ToUInt32(cod, 0);
                        string cat = DevProps.Str(di, ref dd, DevProps.ContainerCategory, true);
                        if (cat.Length > 0) containerCat[cid] = cat;
                        byte[] cn = DevProps.Get(di, ref dd, DevProps.IsConnected, out t);
                        if (cn != null && t == DevProps.TYPE_BOOLEAN && cn.Length >= 1) containerConn[cid] = cn[0] != 0;
                    }
                    byte[] pb = DevProps.Get(di, ref dd, DevProps.BtBattery, out t);
                    if (pb == null || t != DevProps.TYPE_BYTE || pb.Length < 1) continue;
                    int pct = pb[0];
                    // The service node's own connected flag is only a fallback: headphones keep their level on the
                    // Hands-Free node, which reads as disconnected while audio runs over A2DP
                    bool conn = true;
                    byte[] cn2 = DevProps.Get(di, ref dd, DevProps.IsConnected, out t);
                    if (cn2 != null && t == DevProps.TYPE_BOOLEAN && cn2.Length >= 1) conn = cn2[0] != 0;
                    uint st, prob;
                    if (DevProps.CM_Get_DevNode_Status(out st, out prob, dd.DevInst, 0) == 0 && (st & 0x400) != 0) continue;   // DN_HAS_PROBLEM
                    if (pct > 100) continue;
                    Found prev;
                    if (battery.TryGetValue(cid, out prev) && prev.Connected && !conn) continue;   // prefer a live node
                    battery[cid] = new Found { Pct = pct, Name = StripServiceSuffix(name), Connected = conn };
                }
            }
            finally { DevProps.SetupDiDestroyDeviceInfoList(di); }

            // Physical devices a driver above already reported over a HID interface on the Bluetooth link itself
            var reported = new HashSet<string>();
            var hidIds = new Dictionary<string, ushort[]>();
            foreach (HidColl c in ctx.Colls)
            {
                string cont = DevProps.ContainerOf(c.Path);
                hidIds[cont] = new[] { c.Vid, c.Pid };
                if (ctx.IsClaimed(c.Vid, c.Pid)) reported.Add(cont);
            }

            foreach (var kv in battery)
            {
                string cid = kv.Key;
                Found f = kv.Value;
                if (reported.Contains(cid)) continue;
                bool mc;
                if (containerConn.TryGetValue(cid, out mc) ? !mc : !f.Connected) continue;
                var s = new DeviceStatus { Key = "bt:" + cid };
                string nm;
                s.Name = containerName.TryGetValue(cid, out nm) && nm.Length > 0 ? nm : f.Name;
                if (string.IsNullOrEmpty(s.Name)) s.Name = "Bluetooth device";
                bool dup = false;
                foreach (DeviceStatus o in ctx.Out) dup |= o.Name == s.Name;
                if (dup) continue;
                s.Via = "Bluetooth";
                s.Percent = f.Pct;
                uint cod;
                s.Kind = containerCod.TryGetValue(cid, out cod) ? KindFromClassOfDevice(cod) : DeviceKind.Other;
                string cat;
                if (s.Kind == DeviceKind.Other && containerCat.TryGetValue(cid, out cat)) s.Kind = KindFromCategory(cat);
                ushort[] ids;
                bool hasIds = hidIds.TryGetValue(cid, out ids);
                if (s.Kind == DeviceKind.Other && hasIds) s.Kind = KindFromBleIds(ids[0], ids[1]);
                if (s.Kind == DeviceKind.Other && hasIds) s.Kind = Bat.KindFromCollections(ctx.Colls, ids[0], ids[1]);
                if (s.Kind == DeviceKind.Other) s.Kind = Bat.GuessKind(s.Name);
                ctx.Out.Add(s);
                ctx.BtContainers.Add(cid);
            }
        }

        // ---- HeadsetControl: runs on its own thread (it can take seconds per headset); the poll uses the
        // latest result. Only the battery is requested, with a short HID timeout.

        class HscDevice { public ushort Vid, Pid; public DeviceStatus Status; }
        static readonly object hscLock = new object();
        static List<HscDevice> hscDevices = new List<HscDevice>();
        static long hscUpdated;
        static int hscRunning;
        static string hscExe;
        static bool hscLooked;

        static string HscCommand()
        {
            if (!hscLooked)
            {
                hscLooked = true;
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "headsetcontrol.exe");
                if (File.Exists(local)) hscExe = local;
                else
                    foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                        try { if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "headsetcontrol.exe"))) { hscExe = Path.Combine(dir.Trim(), "headsetcontrol.exe"); break; } }
                        catch { }
            }
            if (hscExe == null) return null;
            return "-b -o json --timeout 1500" + (Environment.GetEnvironmentVariable("WB_HSC_TEST_DEVICE") != null ? " --test-device" : "");
        }

        static string RunHsc(string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(hscExe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
                    return output.Result + err.Result;
                }
            }
            catch { return ""; }
        }

        static List<HscDevice> ParseHsc(string text)
        {
            var output = new List<HscDevice>();
            int start = text.IndexOf('{');
            if (start < 0) return output;
            Dictionary<string, object> root;
            try { root = Json.Parse(text.Substring(start)); } catch { return output; }
            object dv;
            var devices = root != null && root.TryGetValue("devices", out dv) ? dv as System.Collections.IList : null;
            if (devices == null) return output;
            foreach (object o in devices)
            {
                var d = o as IDictionary<string, object>;
                if (d == null) continue;
                object vidJ, pidJ, batJ;
                if (!d.TryGetValue("id_vendor", out vidJ) || !d.TryGetValue("id_product", out pidJ) || !d.TryGetValue("battery", out batJ)) continue;
                var bat = batJ as IDictionary<string, object>;
                if (bat == null) continue;
                var h = new HscDevice();
                h.Vid = (ushort)ParseHex(Convert.ToString(vidJ));
                h.Pid = (ushort)ParseHex(Convert.ToString(pidJ));
                object stJ, lvJ, nmJ;
                string st = bat.TryGetValue("status", out stJ) ? Convert.ToString(stJ) : "";
                var s = new DeviceStatus { Key = "hsc:" + Bat.Hex4(h.Vid) + ":" + Bat.Hex4(h.Pid) };
                s.Name = d.TryGetValue("device", out nmJ) && Convert.ToString(nmJ).Length > 0 ? Convert.ToString(nmJ) : "Headset";
                s.Kind = DeviceKind.Headset;
                s.Via = "HeadsetControl";
                s.Charging = st == "BATTERY_CHARGING";
                s.Connected = st == "BATTERY_AVAILABLE" || st == "BATTERY_CHARGING";
                if (s.Connected && bat.TryGetValue("level", out lvJ) && lvJ != null && !(lvJ is string) && !(lvJ is bool))
                {
                    double lv = Convert.ToDouble(lvJ);
                    if (lv >= 0) s.Percent = Bat.Clamp100((int)lv);
                }
                h.Status = s;
                output.Add(h);
            }
            return output;
        }

        static int ParseHex(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            int v;
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v) ? v : 0;
        }

        static void RefreshHsc()
        {
            string args = HscCommand();
            List<HscDevice> found = args == null ? new List<HscDevice>() : ParseHsc(RunHsc(args, 30000));
            lock (hscLock) { hscDevices = found; hscUpdated = Bat.Now; }
        }

        public static void PollHeadsetControl(PollContext ctx)
        {
            if (HscCommand() == null) return;
            if (hscUpdated == 0) RefreshHsc();   // first poll: wait for it, so the first display (and --list) is complete
            else if (Bat.Now - hscUpdated > 50000 && Interlocked.CompareExchange(ref hscRunning, 1, 0) == 0)
                new Thread(() => { RefreshHsc(); Interlocked.Exchange(ref hscRunning, 0); }) { IsBackground = true, Name = "headsetcontrol" }.Start();
            List<HscDevice> devices;
            lock (hscLock) devices = hscDevices;
            foreach (HscDevice h in devices)
                if (!ctx.IsClaimed(h.Vid, h.Pid)) ctx.Add(h.Status.Copy(), h.Vid, h.Pid);
        }

        // ---- Xbox: Windows.Gaming.Input (real %) for Microsoft pads and GameSir in Xbox (GIP) mode, XInput levels otherwise

        static bool wgiWarmed;

        static bool WgiVendor(ushort vid) { return vid == 0x045E || vid == 0x3537; }

        static int PollWgi(PollContext ctx, out int thirdParty)
        {
            thirdParty = 0;
            int added = 0;
            try
            {
                var list = Windows.Gaming.Input.RawGameController.RawGameControllers;
                if (!wgiWarmed) { wgiWarmed = true; Thread.Sleep(300); list = Windows.Gaming.Input.RawGameController.RawGameControllers; }   // fills in asynchronously after first use
                for (int i = 0; i < list.Count; i++)
                {
                    var raw = list[i];
                    ushort vid = raw.HardwareVendorId, pid = raw.HardwareProductId;
                    if (!WgiVendor(vid)) continue;
                    var report = ((Windows.Gaming.Input.IGameControllerBatteryInfo)raw).TryGetBatteryReport();
                    if (report == null) continue;
                    int? rem = report.RemainingCapacityInMilliwattHours, full = report.FullChargeCapacityInMilliwattHours;
                    int r = rem ?? 0, f = full ?? 0;
                    var st = report.Status;
                    if (f <= 0 || st == Windows.System.Power.BatteryStatus.NotPresent) continue;
                    var s = new DeviceStatus { Key = "xbox:" + Bat.Hex4(pid) + ":" + i, Name = "Xbox controller" };
                    try { string dn = raw.DisplayName; if (!string.IsNullOrEmpty(dn)) s.Name = dn; } catch { }
                    if (vid == 0x3537) s.Name = "GameSir controller";   // GIP reports a generic name for third-party pads
                    s.Kind = DeviceKind.Gamepad;
                    s.Via = "Xbox Wireless / Bluetooth";
                    s.Percent = Bat.Clamp100(r * 100 / f);
                    s.Charging = st == Windows.System.Power.BatteryStatus.Charging;
                    ctx.Add(s, vid, pid);
                    added++;
                    if (vid != 0x045E) thirdParty++;
                }
            }
            catch (Exception e) { Log.Write("batteries wgi: " + e.Message); }
            return added;
        }

        [StructLayout(LayoutKind.Sequential)] struct XBATTERY { public byte Type, Level; }
        [StructLayout(LayoutKind.Sequential)] struct XSTATE { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")] static extern uint XGetBattery(uint user, byte type, out XBATTERY b);
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint XGetState(uint user, out XSTATE s);
        static bool xinputMissing;

        public static void PollXInput(PollContext ctx)
        {
            // An Xbox controller on Bluetooth may already be listed with a real percentage
            bool btXbox = false;
            foreach (DeviceStatus s in ctx.Out) btXbox |= s.Via == "Bluetooth" && s.Name.Contains("Xbox");
            int thirdParty = 0;
            int precise = btXbox ? 0 : PollWgi(ctx, out thirdParty);
            // GameSir pads answer XInput's headset query with their own battery even with nothing plugged in
            bool skipHeadset = precise > 0 && thirdParty == precise;
            if (xinputMissing) return;
            try
            {
                for (uint user = 0; user < 4; user++)
                {
                    // The Xbox Wireless adapter answers battery queries for empty slots too: only connected slots
                    XSTATE state;
                    if (XGetState(user, out state) != 0) continue;
                    XBATTERY pad;
                    bool hasPad = Query(user, 0, out pad);
                    foreach (byte type in new byte[] { 0, 1 })   // BATTERY_DEVTYPE_GAMEPAD, _HEADSET
                    {
                        if (type == 0 && (btXbox || precise > 0)) continue;   // already shown precisely
                        if (type == 1 && skipHeadset) continue;
                        XBATTERY bi;
                        if (type == 0) { if (!hasPad) continue; bi = pad; }
                        else
                        {
                            if (!Query(user, type, out bi)) continue;
                            if (bi.Type == 0xFF) continue;   // BATTERY_TYPE_UNKNOWN: no headset plugged in
                            // Xbox One / Series pads echo their own battery for the headset query
                            if (hasPad && bi.Type == pad.Type && bi.Level == pad.Level) continue;
                        }
                        var s = new DeviceStatus { Key = "xinput:" + user + ":" + type };
                        s.Name = type == 0 ? "Xbox controller " + (user + 1) : "Xbox headset";
                        s.Kind = type == 0 ? DeviceKind.Gamepad : DeviceKind.Headset;
                        s.Via = "Xbox Wireless";
                        switch (bi.Level)
                        {
                            case 0: s.Percent = 5; s.LevelText = "Empty"; break;
                            case 1: s.Percent = 25; s.LevelText = "Low"; break;
                            case 2: s.Percent = 60; s.LevelText = "Medium"; break;
                            default: s.Percent = 100; s.LevelText = "Full"; break;
                        }
                        ctx.Out.Add(s);
                    }
                }
            }
            catch (DllNotFoundException) { xinputMissing = true; }
            catch (EntryPointNotFoundException) { xinputMissing = true; }
        }

        static bool Query(uint user, byte type, out XBATTERY bi)
        {
            return XGetBattery(user, type, out bi) == 0 && bi.Type != 0 && bi.Type != 1;   // not DISCONNECTED, not WIRED
        }
    }

    static class ControllerDrivers
    {
        static bool IsDs4(ushort pid) { return pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0; }
        static bool IsDualSense(ushort pid) { return pid == 0x0CE6 || pid == 0x0DF2; }

        // Sony DualShock 4 / DualSense / DualSense Edge, USB or Bluetooth. The battery is in the regular input
        // report: DS4 USB 01 byte 30, BT 11 byte 32 (bits 0-3 level, bit 4 cable); DualSense USB 01 byte 53,
        // BT 31 byte 54 (bits 0-3 level 0-10, bits 4-7 state). Over Bluetooth both send a reduced report until a
        // feature report is read, so the calibration report (05) is read once, as games do.
        public static void PollPlayStation(PollContext ctx)
        {
            foreach (HidColl c in ctx.Colls)
            {
                if (c.Vid != 0x054C || !(IsDs4(c.Pid) || IsDualSense(c.Pid))) continue;
                if (c.Page != 0x01 || c.Usage != 0x05) continue;
                using (var d = new HidDev())
                {
                    if (!d.Open(c)) continue;
                    bool bt = c.InLen > 64;
                    byte[] f;
                    if (bt) d.GetFeature(0x05, out f);   // switches the controller to full reports
                    byte[] r = null;
                    int pct = -1;
                    bool charging = false, found = false;
                    long end = Bat.Now + 500;
                    while (!found && end - Bat.Now > 0)
                    {
                        if (d.Read(ref r, 100) < 10) continue;
                        if (IsDs4(c.Pid))
                        {
                            int at = r[0] == 0x01 ? 30 : r[0] == 0x11 ? 32 : 0;
                            if (at == 0 || r.Length <= at || (r[0] == 0x01 && bt)) continue;
                            int level = r[at] & 0x0F;
                            bool cable = (r[at] & 0x10) != 0;
                            if (c.Pid == 0x0BA0 && r[at] == 0) break;   // wireless adapter without a controller
                            pct = cable ? (level >= 11 ? 100 : Bat.Clamp100(level * 10)) : Bat.Clamp100(level * 10 + 5);
                            charging = cable && level < 11;
                            found = true;
                        }
                        else
                        {
                            int at = r[0] == 0x01 ? 53 : r[0] == 0x31 ? 54 : 0;
                            if (at == 0 || r.Length <= at || (r[0] == 0x01 && bt)) continue;
                            int level = r[at] & 0x0F, state = r[at] >> 4;
                            pct = Bat.Clamp100(level * 10 + 5);
                            charging = state == 0x1;
                            if (state == 0x2) { pct = 100; charging = true; }
                            found = true;
                        }
                    }
                    if (!found) continue;
                    var s = new DeviceStatus { Key = "ps:" + Bat.Hex4(c.Pid) + ":" + Fnv.Hash(c.Path) };
                    s.Name = IsDs4(c.Pid) ? "DualShock 4" : c.Pid == 0x0DF2 ? "DualSense Edge" : "DualSense";
                    s.Kind = DeviceKind.Gamepad;
                    s.Via = bt ? "Bluetooth" : (c.Pid == 0x0BA0 ? "Wireless adapter" : "USB cable");
                    s.Percent = pct;
                    s.Charging = charging;
                    ctx.Out.Add(s);
                    ctx.Claim(c.Vid, c.Pid);
                }
            }
        }

        // Valve Steam Controller battery input reports. Layout reference: Linux hid-steam
        static string SteamTransport(ushort pid)
        {
            switch (pid)
            {
                case 0x1142: return "Wireless adapter";   // 2015 receiver
                case 0x1302: return "USB cable";
                case 0x1303: return "Bluetooth";
                case 0x1304:                              // 2026 puck
                case 0x1305: return "Wireless adapter";   // internal receiver
                default: return null;
            }
        }

        static bool ReadSteamBattery(HidDev dev, bool legacy, bool puck, DeviceStatus status)
        {
            byte[] report = null;
            bool receivedInput = false;   // the puck exposes four slots even when fewer controllers are connected
            long deadline = Bat.Now + 5000;
            while (Bat.Now < deadline)
            {
                int size = dev.Read(ref report, puck && !receivedInput ? 250 : 100);
                if (size < 0 && puck && !receivedInput) return false;
                if (size > 0) receivedInput = true;
                if (size < 15) continue;
                if (legacy)
                {
                    if (report[0] != 0x01 || report[1] != 0x00 || report[2] != 0x04 || report[3] != 11 || report[14] > 100) continue;
                    status.Percent = report[14];
                }
                else
                {
                    if (report[0] != 0x43 || report[2] > 100) continue;
                    status.Percent = report[2];
                    status.Charging = report[1] == 2;
                }
                return true;
            }
            return false;
        }

        public static void PollSteamController(PollContext ctx)
        {
            foreach (HidColl c in ctx.Colls)
            {
                if (c.Vid != 0x28DE || c.Page != 0xFF00 || c.InLen < 15) continue;
                string transport = SteamTransport(c.Pid);
                if (transport == null) continue;
                bool puck = c.Pid == 0x1304;
                if (puck && c.Usage != 0x01) continue;   // skip the charging contacts collection
                using (var dev = new HidDev())
                {
                    if (!dev.Open(c)) continue;
                    var status = new DeviceStatus();
                    bool legacy = c.Pid == 0x1142;
                    if (!ReadSteamBattery(dev, legacy, puck, status)) continue;
                    status.Key = "steam:" + Bat.Hex4(c.Pid) + ":" + Fnv.Hash(c.Path);
                    status.Name = Hid.Product(c);
                    if (legacy || puck || c.Pid == 0x1305 || status.Name.Length == 0) status.Name = "Steam Controller";
                    status.Kind = DeviceKind.Gamepad;
                    status.Via = transport;
                    ctx.Add(status, c.Vid, c.Pid);
                }
            }
        }
    }
}
