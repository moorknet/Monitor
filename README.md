# BlackBox — hardware flight recorder for Windows

An always-on Windows service that records power, temperatures, clocks, loads, fans and voltages, the top
CPU/GPU processes, and crash-related Windows events. It keeps a rolling 72 h on disk and serves a local viewer at
**http://127.0.0.1:8787**. It is built to answer *"what was the machine doing in the seconds before it shut off?"*

- C# / .NET 8: one self-contained `BlackBox.exe` (Windows service + console), Kestrel bound to `127.0.0.1` only.
- Sensors come from [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6, which uses the **PawnIO** driver.
- Storage is SQLite in WAL mode, committed every 2 s. A hard power cut loses at most the last ~2 s.
- Viewer: plain HTML/JS with vendored uPlot. It works offline, has no build step, and does nothing while the tab is hidden.

## Why LibreHardwareMonitor (and not our own)

We checked LHM 0.9.6 against the spec before building on it:

| Need | LHM 0.9.6 |
|---|---|
| Driver not flagged by Defender | Uses **PawnIO** (signed; `\\.\PawnIO`), WinRing0 is gone. PawnIO must be installed once; `install.ps1` handles it |
| 7800X3D Tctl/Tdie, CCD temps, package power | Zen 4 via the PawnIO `RyzenSMU` / `AMDFamily17` modules |
| X670E Hero VRM / fans / voltages | Nuvoton Super I/O (`LpcIO` module) + ASUS EC |
| RX 9070 XT edge / hotspot / memory / power | AMD backend via ADL PMLog (`ADL2_New_QueryPMLogData_Get`) |
| NVMe, DDR5 SPD temps | `DiskInfoToolkit`, `RAMSPDToolkit` |
| Overhead | In-process library, no WMI polling; `Update()` is called per hardware item only when it has due sensors |

A home-grown replacement would need its own signed kernel driver plus SMU, Super I/O and ADL code. That is a large
maintenance burden for no gain in speed or footprint. **RDNA4 is the one item to confirm on the real machine.** Run
the milestone-1 dump tool (below). If GPU sensors are missing, the clean fallback is a small ADLX reader for the GPU
only, with everything else staying on LHM.

## Install

**Easiest (no SDK needed):** download `BlackBox-<version>-win-x64.zip` from the
[latest release](https://github.com/moorknet/Monitor/releases/latest), extract it, and run `.\install.ps1` in an elevated PowerShell.

**From source:** run this in an elevated PowerShell on the target machine (needs the .NET 8 SDK for `-Build`, or a prebuilt `publish\` folder):

```powershell
git clone https://github.com/moorknet/Monitor.git; cd Monitor
.\install.ps1 -Build
```

`install.ps1`:
1. Publishes self-contained `win-x64` builds of the service and `sensordump` into `.\publish`.
2. Installs PawnIO with `winget` if it is missing. On LTSC without winget it prints the https://pawnio.eu link instead.
3. Copies the files to `C:\Program Files\BlackBox` and creates `C:\ProgramData\BlackBox` with an Administrators + SYSTEM-only ACL.
4. Creates the `BlackBox` service: LocalSystem, automatic start, restart on failure (5 s / 5 s / 30 s).
5. Starts the service and checks that `/api/status` answers.

To remove it: `.\uninstall.ps1` keeps the recorded data; `.\uninstall.ps1 -RemoveData` deletes it too. PawnIO is left installed.

### Milestone 1: check what sensors this machine exposes

```powershell
.\publish\sensordump\BlackBox.SensorDump.exe            # one-shot list + spec coverage checklist
.\publish\sensordump\BlackBox.SensorDump.exe --watch    # live, 1 Hz
.\publish\sensordump\BlackBox.SensorDump.exe --json sensors.json
```

The checklist at the end marks each spec item OK/MISS: Tctl, CCD, package power, GPU hotspot/memory/power, Super I/O, fans, VRM, +12 V, NVMe, DIMM.

## Where data lives

| Path | What |
|---|---|
| `C:\ProgramData\BlackBox\blackbox.db` (+`-wal`) | SQLite DB, 72 h rolling |
| `C:\ProgramData\BlackBox\logs\blackbox.log` | Rolling log (5 MB + one `.1` backup) |
| `C:\ProgramData\BlackBox\config.json` | Optional overrides, see `config.example.json`. Restart the service after editing |
| `C:\Program Files\BlackBox\profiles\*.json` | Limit profiles, matched by hardware-name regex |

To run as a console app (for debugging), stop the service and run `BlackBox.exe` elevated. Add `--simulate` to use synthetic hardware.

## Reading the viewer

- **Range bar.** Presets 15 m to 72 h. *live* follows "now" and refreshes every 5 s (30 s for long ranges). *max / avg / min* picks how
  zoomed-out buckets are reduced; *max* is the default because peaks matter for crash diagnosis. *Jump to event* lists
  power losses, WHEA errors, GPU resets (TDR), bugchecks, limit breaches and throttling.
- **72 h overview.** Drag across it to choose the range. Red ticks are crash-type events.
- **Charts.** Power, Temperatures, Load & clocks, Fans, Voltages, Process attribution, and a Custom chart. The cursor is shared across all of them.
  - Vertical lines are events: red = critical, orange = warning, dotted = info such as service start.
  - **Red shaded areas are power-loss gaps**: from the last recorded sample to the next service start, when the service did not stop cleanly.
  - Dashed horizontal lines are a series' warn (orange) and crit (red) limits, shown when the series gets within 15 % of them.
    Dots mark samples above warn.
- **Selection.** Drag on any chart to select a span. The side panel then shows, for that span:
  - **Processes** ranked by *estimated* energy (Wh), with CPU/GPU avg and peak. Estimated W =
    CPU package W × the process's share of CPU time + GPU board W × its share of GPU utilisation. This is an
    attribution estimate, not a measurement.
  - **Limits**: peak, time above warn, and a verdict (OK / Warn / Crit). Hover a row to see where the limit comes from.
  - **Events** in the span. Click one to select ±30 s around it.
- **Last 60 s before shutdown.** Pick a `power_loss` event in *Jump to event* or click *last 60 s ▸* in the event list.
  All tier-1 sensors are shown at full 1 Hz, with a table of last/min/max values and the processes active at the time.
- **Sensors…** builds the Custom chart from any recorded sensor. **Export CSV** saves the selection, or the visible range,
  as a wide CSV.

## Settings tab

Everything below can be changed in the viewer (**Settings**). Changes apply immediately, with no restart, and are saved to
`C:\ProgramData\BlackBox\config.json`. Only the keys the tab owns are rewritten; anything else you added by hand stays,
but comments in the file are not kept.

- **Electricity & running cost:** price source (Nord Pool spot by area, or a fixed price), currency, surcharge (grid fee +
  energy tax + markup), VAT, rest-of-system watts and PSU efficiency, with a live preview of the wall power and cost per hour.
  Wall energy is worked out from the stored CPU/GPU watt-hours when you view it, so these settings re-price your whole history.
  Changing the price area, currency or source refetches prices.
- **Sensors:** per sensor, *Every second*, *Every 10 s* or *Off*. Off stops recording the sensor and removes it from charts,
  limits, derived values and the cost meter. If it had a job (e.g. CPU temperature or GPU power), another suitable sensor takes
  over when one exists. ⚠ flags live readings that are physically implausible (0 °C, negative RPM, +12 V far from 12 V, …),
  which usually means a misreporting sensor. Saved as `sensor_tiers` (LHM identifier → 0/1/2).
- **Limits:** warn/crit per limit (± % for the 12 V rail, CPU temperature threshold for the fan-stall check), with ↺ to
  return to the hardware profile default. Saved as `limit_overrides`.
- **General:** how many hours of detailed history to keep, and the automatic update check.

## Updates

Every push to the default branch triggers `.github/workflows/release.yml` on a Windows runner. Docs-only changes are skipped.
The workflow builds the service and SensorDump and publishes a GitHub Release `v0.2.<run number>`. Each release has a zip,
a `.sha256` checksum and release notes made from the commit messages.

The installed service checks the latest release every 6 h (`update.check_hours`) with one unauthenticated call to
`api.github.com`. When a newer version exists, the viewer shows a banner with **What's new**, **Update now** and **Later**.
Click the version chip in the header to check right away.

**Update now** does the following:
1. The service downloads the zip to `C:\ProgramData\BlackBox\updates`, checks its SHA-256 and extracts it.
2. It starts the **new** `BlackBox.exe --apply-update` from the staging folder, as a separate process.
3. That updater stops the service, backs up `C:\Program Files\BlackBox` to `updates\backup` and copies the new files in.
4. It starts the service and waits for `/api/status` to report the new version.
5. If the new version doesn't come up within 60 s, it restores the backup and starts the old version again.
6. The result appears in the viewer and as an `update_installed` / `update_failed` event. Details go to `logs\update.log`.

Recording pauses for about 10–30 s during an update. Settings and recorded data in `C:\ProgramData\BlackBox` are untouched.

The web UI only accepts `Host: 127.0.0.1` / `localhost`, which blocks DNS rebinding. Its POST endpoints require an
`X-BlackBox: 1` header, which another website's page cannot send, so a random site you visit cannot trigger an update.
To turn updates off, set `"update": { "enabled": false }` in `config.json`.

## Power & cost tab

The second tab turns the measured power into an electricity bill estimate, with some fun facts on top.

- **Energy.** Every sensor tick integrates *estimated wall power* = (CPU package W + GPU board W + `base_load_w`) ÷
  `psu_efficiency` into 15-minute buckets (`energy_quarter`). Per-process attributed energy is summed per hour
  (`proc_energy_hour`). These tables are kept for `long_retention_days` (default 2 years, about 2 MB/year); the 72 h limit
  only applies to raw samples.
- **Prices.** With `provider: "elprisetjustnu"` (the default) the service fetches Nord Pool spot prices for your area (`SE3`
  by default) from elprisetjustnu.se: today's, tomorrow's once they are published around 13:00, and up to 30 missing past days.
  That is one small HTTPS request per day, checked every 30 min. Outside Sweden, use `provider: "fixed"` with
  `fixed_price_per_kwh`. Prices are stored as spot. Cost = kWh × (spot + `surcharge_per_kwh`) × (1 + VAT), worked out
  when you view it, so changing the surcharge in `config.json` re-prices your history after a restart.
  **Set `surcharge_per_kwh` to your grid fee + energy tax** (both on your electricity bill), or the tab shows spot + VAT only.
- **What it shows:**
  - Tiles: power draw now, price now, cost per hour, today, this month, last 30 days with a yearly projection.
  - Today's and tomorrow's price curve, with the cheapest upcoming 3-hour window marked.
  - Cost per hour for the last 48 h, and cost per day for the last 30 days.
  - Which processes cost the most this month.
  - Fun facts: what a game cost you, the cheapest time to play, what playing at today's cheapest price would have saved,
    and the month's energy in litres of coffee boiled, EV km and phone charges.

## How it works

| Component | Details |
|---|---|
| `SensorSampler` (1 Hz) | Tier 1 = every tick (CPU Tctl/CCD/package W/load, GPU board W/edge/hotspot/mem/clock/load, VRM, all fans, +12 V/Vcore/VSOC). Tier 2 = every 10 s (everything else). Per-core clocks/loads are folded into derived *max/avg core clock* and *max core load*; *CPU + GPU power* is also derived. Hardware that has no sensors due on a tick is not `Update()`d. Repeated update failures (e.g. a GPU TDR) reopen LHM instead of crashing |
| `ProcessSampler` (0.5 Hz) | One `NtQuerySystemInformation(SystemProcessInformation)` call, with no per-process handles except one path lookup the first time a PID is persisted. GPU % comes from a single reused PDH query on `\GPU Engine(*)\Utilization Percentage`, summed per PID over 3D and Compute engines. Stores top 10 by CPU ∪ top 5 by GPU ∪ anything over 5 %, plus one `(other)` row. Process start/exit events come from diffing the PID set |
| `EventWatcher` | Backfills the System log for the retention window, then subscribes from the backfill bookmark so nothing is missed or duplicated. Watches Kernel-Power 41/125, EventLog 6008, WHEA 1/17/18/19/46/47, Display 4101, amdkmdag errors, BugCheck 1001, Kernel-Processor-Power 26/37, User32 1074 |
| `LimitEvaluator` | Profiles in `profiles/` (7800X3D, RX 9070 XT, X670E Hero, generic fallback) plus `limit_overrides` in config. Kinds: `high`, `low`, `band` (±% around a nominal value, for 12 V) and `zero_while` (fan at 0 RPM while CPU > 60 °C). Breach events are emitted only after `min_duration_s`, with hysteresis, and an end event records the peak. A throttling heuristic flags load > 80 % with avg clock > 15 % under its 60 s median and Tctl ≥ warn |
| `Writer` | Producers append to pooled in-memory buffers. Every 2 s everything is written in one transaction through raw prepared SQLite statements (no boxing, no per-row allocation). The same pass maintains a per-minute process rollup (`proc_minute`) so 72 h process queries stay fast. Retention runs hourly: per-sensor range deletes, 50k-row chunks for process/event rows, and an incremental vacuum only when free pages exceed 10 % |
| Power-loss detection | A `running.flag` file exists while the service runs and is removed on clean stop. On start, a leftover flag means the last stop was unclean: run `PRAGMA quick_check` (a corrupt DB is renamed aside and a fresh one started) and log a `power_loss` event at the last sample time, carrying the gap length |
| Self-measurement | `/api/status` reports service CPU %, private MB, tick/commit timings, sample lag, DB size and a 72 h size projection. A tick over 200 ms logs a `slow_tick` event |

API endpoints (all take `from` and `to` in Unix ms): `/api/sensors`, `/api/series?ids=&maxPoints=` (server-side min/max/avg buckets on one
shared time grid), `/api/processes`, `/api/proc-series?top=8`, `/api/events`, `/api/limits`,
`/api/limit-summary`, `/api/status`, `/api/export.csv?ids=`.

## Verification so far (Linux container, `--simulate`)

The Windows-only paths (LHM, NtQuerySystemInformation, PDH, Event Log) compile but **have not been run on real hardware
yet**. Everything else ran against synthetic hardware that mimics the target machine (52 sensors: 23 tier 1, 29 tier 2):

| Check | Result |
|---|---|
| 72 h of history (7.7 M rows) written through the real writer | **253 MB**. Breakdown: ~22 B per sensor sample, ~62 B per process row incl. index |
| Extrapolated to the real sensor count (~35 tier 1 / ~150 tier 2) | ~290 MB samples + ~130 MB processes ≈ **~420 MB < 500 MB budget**. Watch `db_projected_mb` in `/api/status`; if it goes over, demote sensors with `tier_overrides` |
| Startup with the 72 h DB | 0.17 s after a clean stop; **1.2 s** after an unclean stop (includes `quick_check`) |
| API over 72 h | series (5 sensors, 2000 points) 0.44 s; processes 0.11 s; proc-series 0.13 s; limit summary 0.6 s. 1 h ranges < 50 ms |
| Steady-state cost | sensor tick 0.3 ms avg; 2 s commit 0.4–7 ms; service CPU ~0.3 % of total while idle (container) |
| `kill -9` mid-run | `integrity_check` ok; restart logged `power_loss` with the correct gap; ≤ 2 s of samples lost |
| Clean stop (SIGTERM / service stop) | `service_stop` event, flag removed, WAL checkpointed |
| Retention (72 h DB, set to 48 h) | Deleted 2.6 M rows in 2.3 s; file shrank from 256 to 182 MB |

**Memory:** the container cannot measure Windows private bytes, since Linux reports reserved GC regions instead. To keep
private memory under the 80 MB budget the service uses workstation non-concurrent GC with `ConserveMemory` and
ReadyToRun, and `install.ps1` caps the gen-0 budget (`DOTNET_GCgen0MaxBudget=6 MB`). Without that cap the gen-0 budget
scales with L3 size, and the 7800X3D has 96 MB. Check `private_mb` in `/api/status` after an hour on the real machine.

### Still to do on the target machine (milestone 8)

1. Run `sensordump` and confirm the RDNA4 + Super I/O coverage checklist.
2. Run for 1 h under Diablo 4 or a stress test, then check `/api/status`: `cpu_pct_avg` < 0.5, `private_mb` < 80, `db_projected_mb` < 500.
3. Kill `BlackBox.exe` from Task Manager mid-run, then confirm the viewer shows a red power-loss gap and the DB opens.

Cost API: `/api/cost/summary`, `/api/cost/hourly`, `/api/cost/daily?days=30`, `/api/cost/prices`, `/api/cost/processes`.

## Development

```bash
dotnet build BlackBox.sln -c Release
# run anywhere with synthetic hardware (web UI on :8787)
dotnet src/BlackBox.Service/bin/Release/net8.0-windows/BlackBox.dll --simulate --data ./data
# write N hours of synthetic history (size / startup / query benchmarks)
dotnet src/BlackBox.Service/bin/Release/net8.0-windows/BlackBox.dll --simulate --data ./data --generate-history 72 --energy-days 35
```
`--energy-days` adds simulated long-term energy (evening gaming sessions) for the cost tab. `--simulate` uses synthetic
prices; set `BLACKBOX_REAL_PRICES=1` to fetch real ones while simulating.

The project is not trimmed: LHM and System.Management rely on reflection and COM, so `PublishTrimmed` would break sensor
discovery. The win-x64 publish is a ~120 MB single-file exe plus `e_sqlite3.dll`.
