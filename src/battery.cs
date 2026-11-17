// Battery levels of wireless mice, keyboards, controllers, headsets and phones, read straight from the devices.
// The device engine is a C# port of Wireless Battery (github.com/krisgfx9/Wireless-Device-Battery-Monitor,
// GPL-3.0-or-later) in src/battery/; this file is its worker thread and command-line output (main.cpp's Worker and
// ListToConsole), and battery-ui.cs the taskbar readout, tray icons, menu and the Batteries window.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KomorebiTray
{
    static class BatteryMonitor
    {
        const int PollIntervalMs = 60 * 1000;   // levels refresh every 60 s, and right away on device changes / resume

        static readonly AutoResetEvent refresh = new AutoResetEvent(false);
        static readonly ManualResetEvent stop = new ManualResetEvent(false);
        static Thread worker;

        // Raised on the worker thread after every poll
        public static event Action<List<DeviceStatus>> Results;

        public static void Start()
        {
            if (worker != null) return;
            stop.Reset();
            worker = new Thread(Worker) { IsBackground = true, Name = "batteries", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public static void RefreshNow() { refresh.Set(); }

        public static void Stop()
        {
            stop.Set();
            if (worker != null && worker != Thread.CurrentThread) worker.Join(5000);
            worker = null;
            try { Devices.Close(); } catch { }
        }

        static void Worker()
        {
            for (;;)
            {
                List<DeviceStatus> list = null;
                try { list = Devices.PollAll(); }
                catch (Exception e) { Log.Write("batteries: " + e.Message); }
                if (list != null && !stop.WaitOne(0))
                {
                    var h = Results;
                    if (h != null) try { h(list); } catch (Exception e) { Log.Write("batteries ui: " + e.Message); }
                }
                if (WaitHandle.WaitAny(new WaitHandle[] { stop, refresh }, PollIntervalMs) == 0) return;
            }
        }

        // ---- command line: --battery-list prints what it finds, --battery-dump every HID collection

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        static void Write(string s)
        {
            AttachConsole(-1);
            try
            {
                var stdout = Console.OpenStandardOutput();
                byte[] b = new UTF8Encoding(false).GetBytes(s);
                stdout.Write(b, 0, b.Length);
                stdout.Flush();
            }
            catch { }
        }

        public static void PrintList()
        {
            var sb = new StringBuilder();
            List<DeviceStatus> list = Devices.PollAll();
            if (list.Count == 0) sb.Append("No devices with battery info found.\n");
            foreach (DeviceStatus s in list)
            {
                string line = s.Name + "  [" + s.Via + "]  ";
                if (!s.Connected) line += "off / not connected";
                else if (s.Asleep) line += "asleep";
                else if (s.LevelText.Length > 0) line += s.LevelText;
                else if (s.Percent >= 0) line += s.Percent + "%";
                else line += "unknown";
                if (s.Detail.Length > 0) line += " (" + s.Detail + ")";
                if (s.Charging) line += ", charging";
                if (s.Millivolts != 0) line += ", " + s.Millivolts + " mV";
                sb.Append(line + "   (" + s.Key + ")\n");
            }
            Devices.Close();
            Write(sb.ToString());
        }

        public static void PrintDump()
        {
            Write(Devices.DumpHidCollections());
            Devices.Close();
        }

        // ---- "report a device" details, copied from the Batteries window into a GitHub issue

        public static string Report(List<BatteryTray.Shown> current)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### Device");
            sb.AppendLine("- Brand and model: ");
            sb.AppendLine("- Connected by: 2.4 GHz dongle / Bluetooth / cable");
            sb.AppendLine("- Battery the vendor app or the device itself shows: ");
            sb.AppendLine();
            sb.AppendLine("### System");
            sb.AppendLine("- komorebi-tray " + Program.Version);
            sb.AppendLine("- " + WindowsVersion());
            sb.AppendLine();
            sb.AppendLine("### What komorebi-tray reads");
            int bt = 0;
            foreach (BatteryTray.Shown d in current)
            {
                if (d.S.Via.StartsWith("Bluetooth")) { bt++; continue; }   // Bluetooth names are often personal ("Alex's AirPods")
                sb.AppendLine("- " + d.S.Name + ": " + d.Status + " (" + d.S.Via + ", " + d.S.Key + ")");
            }
            if (bt > 0) sb.AppendLine("- " + bt + " Bluetooth device" + (bt == 1 ? "" : "s"));
            if (current.Count == 0) sb.AppendLine("- nothing");
            sb.AppendLine();
            sb.AppendLine("### USB / HID devices");
            sb.AppendLine("```");
            var seen = new HashSet<string>();
            foreach (HidColl c in Hid.Enumerate())
            {
                if (c.Vid == 0) continue;
                string line = string.Format("{0:X4}:{1:X4}  {2,2}  {3:X4}:{4:X4}    {5,3} {6,3} {7,3}   {8}",
                    c.Vid, c.Pid, c.Iface, c.Page, c.Usage, c.InLen, c.OutLen, c.FeatLen, Hid.Product(c));
                if (seen.Add(line)) sb.AppendLine(line);
                if (seen.Count >= 80) { sb.AppendLine("…"); break; }
            }
            sb.AppendLine("```");
            return sb.ToString();
        }

        static string WindowsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string build = Convert.ToString(k.GetValue("CurrentBuild"));
                    int b;
                    int.TryParse(build, out b);
                    string name = Convert.ToString(k.GetValue("ProductName"));
                    if (b >= 22000) name = name.Replace("Windows 10", "Windows 11");   // Windows 11 still says 10 here
                    return name + " " + Convert.ToString(k.GetValue("DisplayVersion")) + " (build " + build + "." + Convert.ToString(k.GetValue("UBR")) + ")";
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }
    }
}
