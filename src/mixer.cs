// komorebi-tray volume mixer: a dark, tactile controller flyout (default output + its apps).
// Talks to Windows Core Audio directly; nothing runs while the flyout is closed.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KomorebiTray
{
    // ------------------------------------------------------------------ Core Audio interop

    static class CoreAudio
    {
        public const int eRender = 0, eConsole = 0, eMultimedia = 1, eCommunications = 2;
        public const int DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] public class MMDeviceEnumeratorCom { }
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] public class PolicyConfigCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] public struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Undocumented but stable since Windows 7: the only way to change the default device
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

        public static Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
        public static Guid IID_IAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;
        static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        public static string Name(IMMDevice d)
        {
            IPropertyStore store;
            if (d.OpenPropertyStore(0, out store) != 0 || store == null) return "Audio device";
            var key = FriendlyName;
            PROPVARIANT v;
            try
            {
                if (store.GetValue(ref key, out v) != 0) return "Audio device";
                string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                return string.IsNullOrEmpty(s) ? "Audio device" : s;
            }
            finally { Marshal.ReleaseComObject(store); }
        }
    }

    // ------------------------------------------------------------------ model

    class MixerApp
    {
        public string Key, Name;
        public Bitmap Icon;
        public readonly List<CoreAudio.ISimpleAudioVolume> Volumes = new List<CoreAudio.ISimpleAudioVolume>();
        public float Volume, Shown;   // Shown eases toward Volume for smooth jumps
        public bool Muted;

        public void SetVolume(float v)
        {
            Volume = v;
            var ctx = Guid.Empty;
            foreach (var s in Volumes) try { s.SetMasterVolume(v, ref ctx); } catch { }
        }
        public void SetMute(bool m)
        {
            Muted = m;
            var ctx = Guid.Empty;
            foreach (var s in Volumes) try { s.SetMute(m, ref ctx); } catch { }
        }
    }

    class MixerDevice
    {
        public string Id, Name;
        public bool IsDefault;
        public CoreAudio.IAudioEndpointVolume Endpoint;
        public CoreAudio.IAudioSessionManager2 Sessions;
        public float Volume, Shown;
        public bool Muted;
        public List<MixerApp> Apps = new List<MixerApp>();

        public void SetVolume(float v) { Volume = v; var ctx = Guid.Empty; try { Endpoint.SetMasterVolumeLevelScalar(v, ref ctx); } catch { } }
        public void SetMute(bool m) { Muted = m; var ctx = Guid.Empty; try { Endpoint.SetMute(m, ref ctx); } catch { } }
    }

    static class AudioModel
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static readonly Dictionary<string, Tuple<string, Bitmap>> appInfo = new Dictionary<string, Tuple<string, Bitmap>>(StringComparer.OrdinalIgnoreCase);
        static readonly uint SelfPid = (uint)Process.GetCurrentProcess().Id;

        public static List<MixerDevice> Load(bool includeInactiveApps, int iconPx)
        {
            var list = new List<MixerDevice>();
            CoreAudio.IMMDeviceEnumerator en = null;
            try
            {
                en = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumeratorCom();
                string defaultId = null;
                CoreAudio.IMMDevice def;
                if (en.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out def) == 0 && def != null)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }

                CoreAudio.IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(CoreAudio.eRender, CoreAudio.DEVICE_STATE_ACTIVE, out col) != 0) return list;
                int n;
                col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    CoreAudio.IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    var md = new MixerDevice();
                    d.GetId(out md.Id);
                    md.Name = CoreAudio.Name(d);
                    md.IsDefault = md.Id == defaultId;
                    object o;
                    var iid = CoreAudio.IID_IAudioEndpointVolume;
                    if (d.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o) == 0) md.Endpoint = (CoreAudio.IAudioEndpointVolume)o;
                    iid = CoreAudio.IID_IAudioSessionManager2;
                    if (d.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o) == 0) md.Sessions = (CoreAudio.IAudioSessionManager2)o;
                    Marshal.ReleaseComObject(d);
                    if (md.Endpoint == null) continue;
                    ReadDevice(md);
                    md.Shown = md.Volume;
                    ReadApps(md, includeInactiveApps, iconPx);
                    foreach (var a in md.Apps) a.Shown = a.Volume;
                    list.Add(md);
                }
                Marshal.ReleaseComObject(col);
            }
            catch (Exception ex) { Log.Write("mixer: " + ex.Message); }
            finally { if (en != null) Marshal.ReleaseComObject(en); }
            // The default device first, the rest in Windows' order
            list.Sort((a, b) => a.IsDefault == b.IsDefault ? 0 : a.IsDefault ? -1 : 1);
            return list;
        }

        public static void ReadDevice(MixerDevice d)
        {
            float v; bool m;
            try { if (d.Endpoint.GetMasterVolumeLevelScalar(out v) == 0) d.Volume = v; if (d.Endpoint.GetMute(out m) == 0) d.Muted = m; } catch { }
        }

        // Sessions grouped by app (several processes of one browser are one row)
        public static void ReadApps(MixerDevice d, bool includeInactive, int iconPx)
        {
            var apps = new Dictionary<string, MixerApp>(StringComparer.OrdinalIgnoreCase);
            var order = new List<MixerApp>();
            if (d.Sessions == null) { d.Apps = order; return; }
            CoreAudio.IAudioSessionEnumerator en;
            try
            {
                if (d.Sessions.GetSessionEnumerator(out en) != 0 || en == null) { d.Apps = order; return; }
                int n;
                en.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    CoreAudio.IAudioSessionControl2 s;
                    if (en.GetSession(i, out s) != 0 || s == null) continue;
                    try
                    {
                        int state;
                        s.GetState(out state);
                        if (state == 2 || (!includeInactive && state != 1)) continue;   // 2 = expired, 1 = playing
                        if (s.IsSystemSoundsSession() == 0) continue;
                        uint pid;
                        s.GetProcessId(out pid);
                        if (pid == 0 || pid == SelfPid) continue;
                        string path = ImagePath(pid);
                        string key = path ?? ("pid:" + pid);
                        MixerApp app;
                        if (!apps.TryGetValue(key, out app))
                        {
                            app = new MixerApp { Key = key };
                            var info = Info(path, pid, iconPx);
                            app.Name = info.Item1;
                            app.Icon = info.Item2;
                            apps[key] = app;
                            order.Add(app);
                            var sv0 = (CoreAudio.ISimpleAudioVolume)s;
                            float v; bool m;
                            if (sv0.GetMasterVolume(out v) == 0) app.Volume = v;
                            if (sv0.GetMute(out m) == 0) app.Muted = m;
                        }
                        app.Volumes.Add((CoreAudio.ISimpleAudioVolume)s);
                    }
                    catch { }
                }
                Marshal.ReleaseComObject(en);
            }
            catch (Exception ex) { Log.Write("mixer sessions: " + ex.Message); }
            d.Apps = order;
        }

        static string ImagePath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static Tuple<string, Bitmap> Info(string path, uint pid, int iconPx)
        {
            Tuple<string, Bitmap> t;
            string cacheKey = (path ?? ("pid:" + pid)) + "|" + iconPx;
            if (appInfo.TryGetValue(cacheKey, out t)) return t;
            string name = null;
            Bitmap icon = null;
            if (path != null)
            {
                try { name = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    using (var big = new Icon(ic, 256, 256))
                    using (var src = big.ToBitmap())
                    {
                        icon = new Bitmap(iconPx, iconPx, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(icon))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, new Rectangle(0, 0, iconPx, iconPx));
                        }
                    }
                }
                catch { icon = null; }
            }
            if (name == null)
            {
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { name = "App"; }
            }
            t = Tuple.Create(name.Trim(), icon);
            appInfo[cacheKey] = t;
            return t;
        }

        public static void SetDefault(string id)
        {
            try
            {
                var pc = (CoreAudio.IPolicyConfig)new CoreAudio.PolicyConfigCom();
                pc.SetDefaultEndpoint(id, CoreAudio.eConsole);
                pc.SetDefaultEndpoint(id, CoreAudio.eMultimedia);
                pc.SetDefaultEndpoint(id, CoreAudio.eCommunications);
                Marshal.ReleaseComObject(pc);
            }
            catch (Exception ex) { Log.Write("mixer default device: " + ex.Message); }
        }

        public static void Release(List<MixerDevice> devices)
        {
            if (devices == null) return;
            foreach (var d in devices)
            {
                foreach (var a in d.Apps) foreach (var s in a.Volumes) try { Marshal.ReleaseComObject(s); } catch { }
                try { if (d.Endpoint != null) Marshal.ReleaseComObject(d.Endpoint); } catch { }
                try { if (d.Sessions != null) Marshal.ReleaseComObject(d.Sessions); } catch { }
            }
        }
    }

    // ------------------------------------------------------------------ flyout

    // A dark, tactile controller: a charcoal pill with a recessed display (gradient level + %),
    // two round mode buttons (switch output, mute) and a knurled rotary knob for the default
    // output, then one slim pill per app. Drawn into a per-pixel-alpha (layered) window so the
    // rounded shapes and soft shadows sit directly on the desktop.
    class MixerFlyout : Form
    {
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }

        // Preferences (komorebi-tray.json, edited in Alt+C → Volume mixer)
        public const string PrefInactive = "mixer_inactive_apps", PrefColour = "mixer_colour";
        public static readonly string[] Colours = { "Ice", "Amber", "Accent" };

        // Layout in logical px (× S)
        const int Pad = 26, PillW = 380, MainH = 104, AppH = 56, AppGap = 8, MainGap = 12;

        readonly float S;
        readonly NotifyIcon anchorIcon;
        readonly bool topCenter;
        List<MixerDevice> devices = new List<MixerDevice>();
        MixerDevice main;
        readonly List<Part> parts = new List<Part>();
        Bitmap frame;
        Rectangle screenRect;
        bool inactiveApps;
        Color fillA, fillB;

        readonly Timer tick = new Timer { Interval = 15 };
        readonly Timer pollTimer = new Timer { Interval = 400 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastFrame;
        float openT;
        bool closing;
        DateTime shownAt;
        Part hover, drag;
        Point dragStart;
        float dragStartValue;
        int polls;

        enum PartKind { MainTrack, Knob, OutputButton, MuteButton, AppButton, AppTrack }
        class Part
        {
            public PartKind Kind; public RectangleF Rect; public MixerDevice Device; public MixerApp App;
            public float HoverT, PressT;
            public float Value { get { return App != null ? App.Volume : Device.Volume; } }
            public float Shown { get { return App != null ? App.Shown : Device.Shown; } set { if (App != null) App.Shown = value; else Device.Shown = value; } }
            public bool Muted { get { return App != null ? App.Muted : Device.Muted; } }
            public void Set(float v) { v = Math.Max(0, Math.Min(1, v)); if (App != null) App.SetVolume(v); else Device.SetVolume(v); }
            public void ToggleMute() { if (App != null) App.SetMute(!App.Muted); else Device.SetMute(!Device.Muted); }
        }

        static MixerFlyout current;

        // Opens the mixer (or closes it if open). topCenter: top centre of the monitor under the
        // mouse (the shortcut); otherwise under the tray icon (the tray menu).
        public static void Toggle(NotifyIcon icon, bool topCenter)
        {
            if (current != null && !current.IsDisposed)
            {
                if (!current.closing) current.BeginClose();
                return;
            }
            current = new MixerFlyout(icon, topCenter);
            current.Open();
        }

        MixerFlyout(NotifyIcon icon, bool topCenter)
        {
            anchorIcon = icon;
            this.topCenter = topCenter;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            tick.Tick += (s, e) => Step();
            pollTimer.Tick += (s, e) => Poll();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x8;   // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        float P(float v) { return v * S; }
        int Pi(float v) { return (int)Math.Round(v * S); }

        // ---- open / close

        void Open()
        {
            var prefs = AccentBorder.LoadPrefs();
            object v;
            inactiveApps = !prefs.TryGetValue(PrefInactive, out v) || Convert.ToBoolean(v);
            string colour = prefs.TryGetValue(PrefColour, out v) ? Convert.ToString(v) : "Ice";
            if (colour == "Amber") { fillA = Color.FromArgb(255, 226, 150); fillB = Color.FromArgb(247, 140, 58); }
            else if (colour == "Accent") { Theme.Refresh(); fillA = Mix(Theme.Accent, Color.White, 0.45); fillB = Theme.Accent; }
            else { fillA = Color.FromArgb(92, 230, 206); fillB = Color.FromArgb(110, 182, 255); }

            devices = AudioModel.Load(inactiveApps, Pi(18));
            main = devices.Count > 0 ? devices[0] : null;
            Build();
            Place();
            var hwnd = Handle;
            openT = 0;
            Render();
            Show();
            Native.AllowSetForegroundWindow(-1);
            SetForegroundWindow(Handle);
            Activate();
            shownAt = DateTime.UtcNow;
            lastFrame = clock.Elapsed.TotalMilliseconds;
            tick.Start();
            pollTimer.Start();
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        void BeginClose()
        {
            if (closing) return;
            closing = true;
            drag = null;
            Kick();
        }

        void Finish()
        {
            tick.Stop(); pollTimer.Stop();
            Hide();
            AudioModel.Release(devices);
            devices.Clear();
            if (frame != null) frame.Dispose();
            if (current == this) current = null;
            Close();
            Dispose();
            Native.Trim();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if ((DateTime.UtcNow - shownAt).TotalMilliseconds > 150) BeginClose();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { BeginClose(); e.Handled = true; }
            else if (main != null && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right)) { main.SetVolume(Math.Min(1, Round2(main.Volume + 0.02f))); Kick(); }
            else if (main != null && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left)) { main.SetVolume(Math.Max(0, Round2(main.Volume - 0.02f))); Kick(); }
            else if (main != null && e.KeyCode == Keys.M) { main.SetMute(!main.Muted); Kick(); }
            base.OnKeyDown(e);
        }

        static float Round2(float v) { return (float)Math.Round(v * 100) / 100f; }

        // ---- layout

        RectangleF MainRect { get { return new RectangleF(P(Pad), P(Pad), P(PillW), P(MainH)); } }

        void Build()
        {
            parts.Clear();
            float x = P(Pad), y = P(Pad);
            if (main != null)
            {
                parts.Add(new Part { Kind = PartKind.MainTrack, Device = main, Rect = new RectangleF(x + P(26), y + P(44), P(206), P(36)) });
                parts.Add(new Part { Kind = PartKind.OutputButton, Device = main, Rect = Circle(x + P(262), y + P(32), P(17)) });
                parts.Add(new Part { Kind = PartKind.MuteButton, Device = main, Rect = Circle(x + P(262), y + P(72), P(17)) });
                parts.Add(new Part { Kind = PartKind.Knob, Device = main, Rect = Circle(x + P(PillW - 54), y + P(MainH / 2f), P(40)) });
                float ay = y + P(MainH) + P(MainGap);
                foreach (var a in main.Apps)
                {
                    parts.Add(new Part { Kind = PartKind.AppButton, Device = main, App = a, Rect = Circle(x + P(30), ay + P(AppH / 2f), P(18)) });
                    parts.Add(new Part { Kind = PartKind.AppTrack, Device = main, App = a, Rect = new RectangleF(x + P(58), ay + P(11), P(PillW - 58 - 12), P(34)) });
                    ay += P(AppH) + P(AppGap);
                }
            }
            int apps = main != null ? main.Apps.Count : 0;
            float h = P(Pad) + P(MainH) + (apps > 0 ? P(MainGap) + apps * P(AppH) + (apps - 1) * P(AppGap) : 0) + P(Pad);
            screenRect.Width = Pi(PillW + Pad * 2);
            screenRect.Height = (int)Math.Ceiling(h);
        }

        static RectangleF Circle(float cx, float cy, float r) { return new RectangleF(cx - r, cy - r, r * 2, r * 2); }

        void Place()
        {
            int w = screenRect.Width, h = screenRect.Height;
            if (topCenter)
            {
                var scr = Screen.FromPoint(Cursor.Position);
                var work = scr.WorkingArea;
                // The pill (not the shadow margin) sits 10px below the top of the work area
                screenRect = new Rectangle(work.Left + (work.Width - w) / 2, work.Top + Pi(10) - Pi(Pad), w, h);
            }
            else
            {
                Rectangle icon = IconRect();
                Point anchor = icon.Width > 0 ? new Point(icon.X + icon.Width / 2, icon.Y + icon.Height / 2) : Cursor.Position;
                var scr = Screen.FromPoint(anchor);
                var work = scr.WorkingArea;
                bool bottom = anchor.Y > scr.Bounds.Top + scr.Bounds.Height / 2;
                int left = Math.Max(work.Left - Pi(Pad) + Pi(4), Math.Min(anchor.X - w / 2, work.Right - w + Pi(Pad) - Pi(4)));
                int top = bottom ? work.Bottom - h + Pi(Pad) - Pi(10) : work.Top + Pi(10) - Pi(Pad);
                screenRect = new Rectangle(left, top, w, h);
            }
            Bounds = screenRect;
        }

        Rectangle IconRect()
        {
            try
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var window = typeof(NotifyIcon).GetField("window", flags).GetValue(anchorIcon) as NativeWindow;
                int id = (int)typeof(NotifyIcon).GetField("id", flags).GetValue(anchorIcon);
                if (window == null || window.Handle == IntPtr.Zero) return Rectangle.Empty;
                var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = window.Handle, uID = id };
                RECT r;
                if (Shell_NotifyIconGetRect(ref nid, out r) != 0 || r.Right <= r.Left) return Rectangle.Empty;
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { return Rectangle.Empty; }
        }

        // Height changes (apps come and go) keep the top edge where it is
        void Rebuild()
        {
            int top = screenRect.Y, left = screenRect.X;
            Build();
            screenRect.X = left; screenRect.Y = top;
            Bounds = screenRect;
        }

        // ---- data

        void Poll()
        {
            if (closing || drag != null || main == null) return;
            AudioModel.ReadDevice(main);
            if (++polls % 3 == 0)
            {
                var old = main.Apps;
                AudioModel.ReadApps(main, inactiveApps, Pi(18));
                foreach (var a in main.Apps) { var prev = old.Find(o => o.Key == a.Key); a.Shown = prev != null ? prev.Shown : a.Volume; }
                bool changed = old.Count != main.Apps.Count;
                for (int i = 0; !changed && i < old.Count; i++) changed = old[i].Key != main.Apps[i].Key;
                foreach (var a in old) foreach (var s in a.Volumes) try { Marshal.ReleaseComObject(s); } catch { }
                if (changed) { hover = null; }
                Rebuild();
            }
            else
                foreach (var a in main.Apps)
                    if (a.Volumes.Count > 0) { float v; bool m; try { if (a.Volumes[0].GetMasterVolume(out v) == 0) a.Volume = v; if (a.Volumes[0].GetMute(out m) == 0) a.Muted = m; } catch { } }
            Kick();
        }

        // Switch the default output to the next device, keeping the flyout open
        void CycleOutput()
        {
            if (devices.Count < 2 || main == null) return;
            var all = devices;
            int i = all.IndexOf(main);
            var next = all[(i + 1) % all.Count];
            AudioModel.SetDefault(next.Id);
            AudioModel.Release(devices);
            devices = AudioModel.Load(inactiveApps, Pi(18));
            main = devices.Count > 0 ? devices[0] : null;
            hover = null;
            Rebuild();
            Kick();
        }

        // ---- animation loop (only while something moves)

        void Step()
        {
            double now = clock.Elapsed.TotalMilliseconds;
            float dt = (float)Math.Min(0.05, (now - lastFrame) / 1000.0);
            lastFrame = now;
            bool moving = false;

            float speed = closing ? 1 / 0.14f : 1 / 0.24f;
            openT = closing ? Math.Max(0, openT - dt * speed) : Math.Min(1, openT + dt * speed);
            if (openT != (closing ? 0 : 1)) moving = true;

            foreach (var p in parts)
            {
                moving |= Ease(ref p.HoverT, p == hover && (drag == null || drag == p) ? 1 : 0, dt, 14);
                moving |= Ease(ref p.PressT, p == drag ? 1 : 0, dt, 18);
                if (p.Kind == PartKind.MainTrack || p.Kind == PartKind.AppTrack)
                {
                    float s = p.Shown;
                    moving |= Ease(ref s, p.Value, dt, drag != null && drag.Device == p.Device && drag.App == p.App ? 30 : 12);
                    p.Shown = s;
                }
            }

            Render();
            if (closing && openT <= 0) { Finish(); return; }
            if (!moving && drag == null) tick.Stop();
        }

        static bool Ease(ref float v, float target, float dt, float rate)
        {
            if (Math.Abs(v - target) < 0.002f) { if (v == target) return false; v = target; return true; }
            v += (target - v) * Math.Min(1, dt * rate);
            return true;
        }

        void Kick() { if (!tick.Enabled && !IsDisposed) { lastFrame = clock.Elapsed.TotalMilliseconds; tick.Start(); } }

        // ---- rendering

        void Render()
        {
            if (IsDisposed || screenRect.Width <= 0) return;
            int W = screenRect.Width, H = screenRect.Height;
            if (frame == null || frame.Width != W || frame.Height != H) { if (frame != null) frame.Dispose(); frame = new Bitmap(W, H, PixelFormat.Format32bppArgb); }

            float e = 1 - (float)Math.Pow(1 - openT, 3);
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // Entrance: drop in from 8px above and grow from 97%
                float sc = 0.97f + 0.03f * e;
                g.TranslateTransform(W / 2f, P(Pad));
                g.ScaleTransform(sc, sc);
                g.TranslateTransform(-W / 2f, -P(Pad) - P(8) * (1 - e));

                if (main == null) DrawEmpty(g);
                else
                {
                    DrawMain(g);
                    float ay = P(Pad) + P(MainH) + P(MainGap);
                    foreach (var a in main.Apps)
                    {
                        DrawAppPill(g, new RectangleF(P(Pad), ay, P(PillW), P(AppH)), a);
                        ay += P(AppH) + P(AppGap);
                    }
                }
            }
            Push((byte)Math.Round(255 * e));
        }

        // Charcoal body: soft drop shadow, vertical gradient, hairline highlight on top, dark outline
        void Body(Graphics g, RectangleF r)
        {
            float rad = r.Height / 2;
            for (int i = 8; i >= 1; i--)
            {
                var sh = RectangleF.Inflate(r, P(i * 1.6f), P(i * 1.6f));
                sh.Offset(0, P(7));
                using (var path = Rounded(sh, rad + P(i * 1.6f)))
                using (var br = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                    g.FillPath(br, path);
            }
            using (var path = Rounded(r, rad))
            {
                using (var lg = new LinearGradientBrush(r, Color.FromArgb(44, 44, 47), Color.FromArgb(27, 27, 29), 90f))
                    g.FillPath(lg, path);
                using (var pen = new Pen(Color.FromArgb(160, 0, 0, 0), P(1))) g.DrawPath(pen, path);
            }
            // Top-edge light catch
            var hl = RectangleF.Inflate(r, -P(1), -P(1));
            using (var path = Rounded(hl, hl.Height / 2))
            using (var lg = new LinearGradientBrush(hl, Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            {
                lg.SetBlendTriangularShape(0f, 1f);
                var blend = new Blend { Factors = new[] { 1f, 0.15f, 0f, 0f }, Positions = new[] { 0f, 0.12f, 0.5f, 1f } };
                lg.Blend = blend;
                using (var pen = new Pen(lg, P(1))) g.DrawPath(pen, path);
            }
        }

        void DrawEmpty(Graphics g)
        {
            var r = MainRect;
            Body(g, r);
            using (var f = new Font("Segoe UI", 13 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(Color.FromArgb(200, 230, 230, 235)))
                g.DrawString("No playback devices", f, br, r, sf);
        }

        void DrawMain(Graphics g)
        {
            var r = MainRect;
            Body(g, r);

            // Label: "Volume" and the output's name
            using (var f = new Font("Segoe UI", 13 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fd = new Font("Segoe UI", 12 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var br = new SolidBrush(Color.FromArgb(232, 232, 236)))
            using (var dim = new SolidBrush(Color.FromArgb(120, 120, 128)))
            {
                var lr = new RectangleF(r.X + P(30), r.Y + P(14), P(204), P(22));
                float vw = g.MeasureString("Volume", f, (int)lr.Width, sf).Width;
                g.DrawString("Volume", f, br, lr, sf);
                g.DrawString(main.Name, fd, dim, new RectangleF(lr.X + vw + P(4), lr.Y + P(0.5f), lr.Width - vw - P(4), lr.Height), sf);
            }

            foreach (var p in parts)
            {
                if (p.App != null) continue;
                if (p.Kind == PartKind.MainTrack) Display(g, p, null);
                else if (p.Kind == PartKind.OutputButton) RoundButton(g, p, "\uE7F5", devices.Count > 1, false);
                else if (p.Kind == PartKind.MuteButton) RoundButton(g, p, main.Muted ? "\uE74F" : "\uE767", true, main.Muted);
                else if (p.Kind == PartKind.Knob) Knob(g, p);
            }
        }

        void DrawAppPill(Graphics g, RectangleF r, MixerApp a)
        {
            Body(g, r);
            foreach (var p in parts)
            {
                if (p.App != a) continue;
                if (p.Kind == PartKind.AppButton) AppButton(g, p);
                else if (p.Kind == PartKind.AppTrack) Display(g, p, a.Name);
            }
        }

        // Recessed display: dark well, gradient level pill with a top gloss, % on the right
        void Display(Graphics g, Part p, string name)
        {
            var r = p.Rect;
            float rad = r.Height / 2;
            using (var path = Rounded(r, rad))
            {
                using (var br = new SolidBrush(Color.FromArgb(17, 17, 19))) g.FillPath(br, path);
                // Inner shadow along the top, faint light along the bottom lip
                using (var lg = new LinearGradientBrush(r, Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                {
                    lg.Blend = new Blend { Factors = new[] { 1f, 0f, 0f }, Positions = new[] { 0f, 0.35f, 1f } };
                    g.FillPath(lg, path);
                }
                using (var lg = new LinearGradientBrush(RectangleF.Inflate(r, 0, P(1)), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(22, 255, 255, 255), 90f))
                {
                    lg.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.8f, 1f } };
                    using (var pen = new Pen(lg, P(1))) g.DrawPath(pen, path);
                }
            }

            float inset = P(3);
            var inner = RectangleF.Inflate(r, -inset, -inset);
            float v = Math.Max(0, Math.Min(1, p.Shown));
            bool muted = p.Muted;
            if (v > 0.001f)
            {
                float w = Math.Max(inner.Height, inner.Width * v);
                var fr = new RectangleF(inner.X, inner.Y, w, inner.Height);
                using (var path = Rounded(fr, fr.Height / 2))
                {
                    Color a = muted ? Color.FromArgb(92, 92, 98) : fillA, b = muted ? Color.FromArgb(70, 70, 76) : fillB;
                    // Gradient spans the whole well so the colour shifts as the level grows
                    using (var lg = new LinearGradientBrush(new RectangleF(inner.X - 1, inner.Y, inner.Width + 2, inner.Height), a, b, 0f))
                        g.FillPath(lg, path);
                    using (var lg = new LinearGradientBrush(fr, Color.FromArgb(muted ? 30 : 70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    {
                        lg.Blend = new Blend { Factors = new[] { 1f, 0f, 0f }, Positions = new[] { 0f, 0.55f, 1f } };
                        g.FillPath(lg, path);
                    }
                    // Soft glow while touched
                    if (p.HoverT > 0.01f || p.PressT > 0.01f)
                        using (var pen = new Pen(Color.FromArgb((int)(60 * Math.Max(p.HoverT * 0.6f, p.PressT)), 255, 255, 255), P(1)))
                            g.DrawPath(pen, path);
                }
            }

            using (var f = new Font("Segoe UI", 13 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(Color.FromArgb(muted ? 120 : 228, 236, 236, 240)))
                g.DrawString(Math.Round(p.Value * 100) + "%", f, br, new RectangleF(inner.X, inner.Y, inner.Width - P(10), inner.Height), sf);

            if (name != null)
                using (var f = new Font("Segoe UI Semibold", 12 * S, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                using (var shadow = new SolidBrush(Color.FromArgb(muted ? 0 : 70, 0, 0, 0)))
                using (var br = new SolidBrush(Color.FromArgb(muted ? 130 : 245, 255, 255, 255)))
                {
                    var tr = new RectangleF(inner.X + P(12), inner.Y, inner.Width - P(64), inner.Height);
                    var trs = tr; trs.Offset(0, P(1));
                    g.DrawString(name, f, shadow, trs, sf);
                    g.DrawString(name, f, br, tr, sf);
                }
        }

        // Small round mode button: dark dish, rim light, glyph
        void RoundButton(Graphics g, Part p, string glyph, bool enabled, bool active)
        {
            var r = p.Rect;
            if (p.PressT > 0.01f) r = RectangleF.Inflate(r, -P(1) * p.PressT, -P(1) * p.PressT);
            using (var lg = new LinearGradientBrush(r, Color.FromArgb(active ? 16 : 22, active ? 16 : 22, active ? 18 : 24), Color.FromArgb(active ? 26 : 36, active ? 26 : 36, active ? 28 : 39), 90f))
                g.FillEllipse(lg, r);
            using (var lg = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.FromArgb(10, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90f))
            using (var pen = new Pen(lg, P(1))) g.DrawEllipse(pen, r);
            if (p.HoverT > 0.01f && enabled)
                using (var br = new SolidBrush(Color.FromArgb((int)(16 * p.HoverT), 255, 255, 255))) g.FillEllipse(br, r);
            int a = !enabled ? 70 : active ? 250 : (int)(185 + 60 * p.HoverT);
            using (var f = new Font("Segoe MDL2 Assets", 14 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(Color.FromArgb(a, 236, 236, 240)))
                g.DrawString(glyph, f, br, new RectangleF(r.X, r.Y + P(0.5f), r.Width, r.Height), sf);
        }

        void AppButton(Graphics g, Part p)
        {
            RoundButton(g, p, "", true, p.App.Muted);
            var r = p.Rect;
            float sz = P(18);
            var dest = new RectangleF(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz);
            float alpha = p.App.Muted ? 0.35f : 1f;
            if (p.App.Icon != null)
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(p.App.Icon, Rectangle.Round(dest), 0, 0, p.App.Icon.Width, p.App.Icon.Height, GraphicsUnit.Pixel, ia);
                }
            else
                using (var f = new Font("Segoe UI Semibold", 12 * S, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var br = new SolidBrush(Color.FromArgb((int)(230 * alpha), 236, 236, 240)))
                    g.DrawString(p.App.Name.Substring(0, 1).ToUpperInvariant(), f, br, dest, sf);
            if (p.App.Muted)
                using (var pen = new Pen(Color.FromArgb(220, 236, 236, 240), P(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, dest.Left + P(1), dest.Bottom - P(1), dest.Right - P(1), dest.Top + P(1));
        }

        // Knurled rotary knob: ridged outer ring that turns with the level, domed cap, dark centre well
        void Knob(Graphics g, Part p)
        {
            var r = p.Rect;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, R = r.Width / 2;
            float angle = (-135f + 270f * Math.Max(0, Math.Min(1, main.Shown))) * (float)Math.PI / 180f;

            // Shadow under the knob
            for (int i = 6; i >= 1; i--)
                using (var br = new SolidBrush(Color.FromArgb(14, 0, 0, 0)))
                    g.FillEllipse(br, cx - R - P(i), cy - R - P(i) + P(4), (R + P(i)) * 2, (R + P(i)) * 2);

            // Outer ring
            using (var lg = new LinearGradientBrush(r, Color.FromArgb(52, 52, 56), Color.FromArgb(22, 22, 24), 90f))
                g.FillEllipse(lg, r);
            // Knurling: fine ridges around the rim, rotated by the level
            int ridges = 56;
            for (int i = 0; i < ridges; i++)
            {
                double a = angle + i * Math.PI * 2 / ridges;
                float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                // Ridges facing up catch more light
                float light = 0.5f - 0.5f * sa;
                using (var pen = new Pen(Color.FromArgb((int)(28 + 70 * light), 255, 255, 255), P(1)))
                    g.DrawLine(pen, cx + ca * (R - P(7)), cy + sa * (R - P(7)), cx + ca * (R - P(1.5f)), cy + sa * (R - P(1.5f)));
                using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0), P(1)))
                    g.DrawLine(pen, cx + (float)Math.Cos(a + 0.03) * (R - P(7)), cy + (float)Math.Sin(a + 0.03) * (R - P(7)), cx + (float)Math.Cos(a + 0.03) * (R - P(1.5f)), cy + (float)Math.Sin(a + 0.03) * (R - P(1.5f)));
            }
            using (var pen = new Pen(Color.FromArgb(150, 0, 0, 0), P(1))) g.DrawEllipse(pen, r);

            // Domed cap
            float rc = R - P(9);
            var cap = new RectangleF(cx - rc, cy - rc, rc * 2, rc * 2);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cap);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterPoint = new PointF(cx - rc * 0.35f, cy - rc * 0.45f);
                    pg.CenterColor = Color.FromArgb(64, 64, 68);
                    pg.SurroundColors = new[] { Color.FromArgb(28, 28, 30) };
                    g.FillPath(pg, path);
                }
            }
            using (var lg = new LinearGradientBrush(RectangleF.Inflate(cap, 1, 1), Color.FromArgb(55, 255, 255, 255), Color.FromArgb(120, 0, 0, 0), 90f))
            using (var pen = new Pen(lg, P(1))) g.DrawEllipse(pen, cap);

            // Centre well
            float rw = P(12);
            var well = new RectangleF(cx - rw, cy - rw, rw * 2, rw * 2);
            using (var lg = new LinearGradientBrush(well, Color.FromArgb(8, 8, 9), Color.FromArgb(24, 24, 26), 90f))
                g.FillEllipse(lg, well);
            using (var lg = new LinearGradientBrush(RectangleF.Inflate(well, 1, 1), Color.FromArgb(140, 0, 0, 0), Color.FromArgb(40, 255, 255, 255), 90f))
            using (var pen = new Pen(lg, P(1))) g.DrawEllipse(pen, well);

            // Level marker on the cap, in the fill colour
            float mr = rc - P(6);
            float mx = cx + (float)Math.Cos(angle - Math.PI / 2 + Math.PI / 2) * 0, my = 0;
            mx = cx + (float)Math.Sin(angle) * mr;
            my = cy - (float)Math.Cos(angle) * mr;
            using (var br = new SolidBrush(main.Muted ? Color.FromArgb(110, 110, 116) : fillB))
                g.FillEllipse(br, mx - P(2.2f), my - P(2.2f), P(4.4f), P(4.4f));

            if (p.HoverT > 0.01f)
                using (var br = new SolidBrush(Color.FromArgb((int)(10 * p.HoverT), 255, 255, 255))) g.FillEllipse(br, r);
        }

        void Push(byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = frame.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hbmp);
                var size = new SIZE(frame.Width, frame.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(screenRect.X, screenRect.Y);
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- input

        Part HitPart(Point pt)
        {
            foreach (var p in parts)
            {
                if (p.Kind == PartKind.Knob || p.Kind == PartKind.OutputButton || p.Kind == PartKind.MuteButton || p.Kind == PartKind.AppButton)
                {
                    float cx = p.Rect.X + p.Rect.Width / 2, cy = p.Rect.Y + p.Rect.Height / 2, rr = p.Rect.Width / 2 + P(2);
                    if ((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy) <= rr * rr) return p;
                }
                else if (RectangleF.Inflate(p.Rect, 0, P(4)).Contains(pt)) return p;
            }
            return null;
        }

        float TrackValue(Part p, int x)
        {
            float inset = P(3);
            return Math.Max(0, Math.Min(1, (x - p.Rect.X - inset) / (p.Rect.Width - inset * 2)));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (closing) return;
            if (drag != null)
            {
                if (drag.Kind == PartKind.Knob)
                {
                    // Up or right turns it up; 220px of travel is the full range
                    float delta = ((e.X - dragStart.X) - (e.Y - dragStart.Y)) / P(220);
                    drag.Set(Round2(dragStartValue + delta));
                }
                else drag.Set(Round2(TrackValue(drag, e.X)));
                Kick();
                return;
            }
            var h = HitPart(e.Location);
            if (h != hover) { hover = h; Kick(); Cursor = h != null ? Cursors.Hand : Cursors.Default; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (drag == null && hover != null) { hover = null; Kick(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (closing || e.Button != MouseButtons.Left) return;
            var p = HitPart(e.Location);
            if (p == null) return;
            switch (p.Kind)
            {
                case PartKind.OutputButton: drag = p; CycleOutput(); return;
                case PartKind.MuteButton:
                case PartKind.AppButton: drag = p; p.ToggleMute(); Kick(); return;
                case PartKind.Knob:
                    drag = p; dragStart = e.Location; dragStartValue = p.Value; Capture = true; Kick(); return;
                default:
                    drag = p; Capture = true; p.Set(Round2(TrackValue(p, e.X))); Kick(); return;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (drag != null) { drag = null; Capture = false; hover = HitPart(e.Location); Kick(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var p = HitPart(e.Location);
            if (p == null)
            {
                // Over empty space on the main pill: the main volume
                if (main == null || !MainRect.Contains(e.Location)) return;
                p = parts.Find(x => x.Kind == PartKind.Knob);
            }
            if (p.Kind == PartKind.OutputButton) return;
            p.Set(Round2(p.Value + Math.Sign(e.Delta) * 0.02f));
            Kick();
        }
    }
}
