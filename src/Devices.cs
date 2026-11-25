using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Batery
{
    internal sealed class Device
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Connection;
        public readonly int? Percent;
        public readonly int? ChargeBand;
        public readonly string Detail;

        public Device(string id, string name, string connection, int? percent = null,
            int? chargeBand = null, string detail = null)
        {
            Id = id;
            Name = name;
            Connection = connection;
            Percent = percent;
            ChargeBand = chargeBand;
            Detail = detail;
        }

        public bool HasReading { get { return Percent.HasValue || ChargeBand.HasValue; } }
        public bool IsLow { get { return Percent <= 20 || ChargeBand <= 1; } }
        public bool CanRepeatAlert { get { return Percent >= 25 || ChargeBand >= 2; } }

        public string Level
        {
            get
            {
                if (Percent.HasValue)
                    return Percent.Value + "%";
                if (ChargeBand.HasValue)
                    return new[] { "Crítica", "Baixa", "Média", "Alta" }[ChargeBand.Value];
                return "Indisponível";
            }
        }
    }

    internal sealed class DeviceScan
    {
        public readonly List<Device> Devices = new List<Device>();
        public bool BluetoothFailed;
    }

    internal static class Scanner
    {
        private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
        private const string ContainerProperty = "System.Devices.Aep.ContainerId";
        private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
        private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private static readonly string[] BluetoothProperties =
            { AddressProperty, ContainerProperty, ConnectedProperty, BatteryProperty };

        public static async Task<DeviceScan> ReadAsync()
        {
            var scan = new DeviceScan();
            var classicTask = ConnectedAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected));
            var leTask = ConnectedAsync(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected));
            var receivers = Task.Run(() => UsbReceivers.Read());
            var controllers = NativeDevices.Controllers().ToList();
            var candidates = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
            var knownLevels = NativeDevices.BluetoothLevels();
            Collect(candidates, await classicTask.ConfigureAwait(false), false, knownLevels, scan);
            Collect(candidates, await leTask.ConfigureAwait(false), true, knownLevels, scan);

            var bluetooth = await Task.WhenAll(candidates.Values.Select(ReadEndpointAsync))
                .ConfigureAwait(false);
            scan.Devices.AddRange(bluetooth);
            scan.Devices.AddRange(await receivers.ConfigureAwait(false));

            scan.Devices.AddRange(controllers);
            return scan;
        }

        private static async Task<DeviceInformationCollection> ConnectedAsync(string selector)
        {
            try
            {
                // The selector also disables active inquiry. Require a live connection
                // explicitly so Windows' cached endpoints don't appear after disconnecting.
                string connected = "(" + selector + ") AND " + ConnectedProperty +
                    ":=System.StructuredQueryType.Boolean#True";
                return await AwaitOperation(DeviceInformation.FindAllAsync(connected,
                    BluetoothProperties, DeviceInformationKind.AssociationEndpoint), 4500)
                    .ConfigureAwait(false);
            }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (OperationCanceledException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static void Collect(Dictionary<string, Endpoint> candidates,
            DeviceInformationCollection collection, bool isLe, NativeDevices.Levels knownLevels,
            DeviceScan scan)
        {
            if (collection == null)
            {
                scan.BluetoothFailed = true;
                return;
            }

            foreach (var info in collection)
            {
                object connected = Property(info, ConnectedProperty);
                if (!(connected is bool) || !(bool)connected)
                    continue;

                string address = NativeDevices.NormalizeAddress(Property(info, AddressProperty) as string);
                if (address == null)
                    address = NativeDevices.AddressFromEndpoint(info.Id);
                object containerValue = Property(info, ContainerProperty);
                Guid container = Guid.Empty;
                if (containerValue != null)
                    Guid.TryParse(containerValue.ToString(), out container);
                string key = container != Guid.Empty ? container.ToString() : address ?? info.Id;

                Endpoint endpoint;
                if (!candidates.TryGetValue(key, out endpoint))
                {
                    endpoint = new Endpoint { Key = key, Name = "Dispositivo Bluetooth" };
                    candidates.Add(key, endpoint);
                }
                if (!string.IsNullOrWhiteSpace(info.Name) && (!isLe || !endpoint.Classic))
                    endpoint.Name = info.Name.Trim();
                if (isLe)
                    endpoint.LeId = info.Id;
                else
                    endpoint.Classic = true;

                object battery = Property(info, BatteryProperty);
                if (battery is byte && (byte)battery <= 100)
                    endpoint.Percent = (byte)battery;
                else if (!endpoint.Percent.HasValue)
                    endpoint.Percent = knownLevels.Find(address, container);
            }
        }

        private static object Property(DeviceInformation info, string key)
        {
            object value;
            return info.Properties.TryGetValue(key, out value) ? value : null;
        }

        private static async Task<Device> ReadEndpointAsync(Endpoint endpoint)
        {
            int? level = endpoint.Percent;
            if (!level.HasValue && endpoint.LeId != null)
                level = await ReadGattAsync(endpoint.LeId).ConfigureAwait(false);
            return new Device(endpoint.Key, endpoint.Name,
                endpoint.Classic ? "Bluetooth" : "Bluetooth LE", level);
        }

        private static async Task<int?> ReadGattAsync(string id)
        {
            try
            {
                using (var device = await AwaitOperation(BluetoothLEDevice.FromIdAsync(id), 3500)
                    .ConfigureAwait(false))
                {
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                        return null;
                    var services = await AwaitOperation(device.GetGattServicesForUuidAsync(
                        GattServiceUuids.Battery, BluetoothCacheMode.Cached), 3500).ConfigureAwait(false);
                    if (services.Status != GattCommunicationStatus.Success)
                        return null;
                    try
                    {
                        foreach (var service in services.Services)
                        {
                            var characteristics = await AwaitOperation(
                                service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                                    BluetoothCacheMode.Cached), 3000).ConfigureAwait(false);
                            if (characteristics.Status != GattCommunicationStatus.Success)
                                continue;
                            foreach (var characteristic in characteristics.Characteristics)
                            {
                                var result = await AwaitOperation(characteristic.ReadValueAsync(
                                    BluetoothCacheMode.Uncached), 3000).ConfigureAwait(false);
                                if (result.Status != GattCommunicationStatus.Success ||
                                    result.Value == null || result.Value.Length != 1)
                                    continue;
                                using (var reader = DataReader.FromBuffer(result.Value))
                                {
                                    byte value = reader.ReadByte();
                                    if (value <= 100)
                                        return value;
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var service in services.Services)
                            service.Dispose();
                    }
                }
            }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            catch (OperationCanceledException) { }
            catch (ArgumentException) { }
            return null;
        }

        private static async Task<T> AwaitOperation<T>(IAsyncOperation<T> operation, int timeout)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (completed, status) =>
            {
                try
                {
                    result.TrySetResult(completed.GetResults());
                }
                catch (Exception error)
                {
                    result.TrySetException(error);
                }
            };
            using (var cancellation = new CancellationTokenSource(timeout))
            using (cancellation.Token.Register(() => result.TrySetCanceled()))
            {
                try
                {
                    return await result.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { operation.Cancel(); }
                    catch (COMException) { }
                    throw;
                }
            }
        }

        private sealed class Endpoint
        {
            public string Key;
            public string Name;
            public string LeId;
            public bool Classic;
            public int? Percent;
        }
    }

    internal static partial class NativeDevices
    {
        private const uint FilterPresentEnumerator = 0x101;
        private const uint BufferSmall = 0x1a;
        private static readonly DeviceProperty Battery = new DeviceProperty(
            "104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
        private static readonly DeviceProperty Address = new DeviceProperty(
            "2bd67d8b-8beb-48d5-87e0-6cda3428040a", 1);
        private static readonly DeviceProperty Container = new DeviceProperty(
            "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        private static readonly Regex DeviceAddress = new Regex(
            @"\\DEV_([0-9a-f]{12})(?:\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex EndpointAddress = new Regex(
            @"(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal sealed class Levels
        {
            public readonly Dictionary<string, int> ByAddress =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<Guid, int> ByContainer = new Dictionary<Guid, int>();

            public int? Find(string address, Guid container)
            {
                int level;
                if (address != null && ByAddress.TryGetValue(address, out level))
                    return level;
                if (container != Guid.Empty && ByContainer.TryGetValue(container, out level))
                    return level;
                return null;
            }
        }

        public static Levels BluetoothLevels()
        {
            var levels = new Levels();
            var buffer = new byte[256];
            foreach (string id in DeviceIds("BTHLE").Concat(DeviceIds("BTHENUM")))
            {
                uint node;
                if (CM_Locate_DevNodeW(out node, id, 0) != 0)
                    continue;
                int size = Read(node, Battery, 3, buffer);
                if (size != 1 || buffer[0] > 100)
                    continue;
                int level = buffer[0];
                size = Read(node, Address, 0x12, buffer);
                string address = size > 0
                    ? NormalizeAddress(Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0'))
                    : null;
                if (address == null)
                {
                    var match = DeviceAddress.Match(id);
                    if (match.Success)
                        address = match.Groups[1].Value;
                }
                if (address != null && !levels.ByAddress.ContainsKey(address))
                    levels.ByAddress.Add(address, level);

                size = Read(node, Container, 0x0d, buffer);
                if (size == 16)
                {
                    var bytes = new byte[16];
                    Array.Copy(buffer, bytes, 16);
                    var container = new Guid(bytes);
                    if (container != Guid.Empty && !levels.ByContainer.ContainsKey(container))
                        levels.ByContainer.Add(container, level);
                }
            }
            return levels;
        }

        public static IEnumerable<Device> Controllers()
        {
            for (uint i = 0; i < 4; i++)
            {
                XInputBattery battery;
                if (XInputGetBatteryInformation(i, 0, out battery) != 0 ||
                    (battery.Type != 2 && battery.Type != 3) || battery.Level > 3)
                    continue;
                yield return new Device("xinput-" + i, "Controle " + (i + 1),
                    "Sem fio · XInput", chargeBand: battery.Level);
            }
        }

        internal static string NormalizeAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return null;
            string hex = address.Replace(":", "").Replace("-", "").Trim();
            return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? hex : null;
        }

        public static string AddressFromEndpoint(string id)
        {
            var match = EndpointAddress.Match(id);
            return match.Success ? NormalizeAddress(match.Value) : null;
        }

        private static string[] DeviceIds(string enumerator)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint length;
                if (CM_Get_Device_ID_List_SizeW(out length, enumerator, FilterPresentEnumerator) != 0 ||
                    length <= 1)
                    break;
                var chars = new char[length];
                uint status = CM_Get_Device_ID_ListW(enumerator, chars, length, FilterPresentEnumerator);
                if (status == 0)
                    return new string(chars).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (status != BufferSmall)
                    break;
            }
            return new string[0];
        }

        private static int Read(uint node, DeviceProperty property, uint expected, byte[] buffer)
        {
            uint size = (uint)buffer.Length;
            uint type;
            return CM_Get_DevNode_PropertyW(node, ref property, out type, buffer, ref size, 0) == 0 &&
                type == expected ? (int)size : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceProperty
        {
            public Guid Format;
            public uint Id;

            public DeviceProperty(string format, uint id)
            {
                Format = new Guid(format);
                Id = id;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputBattery
        {
            public byte Type;
            public byte Level;
        }

        [DllImport("xinput1_4.dll")]
        private static extern uint XInputGetBatteryInformation(uint slot, byte deviceType,
            out XInputBattery battery);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer,
            uint length, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);

        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_PropertyW(uint node, ref DeviceProperty property,
            out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    }
}
