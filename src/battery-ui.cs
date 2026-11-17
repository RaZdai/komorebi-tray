// What the battery readings look like: a readout embedded in the taskbar next to the notification area (the
// default), or one number per device in the notification area, plus the Batteries window (Alt+Shift+B) with a
// bigger view of every device, a search, a "report a device" helper and the battery settings.
// C# 5 / .NET Framework 4.8, like the rest of the tray.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KomorebiTray
{
    static class BatteryPrefs
    {
        public const string Display = "battery_display", Warn = "battery_warn", Low = "battery_low";
        public const string Taskbar = "Taskbar", TrayIcons = "Tray icons", Off = "Off";
        public static readonly string[] DisplayOptions = { Taskbar, TrayIcons, Off };
        public const string IssueUrl = "https://github.com/RaZdai/komorebi-tray/issues/new?title=Battery%3A%20device%20not%20found&labels=battery";

        public static string GetDisplay(Dictionary<string, object> p)
        {
            object v;
            string s = p.TryGetValue(Display, out v) ? Convert.ToString(v) : null;
            return Array.IndexOf(DisplayOptions, s) >= 0 ? s : Taskbar;
        }
        public static bool GetWarn(Dictionary<string, object> p) { object v; return !p.TryGetValue(Warn, out v) || Convert.ToBoolean(v); }
        public static int GetLow(Dictionary<string, object> p)
        {
            object v;
            try { return p.TryGetValue(Low, out v) ? Math.Max(5, Math.Min(50, Convert.ToInt32(v))) : 15; }
            catch { return 15; }
        }
        public static void Set(string key, object value) { AccentBorder.UpdatePrefs(p => p[key] = value); }
    }

    // Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10: the same code points in both
    static class Glyphs
    {
        public static readonly string Family = Pick();
        public const string Mouse = "", Keyboard = "", Gamepad = "", Headset = "", Bluetooth = "",
            Battery = "", Bolt = "", Refresh = "", Link = "", Copy = "";

        static string Pick()
        {
            foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                using (var f = new Font(n, 10f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return "Segoe UI Symbol";
        }

        public static string For(BatteryInfo b)
        {
            switch (b.Kind)
            {
                case DeviceKind.Mouse: return Mouse;
                case DeviceKind.Keyboard: return Keyboard;
                case DeviceKind.Gamepad: return Gamepad;
                case DeviceKind.Headset: return Headset;
                default: return b.Via == "Bluetooth" ? Bluetooth : Battery;
            }
        }

        public static bool LightTaskbar()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("SystemUsesLightTheme") : null;
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        // Low-battery red, readable on a light or a dark taskbar
        public static Color Low(bool light) { return light ? Color.FromArgb(196, 30, 30) : Color.FromArgb(255, 99, 90); }
    }

    // ---- the coordinator: readings in, display out (UI thread)

    class BatteryTray : IDisposable
    {
        readonly Control invoker;
        readonly NotifyIcon main;
        readonly ContextMenuStrip menu;
        readonly MethodInvoker open;
        readonly HashSet<string> warned = new HashSet<string>();
        TaskbarStrip strip;
        TrayNumbers numbers;
        DeviceWatcher watcher;
        HashSet<string> beforeSearch;
        bool disposed;

        public List<BatteryInfo> Current = new List<BatteryInfo>();
        public string Display = BatteryPrefs.Taskbar;
        public bool Warn = true;
        public int Low = 15;
        public bool Searching, Polled;
        public int LastSearchNew = -1;     // devices the last search added; -1 before any search
        public event Action Changed;

        public BatteryTray(Control invoker, NotifyIcon main, ContextMenuStrip menu, MethodInvoker open)
        {
            this.invoker = invoker; this.main = main; this.menu = menu; this.open = open;
            LoadPrefs();
            try { watcher = new DeviceWatcher(); } catch (Exception e) { Log.Write("battery device watch: " + e.Message); }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            BatteryMonitor.Updated += OnUpdated;
            BatteryMonitor.Start();
        }

        public void LoadPrefs()
        {
            if (disposed) return;
            var p = AccentBorder.LoadPrefs();
            Display = BatteryPrefs.GetDisplay(p);
            Warn = BatteryPrefs.GetWarn(p);
            Low = BatteryPrefs.GetLow(p);
            ApplyDisplay();
            Fire();
        }

        void ApplyDisplay()
        {
            if (Display == BatteryPrefs.Taskbar)
            {
                if (strip == null) strip = new TaskbarStrip(() => { try { invoker.BeginInvoke(open); } catch { } });
                strip.Show(Current, Low);
            }
            else if (strip != null) { strip.Dispose(); strip = null; }
            if (Display == BatteryPrefs.TrayIcons)
            {
                if (numbers == null) numbers = new TrayNumbers(menu, open);
                numbers.Show(Current, Low);
            }
            else if (numbers != null) { numbers.Dispose(); numbers = null; }
        }

        public void Search()
        {
            if (Searching) return;
            Searching = true;
            LastSearchNew = -1;
            beforeSearch = new HashSet<string>();
            foreach (BatteryInfo b in Current) beforeSearch.Add(b.Key);
            BatteryMonitor.Search();
            Fire();
        }

        void OnUpdated(List<BatteryInfo> list, bool search)
        {
            try { invoker.BeginInvoke((MethodInvoker)(() => { if (!disposed) Apply(list, search); })); } catch { }
        }

        void Apply(List<BatteryInfo> list, bool search)
        {
            Current = list;
            Polled = true;
            foreach (BatteryInfo b in list) CheckLow(b);
            if (strip != null) strip.Show(list, Low);
            if (numbers != null) numbers.Show(list, Low);
            if (search && Searching)
            {
                Searching = false;
                int added = 0;
                foreach (BatteryInfo b in list) if (beforeSearch == null || !beforeSearch.Contains(b.Key)) added++;
                LastSearchNew = added;
            }
            Fire();
        }

        void Fire() { var h = Changed; if (h != null) h(); }

        // Once per drop below the mark; it re-arms after charging back above it
        void CheckLow(BatteryInfo b)
        {
            if (b.Percent < 0) return;
            if (b.Charging || b.Percent > Low + 5) { warned.Remove(b.Key); return; }
            if (!Warn || b.Asleep || b.Percent > Low || !warned.Add(b.Key)) return;
            main.ShowBalloonTip(5000, "Low battery", b.Name + " is at " + b.LevelText() + ".", ToolTipIcon.Warning);
        }

        // Light / dark taskbar or a new accent colour: draw again in the new colours
        void OnPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General && e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
            try
            {
                invoker.BeginInvoke((MethodInvoker)(() =>
                {
                    if (disposed) return;
                    Theme.Refresh();
                    if (strip != null) strip.Redraw();
                    if (numbers != null) numbers.Redraw();
                }));
            }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            BatteryMonitor.Updated -= OnUpdated;
            if (watcher != null) watcher.Dispose();
            if (strip != null) strip.Dispose();
            if (numbers != null) numbers.Dispose();
            BatteryMonitor.Stop();
        }
    }

    // A hidden window for Windows' "devices changed" broadcast, so the device list is only rebuilt when
    // something was plugged in, unplugged, paired or switched off (debounced: one plug-in sends a burst)
    class DeviceWatcher : NativeWindow, IDisposable
    {
        readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 1500 };

        public DeviceWatcher()
        {
            CreateHandle(new CreateParams { Caption = "komorebi-tray devices" });
            settle.Tick += (s, e) => { settle.Stop(); BatteryMonitor.DevicesChanged(); };
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219) { settle.Stop(); settle.Start(); }   // WM_DEVICECHANGE
            base.WndProc(ref m);
        }

        public void Dispose() { settle.Dispose(); DestroyHandle(); }
    }

    // ---- taskbar readout
    // A small layered window made a child of the taskbar, just left of the notification area, so it moves, hides
    // and goes fullscreen-away together with the taskbar. It runs on its own thread with its own message loop:
    // windows of one taskbar share input with Explorer, so the thread that owns it must never be busy (the tray's
    // UI thread sometimes is, for a fraction of a second, while menus animate). Drawn only when a level changes.

    class TaskbarStrip : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT p, int n);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        const int WM_REDRAW = 0x8001, WM_QUIT_STRIP = 0x8002;
        readonly object gate = new object();
        readonly Action clicked;
        readonly Thread thread;
        List<BatteryInfo> items = new List<BatteryInfo>();
        int low = 15;
        volatile bool quitting;
        StripWindow win;
        Size lastSize;
        Point lastPos = new Point(int.MinValue, 0);

        public TaskbarStrip(Action clicked)
        {
            this.clicked = clicked;
            thread = new Thread(Run) { IsBackground = true, Name = "battery taskbar" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Show(List<BatteryInfo> list, int lowMark)
        {
            lock (gate) { items = list; low = lowMark; }
            Post(WM_REDRAW);
        }

        public void Redraw() { Post(WM_REDRAW); }

        void Post(int msg)
        {
            StripWindow w = win;
            if (w != null && w.Handle != IntPtr.Zero) PostMessage(w.Handle, msg, IntPtr.Zero, IntPtr.Zero);
        }

        void Run()
        {
            // Explorer's taskbar is per-monitor DPI aware (v2). The readout thread is too, so its window lives in the
            // taskbar's real pixels and is drawn at the monitor's own scale instead of being stretched by Windows.
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (s, e) => { if (quitting) Application.ExitThread(); else Check(); };
            timer.Start();
            Check();
            try { Application.Run(); }
            catch (Exception e) { Log.Write("battery taskbar: " + e.Message); }
            timer.Dispose();
            if (win != null && win.Handle != IntPtr.Zero) win.DestroyHandle();
            win = null;
        }

        // Every 2 s: still attached to a living taskbar (Explorer restarts make a new one), and still in place
        // (the notification area grows and shrinks as icons come and go)
        void Check()
        {
            try
            {
                IntPtr tb = FindWindow("Shell_TrayWnd", null);
                if (tb == IntPtr.Zero) { Detach(); return; }
                if (win == null || win.Handle == IntPtr.Zero || !IsWindow(win.Handle) || GetParent(win.Handle) != tb)
                {
                    Detach();
                    win = new StripWindow(this);
                    win.Attach(tb);
                    lastSize = Size.Empty;
                    lastPos = new Point(int.MinValue, 0);
                    Render();
                    return;
                }
                if (lastSize.Width > 0) Place(lastSize);
            }
            catch (Exception e) { Log.Write("battery taskbar check: " + e.Message); }
        }

        void Detach()
        {
            if (win != null && win.Handle != IntPtr.Zero) win.DestroyHandle();
            win = null;
        }

        internal void OnMessage(int msg)
        {
            if (msg == WM_REDRAW) Render();
            else if (msg == WM_QUIT_STRIP) Application.ExitThread();
        }

        internal void OnClick() { if (clicked != null) clicked(); }

        void Render()
        {
            if (win == null || win.Handle == IntPtr.Zero) return;
            List<BatteryInfo> list;
            int lowMark;
            lock (gate) { list = items; lowMark = low; }
            var shown = new List<BatteryInfo>();
            foreach (BatteryInfo b in list) if (b.Percent >= 0) shown.Add(b);
            RECT tr;
            GetWindowRect(win.Parent, out tr);
            int tbW = tr.R - tr.L, tbH = tr.B - tr.T;
            if (shown.Count == 0 || tbH <= 0 || tbH > tbW)   // nothing to show, or a vertical taskbar
            {
                ShowWindow(win.Handle, 0);
                lastSize = Size.Empty;
                return;
            }
            float scale = Dpi(win.Parent) / 96f;
            bool light = Glyphs.LightTaskbar();
            Color fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
            // The same size and face as the taskbar clock (13 px Segoe UI Variable at 100%) and the 16 px of the
            // notification area icons, in whole pixels so hinting can snap the stems to the pixel grid
            using (var text = new Font(TextFamily, (float)Math.Round(13f * scale), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var icon = new Font(Glyphs.Family, (float)Math.Round(16f * scale), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bolt = new Font(Glyphs.Family, (float)Math.Round(12f * scale), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                float pad = (float)Math.Round(6 * scale), gap = (float)Math.Round(16 * scale), iconGap = (float)Math.Round(5 * scale);
                var widths = new List<float[]>();
                float total = pad * 2;
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    foreach (BatteryInfo b in shown)
                    {
                        float iw = g.MeasureString(Glyphs.For(b), icon, 1000, fmt).Width;
                        float tw = g.MeasureString(b.Percent + "%", text, 1000, fmt).Width;
                        float bw = b.Charging && b.Percent < 100 ? g.MeasureString(Glyphs.Bolt, bolt, 1000, fmt).Width + 1 * scale : 0;
                        iw = (float)Math.Ceiling(iw); tw = (float)Math.Ceiling(tw); bw = (float)Math.Ceiling(bw);
                        widths.Add(new[] { iw, tw, bw });
                        total += iw + iconGap + tw + bw;
                    }
                    total += gap * (shown.Count - 1);
                }
                int w = (int)Math.Ceiling(total), h = tbH;
                // Premultiplied pixels: exactly what UpdateLayeredWindow blends, so text edges come out clean
                using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        // Hinted greyscale: crisp stems on the pixel grid, and edges that blend over any taskbar
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.TextContrast = 2;
                        float x = pad;
                        float textH = text.GetHeight(g), iconH = icon.GetHeight(g), boltH = bolt.GetHeight(g);
                        for (int i = 0; i < shown.Count; i++)
                        {
                            BatteryInfo b = shown[i];
                            Color c = b.Percent <= lowMark && !b.Charging ? Glyphs.Low(light) : fg;
                            if (b.Asleep) c = Color.FromArgb(light ? 120 : 130, c);
                            using (var brush = new SolidBrush(c))
                            {
                                g.DrawString(Glyphs.For(b), icon, brush, x, (float)Math.Round((h - iconH) / 2f + 1 * scale), fmt);
                                x += widths[i][0] + iconGap;
                                g.DrawString(b.Percent + "%", text, brush, x, (float)Math.Round((h - textH) / 2f), fmt);
                                x += widths[i][1];
                            }
                            if (widths[i][2] > 0)
                                using (var accent = new SolidBrush(Theme.Accent))
                                    g.DrawString(Glyphs.Bolt, bolt, accent, x + (float)Math.Round(1 * scale), (float)Math.Round((h - boltH) / 2f + 1 * scale), fmt);
                            x += widths[i][2] + gap;
                        }
                    }
                    Push(bmp);
                }
                Place(new Size(w, h));
            }
        }

        void Push(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = bmp.Width, biHeight = -bmp.Height,   // top-down
                    biPlanes = 1, biBitCount = 32,
                };
                IntPtr bits;
                hbmp = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hbmp == IntPtr.Zero) return;
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    int row = bmp.Width * 4;
                    for (int y = 0; y < bmp.Height; y++)
                        CopyMemory(new IntPtr(bits.ToInt64() + (long)y * row), new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), new IntPtr(row));
                }
                finally { bmp.UnlockBits(data); }
                old = SelectObject(memDc, hbmp);
                var size = new SIZE(bmp.Width, bmp.Height);
                var src = new POINT(0, 0);
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                UpdateLayeredWindow(win.Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Right next to the notification area, the full height of the taskbar
        void Place(Size size)
        {
            if (win == null || win.Handle == IntPtr.Zero) return;
            IntPtr tb = win.Parent;
            RECT tr, nr;
            GetWindowRect(tb, out tr);
            IntPtr notify = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            int right = notify != IntPtr.Zero && GetWindowRect(notify, out nr) && nr.R > nr.L ? nr.L : tr.R - (int)(200 * Dpi(tb) / 96f);
            var pt = new POINT(right - size.Width - (int)(2 * Dpi(tb) / 96f), tr.T);
            MapWindowPoints(IntPtr.Zero, tb, ref pt, 1);
            bool covered = GetWindow(win.Handle, 3) != IntPtr.Zero;   // GW_HWNDPREV: a sibling above us
            if (pt.X == lastPos.X && pt.Y == lastPos.Y && size == lastSize && !covered) return;
            SetWindowPos(win.Handle, IntPtr.Zero, pt.X, pt.Y, size.Width, size.Height, 0x0010 | 0x0040);   // HWND_TOP, NOACTIVATE | SHOWWINDOW
            lastPos = new Point(pt.X, pt.Y);
            lastSize = size;
        }

        static readonly string TextFamily = PickTextFamily();
        static string PickTextFamily()
        {
            using (var f = new Font("Segoe UI Variable Text", 10f))
                if (string.Equals(f.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase)) return f.Name;
            return "Segoe UI";
        }

        static uint Dpi(IntPtr h)
        {
            try { uint d = GetDpiForWindow(h); return d == 0 ? 96 : d; }
            catch (EntryPointNotFoundException) { return 96; }
        }

        public void Dispose()
        {
            quitting = true;
            Post(WM_QUIT_STRIP);
        }

        class StripWindow : NativeWindow
        {
            readonly TaskbarStrip owner;
            public IntPtr Parent;

            public StripWindow(TaskbarStrip owner) { this.owner = owner; }

            public void Attach(IntPtr taskbar)
            {
                Parent = taskbar;
                CreateHandle(new CreateParams
                {
                    Caption = "komorebi-tray batteries",
                    Parent = taskbar,
                    Style = 0x40000000 | 0x04000000,        // WS_CHILD | WS_CLIPSIBLINGS
                    ExStyle = 0x00080000,                   // WS_EX_LAYERED
                    Width = 1, Height = 1,
                });
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_REDRAW: case WM_QUIT_STRIP: owner.OnMessage(m.Msg); return;
                    case 0x0021: m.Result = (IntPtr)3; return;                 // WM_MOUSEACTIVATE: MA_NOACTIVATE
                    case 0x0202: case 0x0205: owner.OnClick(); return;         // left / right button up
                }
                base.WndProc(ref m);
            }
        }
    }

    // ---- notification area numbers (optional)
    // One small icon per device with its level drawn as a number. Red at the low mark, the accent colour while
    // charging, grey while the device sleeps; 100 is drawn as a full battery mark instead of three digits.

    class TrayNumbers : IDisposable
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        readonly ContextMenuStrip menu;
        readonly MethodInvoker open;
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, string> drawn = new Dictionary<string, string>();
        List<BatteryInfo> last = new List<BatteryInfo>();
        int low = 15;

        public TrayNumbers(ContextMenuStrip menu, MethodInvoker open) { this.menu = menu; this.open = open; }

        public void Redraw() { drawn.Clear(); Show(last, low); }

        public void Show(List<BatteryInfo> list, int lowMark)
        {
            last = list; low = lowMark;
            var keep = new HashSet<string>();
            bool light = Glyphs.LightTaskbar();
            foreach (BatteryInfo b in list)
            {
                if (b.Percent < 0) continue;
                keep.Add(b.Key);
                NotifyIcon ni;
                if (!icons.TryGetValue(b.Key, out ni))
                {
                    ni = new NotifyIcon { ContextMenuStrip = menu };
                    ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) open(); };
                    icons[b.Key] = ni;
                }
                string tip = b.Name + ": " + b.LevelText();
                ni.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
                string look = b.Percent + "|" + b.Charging + "|" + b.Asleep + "|" + (b.Percent <= low) + "|" + light + "|" + Theme.Accent.ToArgb() + "|" + SystemInformation.SmallIconSize.Width;
                string was;
                if (!drawn.TryGetValue(b.Key, out was) || was != look)
                {
                    Icon icon = Draw(b, light);
                    if (icon != null)
                    {
                        ni.Icon = icon;
                        IntPtr old;
                        if (handles.TryGetValue(b.Key, out old)) DestroyIcon(old);
                        handles[b.Key] = icon.Handle;
                        drawn[b.Key] = look;
                    }
                }
                ni.Visible = true;
            }
            var gone = new List<string>();
            foreach (string k in icons.Keys) if (!keep.Contains(k)) gone.Add(k);
            foreach (string k in gone) Remove(k);
        }

        void Remove(string key)
        {
            NotifyIcon ni;
            if (icons.TryGetValue(key, out ni)) { ni.Visible = false; ni.Dispose(); icons.Remove(key); }
            IntPtr h;
            if (handles.TryGetValue(key, out h)) { DestroyIcon(h); handles.Remove(key); }
            drawn.Remove(key);
        }

        Icon Draw(BatteryInfo b, bool light)
        {
            try
            {
                int size = Math.Max(16, SystemInformation.SmallIconSize.Width), big = size * 4;
                Color fg = light ? Color.FromArgb(20, 20, 20) : Color.White;
                if (b.Asleep) fg = light ? Color.FromArgb(130, 130, 130) : Color.FromArgb(140, 140, 146);
                else if (b.Charging) fg = Theme.Accent;
                else if (b.Percent <= low) fg = Glyphs.Low(light);
                using (var hi = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var final = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(hi))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        if (b.Percent >= 100) FullMark(g, big, fg);
                        else Number(g, big, b.Percent.ToString(), fg);
                    }
                    using (var g = Graphics.FromImage(final))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(hi, new Rectangle(0, 0, size, size));
                    }
                    return Icon.FromHandle(final.GetHicon());
                }
            }
            catch (Exception e) { Log.Write("battery icon: " + e.Message); return null; }
        }

        static void Number(Graphics g, int big, string text, Color c)
        {
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            {
                path.AddString(text, family, (int)FontStyle.Bold, big * 0.95f, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF r = path.GetBounds();
                float scale = Math.Min((big * 0.98f) / r.Width, (big * 0.80f) / r.Height);
                using (var m = new Matrix())
                {
                    m.Translate(big / 2f, big / 2f);
                    m.Scale(scale, scale);
                    m.Translate(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
            }
        }

        static void FullMark(Graphics g, int big, Color c)
        {
            float u = big / 16f;
            var body = new RectangleF(1 * u, 4.5f * u, 12.5f * u, 7f * u);
            using (var pen = new Pen(c, 1.4f * u))
            using (var path = Theme.Round(body, 1.6f * u)) g.DrawPath(pen, path);
            using (var brush = new SolidBrush(c))
            {
                g.FillRectangle(brush, 13.8f * u, 6.5f * u, 1.6f * u, 3f * u);
                using (var fill = Theme.Round(new RectangleF(2.6f * u, 6.1f * u, 9.3f * u, 3.8f * u), 0.8f * u)) g.FillPath(brush, fill);
            }
        }

        public void Dispose()
        {
            var keys = new List<string>(icons.Keys);
            foreach (string k in keys) Remove(k);
        }
    }

    // ---- Batteries window (Alt+Shift+B)
    // Same launcher look as Alt+K / Alt+C / Alt+Space: every device as a card with a bigger level, a search for
    // devices Windows has but the tray hasn't picked up yet, a GitHub report helper when a search finds nothing
    // new, and the battery settings (the same ones as Alt+C → Batteries).

    class BatteryWindow : LauncherForm
    {
        readonly BatteryTray tray;
        readonly BatteryView view;
        readonly Label hint = new Label();
        readonly Action changed;
        bool closing;
        public string Status;

        public BatteryWindow(BatteryTray tray)
        {
            this.tray = tray;
            Text = "komorebi · Batteries";
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            if (File.Exists(Paths.Icon)) try { Icon = new Icon(Paths.Icon); } catch { }

            hint.Dock = DockStyle.Bottom;
            hint.Height = 36;
            hint.BackColor = Theme.Bg;
            hint.Font = Theme.MonoSmall;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Padding = new Padding(24, 0, 0, 0);
            hint.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 16, 0, hint.Width - 16, 0); };

            view = new BatteryView(this, tray) { Dock = DockStyle.Fill };
            Controls.Add(view);
            Controls.Add(hint);
            ClientSize = new Size(BatteryView.W, FitHeight());

            changed = () => { if (!IsDisposed && !closing) { Refit(); view.Invalidate(); UpdateHint(); } };
            tray.Changed += changed;
            Shown += (s, e) => { Activate(); view.Focus(); UpdateHint(); };
            Deactivate += (s, e) => { if (!closing && !IsDisposed) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) Close(); })); };
            FormClosing += (s, e) => closing = true;
            FormClosed += (s, e) => tray.Changed -= changed;
            KeyDown += OnKey;
        }

        int FitHeight()
        {
            int max = (int)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.86);
            return Math.Min(max, view.ContentHeight + hint.Height + 4);
        }

        // More or fewer devices: grow or shrink around the same centre
        void Refit()
        {
            int h = FitHeight();
            if (h == ClientSize.Height || Opacity < 1) return;
            int d = h - ClientSize.Height;
            ClientSize = new Size(ClientSize.Width, h);
            Top -= d / 2;
        }

        public void UpdateHint()
        {
            if (Status != null) { hint.ForeColor = Theme.Accent; hint.Text = Status; return; }
            hint.ForeColor = Theme.Dim;
            hint.Text = tray.Searching ? "searching for devices…" : "r search for devices    ↑↓ setting    ←→ change    esc close";
        }

        public void SetStatus(string s) { Status = s; UpdateHint(); }

        public void CopyReport()
        {
            try { Clipboard.SetText(BatteryMonitor.Report(tray.Current)); SetStatus("✓ Details copied: paste them into the GitHub issue"); }
            catch (Exception e) { SetStatus("Couldn't copy: " + e.Message); }
        }

        public void OpenIssue()
        {
            CopyReport();
            Run.Shell(BatteryPrefs.IssueUrl, "");
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            bool handled = true;
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.R || e.KeyCode == Keys.F5) { Status = null; tray.Search(); }
            else if (e.KeyCode == Keys.Up) view.Move(-1);
            else if (e.KeyCode == Keys.Down) view.Move(1);
            else if (e.KeyCode == Keys.Left) view.Adjust(-1);
            else if (e.KeyCode == Keys.Right) view.Adjust(1);
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) view.Adjust(0);
            else if (e.Control && e.KeyCode == Keys.C) CopyReport();
            else handled = false;
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }
    }

    class BatteryView : Control
    {
        public const int W = 780;
        const int HeaderH = 62, Side = 24, Gap = 12, CardH = 112, RowH = 58, ReportH = 158;
        // Two wide cards per row for a handful of devices (long names stay readable), three when there are more
        int Cols { get { return Devices.Count <= 4 ? 2 : 3; } }
        int CardW { get { return (W - Side * 2 - Gap * (Cols - 1)) / Cols; } }
        static readonly Font Huge = new Font(Theme.Mono.FontFamily, 21f, FontStyle.Bold);
        static readonly Font IconBig = new Font(Glyphs.Family, 15f);
        static readonly Font IconSmall = new Font(Glyphs.Family, 10f);

        readonly BatteryWindow form;
        readonly BatteryTray tray;
        int scroll, selected, dragRow = -1;
        Rectangle searchRect, linkRect, copyRect;
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        string hot;

        static readonly string[] Labels = { "Show levels", "Low battery warning", "Low at" };
        static readonly string[] Descriptions = { "Taskbar, numbers in the notification area, or hidden", "A notification when a device runs low", "Level that counts as low (shown in red)" };

        public BatteryView(BatteryWindow form, BatteryTray tray)
        {
            this.form = form; this.tray = tray;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        List<BatteryInfo> Devices { get { return tray.Current; } }
        bool ShowReport { get { return !tray.Searching && tray.LastSearchNew == 0; } }

        int CardsHeight
        {
            get
            {
                int n = Devices.Count;
                if (n == 0) return 70;
                int rows = (n + Cols - 1) / Cols;
                return rows * CardH + (rows - 1) * Gap;
            }
        }

        public int ContentHeight
        {
            get { return HeaderH + 18 + CardsHeight + (ShowReport ? Gap + ReportH : 0) + 30 + 34 + Labels.Length * RowH + 14; }
        }

        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        // ---- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            scroll = Math.Min(scroll, MaxScroll);

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, HeaderH, Width, Height - HeaderH));
            int y = HeaderH + 18 - scroll;
            PaintCards(g, y);
            y += CardsHeight;
            if (ShowReport) { y += Gap; PaintReport(g, new Rectangle(Side, y, W - Side * 2, ReportH)); y += ReportH; }
            y += 30;
            TextRenderer.DrawText(g, "SETTINGS", Theme.Section, new Point(Side + 2, y), Theme.Dim, TextFormatFlags.NoPrefix);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Side + 80, y + 8, W - Side, y + 8);
            y += 34;
            PaintSettings(g, y);
            g.Clip = clip;

            PaintHeader(g);

            int max = MaxScroll;
            if (max > 0)
            {
                int track = Height - HeaderH - 12, th = Math.Max(30, (int)((long)track * (Height - HeaderH) / ContentHeight));
                int ty = HeaderH + 6 + (int)((long)(track - th) * scroll / max);
                using (var path = Theme.Round(new RectangleF(Width - 7, ty, 3, th), 1.5f)) using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, path);
            }
        }

        void PaintHeader(Graphics g)
        {
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, 0, Width, HeaderH);
            TextRenderer.DrawText(g, "Batteries", Theme.MonoBig, new Point(26, 21), Theme.Text, TextFormatFlags.NoPrefix);
            int withLevel = 0;
            foreach (BatteryInfo d in Devices) if (d.Percent >= 0) withLevel++;
            string count = !tray.Polled ? "looking…" : Devices.Count == 0 ? "no devices" : Devices.Count + (Devices.Count == 1 ? " device" : " devices");
            int tw = TextRenderer.MeasureText(g, "Batteries", Theme.MonoBig, Size.Empty, TextFormatFlags.NoPrefix).Width;
            TextRenderer.DrawText(g, "·  " + count, Theme.Mono, new Point(26 + tw + 6, 24), Theme.Dim, TextFormatFlags.NoPrefix);

            string label = tray.Searching ? "Searching…" : "Search for devices";
            int bw = TextRenderer.MeasureText(label, Theme.Mono).Width + 46;
            searchRect = new Rectangle(W - Side - bw, 15, bw, 32);
            bool hover = hot == "search";
            using (var path = Theme.Round(searchRect, 6))
            using (var b = new SolidBrush(hover ? Theme.Hover : Theme.Surface))
            using (var p = new Pen(hover || tray.Searching ? Theme.Accent : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
            TextRenderer.DrawText(g, Glyphs.Refresh, IconSmall, new Rectangle(searchRect.X + 10, searchRect.Y, 22, searchRect.Height), Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, label, Theme.Mono, new Rectangle(searchRect.X + 34, searchRect.Y, searchRect.Width - 40, searchRect.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 16, HeaderH - 1, Width - 16, HeaderH - 1);
        }

        void PaintCards(Graphics g, int top)
        {
            if (Devices.Count == 0)
            {
                string msg = !tray.Polled ? "Looking for wireless devices…" :
                    "No wireless devices found. Plug in a dongle or connect over Bluetooth, then search.";
                TextRenderer.DrawText(g, msg, Theme.Mono, new Rectangle(Side, top, W - Side * 2, 60), Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            for (int i = 0; i < Devices.Count; i++)
            {
                int col = i % Cols, row = i / Cols;
                var r = new Rectangle(Side + col * (CardW + Gap), top + row * (CardH + Gap), CardW, CardH);
                if (r.Bottom < HeaderH || r.Y > Height) continue;
                PaintCard(g, Devices[i], r);
            }
        }

        void PaintCard(Graphics g, BatteryInfo b, Rectangle r)
        {
            bool level = b.Percent >= 0, low = level && b.Percent <= tray.Low && !b.Charging;
            using (var path = Theme.Round(r, 8))
            using (var fill = new SolidBrush(Theme.Surface))
            using (var pen = new Pen(low ? Theme.Danger : Theme.Border)) { g.FillPath(fill, path); g.DrawPath(pen, path); }

            Color iconColor = !level || b.Asleep ? Theme.Dim : low ? Theme.Danger : Theme.Accent;
            TextRenderer.DrawText(g, Glyphs.For(b), IconBig, new Rectangle(r.X + 12, r.Y + 12, 30, 30), iconColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, b.Name, Theme.Mono, new Rectangle(r.X + 48, r.Y + 11, r.Width - 58, 20), Theme.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, b.Via ?? "", Theme.MonoSmall, new Rectangle(r.X + 48, r.Y + 31, r.Width - 58, 18), Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (!level)
            {
                TextRenderer.DrawText(g, b.Note ?? "no reading", Theme.MonoSmall, new Rectangle(r.X + 14, r.Y + 56, r.Width - 28, 46), Theme.Dim,
                    TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                return;
            }

            string pct = b.Percent + "%";
            Color pctColor = low ? Theme.Danger : b.Asleep ? Theme.Dim : Theme.Text;
            TextRenderer.DrawText(g, pct, Huge, new Point(r.X + 10, r.Y + 52), pctColor, TextFormatFlags.NoPrefix);
            int pw = TextRenderer.MeasureText(g, pct, Huge, Size.Empty, TextFormatFlags.NoPrefix).Width;
            var states = new List<string>();
            if (b.Charging) states.Add(b.Percent >= 100 ? "full" : "charging");
            if (b.Asleep) states.Add("asleep");
            if (b.Approx) states.Add("approximate");
            int sx = r.X + 10 + pw + 2;
            if (b.Charging && b.Percent < 100)
            {
                TextRenderer.DrawText(g, Glyphs.Bolt, IconSmall, new Rectangle(sx, r.Y + 62, 16, 20), Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                sx += 16;
            }
            TextRenderer.DrawText(g, string.Join(" · ", states.ToArray()), Theme.MonoSmall, new Rectangle(sx + 2, r.Y + 63, r.Right - sx - 12, 18), Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

            var track = new RectangleF(r.X + 14, r.Bottom - 16, r.Width - 28, 5);
            using (var path = Theme.Round(track, 2.5f)) using (var bg = new SolidBrush(Theme.Bg)) g.FillPath(bg, path);
            float fw = Math.Max(5, track.Width * Math.Min(100, b.Percent) / 100f);
            using (var path = Theme.Round(new RectangleF(track.X, track.Y, fw, track.Height), 2.5f))
            using (var fb = new SolidBrush(low ? Theme.Danger : b.Asleep ? Theme.Border : Theme.Accent)) g.FillPath(fb, path);
        }

        void PaintReport(Graphics g, Rectangle r)
        {
            using (var path = Theme.Round(r, 8))
            using (var fill = new SolidBrush(Theme.Surface))
            using (var pen = new Pen(Theme.Border)) { g.FillPath(fill, path); g.DrawPath(pen, path); }
            TextRenderer.DrawText(g, "The search found nothing new. Is your device missing?", Theme.Mono, new Point(r.X + 16, r.Y + 14), Theme.Text, TextFormatFlags.NoPrefix);

            string link = "Report it on GitHub";
            Size ls = TextRenderer.MeasureText(g, link, Theme.Mono, Size.Empty, TextFormatFlags.NoPrefix);
            linkRect = new Rectangle(r.X + 16, r.Y + 40, ls.Width + 22, ls.Height);
            Color lc = hot == "link" ? Theme.AccentHover : Theme.Accent;
            TextRenderer.DrawText(g, link, Theme.Mono, new Point(linkRect.X, linkRect.Y), lc, TextFormatFlags.NoPrefix);
            using (var p = new Pen(lc)) g.DrawLine(p, linkRect.X, linkRect.Y + ls.Height - 2, linkRect.X + ls.Width - 6, linkRect.Y + ls.Height - 2);
            TextRenderer.DrawText(g, Glyphs.Link, IconSmall, new Rectangle(linkRect.X + ls.Width - 2, linkRect.Y, 20, ls.Height), lc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "and paste these details:", Theme.Mono, new Point(linkRect.Right + 6, linkRect.Y), Theme.TextSoft, TextFormatFlags.NoPrefix);

            string cl = "Copy details";
            int cw = TextRenderer.MeasureText(cl, Theme.MonoSmall).Width + 40;
            copyRect = new Rectangle(r.Right - 16 - cw, r.Y + 12, cw, 28);
            bool ch = hot == "copy";
            using (var path = Theme.Round(copyRect, 6))
            using (var b = new SolidBrush(ch ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
            TextRenderer.DrawText(g, Glyphs.Copy, IconSmall, new Rectangle(copyRect.X + 8, copyRect.Y, 18, copyRect.Height), Theme.OnAccent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, cl, Theme.MonoSmall, new Rectangle(copyRect.X + 28, copyRect.Y, copyRect.Width - 30, copyRect.Height), Theme.OnAccent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // The first lines of the template, so it's clear what gets shared
            var box = new Rectangle(r.X + 16, r.Y + 68, r.Width - 32, r.Height - 82);
            using (var path = Theme.Round(box, 6)) using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, path);
            string[] lines = BatteryMonitor.Report(tray.Current).Replace("\r", "").Split('\n');
            var shown = new List<string>();
            foreach (string l in lines)
            {
                if (l.Length == 0 || l.StartsWith("```")) continue;
                shown.Add(l);
                if (shown.Count == 4) break;
            }
            shown.Add("…");
            TextRenderer.DrawText(g, string.Join("\n", shown.ToArray()), Theme.MonoSmall, new Rectangle(box.X + 12, box.Y + 8, box.Width - 24, box.Height - 12), Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        void PaintSettings(Graphics g, int top)
        {
            rowRects.Clear();
            int ctrlW = 300, ctrlX = W - Side - ctrlW;
            for (int i = 0; i < Labels.Length; i++)
            {
                var row = new Rectangle(Side - 8, top + i * RowH, W - Side * 2 + 16, RowH - 4);
                rowRects.Add(row);
                bool sel = i == selected;
                if (sel)
                {
                    using (var path = Theme.Round(row, 7)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, row.X, row.Y + 12, 3, row.Height - 24);
                }
                TextRenderer.DrawText(g, Labels[i], Theme.Mono, new Point(Side + 6, row.Y + 9), sel ? Theme.Text : Theme.TextSoft, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Descriptions[i], Theme.MonoSmall, new Rectangle(Side + 6, row.Y + 31, ctrlX - Side - 16, 18), Theme.Dim,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                var area = new Rectangle(ctrlX, row.Y, ctrlW, row.Height);
                int cy = area.Y + area.Height / 2;
                if (i == 0)
                {
                    var segs = Segments(area);
                    for (int k = 0; k < segs.Count; k++)
                    {
                        bool active = BatteryPrefs.DisplayOptions[k] == tray.Display;
                        using (var path = Theme.Round(segs[k], 6))
                        using (var b = new SolidBrush(active ? Theme.Accent : Theme.Surface))
                        using (var p = new Pen(active ? Theme.Accent : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                        TextRenderer.DrawText(g, BatteryPrefs.DisplayOptions[k], Theme.MonoSmall, segs[k], active ? Theme.OnAccent : Theme.TextSoft,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }
                else if (i == 1)
                {
                    bool v = tray.Warn;
                    var pill = new RectangleF(area.Right - 46, cy - 11, 44, 22);
                    using (var path = Theme.Round(pill, 11))
                    using (var b = new SolidBrush(v ? Theme.Accent : Theme.Surface))
                    using (var p = new Pen(v ? Theme.Accent : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                    using (var knob = new SolidBrush(v ? Color.White : Theme.Dim)) g.FillEllipse(knob, v ? pill.Right - 19 : pill.X + 3, pill.Y + 3, 16, 16);
                    TextRenderer.DrawText(g, v ? "On" : "Off", Theme.MonoSmall, new Rectangle(area.Right - 100, cy - 10, 48, 20), v ? Theme.Text : Theme.Dim,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else
                {
                    var track = Track(area);
                    float t = (tray.Low - 5) / 45f;
                    float kx = track.X + t * track.Width;
                    using (var path = Theme.Round(new RectangleF(track.X, cy - 2, track.Width, 4), 2)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                    if (kx > track.X + 1)
                        using (var path = Theme.Round(new RectangleF(track.X, cy - 2, kx - track.X, 4), 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                    using (var knob = new SolidBrush(Color.White)) g.FillEllipse(knob, kx - 7, cy - 7, 14, 14);
                    using (var ring = new Pen(Theme.Accent, 2)) g.DrawEllipse(ring, kx - 7, cy - 7, 14, 14);
                    TextRenderer.DrawText(g, tray.Low + "%", Theme.Mono, new Rectangle(area.Right - 74, cy - 11, 74, 22), Theme.Text,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        static List<Rectangle> Segments(Rectangle area)
        {
            var list = new List<Rectangle>();
            int n = BatteryPrefs.DisplayOptions.Length, gap = 4, w = (area.Width - gap * (n - 1)) / n, cy = area.Y + area.Height / 2;
            for (int i = 0; i < n; i++) list.Add(new Rectangle(area.X + i * (w + gap), cy - 14, w, 28));
            return list;
        }

        static Rectangle Track(Rectangle area) { return new Rectangle(area.X + 6, area.Y + area.Height / 2 - 8, area.Width - 96, 16); }

        // ---- changes (saved straight away, like the volume mixer; no komorebi reload needed)

        public void Move(int d) { selected = Math.Max(0, Math.Min(Labels.Length - 1, selected + d)); EnsureVisible(); Invalidate(); }

        void EnsureVisible()
        {
            if (rowRects.Count <= selected) return;
            Rectangle r = rowRects[selected];
            if (r.Bottom > Height) scroll = Math.Min(MaxScroll, scroll + r.Bottom - Height + 8);
            else if (r.Y < HeaderH) scroll = Math.Max(0, scroll - (HeaderH - r.Y) - 8);
        }

        // dir 0: Enter / Space
        public void Adjust(int dir)
        {
            form.Status = null;
            if (selected == 0)
            {
                int i = Array.IndexOf(BatteryPrefs.DisplayOptions, tray.Display);
                int n = BatteryPrefs.DisplayOptions.Length;
                SetDisplay(BatteryPrefs.DisplayOptions[((i < 0 ? 0 : i) + (dir == 0 ? 1 : dir) + n) % n]);
            }
            else if (selected == 1) SetWarn(dir == 0 ? !tray.Warn : dir > 0);
            else SetLow(tray.Low + (dir == 0 ? 5 : dir * 5));
            form.UpdateHint();
        }

        void SetDisplay(string v) { if (v == tray.Display) return; BatteryPrefs.Set(BatteryPrefs.Display, v); tray.LoadPrefs(); }
        void SetWarn(bool v) { if (v == tray.Warn) return; BatteryPrefs.Set(BatteryPrefs.Warn, v); tray.LoadPrefs(); }
        void SetLow(int v)
        {
            v = Math.Max(5, Math.Min(50, (int)Math.Round(v / 5.0) * 5));
            if (v == tray.Low) return;
            BatteryPrefs.Set(BatteryPrefs.Low, v);
            tray.LoadPrefs();
        }

        // ---- mouse

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * 48));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (searchRect.Contains(e.Location)) { form.Status = null; tray.Search(); return; }
            if (e.Y < HeaderH) return;
            if (ShowReport && linkRect.Contains(e.Location)) { form.OpenIssue(); return; }
            if (ShowReport && copyRect.Contains(e.Location)) { form.CopyReport(); Invalidate(); return; }
            for (int i = 0; i < rowRects.Count; i++)
            {
                if (!rowRects[i].Contains(e.Location)) continue;
                selected = i;
                var area = new Rectangle(W - Side - 300, rowRects[i].Y, 300, rowRects[i].Height);
                if (e.X >= area.X - 8)
                {
                    if (i == 0)
                    {
                        var segs = Segments(area);
                        for (int k = 0; k < segs.Count; k++) if (segs[k].Contains(e.Location)) SetDisplay(BatteryPrefs.DisplayOptions[k]);
                    }
                    else if (i == 1) SetWarn(!tray.Warn);
                    else { dragRow = i; Capture = true; SlideTo(area, e.X); }
                }
                form.Status = null;
                form.UpdateHint();
                Invalidate();
                return;
            }
        }

        void SlideTo(Rectangle area, int x)
        {
            var track = Track(area);
            double t = Math.Max(0, Math.Min(1, (x - track.X) / (double)track.Width));
            SetLow(5 + (int)Math.Round(t * 45));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragRow >= 0 && rowRects.Count > dragRow)
                SlideTo(new Rectangle(W - Side - 300, rowRects[dragRow].Y, 300, rowRects[dragRow].Height), e.X);
            string h = searchRect.Contains(e.Location) ? "search" :
                ShowReport && linkRect.Contains(e.Location) ? "link" : ShowReport && copyRect.Contains(e.Location) ? "copy" : null;
            bool row = false;
            foreach (var r in rowRects) if (r.Contains(e.Location) && e.Y >= HeaderH) row = true;
            Cursor = h != null || row ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragRow >= 0) { dragRow = -1; Capture = false; }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != null) { hot = null; Invalidate(); } }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    }
}
