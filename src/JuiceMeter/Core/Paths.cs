namespace JuiceMeter.Core;

/// <summary>
/// Everything Juice Meter writes lives under one folder. Drop a file named
/// "portable.txt" next to the exe to keep the data beside the exe instead of
/// in %LOCALAPPDATA% (handy on a USB stick).
/// </summary>
internal static class Paths
{
    public static string DataDir { get; }
    public static string HistoryDir { get; }
    public static bool Portable { get; }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "juicemeter.log");
    public static string RawDir => Path.Combine(DataDir, "raw");

    static Paths()
    {
        Portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
        DataDir = Portable
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JuiceMeter");
        HistoryDir = Path.Combine(DataDir, "history");
        Directory.CreateDirectory(HistoryDir);
    }

    /// <summary>One CSV per calendar month, named yyyy-MM.csv.</summary>
    public static string MonthFile(int year, int month) =>
        Path.Combine(HistoryDir, $"{year:D4}-{month:D2}.csv");

    public static string MonthFile(DateTime localDate) => MonthFile(localDate.Year, localDate.Month);

    public static string RawFile(DateTime localDate) =>
        Path.Combine(RawDir, $"{localDate:yyyy-MM-dd}.csv");

    /// <summary>Writes a file without risking a half-written file if we die mid-write.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
