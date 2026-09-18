using System.Management;
using JuiceMeter.Core;

namespace JuiceMeter.Sensors;

/// <summary>
/// Panel backlight level for the internal display.
///
/// On a 16 inch laptop the backlight swings by roughly 6-8 W between minimum and
/// maximum, which is a large slice of idle draw. Bucketing the learned baseline
/// by brightness stops a dim battery session from teaching the model a baseline
/// that is far too low for a bright desk session on AC.
///
/// This is the one reading that still goes through WMI, because the internal
/// panel does not answer the physical-monitor DDC/CI calls. It is polled rarely,
/// so the cost does not matter.
/// </summary>
internal static class BrightnessReader
{
    private static DateTime _nextAttempt = DateTime.MinValue;
    private static bool _supported = true;

    /// <summary>Returns 0-100, or -1 when the panel does not report brightness.</summary>
    public static int TryRead()
    {
        if (!_supported || DateTime.UtcNow < _nextAttempt) return -1;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");

            using var results = searcher.Get();
            foreach (var item in results)
            {
                using var mo = (ManagementObject)item;
                var value = mo["CurrentBrightness"];
                if (value is null) continue;
                return Math.Clamp(Convert.ToInt32(value), 0, 100);
            }

            // Query worked but returned nothing: an external-only setup, say.
            _nextAttempt = DateTime.UtcNow.AddMinutes(5);
            return -1;
        }
        catch (ManagementException)
        {
            // Desktops and some docked setups do not implement the class at all.
            _supported = false;
            Log.Info("Panel brightness is not reported on this machine; baseline will not be brightness-bucketed");
            return -1;
        }
        catch (Exception ex)
        {
            _nextAttempt = DateTime.UtcNow.AddMinutes(1);
            Log.Error("Brightness read failed", ex);
            return -1;
        }
    }
}
