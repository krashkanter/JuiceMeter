using System.Runtime.InteropServices;
using System.Text;
using JuiceMeter.Core;
using Microsoft.Win32.SafeHandles;

namespace JuiceMeter.Sensors;

/// <summary>
/// Talks to the battery driver directly through DeviceIoControl rather than WMI.
///
/// The smart battery reports its own charge and discharge power in milliwatts,
/// measured by the fuel gauge across a sense resistor. That makes it the single
/// most accurate power number available anywhere on a laptop, which is exactly
/// why Juice Meter is built around it. Going through the IOCTL interface instead
/// of WMI matters here: polling ROOT\WMI once a second spins up WmiPrvSE and
/// burns a couple of watts of its own, which would corrupt the very measurement
/// we are taking.
/// </summary>
internal sealed class BatteryReader : IDisposable
{
    public sealed class Reading
    {
        public bool Present;
        public bool AcOnline;
        public bool Charging;
        public bool Discharging;
        public bool Critical;
        public bool RateKnown;

        /// <summary>Signed pack power in watts: positive into the pack, negative out of it.</summary>
        public double Watts;

        public double Volts;
        public double RemainingWh;
        public double FullChargeWh;
        public double DesignWh;
        public uint CycleCount;
        public double Percent;
        public TimeSpan? Runtime;

        /// <summary>Firmware reports unitless capacity, so watt-hours are not available.</summary>
        public bool RelativeCapacity;
    }

    private sealed class Pack : IDisposable
    {
        public required string Path { get; init; }
        public required SafeFileHandle Handle { get; init; }
        public uint Tag;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint CycleCount;
        public bool Relative;
        public string? Name;
        public string? Manufacturer;
        public string? Chemistry;

        public void Dispose() => Handle.Dispose();
    }

    private readonly List<Pack> _packs = new();
    private bool _enumerated;
    private int _consecutiveFailures;

    public string? DeviceName { get; private set; }
    public string? Manufacturer { get; private set; }
    public string? Chemistry { get; private set; }
    public int PackCount => _packs.Count;

    public Reading Read()
    {
        EnsureEnumerated();

        var reading = new Reading();

        // GetSystemPowerStatus is the authority on whether the brick is plugged
        // in, and it works even on a machine with no battery at all.
        if (GetSystemPowerStatus(out var sps))
        {
            reading.AcOnline = sps.ACLineStatus == 1;
            if (sps.BatteryLifePercent <= 100) reading.Percent = sps.BatteryLifePercent;
            if (sps.BatteryLifeTime != 0xFFFFFFFF) reading.Runtime = TimeSpan.FromSeconds(sps.BatteryLifeTime);
        }

        double totalMilliwatts = 0;
        double voltageSum = 0;
        int voltageCount = 0;
        uint remaining = 0, full = 0, design = 0, cycles = 0;
        var anyRate = false;
        var anyPack = false;

        foreach (var pack in _packs)
        {
            if (!TryReadStatus(pack, out var status)) continue;
            anyPack = true;

            if ((status.PowerState & BATTERY_POWER_ON_LINE) != 0) reading.AcOnline = true;
            if ((status.PowerState & BATTERY_DISCHARGING) != 0) reading.Discharging = true;
            if ((status.PowerState & BATTERY_CHARGING) != 0) reading.Charging = true;
            if ((status.PowerState & BATTERY_CRITICAL) != 0) reading.Critical = true;

            if (status.Rate != BATTERY_UNKNOWN_RATE)
            {
                totalMilliwatts += status.Rate;
                anyRate = true;
            }

            if (status.Voltage != BATTERY_UNKNOWN_VOLTAGE)
            {
                voltageSum += status.Voltage;
                voltageCount++;
            }

            if (status.Capacity != BATTERY_UNKNOWN_CAPACITY) remaining += status.Capacity;
            if (pack.FullChargedCapacity != BATTERY_UNKNOWN_CAPACITY) full += pack.FullChargedCapacity;
            if (pack.DesignedCapacity != BATTERY_UNKNOWN_CAPACITY) design += pack.DesignedCapacity;
            cycles = Math.Max(cycles, pack.CycleCount);

            if (pack.Relative) reading.RelativeCapacity = true;

            if (reading.Runtime is null && TryQueryUInt(pack, BatteryQueryLevel.EstimatedTime, out var seconds)
                && seconds != 0xFFFFFFFF)
            {
                reading.Runtime = TimeSpan.FromSeconds(seconds);
            }
        }

        reading.Present = anyPack;
        reading.RateKnown = anyRate;

        if (!reading.RelativeCapacity)
        {
            reading.Watts = totalMilliwatts / 1000.0;
            reading.RemainingWh = remaining / 1000.0;
            reading.FullChargeWh = full / 1000.0;
            reading.DesignWh = design / 1000.0;
        }

        reading.CycleCount = cycles;
        if (voltageCount > 0) reading.Volts = voltageSum / voltageCount / 1000.0;
        if (full > 0 && remaining > 0) reading.Percent = Math.Clamp(remaining * 100.0 / full, 0, 100);

        // Some firmwares flag neither state when sitting at 100% on AC.
        if (reading.Charging && reading.Discharging) reading.Discharging = false;

        if (anyPack)
        {
            _consecutiveFailures = 0;
        }
        else if (_packs.Count > 0 && ++_consecutiveFailures >= 5)
        {
            // Tags go stale when a pack is hot-swapped or the driver reloads.
            Log.Warn("Battery reads kept failing, re-enumerating the device interfaces");
            Reset();
        }

        return reading;
    }

