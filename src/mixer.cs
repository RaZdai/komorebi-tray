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
        public const string PrefInactive = "mixer_inactive_apps";

        // Layout in logical px (× S)
        const int Pad = 18;   // room around the panel for its shadow

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
        double dragAngle;
        float knobValue;
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
            Color c = KomorebiColour(prefs);
            fillA = Mix(c, Color.White, 0.38);
            fillB = c;

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

        // The menus' accent: the Windows accent while "Match Windows accent" is on (its readable shade for a
        // very dark accent), komorebi-tray's own colour otherwise. Same as Alt+K, Alt+C and Alt+Space.
        static Color KomorebiColour(Dictionary<string, object> prefs)
        {
            Theme.Refresh(AccentBorder.Follows(prefs));
            return Theme.Accent;
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

        // ---- layout (the tray's menu style: one dark panel, accent outline, header, divider, rows, key-hint footer)

        const int PanelW = 420, HeaderH = 44, MainBlockH = 100, AppRowH = 40, FooterH = 34;

        RectangleF Panel { get { return new RectangleF(P(Pad), P(Pad), P(PanelW), screenRect.Height - P(Pad) * 2); } }
        float MainTop { get { return P(Pad) + P(HeaderH); } }
        float AppsTop { get { return MainTop + P(MainBlockH) + P(6); } }
        float FooterTop
        {
            get
            {
                int apps = main != null ? main.Apps.Count : 0;
                return apps > 0 ? AppsTop + apps * P(AppRowH) + P(6) : MainTop + P(MainBlockH);
            }
        }

        void Build()
        {
            parts.Clear();
            float x = P(Pad);
            if (main != null)
            {
                float cy = MainTop + P(MainBlockH) / 2;
                parts.Add(new Part { Kind = PartKind.MainTrack, Device = main, Rect = new RectangleF(x + P(20), cy - P(18), P(230), P(36)) });
                parts.Add(new Part { Kind = PartKind.OutputButton, Device = main, Rect = new RectangleF(x + P(266), cy - P(36), P(32), P(32)) });
                parts.Add(new Part { Kind = PartKind.MuteButton, Device = main, Rect = new RectangleF(x + P(266), cy + P(4), P(32), P(32)) });
                parts.Add(new Part { Kind = PartKind.Knob, Device = main, Rect = Circle(x + P(PanelW - 60), cy, P(40)) });
                float ay = AppsTop;
                foreach (var a in main.Apps)
                {
                    parts.Add(new Part { Kind = PartKind.AppButton, Device = main, App = a, Rect = new RectangleF(x + P(14), ay + P(4), P(32), P(AppRowH - 8)) });
                    parts.Add(new Part { Kind = PartKind.AppTrack, Device = main, App = a, Rect = new RectangleF(x + P(186), ay + P(4), P(PanelW - 186 - 64), P(AppRowH - 8)) });
                    ay += P(AppRowH);
                }
            }
            float bottom = (main == null ? MainTop + P(MainBlockH) : FooterTop) + P(FooterH);
            screenRect.Width = Pi(PanelW + Pad * 2);
            screenRect.Height = (int)Math.Ceiling(bottom + P(Pad));
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

        static readonly Color Well = Color.FromArgb(18, 18, 20);
        static readonly Color Track = Color.FromArgb(58, 58, 64);

        Font MonoFont(float px, bool bold) { return new Font(Theme.Mono.FontFamily, px * S, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }

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
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // Entrance: drop in from 8px above and grow from 97% (no transform at rest, so the final frame is pixel-exact)
                if (e < 0.999f)
                {
                    float sc = 0.97f + 0.03f * e;
                    g.TranslateTransform(W / 2f, P(Pad));
                    g.ScaleTransform(sc, sc);
                    g.TranslateTransform(-W / 2f, -P(Pad) - P(8) * (1 - e));
                }

                var panel = Panel;
                DrawPanel(g, panel);
                DrawHeader(g, panel);
                if (main == null)
                {
                    using (var f = MonoFont(12.5f, false))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var br = new SolidBrush(Theme.Dim))
                        Text(g, "No playback devices", f, br, new RectangleF(panel.X, MainTop, panel.Width, P(MainBlockH)), sf);
                }
                else
                {
                    foreach (var p in parts)
                    {
                        if (p.App != null) continue;
                        if (p.Kind == PartKind.MainTrack) Display(g, p);
                        else if (p.Kind == PartKind.OutputButton) SquareButton(g, p, "", devices.Count > 1, false);
                        else if (p.Kind == PartKind.MuteButton) SquareButton(g, p, main.Muted ? "" : "", true, main.Muted);
                        else if (p.Kind == PartKind.Knob) Knob(g, p);
                    }
                    if (main.Apps.Count > 0)
                    {
                        Divider(g, panel, AppsTop - P(6));
                        float ay = AppsTop;
                        foreach (var a in main.Apps) { DrawAppRow(g, new RectangleF(panel.X + P(8), ay, panel.Width - P(16), P(AppRowH)), a); ay += P(AppRowH); }
                    }
                }
                DrawFooter(g, panel);
            }
            Push((byte)Math.Round(255 * e));
        }

        // The menus' window: dark panel, thin accent outline, soft shadow
        void DrawPanel(Graphics g, RectangleF r)
        {
            float rad = P(8);
            for (int i = 6; i >= 1; i--)
            {
                var sh = RectangleF.Inflate(r, P(i * 1.5f), P(i * 1.5f));
                sh.Offset(0, P(4));
                using (var path = Rounded(sh, rad + P(i * 1.5f)))
                using (var br = new SolidBrush(Color.FromArgb(10, 0, 0, 0)))
                    g.FillPath(br, path);
            }
            using (var path = Rounded(r, rad))
            {
                using (var br = new SolidBrush(Theme.Bg)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Accent, P(1))) g.DrawPath(pen, path);
            }
        }

        void Divider(Graphics g, RectangleF panel, float y)
        {
            using (var pen = new Pen(Theme.Border, P(1)))
                g.DrawLine(pen, panel.X + P(12), y, panel.Right - P(12), y);
        }

        // "Volume" in the dim header style of the other menus, the output's name on the right
        void DrawHeader(Graphics g, RectangleF panel)
        {
            var hr = new RectangleF(panel.X + P(20), panel.Y, panel.Width - P(40), P(HeaderH));
            using (var f = MonoFont(14, true))
            using (var fd = MonoFont(12, false))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var sfr = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var dim = new SolidBrush(Theme.Dim))
            using (var text = new SolidBrush(Theme.TextSoft))
            {
                float tw = g.MeasureString("Volume", f).Width;
                Text(g, "Volume", f, dim, hr, sf);
                if (main != null)
                    Text(g, main.Name, fd, text, new RectangleF(hr.X + tw + P(16), hr.Y, hr.Width - tw - P(16), hr.Height), sfr);
            }
            Divider(g, panel, panel.Y + P(HeaderH));
        }

        void DrawFooter(Graphics g, RectangleF panel)
        {
            float top = main == null ? MainTop + P(MainBlockH) : FooterTop;
            Divider(g, panel, top);
            using (var f = MonoFont(11, false))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var br = new SolidBrush(Theme.Dim))
                Text(g, "↑↓ volume   ↻ turn knob   m mute   esc close", f, br, new RectangleF(panel.X + P(20), top, panel.Width - P(40), P(FooterH)), sf);
        }

        // Recessed level display: dark well with a grey outline, flat accent level, % on the right
        void Display(Graphics g, Part p)
        {
            var r = p.Rect;
            using (var path = Rounded(r, P(4)))
            {
                using (var br = new SolidBrush(Well)) g.FillPath(br, path);
                using (var lg = new LinearGradientBrush(r, Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                {
                    lg.Blend = new Blend { Factors = new[] { 1f, 0f, 0f }, Positions = new[] { 0f, 0.3f, 1f } };
                    g.FillPath(lg, path);
                }
                using (var pen = new Pen(p.HoverT > 0.01f || p.PressT > 0.01f ? Blend(Theme.Border, Theme.Accent, Math.Max(p.HoverT * 0.6f, p.PressT)) : Theme.Border, P(1)))
                    g.DrawPath(pen, path);
            }

            var inner = RectangleF.Inflate(r, -P(4), -P(4));
            float v = Math.Max(0, Math.Min(1, p.Shown));
            bool muted = p.Muted;
            Color fill = muted ? Color.FromArgb(78, 78, 84) : fillB;
            if (v > 0.001f)
            {
                var fr = new RectangleF(inner.X, inner.Y, Math.Max(P(3), inner.Width * v), inner.Height);
                using (var path = Rounded(fr, P(2)))
                {
                    using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                    using (var lg = new LinearGradientBrush(fr, Color.FromArgb(muted ? 20 : 46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    {
                        lg.Blend = new Blend { Factors = new[] { 1f, 0f, 0f }, Positions = new[] { 0f, 0.5f, 1f } };
                        g.FillPath(lg, path);
                    }
                }
            }

            using (var f = MonoFont(13, true))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                bool overFill = inner.X + inner.Width * v > inner.Right - P(52);
                bool darkText = overFill && Luma(fill) > 90;
                Color tc = darkText ? Color.FromArgb(muted ? 150 : 230, 18, 18, 20) : Color.FromArgb(muted ? 120 : 236, Theme.Text);
                using (var br = new SolidBrush(tc))
                    Text(g, Math.Round(p.Value * 100) + "%", f, br, new RectangleF(inner.X, inner.Y, inner.Width - P(8), inner.Height), sf);
            }
        }

        static double Luma(Color c) { return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B; }

        static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Square button like the settings screen's choice buttons: grey outline, filled with the accent when active
        void SquareButton(Graphics g, Part p, string glyph, bool enabled, bool active)
        {
            var r = p.Rect;
            if (p.PressT > 0.01f) r = RectangleF.Inflate(r, -P(1) * p.PressT, -P(1) * p.PressT);
            Color bg = active ? Theme.Accent : (p.HoverT > 0.01f && enabled ? Blend(Theme.Surface, Theme.Hover, p.HoverT) : Theme.Surface);
            Color border = active ? Theme.Accent : Theme.Border;
            using (var path = Rounded(r, P(4)))
            {
                using (var br = new SolidBrush(bg)) g.FillPath(br, path);
                using (var pen = new Pen(border, P(1))) g.DrawPath(pen, path);
            }
            Color fg = !enabled ? Color.FromArgb(80, Theme.Text) : active ? (Luma(Theme.Accent) > 150 ? Color.FromArgb(20, 20, 22) : Color.White) : Theme.Text;
            using (var f = new Font("Segoe MDL2 Assets", 14 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(fg))
                Text(g, glyph, f, br, new RectangleF(r.X, r.Y + P(0.5f), r.Width, r.Height), sf);
        }

        // An app: a list row like the other menus (selection block with an accent bar on hover), icon, name,
        // the settings screen's thin slider, and its %
        void DrawAppRow(Graphics g, RectangleF row, MixerApp a)
        {
            Part icon = null, track = null;
            foreach (var p in parts) if (p.App == a) { if (p.Kind == PartKind.AppButton) icon = p; else if (p.Kind == PartKind.AppTrack) track = p; }
            if (icon == null || track == null) return;
            float hov = Math.Max(Math.Max(icon.HoverT, track.HoverT), track.PressT);
            if (hov > 0.01f)
            {
                using (var path = Rounded(row, P(4)))
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * hov), Theme.Selection)))
                    g.FillPath(br, path);
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * hov), Theme.Accent)))
                    g.FillRectangle(br, row.X, row.Y + P(7), P(3), row.Height - P(14));
            }

            bool muted = a.Muted;
            float cy = row.Y + row.Height / 2;
            // Icon (click to mute)
            float sz = P(18);
            var dest = new RectangleF(icon.Rect.X + (icon.Rect.Width - sz) / 2, cy - sz / 2, sz, sz);
            float alpha = muted ? 0.35f : 1f;
            if (a.Icon != null)
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(a.Icon, Rectangle.Round(dest), 0, 0, a.Icon.Width, a.Icon.Height, GraphicsUnit.Pixel, ia);
                }
            else
                using (var f = MonoFont(12, true))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var br = new SolidBrush(Color.FromArgb((int)(230 * alpha), Theme.Text)))
                    Text(g, a.Name.Substring(0, 1).ToUpperInvariant(), f, br, dest, sf);
            if (muted)
                using (var pen = new Pen(Color.FromArgb(220, Theme.Text), P(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, dest.Left + P(1), dest.Bottom - P(1), dest.Right - P(1), dest.Top + P(1));

            // Name
            using (var f = MonoFont(12.5f, true))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var br = new SolidBrush(muted ? Theme.Dim : Theme.Text))
                Text(g, a.Name, f, br, new RectangleF(row.X + P(46), row.Y, P(128), row.Height), sf);

            // Slider (thin track, accent fill, white round thumb)
            var tr = track.Rect;
            float x0 = tr.X + P(8), x1 = tr.Right - P(8), th = P(4);
            float v = Math.Max(0, Math.Min(1, track.Shown));
            float tx = x0 + v * (x1 - x0);
            using (var path = Rounded(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2))
            using (var br = new SolidBrush(Track)) g.FillPath(br, path);
            if (tx > x0 + P(1))
                using (var path = Rounded(new RectangleF(x0, cy - th / 2, tx - x0, th), th / 2))
                using (var br = new SolidBrush(muted ? Color.FromArgb(96, 96, 104) : fillB)) g.FillPath(br, path);
            float tr2 = P(7) + P(1.5f) * Math.Max(track.HoverT, track.PressT);
            using (var br = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(br, tx - tr2, cy - tr2 + P(1), tr2 * 2, tr2 * 2);
            using (var br = new SolidBrush(muted ? Color.FromArgb(170, 170, 176) : Color.White)) g.FillEllipse(br, tx - tr2, cy - tr2, tr2 * 2, tr2 * 2);

            // %
            using (var f = MonoFont(12.5f, true))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(muted ? Theme.Dim : Theme.Text))
                Text(g, Math.Round(a.Volume * 100) + "%", f, br, new RectangleF(tr.Right, row.Y, row.Right - tr.Right - P(12), row.Height), sf);
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

            // Level light on the cap, in komorebi's colour (grey while muted), with a soft glow
            float mr = rc - P(6);
            float mx = cx + (float)Math.Sin(angle) * mr;
            float my = cy - (float)Math.Cos(angle) * mr;
            Color lamp = main.Muted ? Color.FromArgb(110, 110, 116) : Mix(fillB, Color.White, 0.15);
            if (!main.Muted)
                for (int i = 4; i >= 1; i--)
                {
                    float gr = P(2.2f + i * 1.6f);
                    using (var br = new SolidBrush(Color.FromArgb(22, fillB))) g.FillEllipse(br, mx - gr, my - gr, gr * 2, gr * 2);
                }
            using (var br = new SolidBrush(lamp))
                g.FillEllipse(br, mx - P(2.4f), my - P(2.4f), P(4.8f), P(4.8f));

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

        // Text goes on the opaque pills, so ClearType works; it is drawn on whole pixels without the
        // half-pixel offset the shapes use, which would otherwise smear small text.
        static void Text(Graphics g, string text, Font f, Brush br, RectangleF r, StringFormat sf)
        {
            var mode = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.None;
            var e = g.Transform.Elements;
            bool plain = Math.Abs(e[0] - 1) < 0.0001f && Math.Abs(e[3] - 1) < 0.0001f;
            if (plain)
            {
                // Snap to device pixels (the transform only translates once the entrance has finished)
                float x = (float)Math.Round(r.X + e[4]) - e[4], y = (float)Math.Round(r.Y + e[5]) - e[5];
                r = new RectangleF(x, y, (float)Math.Round(r.Width), (float)Math.Round(r.Height));
            }
            g.DrawString(text, f, br, r, sf);
            g.PixelOffsetMode = mode;
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
                if (p.Kind == PartKind.Knob)
                {
                    float cx = p.Rect.X + p.Rect.Width / 2, cy = p.Rect.Y + p.Rect.Height / 2, rr = p.Rect.Width / 2 + P(2);
                    if ((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy) <= rr * rr) return p;
                }
                else if (RectangleF.Inflate(p.Rect, P(1), P(2)).Contains(pt)) return p;
            }
            return null;
        }

        static PointF Centre(RectangleF r) { return new PointF(r.X + r.Width / 2, r.Y + r.Height / 2); }

        float TrackValue(Part p, int x)
        {
            float inset = p.Kind == PartKind.AppTrack ? P(8) : P(4);
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
                    // Turn it like a real knob: the change in angle around its centre moves the level.
                    // Clockwise turns it up; the 270° sweep of the marker is the full range.
                    var c = Centre(drag.Rect);
                    float dx = e.X - c.X, dy = e.Y - c.Y;
                    if (dx * dx + dy * dy < P(5) * P(5)) return;   // too close to the centre to read a direction
                    double a = Math.Atan2(dy, dx), d = a - dragAngle;
                    if (d > Math.PI) d -= 2 * Math.PI;
                    if (d < -Math.PI) d += 2 * Math.PI;
                    dragAngle = a;
                    knobValue = Math.Max(0, Math.Min(1, knobValue + (float)(d / (1.5 * Math.PI))));
                    drag.Set(Round2(knobValue));
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
                    {
                        var c = Centre(p.Rect);
                        drag = p; dragAngle = Math.Atan2(e.Y - c.Y, e.X - c.X); knobValue = p.Value; Capture = true; Kick(); return;
                    }
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
                if (main == null || !Panel.Contains(e.Location)) return;
                p = parts.Find(x => x.Kind == PartKind.Knob);
            }
            if (p.Kind == PartKind.OutputButton) return;
            p.Set(Round2(p.Value + Math.Sign(e.Delta) * 0.02f));
            Kick();
        }
    }
}
