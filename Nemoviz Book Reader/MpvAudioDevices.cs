using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Enumerates libmpv's output audio devices via the <c>audio-device-list</c>
    /// property — an mpv NODE array of {name, description} maps. The fiddly node
    /// marshalling lives here, apart from Form1. Each entry's <see cref="Device.Name"/>
    /// is the identifier to hand back to the <c>audio-device</c> property; the
    /// <see cref="Device.Description"/> is the human-readable name for the UI. The
    /// list always starts with mpv's own "auto" entry (system default).
    /// </summary>
    public static class MpvAudioDevices
    {
        private const int MPV_FORMAT_STRING = 1;
        private const int MPV_FORMAT_NODE = 6;
        private const int MPV_FORMAT_NODE_ARRAY = 7;
        private const int MPV_FORMAT_NODE_MAP = 8;

        // On x64: mpv_node is a 16-byte struct (8-byte union + 4-byte format + pad);
        // mpv_node_list is { int num; mpv_node* values@8; char** keys@16 }.
        private const int NODE_SIZE = 16;

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(IntPtr ctx, string name, int format, IntPtr data);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_free_node_contents(IntPtr node);

        public struct Device
        {
            public string Name;         // audio-device identifier ("auto", "wasapi/…")
            public string Description;   // human-readable label
        }

        /// <summary>Reads the device list from a live mpv context. Never throws;
        /// returns an empty list on any failure.</summary>
        public static List<Device> Get(IntPtr ctx)
        {
            var result = new List<Device>();
            if (ctx == IntPtr.Zero) return result;

            IntPtr nodePtr = Marshal.AllocHGlobal(NODE_SIZE);
            try
            {
                Marshal.WriteInt64(nodePtr, 0, 0);
                Marshal.WriteInt64(nodePtr, 8, 0);
                if (mpv_get_property(ctx, "audio-device-list", MPV_FORMAT_NODE, nodePtr) < 0)
                    return result;
                try
                {
                    if (Marshal.ReadInt32(nodePtr, 8) != MPV_FORMAT_NODE_ARRAY) return result;
                    IntPtr list = Marshal.ReadIntPtr(nodePtr, 0);
                    if (list == IntPtr.Zero) return result;
                    int num = Marshal.ReadInt32(list, 0);
                    IntPtr values = Marshal.ReadIntPtr(list, 8);
                    for (int i = 0; i < num; i++)
                    {
                        Device d = ReadDeviceMap(IntPtr.Add(values, i * NODE_SIZE));
                        if (d.Name != null) result.Add(d);
                    }
                }
                finally { mpv_free_node_contents(nodePtr); }
            }
            catch { }
            finally { Marshal.FreeHGlobal(nodePtr); }
            return result;
        }

        private static Device ReadDeviceMap(IntPtr elem)
        {
            var d = new Device();
            if (Marshal.ReadInt32(elem, 8) != MPV_FORMAT_NODE_MAP) return d;
            IntPtr map = Marshal.ReadIntPtr(elem, 0);
            if (map == IntPtr.Zero) return d;
            int num = Marshal.ReadInt32(map, 0);
            IntPtr values = Marshal.ReadIntPtr(map, 8);
            IntPtr keys = Marshal.ReadIntPtr(map, 16);
            for (int j = 0; j < num; j++)
            {
                string key = ReadUtf8(Marshal.ReadIntPtr(keys, j * IntPtr.Size));
                IntPtr valNode = IntPtr.Add(values, j * NODE_SIZE);
                if (Marshal.ReadInt32(valNode, 8) != MPV_FORMAT_STRING) continue;
                string val = ReadUtf8(Marshal.ReadIntPtr(valNode, 0));
                if (key == "name") d.Name = val;
                else if (key == "description") d.Description = val;
            }
            return d;
        }

        private static string ReadUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return "";
            byte[] buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }
}