    private void EnsureEnumerated()
    {
        if (_enumerated) return;
        _enumerated = true;

        foreach (var path in EnumerateBatteryPaths())
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    continue;
                }

                var pack = new Pack { Path = path, Handle = handle };
                if (!TryRefreshTag(pack) || !TryLoadStaticInfo(pack))
                {
                    pack.Dispose();
                    continue;
                }

                _packs.Add(pack);
                handle = null;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not open battery device {path}", ex);
                handle?.Dispose();
            }
        }

        var first = _packs.FirstOrDefault();
        DeviceName = first?.Name;
        Manufacturer = first?.Manufacturer;
        Chemistry = first?.Chemistry;

        Log.Info(_packs.Count == 0
            ? "No system battery found; Juice Meter will run in AC-estimate-only mode"
            : $"Battery: {Manufacturer} {DeviceName} ({Chemistry}), {_packs.Count} pack(s)");
    }

    private void Reset()
    {
        foreach (var pack in _packs) pack.Dispose();
        _packs.Clear();
        _enumerated = false;
        _consecutiveFailures = 0;
    }

    private static IEnumerable<string> EnumerateBatteryPaths()
    {
        var guid = GUID_DEVICE_BATTERY;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) yield break;

        try
        {
            for (var index = 0; ; index++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref did)) yield break;

                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0) continue;

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (!string.IsNullOrEmpty(path)) yield return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool TryRefreshTag(Pack pack)
    {
        uint timeout = 0;
        if (!DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_TAG, ref timeout, sizeof(uint),
                out uint tag, sizeof(uint), out _, IntPtr.Zero))
        {
            return false;
        }

        pack.Tag = tag;
        return tag != BATTERY_TAG_INVALID;
    }

    private static bool TryLoadStaticInfo(Pack pack)
    {
        var query = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = pack.Tag,
            InformationLevel = (int)BatteryQueryLevel.Information,
            AtRate = 0,
        };

        var buffer = new byte[36];
        if (!DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_INFORMATION, ref query,
                Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < 36)
        {
            return false;
        }

        var capabilities = BitConverter.ToUInt32(buffer, 0);

        // A relative-capacity pack reports unitless numbers, so watt-hours are meaningless.
        pack.Relative = (capabilities & BATTERY_CAPACITY_RELATIVE) != 0;

        // Ignore anything that is not the system battery (UPS units show up here too).
        if ((capabilities & BATTERY_SYSTEM_BATTERY) == 0) return false;

        pack.Chemistry = Encoding.ASCII.GetString(buffer, 8, 4).Trim('\0', ' ');
        pack.DesignedCapacity = BitConverter.ToUInt32(buffer, 12);
        pack.FullChargedCapacity = BitConverter.ToUInt32(buffer, 16);
        pack.CycleCount = BitConverter.ToUInt32(buffer, 32);

        pack.Name = TryQueryString(pack, BatteryQueryLevel.DeviceName);
        pack.Manufacturer = TryQueryString(pack, BatteryQueryLevel.ManufactureName);
        return true;
    }

    private static bool TryReadStatus(Pack pack, out BATTERY_STATUS status)
    {
        var wait = new BATTERY_WAIT_STATUS { BatteryTag = pack.Tag };

        if (DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_STATUS, ref wait,
                Marshal.SizeOf<BATTERY_WAIT_STATUS>(), out status, Marshal.SizeOf<BATTERY_STATUS>(),
                out _, IntPtr.Zero))
        {
            return true;
        }

        // A stale tag is the usual cause; re-fetch it and try once more.
        if (TryRefreshTag(pack))
        {
            wait.BatteryTag = pack.Tag;
            TryLoadStaticInfo(pack);
            return DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_STATUS, ref wait,
                Marshal.SizeOf<BATTERY_WAIT_STATUS>(), out status, Marshal.SizeOf<BATTERY_STATUS>(),
                out _, IntPtr.Zero);
        }

        status = default;
        return false;
    }

    private static bool TryQueryUInt(Pack pack, BatteryQueryLevel level, out uint value)
    {
        var query = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = pack.Tag,
            InformationLevel = (int)level,
            AtRate = 0,
        };

        return DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_INFORMATION, ref query,
            Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), out value, sizeof(uint), out _, IntPtr.Zero);
    }

    private static string? TryQueryString(Pack pack, BatteryQueryLevel level)
    {
        var query = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = pack.Tag,
            InformationLevel = (int)level,
            AtRate = 0,
        };

        var buffer = new byte[256];
        if (!DeviceIoControl(pack.Handle, IOCTL_BATTERY_QUERY_INFORMATION, ref query,
                Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned <= 0)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, Math.Min(returned, buffer.Length)).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void Dispose()
    {
        foreach (var pack in _packs) pack.Dispose();
        _packs.Clear();
    }

    // ---------------------------------------------------------------- interop

    private enum BatteryQueryLevel
    {
        Information = 0,
        GranularityInformation = 1,
        Temperature = 2,
        EstimatedTime = 3,
        DeviceName = 4,
        ManufactureDate = 5,
        ManufactureName = 6,
        UniqueId = 7,
        SerialNumber = 8,
    }

    private static readonly Guid GUID_DEVICE_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;

    // CTL_CODE(FILE_DEVICE_BATTERY, function, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    private const uint BATTERY_TAG_INVALID = 0;
    private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
    private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    private const uint BATTERY_POWER_ON_LINE = 0x00000001;
    private const uint BATTERY_DISCHARGING = 0x00000002;
    private const uint BATTERY_CHARGING = 0x00000004;
    private const uint BATTERY_CRITICAL = 0x00000008;

    private const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
    private const uint BATTERY_SYSTEM_BATTERY = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, int detailDataSize,
        out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        ref uint inBuffer, int inSize, out uint outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        ref BATTERY_QUERY_INFORMATION inBuffer, int inSize, out uint outBuffer, int outSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        ref BATTERY_QUERY_INFORMATION inBuffer, int inSize, [Out] byte[] outBuffer, int outSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        ref BATTERY_WAIT_STATUS inBuffer, int inSize, out BATTERY_STATUS outBuffer, int outSize,
        out int returned, IntPtr overlapped);
}
