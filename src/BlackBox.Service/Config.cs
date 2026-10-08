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

    /// <summary>Settings tab: LHM sensor identifier → tier (0 = off, 1 = 1 Hz, 2 = every Tier2EveryS). Wins over everything.</summary>
    public Dictionary<string, int> SensorTiers { get; set; } = new();

    public ElectricityConfig Electricity { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    /// <summary>Run with synthetic sensors/processes (development on non-Windows / no driver).</summary>
    public bool Simulate { get; set; }

    [JsonIgnore] public string DataPath => DataDir ??= DefaultDataDir();
    [JsonIgnore] public string DbPath => Path.Combine(DataPath, "blackbox.db");
    [JsonIgnore] public string LogDir => Path.Combine(DataPath, "logs");
    [JsonIgnore] public long RetentionMs => RetentionHours * 3600_000L;

    [JsonIgnore] public long LongRetentionMs => Electricity.LongRetentionDays * 86_400_000L;

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

    [JsonIgnore] public string ConfigPath => Path.Combine(DataPath, "config.json");
    static readonly object SaveLock = new();

    /// <summary>
    /// Patch config.json in place: only the keys the Settings tab owns are replaced, everything else in the file
    /// (hand-written tier_overrides, port, …) is kept. Comments are not preserved. Written atomically.
    /// </summary>
    public void Save()
    {
        lock (SaveLock)
        {
            var docOpts = new System.Text.Json.JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var root = File.Exists(ConfigPath)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ConfigPath), documentOptions: docOpts) as System.Text.Json.Nodes.JsonObject ?? new()
                : new System.Text.Json.Nodes.JsonObject();
            var el = JsonSerializer.SerializeToNode(Electricity, Json)!.AsObject();
            if (Electricity.Provider == "sim" && Electricity.FileProvider != null) el["provider"] = Electricity.FileProvider;
            root["electricity"] = el;
            root["update"] = JsonSerializer.SerializeToNode(Update, Json);
            root["retention_hours"] = RetentionHours;
            root["sensor_tiers"] = JsonSerializer.SerializeToNode(SensorTiers, Json);
            root["limit_overrides"] = JsonSerializer.SerializeToNode(LimitOverrides, Json);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, ConfigPath, true);
        }
    }

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
        // no network assumptions when simulating: synthetic prices unless the config forces a provider via BLACKBOX_REAL_PRICES
        if (cfg.Simulate && cfg.Electricity.Provider == "elprisetjustnu" && Environment.GetEnvironmentVariable("BLACKBOX_REAL_PRICES") == null)
        {
            cfg.Electricity.FileProvider = cfg.Electricity.Provider;
            cfg.Electricity.Provider = "sim";
        }
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--port") cfg.Port = int.Parse(args[i + 1]);
        Directory.CreateDirectory(cfg.DataPath);
        Directory.CreateDirectory(cfg.LogDir);
        return cfg;
    }
}

/// <summary>Energy cost estimate settings ("Power &amp; cost" tab).</summary>
public sealed class ElectricityConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>elprisetjustnu (Swedish spot prices, SE1-SE4) | fixed | sim</summary>
    public string Provider { get; set; } = "elprisetjustnu";
    public string Area { get; set; } = "SE3";
    public string Currency { get; set; } = "SEK";
    public string BaseUrl { get; set; } = "https://www.elprisetjustnu.se/api/v1/prices";
    /// <summary>Provider "fixed": all-inclusive price per kWh (surcharge and VAT are not added).</summary>
    public double FixedPricePerKwh { get; set; } = 2.0;
    /// <summary>Added to the spot price before VAT: grid transfer fee + energy tax + supplier markup, per kWh.</summary>
    public double SurchargePerKwh { get; set; }
    public double VatPct { get; set; } = 25;
    /// <summary>Rest of the system not covered by sensors (board, RAM, SSDs, fans, pump), in watts.</summary>
    public double BaseLoadW { get; set; } = 60;
    public double PsuEfficiency { get; set; } = 0.92;
    /// <summary>Energy, price and per-process energy history is kept this long (the 72 h limit is for raw samples only).</summary>
    public int LongRetentionDays { get; set; } = 730;

    /// <summary>Provider as written in config.json when --simulate replaced it with "sim" (so saving keeps the user's choice).</summary>
    [JsonIgnore] public string? FileProvider { get; set; }

    public double Total(double spot) => Provider == "fixed" ? FixedPricePerKwh : (spot + SurchargePerKwh) * (1 + VatPct / 100);
}

/// <summary>Self-update from GitHub Releases (see .github/workflows/release.yml).</summary>
public sealed class UpdateConfig
{
    public bool Enabled { get; set; } = true;
    public string Repo { get; set; } = "moorknet/Monitor";
    public double CheckHours { get; set; } = 6;
}
