using System.Runtime.InteropServices;
using System.Text;
using JuiceMeter.Core;

namespace JuiceMeter.Sensors;

/// <summary>
/// Tracks whether the discrete GPU is actually powered up and started.
///
/// G-Helper (and the ASUS software, and Windows itself) can disable the dGPU at
/// the device level -- that is what Eco mode does. NVML does not cope with the
/// device vanishing underneath it: the next nvmlDeviceGetPowerUsage on the stale
/// handle dereferences freed memory and raises an AccessViolationException,
/// which .NET 8 treats as a corrupted-state exception and cannot be caught. The
/// process simply dies.
///
/// So the only defence is to never make the call. Before every GPU read we ask
/// the configuration manager whether the devnode is still started, which is a
/// cheap native call against a device instance id we cached once. A disabled
/// device keeps its devnode with problem code 22, and a fully removed one stops
/// resolving altogether; both come back as "not started".
///
/// This cannot close the race completely -- nothing in user mode can, since the
/// device could go away microseconds after the check -- but it shrinks the
/// exposure from a full sample interval to the width of one function call.
/// </summary>
internal sealed class DiscreteGpu
{
    private const int RescanSeconds = 10;

    private string? _deviceInstanceId;
    private DateTime _nextScan = DateTime.MinValue;

    /// <summary>The PCI device instance id, once one has been seen.</summary>
    public string? DeviceInstanceId => _deviceInstanceId;

    /// <summary>True once an NVIDIA display adapter has been seen at least once.</summary>
    public bool EverSeen { get; private set; }

    /// <summary>
    /// True when an NVIDIA dGPU is present and started right now.
    ///
    /// On this class of ASUS laptop, Eco mode does not merely disable the
    /// device: the devnode disappears from the display class entirely. So a
    /// cached id that stops resolving is the normal signal that the GPU has
    /// gone, and finding nothing has to stay retryable rather than latching,
    /// or a GPU coming back would never be noticed.
    /// </summary>
    public bool IsRunning()
    {
        if (_deviceInstanceId is not null && Started(_deviceInstanceId)) return true;

        // Either we have never seen one, or the one we knew about has gone.
        // Re-enumerate occasionally; this is how a returning GPU is spotted.
        if (DateTime.UtcNow < _nextScan) return false;
        _nextScan = DateTime.UtcNow.AddSeconds(RescanSeconds);

        var found = FindDiscreteGpu();
        if (found is null) return false;

        if (!EverSeen || found != _deviceInstanceId)
        {
            Log.Info($"Discrete GPU devnode: {found}");
        }

        _deviceInstanceId = found;
        EverSeen = true;

        return Started(found);
    }

    private static bool Started(string deviceInstanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
        if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != CR_SUCCESS) return false;

        return (status & DN_STARTED) != 0 && problem == 0;
    }

    private static string? FindDiscreteGpu()
    {
        var guid = GUID_DEVCLASS_DISPLAY;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return null;

        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) return null;

                // VEN_10DE is NVIDIA. AMD discrete parts show up as VEN_1002,
                // but LibreHardwareMonitor talks to those through ADL, which
                // does not have the same stale-handle problem, so only the
                // NVIDIA path needs gating.
                var hardwareIds = GetMultiStringProperty(set, ref data, SPDRP_HARDWAREID);
                if (hardwareIds.Any(id => id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)))
                {
                    return GetDeviceInstanceId(set, ref data);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate display adapters", ex);
            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string[] GetMultiStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, IntPtr.Zero, 0, out var required);
        if (required == 0) return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, required, out _))
            {
                return Array.Empty<string>();
            }

            var bytes = new byte[required];
            Marshal.Copy(buffer, bytes, 0, (int)required);

            return Encoding.Unicode.GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    // ---------------------------------------------------------------- interop

    private static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const int DIGCF_PRESENT = 0x02;
    private const uint SPDRP_HARDWAREID = 0x00000001;

    private const int CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint DN_STARTED = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
