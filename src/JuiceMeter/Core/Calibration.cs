using System.Text.Json.Serialization;

namespace JuiceMeter.Core;

/// <summary>
/// The trick that makes the AC numbers believable.
///
/// While the laptop runs on battery the pack reports true total system power.
/// Subtract the CPU and GPU package power we can read directly and what is left
/// over -- panel, board, RAM, SSD, fans, wifi -- is the baseline. We bank that
/// residual, bucketed by screen brightness because the panel is a big slice of
/// it, and then reuse it on AC where nothing measures total draw for us.
///
/// A median over a rolling window is used rather than a mean: residuals get
/// spiked by things the CPU/GPU sensors cannot see (a burst of SSD writes, a
/// phone charging off a USB port) and those should not drag the baseline up.
/// </summary>
public sealed class CalibrationModel
{
    public const int BucketCount = 11;      // brightness 0-9, 10-19, ... 100
    public const int WindowSize = 900;      // 15 minutes at 1 Hz

    private const int MinSamplesForBucket = 60;
    private const int MinSamplesGlobal = 120;
    private const double MinResidual = 0.5;
    private const double MaxResidual = 60.0;

    /// <summary>Persisted per-bucket medians, indexed by brightness/10.</summary>
    public double[] BucketWatts { get; set; } = new double[BucketCount];

    public int[] BucketSamples { get; set; } = new int[BucketCount];

    public double GlobalWatts { get; set; }
    public int GlobalSamples { get; set; }
    public DateTimeOffset? LastObserved { get; set; }

    [JsonIgnore] private readonly List<double>[] _windows = CreateWindows();
    [JsonIgnore] private readonly List<double> _globalWindow = new(WindowSize);
    [JsonIgnore] private readonly bool[] _dirty = new bool[BucketCount];
    [JsonIgnore] private bool _globalDirty;
    [JsonIgnore] private bool _seeded;

    [JsonIgnore]
    public bool IsCalibrated => GlobalSamples >= MinSamplesGlobal;

    private static List<double>[] CreateWindows()
    {
        var windows = new List<double>[BucketCount];
        for (var i = 0; i < BucketCount; i++) windows[i] = new List<double>(WindowSize);
        return windows;
    }

    public static int BucketOf(int brightnessPercent) =>
        brightnessPercent < 0
            ? BucketCount - 1
            : Math.Clamp(brightnessPercent / 10, 0, BucketCount - 1);

    /// <summary>
    /// Re-seeds the rolling windows from the persisted medians after a restart,
    /// so a fresh launch does not start guessing from zero again.
    /// </summary>
    public void Rehydrate()
    {
        if (_seeded) return;
        _seeded = true;

        BucketWatts = Fit(BucketWatts);
        BucketSamples = Fit(BucketSamples);

        for (var i = 0; i < BucketCount; i++)
        {
            if (BucketSamples[i] <= 0 || BucketWatts[i] <= 0) continue;
            var seed = Math.Min(BucketSamples[i], WindowSize / 2);
            for (var n = 0; n < seed; n++) _windows[i].Add(BucketWatts[i]);
        }

        if (GlobalSamples > 0 && GlobalWatts > 0)
        {
            var seed = Math.Min(GlobalSamples, WindowSize / 2);
            for (var n = 0; n < seed; n++) _globalWindow.Add(GlobalWatts);
        }
    }

    private static T[] Fit<T>(T[]? source)
    {
        var fitted = new T[BucketCount];
        if (source is not null) Array.Copy(source, fitted, Math.Min(source.Length, BucketCount));
        return fitted;
    }

    public void Observe(double residualWatts, int brightnessPercent)
    {
        if (double.IsNaN(residualWatts) || residualWatts < MinResidual || residualWatts > MaxResidual) return;
        Rehydrate();

        var bucket = BucketOf(brightnessPercent);
        Push(_windows[bucket], residualWatts);
        Push(_globalWindow, residualWatts);

        _dirty[bucket] = true;
        _globalDirty = true;

        BucketSamples[bucket]++;
        GlobalSamples++;
        LastObserved = DateTimeOffset.Now;
    }

    private static void Push(List<double> window, double value)
    {
        window.Add(value);
        if (window.Count > WindowSize) window.RemoveRange(0, window.Count - WindowSize);
    }

    /// <summary>Best guess at non-CPU/GPU watts for the given brightness.</summary>
    public double Estimate(int brightnessPercent, double fallbackWatts)
    {
        Rehydrate();
        var bucket = BucketOf(brightnessPercent);

        if (_windows[bucket].Count >= MinSamplesForBucket)
        {
            if (_dirty[bucket])
            {
                BucketWatts[bucket] = Median(_windows[bucket]);
                _dirty[bucket] = false;
            }
            if (BucketWatts[bucket] > 0) return BucketWatts[bucket];
        }

        if (_globalWindow.Count >= MinSamplesGlobal)
        {
            if (_globalDirty)
            {
                GlobalWatts = Median(_globalWindow);
                _globalDirty = false;
            }
            if (GlobalWatts > 0) return GlobalWatts;
        }

        return fallbackWatts;
    }

    /// <summary>Flattens the rolling windows into the persisted medians before a save.</summary>
    public void Flush()
    {
        Rehydrate();
        for (var i = 0; i < BucketCount; i++)
        {
            if (_windows[i].Count > 0) BucketWatts[i] = Median(_windows[i]);
        }
        if (_globalWindow.Count > 0) GlobalWatts = Median(_globalWindow);

        _globalDirty = false;
        Array.Clear(_dirty);
    }

    public void Reset()
    {
        Rehydrate();
        for (var i = 0; i < BucketCount; i++)
        {
            _windows[i].Clear();
            BucketWatts[i] = 0;
            BucketSamples[i] = 0;
        }
        _globalWindow.Clear();
        GlobalWatts = 0;
        GlobalSamples = 0;
        LastObserved = null;
        _globalDirty = false;
        Array.Clear(_dirty);
    }

    /// <summary>Brightness buckets that have enough samples to stand on their own.</summary>
    public IEnumerable<(int FromPercent, int ToPercent, double Watts, int Samples)> LearnedBuckets()
    {
        Rehydrate();
        for (var i = 0; i < BucketCount; i++)
        {
            if (BucketSamples[i] < MinSamplesForBucket || BucketWatts[i] <= 0) continue;
            var from = i * 10;
            var to = i == BucketCount - 1 ? 100 : from + 9;
            yield return (from, to, BucketWatts[i], BucketSamples[i]);
        }
    }

    private static double Median(List<double> values)
    {
        var copy = values.ToArray();
        Array.Sort(copy);
        var n = copy.Length;
        return n % 2 == 1 ? copy[n / 2] : (copy[n / 2 - 1] + copy[n / 2]) / 2.0;
    }
}
