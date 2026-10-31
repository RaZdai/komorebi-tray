// komorebi-tray setup: one self-contained exe that installs, updates and uninstalls the tray.
// The tray, its icon and the default config travel inside as embedded resources.
// komorebi itself is never bundled: it is installed from its official winget package when missing.
//
//   komorebi-tray-setup.exe                 install (or update) with a window
//   komorebi-tray-setup.exe /S              install silently with the defaults
//   uninstall.exe /uninstall [/S]           remove (Apps & Features calls this)
//   /root:<dir>                             test mode: install into <dir> instead of your profile, change nothing else

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("komorebi-tray setup")]
[assembly: AssemblyProduct("komorebi-tray")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace KomorebiTraySetup
{
    static class Product
    {
        public const string Name = "komorebi-tray";
        public const string Version = "1.0.0";
        public const string Publisher = "komorebi-tray contributors";
        public const string Url = "https://github.com/LGUG2Z/komorebi";
        public const string ElevatedTask = "komorebi (elevated)";
    }

    // Where everything goes. Mirrors how the tray finds its files (KOMOREBI_CONFIG_HOME / WHKD_CONFIG_HOME).
    class Target
    {
        public bool Test;           // /root: files only, into a sandbox
        public string Home, ConfigDir, TrayDir, Whkdrc, Programs, Startup, RegKey;
        public string Exe { get { return Path.Combine(TrayDir, "komorebi-tray.exe"); } }
        public string Uninstaller { get { return Path.Combine(TrayDir, "uninstall.exe"); } }

        public static Target Create(string root)
        {
            var t = new Target { Test = root != null };
            if (t.Test)
            {
                t.Home = Path.GetFullPath(root);
                t.ConfigDir = Path.Combine(t.Home, @".config\komorebi");
                t.Whkdrc = Path.Combine(t.Home, @".config\whkdrc");
                t.Programs = Path.Combine(t.Home, "Programs");
                t.Startup = Path.Combine(t.Home, "Startup");
                t.RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.Name + "-test";
            }
            else
            {
                t.Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                t.ConfigDir = Env("KOMOREBI_CONFIG_HOME", Path.Combine(t.Home, @".config\komorebi"));
                t.Whkdrc = Path.Combine(Env("WHKD_CONFIG_HOME", Path.Combine(t.Home, ".config")), "whkdrc");
                t.Programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                t.Startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                t.RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.Name;
            }
            t.TrayDir = Path.Combine(t.ConfigDir, "tray");
            return t;
        }

        static string Env(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(v)) v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : Environment.ExpandEnvironmentVariables(v);
        }
    }

    static class Komorebi
    {
        public static string Find()
        {
            string fixedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"komorebi\bin\komorebic.exe");
            if (File.Exists(fixedPath)) return fixedPath;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { string p = Path.Combine(dir.Trim(), "komorebic.exe"); if (dir.Trim().Length > 0 && File.Exists(p)) return p; } catch { }
            return null;
        }

        public static string Version(string komorebic)
        {
            try
            {
                string exe = Path.Combine(Path.GetDirectoryName(komorebic), "komorebi.exe");
                var v = FileVersionInfo.GetVersionInfo(File.Exists(exe) ? exe : komorebic);
                string s = (v.ProductVersion ?? "").Trim();
                if (s.Length == 0) s = (v.FileVersion ?? "").Trim();
                if (s.Length > 0) return s;
                // Builds without a version resource: "komorebic 0.1.41" from --version
                var psi = new ProcessStartInfo(komorebic, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine() ?? "";
                    p.WaitForExit(3000);
                    string[] parts = line.Trim().Split(' ');
                    return parts.Length > 1 ? parts[1] : null;
                }
            }
            catch { return null; }
        }
    }

    static class Sys
    {
        public static int Run(string file, string args, int timeoutMs, Dictionary<string, string> env)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -2; }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // Shows one UAC prompt; returns -1 when it is declined
        public static int RunElevated(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) { p.WaitForExit(60000); return p.ExitCode; }
            }
            catch (Win32Exception) { return -1; }
        }

        public static void Kill(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); }
        }

        public static void Shortcut(string lnk, string target, string icon, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = s.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, s, new object[] { icon + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { description });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
                Marshal.FinalReleaseComObject(s);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static string ShortcutTarget(string lnk)
        {
            if (!File.Exists(lnk)) return null;
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = (string)s.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, s, null);
                Marshal.FinalReleaseComObject(s);
                return target;
            }
            catch { return null; }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static void WriteResource(string name, string path)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("missing payload: " + name);
                using (var f = File.Create(path)) s.CopyTo(f);
            }
        }

        public static bool TaskExists(string name) { return Run("schtasks.exe", "/query /tn \"" + name + "\"", 10000, null) == 0; }
    }

    // ---------------------------------------------------------------- install / uninstall work

    class Options
    {
        public bool Autostart = true, UseDefaults, Elevated, StartNow = true, RemoveConfig;
    }

    static class Installer
    {
        public static void Install(Target t, Options o, Action<string, double> report)
        {
            string komorebic = Komorebi.Find();
            if (komorebic == null && !t.Test)
            {
                report("Installing komorebi from its official winget package…", 0.05);
                int rc = Sys.Run("winget.exe", "install --id LGUG2Z.komorebi -e --silent --accept-source-agreements --accept-package-agreements", 600000, null);
                komorebic = Komorebi.Find();
                if (komorebic == null)
                    throw new Exception(rc == -1 ? "winget isn't available. Install komorebi from github.com/LGUG2Z/komorebi, then run setup again."
                                                 : "komorebi couldn't be installed (winget exit code " + rc + "). Install it from github.com/LGUG2Z/komorebi, then run setup again.");
            }

            if (!t.Test)
            {
                report("Closing the running tray…", 0.35);
                Sys.Kill("komorebi-tray");
                Sys.Kill("whkd");           // the tray registers the keybinds itself now
            }

            report("Copying files…", 0.45);
            Directory.CreateDirectory(t.TrayDir);
            Sys.WriteResource("komorebi-tray.exe", t.Exe);
            Sys.WriteResource("komorebi-tray.exe.config", t.Exe + ".config");
            string icon = Path.Combine(t.TrayDir, "komorebi-tray.ico");
            Sys.WriteResource("komorebi-tray.ico", icon);
            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(t.Uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, t.Uninstaller, true);

            report("Writing the config…", 0.55);
            Directory.CreateDirectory(Path.GetDirectoryName(t.Whkdrc));
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var pair in new[] { new[] { "komorebi.json", Path.Combine(t.ConfigDir, "komorebi.json") }, new[] { "whkdrc", t.Whkdrc } })
            {
                bool exists = File.Exists(pair[1]);
                if (exists && !o.UseDefaults) continue;
                if (exists) File.Copy(pair[1], pair[1] + ".before-komorebi-tray-" + stamp, true);
                Sys.WriteResource(pair[0], pair[1]);
            }

            report("Adding shortcuts…", 0.65);
            Directory.CreateDirectory(t.Programs);
            Directory.CreateDirectory(t.Startup);
            // Shortcuts from the old script kit pointed at the same exe under other names
            foreach (string old in new[] { Path.Combine(t.Programs, "komorebi.lnk"), Path.Combine(t.Startup, "komorebi tray.lnk") })
            {
                string target = Sys.ShortcutTarget(old);
                if (target != null && string.Equals(target, t.Exe, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
            }
            Sys.Shortcut(Path.Combine(t.Programs, "komorebi-tray.lnk"), t.Exe, icon, "komorebi tiling, keybinds and launcher");
            string startupLnk = Path.Combine(t.Startup, "komorebi-tray.lnk");
            if (o.Autostart) Sys.Shortcut(startupLnk, t.Exe, icon, "Starts komorebi with the tray at sign-in");
            else if (File.Exists(startupLnk)) File.Delete(startupLnk);

            report("Registering with Apps & Features…", 0.72);
            using (var k = Registry.CurrentUser.CreateSubKey(t.RegKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", Product.Version);
                k.SetValue("Publisher", Product.Publisher);
                k.SetValue("DisplayIcon", icon);
                k.SetValue("InstallLocation", t.TrayDir);
                k.SetValue("UninstallString", "\"" + t.Uninstaller + "\" /uninstall" + (t.Test ? " \"/root:" + t.Home + "\"" : ""));
                k.SetValue("QuietUninstallString", "\"" + t.Uninstaller + "\" /uninstall /S" + (t.Test ? " \"/root:" + t.Home + "\"" : ""));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 400, RegistryValueKind.DWord);
            }

            if (t.Test) { report("Done", 1); return; }

            report("Pointing komorebi at the config…", 0.78);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KOMOREBI_CONFIG_HOME", EnvironmentVariableTarget.User)))
                Environment.SetEnvironmentVariable("KOMOREBI_CONFIG_HOME", t.ConfigDir, EnvironmentVariableTarget.User);
            var env = new Dictionary<string, string> { { "KOMOREBI_CONFIG_HOME", t.ConfigDir } };
            if (!File.Exists(Path.Combine(t.ConfigDir, "applications.json")))
            {
                report("Downloading komorebi's app compatibility rules…", 0.8);
                Sys.Run(komorebic, "fetch-asc", 60000, env);
            }

            if (o.Elevated)
            {
                report("Setting up admin mode (Windows will ask for permission)…", 0.86);
                if (!CreateElevatedTask(komorebic)) report("Admin mode was skipped (permission declined)", 0.9);
            }

            if (o.StartNow)
            {
                report("Starting the tray…", 0.95);
                var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = false, WorkingDirectory = t.TrayDir };
                psi.EnvironmentVariables["KOMOREBI_CONFIG_HOME"] = t.ConfigDir;
                Process.Start(psi).Dispose();
            }
            report("Done", 1);
        }

        // The tray runs this on-demand task (if it exists) to start komorebi as admin without a UAC prompt each time
        static bool CreateElevatedTask(string komorebic)
        {
            string noConsole = Path.Combine(Path.GetDirectoryName(komorebic), "komorebic-no-console.exe");
            if (!File.Exists(noConsole)) noConsole = komorebic;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Starts komorebi as administrator for komorebi-tray, so it can tile admin windows.</Description></RegistrationInfo>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
"<AllowStartOnDemand>true</AllowStartOnDemand><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(noConsole) + "</Command><Arguments>start --clean-state</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "komorebi-elevated-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { return Sys.RunElevated("schtasks.exe", "/create /tn \"" + Product.ElevatedTask + "\" /xml \"" + file + "\" /f") == 0; }
            finally { try { File.Delete(file); } catch { } }
        }

        public static void Uninstall(Target t, Options o, Action<string, double> report)
        {
            if (!t.Test)
            {
                report("Stopping the tray and komorebi…", 0.1);
                Sys.Kill("komorebi-tray");
                string komorebic = Komorebi.Find();
                if (komorebic != null) Sys.Run(komorebic, "stop", 8000, null);   // brings every hidden window back
            }

            report("Removing shortcuts…", 0.3);
            foreach (string lnk in new[] { Path.Combine(t.Programs, "komorebi-tray.lnk"), Path.Combine(t.Startup, "komorebi-tray.lnk") })
                if (File.Exists(lnk)) File.Delete(lnk);

            report("Removing files…", 0.5);
            foreach (string f in new[] { t.Exe, t.Exe + ".config", Path.Combine(t.TrayDir, "komorebi-tray.ico") })
                if (File.Exists(f)) File.Delete(f);
            Registry.CurrentUser.DeleteSubKeyTree(t.RegKey, false);

            if (!t.Test && Sys.TaskExists(Product.ElevatedTask))
            {
                report("Removing admin mode (Windows will ask for permission)…", 0.7);
                Sys.RunElevated("schtasks.exe", "/delete /tn \"" + Product.ElevatedTask + "\" /f");
            }

            if (o.RemoveConfig)
            {
                report("Removing your config and keybinds…", 0.85);
                foreach (string f in new[] { "komorebi.json", "applications.json", "tray.log", "diag.txt" })
                {
                    string p = Path.Combine(t.ConfigDir, f);
                    if (File.Exists(p)) File.Delete(p);
                }
                if (File.Exists(t.Whkdrc)) File.Delete(t.Whkdrc);
                if (!t.Test && string.Equals(Environment.GetEnvironmentVariable("KOMOREBI_CONFIG_HOME", EnvironmentVariableTarget.User), t.ConfigDir, StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("KOMOREBI_CONFIG_HOME", null, EnvironmentVariableTarget.User);
            }

            // uninstall.exe can't delete itself while running: a hidden cmd does it once this process has exited
            string self = Application.ExecutablePath;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(t.Uninstaller), StringComparison.OrdinalIgnoreCase))
            {
                string cmd = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\" & rmdir \"" + t.TrayDir + "\" 2>nul";
                var psi = new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
                Process.Start(psi).Dispose();
            }
            else
            {
                if (File.Exists(t.Uninstaller)) File.Delete(t.Uninstaller);
                try { Directory.Delete(t.TrayDir); } catch { }   // only when empty
            }
            report("Done", 1);
        }
    }

    // ---------------------------------------------------------------- window

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(28, 28, 30);
        public static readonly Color Card = Color.FromArgb(36, 36, 40);
        public static readonly Color Border = Color.FromArgb(66, 66, 74);
        public static readonly Color Text = Color.FromArgb(236, 236, 240);
        public static readonly Color Dim = Color.FromArgb(150, 150, 162);
        public static readonly Color Accent = Color.FromArgb(192, 69, 23);
        public static readonly Color AccentHover = Color.FromArgb(214, 88, 40);
        public static readonly Color Good = Color.FromArgb(120, 200, 140);
        public static readonly Color Danger = Color.FromArgb(235, 100, 100);
        static readonly string Mono = PickMono();
        public static readonly Font Title = new Font(Mono, 18f, FontStyle.Bold);
        public static readonly Font Body = new Font(Mono, 10f, FontStyle.Bold);
        public static readonly Font Small = new Font(Mono, 8.75f);

        static string PickMono()
        {
            foreach (string n in new[] { "CaskaydiaMono Nerd Font", "JetBrainsMono Nerd Font", "Cascadia Mono", "JetBrains Mono", "Consolas" })
                using (var f = new Font(n, 10f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return "Consolas";
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    class Toggle
    {
        public string Label, Description;
        public bool Value, Locked;
        public Rectangle Bounds;
    }

    class SetupForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        enum Stage { Choose, Working, Done, Failed }

        readonly Target target;
        readonly bool uninstall;
        readonly List<Toggle> toggles = new List<Toggle>();
        readonly Toggle autostart, defaults, elevated, removeConfig;
        readonly string komorebic, komorebiVersion;
        readonly float S;
        Stage stage = Stage.Choose;
        string status = "", error;
        double progress;
        Rectangle primary, secondary;
        int hover = -1;     // 0 primary, 1 secondary, 10+ toggle index

        public SetupForm(Target t, bool uninstall)
        {
            target = t;
            this.uninstall = uninstall;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            TopMost = true;     // komorebi may retile other windows over it while it runs
            Text = uninstall ? "Uninstall komorebi-tray" : "komorebi-tray setup";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            ClientSize = new Size((int)(600 * S), (int)((uninstall ? 330 : 470) * S));
            Opacity = 0;

            komorebic = Komorebi.Find();
            komorebiVersion = komorebic != null ? Komorebi.Version(komorebic) : null;

            if (uninstall)
            {
                removeConfig = Add("Also remove my config and keybinds", "Deletes komorebi.json and whkdrc. komorebi itself stays installed.", false);
            }
            else
            {
                bool hasConfig = File.Exists(Path.Combine(t.ConfigDir, "komorebi.json")) || File.Exists(t.Whkdrc);
                autostart = Add("Start with Windows", "Tiling, keybinds and menus are ready when you sign in.", true);
                defaults = Add("Use the included config and keybinds",
                    hasConfig ? "Your current komorebi.json and whkdrc are backed up first." : "No existing config found, so these are installed.", !hasConfig);
                defaults.Locked = !hasConfig;
                elevated = Add("Run komorebi as administrator", "Tiles admin apps too. Windows asks for permission once.",
                    Sys.TaskExists(Product.ElevatedTask));
            }
        }

        Toggle Add(string label, string desc, bool value)
        {
            var tg = new Toggle { Label = label, Description = desc, Value = value };
            toggles.Add(tg);
            return tg;
        }

        int P(float v) { return (int)Math.Round(v * S); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, round = 2, color = Theme.Accent.R | (Theme.Accent.G << 8) | (Theme.Accent.B << 16);
            try
            {
                DwmSetWindowAttribute(Handle, 20, ref on, 4);
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                DwmSetWindowAttribute(Handle, 34, ref color, 4);
            }
            catch { }
        }

        // Same fade + rise the tray's menus use
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int finalTop = Top, rise = P(14);
            Top = finalTop + rise;
            Update();
            var clock = Stopwatch.StartNew();
            while (!IsDisposed)
            {
                double k = Math.Min(1, clock.Elapsed.TotalMilliseconds / 180), ease = 1 - Math.Pow(1 - k, 3);
                Opacity = ease;
                Top = finalTop + (int)Math.Round(rise * (1 - ease));
                if (k >= 1) break;
                if (DwmFlush() != 0) Thread.Sleep(4);
            }
            Opacity = 1; Top = finalTop;
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, x = P(36), y = P(32);
            using (var pen = new Pen(Theme.Accent, 2f)) g.DrawRectangle(pen, 1, 1, w - 2, ClientSize.Height - 2);

            DrawLogo(g, new RectangleF(x, y + P(2), P(30), P(30)));
            Label(g, uninstall ? "Uninstall komorebi-tray" : "komorebi-tray", Theme.Title, Theme.Text, x + P(44), y);
            y += P(44);
            Label(g, uninstall ? "Removes the tray, its shortcuts and its entry in Apps & Features."
                              : "Omarchy-style tray, keybind editor, settings and app launcher for komorebi.", Theme.Small, Theme.Dim, x, y);
            y += P(34);

            if (!uninstall)
            {
                // komorebi status card
                var card = new Rectangle(x, y, w - 2 * x, P(46));
                using (var p = Theme.Round(card, P(8))) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                bool found = komorebic != null;
                using (var b = new SolidBrush(found ? Theme.Good : Theme.Accent)) g.FillEllipse(b, card.X + P(16), card.Y + P(19), P(8), P(8));
                Label(g, found ? (komorebiVersion != null ? "komorebi " + komorebiVersion + " found" : "komorebi found") : "komorebi will be installed",
                     Theme.Body, Theme.Text, card.X + P(34), card.Y + P(6));
                Label(g, found ? Path.GetDirectoryName(Path.GetDirectoryName(komorebic)) : "From its official winget package (LGUG2Z.komorebi).",
                     Theme.Small, Theme.Dim, card.X + P(34), card.Y + P(25));
                y += P(66);
            }

            if (stage == Stage.Choose)
            {
                for (int i = 0; i < toggles.Count; i++)
                {
                    var tg = toggles[i];
                    tg.Bounds = new Rectangle(x - P(10), y - P(6), w - 2 * x + P(20), P(52));
                    if (hover == 10 + i && !tg.Locked)
                        using (var p = Theme.Round(tg.Bounds, P(8))) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                    Color fg = tg.Locked ? Theme.Dim : Theme.Text;
                    Label(g, tg.Label, Theme.Body, fg, x, y);
                    Label(g, tg.Description, Theme.Small, Theme.Dim, x, y + P(21));
                    DrawSwitch(g, new Rectangle(w - x - P(44), y + P(8), P(44), P(24)), tg.Value, tg.Locked);
                    y += P(60);
                }
            }
            else
            {
                y += P(6);
                Color c = stage == Stage.Failed ? Theme.Danger : stage == Stage.Done ? Theme.Good : Theme.Text;
                string msg = stage == Stage.Failed ? error : status;
                using (var b = new SolidBrush(c)) g.DrawString(msg, Theme.Body, b, new RectangleF(x, y, w - 2 * x, P(70)));
                y += P(70);
                var track = new Rectangle(x, y, w - 2 * x, P(4));
                using (var p = Theme.Round(track, P(2))) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                if (progress > 0)
                    using (var p = Theme.Round(new RectangleF(track.X, track.Y, Math.Max(P(4), (float)(track.Width * progress)), track.Height), P(2)))
                    using (var b = new SolidBrush(stage == Stage.Failed ? Theme.Danger : Theme.Accent)) g.FillPath(b, p);
                if (stage == Stage.Done && !uninstall)
                {
                    y += P(26);
                    Label(g, "Alt+K  keybinds     Alt+C  settings     Alt+Space  apps", Theme.Small, Theme.Dim, x, y);
                    Label(g, "Click the tray icon to stop or start everything.", Theme.Small, Theme.Dim, x, y + P(20));
                }
            }

            // buttons
            int bh = P(38), by = ClientSize.Height - P(28) - bh;
            string main = stage == Stage.Choose ? (uninstall ? "Uninstall" : File.Exists(target.Exe) ? "Update" : "Install")
                        : stage == Stage.Working ? null : "Close";
            primary = main == null ? Rectangle.Empty : new Rectangle(w - x - P(150), by, P(150), bh);
            secondary = stage == Stage.Choose ? new Rectangle(primary.X - P(12) - P(120), by, P(120), bh) : Rectangle.Empty;
            if (main != null) Button(g, primary, main + "  ↵", true, hover == 0);
            if (!secondary.IsEmpty) Button(g, secondary, "Cancel", false, hover == 1);
            if (stage == Stage.Choose) Label(g, "v" + Product.Version, Theme.Small, Theme.Dim, x, by + P(10));
        }

        static void DrawLogo(Graphics g, RectangleF r)
        {
            float gap = r.Width * 0.1f, mid = r.X + r.Width / 2, midY = r.Y + r.Height / 2, rad = r.Width * 0.1f;
            using (var b = new SolidBrush(Theme.Accent))
            {
                using (var p = Theme.Round(new RectangleF(r.X, r.Y, mid - gap / 2 - r.X, r.Height), rad)) g.FillPath(b, p);
                using (var p = Theme.Round(new RectangleF(mid + gap / 2, r.Y, r.Right - mid - gap / 2, midY - gap / 2 - r.Y), rad)) g.FillPath(b, p);
                using (var p = Theme.Round(new RectangleF(mid + gap / 2, midY + gap / 2, r.Right - mid - gap / 2, r.Bottom - midY - gap / 2), rad)) g.FillPath(b, p);
            }
        }

        void DrawSwitch(Graphics g, Rectangle r, bool on, bool locked)
        {
            Color track = on ? (locked ? Color.FromArgb(110, 60, 40) : Theme.Accent) : Theme.Border;
            using (var p = Theme.Round(r, r.Height / 2f)) using (var b = new SolidBrush(track)) g.FillPath(b, p);
            int d = r.Height - P(6);
            int kx = on ? r.Right - P(3) - d : r.X + P(3);
            using (var b = new SolidBrush(locked ? Theme.Dim : Color.White)) g.FillEllipse(b, kx, r.Y + P(3), d, d);
        }

        void Button(Graphics g, Rectangle r, string label, bool main, bool hot)
        {
            Color fill = main ? (hot ? Theme.AccentHover : Theme.Accent) : (hot ? Theme.Card : Theme.Bg);
            using (var p = Theme.Round(r, P(8)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (!main) using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, label, Theme.Body, r, main ? Color.White : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static void Label(Graphics g, string s, Font f, Color c, int x, int y)
        {
            TextRenderer.DrawText(g, s, f, new Point(x, y), c, TextFormatFlags.NoPrefix);
        }

        int HitTest(Point p)
        {
            if (primary.Contains(p)) return 0;
            if (secondary.Contains(p)) return 1;
            if (stage == Stage.Choose)
                for (int i = 0; i < toggles.Count; i++) if (toggles[i].Bounds.Contains(p)) return 10 + i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 && !(h >= 10 && toggles[h - 10].Locked) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h == 0) Primary();
            else if (h == 1) Close();
            else if (h >= 10) { var tg = toggles[h - 10]; if (!tg.Locked) { tg.Value = !tg.Value; Invalidate(); } }
            else if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }   // drag the window
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) Primary();
            else if (e.KeyCode == Keys.Escape && stage != Stage.Working) Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (stage == Stage.Working) { e.Cancel = true; return; }
            var clock = Stopwatch.StartNew();
            while (Opacity > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                double k = Math.Min(1, clock.Elapsed.TotalMilliseconds / 140);
                Opacity = 1 - k * k * k;
                if (k >= 1) break;
                if (DwmFlush() != 0) Thread.Sleep(4);
            }
            base.OnFormClosing(e);
        }

        void Primary()
        {
            if (stage == Stage.Done || stage == Stage.Failed) { Close(); return; }
            if (stage != Stage.Choose) return;
            stage = Stage.Working;
            hover = -1;
            Cursor = Cursors.Default;
            var o = new Options();
            if (uninstall) o.RemoveConfig = removeConfig.Value;
            else { o.Autostart = autostart.Value; o.UseDefaults = defaults.Value; o.Elevated = elevated.Value && !Sys.TaskExists(Product.ElevatedTask); }
            Report(uninstall ? "Uninstalling…" : "Installing…", 0.02);
            var worker = new Thread(() =>
            {
                try
                {
                    Action<string, double> rep = (s, p) => BeginInvoke((MethodInvoker)(() => Report(s, p)));
                    if (uninstall) Installer.Uninstall(target, o, rep); else Installer.Install(target, o, rep);
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        stage = Stage.Done;
                        Report(uninstall ? "komorebi-tray was removed." : "komorebi-tray is installed and running.", 1);
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)(() => { stage = Stage.Failed; error = ex.Message; Invalidate(); }));
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);   // WScript.Shell for the shortcuts
            worker.Start();
        }

        void Report(string s, double p)
        {
            if (s != "Done") status = s;
            progress = p;
            Invalidate();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = false, silent = false;
            string root = null;
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/uninstall" || l == "--uninstall") uninstall = true;
                else if (l == "/s" || l == "/quiet" || l == "--silent") silent = true;
                else if (l.StartsWith("/root:")) root = a.Substring(6);
            }
            var target = Target.Create(root);

            if (silent)
            {
                try
                {
                    var o = new Options { UseDefaults = !File.Exists(Path.Combine(target.ConfigDir, "komorebi.json")) && !File.Exists(target.Whkdrc) };
                    Action<string, double> quiet = (s, p) => { };
                    if (uninstall) Installer.Uninstall(target, o, quiet); else Installer.Install(target, o, quiet);
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "komorebi-tray-setup.log"), ex.ToString()); } catch { }
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(target, uninstall));
            return 0;
        }
    }
}
