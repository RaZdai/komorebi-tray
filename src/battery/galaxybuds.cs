// Samsung Galaxy Buds (Buds, Buds+, Live, Pro, Buds2, Buds2 Pro, FE, Core, Buds3 / 3 Pro / 3 FE ...). Ported from
// Wireless Battery (galaxy_buds.cpp, galaxy_buds_protocol.h; GPL-3.0-or-later).
// The earbuds accept an RFCOMM connection on Samsung's SPP service and push a status frame with the left, right and
// case levels as soon as it opens; nothing is sent to them. Frame format and service UUIDs from GalaxyBudsClient
// (timschneeb/GalaxyBudsClient): FD [size lo][size hi | flags] [id] [payload] [crc lo][crc hi] DD (original Buds:
// FE [type] [size] ... EE), CRC-16/XMODEM over id + payload; 0x60 STATUS_UPDATED, 0x61 EXTENDED_STATUS_UPDATED.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace KomorebiTray
{
    static class GalaxyBudsDriver
    {
        static readonly Guid SppNew = new Guid("2e73a4ad-332d-41fc-90e2-16bef06523f2");
        static readonly Guid SppStandard = new Guid("00001101-0000-1000-8000-00805f9b34fb");
        static readonly Guid SppLegacy = new Guid("00001102-0000-1000-8000-00805f9b34fd");

        class Status { public int Left = -1, Right = -1, Case = -1; }

        static ushort Crc16(byte[] data, int off, int size)
        {
            ushort crc = 0;
            for (int i = 0; i < size; i++)
            {
                crc ^= (ushort)(data[off + i] << 8);
                for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        static int Level(byte v) { return v >= 1 && v <= 100 ? v : -1; }

        // Scans a received byte stream for status frames and keeps the levels of the last valid one
        static bool FindStatus(byte[] data, int size, bool legacy, Status output)
        {
            byte som = (byte)(legacy ? 0xFE : 0xFD), eom = (byte)(legacy ? 0xEE : 0xDD);
            bool found = false;
            for (int i = 0; i + 6 <= size; i++)
            {
                if (data[i] != som) continue;
                int len;
                if (legacy) len = data[i + 2];
                else
                {
                    int header = data[i + 1] | (data[i + 2] << 8);
                    if ((header & 0x2000) != 0) continue;   // fragments only carry firmware data
                    len = header & 0x3FF;
                }
                if (len < 3 || i + 3 + len >= size || data[i + 3 + len] != eom) continue;
                int body = i + 3;   // id, payload, crc
                ushort crc = (ushort)(data[body + len - 2] | (data[body + len - 1] << 8));
                if (Crc16(data, body, len - 2) != crc) continue;
                byte id = data[body];
                int p = body + 1, plen = len - 3;
                var s = new Status();
                if (id == 0x61 && plen >= 4)
                {
                    s.Left = Level(data[p + 2]);
                    s.Right = Level(data[p + 3]);
                    if (!legacy && plen >= 8) s.Case = Level(data[p + 7]);
                }
                else if (id == 0x60 && plen >= 3)
                {
                    s.Left = Level(data[p + 1]);
                    s.Right = Level(data[p + 2]);
                    if (!legacy && plen >= 7) s.Case = Level(data[p + 6]);
                }
                else continue;
                output.Left = s.Left; output.Right = s.Right; output.Case = s.Case;
                found = true;
                i += 3 + len;   // skip past this frame's EOM
            }
            return found;
        }

        class Buds { public string Name, AddrText; public ulong Addr; }

        static List<Buds> FindConnectedBuds()
        {
            var output = new List<Buds>();
            IntPtr di = DevProps.SetupDiGetClassDevs(IntPtr.Zero, "BTHENUM", IntPtr.Zero, 0x6);
            if (di == new IntPtr(-1)) return output;
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
                    if (!inst.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) || inst.Length < 24) continue;
                    string hex = inst.Substring(12, 12);
                    ulong addr;
                    if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out addr) || addr == 0) continue;
                    uint t;
                    byte[] cn = DevProps.Get(di, ref dd, DevProps.IsConnected, out t);
                    if (cn == null || t != DevProps.TYPE_BOOLEAN || cn[0] == 0) continue;   // never page a device that isn't connected
                    string name = DevProps.Str(di, ref dd, DevProps.FriendlyName, false);
                    if (name.Length == 0 || !name.ToLowerInvariant().Contains("buds")) continue;
                    output.Add(new Buds { Name = name, AddrText = hex, Addr = addr });
                }
            }
            finally { DevProps.SetupDiDestroyDeviceInfoList(di); }
            return output;
        }

        // SOCKADDR_BTH: family (2), btAddr (8), serviceClassId (16), port (4) = 30 bytes; Windows looks the channel up over SDP
        class BtEndPoint : EndPoint
        {
            readonly ulong addr;
            readonly Guid service;
            public BtEndPoint(ulong addr, Guid service) { this.addr = addr; this.service = service; }
            public override AddressFamily AddressFamily { get { return (AddressFamily)32; } }
            public override SocketAddress Serialize()
            {
                var sa = new SocketAddress((AddressFamily)32, 30);
                for (int i = 0; i < 8; i++) sa[2 + i] = (byte)(addr >> (8 * i));
                byte[] g = service.ToByteArray();
                for (int i = 0; i < 16; i++) sa[10 + i] = g[i];
                return sa;
            }
            public override EndPoint Create(SocketAddress sa) { return this; }
        }

        // Opens the RFCOMM service and listens for the status frame the earbuds send on connect
        static bool ReadStatus(ulong addr, Guid service, bool legacy, Status st)
        {
            try
            {
                using (var s = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3))   // AF_BTH, BTHPROTO_RFCOMM
                {
                    IAsyncResult ar = s.BeginConnect(new BtEndPoint(addr, service), null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                    s.EndConnect(ar);
                    var buf = new List<byte>();
                    long deadline = Bat.Now + 3000;
                    var chunk = new byte[512];
                    bool found = false;
                    while (!found && buf.Count < 8192)
                    {
                        long left = deadline - Bat.Now;
                        if (left <= 0 || !s.Poll((int)Math.Min(int.MaxValue / 1000, left) * 1000, SelectMode.SelectRead)) break;
                        int n = s.Receive(chunk);
                        if (n <= 0) break;
                        for (int i = 0; i < n; i++) buf.Add(chunk[i]);
                        byte[] arr = buf.ToArray();
                        found = FindStatus(arr, arr.Length, legacy, st);
                    }
                    return found;
                }
            }
            catch { return false; }
        }

        static string Pct(int v) { return v >= 0 ? v + "%" : "-"; }

        public static void Poll(PollContext ctx)
        {
            List<Buds> list = FindConnectedBuds();
            foreach (Buds b in list)
            {
                var st = new Status();
                bool ok = ReadStatus(b.Addr, SppNew, false, st) || ReadStatus(b.Addr, SppStandard, false, st) || ReadStatus(b.Addr, SppLegacy, true, st);
                if (!ok) continue;
                var s = new DeviceStatus { Key = "galaxybuds:" + b.AddrText, Name = b.Name, Via = "Bluetooth", Kind = DeviceKind.Headset };
                // The lower earbud is shown; the detail lists all three parts
                if (st.Left >= 0 && st.Right >= 0) s.Percent = Math.Min(st.Left, st.Right);
                else s.Percent = st.Left >= 0 ? st.Left : st.Right;
                s.Detail = "L " + Pct(st.Left) + ", R " + Pct(st.Right);
                if (st.Case >= 0) s.Detail += ", case " + Pct(st.Case);
                s.GraceSec = 180;   // a busy link (Galaxy Buds app open) shouldn't make them flicker
                ctx.Out.Add(s);
            }
        }
    }
}
