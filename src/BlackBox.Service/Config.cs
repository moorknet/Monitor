using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox;

public sealed class Config
{
    public string? DataDir { get; set; }
    public int Port { get; set; } = 8787;
    public int RetentionHours { get; set; } = 72;
    public int CommitIntervalMs { get; set; } = 2000;
    public int Tier2EveryS { get; set; } = 10;
    public int ProcessIntervalS { get; set; } = 2;
    public int TopByCpu { get; set; } = 10;
    public int TopByGpu { get; set; } = 5;
    public double ProcThresholdPct { get; set; } = 5;
    public int DbBudgetMb { get; set; } = 500;
    public int SlowTickMs { get; set; } = 200;

    /// <summary>Regex (matched against "hardware name/sensor name" and the LHM identifier) → tier (0 = don't store, 1, 2).</summary>
    public Dictionary<string, int> TierOverrides { get; set; } = new();

    /// <summary>User limits; same shape as profile limits, override profile entries with the same role.</summary>
    public List<LimitDef> LimitOverrides { get; set; } = new();

    /// <summary>Run with synthetic sensors/processes (development on non-Windows / no driver).</summary>
    public bool Simulate { get; set; }

    [JsonIgnore] public string DataPath => DataDir ??= DefaultDataDir();
    [JsonIgnore] public string DbPath => Path.Combine(DataPath, "blackbox.db");
    [JsonIgnore] public string LogDir => Path.Combine(DataPath, "logs");
    [JsonIgnore] public long RetentionMs => RetentionHours * 3600_000L;

    public static string DefaultDataDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlackBox")
        : Path.Combine(AppContext.BaseDirectory, "data");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config Load(string[] args)
    {
        // config.json lives in the data dir; BLACKBOX_DATA / --data override the location.
        string? dataDir = Environment.GetEnvironmentVariable("BLACKBOX_DATA");
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--data") dataDir = args[i + 1];
        dataDir ??= DefaultDataDir();
        var path = Path.Combine(dataDir, "config.json");
        Config cfg = File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new() : new();
        cfg.DataDir ??= dataDir;
        if (args.Contains("--simulate") || !OperatingSystem.IsWindows()) cfg.Simulate = true;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--port") cfg.Port = int.Parse(args[i + 1]);
        Directory.CreateDirectory(cfg.DataPath);
        Directory.CreateDirectory(cfg.LogDir);
        return cfg;
    }
}
