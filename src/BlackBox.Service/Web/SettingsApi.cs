using System.Text.Json;
using BlackBox.Energy;
using BlackBox.Sensors;
using BlackBox.Update;

namespace BlackBox.Web;

/// <summary>
/// Settings tab. GET returns everything the panel edits (with defaults); POST validates a partial change set,
/// applies it live (sensor tiers/limits on the next sensor tick, prices refetched on source change) and patches config.json.
/// </summary>
public static class SettingsApi
{
    static readonly HashSet<string> Providers = ["elprisetjustnu", "fixed", "sim"];
    static readonly HashSet<string> Areas = ["SE1", "SE2", "SE3", "SE4"];
    static readonly HashSet<string> Currencies = ["SEK", "EUR"];

    public static void Map(WebApplication app, Config cfg)
    {
        app.MapGet("/api/settings", (SensorSampler sampler, LimitEvaluator le) => Results.Json(Snapshot(cfg, sampler, le), Config.Json));

        app.MapPost("/api/settings", async (HttpContext ctx, SensorSampler sampler, LimitEvaluator le, PriceService prices) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            var errors = new List<string>();
            double Num(JsonElement o, string name, double min, double max, double current)
            {
                if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return current;
                if (v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble()) || v.GetDouble() < min || v.GetDouble() > max)
                { errors.Add($"{name} must be a number between {min} and {max}"); return current; }
                return v.GetDouble();
            }
            string Str(JsonElement o, string name, HashSet<string> allowed, string current)
            {
                if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return current;
                var s = v.GetString() ?? "";
                if (!allowed.Contains(s)) { errors.Add($"{name} must be one of {string.Join(", ", allowed)}"); return current; }
                return s;
            }

            // ---- validate everything first; apply only if there are no errors ----
            var e = cfg.Electricity;
            ElectricityConfig? newE = null;
            if (root.TryGetProperty("electricity", out var je))
                newE = new ElectricityConfig
                {
                    Enabled = e.Enabled, BaseUrl = e.BaseUrl, LongRetentionDays = e.LongRetentionDays, FileProvider = e.FileProvider,
                    Provider = Str(je, "provider", Providers, e.Provider),
                    Area = Str(je, "area", Areas, e.Area),
                    Currency = Str(je, "currency", Currencies, e.Currency),
                    SurchargePerKwh = Num(je, "surcharge_per_kwh", 0, 20, e.SurchargePerKwh),
                    VatPct = Num(je, "vat_pct", 0, 50, e.VatPct),
                    FixedPricePerKwh = Num(je, "fixed_price_per_kwh", 0, 50, e.FixedPricePerKwh),
                    BaseLoadW = Num(je, "base_load_w", 0, 500, e.BaseLoadW),
                    PsuEfficiency = Num(je, "psu_efficiency", 0.5, 1.0, e.PsuEfficiency),
                };
            int retention = (int)Num(root, "retention_hours", 12, 336, cfg.RetentionHours);
            bool updEnabled = cfg.Update.Enabled; double updHours = cfg.Update.CheckHours;
            if (root.TryGetProperty("update", out var ju))
            {
                if (ju.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False) updEnabled = en.GetBoolean();
                updHours = Num(ju, "check_hours", 1, 168, updHours);
            }

            Dictionary<string, int>? tiers = null;
            if (root.TryGetProperty("sensors", out var js) && js.ValueKind == JsonValueKind.Object)
            {
                tiers = new Dictionary<string, int>(cfg.SensorTiers);
                var known = sampler.Groups.SelectMany(g => g.Sensors).ToDictionary(s => s.Identifier);
                foreach (var p in js.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null) { tiers.Remove(p.Name); continue; }
                    if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetInt32() is < 0 or > 2) { errors.Add($"sensor {p.Name}: tier must be 0, 1 or 2"); continue; }
                    int t = p.Value.GetInt32();
                    // storing the default is the same as no override; keep config.json minimal
                    if (known.TryGetValue(p.Name, out var s) && s.DefaultTier == t) tiers.Remove(p.Name); else tiers[p.Name] = t;
                }
            }

            List<LimitDef>? overrides = null;
            if (root.TryGetProperty("limits", out var jl) && jl.ValueKind == JsonValueKind.Array)
            {
                overrides = new List<LimitDef>(cfg.LimitOverrides);
                foreach (var item in jl.EnumerateArray())
                {
                    var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
                    var b = le.Bound.FirstOrDefault(x => x.Sensor.Role == role);
                    if (role == null || b == null) { errors.Add($"limit '{role}': unknown or inactive role"); continue; }
                    overrides.RemoveAll(o => o.Role == role);
                    if (item.TryGetProperty("reset", out var rs) && rs.ValueKind == JsonValueKind.True) continue;
                    var baseDef = le.ProfileDefault(b.Sensor, role)?.def ?? b.Def;
                    double? Opt(string n, double cur) => item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : cur;
                    var d = new LimitDef
                    {
                        Role = role, Label = baseDef.Label, Kind = baseDef.Kind, Hysteresis = baseDef.Hysteresis, MinDurationS = baseDef.MinDurationS,
                        Nominal = baseDef.Nominal, WhileRole = baseDef.WhileRole, WhileAbove = baseDef.WhileAbove,
                        Warn = baseDef.Warn, Crit = baseDef.Crit, WarnPct = baseDef.WarnPct, CritPct = baseDef.CritPct,
                        Source = "Settings tab (default: " + (baseDef.Source ?? "profile") + ")",
                    };
                    if (d.Kind == "band") { d.WarnPct = Opt("warn_pct", d.WarnPct ?? 5); d.CritPct = Opt("crit_pct", d.CritPct ?? 8); }
                    else if (d.Kind == "zero_while") d.WhileAbove = Opt("while_above", d.WhileAbove ?? 60);
                    else { d.Warn = Opt("warn", d.Warn ?? 0); d.Crit = Opt("crit", d.Crit ?? 0); }
                    bool ok = d.Kind switch
                    {
                        "band" => d.WarnPct > 0 && d.CritPct >= d.WarnPct,
                        "low" => d.Warn >= d.Crit,
                        "zero_while" => d.WhileAbove > 0,
                        _ => d.Crit >= d.Warn,
                    };
                    if (!ok) { errors.Add($"{d.Label ?? role}: crit must be beyond warn"); continue; }
                    overrides.Add(d);
                }
            }
            if (errors.Count > 0) return Results.Json(new { error = string.Join("; ", errors) }, Config.Json, statusCode: 400);

            // ---- apply ----
            if (newE != null)
            {
                bool sourceChanged = newE.Provider != e.Provider || newE.Area != e.Area || newE.Currency != e.Currency;
                e.Provider = newE.Provider; e.Area = newE.Area; e.Currency = newE.Currency;   // same instance: CostApi/EnergyMeter see it live
                e.SurchargePerKwh = newE.SurchargePerKwh; e.VatPct = newE.VatPct; e.FixedPricePerKwh = newE.FixedPricePerKwh;
                e.BaseLoadW = newE.BaseLoadW; e.PsuEfficiency = newE.PsuEfficiency;
                if (newE.Provider != "sim") e.FileProvider = null;
                if (sourceChanged) prices.ResetPrices();
            }
            cfg.RetentionHours = retention;
            cfg.Update.Enabled = updEnabled; cfg.Update.CheckHours = updHours;
            bool rebuild = false;
            if (tiers != null) { cfg.SensorTiers = tiers; rebuild = true; }          // swap whole objects: the sensor thread never sees a half-edited map
            if (overrides != null) { cfg.LimitOverrides = overrides; rebuild = true; }
            if (rebuild) sampler.RequestRebuild();
            try { cfg.Save(); }
            catch (Exception ex) { return Results.Json(new { error = $"Applied, but saving {cfg.ConfigPath} failed: {ex.Message}" }, Config.Json, statusCode: 500); }
            if (rebuild) await Task.Delay(1500);  // let the sensor tick re-apply so the response shows the new state
            return Results.Json(new { ok = true, settings = Snapshot(cfg, sampler, le) }, Config.Json);
        });
    }

    static object Snapshot(Config cfg, SensorSampler sampler, LimitEvaluator le)
    {
        var e = cfg.Electricity;
        var sensors = sampler.Groups.SelectMany(g => g.Sensors).Select(s => new
        {
            identifier = s.Identifier, id = s.Id, hw = s.Group.Name, hw_kind = s.Group.Kind, name = s.Name, type = s.Type, unit = s.Unit,
            tier = s.Tier, default_tier = s.DefaultTier, role = s.Role, default_role = s.DefaultRole,
            last = double.IsFinite(s.Last) ? Math.Round(s.Last, 3) : (double?)null,
            user = cfg.SensorTiers.ContainsKey(s.Identifier),
        });
        // limits of sensors that are currently off are listed too (from their default role) so they can be re-enabled knowingly
        var limits = le.Bound.Select(b =>
        {
            var def = le.ProfileDefault(b.Sensor, b.Sensor.Role);
            return new
            {
                role = b.Sensor.Role, sensor = $"{b.Sensor.Group.Name} / {b.Sensor.Name}", label = b.Def.Label ?? b.Sensor.Name, unit = b.Sensor.Unit,
                kind = b.Def.Kind, warn = b.Def.Warn, crit = b.Def.Crit, warn_pct = b.Def.WarnPct, crit_pct = b.Def.CritPct, while_above = b.Def.WhileAbove,
                source = b.Def.Source, profile = b.ProfileName, overridden = b.ProfileName == "config.json",
                @default = def == null ? null : new { warn = def.Value.def.Warn, crit = def.Value.def.Crit, warn_pct = def.Value.def.WarnPct, crit_pct = def.Value.def.CritPct, while_above = def.Value.def.WhileAbove, profile = def.Value.profile },
                last = double.IsFinite(b.Sensor.Last) ? Math.Round(b.Sensor.Last, 2) : (double?)null,
            };
        });
        return new
        {
            config_path = cfg.ConfigPath,
            simulate = cfg.Simulate,
            electricity = new { e.Provider, e.Area, e.Currency, e.SurchargePerKwh, e.VatPct, e.FixedPricePerKwh, e.BaseLoadW, e.PsuEfficiency },
            retention_hours = cfg.RetentionHours,
            update = new { cfg.Update.Enabled, cfg.Update.CheckHours, current = UpdateService.Current.ToString() },
            sensors, limits,
        };
    }
}
