// What the battery readings look like, ported from Wireless Battery (widget.cpp and main.cpp's tray / menu code;
// GPL-3.0-or-later): the readout embedded in the taskbar next to the notification area (the default), one icon per
// device in the notification area (also the fallback when the taskbar is vertical), the click menu, plus the
// Batteries window (Alt+Shift+B) with a bigger view of every device, a search, a "report a device" helper and the
// battery settings.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KomorebiTray
{
    static class BatteryPrefs
    {
        public const string Display = "battery_display", Warn = "battery_warn", Low = "battery_low",
            Background = "battery_background", HideLastKnown = "battery_hide_last_known", Hidden = "battery_hidden",
            TaskbarOffset = "battery_taskbar_offset";
        public const string Taskbar = "Taskbar", TrayIcons = "Tray icons", Off = "Off";
        public static readonly string[] DisplayOptions = { Taskbar, TrayIcons, Off };
        public const string IssueUrl = "https://github.com/RaZdai/komorebi-tray/issues/new?title=Battery%3A%20device%20not%20found&labels=battery";

        public static string GetDisplay(Dictionary<string, object> p)
        {
            object v;
            string s = p.TryGetValue(Display, out v) ? Convert.ToString(v) : null;
            return Array.IndexOf(DisplayOptions, s) >= 0 ? s : Taskbar;
        }
        public static bool GetBool(Dictionary<string, object> p, string key, bool def)
        {
            object v;
            try { return p.TryGetValue(key, out v) ? Convert.ToBoolean(v) : def; } catch { return def; }
        }
        public static int GetLow(Dictionary<string, object> p)
        {
            object v;
            try { return p.TryGetValue(Low, out v) ? Math.Max(5, Math.Min(50, Convert.ToInt32(v))) : 15; } catch { return 15; }
        }
        public static int GetOffset(Dictionary<string, object> p)
        {
            object v;
            try { return p.TryGetValue(TaskbarOffset, out v) ? Convert.ToInt32(v) : 0; } catch { return 0; }
        }
        public static HashSet<string> GetHidden(Dictionary<string, object> p)
        {
            var set = new HashSet<string>();
            object v;
            var list = p.TryGetValue(Hidden, out v) ? v as System.Collections.IList : null;
            if (list != null) foreach (object o in list) if (o != null) set.Add(Convert.ToString(o));
            return set;
        }
        public static void Set(string key, object value) { AccentBorder.UpdatePrefs(p => p[key] = value); }
        public static void SetHidden(HashSet<string> keys)
        {
            var list = new List<object>();
            foreach (string k in keys) list.Add(k);
            list.Sort((a, b) => string.CompareOrdinal((string)a, (string)b));
            AccentBorder.UpdatePrefs(p => p[Hidden] = list);
        }
    }

    // Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10: the same code points in both
    static class Glyphs
    {
        public static readonly string Family = Pick();
        public const string Bolt = "", Refresh = "", Link = "", Copy = "", Hidden = "";

        static string Pick()
        {
            foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                using (var f = new Font(n, 10f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return "Segoe UI Symbol";
        }

        // widget.cpp's Glyph(): the same icon for each kind
        public static string For(DeviceKind k, bool bluetooth)
        {
            switch (k)
            {
                case DeviceKind.Headset: return "";    // Headphone
                case DeviceKind.Mouse: return "";      // Mouse
                case DeviceKind.Keyboard: return "";   // KeyboardClassic
                case DeviceKind.Gamepad: return "";    // Game
                case DeviceKind.Phone: return "";      // CellPhone
                default: return bluetooth ? "" : "";   // Bluetooth / Devices
            }
        }

        public static bool LightTaskbar()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("SystemUsesLightTheme") : null;
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }
    }

    // GDI text as greyscale coverage masks (white on black, brightness = alpha), like the original renders the
    // taskbar text and the tray icon digits: crisp hinted glyphs that blend over any taskbar
    static class Gdi
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern int GetTextFaceW(IntPtr dc, int n, StringBuilder face);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32W(IntPtr dc, string s, int n, out SIZE size);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool TextOutW(IntPtr dc, int x, int y, string s, int n);
        [DllImport("gdi32.dll")] public static extern int SetTextCharacterExtra(IntPtr dc, int extra);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
        [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        // Creates a font, falling back when the preferred face is not installed (GDI would silently substitute)
        public static IntPtr MakeFont(int px, int weight, string face, string fallback)
        {
            foreach (string f in new[] { face, fallback })
            {
                IntPtr h = CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, f);   // DEFAULT_CHARSET, ANTIALIASED_QUALITY
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                IntPtr o = SelectObject(dc, h);
                var got = new StringBuilder(64);
                GetTextFaceW(dc, 64, got);
                SelectObject(dc, o);
                DeleteDC(dc);
                if (string.Equals(got.ToString(), f, StringComparison.OrdinalIgnoreCase) || f == fallback) return h;
                DeleteObject(h);
            }
            return IntPtr.Zero;
        }

        public class Mask { public int W, H; public byte[] A = new byte[0]; }

        // Greyscale coverage mask of a string, cropped to its ink bounds
        public static Mask RenderMask(string s, IntPtr font, int charExtra = 0)
        {
            var m = new Mask();
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr of = SelectObject(dc, font);
            SetTextCharacterExtra(dc, charExtra);
            SIZE sz;
            GetTextExtentPoint32W(dc, s, s.Length, out sz);
            int W = sz.cx + 8, H = sz.cy + 8;
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            IntPtr ob = SelectObject(dc, bmp);
            var px = new int[W * H];
            Marshal.Copy(px, 0, bits, px.Length);
            SetBkMode(dc, 1);   // TRANSPARENT
            SetTextColor(dc, 0x00FFFFFF);
            TextOutW(dc, 4, 4, s, s.Length);
            GdiFlush();
            Marshal.Copy(bits, px, 0, px.Length);
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if ((px[y * W + x] & 0xFF) != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (x1 >= 0)
            {
                m.W = x1 - x0 + 1;
                m.H = y1 - y0 + 1;
                m.A = new byte[m.W * m.H];
                for (int y = 0; y < m.H; y++)
                    for (int x = 0; x < m.W; x++) m.A[y * m.W + x] = (byte)(px[(y0 + y) * W + x0 + x] & 0xFF);
            }
            SelectObject(dc, ob);
            SelectObject(dc, of);
            DeleteObject(bmp);
            DeleteDC(dc);
            return m;
        }

        // Premultiplied BGRA, source-over blending
        public class Canvas
        {
            public readonly int W, H;
            public readonly uint[] Px;
            public Canvas(int w, int h) { W = w; H = h; Px = new uint[w * h]; }
            public void Blend(int x, int y, Color c, int a)
            {
                if (x < 0 || y < 0 || x >= W || y >= H || a <= 0) return;
                uint d = Px[y * W + x];
                int inv = 255 - a;
                int da = (int)(d >> 24), dr = (int)((d >> 16) & 255), dg = (int)((d >> 8) & 255), db = (int)(d & 255);
                int oa = a + da * inv / 255;
                int orr = c.R * a / 255 + dr * inv / 255;
                int og = c.G * a / 255 + dg * inv / 255;
                int ob = c.B * a / 255 + db * inv / 255;
                Px[y * W + x] = ((uint)oa << 24) | ((uint)orr << 16) | ((uint)og << 8) | (uint)ob;
            }
            public void Draw(Mask m, int x, int y, Color c, int alpha)
            {
                for (int j = 0; j < m.H; j++)
                    for (int i = 0; i < m.W; i++) Blend(x + i, y + j, c, m.A[j * m.W + i] * alpha / 255);
            }
        }
    }

    // ---- the coordinator: entries (main.cpp's g_entries) and what shows them (UI thread)

    class BatteryTray : IDisposable
    {
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr m, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] static extern bool CheckMenuRadioItem(IntPtr m, uint first, uint last, uint check, uint flags);
        [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

        const int LowBatteryDefault = 15;

        public class Shown
        {
            public DeviceStatus S;
            public int Percent;      // shown value: current, or the last known one (dimmed)
            public bool Dim, Hidden, Low;
            public string Status = "";
        }

        class Entry { public DeviceStatus St; public int LastPct = -1; public long LastSeen; public bool LowWarned; }

        readonly Control invoker;
        readonly NotifyIcon main;
        readonly MethodInvoker openWindow;
        readonly SortedDictionary<string, Entry> entries = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        HashSet<string> hidden = new HashSet<string>();
        TaskbarWidget widget;
        TrayIconSet trayIcons;
        DeviceWatcher watcher;
        readonly System.Windows.Forms.Timer reconcile = new System.Windows.Forms.Timer { Interval = 1000 };
        bool useWidget, light, disposed;
        HashSet<string> beforeSearch;

        public string Display = BatteryPrefs.Taskbar;
        public bool Warn = true, Background, HideLastKnown;
        public int Low = LowBatteryDefault;
        public bool Searching, Polled;
        public int LastSearchNew = -1;
        public List<Shown> Current = new List<Shown>();
        public event Action Changed;

        public BatteryTray(Control invoker, NotifyIcon main, MethodInvoker openWindow)
        {
            this.invoker = invoker; this.main = main; this.openWindow = openWindow;
            light = Glyphs.LightTaskbar();
            LoadPrefs();
            try { watcher = new DeviceWatcher(() => BatteryMonitor.RefreshNow()); } catch (Exception e) { Log.Write("battery device watch: " + e.Message); }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            reconcile.Tick += (s, e) => Reconcile();
            reconcile.Start();
            BatteryMonitor.Results += OnResults;
            BatteryMonitor.Start();
        }

        public void LoadPrefs()
        {
            if (disposed) return;
            var p = AccentBorder.LoadPrefs();
            Display = BatteryPrefs.GetDisplay(p);
            Warn = BatteryPrefs.GetBool(p, BatteryPrefs.Warn, true);
            Low = BatteryPrefs.GetLow(p);
            Background = BatteryPrefs.GetBool(p, BatteryPrefs.Background, false);
            HideLastKnown = BatteryPrefs.GetBool(p, BatteryPrefs.HideLastKnown, false);
            hidden = BatteryPrefs.GetHidden(p);
            if (widget != null) widget.SetBackground(Background);
            Reconcile();
            ApplyAll();
        }

        // ---- results (main.cpp OnResults)

        void OnResults(List<DeviceStatus> results)
        {
            try { invoker.BeginInvoke((MethodInvoker)(() => { if (!disposed) Apply(results); })); } catch { }
        }

        void Apply(List<DeviceStatus> results)
        {
            Polled = true;
            var seen = new HashSet<string>();
            long now = Bat.Now;
            foreach (DeviceStatus s in results)
            {
                Entry e;
                if (!entries.TryGetValue(s.Key, out e)) entries[s.Key] = e = new Entry();
                e.St = s;
                if (s.Percent >= 0) e.LastPct = s.Percent;
                e.LastSeen = now;
                seen.Add(s.Key);
            }
            var gone = new List<string>();
            foreach (var kv in entries)
            {
                if (seen.Contains(kv.Key)) continue;
                Entry e = kv.Value;
                // Some receivers drop sleeping devices from the link; keep showing the last value for a while
                bool inGrace = e.St.GraceSec > 0 && e.St.Connected && now - e.LastSeen < (long)e.St.GraceSec * 1000;
                if (inGrace) { e.St.Percent = -1; e.St.Asleep = true; }
                else gone.Add(kv.Key);
            }
            foreach (string k in gone) entries.Remove(k);
            if (Searching)
            {
                Searching = false;
                int added = 0;
                foreach (string k in entries.Keys) if (beforeSearch == null || !beforeSearch.Contains(k)) added++;
                LastSearchNew = added;
            }
            ApplyAll();
        }

        public void Search()
        {
            if (Searching) return;
            Searching = true;
            LastSearchNew = -1;
            beforeSearch = new HashSet<string>(entries.Keys);
            BatteryMonitor.RefreshNow();
            Fire();
        }

        public void RefreshNow() { BatteryMonitor.RefreshNow(); }

        static string StatusText(Entry e)
        {
            DeviceStatus s = e.St;
            if (!s.Connected) return "off / not connected";
            string t;
            if (s.LevelText.Length > 0 && s.Percent >= 0) t = s.LevelText;
            else if (s.Percent >= 0) t = s.Percent + "%";
            else if (e.LastPct >= 0) t = e.LastPct + "% (last known)";
            else t = "unknown";
            if (s.Detail.Length > 0 && s.Percent >= 0) t += " (" + s.Detail + ")";
            if (s.Charging) t += ", charging";
            if (s.Asleep) t += ", asleep";
            return t;
        }

        bool IsEntryHidden(Entry e)
        {
            return hidden.Contains(e.St.Key) || (HideLastKnown && (e.St.Asleep || (e.St.Percent < 0 && e.LastPct >= 0)));
        }

        static int KindOrder(DeviceKind k)   // headset first, then mouse, ... (the left-to-right layout)
        {
            switch (k)
            {
                case DeviceKind.Headset: return 0;
                case DeviceKind.Mouse: return 1;
                case DeviceKind.Keyboard: return 2;
                case DeviceKind.Gamepad: return 3;
                case DeviceKind.Phone: return 4;
                default: return 5;
            }
        }

        void ApplyAll()
        {
            var shown = new List<Shown>();
            foreach (var kv in entries)
            {
                Entry e = kv.Value;
                int pct = e.St.Percent >= 0 ? e.St.Percent : e.LastPct;
                var sh = new Shown { S = e.St, Percent = pct, Dim = e.St.Percent < 0, Hidden = IsEntryHidden(e), Status = StatusText(e) };
                sh.Low = !e.St.Charging && pct >= 0 && pct <= Low;
                shown.Add(sh);
                // Low battery notification, once per drop below the mark
                if (e.St.Charging || pct > Low + 10) e.LowWarned = false;
                if (Warn && e.St.Connected && !sh.Hidden && !e.St.Charging && e.St.Percent >= 0 && e.St.Percent <= Low && !e.LowWarned)
                {
                    e.LowWarned = true;
                    main.ShowBalloonTip(5000, "Low battery", e.St.Name + " is at " + e.St.Percent + "%", ToolTipIcon.Warning);
                }
            }
            shown.Sort((a, b) =>
            {
                int ka = KindOrder(a.S.Kind), kb = KindOrder(b.S.Kind);
                return ka != kb ? ka.CompareTo(kb) : string.CompareOrdinal(a.S.Name, b.S.Name);
            });
            Current = shown;
            UpdateWidget();
            UpdateTrayIcons();
            Fire();
        }

        void Fire() { var h = Changed; if (h != null) h(); }

        void UpdateWidget()
        {
            if (widget == null) return;
            var items = new List<TaskbarWidget.Item>();
            var tip = new StringBuilder();
            foreach (Shown d in Current)
            {
                if (!d.S.Connected || d.Hidden) continue;
                items.Add(new TaskbarWidget.Item
                {
                    Kind = d.S.Kind, Percent = d.Percent, Text = d.S.Percent >= 0 ? d.S.LevelText : "", Charging = d.S.Charging,
                    Dim = d.Dim, Low = d.Low, Bluetooth = d.S.Via == "Bluetooth",
                });
                if (tip.Length > 0) tip.Append("\n");
                tip.Append(d.S.Name + ": " + d.Status);
            }
            widget.SetItems(items, tip.Length == 0 ? "Batteries: no devices with battery info found" : tip.ToString());
        }

        bool TrayIconsWanted { get { return Display == BatteryPrefs.TrayIcons || (Display == BatteryPrefs.Taskbar && !useWidget); } }

        void UpdateTrayIcons()
        {
            if (!TrayIconsWanted) { if (trayIcons != null) { trayIcons.Dispose(); trayIcons = null; } return; }
            if (trayIcons == null) trayIcons = new TrayIconSet(ShowMenu);
            var list = new List<Shown>();
            foreach (Shown d in Current) if (d.S.Connected && !d.Hidden) list.Add(d);
            trayIcons.Show(list, light, Low);
        }

        // Makes the display match the chosen mode; falls back to tray icons when the taskbar readout can't be created
        // (vertical taskbar, Explorer not ready). Once a second, like the original.
        void Reconcile()
        {
            if (disposed) return;
            bool want = Display == BatteryPrefs.Taskbar;
            if (want && widget == null)
            {
                widget = new TaskbarWidget(() => { try { invoker.BeginInvoke((MethodInvoker)ShowMenu); } catch { } }, light, Background,
                    BatteryPrefs.GetOffset(AccentBorder.LoadPrefs()));
                UpdateWidget();
            }
            else if (!want && widget != null) { widget.Dispose(); widget = null; }
            bool alive = want && widget != null && widget.Alive;
            if (alive != useWidget)
            {
                useWidget = alive;
                UpdateTrayIcons();
                UpdateWidget();
            }
            else if (Display != BatteryPrefs.Off) UpdateTrayIcons();
        }

        void OnPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General && e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
            try
            {
                invoker.BeginInvoke((MethodInvoker)(() =>
                {
                    if (disposed) return;
                    bool l = Glyphs.LightTaskbar();
                    if (l == light) return;
                    light = l;
                    if (widget != null) widget.SetLight(l);
                    if (trayIcons != null) trayIcons.Redraw(light, Low);
                }));
            }
            catch { }
        }

        // ---- the click menu (main.cpp ShowMenu)

        const uint IdRefresh = 100, IdHideLastKnown = 101, IdBackground = 102, IdModeWidget = 103, IdModeTray = 104, IdWindow = 105, IdDeviceFirst = 1000;

        public void ShowMenu()
        {
            if (disposed) return;
            try { SetPreferredAppMode(light ? 3 : 2); FlushMenuThemes(); } catch { }
            IntPtr m = CreatePopupMenu();
            var keys = new List<string>();
            const uint STRING = 0, GRAYED = 1, CHECKED = 8, SEPARATOR = 0x800;
            AppendMenuW(m, STRING | GRAYED, UIntPtr.Zero, entries.Count == 0 ? "No devices with battery info found" : "Click a device to show or hide it");
            foreach (var kv in entries)
            {
                Entry e = kv.Value;
                string line = e.St.Name + ":  " + StatusText(e) + "   [" + e.St.Via + "]";
                AppendMenuW(m, STRING | (hidden.Contains(kv.Key) ? 0 : CHECKED), (UIntPtr)(IdDeviceFirst + (uint)keys.Count), line.Replace("&", "&&"));
                keys.Add(kv.Key);
            }
            AppendMenuW(m, STRING | (HideLastKnown ? CHECKED : 0), (UIntPtr)IdHideLastKnown, "Hide last known / asleep devices");
            AppendMenuW(m, SEPARATOR, UIntPtr.Zero, null);
            bool wantWidget = Display == BatteryPrefs.Taskbar;
            AppendMenuW(m, STRING, (UIntPtr)IdModeWidget, "Show on the taskbar (icon + percentage)");
            AppendMenuW(m, STRING, (UIntPtr)IdModeTray, "Show as tray icons");
            if (Display != BatteryPrefs.Off) CheckMenuRadioItem(m, IdModeWidget, IdModeTray, wantWidget ? IdModeWidget : IdModeTray, 0);
            AppendMenuW(m, STRING | (Background ? CHECKED : 0) | (wantWidget ? 0 : GRAYED), (UIntPtr)IdBackground, "Background behind taskbar text (for transparent taskbars)");
            AppendMenuW(m, SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(m, STRING, (UIntPtr)IdRefresh, "Refresh now");
            AppendMenuW(m, STRING, (UIntPtr)IdWindow, "Batteries window\tAlt+Shift+B");
            Point pt = Cursor.Position;
            SetForegroundWindow(invoker.Handle);
            uint cmd = (uint)TrackPopupMenu(m, 0x0100 | 0x0080 | 0x0002, pt.X, pt.Y, 0, invoker.Handle, IntPtr.Zero);   // RETURNCMD | NONOTIFY | RIGHTBUTTON
            PostMessage(invoker.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(m);
            switch (cmd)
            {
                case IdRefresh: BatteryMonitor.RefreshNow(); break;
                case IdHideLastKnown: BatteryPrefs.Set(BatteryPrefs.HideLastKnown, !HideLastKnown); LoadPrefs(); break;
                case IdBackground: BatteryPrefs.Set(BatteryPrefs.Background, !Background); LoadPrefs(); break;
                case IdModeWidget: BatteryPrefs.Set(BatteryPrefs.Display, BatteryPrefs.Taskbar); LoadPrefs(); break;
                case IdModeTray: BatteryPrefs.Set(BatteryPrefs.Display, BatteryPrefs.TrayIcons); LoadPrefs(); break;
                case IdWindow: openWindow(); break;
                default:
                    if (cmd >= IdDeviceFirst && cmd < IdDeviceFirst + keys.Count) ToggleHidden(keys[(int)(cmd - IdDeviceFirst)]);
                    break;
            }
        }

        public void ToggleHidden(string key)
        {
            if (!hidden.Remove(key)) hidden.Add(key);
            BatteryPrefs.SetHidden(hidden);
            ApplyAll();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            reconcile.Dispose();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            BatteryMonitor.Results -= OnResults;
            if (watcher != null) watcher.Dispose();
            if (widget != null) widget.Dispose();
            if (trayIcons != null) trayIcons.Dispose();
            BatteryMonitor.Stop();
        }
    }

    // A hidden top-level window for HID arrival / removal (refresh 2.5 s later) and resume from sleep (5 s later)
    class DeviceWatcher : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterDeviceNotification(IntPtr h, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);
        [DllImport("user32.dll")] static extern bool UnregisterDeviceNotification(IntPtr h);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEV_BROADCAST_DEVICEINTERFACE { public int dbcc_size, dbcc_devicetype, dbcc_reserved; public Guid dbcc_classguid; public char dbcc_name; }

        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly IntPtr notify;

        public DeviceWatcher(Action refresh)
        {
            CreateHandle(new CreateParams { Caption = "komorebi-tray batteries watch" });
            var f = new DEV_BROADCAST_DEVICEINTERFACE { dbcc_devicetype = 5, dbcc_classguid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030") };
            f.dbcc_size = Marshal.SizeOf(f);
            notify = RegisterDeviceNotification(Handle, ref f, 0);
            debounce.Tick += (s, e) => { debounce.Stop(); refresh(); };
        }

        void RefreshSoon(int ms) { debounce.Stop(); debounce.Interval = ms; debounce.Start(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219 && ((long)m.WParam == 0x8000 || (long)m.WParam == 0x8004)) { RefreshSoon(2500); m.Result = (IntPtr)1; return; }   // DBT_DEVICEARRIVAL / REMOVECOMPLETE
            if (m.Msg == 0x0218 && (long)m.WParam == 0x12) { RefreshSoon(5000); m.Result = (IntPtr)1; return; }   // PBT_APMRESUMEAUTOMATIC
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            debounce.Dispose();
            if (notify != IntPtr.Zero) UnregisterDeviceNotification(notify);
            DestroyHandle();
        }
    }

    // ---- taskbar readout (widget.cpp)
    // A small layered child window of the taskbar (Shell_TrayWnd), just left of the notification area, showing
    // "[icon] 30%  [icon] 93%". It runs on its own thread with its own message loop: windows of one taskbar share input
    // with Explorer, so the thread that owns it must never be busy.

    class TaskbarWidget : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref TOOLINFO l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr c);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOOLINFO { public int cbSize; public uint uFlags; public IntPtr hwnd; public IntPtr uId; public RECT rect; public IntPtr hinst; public IntPtr lpszText; public IntPtr lParam; public IntPtr lpReserved; }

        public class Item
        {
            public DeviceKind Kind = DeviceKind.Other;
            public int Percent = -1;
            public string Text = "";   // shown instead of the percentage when set (coarse levels like "Low")
            public bool Charging, Dim, Low, Bluetooth;
        }

        const int WM_UPDATE = 0x8001, WM_QUIT_WIDGET = 0x8002;
        const string BatteryUnknown = "";   // shown when there is nothing to display

        readonly object gate = new object();
        readonly Action clicked;
        readonly Thread thread;
        readonly int offsetPx;
        List<Item> items = new List<Item>();
        string tipText = "";
        bool light, background, hover;
        volatile bool quitting, alive;
        StripWindow wnd;
        Owner owner;
        IntPtr taskbar, rebar, tip;
        RECT lastTray, lastBar, lastWidgets;
        int lastW = -1, lastDpi, lastLeft = -1;
        SIZE size;
        string tipShown = "";

        // Windows 11 puts the Widgets (weather) button next to the tray when the taskbar is left-aligned; it is XAML,
        // so only UI Automation knows where it is. The lookup runs on its own thread (UI Automation calls go to Explorer,
        // which waits for the thread that owns a child of its taskbar), and the readout thread only reads the last rectangle.
        static readonly object wbLock = new object();
        static RECT wbRect;
        static IntPtr wbRectBar, wbTaskbar;
        static AutoResetEvent wbWake;

        public TaskbarWidget(Action clicked, bool light, bool background, int offsetPx)
        {
            this.clicked = clicked; this.light = light; this.background = background; this.offsetPx = offsetPx;
            thread = new Thread(Run) { IsBackground = true, Name = "battery taskbar" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool Alive { get { return alive; } }

        public void SetItems(List<Item> list, string tooltip) { lock (gate) { items = list; tipText = tooltip; } Post(WM_UPDATE); }
        public void SetLight(bool l) { lock (gate) light = l; Post(WM_UPDATE); }
        public void SetBackground(bool on) { lock (gate) background = on; Post(WM_UPDATE); }

        void Post(int msg) { StripWindow w = wnd; if (w != null && w.Handle != IntPtr.Zero) PostMessage(w.Handle, msg, IntPtr.Zero, IntPtr.Zero); }

        int Dpi() { uint d = 0; try { d = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0; } catch (EntryPointNotFoundException) { } return d != 0 ? (int)d : 96; }
        int Px(int v) { return (int)Math.Round(v * Dpi() / 96.0, MidpointRounding.AwayFromZero); }

        void Run()
        {
            // Explorer's taskbar is per-monitor DPI aware: so is this thread, so the readout is drawn at the monitor's own scale
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            owner = new Owner();
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => { if (quitting) Application.ExitThread(); else Tick(); };
            timer.Start();
            Tick();
            try { Application.Run(); }
            catch (Exception e) { Log.Write("battery taskbar: " + e.Message); }
            timer.Dispose();
            Destroy();
            owner.DestroyHandle();
            alive = false;
        }

        // Once a second: attached to a living taskbar (Explorer restarts make a new one), and still in place
        void Tick()
        {
            try
            {
                if (wnd != null && wnd.Handle != IntPtr.Zero && IsWindow(wnd.Handle) && taskbar != IntPtr.Zero && IsWindow(taskbar)) { Reposition(); return; }
                Destroy();
                alive = Create();
            }
            catch (Exception e) { Log.Write("battery taskbar: " + e.Message); alive = false; }
        }

        bool Create()
        {
            taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null) == IntPtr.Zero) { taskbar = IntPtr.Zero; return false; }
            RECT tb;
            GetWindowRect(taskbar, out tb);
            if (tb.B - tb.T > tb.R - tb.L) { taskbar = IntPtr.Zero; return false; }   // vertical taskbar: tray icons instead
            // Windows 11's taskbar is a XAML island; on Windows 10 the task buttons live in ReBarWindow32 and are narrowed
            rebar = FindWindowEx(taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero
                ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            wnd = new StripWindow(this);
            wnd.Create(taskbar);
            if (wnd.Handle == IntPtr.Zero) { wnd = null; taskbar = IntPtr.Zero; return false; }
            tip = CreateWindowEx(0x8, "tooltips_class32", null, unchecked((int)0x80000000) | 0x1 | 0x2, 0, 0, 0, 0, owner.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);   // TOPMOST; POPUP | ALWAYSTIP | NOPREFIX
            if (tip != IntPtr.Zero)
            {
                ApplyTipTheme();
                var ti = new TOOLINFO { uFlags = 0x1 | 0x10, hwnd = owner.Handle, uId = wnd.Handle, lpszText = Marshal.StringToHGlobalUni("Batteries") };   // IDISHWND | SUBCLASS
                ti.cbSize = Marshal.SizeOf(ti);
                SendMessage(tip, 0x432, IntPtr.Zero, ref ti);   // TTM_ADDTOOLW
                Marshal.FreeHGlobal(ti.lpszText);
                SendMessage(tip, 0x418, IntPtr.Zero, (IntPtr)600);   // TTM_SETMAXTIPWIDTH
                tipShown = "";
            }
            WatchWidgetsButton(taskbar);
            lastDpi = Dpi();
            Paint();
            lastW = -1;
            Reposition();
            ShowWindow(wnd.Handle, 4);   // SW_SHOWNOACTIVATE
            return true;
        }

        void Destroy()
        {
            if (rebar != IntPtr.Zero && IsWindow(rebar) && taskbar != IntPtr.Zero && IsWindow(taskbar))
            {
                RECT r;   // let Explorer lay the task buttons out again over the full width
                GetClientRect(taskbar, out r);
                PostMessage(taskbar, 0x0005, IntPtr.Zero, (IntPtr)((r.B << 16) | (r.R & 0xFFFF)));   // WM_SIZE
            }
            rebar = IntPtr.Zero;
            if (wbWake != null) WatchWidgetsButton(IntPtr.Zero);
            if (tip != IntPtr.Zero) { DestroyWindow(tip); tip = IntPtr.Zero; }
            if (wnd != null && wnd.Handle != IntPtr.Zero) wnd.DestroyHandle();
            wnd = null;
            taskbar = IntPtr.Zero;
            hover = false;
        }

        void ApplyTipTheme()
        {
            if (tip == IntPtr.Zero) return;
            try { SetWindowTheme(tip, light ? "Explorer" : "DarkMode_Explorer", null); SendMessage(tip, 0x031A, IntPtr.Zero, IntPtr.Zero); } catch { }   // WM_THEMECHANGED
        }

        internal void OnMessage(int msg)
        {
            if (msg == WM_UPDATE)
            {
                string t;
                lock (gate) t = tipText;
                if (tip != IntPtr.Zero && t != tipShown && wnd != null)
                {
                    tipShown = t;
                    var ti = new TOOLINFO { hwnd = owner.Handle, uId = wnd.Handle, lpszText = Marshal.StringToHGlobalUni(t) };
                    ti.cbSize = Marshal.SizeOf(ti);
                    SendMessage(tip, 0x439, IntPtr.Zero, ref ti);   // TTM_UPDATETIPTEXTW
                    Marshal.FreeHGlobal(ti.lpszText);
                }
                ApplyTipTheme();
                Paint();
                Reposition();
            }
            else if (msg == WM_QUIT_WIDGET) Application.ExitThread();
        }

        internal void OnMouse(int msg)
        {
            if (msg == 0x0200 && !hover)   // WM_MOUSEMOVE
            {
                hover = true;
                var t = new TRACKMOUSEEVENT { dwFlags = 0x2, hwndTrack = wnd.Handle };   // TME_LEAVE
                t.cbSize = Marshal.SizeOf(t);
                TrackMouseEvent(ref t);
                Paint();
                Reposition();
            }
            else if (msg == 0x02A3) { hover = false; Paint(); Reposition(); }   // WM_MOUSELEAVE
            else if (msg == 0x0202 || msg == 0x0205) { if (clicked != null) clicked(); }   // left / right button up
        }

        class Piece { public Gdi.Mask M; public Color C; public int Alpha, GapAfter; }

        List<Piece> Layout(out int width)
        {
            List<Item> list;
            bool l;
            lock (gate) { list = items; l = light; }
            Color fg = l ? Color.FromArgb(0, 0, 0) : Color.FromArgb(255, 255, 255);
            Color red = l ? Color.FromArgb(196, 30, 40) : Color.FromArgb(255, 90, 90);
            Color charge = l ? Color.FromArgb(0, 120, 212) : Color.FromArgb(96, 205, 255);
            IntPtr icons = Gdi.MakeFont(Px(16), 400, "Segoe Fluent Icons", "Segoe MDL2 Assets");
            IntPtr bolt = Gdi.MakeFont(Px(11), 400, "Segoe Fluent Icons", "Segoe MDL2 Assets");
            IntPtr text = Gdi.MakeFont(Px(13), 400, "Segoe UI Variable Text", "Segoe UI");
            var output = new List<Piece>();
            if (list.Count == 0) output.Add(new Piece { M = Gdi.RenderMask(BatteryUnknown, icons), C = fg, Alpha = 110 });
            for (int i = 0; i < list.Count; i++)
            {
                Item it = list[i];
                int a = it.Dim ? 120 : 255;
                Color tc = it.Low ? red : fg;
                output.Add(new Piece { M = Gdi.RenderMask(Glyphs.For(it.Kind, it.Bluetooth), icons), C = fg, Alpha = a, GapAfter = Px(5) });
                string t = it.Text.Length > 0 ? it.Text : it.Percent >= 0 ? it.Percent + "%" : "?";
                output.Add(new Piece { M = Gdi.RenderMask(t, text), C = tc, Alpha = a, GapAfter = it.Charging ? Px(2) : 0 });
                if (it.Charging) output.Add(new Piece { M = Gdi.RenderMask(Glyphs.Bolt, bolt), C = charge, Alpha = a });
                output[output.Count - 1].GapAfter = i + 1 < list.Count ? Px(14) : 0;
            }
            Gdi.DeleteObject(icons);
            Gdi.DeleteObject(bolt);
            Gdi.DeleteObject(text);
            width = Px(8) * 2;
            foreach (Piece p in output) width += p.M.W + p.GapAfter;
            return output;
        }

        void Paint()
        {
            if (wnd == null || wnd.Handle == IntPtr.Zero) return;
            RECT bar;
            GetClientRect(taskbar, out bar);
            int H = bar.B - bar.T, W;
            List<Piece> pieces = Layout(out W);
            if (W <= 0 || H <= 0) return;
            var cv = new Gdi.Canvas(W, H);
            bool l, bg;
            lock (gate) { l = light; bg = background; }
            // Nearly transparent background so the whole strip receives mouse input, plus a rounded hover highlight
            for (int i = 0; i < cv.Px.Length; i++) cv.Px[i] = 0x01000000;
            Action<int, int, Color, int> roundRect = (inset, r, c, a) =>
            {
                for (int y = inset; y < H - inset; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int dx = x < r ? r - x : (x >= W - r ? x - (W - r - 1) : 0);
                        int dy = y < inset + r ? inset + r - y : (y >= H - inset - r ? y - (H - inset - r - 1) : 0);
                        if (dx * dx + dy * dy <= r * r) cv.Blend(x, y, c, a);
                    }
            };
            // Optional plate in the taskbar's own colours, for taskbars made transparent by a theme
            if (bg) roundRect(Px(4), Px(6), l ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32), 240);
            if (hover) roundRect(Px(4), Px(4), l ? Color.FromArgb(0, 0, 0) : Color.FromArgb(255, 255, 255), l ? 20 : 26);
            int x0 = Px(8);
            foreach (Piece p in pieces)
            {
                cv.Draw(p.M, x0, (H - p.M.H) / 2, p.C, p.Alpha);
                x0 += p.M.W + p.GapAfter;
            }
            IntPtr screen = GetDC(IntPtr.Zero), mem = Gdi.CreateCompatibleDC(screen);
            var bi = new Gdi.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Gdi.BITMAPINFOHEADER)), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr bmp = Gdi.CreateDIBSection(mem, ref bi, 0, out bits, IntPtr.Zero, 0);
            var ints = new int[cv.Px.Length];
            Buffer.BlockCopy(cv.Px, 0, ints, 0, ints.Length * 4);
            Marshal.Copy(ints, 0, bits, ints.Length);
            IntPtr ob = Gdi.SelectObject(mem, bmp);
            size = new SIZE(W, H);
            var src = new POINT(0, 0);
            var bf = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(wnd.Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref bf, 2);
            Gdi.SelectObject(mem, ob);
            Gdi.DeleteObject(bmp);
            Gdi.DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            lastW = -1;   // force a re-position with the new size
        }

        void Reposition()
        {
            if (wnd == null || wnd.Handle == IntPtr.Zero || taskbar == IntPtr.Zero) return;
            if (Dpi() != lastDpi) { lastDpi = Dpi(); Paint(); }
            IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero) return;
            RECT tr, bar;
            GetWindowRect(tray, out tr);
            GetWindowRect(taskbar, out bar);
            // Right-to-left Windows mirrors the taskbar: client x runs from the screen's right edge
            bool rtl = (GetWindowLong(taskbar, -20) & 0x00400000) != 0;   // GWL_EXSTYLE, WS_EX_LAYOUTRTL
            int edge = rtl ? tr.R : tr.L;
            // Windows 10: News and Interests / Weather sits immediately left of the tray
            IntPtr feeds = FindWindowEx(taskbar, IntPtr.Zero, "ShellFeedsTaskbarViewWindow", null);
            if (!rtl && feeds != IntPtr.Zero && IsWindowVisible(feeds))
            {
                RECT fr;
                GetWindowRect(feeds, out fr);
                if (fr.L < edge && fr.R > fr.L) edge = fr.L;
            }
            // Step past the Widgets button when it sits where we would go
            RECT wb = WidgetsButtonRect();
            if (wb.R > wb.L && wb.T < bar.B && wb.B > bar.T &&
                (rtl ? wb.L < edge + size.W + Px(2) && wb.R > edge : wb.R > edge - size.W - Px(2) && wb.L < edge))
                edge = rtl ? wb.R : wb.L;
            var p = new POINT(edge, bar.T);
            ScreenToClient(taskbar, ref p);
            int left = p.X - size.W - Px(2) - Px(offsetPx);
            // Windows 10: Explorer re-expands the task buttons on its own layout passes; narrow them again
            if (rebar != IntPtr.Zero && IsWindow(rebar))
            {
                RECT rr;
                GetWindowRect(rebar, out rr);
                var tl = new POINT(rtl ? rr.R : rr.L, rr.T);
                ScreenToClient(taskbar, ref tl);
                if (tl.X + (rr.R - rr.L) > left && left > tl.X) MoveWindow(rebar, tl.X, tl.Y, left - tl.X, rr.B - rr.T, true);
            }
            // Keep us above the taskbar's own content (our sibling windows)
            bool covered = GetWindow(wnd.Handle, 3) != IntPtr.Zero;   // GW_HWNDPREV
            if (!covered && Same(tr, lastTray) && Same(bar, lastBar) && Same(wb, lastWidgets) && lastW == size.W && lastLeft == left) return;
            lastTray = tr; lastWidgets = wb; lastBar = bar; lastW = size.W; lastLeft = left;
            SetWindowPos(wnd.Handle, IntPtr.Zero, left, 0, size.W, size.H, 0x0010 | 0x0040);   // HWND_TOP; NOACTIVATE | SHOWWINDOW
        }

        static bool Same(RECT a, RECT b) { return a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B; }

        static void WatchWidgetsButton(IntPtr bar)
        {
            lock (wbLock) wbTaskbar = bar;
            if (wbWake == null)
            {
                wbWake = new AutoResetEvent(false);
                new Thread(WidgetsButtonThread) { IsBackground = true, Name = "battery widgets button" }.Start();
            }
            wbWake.Set();
        }

        static RECT WidgetsButtonRect()
        {
            lock (wbLock) return wbRectBar != IntPtr.Zero && wbRectBar == wbTaskbar ? wbRect : new RECT();
        }

        // The button only sits by the tray on a left-aligned Windows 11 taskbar with Widgets on; skipping the lookup
        // otherwise keeps UI Automation (a few MB) out of the process
        static bool WidgetsButtonPossible()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    if (k == null) return true;
                    object al = k.GetValue("TaskbarAl"), da = k.GetValue("TaskbarDa");
                    if (da is int && (int)da == 0) return false;
                    return al is int && (int)al == 0;
                }
            }
            catch { return true; }
        }

        static void WidgetsButtonThread()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            System.Windows.Automation.AutomationElement btn = null;
            IntPtr btnBar = IntPtr.Zero;
            for (;;)
            {
                wbWake.WaitOne(btnBar != IntPtr.Zero ? 5000 : Timeout.Infinite);   // no readout (tray-icon mode): sleep until there is one
                IntPtr bar;
                lock (wbLock) bar = wbTaskbar;
                if (btn != null && bar != btnBar) btn = null;
                btnBar = bar;
                var r = new RECT();
                if (bar != IntPtr.Zero && IsWindow(bar) && WidgetsButtonPossible())
                {
                    try
                    {
                        if (btn == null)
                        {
                            var root = System.Windows.Automation.AutomationElement.FromHandle(bar);
                            btn = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "WidgetsButton"));
                        }
                        if (btn != null)
                        {
                            var b = btn.Current.BoundingRectangle;
                            if (!b.IsEmpty) r = new RECT { L = (int)b.Left, T = (int)b.Top, R = (int)b.Right, B = (int)b.Bottom };
                        }
                    }
                    catch { btn = null; r = new RECT(); }   // stale (Widgets turned off, Explorer restarted): look it up again next time
                }
                lock (wbLock)
                    if (bar != IntPtr.Zero && wbTaskbar == bar) { wbRect = r; wbRectBar = bar; }
            }
        }

        public void Dispose()
        {
            quitting = true;
            Post(WM_QUIT_WIDGET);
        }

        // Hidden top-level window that owns the tooltip (the original's notify window)
        class Owner : NativeWindow
        {
            public Owner() { CreateHandle(new CreateParams { Caption = "komorebi-tray batteries tip" }); }
        }

        class StripWindow : NativeWindow
        {
            readonly TaskbarWidget owner;
            static readonly IntPtr Arrow = LoadCursor(IntPtr.Zero, (IntPtr)32512);
            public StripWindow(TaskbarWidget owner) { this.owner = owner; }

            public void Create(IntPtr bar)
            {
                CreateHandle(new CreateParams
                {
                    Caption = "komorebi-tray batteries",
                    Parent = bar,
                    Style = 0x40000000 | 0x04000000,          // WS_CHILD | WS_CLIPSIBLINGS
                    ExStyle = 0x00080000 | 0x08000000,        // WS_EX_LAYERED | WS_EX_NOACTIVATE
                    Width = 1, Height = 1,
                });
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_UPDATE: case WM_QUIT_WIDGET: owner.OnMessage(m.Msg); return;
                    case 0x0200: case 0x02A3: case 0x0202: case 0x0205: owner.OnMouse(m.Msg); return;
                    case 0x0021: m.Result = (IntPtr)3; return;   // WM_MOUSEACTIVATE: MA_NOACTIVATE
                    case 0x0020: SetCursor(Arrow); m.Result = (IntPtr)1; return;   // WM_SETCURSOR
                }
                base.WndProc(ref m);
            }
        }
    }

    // ---- notification area icons (main.cpp RenderIcon / ApplyEntry)
    // One icon per device: the percentage as large digits with a small level bar underneath (green, orange from 30 %,
    // red at 15 %, blue while charging); dimmed while asleep. A "-" placeholder while there is nothing to show.

    class TrayIconSet : IDisposable
    {
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);

        readonly MethodInvoker click;
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, string> sigs = new Dictionary<string, string>();
        NotifyIcon placeholder;
        IntPtr placeholderIcon;
        List<BatteryTray.Shown> last = new List<BatteryTray.Shown>();
        bool light;
        int low = 15;

        public TrayIconSet(MethodInvoker click) { this.click = click; }

        NotifyIcon NewIcon()
        {
            var ni = new NotifyIcon();
            ni.MouseUp += (s, e) => click();   // either button opens the menu, like the original
            return ni;
        }

        public void Redraw(bool l, int lowMark) { sigs.Clear(); if (placeholder != null) { placeholder.Dispose(); placeholder = null; Gdi.DestroyIcon(placeholderIcon); placeholderIcon = IntPtr.Zero; } Show(last, l, lowMark); }

        public void Show(List<BatteryTray.Shown> list, bool l, int lowMark)
        {
            last = list; light = l; low = lowMark;
            var keep = new HashSet<string>();
            foreach (BatteryTray.Shown d in list)
            {
                keep.Add(d.S.Key);
                NotifyIcon ni;
                if (!icons.TryGetValue(d.S.Key, out ni)) icons[d.S.Key] = ni = NewIcon();
                string sig = d.Percent + (d.S.Charging ? "c" : "") + (d.Dim ? "d" : "") + (light ? "l" : "k") + low;
                string was;
                if (!sigs.TryGetValue(d.S.Key, out was) || was != sig)
                {
                    IntPtr h = IconFor(d.Percent, d.S.Charging, d.Dim);
                    if (h != IntPtr.Zero)
                    {
                        ni.Icon = Icon.FromHandle(h);
                        IntPtr old;
                        if (handles.TryGetValue(d.S.Key, out old)) Gdi.DestroyIcon(old);
                        handles[d.S.Key] = h;
                        sigs[d.S.Key] = sig;
                    }
                }
                string tip = d.S.Name + "\n" + d.Status;
                ni.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
                ni.Visible = true;
            }
            var gone = new List<string>();
            foreach (string k in icons.Keys) if (!keep.Contains(k)) gone.Add(k);
            foreach (string k in gone) Remove(k);
            if (icons.Count == 0 && placeholder == null)
            {
                placeholderIcon = RenderIcon("-", -1, Fg(), Fg(), true);
                placeholder = NewIcon();
                placeholder.Icon = Icon.FromHandle(placeholderIcon);
                placeholder.Text = "Batteries\nNo devices with battery info found";
                placeholder.Visible = true;
            }
            else if (icons.Count > 0 && placeholder != null)
            {
                placeholder.Dispose();
                placeholder = null;
                Gdi.DestroyIcon(placeholderIcon);
                placeholderIcon = IntPtr.Zero;
            }
        }

        void Remove(string key)
        {
            NotifyIcon ni;
            if (icons.TryGetValue(key, out ni)) { ni.Visible = false; ni.Dispose(); icons.Remove(key); }
            IntPtr h;
            if (handles.TryGetValue(key, out h)) { Gdi.DestroyIcon(h); handles.Remove(key); }
            sigs.Remove(key);
        }

        Color Fg() { return light ? Color.FromArgb(0, 0, 0) : Color.FromArgb(255, 255, 255); }

        IntPtr IconFor(int pct, bool charging, bool dim)
        {
            Color fg = Fg();
            Color bar = charging ? Color.FromArgb(0, 170, 255) : pct <= low ? Color.FromArgb(232, 32, 48) : pct <= 30 ? Color.FromArgb(255, 150, 0) : Color.FromArgb(30, 190, 90);
            Color text = !charging && pct >= 0 && pct <= low ? Color.FromArgb(240, 50, 60) : fg;
            return RenderIcon(pct < 0 ? "?" : pct.ToString(), pct, text, bar, dim);
        }

        // Large digits with a small level bar underneath; GDI text rendered white-on-black, its brightness the alpha
        IntPtr RenderIcon(string text, int barPct, Color textColor, Color barColor, bool dim)
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            uint dpi = 0;
            try { dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0; if (dpi == 0) dpi = GetDpiForSystem(); } catch { dpi = 96; }
            int S;
            try { S = GetSystemMetricsForDpi(49, dpi); } catch { S = SystemInformation.SmallIconSize.Width; }   // SM_CXSMICON
            int barH = Math.Max(2, S / 8), textH = S - barH - 1;
            Gdi.Mask mask = null;
            for (int fh = textH * 2; fh >= 6; fh--)
            {
                IntPtr f = Gdi.CreateFontW(-fh, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");   // FW_BOLD, ANTIALIASED_QUALITY
                Gdi.Mask m = Gdi.RenderMask(text, f, text.Length >= 3 ? -1 : 0);
                Gdi.DeleteObject(f);
                mask = m;
                if (m.W == 0) break;
                if (m.W <= S && m.H <= textH) break;
            }
            var px = new int[S * S];
            int alphaScale = dim ? 140 : 255;
            Action<int, int, Color, int> put = (x, y, c, a) =>
            {
                if (x < 0 || y < 0 || x >= S || y >= S || a <= 0) return;
                a = a * alphaScale / 255;
                px[y * S + x] = (a << 24) | (c.R << 16) | (c.G << 8) | c.B;   // straight alpha
            };
            if (mask != null && mask.W > 0)
            {
                int ox = (S - mask.W) / 2, oy = (textH - mask.H) / 2;
                for (int y = 0; y < mask.H; y++)
                    for (int x = 0; x < mask.W; x++) put(ox + x, oy + y, textColor, mask.A[y * mask.W + x]);
            }
            // Level bar: faint track + filled part
            int fill = barPct < 0 ? 0 : Math.Max(barPct > 0 ? 1 : 0, (barPct * S + 50) / 100);
            for (int y = S - barH; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    if (x < fill) put(x, y, barColor, 255);
                    else put(x, y, light ? Color.FromArgb(0, 0, 0) : Color.FromArgb(255, 255, 255), 70);
                }
            var bi = new Gdi.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Gdi.BITMAPINFOHEADER)), biWidth = S, biHeight = -S, biPlanes = 1, biBitCount = 32 };
            IntPtr dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
            IntPtr bits;
            IntPtr color = Gdi.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            Marshal.Copy(px, 0, bits, px.Length);
            IntPtr maskBmp = Gdi.CreateBitmap(S, S, 1, 1, IntPtr.Zero);
            IntPtr mdc = Gdi.CreateCompatibleDC(IntPtr.Zero);
            IntPtr o = Gdi.SelectObject(mdc, maskBmp);
            Gdi.PatBlt(mdc, 0, 0, S, S, 0x00000042);   // BLACKNESS
            Gdi.SelectObject(mdc, o);
            Gdi.DeleteDC(mdc);
            Gdi.DeleteDC(dc);
            var ii = new Gdi.ICONINFO { fIcon = true, hbmMask = maskBmp, hbmColor = color };
            IntPtr icon = Gdi.CreateIconIndirect(ref ii);
            Gdi.DeleteObject(maskBmp);
            Gdi.DeleteObject(color);
            return icon;
        }

        public void Dispose()
        {
            var keys = new List<string>(icons.Keys);
            foreach (string k in keys) Remove(k);
            if (placeholder != null) { placeholder.Dispose(); placeholder = null; }
            if (placeholderIcon != IntPtr.Zero) { Gdi.DestroyIcon(placeholderIcon); placeholderIcon = IntPtr.Zero; }
        }
    }

    // ---- Batteries window (Alt+Shift+B)
    // Same launcher look as Alt+K / Alt+C / Alt+Space: every device as a card (click one to hide or show it on the
    // taskbar), a search for devices that were just plugged in or paired, a GitHub report helper when a search finds
    // nothing new, and the battery settings (also in Alt+C → Batteries).

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
            hint.Text = tray.Searching ? "searching for devices…" : "r search    click a device to hide it    ↑↓ setting    ←→ change    esc close";
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
            else if (e.KeyCode == Keys.Up) view.MoveSel(-1);
            else if (e.KeyCode == Keys.Down) view.MoveSel(1);
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
        static readonly Font Huge = new Font(Theme.Mono.FontFamily, 21f, FontStyle.Bold);
        static readonly Font IconBig = new Font(Glyphs.Family, 15f);
        static readonly Font IconSmall = new Font(Glyphs.Family, 10f);

        readonly BatteryWindow form;
        readonly BatteryTray tray;
        int scroll, selected, dragRow = -1;
        Rectangle searchRect, linkRect, copyRect;
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        readonly List<Rectangle> cardRects = new List<Rectangle>();
        string hot;
        int hotCard = -1;

        static readonly string[] Labels = { "Show levels", "Background behind taskbar text", "Hide last known / asleep devices", "Low battery warning", "Low at" };
        static readonly string[] Descriptions =
        {
            "Taskbar, numbers in the notification area, or hidden", "For taskbars made transparent by a theme",
            "Only devices with a current reading", "A notification when a device runs low", "Level that counts as low (shown in red)",
        };

        public BatteryView(BatteryWindow form, BatteryTray tray)
        {
            this.form = form; this.tray = tray;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        List<BatteryTray.Shown> Devices { get { return tray.Current; } }
        bool ShowReport { get { return !tray.Searching && tray.LastSearchNew == 0; } }
        int Cols { get { return Devices.Count <= 4 ? 2 : 3; } }
        int CardW { get { return (W - Side * 2 - Gap * (Cols - 1)) / Cols; } }

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

        public int ContentHeight { get { return HeaderH + 18 + CardsHeight + (ShowReport ? Gap + ReportH : 0) + 30 + 34 + Labels.Length * RowH + 14; } }
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

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
            cardRects.Clear();
            if (Devices.Count == 0)
            {
                string msg = !tray.Polled ? "Looking for wireless devices…" : "No wireless devices found. Plug in a dongle or connect over Bluetooth, then search.";
                TextRenderer.DrawText(g, msg, Theme.Mono, new Rectangle(Side, top, W - Side * 2, 60), Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            for (int i = 0; i < Devices.Count; i++)
            {
                int col = i % Cols, row = i / Cols;
                var r = new Rectangle(Side + col * (CardW + Gap), top + row * (CardH + Gap), CardW, CardH);
                cardRects.Add(r);
                if (r.Bottom < HeaderH || r.Y > Height) continue;
                PaintCard(g, Devices[i], r, i == hotCard);
            }
        }

        void PaintCard(Graphics g, BatteryTray.Shown d, Rectangle r, bool hover)
        {
            DeviceStatus s = d.S;
            bool level = d.Percent >= 0 && s.Connected, low = level && d.Low, faded = d.Dim || d.Hidden || !s.Connected;
            using (var path = Theme.Round(r, 8))
            using (var fill = new SolidBrush(hover ? Theme.Hover : Theme.Surface))
            using (var pen = new Pen(low ? Theme.Danger : hover ? Theme.Accent : Theme.Border)) { g.FillPath(fill, path); g.DrawPath(pen, path); }
            Color iconColor = faded ? Theme.Dim : low ? Theme.Danger : Theme.Accent;
            TextRenderer.DrawText(g, Glyphs.For(s.Kind, s.Via == "Bluetooth"), IconBig, new Rectangle(r.X + 12, r.Y + 12, 30, 30), iconColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, s.Name, Theme.Mono, new Rectangle(r.X + 48, r.Y + 11, r.Width - 58 - (d.Hidden ? 22 : 0), 20), d.Hidden ? Theme.Dim : Theme.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (d.Hidden)
                TextRenderer.DrawText(g, Glyphs.Hidden, IconSmall, new Rectangle(r.Right - 30, r.Y + 11, 20, 20), Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, s.Via, Theme.MonoSmall, new Rectangle(r.X + 48, r.Y + 31, r.Width - 58, 18), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (!level)
            {
                TextRenderer.DrawText(g, d.Status + (d.Hidden ? " · hidden" : ""), Theme.MonoSmall, new Rectangle(r.X + 14, r.Y + 56, r.Width - 28, 46), Theme.Dim,
                    TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                return;
            }
            string pct = s.Percent >= 0 && s.LevelText.Length > 0 ? s.LevelText : d.Percent + "%";
            TextRenderer.DrawText(g, pct, Huge, new Point(r.X + 10, r.Y + 52), low ? Theme.Danger : faded ? Theme.Dim : Theme.Text, TextFormatFlags.NoPrefix);
            int pw = TextRenderer.MeasureText(g, pct, Huge, Size.Empty, TextFormatFlags.NoPrefix).Width;
            var states = new List<string>();
            if (s.Charging) states.Add(d.Percent >= 100 ? "full" : "charging");
            if (d.Dim) states.Add(s.Asleep ? "asleep" : "last known");
            if (s.Detail.Length > 0) states.Add(s.Detail);
            if (d.Hidden) states.Add("hidden");
            int sx = r.X + 10 + pw + 2;
            if (s.Charging && d.Percent < 100)
            {
                TextRenderer.DrawText(g, Glyphs.Bolt, IconSmall, new Rectangle(sx, r.Y + 62, 16, 20), Glyphs.LightTaskbar() ? Color.FromArgb(0, 120, 212) : Color.FromArgb(96, 205, 255),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                sx += 16;
            }
            TextRenderer.DrawText(g, string.Join(" · ", states.ToArray()), Theme.MonoSmall, new Rectangle(sx + 2, r.Y + 63, r.Right - sx - 12, 18), Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            var track = new RectangleF(r.X + 14, r.Bottom - 16, r.Width - 28, 5);
            using (var path = Theme.Round(track, 2.5f)) using (var bg = new SolidBrush(Theme.Bg)) g.FillPath(bg, path);
            float fw = Math.Max(5, track.Width * Math.Min(100, d.Percent) / 100f);
            using (var path = Theme.Round(new RectangleF(track.X, track.Y, fw, track.Height), 2.5f))
            using (var fb = new SolidBrush(low ? Theme.Danger : faded ? Theme.Border : Theme.Accent)) g.FillPath(fb, path);
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
            TextRenderer.DrawText(g, Glyphs.Link, IconSmall, new Rectangle(linkRect.X + ls.Width - 2, linkRect.Y, 20, ls.Height), lc, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "and paste these details:", Theme.Mono, new Point(linkRect.Right + 6, linkRect.Y), Theme.TextSoft, TextFormatFlags.NoPrefix);
            string cl = "Copy details";
            int cw = TextRenderer.MeasureText(cl, Theme.MonoSmall).Width + 40;
            copyRect = new Rectangle(r.Right - 16 - cw, r.Y + 12, cw, 28);
            using (var path = Theme.Round(copyRect, 6)) using (var b = new SolidBrush(hot == "copy" ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
            TextRenderer.DrawText(g, Glyphs.Copy, IconSmall, new Rectangle(copyRect.X + 8, copyRect.Y, 18, copyRect.Height), Theme.OnAccent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, cl, Theme.MonoSmall, new Rectangle(copyRect.X + 28, copyRect.Y, copyRect.Width - 30, copyRect.Height), Theme.OnAccent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var box = new Rectangle(r.X + 16, r.Y + 68, r.Width - 32, r.Height - 82);
            using (var path = Theme.Round(box, 6)) using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, path);
            string preview = "### Device\n- Brand and model:\n- Connected by: 2.4 GHz dongle / Bluetooth / cable\n- Battery the vendor app or the device itself shows:\n…";
            TextRenderer.DrawText(g, preview, Theme.MonoSmall, new Rectangle(box.X + 12, box.Y + 8, box.Width - 24, box.Height - 12), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        bool BoolOf(int row)
        {
            if (row == 1) return tray.Background;
            if (row == 2) return tray.HideLastKnown;
            return tray.Warn;
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
                bool on = i != 1 || tray.Display == BatteryPrefs.Taskbar;
                if (sel)
                {
                    using (var path = Theme.Round(row, 7)) using (var b = new SolidBrush(Theme.Selection)) g.FillPath(b, path);
                    using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, row.X, row.Y + 12, 3, row.Height - 24);
                }
                TextRenderer.DrawText(g, Labels[i], Theme.Mono, new Point(Side + 6, row.Y + 9), !on ? Theme.Dim : sel ? Theme.Text : Theme.TextSoft, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Descriptions[i], Theme.MonoSmall, new Rectangle(Side + 6, row.Y + 31, ctrlX - Side - 16, 18), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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
                else if (i <= 3)
                {
                    bool v = BoolOf(i);
                    Color acc = on ? Theme.Accent : Theme.Border;
                    var pill = new RectangleF(area.Right - 46, cy - 11, 44, 22);
                    using (var path = Theme.Round(pill, 11))
                    using (var b = new SolidBrush(v ? acc : Theme.Surface))
                    using (var p = new Pen(v ? acc : Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                    using (var knob = new SolidBrush(v ? Color.White : Theme.Dim)) g.FillEllipse(knob, v ? pill.Right - 19 : pill.X + 3, pill.Y + 3, 16, 16);
                    TextRenderer.DrawText(g, v ? "On" : "Off", Theme.MonoSmall, new Rectangle(area.Right - 100, cy - 10, 48, 20), v && on ? Theme.Text : Theme.Dim,
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

        public void MoveSel(int d) { selected = Math.Max(0, Math.Min(Labels.Length - 1, selected + d)); EnsureVisible(); Invalidate(); }

        void EnsureVisible()
        {
            if (rowRects.Count <= selected) return;
            Rectangle r = rowRects[selected];
            if (r.Bottom > Height) scroll = Math.Min(MaxScroll, scroll + r.Bottom - Height + 8);
            else if (r.Y < HeaderH) scroll = Math.Max(0, scroll - (HeaderH - r.Y) - 8);
        }

        public void Adjust(int dir)
        {
            form.Status = null;
            if (selected == 0)
            {
                int i = Array.IndexOf(BatteryPrefs.DisplayOptions, tray.Display), n = BatteryPrefs.DisplayOptions.Length;
                SetPref(BatteryPrefs.Display, BatteryPrefs.DisplayOptions[((i < 0 ? 0 : i) + (dir == 0 ? 1 : dir) + n) % n]);
            }
            else if (selected <= 3)
            {
                string key = selected == 1 ? BatteryPrefs.Background : selected == 2 ? BatteryPrefs.HideLastKnown : BatteryPrefs.Warn;
                SetPref(key, dir == 0 ? !BoolOf(selected) : dir > 0);
            }
            else SetLow(tray.Low + (dir == 0 ? 5 : dir * 5));
            form.UpdateHint();
        }

        void SetPref(string key, object v) { BatteryPrefs.Set(key, v); tray.LoadPrefs(); }

        void SetLow(int v)
        {
            v = Math.Max(5, Math.Min(50, (int)Math.Round(v / 5.0) * 5));
            if (v == tray.Low) return;
            SetPref(BatteryPrefs.Low, v);
        }

        protected override void OnMouseWheel(MouseEventArgs e) { scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * 48)); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (searchRect.Contains(e.Location)) { form.Status = null; tray.Search(); return; }
            if (e.Y < HeaderH) return;
            if (ShowReport && linkRect.Contains(e.Location)) { form.OpenIssue(); return; }
            if (ShowReport && copyRect.Contains(e.Location)) { form.CopyReport(); Invalidate(); return; }
            for (int i = 0; i < cardRects.Count && i < Devices.Count; i++)
                if (cardRects[i].Contains(e.Location)) { tray.ToggleHidden(Devices[i].S.Key); return; }   // like the original's device menu items
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
                        for (int k = 0; k < segs.Count; k++) if (segs[k].Contains(e.Location)) SetPref(BatteryPrefs.Display, BatteryPrefs.DisplayOptions[k]);
                    }
                    else if (i <= 3) Adjust(0);
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
            if (dragRow >= 0 && rowRects.Count > dragRow) SlideTo(new Rectangle(W - Side - 300, rowRects[dragRow].Y, 300, rowRects[dragRow].Height), e.X);
            string h = searchRect.Contains(e.Location) ? "search" : ShowReport && linkRect.Contains(e.Location) ? "link" : ShowReport && copyRect.Contains(e.Location) ? "copy" : null;
            int card = -1;
            if (e.Y >= HeaderH) for (int i = 0; i < cardRects.Count; i++) if (cardRects[i].Contains(e.Location)) card = i;
            bool row = false;
            foreach (var r in rowRects) if (r.Contains(e.Location) && e.Y >= HeaderH) row = true;
            Cursor = h != null || row || card >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hot || card != hotCard) { hot = h; hotCard = card; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (dragRow >= 0) { dragRow = -1; Capture = false; } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != null || hotCard >= 0) { hot = null; hotCard = -1; Invalidate(); } }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    }
}
