using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Batery
{
    internal static partial class NativeDevices
    {
        private static readonly Guid HidInterface = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        private static readonly DeviceProperty BusDescription = new DeviceProperty(
            "540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);

        internal static IEnumerable<string> UsbHidPaths()
        {
            var guid = HidInterface;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint length;
                if (CM_Get_Device_Interface_List_SizeW(out length, ref guid, null, 0) != 0 || length <= 1)
                    break;
                var buffer = new char[length];
                uint status = CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0);
                if (status == 0)
                    return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (status != BufferSmall)
                    break;
            }
            return new string[0];
        }

        internal static string UsbParent(string path)
        {
            const string prefix = @"\\?\HID#VID_";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            int end = path.IndexOf("#{", StringComparison.Ordinal);
            if (end < prefix.Length)
                return null;
            string id = path.Substring(4, end - 4).Replace('#', '\\');
            uint node;
            if (CM_Locate_DevNodeW(out node, id, 0) != 0)
                return null;

            var parentId = new StringBuilder(200);
            for (int depth = 0; depth < 8; depth++)
            {
                if (CM_Get_Parent(out node, node, 0) != 0 ||
                    CM_Get_Device_IDW(node, parentId, (uint)parentId.Capacity, 0) != 0)
                    return null;
                string parent = parentId.ToString();
                if (parent.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) &&
                    parent.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0)
                    return parent;
            }
            return null;
        }

        internal static IEnumerable<string> PresentUsbDevices() { return DeviceIds("USB"); }

        internal static string UsbName(string id)
        {
            uint node;
            if (CM_Locate_DevNodeW(out node, id, 0) != 0)
                return null;
            var buffer = new byte[512];
            int size = Read(node, BusDescription, 0x12, buffer);
            return size > 0 ? Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0').Trim() : null;
        }

        internal static int? PublishedBattery(string id)
        {
            uint node;
            var buffer = new byte[1];
            return CM_Locate_DevNodeW(out node, id, 0) == 0 &&
                Read(node, Battery, 3, buffer) == 1 && buffer[0] <= 100 ? (int?)buffer[0] : null;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length,
            ref Guid guid, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId,
            [Out] char[] buffer, uint length, uint flags);

        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, uint length, uint flags);
    }

    internal static class UsbHid
    {
        private const int HidSuccess = 0x00110000;
        private const ushort DeviceControlsPage = 0x06;
        private const ushort BatteryStrength = 0x20;
        private const int InputReport = 0;
        private const int FeatureReport = 2;

        internal static int? Inspect(string path, out Caps caps, out bool reportsBattery)
        {
            caps = new Caps();
            reportsBattery = false;
            using (var handle = Open(path, 0, false))
            {
                IntPtr data;
                if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out data))
                    return null;
                try
                {
                    if (HidP_GetCaps(data, out caps) != HidSuccess)
                        return null;
                    return ReadBattery(handle, data, FeatureReport, caps.FeatureLength,
                        caps.FeatureValues, ref reportsBattery) ??
                        ReadBattery(handle, data, InputReport, caps.InputLength,
                            caps.InputValues, ref reportsBattery);
                }
                finally
                {
                    HidD_FreePreparsedData(data);
                }
            }
        }

        private static int? ReadBattery(SafeFileHandle handle, IntPtr data, int type, ushort length,
            ushort count, ref bool reportsBattery)
        {
            if (length == 0 || count == 0)
                return null;
            var values = new ValueCaps[count];
            if (HidP_GetSpecificValueCaps(type, DeviceControlsPage, 0, BatteryStrength, values,
                ref count, data) != HidSuccess)
                return null;
            reportsBattery = count > 0 || reportsBattery;
            for (int i = 0; i < count; i++)
            {
                var value = values[i];
                if (value.ReportCount != 1 || value.IsAbsolute == 0)
                    continue;
                var report = new byte[length];
                report[0] = value.ReportId;
                bool read = type == FeatureReport ? HidD_GetFeature(handle, report, (uint)length) :
                    HidD_GetInputReport(handle, report, (uint)length);
                uint raw;
                if (!read || HidP_GetUsageValue(type, DeviceControlsPage, value.LinkCollection,
                    BatteryStrength, out raw, data, report, (uint)length) != HidSuccess)
                    continue;

                int? percent = NormalizeBattery(raw, value.BitSize, value.Minimum, value.Maximum);
                if (percent.HasValue)
                    return percent;
            }
            return null;
        }

        internal static int? NormalizeBattery(uint raw, int bits, int minimum, int maximum)
        {
            if (bits < 1 || bits > 32 || maximum <= minimum)
                return null;
            long number = raw;
            if (minimum < 0 && (number & (1L << (bits - 1))) != 0)
                number -= 1L << bits;
            if (number < minimum || number > maximum)
                return null;
            return (int)Math.Round(100.0 * (number - minimum) / ((long)maximum - minimum),
                MidpointRounding.AwayFromZero);
        }

        internal static SafeFileHandle Open(string path, uint access, bool overlapped)
        {
            return CreateFileW(path, access, 3, IntPtr.Zero, 3,
                overlapped ? 0x40000000u : 0, IntPtr.Zero);
        }

        internal static byte[] Request(SafeFileHandle handle, int inputLength, byte[] request,
            Func<byte[], bool> matches, int timeout)
        {
            var clock = Stopwatch.StartNew();
            if (Transfer(handle, request, true, timeout) != request.Length)
                return null;

            var report = new byte[inputLength];
            for (int remaining = timeout - (int)clock.ElapsedMilliseconds;
                remaining > 0; remaining = timeout - (int)clock.ElapsedMilliseconds)
            {
                if (Transfer(handle, report, false, remaining) != inputLength)
                    return null;
                if (matches(report))
                    return report;
            }
            return null;
        }

        private static int Transfer(SafeFileHandle handle, byte[] buffer, bool write, int timeout)
        {
            using (var completed = new EventWaitHandle(false, EventResetMode.ManualReset))
            {
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                var operation = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
                try
                {
                    Marshal.StructureToPtr(new Overlapped
                    {
                        Event = completed.SafeWaitHandle.DangerousGetHandle()
                    }, operation, false);
                    IntPtr data = pinned.AddrOfPinnedObject();
                    bool started = write ? WriteFile(handle, data, buffer.Length, IntPtr.Zero, operation) :
                        ReadFile(handle, data, buffer.Length, IntPtr.Zero, operation);
                    int length;
                    if (started)
                        return GetOverlappedResult(handle, operation, out length, false) ? length : 0;
                    if (Marshal.GetLastWin32Error() != 997)
                        return 0;
                    if (completed.WaitOne(timeout))
                        return GetOverlappedResult(handle, operation, out length, false) ? length : 0;

                    // A cancelled I/O still owns the buffer and OVERLAPPED until Windows finishes it.
                    CancelIoEx(handle, operation);
                    GetOverlappedResult(handle, operation, out length, true);
                    return 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(operation);
                    pinned.Free();
                }
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct Caps
        {
            [FieldOffset(0)] public ushort Usage;
            [FieldOffset(2)] public ushort UsagePage;
            [FieldOffset(4)] public ushort InputLength;
            [FieldOffset(6)] public ushort OutputLength;
            [FieldOffset(8)] public ushort FeatureLength;
            [FieldOffset(48)] public ushort InputValues;
            [FieldOffset(60)] public ushort FeatureValues;
        }

        [StructLayout(LayoutKind.Explicit, Size = 72)]
        private struct ValueCaps
        {
            [FieldOffset(2)] public byte ReportId;
            [FieldOffset(6)] public ushort LinkCollection;
            [FieldOffset(15)] public byte IsAbsolute;
            [FieldOffset(18)] public ushort BitSize;
            [FieldOffset(20)] public ushort ReportCount;
            [FieldOffset(40)] public int Minimum;
            [FieldOffset(44)] public int Maximum;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Overlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr Event;
        }

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr data, out Caps caps);

        [DllImport("hid.dll")]
        private static extern int HidP_GetSpecificValueCaps(int type, ushort usagePage, ushort link,
            ushort usage, [Out] ValueCaps[] values, ref ushort count, IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(int type, ushort usagePage, ushort link, ushort usage,
            out uint value, IntPtr data, byte[] report, uint length);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, uint length);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetInputReport(SafeFileHandle handle, [In, Out] byte[] report, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int length,
            IntPtr transferred, IntPtr operation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, int length,
            IntPtr transferred, IntPtr operation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr operation,
            out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr operation);
    }
}
