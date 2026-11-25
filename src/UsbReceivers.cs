using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Batery
{
    internal static class UsbReceivers
    {
        private const string UsbConnection = "Receptor USB · 2,4 GHz";
        private const string FuxiHardwareId = @"USB\VID_040B&PID_0897\";

        private static readonly Dictionary<string, string> LogitechReceivers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "C52B", "Logitech Unifying" }, { "C532", "Logitech Unifying" },
                { "C52F", "Receptor Logitech Nano" }, { "C534", "Receptor Logitech Nano" },
                { "C547", "Logi Bolt" }, { "C548", "Logi Bolt" },
                { "C539", "Logitech Lightspeed" }, { "C53A", "Logitech Lightspeed" },
                { "C53D", "Logitech Lightspeed" }, { "C541", "Logitech Lightspeed" }
            };
        private static readonly Dictionary<string, string> ArctisReceivers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "2202", "Arctis Nova 7" }, { "2206", "Arctis Nova 7X" },
                { "220A", "Arctis Nova 7P" }, { "227E", "Arctis Nova 7 Gen 2" },
                { "2298", "Arctis Nova 7P Gen 2" }, { "229E", "Arctis Nova 7X Gen 2" }
            };

        public static List<Device> Read()
        {
            var results = new List<Device>();
            var usbPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in NativeDevices.UsbHidPaths())
            {
                string parent = NativeDevices.UsbParent(path);
                if (parent == null)
                    continue;
                List<string> paths;
                if (!usbPaths.TryGetValue(parent, out paths))
                {
                    paths = new List<string>();
                    usbPaths.Add(parent, paths);
                }
                paths.Add(path);
            }

            foreach (string id in NativeDevices.PresentUsbDevices())
            {
                string logitech = ReceiverName(id, "046D", LogitechReceivers);
                string arctis = ReceiverName(id, "1038", ArctisReceivers);
                bool fuxiReceiver = id.StartsWith(FuxiHardwareId, StringComparison.OrdinalIgnoreCase);
                List<string> paths;
                bool knownReceiver = fuxiReceiver || logitech != null || arctis != null;
                if (!usbPaths.TryGetValue(id, out paths) && !knownReceiver)
                    continue;

                var channels = new List<Channel>();
                foreach (string path in paths ?? Enumerable.Empty<string>())
                {
                    UsbHid.Caps caps;
                    bool reportsBattery;
                    var percent = UsbHid.Inspect(path, out caps, out reportsBattery);
                    channels.Add(new Channel { Path = path, Caps = caps, ReportsBattery = reportsBattery,
                        Percent = percent });
                }

                if (logitech != null)
                {
                    var slots = ReadLogitech(id, logitech, channels);
                    if (slots.Count != 0)
                    {
                        results.AddRange(slots);
                        continue;
                    }
                }

                if (arctis != null)
                {
                    Device headset = ReadArctis(id, arctis, channels);
                    if (headset != null)
                    {
                        results.Add(headset);
                        continue;
                    }
                }

                var standard = channels.FirstOrDefault(channel => channel.Percent.HasValue);
                if (standard == null && !knownReceiver && !channels.Any(channel => channel.ReportsBattery))
                    continue;

                string product = NativeDevices.UsbName(id);
                string name = logitech ?? arctis ?? (string.IsNullOrWhiteSpace(product)
                    ? (fuxiReceiver ? "Receptor de headset" : "Dispositivo USB") : product);
                string connection = knownReceiver ? UsbConnection : "USB · HID";
                var level = standard != null ? standard.Percent : NativeDevices.PublishedBattery(id);
                string detail = null;
                if (!level.HasValue && fuxiReceiver)
                    detail = "O receptor USB foi detectado. A carga está indisponível neste modo.";
                else if (!level.HasValue && knownReceiver)
                    detail = "Aguardando leitura de um dispositivo pelo receptor.";
                results.Add(new Device(id, name, connection, level, detail: detail));
            }
            return results;
        }

        private static string ReceiverName(string id, string vendor, Dictionary<string, string> models)
        {
            string prefix = @"USB\VID_" + vendor + "&PID_";
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                id.Length < prefix.Length + 5 || id[prefix.Length + 4] != '\\')
                return null;
            string name;
            models.TryGetValue(id.Substring(prefix.Length, 4), out name);
            return name;
        }

        private static Device ReadArctis(string id, string name, List<Channel> channels)
        {
            bool gen2 = name.EndsWith("Gen 2", StringComparison.Ordinal);
            foreach (var channel in channels.Where(item => item.Caps.UsagePage == 0xffc0 &&
                item.Caps.InputLength >= 8 && item.Caps.OutputLength >= 64))
            {
                using (var handle = UsbHid.Open(channel.Path, 0xc0000000, true))
                {
                    if (handle.IsInvalid)
                        continue;
                    // The Nova 7 USB status request is report 0, command 0xB0.
                    var request = new byte[channel.Caps.OutputLength];
                    request[1] = 0xb0;
                    var reply = UsbHid.Request(handle, channel.Caps.InputLength, request,
                        frame => frame[0] == 0xb0 || (frame[0] == 0 && frame[1] == 0xb0), 400);
                    if (reply == null)
                        continue;
                    int? level = ArctisLevel(reply, gen2);
                    if (level.HasValue)
                        return new Device(id, name, UsbConnection, gen2 ? level : null,
                            chargeBand: gen2 ? null : level);
                }
            }
            return null;
        }

        internal static int? ArctisLevel(byte[] reply, bool gen2)
        {
            if (reply.Length < 5)
                return null;
            int body = reply[0] == 0 ? 1 : 0;
            if (reply[body] != 0xb0 || reply[body + 1] != 3 ||
                reply[body + 3] < 1 || reply[body + 3] > 3)
                return null;
            byte level = reply[body + 2];
            if (gen2)
                return level <= 100 ? (int?)level : null;
            return level <= 4 ? (int?)Math.Min(level, (byte)3) : null;
        }

        private static List<Device> ReadLogitech(string id, string receiver, List<Channel> channels)
        {
            var results = new List<Device>();
            var channel = channels.FirstOrDefault(item => item.Caps.UsagePage == 0xff00 &&
                item.Caps.Usage == 2 && item.Caps.InputLength == 20 && item.Caps.OutputLength == 20);
            if (channel == null)
                return results;

            using (var handle = UsbHid.Open(channel.Path, 0xc0000000, true))
            {
                if (handle.IsInvalid)
                    return results;
                for (byte slot = 1; slot <= 6; slot++)
                {
                    var device = ReadLogitechSlot(handle, id, receiver, slot);
                    if (device != null)
                        results.Add(device);
                }
            }
            return results;
        }

        private static Device ReadLogitechSlot(SafeFileHandle handle, string id, string receiver, byte slot)
        {
            // HID++ 2.0 Root.GetFeature, Unified Battery and Battery Status.
            var unified = LogitechRequest(handle, slot, 0, 0, 0x10, 0x04);
            if (unified == null)
                return null;

            int? percent = null;
            int? band = null;
            if (unified[4] != 0)
            {
                var capabilities = LogitechRequest(handle, slot, unified[4], 0);
                var status = LogitechRequest(handle, slot, unified[4], 1);
                if (status != null && status[6] <= 4)
                {
                    if (capabilities != null && (capabilities[5] & 2) != 0 && status[4] <= 100)
                        percent = status[4];
                    else
                        band = LogitechBand(status[5]);
                }
            }
            if (!percent.HasValue && !band.HasValue)
            {
                var legacy = LogitechRequest(handle, slot, 0, 0, 0x10, 0x00);
                if (legacy != null && legacy[4] != 0)
                {
                    var status = LogitechRequest(handle, slot, legacy[4], 0);
                    if (status != null && status[4] > 0 && status[4] <= 100 && status[6] <= 6)
                        percent = status[4];
                }
            }
            if (!percent.HasValue && !band.HasValue)
                return null;

            string name = ReadLogitechName(handle, slot) ?? "Dispositivo Logitech " + slot;
            return new Device(id + ":" + slot, name, UsbConnection, percent, band, receiver);
        }

        private static string ReadLogitechName(SafeFileHandle handle, byte slot)
        {
            var feature = LogitechRequest(handle, slot, 0, 0, 0, 0x05);
            if (feature == null || feature[4] == 0)
                return null;
            var size = LogitechRequest(handle, slot, feature[4], 0);
            if (size == null || size[4] == 0 || size[4] > 80)
                return null;

            var name = new byte[size[4]];
            for (int offset = 0; offset < name.Length; offset += 16)
            {
                var fragment = LogitechRequest(handle, slot, feature[4], 1, (byte)offset);
                if (fragment == null)
                    return null;
                Array.Copy(fragment, 4, name, offset, Math.Min(16, name.Length - offset));
            }
            string value = Encoding.UTF8.GetString(name).TrimEnd('\0').Trim();
            return value.Length == 0 || value.Any(char.IsControl) ? null : value;
        }

        internal static int? LogitechBand(byte flags)
        {
            switch (flags)
            {
                case 1: return 0;
                case 2: return 1;
                case 4: return 2;
                case 8: return 3;
                default: return null;
            }
        }

        private static byte[] LogitechRequest(SafeFileHandle handle, byte slot, byte feature,
            byte function, byte first = 0, byte second = 0)
        {
            // The software ID is echoed in replies. Unsolicited notifications use zero.
            byte address = (byte)((function << 4) | 0x0b);
            var request = new byte[20];
            request[0] = 0x11;
            request[1] = slot;
            request[2] = feature;
            request[3] = address;
            request[4] = first;
            request[5] = second;
            var reply = UsbHid.Request(handle, 20, request, report =>
                report[0] == 0x11 && report[1] == slot &&
                ((report[2] == feature && report[3] == address) ||
                    (report[2] == 0xff && report[3] == feature && report[4] == address)), 260);
            return reply != null && reply[2] == 0xff ? null : reply;
        }

        private sealed class Channel
        {
            public string Path;
            public UsbHid.Caps Caps;
            public bool ReportsBattery;
            public int? Percent;
        }
    }
}
