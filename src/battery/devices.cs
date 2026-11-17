// Runs every driver in order, ported from Wireless Battery (devices.cpp; GPL-3.0-or-later). Vendor drivers go first
// and claim the devices they handle; the generic fallbacks then only look at what is left.
using System;
using System.Collections.Generic;
using System.Text;

namespace KomorebiTray
{
    static class Devices
    {
        // "VID:PID" pairs in hex, separated by spaces, commas or semicolons ("28BD:0947, 046D:C547"): no driver ever
        // opens those devices. Read from komorebi-tray.json ("battery_ignore_devices"), and from Wireless Battery's own
        // registry value (HKCU\Software\WirelessBattery\IgnoreDevices) for people who set it up there
        public const string IgnorePref = "battery_ignore_devices";

        static HashSet<uint> IgnoredDevices()
        {
            var output = new HashSet<uint>();
            var sources = new List<string>();
            try
            {
                object v;
                if (AccentBorder.LoadPrefs().TryGetValue(IgnorePref, out v) && v != null) sources.Add(Convert.ToString(v));
            }
            catch { }
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\WirelessBattery"))
                    if (k != null && k.GetValue("IgnoreDevices") is string) sources.Add((string)k.GetValue("IgnoreDevices"));
            }
            catch { }
            foreach (string src in sources)
                foreach (string part in src.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] vp = part.Split(':');
                    int vid, pid;
                    if (vp.Length == 2 && int.TryParse(vp[0], System.Globalization.NumberStyles.HexNumber, null, out vid) &&
                        int.TryParse(vp[1], System.Globalization.NumberStyles.HexNumber, null, out pid))
                        output.Add(((uint)(vid & 0xFFFF) << 16) | (uint)(pid & 0xFFFF));
                }
            return output;
        }

        static readonly Action<PollContext>[] Drivers =
        {
            GloriousDriver.Poll,
            AulaDriver.Poll,              // before Kysona, which shares the 3554 vendor id and would probe it first
            DareuDriver.Poll,
            AtkSerialDriver.Poll,         // before Kysona, which shares the ATK vendor id
            PulsarDriver.Poll,            // before Kysona, which also scans the 3710 vendor id
            AtkBitmouseDriver.Poll,       // before Kysona, which would probe the NK dongle with the wrong protocol
            KysonaDriver.Poll,
            KeychronDriver.Poll,
            LamzuDriver.Poll,
            EndgameDriver.Poll,
            VaxeeDriver.Poll,
            WlmouseDriver.Poll,
            NordicStatusDrivers.PollNinjutso,
            NordicStatusDrivers.PollSprime,
            RongyuanDriver.Poll,
            SonixDriver.Poll,
            SonyInzoneDriver.Poll,
            FractalDriver.Poll,
            JabraDriver.Poll,
            JblDriver.Poll,
            RogDriver.Poll,
            ZowieDriver.Poll,
            RoyalKludgeDriver.Poll,
            MchoseDriver.Poll,
            RapooDriver.Poll,
            AresonDriver.Poll,
            MchoseDriver.PollG3,
            MiscMiceDriver.Poll,
            FantechDriver.Poll,
            RazerDriver.PollHeadsets,     // before the generic Razer driver, which shares the vendor id
            HyperXDriver.Poll,
            LogitechDriver.PollG435,      // before the HID++ driver, which shares the vendor id
            LogitechDriver.Poll,
            RazerDriver.Poll,
            SteelSeriesDriver.Poll,
            CorsairDriver.Poll,
            CoolerMasterDriver.Poll,
            ControllerDrivers.PollPlayStation,
            ControllerDrivers.PollSteamController,
            SwitchDriver.Poll,            // komorebi-tray: Switch Pro protocol (8BitDo pads in Switch mode, Joy-Con)
            EightBitDoDriver.Poll,        // komorebi-tray: 8BitDo pads on their 2.4 GHz dongle (no level there)
            GameSirDriver.Poll,
            FlydigiDriver.Poll,
            GameSirDriver.PollCyclone,
            AirPodsDriver.Poll,           // before the Bluetooth fallback, which has no level for AirPods
            PhoneLinkDriver.Poll,
            GalaxyBudsDriver.Poll,        // before the Bluetooth fallback, which skips devices already listed by name
            MoondropDriver.Poll,
            GenericDrivers.PollHeadsetControl,
            GenericDrivers.PollBluetooth, // before the generic HID check: Windows' own level wins for the same device
            GenericDrivers.PollGenericHid,
            GenericDrivers.PollXInput,
        };

        // Queries every supported device. Blocking (a second or two); call from a worker thread
        public static List<DeviceStatus> PollAll()
        {
            var ctx = new PollContext();
            ctx.Colls = Hid.Enumerate();
            HashSet<uint> ignored = IgnoredDevices();
            if (ignored.Count > 0) ctx.Colls.RemoveAll(c => ignored.Contains(((uint)c.Vid << 16) | c.Pid));
            foreach (Action<PollContext> driver in Drivers)
            {
                try { driver(ctx); }
                catch (Exception e) { Log.Write("batteries " + driver.Method.DeclaringType.Name + "." + driver.Method.Name + ": " + e.GetType().Name + " " + e.Message); }
            }
            // KOMOREBI_TRAY_BATTERY_DEMO=1: sample devices for checking the look (screenshots, themes) without the hardware
            if (Environment.GetEnvironmentVariable("KOMOREBI_TRAY_BATTERY_DEMO") == "1")
            {
                ctx.Out.Add(new DeviceStatus { Key = "demo:headset", Name = "Demo headset", Via = "2.4 GHz dongle", Kind = DeviceKind.Headset, Percent = 64 });
                ctx.Out.Add(new DeviceStatus { Key = "demo:mouse", Name = "Demo mouse", Via = "2.4 GHz receiver", Kind = DeviceKind.Mouse, Percent = 90, Charging = true });
                ctx.Out.Add(new DeviceStatus { Key = "demo:pad", Name = "Demo controller", Via = "Bluetooth", Kind = DeviceKind.Gamepad, Percent = 12 });
            }
            return ctx.Out;
        }

        // Every HID collection (for adding support for new devices), plus raw Xenta frames
        public static string DumpHidCollections()
        {
            var sb = new StringBuilder("VID:PID    IF  UsagePg:Usage  In  Out Feat  Product\n");
            foreach (HidColl c in Hid.Enumerate())
                sb.AppendFormat("{0:X4}:{1:X4}  {2,2}  {3:X4}:{4:X4}    {5,3} {6,3} {7,3}   {8}\n", c.Vid, c.Pid, c.Iface, c.Page, c.Usage, c.InLen, c.OutLen, c.FeatLen, Hid.Product(c));
            sb.Append(MiscMiceDriver.DumpXentaFrames());
            sb.Append(EightBitDoDriver.DumpFrames());
            return sb.ToString();
        }

        public static void Close() { MiscMiceDriver.Close(); }
    }
}
