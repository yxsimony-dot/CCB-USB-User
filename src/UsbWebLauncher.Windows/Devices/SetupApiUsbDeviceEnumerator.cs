using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Windows.Devices;

public sealed class SetupApiUsbDeviceEnumerator : IWindowsUsbDeviceEnumerator
{
    public Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Enumerate(cancellationToken), cancellationToken);

    private static IReadOnlyList<UsbDeviceDescriptor> Enumerate(
        CancellationToken cancellationToken)
    {
        var records = EnumerateDeviceRecords(cancellationToken);
        var volumes = EnumerateVolumes();
        return WindowsDeviceSnapshotMapper.Map(records, volumes);
    }

    private static IReadOnlyList<WindowsDeviceRecord> EnumerateDeviceRecords(
        CancellationToken cancellationToken)
    {
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevsW(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfAllClasses);
        if (deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var records = new List<WindowsDeviceRecord>();
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceInfo = NativeMethods.CreateDeviceInfoData();
                if (!NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                var hardwareIds = ReadRegistryMultiString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.SpdrpHardwareId);
                var busDescription = ReadDevicePropertyString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.DeviceBusReportedDescription);
                var friendlyName = ReadRegistryString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.SpdrpFriendlyName) ??
                    ReadRegistryString(
                        deviceInfoSet,
                        ref deviceInfo,
                        NativeMethods.SpdrpDeviceDescription);
                var deviceClass = ReadRegistryString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.SpdrpClass);
                var parentInstanceId = ReadDevicePropertyString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.DeviceParent);
                var service = ReadRegistryString(
                    deviceInfoSet,
                    ref deviceInfo,
                    NativeMethods.SpdrpService);
                records.Add(new(
                    instanceId,
                    hardwareIds,
                    busDescription,
                    friendlyName,
                    deviceClass,
                    parentInstanceId,
                    service));
            }

            return records;
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static IReadOnlyList<WindowsVolumeRecord> EnumerateVolumes()
    {
        var volumes = new List<WindowsVolumeRecord>();
        var cdRomDeviceIds = EnumerateCdRomDeviceIdsByDrive();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.CDRom)
                {
                    volumes.Add(new(
                        drive.VolumeLabel,
                        drive.DriveFormat,
                        cdRomDeviceIds.GetValueOrDefault(NormalizeDriveName(drive.Name))));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return volumes;
    }

    private static IReadOnlyDictionary<string, string> EnumerateCdRomDeviceIdsByDrive()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Drive, PNPDeviceID FROM Win32_CDROMDrive");
            using var devices = searcher.Get();
            foreach (ManagementBaseObject device in devices)
            {
                var drive = device["Drive"] as string;
                var instanceId = device["PNPDeviceID"] as string;
                if (!string.IsNullOrWhiteSpace(drive) &&
                    !string.IsNullOrWhiteSpace(instanceId))
                {
                    result[NormalizeDriveName(drive)] = instanceId;
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static string NormalizeDriveName(string driveName) =>
        driveName.TrimEnd(Path.DirectorySeparatorChar);

    private static string ReadInstanceId(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo)
    {
        var buffer = new StringBuilder(1024);
        if (!NativeMethods.SetupDiGetDeviceInstanceIdW(
                deviceInfoSet,
                ref deviceInfo,
                buffer,
                buffer.Capacity,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer.ToString();
    }

    private static string[] ReadRegistryMultiString(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo,
        uint property)
    {
        var bytes = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, property);
        if (bytes.Length == 0)
        {
            return [];
        }

        return Encoding.Unicode
            .GetString(bytes)
            .TrimEnd('\0')
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? ReadRegistryString(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo,
        uint property)
    {
        var bytes = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, property);
        return bytes.Length == 0
            ? null
            : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static byte[] ReadRegistryProperty(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo,
        uint property)
    {
        var buffer = new byte[4096];
        if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out var requiredSize))
        {
            return [];
        }

        return buffer[..checked((int)requiredSize)];
    }

    private static string? ReadDevicePropertyString(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo,
        NativeMethods.DevPropKey property)
    {
        var buffer = new byte[4096];
        if (!NativeMethods.SetupDiGetDevicePropertyW(
                deviceInfoSet,
                ref deviceInfo,
                ref property,
                out _,
                buffer,
                (uint)buffer.Length,
                out var requiredSize,
                0))
        {
            return null;
        }

        return Encoding.Unicode
            .GetString(buffer, 0, checked((int)requiredSize))
            .TrimEnd('\0');
    }

    private static class NativeMethods
    {
        public static readonly IntPtr InvalidHandleValue = new(-1);
        public static readonly DevPropKey DeviceBusReportedDescription = new(
            new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
            4);
        public static readonly DevPropKey DeviceParent = new(
            new("4340A6C5-93FA-4706-972C-7B648008A5A7"),
            8);

        public const uint DigcfPresent = 0x00000002;
        public const uint DigcfAllClasses = 0x00000004;
        public const int ErrorNoMoreItems = 259;
        public const uint SpdrpDeviceDescription = 0x00000000;
        public const uint SpdrpHardwareId = 0x00000001;
        public const uint SpdrpService = 0x00000004;
        public const uint SpdrpClass = 0x00000007;
        public const uint SpdrpFriendlyName = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDevInfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DeviceInstance;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DevPropKey(Guid formatId, uint propertyId)
        {
            public Guid FormatId = formatId;
            public uint PropertyId = propertyId;
        }

        public static SpDevInfoData CreateDeviceInfoData() => new()
        {
            Size = (uint)Marshal.SizeOf<SpDevInfoData>()
        };

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid,
            string? enumerator,
            IntPtr parentWindow,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SpDevInfoData deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr deviceInfoSet,
            ref SpDevInfoData deviceInfoData,
            StringBuilder deviceInstanceId,
            int deviceInstanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr deviceInfoSet,
            ref SpDevInfoData deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            [Out] byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceInfoSet,
            ref SpDevInfoData deviceInfoData,
            ref DevPropKey propertyKey,
            out uint propertyType,
            [Out] byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    }
}
