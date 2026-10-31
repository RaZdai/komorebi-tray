// komorebi tray controller + keyboard shortcut editor.
// Native WinForms (.NET Framework 4.x, C# 5) so it idles at a few MB instead of a PowerShell host.
// Build with build.ps1 in this folder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KomorebiTray
{
    static class Native
    {
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();

        // Hand idle pages back to the OS; they fault back in only if used again
        public static void Trim()
        {
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                EmptyWorkingSet(new IntPtr(-1));
            }
            catch { }
        }

        public static void DarkTitleBar(Form f)
        {
            int on = 1;
            try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { }
        }

        // Win11: dark, rounded corners and a coloured DWM border on a borderless window
        public static void LauncherChrome(Form f, Color border)
        {
            int on = 1, round = 2, colorRef = border.R | (border.G << 8) | (border.B << 16);
            try
            {
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);
                DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                DwmSetWindowAttribute(f.Handle, 34, ref colorRef, 4);
            }
            catch { }
        }

        public static bool WinDown()
        {
            return GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        }
    }

    static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string ConfigDir = Env("KOMOREBI_CONFIG_HOME", Path.Combine(Home, @".config\komorebi"));
        public static readonly string Whkdrc = Path.Combine(Env("WHKD_CONFIG_HOME", Path.Combine(Home, ".config")), "whkdrc");
        public static readonly string Log = Path.Combine(ConfigDir, "tray.log");
        public static readonly string Icon = IconPath();
        public static readonly string Config = Path.Combine(ConfigDir, "komorebi.json");

        // The installer puts the icon next to the exe; older setups kept it in the config folder
        static string IconPath()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "komorebi-tray.ico");
            return File.Exists(local) ? local : Path.Combine(ConfigDir, "komorebi.ico");
        }

        static string Env(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }
    }

    static class Log
    {
        public static void Start()
        {
            try { File.WriteAllText(Paths.Log, "--- tray start " + DateTime.Now.ToString("s") + " | native\r\n"); } catch { }
        }
        public static void Write(string msg)
        {
            try { File.AppendAllText(Paths.Log, DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\r\n"); } catch { }
        }
    }

    static class Run
    {
        static string komorebic;

        static string Resolve(string exe, string fallback)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(';'))
            {
                if (dir.Length == 0) continue;
                try
                {
                    string p = Path.Combine(dir.Trim(), exe + ".exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return File.Exists(fallback) ? fallback : exe;
        }

        public static string Komorebic { get { return komorebic ?? (komorebic = Resolve("komorebic", @"C:\Program Files\komorebi\bin\komorebic.exe")); } }

        // Never wait on the whole process tree: 'start' leaves komorebi running as a descendant
        public static void Start(string file, string args, int waitMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (waitMs > 0 && p != null) p.WaitForExit(waitMs);
                }
            }
            catch (Exception e) { Log.Write(file + " " + args + " failed: " + e.Message); }
        }

        public const string ElevatedTask = "komorebi (elevated)";

        // true when the elevated task ran; false (task missing or refused) means: start komorebi normally
        public static bool StartElevatedKomorebi()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + ElevatedTask + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (p == null || !p.WaitForExit(5000)) return false;
                    if (p.ExitCode == 0) return true;
                }
            }
            catch (Exception e) { Log.Write("elevated start: " + e.Message); }
            return false;
        }

        public static void K(string args) { Start(Komorebic, args, 0); }
        public static void K(string args, int waitMs) { Start(Komorebic, args, waitMs); }

        public static bool IsRunning(string name)
        {
            Process[] ps = Process.GetProcessesByName(name);
            bool any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return any;
        }

        public static void KillAll(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
                finally { p.Dispose(); }
            }
        }

        // Whatever browser is the https default right now (so changing the default later just works)
        public static void OpenDefaultBrowser()
        {
            string exe = null;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                {
                    string progId = k == null ? null : k.GetValue("ProgId") as string;
                    if (progId != null)
                        using (var cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                            exe = ExeFrom(cmd == null ? null : cmd.GetValue("") as string);
                }
            }
            catch (Exception e) { Log.Write("browser lookup: " + e.Message); }
            Native.AllowSetForegroundWindow(-1);
            if (exe != null && File.Exists(exe)) Shell(exe, "");
            else Shell("explorer.exe", "https://www.google.com");
        }

        static string ExeFrom(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd.Substring(1, end - 1) : null; }
            int exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? cmd.Substring(0, exeEnd + 4) : null;
        }

        public static void Shell(string file, string args)
        {
            try { var p = Process.Start(file, args); if (p != null) p.Dispose(); }
            catch (Exception e) { Log.Write(file + " failed: " + e.Message); }
        }

        public static void OpenFile(string path)
        {
            string code = Resolve("code", "");
            string codeCmd = null;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "code.cmd"))) { codeCmd = Path.Combine(dir.Trim(), "code.cmd"); break; } }
                catch { }
            }
            if (File.Exists(code)) Start(code, "\"" + path + "\"", 0);
            else if (codeCmd != null) Start("cmd.exe", "/c \"\"" + codeCmd + "\" \"" + path + "\"\"", 0);
            else Shell("notepad.exe", "\"" + path + "\"");
        }
    }

    // ---------------------------------------------------------------- theme

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(28, 28, 30);
        public static readonly Color Surface = Color.FromArgb(38, 38, 42);
        public static readonly Color Hover = Color.FromArgb(50, 50, 56);
        public static readonly Color Border = Color.FromArgb(66, 66, 74);
        public static readonly Color Text = Color.FromArgb(236, 236, 240);
        public static readonly Color Dim = Color.FromArgb(150, 150, 162);
        public static readonly Color Accent = Color.FromArgb(192, 69, 23);
        public static readonly Color AccentHover = Color.FromArgb(214, 88, 40);
        public static readonly Color Danger = Color.FromArgb(235, 100, 100);

        public static readonly Font UI = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        public static readonly Font UISmall = new Font("Segoe UI", 8.75f);
        public static readonly Font UIBold = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        public static readonly Font Title = new Font("Segoe UI", 15f, FontStyle.Bold);
        public static readonly Font Section = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        public static readonly Font Big = new Font("Segoe UI", 12f, FontStyle.Bold);

        public static readonly Color TextSoft = Color.FromArgb(200, 200, 208);
        public static readonly Color Selection = Color.FromArgb(48, 40, 38);
        public static readonly Color KeyShadow = Color.FromArgb(14, 14, 16);

        // First installed of the monospace fonts Omarchy-style setups use
        static readonly string MonoFamily = PickMono();
        public static readonly Font Mono = new Font(MonoFamily, 10.5f, FontStyle.Bold);
        public static readonly Font MonoBig = new Font(MonoFamily, 13f, FontStyle.Bold);
        public static readonly Font MonoSmall = new Font(MonoFamily, 9f);

        static string PickMono()
        {
            foreach (string n in new[] { "CaskaydiaMono Nerd Font", "CaskaydiaCove Nerd Font", "JetBrainsMono Nerd Font", "Cascadia Mono", "JetBrains Mono", "Consolas" })
                using (var f = new Font(n, 10f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return "Consolas";
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = primary ? UIBold : UI,
                ForeColor = Text,
                BackColor = primary ? Accent : Surface,
                AutoSize = false,
                Height = 34,
                Width = Math.Max(96, TextRenderer.MeasureText(text, UIBold).Width + 32),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
            return b;
        }

        public static void DarkCombo(ComboBox c)
        {
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Surface;
            c.ForeColor = Text;
            c.Font = UI;
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = 24;
            c.MaxDropDownItems = 16;
            c.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(sel ? Hover : Surface)) e.Graphics.FillRectangle(b, e.Bounds);
                string t = c.GetItemText(c.Items[e.Index]);
                bool header = t.StartsWith("—");
                TextRenderer.DrawText(e.Graphics, header ? t.Trim('—', ' ').ToUpperInvariant() : t, header ? Section : UI,
                    new Rectangle(e.Bounds.X + (header ? 6 : 14), e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height),
                    header ? Dim : Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        public static TextBox DarkText()
        {
            return new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Surface, ForeColor = Text, Font = UI };
        }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(32, 32, 32);
        static readonly Color Hover = Color.FromArgb(60, 60, 60);
        static readonly Color Border = Color.FromArgb(72, 72, 72);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Bg; } }
    }

    // ---------------------------------------------------------------- whkdrc model

    class Line
    {
        public string Raw;          // used for comments, blanks, directives, app-specific blocks
        public bool IsBinding;
        public string Keys;         // whkd syntax, e.g. "alt + shift + left"
        public string Command;
        public string Section;
    }

    static class Whkdrc
    {
        public static List<Line> Parse(string[] lines)
        {
            var list = new List<Line>();
            string section = "General";
            bool inBlock = false;
            foreach (string raw in lines)
            {
                string t = raw.Trim();
                if (inBlock)
                {
                    list.Add(new Line { Raw = raw });
                    if (t == "]") inBlock = false;
                    continue;
                }
                if (t.StartsWith("#"))
                {
                    string h = t.TrimStart('#').Trim().Trim('-').Trim();
                    if (h.Length > 0) section = h;
                    list.Add(new Line { Raw = raw });
                    continue;
                }
                int colon = t.IndexOf(':');
                if (t.EndsWith("[")) { inBlock = true; list.Add(new Line { Raw = raw }); continue; }
                if (t.Length == 0 || t.StartsWith(".") || colon <= 0)
                {
                    list.Add(new Line { Raw = raw });
                    continue;
                }
                list.Add(new Line
                {
                    IsBinding = true,
                    Keys = Normalize(t.Substring(0, colon)),
                    Command = t.Substring(colon + 1).Trim(),
                    Section = section
                });
            }
            return list;
        }

        public static string Serialize(List<Line> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                if (!l.IsBinding) { sb.Append(l.Raw).Append("\r\n"); continue; }
                int pad = Math.Max(26, l.Keys.Length + 1);
                sb.Append(l.Keys.PadRight(pad)).Append(": ").Append(l.Command).Append("\r\n");
            }
            return sb.ToString();
        }

        static readonly string[] ModOrder = { "win", "alt", "ctrl", "shift" };

        // Canonical form so "shift+alt+X" and "alt + shift + x" compare equal
        public static string Normalize(string keys)
        {
            var mods = new List<string>();
            string key = "";
            foreach (string part in keys.Split('+'))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "control") p = "ctrl";
                if (p == "lwin" || p == "rwin") p = "win";
                if (p.Length == 0) continue;
                if (Array.IndexOf(ModOrder, p) >= 0) { if (!mods.Contains(p)) mods.Add(p); }
                else key = p;
            }
            mods.Sort((a, b) => Array.IndexOf(ModOrder, a) - Array.IndexOf(ModOrder, b));
            if (key.Length > 0) mods.Add(key);
            return string.Join(" + ", mods.ToArray());
        }

        static readonly Dictionary<string, string> Pretty = new Dictionary<string, string>
        {
            { "left", "←" }, { "right", "→" }, { "up", "↑" }, { "down", "↓" },
            { "return", "Enter" }, { "enter", "Enter" }, { "space", "Space" }, { "tab", "Tab" },
            { "escape", "Esc" }, { "back", "Backspace" }, { "delete", "Del" }, { "insert", "Ins" },
            { "home", "Home" }, { "end", "End" }, { "prior", "PgUp" }, { "next", "PgDn" },
            { "oem_period", "." }, { "oem_comma", "," }, { "oem_minus", "-" }, { "oem_plus", "=" },
            { "oem_1", ";" }, { "oem_2", "/" }, { "oem_3", "`" }, { "oem_4", "[" },
            { "oem_5", "\\" }, { "oem_6", "]" }, { "oem_7", "'" },
            { "win", "Win" }, { "alt", "Alt" }, { "ctrl", "Ctrl" }, { "shift", "Shift" }
        };

        public static string[] KeyCaps(string keys)
        {
            var caps = new List<string>();
            foreach (string part in keys.Split('+'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                string v;
                if (Pretty.TryGetValue(p, out v)) caps.Add(v);
                else if (p.StartsWith("numpad")) caps.Add("Num " + p.Substring(6));
                else caps.Add(p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p.Substring(1));
            }
            return caps.ToArray();
        }

        public static string PrettyKeys(string keys) { return string.Join(" + ", KeyCaps(keys)); }

        // WinForms key -> whkd key name (null = not supported)
        public static string KeyName(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return k.ToString().ToLowerInvariant();
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "numpad" + (int)(k - Keys.NumPad0);
            if (k >= Keys.F1 && k <= Keys.F24) return "f" + ((int)(k - Keys.F1) + 1);
            switch (k)
            {
                case Keys.Return: return "return";
                case Keys.Space: return "space";
                case Keys.Tab: return "tab";
                case Keys.Escape: return "escape";
                case Keys.Back: return "back";
                case Keys.Delete: return "delete";
                case Keys.Insert: return "insert";
                case Keys.Home: return "home";
                case Keys.End: return "end";
                case Keys.PageUp: return "prior";
                case Keys.PageDown: return "next";
                case Keys.Left: return "left";
                case Keys.Right: return "right";
                case Keys.Up: return "up";
                case Keys.Down: return "down";
                case Keys.OemPeriod: return "oem_period";
                case Keys.Oemcomma: return "oem_comma";
                case Keys.OemMinus: return "oem_minus";
                case Keys.Oemplus: return "oem_plus";
                case Keys.OemSemicolon: return "oem_1";
                case Keys.OemQuestion: return "oem_2";
                case Keys.Oemtilde: return "oem_3";
                case Keys.OemOpenBrackets: return "oem_4";
                case Keys.OemPipe: return "oem_5";
                case Keys.OemCloseBrackets: return "oem_6";
                case Keys.OemQuotes: return "oem_7";
            }
            return null;
        }
    }

    // ---------------------------------------------------------------- action catalog

    class ActionDef
    {
        public string Group, Label, Command;   // Command = exact command, or prefix when Options/FreeText
        public string[] Values, ValueLabels;
        public bool FreeText;
        public string Hint;

        public override string ToString() { return Label; }

        public string Build(int option, string text)
        {
            if (FreeText) return (Command.Length > 0 ? Command + " " : "") + text.Trim();
            if (Values != null) return Command + " " + Values[option];
            return Command;
        }
    }

    static class Catalog
    {
        public static readonly List<ActionDef> All = new List<ActionDef>();
        public static ActionDef RunApp, CustomKomorebic;

        static readonly string[] Dirs = { "left", "right", "up", "down" };
        static readonly string[] DirLabels = { "Left", "Right", "Up", "Down" };
        static readonly string[] NextPrev = { "next", "previous" };
        static readonly string[] NextPrevLabels = { "Next", "Previous" };

        static void Add(string group, string label, string cmd) { All.Add(new ActionDef { Group = group, Label = label, Command = cmd }); }
        static void Add(string group, string label, string cmd, string[] values, string[] labels)
        {
            All.Add(new ActionDef { Group = group, Label = label, Command = cmd, Values = values, ValueLabels = labels });
        }

        static Catalog()
        {
            string[] ws = new string[9], wsLabels = new string[9];
            for (int i = 0; i < 9; i++) { ws[i] = i.ToString(); wsLabels[i] = "Workspace " + (i + 1); }

            Add("Window", "Close window", "komorebic close");
            Add("Window", "Minimize window", "komorebic minimize");
            Add("Window", "Toggle floating", "komorebic toggle-float");
            Add("Window", "Toggle fullscreen (monocle)", "komorebic toggle-monocle");
            Add("Window", "Toggle maximize", "komorebic toggle-maximize");
            Add("Window", "Promote to main area", "komorebic promote");
            Add("Window", "Focus window", "komorebic focus", Dirs, DirLabels);
            Add("Window", "Move / swap window", "komorebic move", Dirs, DirLabels);
            Add("Window", "Stack with window", "komorebic stack", Dirs, DirLabels);
            Add("Window", "Unstack window", "komorebic unstack");
            Add("Window", "Cycle stack", "komorebic cycle-stack", NextPrev, NextPrevLabels);
            Add("Window", "Resize window", "komorebic resize-axis",
                new[] { "horizontal increase", "horizontal decrease", "vertical increase", "vertical decrease" },
                new[] { "Wider", "Narrower", "Taller", "Shorter" });

            Add("Layout", "Change layout", "komorebic change-layout",
                new[] { "bsp", "columns", "rows", "grid", "vertical-stack", "horizontal-stack", "right-main-vertical-stack", "ultrawide-vertical-stack" },
                new[] { "BSP (dwindle)", "Columns", "Rows", "Grid", "Vertical stack", "Horizontal stack", "Right main + stack", "Ultrawide stack" });
            Add("Layout", "Flip layout", "komorebic flip-layout",
                new[] { "horizontal", "vertical", "horizontal-and-vertical" }, new[] { "Horizontally", "Vertically", "Both ways" });
            Add("Layout", "Retile", "komorebic retile");

            Add("Workspace", "Go to workspace", "komorebic focus-workspace", ws, wsLabels);
            Add("Workspace", "Move window to workspace (follow)", "komorebic move-to-workspace", ws, wsLabels);
            Add("Workspace", "Send window to workspace (stay)", "komorebic send-to-workspace", ws, wsLabels);
            Add("Workspace", "Cycle workspaces", "komorebic cycle-workspace", NextPrev, NextPrevLabels);

            Add("Monitor", "Focus monitor", "komorebic cycle-monitor", NextPrev, NextPrevLabels);
            Add("Monitor", "Move window to monitor", "komorebic cycle-move-to-monitor", NextPrev, NextPrevLabels);

            Add("Session", "Pause / resume tiling", "komorebic toggle-pause");
            Add("Session", "Reload komorebi config", "komorebic reload-configuration");
            Add("Session", "Reload hotkeys", RestartWhkdCommand);
            Add("Session", "Open keyboard shortcuts editor", OpenEditorCommand);
            Add("Session", "Search apps (launcher)", OpenAppsCommand);
            Add("Session", "Open default web browser", OpenBrowserCommand);
            Add("Session", "Open komorebi settings", OpenSettingsCommand);

            RunApp = new ActionDef { Group = "Other", Label = "Open an app / run a command", Command = "", FreeText = true, Hint = "e.g.  wt   or   firefox   or   explorer" };
            CustomKomorebic = new ActionDef { Group = "Other", Label = "Other komorebic command", Command = "komorebic", FreeText = true, Hint = "e.g.  toggle-tiling" };
            All.Add(RunApp);
            All.Add(CustomKomorebic);
        }

        // Works under '.shell cmd': whkd itself is killed, the detached start brings it back
        public const string RestartWhkdCommand = "taskkill /f /im whkd.exe >nul & start \"\" /b whkd";

        static string exePath;
        public static string OpenEditorCommand
        {
            get
            {
                if (exePath == null) exePath = Application.ExecutablePath;
                return (exePath.Contains(" ") ? "\"" + exePath + "\"" : exePath) + " --shortcuts";
            }
        }
        public static string OpenAppsCommand { get { return OpenEditorCommand.Replace(" --shortcuts", " --apps"); } }
        public static string OpenBrowserCommand { get { return OpenEditorCommand.Replace(" --shortcuts", " --browser"); } }
        public static string OpenSettingsCommand { get { return OpenEditorCommand.Replace(" --shortcuts", " --settings"); } }

        public static ActionDef Match(string cmd, out int option, out string text)
        {
            option = 0; text = "";
            string c = cmd.Trim();
            foreach (var a in All)
            {
                if (a.FreeText) continue;
                if (a.Values == null) { if (string.Equals(c, a.Command, StringComparison.OrdinalIgnoreCase) || (a.Command == RestartWhkdCommand && c.Contains("whkd") && c.Contains("taskkill"))
                    || (a.Label == "Open keyboard shortcuts editor" && c.Contains("komorebi-tray") && c.Contains("--shortcuts"))
                    || (a.Label == "Search apps (launcher)" && c.Contains("komorebi-tray") && c.Contains("--apps"))
                    || (a.Label == "Open default web browser" && c.Contains("komorebi-tray") && c.Contains("--browser"))
                    || (a.Label == "Open komorebi settings" && c.Contains("komorebi-tray") && c.Contains("--settings"))) return a; continue; }
                for (int i = 0; i < a.Values.Length; i++)
                    if (string.Equals(c, a.Command + " " + a.Values[i], StringComparison.OrdinalIgnoreCase)) { option = i; return a; }
            }
            if (c.StartsWith("komorebic ", StringComparison.OrdinalIgnoreCase)) { text = c.Substring(10).Trim(); return CustomKomorebic; }
            text = c;
            return RunApp;
        }

        public static string Describe(string cmd)
        {
            int opt; string text;
            ActionDef a = Match(cmd, out opt, out text);
            if (a == RunApp) return "Open: " + text;
            if (a == CustomKomorebic) return "komorebic " + text;
            if (a.Values != null) return a.Label + "  ·  " + a.ValueLabels[opt];
            return a.Label;
        }
    }

    // ---------------------------------------------------------------- launcher-style list (Omarchy Super+K look)

    class Row
    {
        public string Keys;     // left column, e.g. "ALT + SHIFT + R"
        public string Text;
        public object Tag;      // Line, ActionDef or option index
        public bool Recording;
    }

    class LauncherList : Control
    {
        public readonly List<Row> Rows = new List<Row>();
        public int Selected;
        public int KeyColumn = 200;
        public bool Plain;                      // app launcher: icon (or letter badge) + name, no arrow column
        public Func<Row, Image> IconFor;
        public string Empty = "Nothing matches";
        int scroll;
        const int RowH = 32, Pad = 6, KeyX = 26;
        public static int Measure(string s)
        {
            return TextRenderer.MeasureText(s, Theme.Mono, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
        }

        // (row index, clicked on the keys column)
        public event Action<int, bool> Activated;

        public LauncherList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public Row SelectedRow { get { return Selected >= 0 && Selected < Rows.Count ? Rows[Selected] : null; } }

        int ArrowX { get { return KeyX + KeyColumn + 18; } }
        int MaxScroll { get { return Math.Max(0, Pad * 2 + Rows.Count * RowH - Height); } }

        public void Reload(int keepSelected)
        {
            Selected = Rows.Count == 0 ? -1 : Math.Max(0, Math.Min(Rows.Count - 1, keepSelected));
            scroll = Math.Min(scroll, MaxScroll);
            EnsureVisible();
            Invalidate();
        }

        public void MoveSelection(int delta)
        {
            if (Rows.Count == 0) return;
            Selected = Math.Max(0, Math.Min(Rows.Count - 1, Selected + delta));
            EnsureVisible();
            Invalidate();
        }

        void EnsureVisible()
        {
            if (Selected < 0) return;
            int y = Pad + Selected * RowH;
            if (y - Pad < scroll) scroll = Math.Max(0, y - Pad);
            else if (y + RowH + Pad > scroll + Height) scroll = Math.Min(MaxScroll, y + RowH + Pad - Height);
        }

        int IndexAt(int y)
        {
            int i = (y + scroll - Pad) / RowH;
            return y + scroll - Pad >= 0 && i < Rows.Count ? i : -1;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); scroll = Math.Min(scroll, MaxScroll); EnsureVisible(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH * 3));
            Invalidate();
        }

        // Hover moves the highlight, like a launcher
        public bool HoverLocked;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Y);
            if (!HoverLocked && i >= 0 && i != Selected) { Selected = i; Invalidate(); }
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = IndexAt(e.Y);
            if (e.Button == MouseButtons.Left && i >= 0 && Activated != null) Activated(i, e.X < ArrowX);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Rows.Count == 0)
            {
                TextRenderer.DrawText(g, Empty, Theme.Mono, new Rectangle(KeyX, Pad, Width - KeyX, RowH), Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            int arrowX = ArrowX, descX = arrowX + 30;
            int first = Math.Max(0, (scroll - Pad) / RowH);
            for (int i = first; i < Rows.Count; i++)
            {
                int y = Pad + i * RowH - scroll;
                if (y > Height) break;
                Row r = Rows[i];
                bool sel = i == Selected;

                if (sel)
                {
                    using (var path = Theme.Round(new RectangleF(10, y + 1, Width - 20, RowH - 2), 6))
                    using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, 10, y + 7, 3, RowH - 14);
                }

                Image icon = Plain && IconFor != null ? IconFor(r) : null;
                if (icon != null)
                {
                    g.DrawImage(icon, KeyX - 2, y + (RowH - icon.Height) / 2, icon.Width, icon.Height);
                    TextRenderer.DrawText(g, r.Text, Theme.Mono, new Rectangle(KeyX + RowH, y, Width - KeyX - RowH - 20, RowH), sel ? Theme.Text : Theme.TextSoft,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    continue;
                }

                if (Plain)
                {
                    var badge = new RectangleF(KeyX, y + 6, RowH - 12, RowH - 12);
                    using (var path = Theme.Round(badge, 4))
                    using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface)) g.FillPath(b, path);
                    TextRenderer.DrawText(g, r.Keys, Theme.Mono, Rectangle.Round(badge), sel ? Theme.Text : Theme.Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, r.Text, Theme.Mono, new Rectangle(KeyX + RowH, y, Width - KeyX - RowH - 20, RowH), sel ? Theme.Text : Theme.TextSoft,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    continue;
                }

                // Omarchy draws the whole row in one monospace face: keys, arrow, description
                if (!string.IsNullOrEmpty(r.Keys))
                    TextRenderer.DrawText(g, r.Keys, Theme.Mono, new Rectangle(KeyX, y, KeyColumn + 12, RowH),
                        r.Recording ? Theme.Accent : sel ? Theme.Text : Theme.TextSoft,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                TextRenderer.DrawText(g, "→", Theme.Mono, new Rectangle(arrowX, y, 28, RowH), Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, r.Text, Theme.Mono, new Rectangle(descX, y, Width - descX - 20, RowH), sel ? Theme.Text : Theme.TextSoft,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            int max = MaxScroll;
            if (max > 0)
            {
                int track = Height - 12;
                int th = Math.Max(30, (int)((long)track * Height / (Pad * 2 + Rows.Count * RowH)));
                int ty = 6 + (int)((long)(track - th) * scroll / max);
                using (var path = Theme.Round(new RectangleF(Width - 7, ty, 3, th), 1.5f))
                using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, path);
            }
        }
    }

    // ---------------------------------------------------------------- borderless launcher window base

    class LauncherForm : Form
    {
        public LauncherForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            KeyPreview = true;
            DoubleBuffered = true;
            Padding = new Padding(2);
            Opacity = 0;        // revealed by the intro animation in OnShown
        }

        // Quick fade + rise (ease-out cubic). Frames are paced by DwmFlush, so they land on the display's
        // refresh (240 Hz here); at full opacity WinForms drops the layered style, so there is no cost afterwards.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);        // subclasses fill their lists and take focus first
            const int Rise = 14;
            const double Duration = 180;
            int finalTop = Top;
            Top = finalTop + Rise;
            Update();
            var clock = Stopwatch.StartNew();
            while (!IsDisposed)
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration);
                double ease = 1 - Math.Pow(1 - t, 3);
                Opacity = ease;
                Top = finalTop + (int)Math.Round(Rise * (1 - ease));
                if (t >= 1) break;
                if (Native.DwmFlush() != 0) Thread.Sleep(4);
            }
            if (!IsDisposed) { Opacity = 1; Top = finalTop; }
        }

        // The intro in reverse (ease-in cubic, a little quicker) before the window goes away, whichever way it closes.
        // Runs before the FormClosing handlers, so saving and komorebi's reload happen once it has faded.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Visible && Opacity > 0 && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
            {
                const int Drop = 10;
                const double Duration = 140;
                double start = Opacity;
                int startTop = Top;
                var clock = Stopwatch.StartNew();
                while (!IsDisposed)
                {
                    double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration);
                    double ease = t * t * t;
                    Opacity = start * (1 - ease);
                    Top = startTop + (int)Math.Round(Drop * ease);
                    if (t >= 1) break;
                    if (Native.DwmFlush() != 0) Thread.Sleep(4);
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.LauncherChrome(this, Theme.Accent);
        }

        // 2px accent frame like Omarchy's launcher; DWM rounds the corners
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Accent, 2f)) e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }

    // ---------------------------------------------------------------- main editor window
    // One launcher window: rebind keys in place, or drill into nested lists to change the action.

    class KeybindEditor : LauncherForm
    {
        enum Mode { List, Record, PickAction, PickOption, FreeText }

        List<Line> lines;
        readonly LauncherList list = new LauncherList();
        readonly TextBox search = new TextBox();
        readonly Label hint = new Label();
        bool changed, closing, ignoreSearch;
        public bool Saved;

        Mode mode = Mode.List;
        Line target;            // binding being changed
        bool targetIsNew;       // added with Ctrl+N; dropped again if cancelled before it gets keys
        ActionDef picked;
        string recordMods = "";
        string status;          // one-line message in the footer (conflicts etc.)
        int bindingKeyColumn;

        public KeybindEditor()
        {
            Text = "komorebi · Keybindings";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 520);
            TopMost = true;
            if (File.Exists(Paths.Icon)) try { Icon = new Icon(Paths.Icon); } catch { }

            lines = Whkdrc.Parse(File.Exists(Paths.Whkdrc) ? File.ReadAllLines(Paths.Whkdrc) : new[] { ".shell cmd", "" });

            // Search line (always focused: typing filters straight away)
            var top = new Panel { Dock = DockStyle.Top, Width = ClientSize.Width, Height = 62, BackColor = Theme.Bg };
            search.BorderStyle = BorderStyle.None;
            search.BackColor = Theme.Bg;
            search.ForeColor = Theme.Text;
            search.Font = Theme.MonoBig;
            search.SetBounds(26, 22, ClientSize.Width - 52, 26);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            search.TextChanged += (s, e) => { if (!ignoreSearch && mode != Mode.Record) Rebuild(0); };
            top.Controls.Add(search);
            top.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, top.Height - 1, top.Width - 16, top.Height - 1); };

            hint.Dock = DockStyle.Bottom;
            hint.Height = 36;
            hint.BackColor = Theme.Bg;
            hint.Font = Theme.MonoSmall;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Padding = new Padding(24, 0, 0, 0);
            hint.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, 0, hint.Width - 16, 0); };

            list.Dock = DockStyle.Fill;
            list.Activated += OnActivated;
            int col = 0;
            foreach (var l in lines) if (l.IsBinding) col = Math.Max(col, LauncherList.Measure(KeysText(l.Keys)));
            bindingKeyColumn = Math.Min(col, LauncherList.Measure(new string('X', 35)));

            Controls.Add(list);
            Controls.Add(hint);
            Controls.Add(top);

            Shown += (s, e) => { SetMode(Mode.List, 0); Activate(); search.Focus(); };
            // Clicking away closes it, like a launcher
            Deactivate += (s, e) => { if (!closing && !IsDisposed) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) Close(); })); };
            FormClosing += (s, e) =>
            {
                closing = true;
                if (lines.RemoveAll(l => l.IsBinding && l.Keys.Length == 0) > 0) changed = true;
                if (changed) Save();
            };
            KeyDown += OnKey;
            KeyUp += (s, e) => { if (mode == Mode.Record) { recordMods = Mods(e.Modifiers); ShowLive(); } };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        void SetCue(string cue) { if (search.IsHandleCreated) SendMessage(search.Handle, 0x1501, (IntPtr)1, cue); }

        bool IsOpenKey(Keys keyData)
        {
            string name = Whkdrc.KeyName(keyData & Keys.KeyCode);
            if (name == null) return false;
            string mods = Mods(keyData);
            string pressed = Combo(mods.Length > 0 ? mods + " + " + name : name);
            foreach (var l in lines)
                if (l.IsBinding && l.Command != null && l.Command.Contains("komorebi-tray") && l.Command.Contains("--shortcuts")
                    && Combo(l.Keys) == pressed) return true;
            return false;
        }

        // Order-independent form of a whkd combo ("shift + alt + k" == "alt + shift + k")
        static string Combo(string keys)
        {
            var parts = new List<string>();
            foreach (string p in (keys ?? "").Split('+')) { string s = p.Trim().ToLowerInvariant(); if (s.Length > 0) parts.Add(s); }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("+", parts.ToArray());
        }

        static string KeysText(string keys) { return string.Join(" + ", Whkdrc.KeyCaps(keys)).ToUpperInvariant(); }

        static string Mods(Keys m)
        {
            var parts = new List<string>();
            if (Native.WinDown()) parts.Add("win");
            if ((m & Keys.Alt) != 0) parts.Add("alt");
            if ((m & Keys.Control) != 0) parts.Add("ctrl");
            if ((m & Keys.Shift) != 0) parts.Add("shift");
            return string.Join(" + ", parts.ToArray());
        }

        // ---- modes

        void SetMode(Mode m, int select)
        {
            mode = m;
            status = null;
            list.HoverLocked = m == Mode.Record;
            ignoreSearch = true;
            if (m != Mode.Record) search.Text = "";
            ignoreSearch = false;
            search.ReadOnly = m == Mode.Record;
            switch (m)
            {
                case Mode.List: SetCue("Keybindings"); break;
                case Mode.Record: SetCue(""); break;
                case Mode.PickAction: SetCue("Pick an action for " + (targetIsNew ? "the new keybinding" : KeysText(target.Keys))); break;
                case Mode.PickOption: SetCue(picked.Label); break;
                case Mode.FreeText: SetCue(picked.Hint ?? "Type a command"); break;
            }
            Rebuild(select);
        }

        void UpdateHint()
        {
            string h;
            switch (mode)
            {
                case Mode.Record: h = "press the new key combination    esc cancel"; break;
                case Mode.PickAction:
                case Mode.PickOption: h = "↵ choose    esc back"; break;
                case Mode.FreeText: h = "type the command    ↵ confirm    esc back"; break;
                default: h = "↵ rebind keys    tab change action    ctrl+n add    ctrl+del remove    ctrl+e edit as text    esc close"; break;
            }
            if (status != null) { hint.ForeColor = Theme.Danger; hint.Text = status; }
            else { hint.ForeColor = Theme.Dim; hint.Text = h + (changed && mode == Mode.List ? "    ● saves on close" : ""); }
        }

        void Rebuild(int select)
        {
            string[] terms = search.Text.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.Rows.Clear();

            if (mode == Mode.List || mode == Mode.Record)
            {
                list.KeyColumn = bindingKeyColumn;
                foreach (var l in lines)
                {
                    if (!l.IsBinding) continue;
                    string desc = l.Command.Length == 0 ? "" : Catalog.Describe(l.Command);
                    string keys = KeysText(l.Keys);
                    if (mode == Mode.List && !Matches(terms, desc + " " + keys + " " + l.Keys + " " + l.Command + " " + l.Section)) continue;
                    var row = new Row { Keys = keys, Text = desc, Tag = l };
                    if (mode == Mode.Record && l == target) { row.Recording = true; row.Keys = LiveKeys(); select = list.Rows.Count; }
                    list.Rows.Add(row);
                }
            }
            else if (mode == Mode.PickAction)
            {
                int col = 0;
                foreach (var a in Catalog.All)
                {
                    if (!Matches(terms, a.Group + " " + a.Label)) continue;
                    list.Rows.Add(new Row { Keys = a.Group.ToUpperInvariant(), Text = a.Label, Tag = a });
                    col = Math.Max(col, LauncherList.Measure(a.Group.ToUpperInvariant()));
                }
                list.KeyColumn = col;
            }
            else if (mode == Mode.PickOption)
            {
                string head = picked.Label.ToUpperInvariant();
                for (int i = 0; i < picked.ValueLabels.Length; i++)
                    if (Matches(terms, picked.ValueLabels[i]))
                        list.Rows.Add(new Row { Keys = head, Text = picked.ValueLabels[i], Tag = i });
                list.KeyColumn = LauncherList.Measure(head);
            }
            else if (mode == Mode.FreeText)
            {
                string head = picked.Label.ToUpperInvariant();
                string t = search.Text.Trim();
                list.Rows.Add(new Row { Keys = head, Text = t.Length > 0 ? picked.Build(0, t) : "…", Tag = null });
                list.KeyColumn = LauncherList.Measure(head);
            }

            list.Reload(select);
            UpdateHint();
        }

        static bool Matches(string[] terms, string hay)
        {
            hay = hay.ToLowerInvariant();
            foreach (string t in terms) if (hay.IndexOf(t) < 0) return false;
            return true;
        }

        int RowOf(object tag)
        {
            for (int i = 0; i < list.Rows.Count; i++) if (list.Rows[i].Tag == tag) return i;
            return 0;
        }

        // ---- input

        void OnActivated(int index, bool onKeys)
        {
            list.Selected = index;
            Row r = list.SelectedRow;
            if (r == null) return;
            switch (mode)
            {
                case Mode.List: if (onKeys) StartRecord((Line)r.Tag, false); else StartPick((Line)r.Tag); break;
                case Mode.PickAction: Pick((ActionDef)r.Tag); break;
                case Mode.PickOption: Finish(picked.Build((int)r.Tag, "")); break;
                case Mode.FreeText: if (search.Text.Trim().Length > 0) Finish(picked.Build(0, search.Text)); break;
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (mode == Mode.Record) return;   // handled in ProcessCmdKey
            bool handled = true;
            Row r = list.SelectedRow;
            if (e.KeyCode == Keys.Escape) { if (mode == Mode.List) Close(); else Back(); }
            else if (e.KeyCode == Keys.Down) list.MoveSelection(1);
            else if (e.KeyCode == Keys.Up) list.MoveSelection(-1);
            else if (e.KeyCode == Keys.PageDown) list.MoveSelection(10);
            else if (e.KeyCode == Keys.PageUp) list.MoveSelection(-10);
            else if (e.KeyCode == Keys.Enter) { if (r != null) OnActivated(list.Selected, true); }
            else if (e.KeyCode == Keys.Tab && mode == Mode.List) { if (r != null) StartPick((Line)r.Tag); }
            else if (mode == Mode.List && e.Control && e.KeyCode == Keys.N) { target = null; targetIsNew = true; SetMode(Mode.PickAction, 0); }
            else if (mode == Mode.List && e.Control && e.KeyCode == Keys.Delete) RemoveSelected();
            else if (mode == Mode.List && e.Control && e.KeyCode == Keys.E) { Run.OpenFile(Paths.Whkdrc); Close(); }
            else handled = false;
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        // Tab must reach OnKey instead of moving focus
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Tab && mode == Mode.List) { OnKey(this, new KeyEventArgs(keyData)); return true; }
            return base.ProcessDialogKey(keyData);
        }

        // While recording, every key (incl. Alt+F4, Tab, arrows) is swallowed and captured
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
            if (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN) return base.ProcessCmdKey(ref msg, keyData);
            // Hotkeys are off while this is open, so the key that opens it (Alt+K) has to close it here
            if (mode != Mode.Record && IsOpenKey(keyData)) { Close(); return true; }
            if (mode != Mode.Record) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            string mods = Mods(keyData);
            if (key == Keys.Escape && mods.Length == 0) { CancelRecord(); return true; }
            if (key == Keys.Menu || key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.LWin || key == Keys.RWin)
            {
                recordMods = mods; ShowLive(); return true;
            }
            string name = Whkdrc.KeyName(key);
            bool fkey = key >= Keys.F13 && key <= Keys.F24;
            if (name == null) { status = "That key can't be used — try another"; ShowLive(); return true; }
            if (mods.Length == 0 && !fkey) { status = "Hold Alt, Ctrl, Shift or Win together with a key"; ShowLive(); return true; }
            CommitRecord(Whkdrc.Normalize(mods + " + " + name));
            return true;
        }

        string LiveKeys() { return recordMods.Length > 0 ? KeysText(recordMods) + " + _" : "_"; }

        void ShowLive()
        {
            Row r = list.SelectedRow;
            if (r != null && r.Recording) { r.Keys = LiveKeys(); list.Invalidate(); }
            UpdateHint();
        }

        // ---- actions

        void StartRecord(Line l, bool isNew)
        {
            target = l;
            targetIsNew = isNew;
            recordMods = "";
            SetMode(Mode.Record, 0);
        }

        void CommitRecord(string keys)
        {
            Line other = FindByKeys(keys);
            if (other != null && other != target)
            {
                status = KeysText(keys) + " is already used by \"" + Catalog.Describe(other.Command) + "\"";
                recordMods = "";
                ShowLive();
                return;
            }
            if (target.Keys != keys) { target.Keys = keys; MarkChanged(); }
            Line done = target;
            target = null; targetIsNew = false;
            SetMode(Mode.List, 0);
            list.Reload(RowOf(done));
        }

        void CancelRecord()
        {
            Line l = target;
            if (targetIsNew) lines.Remove(l);
            target = null; targetIsNew = false;
            SetMode(Mode.List, 0);
            if (l != null) list.Reload(RowOf(l));
        }

        void StartPick(Line l)
        {
            target = l;
            targetIsNew = false;
            SetMode(Mode.PickAction, 0);
            int opt; string t;
            ActionDef current = Catalog.Match(l.Command, out opt, out t);
            list.Reload(RowOf(current));
        }

        void Pick(ActionDef a)
        {
            picked = a;
            if (a.Values != null)
            {
                SetMode(Mode.PickOption, 0);
                int opt; string t;
                if (target != null && Catalog.Match(target.Command, out opt, out t) == a) list.Reload(opt);
            }
            else if (a.FreeText)
            {
                SetMode(Mode.FreeText, 0);
                int opt; string t;
                if (target != null && Catalog.Match(target.Command, out opt, out t) == a) { search.Text = t; search.SelectAll(); }
            }
            else Finish(a.Build(0, ""));
        }

        void Finish(string command)
        {
            if (targetIsNew)
            {
                // New binding: place it next to similar ones, then record its keys on the spot
                var nl = new Line { IsBinding = true, Keys = "", Command = command };
                Insert(nl, picked.Group);
                StartRecord(nl, true);
                return;
            }
            if (target.Command != command) { target.Command = command; MarkChanged(); }
            Line done = target;
            target = null;
            SetMode(Mode.List, 0);
            list.Reload(RowOf(done));
        }

        void Back()
        {
            if (mode == Mode.PickOption || mode == Mode.FreeText)
            {
                ActionDef a = picked;
                SetMode(Mode.PickAction, 0);
                list.Reload(RowOf(a));
                return;
            }
            Line l = target;
            target = null; targetIsNew = false;
            SetMode(Mode.List, 0);
            if (l != null) list.Reload(RowOf(l));
        }

        void Insert(Line nl, string group)
        {
            int at = -1;
            string section = null;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].IsBinding || lines[i].Command.Length == 0) continue;
                int o; string t;
                if (Catalog.Match(lines[i].Command, out o, out t).Group == group) { at = i; section = lines[i].Section; }
            }
            if (at >= 0) { nl.Section = section; lines.Insert(at + 1, nl); return; }
            bool hasCustom = false;
            foreach (var l in lines) if (!l.IsBinding && l.Raw.Trim() == "# Custom") hasCustom = true;
            if (!hasCustom) { lines.Add(new Line { Raw = "" }); lines.Add(new Line { Raw = "# Custom" }); }
            nl.Section = "Custom";
            lines.Add(nl);
        }

        void RemoveSelected()
        {
            Row r = list.SelectedRow;
            if (r == null) return;
            int at = list.Selected;
            lines.Remove((Line)r.Tag);
            MarkChanged();
            Rebuild(at);
        }

        void MarkChanged() { changed = true; }

        Line FindByKeys(string keys)
        {
            if (keys.Length == 0) return null;
            foreach (var l in lines) if (l.IsBinding && l.Keys == keys) return l;
            return null;
        }

        void Save()
        {
            try
            {
                if (File.Exists(Paths.Whkdrc)) File.Copy(Paths.Whkdrc, Paths.Whkdrc + ".bak", true);
                File.WriteAllText(Paths.Whkdrc, Whkdrc.Serialize(lines), new UTF8Encoding(false));
                changed = false;
                Saved = true;
            }
            catch (Exception ex)
            {
                Log.Write("save failed: " + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- app launcher (Alt+Space)

    class AppEntry
    {
        public string Name, Id, Lower;
    }

    // Everything in shell:AppsFolder (Start menu, Store and desktop apps), cached as plain strings
    static class AppCatalog
    {
        public static List<AppEntry> Items = new List<AppEntry>();
        static DateTime loadedAt = DateTime.MinValue;
        static bool loading;

        public static bool Stale { get { return (DateTime.Now - loadedAt).TotalMinutes > 5; } }

        // Shell COM needs an STA thread; runs off the UI thread and reports back through 'done'
        public static void Refresh(Action done)
        {
            if (loading) return;
            loading = true;
            var t = new Thread(() =>
            {
                var list = new List<AppEntry>();
                try { Enumerate(list); }
                catch (Exception ex) { Log.Write("apps: " + ex.Message); }
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (list.Count > 0) { Items = list; loadedAt = DateTime.Now; }
                loading = false;
                if (done != null) done();
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static object Call(object o, string name, System.Reflection.BindingFlags kind, params object[] args)
        {
            return o.GetType().InvokeMember(name, kind, null, o, args);
        }

        static void Enumerate(List<AppEntry> list)
        {
            const System.Reflection.BindingFlags M = System.Reflection.BindingFlags.InvokeMethod, P = System.Reflection.BindingFlags.GetProperty;
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object folder = Call(shell, "NameSpace", M, "shell:AppsFolder");
            object items = Call(folder, "Items", M);
            int count = (int)Call(items, "Count", P);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                object item = Call(items, "Item", M, i);
                try
                {
                    string name = (string)Call(item, "Name", P), id = (string)Call(item, "Path", P);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || Junk(name, id) || !seen.Add(name)) continue;
                    list.Add(new AppEntry { Name = name, Id = id, Lower = name.ToLowerInvariant() });
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            Marshal.ReleaseComObject(items); Marshal.ReleaseComObject(folder); Marshal.ReleaseComObject(shell);
        }

        static bool Junk(string name, string id)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("uninstall") || n.Contains(" uninstall") || n.Contains("readme") || n.EndsWith(" help") ||
                   n.EndsWith(" website") || id.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> Ids()
        {
            var ids = new List<string>();
            foreach (var a in Items) ids.Add(a.Id);
            return ids;
        }

        // Explorer launches desktop and Store apps alike, outside the tray's process tree
        public static void Launch(AppEntry a)
        {
            Native.AllowSetForegroundWindow(-1);
            Run.Shell("explorer.exe", "shell:AppsFolder\\" + a.Id);
        }
        // Launcher-style ranking: prefix > word start > substring > in-order letters (no usage history)
        public static int Score(AppEntry a, string q)
        {
            int s;
            if (q.Length == 0) s = 1;
            else if (a.Lower.StartsWith(q)) s = 1000;
            else if (a.Lower.Contains(" " + q) || a.Lower.Contains("-" + q) || a.Lower.Contains("(" + q)) s = 800;
            else if (a.Lower.Contains(q)) s = 600;
            else
            {
                int j = 0;
                foreach (char c in a.Lower) if (j < q.Length && c == q[j]) j++;
                if (j < q.Length) return 0;
                s = 300;
            }
            return s - Math.Min(a.Name.Length, 60);
        }
    }

    // App icons from the shell (works for Store and desktop apps alike), loaded off the UI thread and cached
    // as small 24px bitmaps. The worker thread exits after a few idle seconds so nothing lingers.
    static class IconCache
    {
        public const int Size = 24;
        static readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();
        static readonly Queue<string> pending = new Queue<string>();
        static readonly HashSet<string> queued = new HashSet<string>();
        static readonly object gate = new object();
        static Thread worker;
        public static Action Loaded;    // called on the worker thread after each icon

        public static Bitmap Get(string id)
        {
            lock (gate)
            {
                Bitmap b;
                if (icons.TryGetValue(id, out b)) return b;
                Enqueue(id);
                return null;
            }
        }

        public static void Prefetch(IEnumerable<string> ids)
        {
            lock (gate) foreach (string id in ids) Enqueue(id);
        }

        static void Enqueue(string id)
        {
            if (!queued.Add(id)) return;
            pending.Enqueue(id);
            if (worker == null)
            {
                worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            Monitor.Pulse(gate);
        }

        static void Work()
        {
            while (true)
            {
                string id;
                lock (gate)
                {
                    while (pending.Count == 0)
                        if (!Monitor.Wait(gate, 5000) && pending.Count == 0) { worker = null; Native.Trim(); return; }   // done loading: hand the memory back
                    id = pending.Dequeue();
                }
                Bitmap b = Load(id);
                lock (gate) icons[id] = b;     // null is cached too: the launcher falls back to a letter badge
                Action cb = Loaded;
                if (cb != null && b != null) cb();
            }
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(NSize size, int flags, out IntPtr phbm); }

        [StructLayout(LayoutKind.Sequential)] struct NSize { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint f0, f1, f2; public IntPtr dshSection; public uint dsOffset; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

        static Bitmap Load(string id)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName("shell:AppsFolder\\" + id, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                // Ask for 48px and scale down: small sizes are often a blurry upscale of a 16px icon
                const int ICON_ONLY = 0x4, Source = 48;
                if (f.GetImage(new NSize { cx = Source, cy = Source }, ICON_ONLY, out hbm) != 0 || hbm == IntPtr.Zero) return null;

                var ds = new DIBSECTION();
                if (GetObject(hbm, Marshal.SizeOf(typeof(DIBSECTION)), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
                    using (var plain = Image.FromHbitmap(hbm)) return Scale(plain);
                int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;

                // Copy the pixels top-down (FromHbitmap would drop the alpha channel)
                var px = new int[w * h];
                bool bottomUp = ds.dsBmih.biHeight > 0;
                for (int y = 0; y < h; y++)
                    Marshal.Copy(ds.dsBm.bmBits + (bottomUp ? h - 1 - y : y) * stride, px, y * w, w);

                // The shell returns some icons premultiplied and some with straight alpha. A colour channel above
                // its alpha is impossible when premultiplied, so that tells them apart; reading straight alpha as
                // premultiplied is what produced the bright coloured specks on icon edges.
                bool straight = false;
                for (int i = 0; i < px.Length && !straight; i++)
                {
                    int a = (px[i] >> 24) & 0xFF;
                    if (((px[i] >> 16) & 0xFF) > a || ((px[i] >> 8) & 0xFF) > a || (px[i] & 0xFF) > a) straight = true;
                }
                var format = straight ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : System.Drawing.Imaging.PixelFormat.Format32bppPArgb;
                using (var full = new Bitmap(w, h, format))
                {
                    var data = full.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, format);
                    for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, data.Scan0 + y * data.Stride, w);
                    full.UnlockBits(data);
                    return Scale(full);
                }
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        // High-quality downscale to the row size; drawing it later is then a plain 1:1 blit
        static Bitmap Scale(Image src)
        {
            var dst = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dst))
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                attrs.SetWrapMode(WrapMode.TileFlipXY);   // no dark halo from sampling outside the edges
                g.DrawImage(src, new Rectangle(0, 0, Size, Size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            return dst;
        }
    }

    class AppLauncher : LauncherForm
    {
        int repaintQueued;
        readonly LauncherList list = new LauncherList();
        readonly TextBox search = new TextBox();
        readonly Label hint = new Label();
        bool closing;
        const int MaxRows = 60;

        public AppLauncher()
        {
            Text = "komorebi · Apps";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 440);
            TopMost = true;

            var top = new Panel { Dock = DockStyle.Top, Width = ClientSize.Width, Height = 62, BackColor = Theme.Bg };
            search.BorderStyle = BorderStyle.None;
            search.BackColor = Theme.Bg;
            search.ForeColor = Theme.Text;
            search.Font = Theme.MonoBig;
            search.SetBounds(26, 22, ClientSize.Width - 52, 26);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            search.TextChanged += (s, e) => Rebuild();
            search.HandleCreated += (s, e) => SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search apps");
            top.Controls.Add(search);
            top.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, top.Height - 1, top.Width - 16, top.Height - 1); };

            hint.Dock = DockStyle.Bottom;
            hint.Height = 36;
            hint.BackColor = Theme.Bg;
            hint.ForeColor = Theme.Dim;
            hint.Font = Theme.MonoSmall;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Padding = new Padding(24, 0, 0, 0);
            hint.Text = "↵ open    ↑↓ move    esc close";
            hint.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, 0, hint.Width - 16, 0); };

            list.Dock = DockStyle.Fill;
            list.Plain = true;
            list.IconFor = r => IconCache.Get(((AppEntry)r.Tag).Id);
            // Icons arrive from a worker thread; coalesce them into one repaint per UI tick
            IconCache.Loaded = () =>
            {
                if (Interlocked.Exchange(ref repaintQueued, 1) == 0)
                    try { BeginInvoke((MethodInvoker)(() => { repaintQueued = 0; list.Invalidate(); })); } catch { }
            };
            FormClosed += (s, e) => IconCache.Loaded = null;
            list.Activated += (i, onKeys) => { list.Selected = i; LaunchSelected(); };

            Controls.Add(list);
            Controls.Add(hint);
            Controls.Add(top);

            Shown += (s, e) =>
            {
                Rebuild();
                Activate();
                search.Focus();
                if (AppCatalog.Items.Count == 0 || AppCatalog.Stale)
                    AppCatalog.Refresh(() => { try { BeginInvoke((MethodInvoker)Rebuild); } catch { } });
            };
            Deactivate += (s, e) => { if (!closing && !IsDisposed) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) Close(); })); };
            FormClosing += (s, e) => closing = true;
            KeyDown += (s, e) =>
            {
                bool handled = true;
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Down) list.MoveSelection(1);
                else if (e.KeyCode == Keys.Up) list.MoveSelection(-1);
                else if (e.KeyCode == Keys.PageDown) list.MoveSelection(10);
                else if (e.KeyCode == Keys.PageUp) list.MoveSelection(-10);
                else if (e.KeyCode == Keys.Enter) LaunchSelected();
                else handled = false;
                if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        void Rebuild()
        {
            if (IsDisposed) return;
            string q = search.Text.Trim().ToLowerInvariant();
            var scored = new List<KeyValuePair<int, AppEntry>>();
            foreach (var a in AppCatalog.Items)
            {
                int s = AppCatalog.Score(a, q);
                if (s > 0) scored.Add(new KeyValuePair<int, AppEntry>(s, a));
            }
            scored.Sort((x, y) => y.Key != x.Key ? y.Key - x.Key : string.Compare(x.Value.Name, y.Value.Name, StringComparison.OrdinalIgnoreCase));

            list.Rows.Clear();
            for (int i = 0; i < scored.Count && i < MaxRows; i++)
            {
                AppEntry a = scored[i].Value;
                list.Rows.Add(new Row { Keys = a.Name.Substring(0, 1).ToUpperInvariant(), Text = a.Name, Tag = a });
            }
            list.Empty = AppCatalog.Items.Count == 0 ? "Loading apps…" : "No matching apps";
            list.Reload(0);
        }

        void LaunchSelected()
        {
            Row r = list.SelectedRow;
            if (r == null) return;
            AppCatalog.Launch((AppEntry)r.Tag);
            Close();
        }
    }

    // ---------------------------------------------------------------- komorebi.json settings (Alt+C)

    // Minimal JSON round-trip: parse with the framework, write back pretty-printed in the original key order.
    // Only the values the user changes are touched; every other key survives as-is.
    static class Json
    {
        public static Dictionary<string, object> Parse(string text)
        {
            return (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        }

        public static string Write(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o, 0);
            return sb.Append("\r\n").ToString();
        }

        static void WriteValue(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            var map = o as IDictionary<string, object>;
            if (map != null)
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\r\n");
                int i = 0;
                foreach (var kv in map)
                {
                    sb.Append(' ', (depth + 1) * 2);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    WriteValue(sb, kv.Value, depth + 1);
                    sb.Append(++i < map.Count ? ",\r\n" : "\r\n");
                }
                sb.Append(' ', depth * 2).Append('}');
                return;
            }
            var list = o as System.Collections.IList;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\r\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(' ', (depth + 1) * 2);
                    WriteValue(sb, list[i], depth + 1);
                    sb.Append(i + 1 < list.Count ? ",\r\n" : "\r\n");
                }
                sb.Append(' ', depth * 2).Append(']');
                return;
            }
            sb.Append(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        // Path like "animation.duration.movement" or "monitors.0.workspaces.2.layout"
        public static object Get(object root, string path)
        {
            object cur = root;
            foreach (string seg in path.Split('.'))
            {
                int idx;
                var map = cur as IDictionary<string, object>;
                var list = cur as System.Collections.IList;
                if (map != null) { if (!map.TryGetValue(seg, out cur)) return null; }
                else if (list != null && int.TryParse(seg, out idx) && idx < list.Count) cur = list[idx];
                else return null;
            }
            return cur;
        }

        public static void Set(Dictionary<string, object> root, string path, object value)
        {
            string[] segs = path.Split('.');
            object cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                var map = (IDictionary<string, object>)cur;
                object next;
                if (!map.TryGetValue(segs[i], out next) || next == null) { next = new Dictionary<string, object>(); map[segs[i]] = next; }
                else if (next is System.Collections.IList)
                {
                    cur = ((System.Collections.IList)next)[int.Parse(segs[++i])];
                    continue;
                }
                else if (!(next is IDictionary<string, object>))
                {
                    // e.g. "duration": 200 -> {"movement": 200, "transparency": 200} before setting one of them
                    var split = new Dictionary<string, object> { { "movement", next }, { "transparency", next } };
                    map[segs[i]] = split;
                    next = split;
                }
                cur = next;
            }
            ((IDictionary<string, object>)cur)[segs[segs.Length - 1]] = value;
        }
    }

    class Setting
    {
        public string Group, Label, Description, Path, Kind;   // Kind: bool | int | enum | color
        public string[] Options;
        public int Min, Max, Step;
        public string Unit = "";
        public object Default;
        public string DependsOn;    // dimmed while this boolean path is off
    }

    static class SettingCatalog
    {
        public static readonly string[] Sections = { "Gaps", "Borders", "Transparency", "Animation", "Behaviour", "Workspaces" };

        static readonly string[] Easings = {
            "Linear", "EaseInSine", "EaseOutSine", "EaseInOutSine", "EaseInQuad", "EaseOutQuad", "EaseInOutQuad",
            "EaseInCubic", "EaseOutCubic", "EaseInOutCubic", "EaseInQuart", "EaseOutQuart", "EaseInOutQuart",
            "EaseInQuint", "EaseOutQuint", "EaseInOutQuint", "EaseInExpo", "EaseOutExpo", "EaseInOutExpo",
            "EaseInCirc", "EaseOutCirc", "EaseInOutCirc", "EaseInBack", "EaseOutBack", "EaseInOutBack",
            "EaseInElastic", "EaseOutElastic", "EaseInOutElastic", "EaseInBounce", "EaseOutBounce", "EaseInOutBounce" };
        static readonly string[] Layouts = { "BSP", "Columns", "Rows", "VerticalStack", "HorizontalStack", "UltrawideVerticalStack", "Grid", "RightMainVerticalStack", "Scrolling" };

        static Setting S(string g, string kind, string label, string desc, string path, object def)
        {
            return new Setting { Group = g, Kind = kind, Label = label, Description = desc, Path = path, Default = def };
        }
        static Setting N(string g, string label, string desc, string path, int def, int min, int max, int step, string unit)
        {
            var s = S(g, "int", label, desc, path, def); s.Min = min; s.Max = max; s.Step = step; s.Unit = unit; return s;
        }
        static Setting E(string g, string label, string desc, string path, string def, string[] opts)
        {
            var s = S(g, "enum", label, desc, path, def); s.Options = opts; return s;
        }
        static Setting D(Setting s, string dependsOn) { s.DependsOn = dependsOn; return s; }

        // Defaults are komorebi's own (from its schema), shown when a key isn't in the file yet
        public static List<Setting> For(Dictionary<string, object> cfg)
        {
            var s = new List<Setting>
            {
                N("Gaps", "Screen edge gap", "Space between windows and the edges of the screen", "default_workspace_padding", 10, 0, 100, 2, " px"),
                N("Gaps", "Gap between windows", "Space between neighbouring tiled windows", "default_container_padding", 10, 0, 100, 2, " px"),

                S("Borders", "bool", "Borders", "Draw a coloured outline around windows", "border", false),
                D(N("Borders", "Width", "Thickness of the outline", "border_width", 8, 0, 20, 1, " px"), "border"),
                D(N("Borders", "Offset", "Push the outline outwards (+) or inwards (−)", "border_offset", -1, -10, 20, 1, " px"), "border"),
                D(E("Borders", "Corners", "Shape of the outline's corners", "border_style", "System", new[] { "System", "Rounded", "Square" }), "border"),
                D(E("Borders", "Drawn by", "Komorebi's own outline, or Windows' thin native one", "border_implementation", "Komorebi", new[] { "Komorebi", "Windows" }), "border"),
                D(S("Borders", "color", "Focused window", "Outline of the window you're working in", "border_colours.single", "#42A5F5"), "border"),
                D(S("Borders", "color", "Other windows", "Outline of every other tiled window", "border_colours.unfocused", "#808080"), "border"),
                D(S("Borders", "color", "Floating windows", "Outline of windows floating above the tiles", "border_colours.floating", "#F5F5F5"), "border"),
                D(S("Borders", "color", "Stacked windows", "Outline when several windows share one tile", "border_colours.stack", "#00A542"), "border"),
                D(S("Borders", "color", "Monocle", "Outline when one window fills the workspace", "border_colours.monocle", "#FF3399"), "border"),

                S("Transparency", "bool", "Dim inactive windows", "Make windows you're not using slightly see-through", "transparency", false),
                D(N("Transparency", "Inactive opacity", "How solid inactive windows stay", "transparency_alpha", 200, 50, 255, 5, ""), "transparency"),

                S("Animation", "bool", "Animations", "Slide windows smoothly instead of jumping", "animation.enabled", false),
                D(N("Animation", "Frame rate", "Smoothest when it divides your refresh rate", "animation.fps", 60, 30, 240, 10, " fps"), "animation.enabled"),
                D(N("Animation", "Move duration", "How long a window takes to slide into place", "animation.duration.movement", 250, 0, 1000, 10, " ms"), "animation.enabled"),
                D(E("Animation", "Move curve", "How the slide speeds up and settles", "animation.style.movement", "Linear", Easings), "animation.enabled"),
                D(N("Animation", "Fade duration", "How long dimming takes when focus changes", "animation.duration.transparency", 250, 0, 1000, 10, " ms"), "animation.enabled"),
                D(E("Animation", "Fade curve", "How the fade speeds up and settles", "animation.style.transparency", "Linear", Easings), "animation.enabled"),

                S("Behaviour", "bool", "Mouse follows focus", "Cursor jumps to windows you focus by keyboard", "mouse_follows_focus", true),
                E("Behaviour", "Hide windows by", "How other workspaces' windows are hidden", "window_hiding_behaviour", "Cloak", new[] { "Cloak", "Hide", "Minimize" }),
                E("Behaviour", "Across monitors", "When a window moves onto another monitor", "cross_monitor_move_behaviour", "Swap", new[] { "Swap", "Insert", "NoOp" }),
                N("Behaviour", "Resize step", "How much one resize shortcut changes a window", "resize_delta", 50, 10, 300, 10, " px"),
            };

            var monitors = Json.Get(cfg, "monitors") as System.Collections.IList;
            if (monitors != null)
                for (int m = 0; m < monitors.Count; m++)
                {
                    var wss = Json.Get(monitors[m], "workspaces") as System.Collections.IList;
                    if (wss == null) continue;
                    for (int w = 0; w < wss.Count; w++)
                    {
                        string name = Json.Get(wss[w], "name") as string ?? (w + 1).ToString();
                        s.Add(E("Workspaces", "Workspace " + name, "Layout new windows tile into" + (monitors.Count > 1 ? " · monitor " + (m + 1) : ""),
                            "monitors." + m + ".workspaces." + w + ".layout", "BSP", Layouts));
                    }
                }
            return s;
        }
    }

    // komorebi.json as a settings screen: section sidebar, one row per setting with a description and a
    // fitting control (switch, slider, segmented choice, spinner, colour). Same launcher look as Alt+K/Alt+Space.
    class SettingsEditor : LauncherForm
    {
        public enum Mode { Browse, Pick, Input }

        readonly Dictionary<string, object> cfg;
        readonly List<Setting> all;
        readonly TextBox search = new TextBox();
        readonly Label hint = new Label();
        readonly SettingsView view;
        bool changed, closing, ignoreSearch, appliedOnce;
        public bool Saved;
        string loadError, status;

        public Mode CurrentMode = Mode.Browse;
        public int Section;
        public int Selected;
        public int PickIndex;
        public Setting Editing;
        public readonly List<Setting> Rows = new List<Setting>();

        public SettingsEditor()
        {
            Text = "komorebi · Settings";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 560);
            TopMost = true;
            if (File.Exists(Paths.Icon)) try { Icon = new Icon(Paths.Icon); } catch { }

            try { cfg = Json.Parse(File.ReadAllText(Paths.Config)); }
            catch (Exception e) { cfg = new Dictionary<string, object>(); loadError = "Couldn't read komorebi.json: " + e.Message; }
            all = SettingCatalog.For(cfg);

            var top = new Panel { Dock = DockStyle.Top, Width = ClientSize.Width, Height = 62, BackColor = Theme.Bg };
            search.BorderStyle = BorderStyle.None;
            search.BackColor = Theme.Bg;
            search.ForeColor = Theme.Text;
            search.Font = Theme.MonoBig;
            search.SetBounds(26, 22, ClientSize.Width - 52, 26);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            search.TextChanged += (s, e) => { if (!ignoreSearch) { if (CurrentMode == Mode.Browse) { Selected = 0; Refresh2(); } else view.Invalidate(); } };
            top.Controls.Add(search);
            top.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, top.Height - 1, top.Width - 16, top.Height - 1); };

            hint.Dock = DockStyle.Bottom;
            hint.Height = 36;
            hint.BackColor = Theme.Bg;
            hint.Font = Theme.MonoSmall;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Padding = new Padding(24, 0, 0, 0);
            hint.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, 0, hint.Width - 16, 0); };

            view = new SettingsView(this) { Dock = DockStyle.Fill };

            Controls.Add(view);
            Controls.Add(hint);
            Controls.Add(top);

            Shown += (s, e) => { SetCue("Search settings"); Refresh2(); Activate(); search.Focus(); };
            Deactivate += (s, e) => { if (!closing && !IsDisposed) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) Close(); })); };
            FormClosing += (s, e) => { closing = true; if (changed) Save(); };
            KeyDown += OnKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        void SetCue(string cue) { if (search.IsHandleCreated) SendMessage(search.Handle, 0x1501, (IntPtr)1, cue); }

        public bool Searching { get { return search.Text.Trim().Length > 0 && CurrentMode == Mode.Browse; } }
        public string Typed { get { return search.Text.Trim(); } }

        // ---- values

        public object Value(Setting s) { return Json.Get(cfg, s.Path) ?? s.Default; }
        public bool Bool(Setting s) { return Convert.ToBoolean(Value(s)); }
        public int Int(Setting s) { return Convert.ToInt32(Value(s)); }
        public string Str(Setting s) { return Convert.ToString(Value(s)); }
        public bool IsActive(Setting s) { return s.DependsOn == null || Convert.ToBoolean(Json.Get(cfg, s.DependsOn) ?? false); }

        public string Display(Setting s)
        {
            if (s.Kind == "bool") return Bool(s) ? "On" : "Off";
            if (s.Kind == "int") return s.Path == "transparency_alpha" ? (int)Math.Round(Int(s) * 100 / 255.0) + "%" : Int(s) + s.Unit;
            return Str(s);
        }

        public static bool TryColor(string text, out Color c)
        {
            c = Color.Empty;
            string t = (text ?? "").Trim().TrimStart('#');
            int rgb;
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out rgb)) return false;
            c = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }

        public void Change(Setting s, object value)
        {
            if (Equals(Value(s), value)) return;
            Json.Set(cfg, s.Path, value);
            changed = true;
            UpdateHint();
            view.Invalidate();
        }

        public void SetInt(Setting s, int v) { Change(s, Math.Max(s.Min, Math.Min(s.Max, (int)Math.Round((double)(v - s.Min) / s.Step) * s.Step + s.Min))); }

        // ---- rows

        public void Refresh2()
        {
            Rows.Clear();
            if (Searching)
            {
                string[] terms = Typed.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in all)
                {
                    string hay = (s.Group + " " + s.Label + " " + s.Description + " " + Display(s) + " " + s.Path.Replace('_', ' ').Replace('.', ' ')
                        + (s.Kind == "color" ? " colour color" : "") + (s.Path.Contains("padding") ? " padding margin spacing" : "")
                        + (s.Path.StartsWith("transparency") ? " opacity alpha transparent" : "")).ToLowerInvariant();
                    bool ok = true;
                    foreach (string t in terms) if (hay.IndexOf(t) < 0) { ok = false; break; }
                    if (ok) Rows.Add(s);
                }
            }
            else foreach (var s in all) if (s.Group == SettingCatalog.Sections[Section]) Rows.Add(s);
            Selected = Math.Max(0, Math.Min(Rows.Count - 1, Selected));
            view.EnsureVisible();
            view.Invalidate();
            UpdateHint();
        }

        public Setting Current { get { return Selected >= 0 && Selected < Rows.Count ? Rows[Selected] : null; } }

        public void GoSection(int i)
        {
            Section = (i + SettingCatalog.Sections.Length) % SettingCatalog.Sections.Length;
            ignoreSearch = true; search.Text = ""; ignoreSearch = false;
            Selected = 0;
            Refresh2();
        }

        // ---- actions

        public void Adjust(int dir, bool big)
        {
            Setting s = Current;
            if (s == null) return;
            if (s.Kind == "bool") Change(s, !Bool(s));
            else if (s.Kind == "int") SetInt(s, Int(s) + dir * s.Step * (big ? 5 : 1));
            else if (s.Kind == "enum")
            {
                int i = Array.IndexOf(s.Options, Str(s));
                Change(s, s.Options[((i < 0 ? 0 : i) + dir + s.Options.Length) % s.Options.Length]);
            }
        }

        public void EditCurrent()
        {
            Setting s = Current;
            if (s == null) return;
            if (s.Kind == "bool") Change(s, !Bool(s));
            else if (s.Kind == "enum" && s.Options.Length <= 4) Adjust(1, false);
            else if (s.Kind == "enum") OpenPick(s);
            else OpenInput(s);
        }

        public void OpenPick(Setting s)
        {
            Editing = s;
            CurrentMode = Mode.Pick;
            PickIndex = Math.Max(0, Array.IndexOf(s.Options, Str(s)));
            status = null;
            view.Invalidate();
            UpdateHint();
        }

        public void OpenInput(Setting s)
        {
            Editing = s;
            CurrentMode = Mode.Input;
            status = null;
            SetCue(s.Kind == "color" ? "Type a colour, e.g. #C04517" : "Type a number from " + s.Min + " to " + s.Max);
            ignoreSearch = true;
            search.Text = s.Kind == "color" ? Str(s) : Int(s).ToString();
            search.SelectAll();
            ignoreSearch = false;
            view.Invalidate();
            UpdateHint();
        }

        public void Choose(int index)
        {
            if (Editing == null || index < 0 || index >= Editing.Options.Length) return;
            Change(Editing, Editing.Options[index]);
            Back();
        }

        void Commit()
        {
            string t = Typed;
            if (Editing.Kind == "color")
            {
                Color c;
                if (!TryColor(t, out c)) { status = "That isn't a colour. Use 6 hex digits, e.g. #C04517"; UpdateHint(); return; }
                Change(Editing, "#" + t.TrimStart('#').ToUpperInvariant());
            }
            else
            {
                int n;
                if (!int.TryParse(t, out n) || n < Editing.Min || n > Editing.Max) { status = "Pick a whole number from " + Editing.Min + " to " + Editing.Max; UpdateHint(); return; }
                Change(Editing, n);
            }
            Back();
        }

        public void Back()
        {
            CurrentMode = Mode.Browse;
            Editing = null;
            status = null;
            SetCue("Search settings");
            ignoreSearch = true; search.Text = ""; ignoreSearch = false;
            Refresh2();
        }

        void ApplyNow()
        {
            if (!changed) { status = null; UpdateHint(); return; }
            Save();
            appliedOnce = true;
            UpdateHint();
        }

        void UpdateHint()
        {
            string h;
            if (CurrentMode == Mode.Pick) h = "↑↓ choose    ↵ select    esc back";
            else if (CurrentMode == Mode.Input) h = "↵ save value    esc cancel";
            else h = "↑↓ setting    ←→ change    tab section    ↵ edit    ctrl+s apply now    esc close";
            if (loadError != null) { hint.ForeColor = Theme.Danger; hint.Text = loadError; return; }
            if (status != null) { hint.ForeColor = Theme.Danger; hint.Text = status; return; }
            hint.ForeColor = Theme.Dim;
            hint.Text = h + (CurrentMode != Mode.Browse ? "" : changed ? "    ● applies on close" : appliedOnce ? "    ✓ applied" : "");
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            bool handled = true;
            if (CurrentMode == Mode.Pick)
            {
                if (e.KeyCode == Keys.Escape) Back();
                else if (e.KeyCode == Keys.Up) { PickIndex = Math.Max(0, PickIndex - 1); view.EnsurePickVisible(); view.Invalidate(); }
                else if (e.KeyCode == Keys.Down) { PickIndex = Math.Min(Editing.Options.Length - 1, PickIndex + 1); view.EnsurePickVisible(); view.Invalidate(); }
                else if (e.KeyCode == Keys.Enter) Choose(PickIndex);
                else handled = false;
            }
            else if (CurrentMode == Mode.Input)
            {
                if (e.KeyCode == Keys.Escape) Back();
                else if (e.KeyCode == Keys.Enter) Commit();
                else handled = false;
            }
            else
            {
                if (e.KeyCode == Keys.Escape) { if (Searching) GoSection(Section); else Close(); }
                else if (e.KeyCode == Keys.Up) { if (Selected > 0) { Selected--; view.EnsureVisible(); view.Invalidate(); } }
                else if (e.KeyCode == Keys.Down) { if (Selected < Rows.Count - 1) { Selected++; view.EnsureVisible(); view.Invalidate(); } }
                else if (e.KeyCode == Keys.Left) Adjust(-1, e.Shift);
                else if (e.KeyCode == Keys.Right) Adjust(1, e.Shift);
                else if (e.KeyCode == Keys.Enter) EditCurrent();
                else if (e.KeyCode == Keys.PageDown) GoSection(Section + 1);
                else if (e.KeyCode == Keys.PageUp) GoSection(Section - 1);
                else if (e.Control && e.KeyCode == Keys.S) ApplyNow();
                else if (e.Control && e.KeyCode == Keys.E) { if (changed) Save(); Run.OpenFile(Paths.Config); Close(); }
                else handled = false;
            }
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        // Tab / Shift+Tab move between sections instead of focus
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (CurrentMode == Mode.Browse && (keyData == Keys.Tab || keyData == (Keys.Tab | Keys.Shift)))
            {
                GoSection(Section + (keyData == Keys.Tab ? 1 : -1));
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        // Saves and has komorebi reload, so the change is visible right away
        void Save()
        {
            if (loadError != null) return;   // never overwrite a file we couldn't read
            try
            {
                File.Copy(Paths.Config, Paths.Config + ".bak", true);
                File.WriteAllText(Paths.Config, Json.Write(cfg), new UTF8Encoding(false));
                changed = false;
                Saved = true;
                Run.K("reload-configuration");
            }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }
    }

    class SettingsView : Control
    {
        readonly SettingsEditor ed;
        const int SideW = 196, RowH = 60, PickH = 32, CtrlW = 270, Pad = 10;
        int scroll, pickScroll, dragRow = -1;
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        readonly List<Rectangle> sideRects = new List<Rectangle>();

        public SettingsView(SettingsEditor editor)
        {
            ed = editor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        static readonly Color SideBg = Color.FromArgb(24, 24, 26);
        int ContentX { get { return SideW + 18; } }
        int ContentW { get { return Width - ContentX - 18; } }
        int CtrlX { get { return Width - 18 - CtrlW - 12; } }
        int MaxScroll { get { return Math.Max(0, Pad * 2 + ed.Rows.Count * RowH - Height); } }

        public void EnsureVisible()
        {
            int y = Pad + ed.Selected * RowH;
            if (y - Pad < scroll) scroll = Math.Max(0, y - Pad);
            else if (y + RowH + Pad > scroll + Height) scroll = y + RowH + Pad - Height;
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll));
        }

        public void EnsurePickVisible()
        {
            int top = 56, y = ed.PickIndex * PickH;
            if (y < pickScroll) pickScroll = y;
            else if (y + PickH > pickScroll + Height - top - Pad) pickScroll = y + PickH - (Height - top - Pad);
        }

        // ---- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintSidebar(g);
            if (ed.CurrentMode == SettingsEditor.Mode.Pick) PaintPick(g);
            else PaintRows(g);
        }

        void PaintSidebar(Graphics g)
        {
            using (var b = new SolidBrush(SideBg)) g.FillRectangle(b, 2, 0, SideW - 2, Height);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, SideW, 0, SideW, Height);
            sideRects.Clear();
            int y = 14;
            for (int i = 0; i < SettingCatalog.Sections.Length; i++)
            {
                var r = new Rectangle(10, y, SideW - 20, 36);
                sideRects.Add(r);
                bool sel = !ed.Searching && i == ed.Section;
                if (sel)
                {
                    using (var path = Theme.Round(r, 6)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, r.X, r.Y + 9, 3, r.Height - 18);
                }
                TextRenderer.DrawText(g, SettingCatalog.Sections[i], Theme.Mono, new Rectangle(r.X + 16, r.Y, r.Width - 16, r.Height),
                    sel ? Theme.Text : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += 40;
            }
            if (ed.Searching)
            {
                var r = new Rectangle(10, y + 10, SideW - 20, 36);
                using (var path = Theme.Round(r, 6)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, r.X, r.Y + 9, 3, r.Height - 18);
                TextRenderer.DrawText(g, "Results (" + ed.Rows.Count + ")", Theme.Mono, new Rectangle(r.X + 16, r.Y, r.Width - 16, r.Height), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        void PaintRows(Graphics g)
        {
            rowRects.Clear();
            if (ed.Rows.Count == 0)
            {
                TextRenderer.DrawText(g, "No settings match", Theme.Mono, new Rectangle(ContentX, 20, ContentW, 30), Theme.Dim, TextFormatFlags.NoPrefix);
                return;
            }
            var clip = g.Clip;
            g.SetClip(new Rectangle(SideW + 1, 0, Width - SideW - 1, Height));
            for (int i = 0; i < ed.Rows.Count; i++)
            {
                Setting s = ed.Rows[i];
                int y = Pad + i * RowH - scroll;
                var row = new Rectangle(ContentX - 8, y + 2, ContentW + 8, RowH - 4);
                rowRects.Add(row);
                if (y + RowH < 0 || y > Height) continue;

                bool sel = i == ed.Selected, on = ed.IsActive(s);
                bool editingThis = ed.CurrentMode == SettingsEditor.Mode.Input && ed.Editing == s;
                if (sel || editingThis)
                {
                    using (var path = Theme.Round(row, 7)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, row.X, row.Y + 12, 3, row.Height - 24);
                }

                Color label = !on ? Theme.Dim : sel ? Theme.Text : Theme.TextSoft;
                string group = ed.Searching ? s.Group.ToUpperInvariant() + "  ·  " : "";
                TextRenderer.DrawText(g, s.Label, Theme.Mono, new Point(ContentX + 6, y + 10), label, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, group + s.Description + (on ? "" : "  (turned off)"), Theme.MonoSmall,
                    new Rectangle(ContentX + 6, y + 32, CtrlX - ContentX - 12, 18), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                PaintControl(g, s, new Rectangle(CtrlX, y + 2, CtrlW, RowH - 4), sel, on, editingThis);
            }
            g.Clip = clip;

            int max = MaxScroll;
            if (max > 0)
            {
                int track = Height - 12, th = Math.Max(30, (int)((long)track * Height / (Pad * 2 + ed.Rows.Count * RowH)));
                int ty = 6 + (int)((long)(track - th) * scroll / max);
                using (var path = Theme.Round(new RectangleF(Width - 7, ty, 3, th), 1.5f)) using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, path);
            }
        }

        void PaintControl(Graphics g, Setting s, Rectangle area, bool sel, bool on, bool editing)
        {
            int cy = area.Y + area.Height / 2;
            Color accent = on ? Theme.Accent : Theme.Border;
            switch (s.Kind)
            {
                case "bool":
                {
                    bool v = ed.Bool(s);
                    var pill = new RectangleF(area.Right - 46, cy - 11, 44, 22);
                    using (var path = Theme.Round(pill, 11))
                    using (var b = new SolidBrush(v ? Theme.Accent : Theme.Surface))
                    using (var p = new Pen(v ? Theme.Accent : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                    using (var knob = new SolidBrush(v ? Color.White : Theme.Dim)) g.FillEllipse(knob, v ? pill.Right - 19 : pill.X + 3, pill.Y + 3, 16, 16);
                    TextRenderer.DrawText(g, v ? "On" : "Off", Theme.MonoSmall, new Rectangle(area.Right - 100, cy - 10, 48, 20), v ? Theme.Text : Theme.Dim,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
                }
                case "int":
                {
                    int v = ed.Int(s);
                    var track = SliderTrack(area);
                    float t = s.Max == s.Min ? 0 : (float)(v - s.Min) / (s.Max - s.Min);
                    float kx = track.X + t * track.Width;
                    using (var path = Theme.Round(new RectangleF(track.X, cy - 2, track.Width, 4), 2)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                    if (kx > track.X + 1)
                        using (var path = Theme.Round(new RectangleF(track.X, cy - 2, kx - track.X, 4), 2)) using (var b = new SolidBrush(accent)) g.FillPath(b, path);
                    using (var knob = new SolidBrush(Color.White)) g.FillEllipse(knob, kx - 7, cy - 7, 14, 14);
                    using (var ring = new Pen(accent, 2)) g.DrawEllipse(ring, kx - 7, cy - 7, 14, 14);
                    string shown = editing ? ed.Typed + s.Unit + "▏" : ed.Display(s);
                    TextRenderer.DrawText(g, shown, Theme.Mono, new Rectangle(area.Right - 74, cy - 11, 74, 22), on || editing ? Theme.Text : Theme.Dim,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
                }
                case "enum":
                    if (s.Options.Length <= 4) PaintSegments(g, s, area, on);
                    else PaintSpinner(g, s, area, sel, on);
                    break;
                case "color":
                {
                    string hex = editing ? ed.Typed : ed.Str(s);
                    Color c;
                    bool valid = SettingsEditor.TryColor(hex, out c);
                    var sw = new RectangleF(area.Right - 132, cy - 11, 22, 22);
                    using (var path = Theme.Round(sw, 5))
                    using (var b = new SolidBrush(valid ? c : Theme.Surface))
                    using (var p = new Pen(Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                    var box = new Rectangle(area.Right - 102, cy - 13, 102, 26);
                    if (editing)
                        using (var path = Theme.Round(box, 6)) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawPath(p, path);
                    TextRenderer.DrawText(g, (hex.StartsWith("#") || hex.Length == 0 ? hex : "#" + hex).ToUpperInvariant() + (editing ? "▏" : ""), Theme.Mono, box,
                        valid || !editing ? (on ? Theme.Text : Theme.Dim) : Theme.Danger, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
                }
            }
        }

        Rectangle SliderTrack(Rectangle area) { return new Rectangle(area.X + 6, area.Y + area.Height / 2 - 8, area.Width - 96, 16); }

        List<Rectangle> SegmentRects(Setting s, Rectangle area)
        {
            var list = new List<Rectangle>();
            int n = s.Options.Length, gap = 4, w = (area.Width - gap * (n - 1)) / n, cy = area.Y + area.Height / 2;
            for (int i = 0; i < n; i++) list.Add(new Rectangle(area.X + i * (w + gap), cy - 14, w, 28));
            return list;
        }

        void PaintSegments(Graphics g, Setting s, Rectangle area, bool on)
        {
            string cur = ed.Str(s);
            var rects = SegmentRects(s, area);
            for (int i = 0; i < rects.Count; i++)
            {
                bool active = s.Options[i] == cur;
                using (var path = Theme.Round(rects[i], 6))
                using (var b = new SolidBrush(active ? (on ? Theme.Accent : Theme.Border) : Theme.Surface))
                using (var p = new Pen(active ? (on ? Theme.Accent : Theme.Border) : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                TextRenderer.DrawText(g, s.Options[i], Theme.MonoSmall, rects[i], active ? Color.White : Theme.TextSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        void PaintSpinner(Graphics g, Setting s, Rectangle area, bool sel, bool on)
        {
            int cy = area.Y + area.Height / 2;
            var box = new Rectangle(area.X, cy - 14, area.Width, 28);
            using (var path = Theme.Round(box, 6))
            using (var b = new SolidBrush(Theme.Surface))
            using (var p = new Pen(sel ? Theme.Accent : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
            TextRenderer.DrawText(g, "‹", Theme.MonoBig, new Rectangle(box.X + 4, box.Y, 24, box.Height), Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "›", Theme.MonoBig, new Rectangle(box.Right - 28, box.Y, 24, box.Height), Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, ed.Str(s), Theme.Mono, new Rectangle(box.X + 28, box.Y, box.Width - 56, box.Height), on ? Theme.Text : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        void PaintPick(Graphics g)
        {
            Setting s = ed.Editing;
            TextRenderer.DrawText(g, s.Label, Theme.MonoBig, new Point(ContentX, 14), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, s.Description, Theme.MonoSmall, new Point(ContentX + 1, 36), Theme.Dim, TextFormatFlags.NoPrefix);
            string cur = ed.Str(s);
            int top = 56;
            var clip = g.Clip;
            g.SetClip(new Rectangle(SideW + 1, top, Width - SideW - 1, Height - top));
            rowRects.Clear();
            for (int i = 0; i < s.Options.Length; i++)
            {
                int y = top + i * PickH - pickScroll;
                var r = new Rectangle(ContentX - 8, y, ContentW + 8, PickH - 2);
                rowRects.Add(r);
                if (y + PickH < top || y > Height) continue;
                bool sel = i == ed.PickIndex;
                if (sel)
                {
                    using (var path = Theme.Round(r, 6)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, r.X, r.Y + 8, 3, r.Height - 16);
                }
                if (s.Options[i] == cur) using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, r.X + 14, r.Y + r.Height / 2 - 4, 8, 8);
                TextRenderer.DrawText(g, s.Options[i], Theme.Mono, new Rectangle(r.X + 32, r.Y, r.Width - 40, r.Height), sel ? Theme.Text : Theme.TextSoft,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            g.Clip = clip;
        }

        // ---- mouse

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (ed.CurrentMode == SettingsEditor.Mode.Pick)
                pickScroll = Math.Max(0, Math.Min(Math.Max(0, ed.Editing.Options.Length * PickH - (Height - 56 - Pad)), pickScroll - e.Delta / 120 * PickH * 3));
            else scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < sideRects.Count; i++)
                if (sideRects[i].Contains(e.Location)) { if (ed.CurrentMode != SettingsEditor.Mode.Browse) ed.Back(); ed.GoSection(i); return; }

            if (ed.CurrentMode == SettingsEditor.Mode.Pick)
            {
                for (int i = 0; i < rowRects.Count; i++) if (rowRects[i].Contains(e.Location)) { ed.Choose(i); return; }
                return;
            }
            if (ed.CurrentMode == SettingsEditor.Mode.Input) ed.Back();

            for (int i = 0; i < rowRects.Count; i++)
            {
                if (!rowRects[i].Contains(e.Location)) continue;
                ed.Selected = i;
                Setting s = ed.Rows[i];
                int y = rowRects[i].Y - 2;
                var area = new Rectangle(CtrlX, y + 2, CtrlW, RowH - 4);
                if (e.X >= area.X - 8)
                {
                    if (s.Kind == "bool") ed.Change(s, !ed.Bool(s));
                    else if (s.Kind == "int") { dragRow = i; Capture = true; SlideTo(s, area, e.X); }
                    else if (s.Kind == "color") ed.OpenInput(s);
                    else if (s.Options.Length <= 4)
                    {
                        var segs = SegmentRects(s, area);
                        for (int k = 0; k < segs.Count; k++) if (segs[k].Contains(e.Location)) ed.Change(s, s.Options[k]);
                    }
                    else if (e.X < area.X + 28) ed.Adjust(-1, false);
                    else if (e.X > area.Right - 28) ed.Adjust(1, false);
                    else ed.OpenPick(s);
                }
                Invalidate();
                return;
            }
        }

        void SlideTo(Setting s, Rectangle area, int x)
        {
            var track = SliderTrack(area);
            double t = Math.Max(0, Math.Min(1, (x - track.X) / (double)track.Width));
            ed.SetInt(s, s.Min + (int)Math.Round(t * (s.Max - s.Min)));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragRow >= 0 && dragRow < ed.Rows.Count && rowRects.Count > dragRow)
            {
                int y = rowRects[dragRow].Y - 2;
                SlideTo(ed.Rows[dragRow], new Rectangle(CtrlX, y + 2, CtrlW, RowH - 4), e.X);
            }
            bool hot = false;
            foreach (var r in sideRects) if (r.Contains(e.Location)) hot = true;
            foreach (var r in rowRects) if (r.Contains(e.Location)) hot = true;
            Cursor = hot ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragRow >= 0) { dragRow = -1; Capture = false; }
        }
    }

    // ---------------------------------------------------------------- built-in hotkeys (replaces whkd)
    // RegisterHotKey costs nothing while typing (no keyboard hook) and fires straight into this process.

    class Hotkeys : NativeWindow
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        readonly Dictionary<int, string> commands = new Dictionary<int, string>();
        public Action<string> Fire;

        public Hotkeys()
        {
            // Message-only window: no UI, just receives WM_HOTKEY
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        public bool Active { get { return commands.Count > 0; } }

        // Registers every binding in whkdrc; returns the ones Windows refused (already taken by another app)
        public List<string> Load()
        {
            Clear();
            var failed = new List<string>();
            if (!File.Exists(Paths.Whkdrc)) return failed;
            int id = 1;
            foreach (var l in Whkdrc.Parse(File.ReadAllLines(Paths.Whkdrc)))
            {
                if (!l.IsBinding) continue;
                uint mods, vk;
                if (!Parse(l.Keys, out mods, out vk)) { failed.Add(l.Keys); continue; }
                // Holding resize keeps resizing; everything else fires once per press
                if (!l.Command.Contains("resize-axis")) mods |= MOD_NOREPEAT;
                if (RegisterHotKey(Handle, id, mods, vk)) commands[id++] = l.Command;
                else failed.Add(l.Keys);
            }
            if (failed.Count > 0) Log.Write("hotkeys not registered: " + string.Join(", ", failed.ToArray()));
            return failed;
        }

        public void Clear()
        {
            foreach (int id in commands.Keys) UnregisterHotKey(Handle, id);
            commands.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            string cmd;
            if (m.Msg == WM_HOTKEY && commands.TryGetValue((int)m.WParam, out cmd) && Fire != null) Fire(cmd);
            base.WndProc(ref m);
        }

        static bool Parse(string keys, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (string part in keys.Split('+'))
            {
                string p = part.Trim();
                if (p == "alt") mods |= MOD_ALT;
                else if (p == "ctrl") mods |= MOD_CONTROL;
                else if (p == "shift") mods |= MOD_SHIFT;
                else if (p == "win") mods |= MOD_WIN;
                else vk = Vk(p);
            }
            return vk != 0;
        }

        // whkd key name -> virtual key code (inverse of Whkdrc.KeyName)
        static uint Vk(string k)
        {
            if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z') return (uint)char.ToUpperInvariant(k[0]);
            if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') return (uint)k[0];
            int n;
            if (k.StartsWith("numpad") && int.TryParse(k.Substring(6), out n) && n >= 0 && n <= 9) return (uint)(0x60 + n);
            if (k.Length > 1 && k[0] == 'f' && int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) return (uint)(0x6F + n);
            switch (k)
            {
                case "return": case "enter": return 0x0D;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "escape": case "esc": return 0x1B;
                case "back": case "backspace": return 0x08;
                case "delete": return 0x2E;
                case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "prior": case "pageup": return 0x21;
                case "next": case "pagedown": return 0x22;
                case "left": return 0x25;
                case "up": return 0x26;
                case "right": return 0x27;
                case "down": return 0x28;
                case "oem_1": return 0xBA;
                case "oem_plus": return 0xBB;
                case "oem_comma": return 0xBC;
                case "oem_minus": return 0xBD;
                case "oem_period": return 0xBE;
                case "oem_2": return 0xBF;
                case "oem_3": return 0xC0;
                case "oem_4": return 0xDB;
                case "oem_5": return 0xDC;
                case "oem_6": return 0xDD;
                case "oem_7": return 0xDE;
            }
            return 0;
        }
    }

    // komorebi's transparency makes app windows layered. If komorebi stops or exits while they are, the style
    // stays behind: the window remains semi-transparent, and komorebi refuses to manage layered windows, so it
    // never tiles again. This strips exactly that leftover (komorebi's alpha, or fully opaque) from titled app
    // windows, and only while komorebi isn't running.
    static class WindowFixer
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_CAPTION = 0x00C00000;
        const uint LWA_COLORKEY = 1, LWA_ALPHA = 2, RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

        public static int ClearKomorebiTransparency()
        {
            byte alpha = ConfiguredAlpha();
            int own;
            using (var me = Process.GetCurrentProcess()) own = me.Id;
            int cleared = 0;
            EnumWindows((h, l) =>
            {
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0 || (GetWindowLong(h, GWL_STYLE) & WS_CAPTION) != WS_CAPTION) return true;
                int pid; GetWindowThreadProcessId(h, out pid);
                uint key, flags; byte a;
                if (pid == own || !GetLayeredWindowAttributes(h, out key, out a, out flags)) return true;
                // komorebi sets a plain alpha (no colour key); apps doing their own per-pixel transparency don't match
                if ((flags & LWA_ALPHA) == 0 || (flags & LWA_COLORKEY) != 0 || (a != alpha && a != 255)) return true;
                SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_LAYERED) != 0) return true;   // e.g. an app running as admin
                RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
                cleared++;
                return true;
            }, IntPtr.Zero);
            if (cleared > 0) Log.Write("cleared leftover komorebi transparency from " + cleared + " window(s)");
            return cleared;
        }

        static byte ConfiguredAlpha()
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(Paths.ConfigDir, "komorebi.json")),
                    "\"transparency_alpha\"\\s*:\\s*(\\d+)");
                if (m.Success) return (byte)Math.Min(255, int.Parse(m.Groups[1].Value));
            }
            catch { }
            return 200;   // komorebi's default
        }
    }

    // Tray icons drawn in code: the .ico keeps its small sizes as PNG, which .NET's Icon class can't decode
    // (its ToBitmap produced noise). Drawn at 4x and scaled down so the 16px result stays crisp.
    static class TrayIcons
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        static readonly List<IntPtr> handles = new List<IntPtr>();

        public static Icon Make(bool stopped)
        {
            try
            {
                int size = Math.Max(16, SystemInformation.SmallIconSize.Width), big = size * 4;
                using (var hi = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var final = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(hi))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        float u = big / 256f, m = 10 * u, gap = 18 * u, rad = 34 * u, mid = big / 2f;
                        // Running: all-white tiles. Stopped: the same glyph in muted greys.
                        Color main = stopped ? Color.FromArgb(255, 138, 138, 146) : Color.White;
                        Color side = stopped ? Color.FromArgb(255, 92, 92, 100) : Color.White;
                        Tile(g, m, m, mid - gap / 2, big - m, rad, main);
                        Tile(g, mid + gap / 2, m, big - m, mid - gap / 2, rad, side);
                        Tile(g, mid + gap / 2, mid + gap / 2, big - m, big - m, rad, side);
                    }
                    using (var g = Graphics.FromImage(final))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(hi, new Rectangle(0, 0, size, size));
                    }
                    IntPtr h = final.GetHicon();
                    handles.Add(h);
                    return Icon.FromHandle(h);
                }
            }
            catch (Exception e) { Log.Write("tray icon: " + e.Message); return null; }
        }

        static void Tile(Graphics g, float x0, float y0, float x1, float y1, float rad, Color c)
        {
            using (var path = Theme.Round(new RectangleF(x0, y0, x1 - x0, y1 - y0), rad))
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
        }

        public static void Release()
        {
            foreach (IntPtr h in handles) DestroyIcon(h);
            handles.Clear();
        }
    }

    // ---------------------------------------------------------------- tray

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripItem status, toggle, retile, reload, shortcutsItem, appsItem, settingsItem;
        readonly Hotkeys hotkeys = new Hotkeys();
        Icon normalIcon, stoppedIcon;
        KeybindEditor editor;
        AppLauncher apps;
        SettingsEditor settingsWin;
        bool stopped, quitting;
        readonly List<DateTime> crashes = new List<DateTime>();
        readonly Control invoker = new Control();
        EventWaitHandle openSignal, appsSignal, toggleSignal, diagSignal, settingsSignal;

        public TrayApp(bool openShortcuts, bool openApps)
        {
            // Other launches with --shortcuts/--apps/--toggle-pause set these events; pool waits avoid dedicated threads
            invoker.CreateControl();
            try
            {
                openSignal = Listen(Program.OpenShortcutsEvent, OpenEditor);
                appsSignal = Listen(Program.OpenAppsEvent, ToggleApps);
                toggleSignal = Listen(Program.TogglePauseEvent, ToggleAll);
                diagSignal = Listen(Program.DiagnosticsEvent, SaveDiagnostics);
                settingsSignal = Listen(Program.SettingsEvent, ToggleSettings);
            }
            catch (Exception ex) { Log.Write("signal: " + ex.Message); }

            normalIcon = TrayIcons.Make(false) ?? SystemIcons.Application;
            tray.Icon = normalIcon;
            tray.Text = "komorebi";

            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
            menu.BackColor = Color.FromArgb(32, 32, 32);
            menu.ForeColor = Color.FromArgb(235, 235, 235);
            menu.ShowImageMargin = false;

            status = menu.Items.Add("Status");
            status.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            toggle = menu.Items.Add("Stop everything", null, (s, e) => ToggleAll());
            retile = menu.Items.Add("Retile", null, (s, e) => Run.K("retile"));
            menu.Items.Add(new ToolStripSeparator());
            reload = menu.Items.Add("Reload config", null, (s, e) =>
            {
                Run.K("reload-configuration", 3000);
                ReloadHotkeys();
                tray.ShowBalloonTip(1500, "komorebi", "Config and keybinds reloaded", ToolTipIcon.Info);
            });
            settingsItem = menu.Items.Add("Settings…", null, (s, e) => ToggleSettings());
            shortcutsItem = menu.Items.Add("Keyboard shortcuts…", null, (s, e) => OpenEditor());
            appsItem = menu.Items.Add("Apps…", null, (s, e) => ToggleApps());
            menu.Items.Add("Save diagnostics", null, (s, e) => SaveDiagnostics());
            menu.Items.Add("Open config folder", null, (s, e) => Run.Shell("explorer.exe", "\"" + Paths.ConfigDir + "\""));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit (stop komorebi + tray)", null, (s, e) =>
            {
                quitting = true;
                hotkeys.Clear();
                Run.K("stop", 5000);
                WaitForKomorebiExit();
                WindowFixer.ClearKomorebiTransparency();
                tray.Visible = false;
                ExitThread();
            });

            menu.Opening += (s, e) =>
            {
                status.Text = stopped ? "komorebi: stopped" : Run.IsRunning("komorebi") ? "komorebi: running" : "komorebi: starting…";
                toggle.Text = stopped ? "Start everything" : "Stop everything";
                // Stopped means everything: only Start, Quit and the config folder stay usable
                retile.Enabled = reload.Enabled = shortcutsItem.Enabled = appsItem.Enabled = settingsItem.Enabled = !stopped;
                toggle.Enabled = editor == null;
            };
            menu.Closed += (s, e) => { if (editor == null && apps == null && settingsWin == null) Native.Trim(); };

            tray.ContextMenuStrip = menu;
            // Left click stops / starts everything; right click opens the menu
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && editor == null) ToggleAll(); };
            tray.Visible = true;

            hotkeys.Fire = Execute;

            // whkd is no longer needed: the tray owns the hotkeys now
            Run.KillAll("whkd");
            if (!Run.IsRunning("komorebi")) StartKomorebi(); else Watch();
            LoadHotkeys(false);
            Log.Write("running");
            if (openShortcuts) invoker.BeginInvoke((MethodInvoker)OpenEditor);
            if (openApps) invoker.BeginInvoke((MethodInvoker)ToggleApps);

            // Warm the app list once startup has settled (so the first Alt+Space is instant), then trim
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            // Icons are prefetched too, so the first Alt+Space already shows them
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); AppCatalog.Refresh(() => { IconCache.Prefetch(AppCatalog.Ids()); Native.Trim(); }); };
            t.Start();
        }

        EventWaitHandle Listen(string name, MethodInvoker action)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            ThreadPool.RegisterWaitForSingleObject(ev, (st, timedOut) => { try { invoker.BeginInvoke(action); } catch { } }, null, Timeout.Infinite, false);
            return ev;
        }

        // ---- hotkeys

        void LoadHotkeys(bool announce)
        {
            List<string> failed = hotkeys.Load();
            if (failed.Count > 0)
                tray.ShowBalloonTip(4000, "komorebi", "These shortcuts are taken by another app or invalid: " +
                    string.Join(", ", failed.ConvertAll(k => Whkdrc.PrettyKeys(k)).ToArray()), ToolTipIcon.Warning);
            else if (announce)
                tray.ShowBalloonTip(1500, "komorebi", "Shortcuts saved", ToolTipIcon.Info);
        }

        void ReloadHotkeys() { if (!stopped && editor == null) LoadHotkeys(false); }

        // Runs a whkdrc command: tray features in-process, komorebic directly (no cmd in between), the rest via cmd
        void Execute(string command)
        {
            string c = command.Trim();
            if (c.IndexOf("komorebi-tray", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (c.Contains("--shortcuts")) OpenEditor();
                else if (c.Contains("--apps")) ToggleApps();
                else if (c.Contains("--toggle-pause")) ToggleAll();
                else if (c.Contains("--browser")) Run.OpenDefaultBrowser();
                else if (c.Contains("--settings")) ToggleSettings();
                return;
            }
            if (c.Contains("taskkill") && c.Contains("whkd")) { ReloadHotkeys(); return; }   // old "restart whkd" binding
            Native.AllowSetForegroundWindow(-1);
            if (c.StartsWith("komorebic ", StringComparison.OrdinalIgnoreCase)) Run.K(c.Substring(10));
            else Run.Start("cmd.exe", "/c " + c, 0);
        }

        // ---- stop / start everything

        // ---- komorebi lifecycle

        // Always start from clean windows: komorebi ignores windows that still carry its old transparency
        // If the "komorebi (elevated)" scheduled task exists, komorebi starts through it with admin rights
        // (no prompt), so it can tile apps that run as administrator too. The tray itself stays at normal
        // rights, so apps it launches (Alt+Space, browser, terminal) don't inherit admin.
        void StartKomorebi()
        {
            WindowFixer.ClearKomorebiTransparency();
            if (!Run.StartElevatedKomorebi()) Run.K("start --clean-state");
            Watch();
        }

        static void WaitForKomorebiExit()
        {
            for (int i = 0; i < 30 && Run.IsRunning("komorebi"); i++) Thread.Sleep(100);
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        WaitHandle exitHandle;
        RegisteredWaitHandle exitWait;
        System.Windows.Forms.Timer exitPoll;

        // Get told when komorebi exits. Only SYNCHRONIZE is requested, which Windows also grants on an admin
        // komorebi (Process.EnableRaisingEvents asks for more and is refused there). The wait is a kernel
        // wait on the thread pool, so nothing runs while komorebi is fine; a 5 s check is the fallback.
        void Watch()
        {
            int tries = 0;
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (s, e) =>
            {
                Process[] ps = Process.GetProcessesByName("komorebi");
                int pid = ps.Length > 0 ? ps[0].Id : 0;
                foreach (var p in ps) p.Dispose();
                if (pid == 0 && ++tries <= 30) return;
                t.Stop(); t.Dispose();
                if (pid != 0) Attach(pid);
            };
            t.Start();
        }

        void Attach(int pid)
        {
            Unwatch();
            IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(SYNCHRONIZE, false, pid);
            if (h != IntPtr.Zero)
            {
                exitHandle = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, true) };
                exitWait = ThreadPool.RegisterWaitForSingleObject(exitHandle,
                    (st, timedOut) => { try { invoker.BeginInvoke((MethodInvoker)OnKomorebiExited); } catch { } }, null, Timeout.Infinite, true);
                return;
            }
            Log.Write("watch: no exit notification (" + Marshal.GetLastWin32Error() + "), checking every 5 s instead");
            exitPoll = new System.Windows.Forms.Timer { Interval = 5000 };
            exitPoll.Tick += (s, e) => { if (!Run.IsRunning("komorebi")) OnKomorebiExited(); };
            exitPoll.Start();
        }

        void Unwatch()
        {
            if (exitWait != null) { exitWait.Unregister(null); exitWait = null; }
            if (exitHandle != null) { exitHandle.Dispose(); exitHandle = null; }
            if (exitPoll != null) { exitPoll.Stop(); exitPoll.Dispose(); exitPoll = null; }
        }
        void OnKomorebiExited()
        {
            Unwatch();
            if (stopped || quitting) return;            // our own stop already cleaned up
            WindowFixer.ClearKomorebiTransparency();
            crashes.Add(DateTime.Now);
            crashes.RemoveAll(d => (DateTime.Now - d).TotalSeconds > 60);
            if (crashes.Count > 3)
            {
                // Don't loop forever: stop cleanly and let the user start again from the tray
                Log.Write("komorebi keeps exiting; leaving everything stopped");
                stopped = true;
                hotkeys.Clear();
                tray.Text = "komorebi (stopped)";
                tray.Icon = StoppedIcon();
                tray.ShowBalloonTip(4000, "komorebi", "komorebi keeps closing unexpectedly. Click the tray icon to start it again.", ToolTipIcon.Warning);
                return;
            }
            Log.Write("komorebi exited unexpectedly; restarting");
            StartKomorebi();
        }

        DateTime lastToggle = DateTime.MinValue;

        // A double-click on the icon must not stop and immediately restart everything
        void ToggleAll()
        {
            if ((DateTime.Now - lastToggle).TotalMilliseconds < 1000) return;
            lastToggle = DateTime.Now;
            if (stopped) StartAll(); else StopAll();
        }

        void StopAll()
        {
            stopped = true;
            if (editor != null) editor.Close();
            if (apps != null) apps.Close();
            if (settingsWin != null) settingsWin.Close();   // saves pending changes first
            hotkeys.Clear();
            Run.K("stop", 4000);   // komorebi restores every hidden window, removes borders and exits
            WaitForKomorebiExit();
            WindowFixer.ClearKomorebiTransparency();
            tray.Text = "komorebi (stopped)";
            tray.Icon = StoppedIcon();
            tray.ShowBalloonTip(1200, "komorebi", "Everything stopped", ToolTipIcon.Info);
            Native.Trim();
        }

        void StartAll()
        {
            stopped = false;
            // A komorebi that is still shutting down would make "start" a no-op; give it a moment to exit
            WaitForKomorebiExit();
            if (!Run.IsRunning("komorebi")) StartKomorebi(); else Watch();
            LoadHotkeys(false);
            tray.Text = "komorebi";
            tray.Icon = normalIcon;
            tray.ShowBalloonTip(1200, "komorebi", "Everything started", ToolTipIcon.Info);
        }

        Icon StoppedIcon()
        {
            if (stoppedIcon == null) stoppedIcon = TrayIcons.Make(true) ?? normalIcon;
            return stoppedIcon;
        }
        // ---- menus

        void OpenEditor()
        {
            if (editor != null) { editor.Activate(); return; }
            if (stopped) return;
            // Hotkeys would otherwise fire (e.g. Alt+W closes windows) while recording shortcuts
            hotkeys.Clear();
            editor = new KeybindEditor();
            editor.FormClosed += (s, e) =>
            {
                bool saved = editor.Saved;
                editor.Dispose();
                editor = null;
                if (!stopped) LoadHotkeys(saved);
                Native.Trim();
            };
            editor.Show();
            editor.Activate();
        }

        // Alt+C toggles the komorebi.json settings screen
        void ToggleSettings()
        {
            if (settingsWin != null) { settingsWin.Close(); return; }
            if (stopped) return;
            settingsWin = new SettingsEditor();
            settingsWin.FormClosed += (s, e) =>
            {
                bool saved = settingsWin.Saved;
                settingsWin.Dispose();
                settingsWin = null;
                // The settings window already saved and had komorebi reload; just confirm it
                if (saved && !stopped) tray.ShowBalloonTip(1500, "komorebi", "Settings applied", ToolTipIcon.Info);
                Native.Trim();
            };
            settingsWin.Show();
            settingsWin.Activate();
        }

        // Alt+Space toggles it, like Omarchy's launcher
        void ToggleApps()
        {
            if (apps != null) { apps.Close(); return; }
            if (stopped) return;
            apps = new AppLauncher();
            apps.FormClosed += (s, e) => { apps.Dispose(); apps = null; Native.Trim(); };
            apps.Show();
            apps.Activate();
        }

        // Dumps what komorebi thinks of every window to diag.txt (off the UI thread)
        void SaveDiagnostics()
        {
            var t = new Thread(() =>
            {
                var sb = new StringBuilder();
                sb.Append("== ").Append(DateTime.Now.ToString("s")).Append("\r\n");
                foreach (string cmd in new[] { "visible-windows", "monitor-information", "state" })
                {
                    sb.Append("\r\n== komorebic ").Append(cmd).Append("\r\n");
                    try
                    {
                        var psi = new ProcessStartInfo(Run.Komorebic, cmd)
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        };
                        using (var p = Process.Start(psi))
                        {
                            var err = p.StandardError.ReadToEndAsync();
                            sb.Append(p.StandardOutput.ReadToEnd());
                            p.WaitForExit(10000);
                            sb.Append(err.Result);
                        }
                    }
                    catch (Exception ex) { sb.Append("failed: ").Append(ex.Message); }
                }
                try { File.WriteAllText(Path.Combine(Paths.ConfigDir, "diag.txt"), sb.ToString()); } catch { }
                tray.ShowBalloonTip(2000, "komorebi", "Diagnostics saved to diag.txt", ToolTipIcon.Info);
            }) { IsBackground = true };
            t.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hotkeys.Clear();
                hotkeys.DestroyHandle();
                Unwatch();
                tray.Dispose(); menu.Dispose(); invoker.Dispose();
                TrayIcons.Release();
                foreach (var ev in new[] { openSignal, appsSignal, toggleSignal, diagSignal, settingsSignal }) if (ev != null) ev.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        public const string OpenShortcutsEvent = @"Local\KomorebiTrayOpenShortcuts";
        public const string OpenAppsEvent = @"Local\KomorebiTrayOpenApps";
        public const string TogglePauseEvent = @"Local\KomorebiTrayTogglePause";
        public const string DiagnosticsEvent = @"Local\KomorebiTrayDiagnostics";
        public const string SettingsEvent = @"Local\KomorebiTraySettings";

        [STAThread]
        static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--browser") >= 0) { Run.OpenDefaultBrowser(); return; }
            bool openShortcuts = Array.IndexOf(args, "--shortcuts") >= 0;
            bool openApps = Array.IndexOf(args, "--apps") >= 0;
            bool togglePause = Array.IndexOf(args, "--toggle-pause") >= 0;
            bool diagnostics = Array.IndexOf(args, "--diagnostics") >= 0;
            bool openSettings = Array.IndexOf(args, "--settings") >= 0;
            bool created;
            using (var mutex = new Mutex(true, "KomorebiTrayController", out created))
            {
                if (!created)
                {
                    // Tray already running: hand the request to it
                    // This process got the keypress, so it may hand foreground rights to the tray
                    // --toggle-pause does the same as the tray's Pause/Resume item (handy for scripts)
                    string ev = openSettings ? SettingsEvent : diagnostics ? DiagnosticsEvent : togglePause ? TogglePauseEvent : openApps ? OpenAppsEvent : openShortcuts ? OpenShortcutsEvent : null;
                    if (ev != null)
                        try
                        {
                            Native.AllowSetForegroundWindow(-1);
                            using (var h = EventWaitHandle.OpenExisting(ev)) h.Set();
                        }
                        catch { }
                    return;
                }
                Log.Start();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("HANDLER ERROR: " + e.Exception);
                try { Application.Run(new TrayApp(openShortcuts, openApps)); }
                catch (Exception ex) { Log.Write("FATAL: " + ex); }
                Log.Write("message loop ended");
            }
        }
    }
}
