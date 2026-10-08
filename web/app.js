'use strict';
// BlackBox viewer. Plain JS + uPlot, no build step. Does nothing while the tab is hidden.

const $ = s => document.querySelector(s);
const PALETTE = ['#4e9cff', '#ff8a3d', '#3ddc97', '#e45b78', '#b48cff', '#ffd24d', '#4dd8e6', '#ff6fd8', '#9bd35a', '#c9ced6'];
// vertical markers only for what matters when diagnosing a crash; everything else stays in the event list / Jump menu
const MARKER_KINDS = new Set(['power_loss', 'kernel_power_41', 'unexpected_shutdown', 'bugcheck', 'whea', 'gpu_tdr', 'throttle',
  'sensor_reset', 'db_reset', 'thermal', 'cpu_firmware_limit']);
const markerShown = e => MARKER_KINDS.has(e.kind) || (e.kind === 'limit_breach' && e.severity === 'crit');
const JUMP_KINDS = new Set(['power_loss', 'kernel_power_41', 'unexpected_shutdown', 'bugcheck', 'whea', 'gpu_tdr', 'limit_breach',
  'throttle', 'sensor_reset', 'db_reset', 'thermal', 'cpu_firmware_limit']);

const CHARTS = [
  { id: 'power', title: 'Power (W)', roles: ['cpu.package_power', 'gpu.board_power', 'power.total'] },
  // the few temperatures that matter per component; 'a|b' = first role that exists (cards without a hotspot sensor show the core)
  { id: 'temps', title: 'Temperatures (°C)', roles: ['cpu.tctl', 'gpu.hotspot|gpu.edge', 'gpu.mem_temp', 'mb.vrm', 'nvme.max'] },
  { id: 'load', title: 'Load (%) & clocks (MHz)', roles: ['cpu.load', 'cpu.core_load_max', 'gpu.load', 'cpu.clock_max', 'cpu.clock_avg', 'gpu.clock'] },
  { id: 'fans', title: 'Fans (RPM)', filter: s => s.type === 'Fan' },
  { id: 'volts', title: 'Voltages (V)', roles: ['mb.12v', 'mb.vcore', 'mb.vsoc'], scaleOf: s => s.role === 'mb.12v' ? '12 V' : 'V' },
  { id: 'procs', title: 'Process attribution — estimated W (CPU share × package W + GPU share × board W)', procs: true },
  { id: 'custom', title: 'Custom', custom: true },
];

const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { } },
};

const state = {
  dur: store.get('bb.dur', 3600e3), live: true, from: 0, to: 0, agg: store.get('bb.agg', 'max'),
  sel: null, sensors: [], sensorById: new Map(), limitsBySensor: new Map(), events: [], allEvents: [],
  tab: 'rec', custom: store.get('bb.custom', []), shutdown: null, charts: new Map(), overview: null, timer: 0, loading: false,
};

// ---------- utils ----------
const pad = n => String(n).padStart(2, '0');
function fmtTime(ms, withDate = true) {
  const d = new Date(ms);
  const t = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  return withDate ? `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${t}` : t;
}
function fmtDur(ms) {
  const s = Math.round(ms / 1000);
  if (s < 120) return `${s} s`;
  if (s < 7200) return `${Math.round(s / 60)} min`;
  return `${(s / 3600).toFixed(1)} h`;
}
const fmt = (v, d = 1) => v == null || Number.isNaN(v) ? '–' : Number(v).toFixed(d);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
async function api(path) {
  const r = await fetch(path, { cache: 'no-store' });
  if (!r.ok) {
    let msg = '';
    try { msg = (await r.json()).error || ''; } catch { }
    throw new Error(`${path.split('?')[0]} → HTTP ${r.status}${msg ? ': ' + msg : ''}`);
  }
  return r.json();
}
function shortHw(s) {
  switch (s.hw_kind) {
    case 'Cpu': return 'CPU';
    case 'GpuAmd': case 'GpuNvidia': case 'GpuIntel': return 'GPU';
    case 'SuperIO': case 'Motherboard': case 'EmbeddedController': return 'MB';
    case 'Memory': return 'RAM';
    case 'Derived': return '';
    default: return s.hw.length > 18 ? s.hw.slice(0, 16) + '…' : s.hw;
  }
}
const label = s => { const p = shortHw(s); return (p && !s.name.toUpperCase().startsWith(p) ? `${p} ${s.name}` : s.name).trim(); };
const chartWidth = () => Math.max(300, $('#charts').clientWidth - 14);

// ---------- range ----------
function setRange(from, to, live = false) {
  state.from = Math.round(from); state.to = Math.round(to); state.live = live;
  $('#live').checked = live;
  refresh();
}
function presetRange() {
  const now = Date.now();
  state.from = now - state.dur; state.to = now;
}

// ---------- overlay drawing (events, power-loss gaps, selection, limit lines) ----------
function overlayPlugin(getSeriesLimits) {
  return {
    hooks: {
      draw: [u => {
        const ctx = u.ctx, { left, top, width, height } = u.bbox, dpr = devicePixelRatio || 1;
        const x = v => u.valToPos(v, 'x', true);
        ctx.save();
        ctx.beginPath(); ctx.rect(left, top, width, height); ctx.clip();
        // power-loss gaps (last sample → restart) shaded red
        for (const e of state.allEvents) {
          if (e.kind !== 'power_loss') continue;
          const x0 = x(e.ts), x1 = x(e.ts + (e.value || 0) * 1000);
          if (x1 < left || x0 > left + width) continue;
          ctx.fillStyle = 'rgba(229,72,77,0.16)';
          ctx.fillRect(x0, top, Math.max(2 * dpr, x1 - x0), height);
        }
        // selection
        if (state.sel) {
          const x0 = x(state.sel.from), x1 = x(state.sel.to);
          ctx.fillStyle = 'rgba(78,156,255,0.13)';
          ctx.fillRect(x0, top, x1 - x0, height);
        }
        // event markers: merged per 8 px (worst severity wins); if still dense, drawn as ticks on the top edge so they never cover data
        const rank = { crit: 3, warn: 2, info: 1 }, bucket = 8 * dpr, cells = new Map();
        for (const e of state.events) {
          if (!markerShown(e)) continue;
          const px = x(e.ts);
          if (px < left || px > left + width) continue;
          const k = Math.round(px / bucket), cur = cells.get(k);
          if (!cur || rank[e.severity] > rank[cur.sev]) cells.set(k, { px, sev: e.severity });
        }
        // full-height lines only for critical events (crash, WHEA, TDR, critical breach) unless they are too many to read;
        // warnings/info are short ticks along the top edge (hover the event list for details)
        let crit = 0; for (const c of cells.values()) if (c.sev === 'crit') crit++;
        for (const { px, sev } of cells.values()) {
          ctx.strokeStyle = sev === 'crit' ? '#e5484d' : sev === 'warn' ? '#f5a524' : '#6b7480';
          ctx.lineWidth = (sev === 'crit' ? 2 : 1) * dpr;
          ctx.setLineDash([]);
          const full = sev === 'crit' && crit <= width / (40 * dpr);
          ctx.beginPath(); ctx.moveTo(px, top); ctx.lineTo(px, top + (full ? height : 7 * dpr)); ctx.stroke();
        }
        ctx.setLineDash([]);
        // limit lines + points above warn
        const lims = getSeriesLimits ? getSeriesLimits() : [];
        lims.forEach((lim, i) => {
          const si = i + 1;
          if (!lim || !u.series[si] || !u.series[si].show) return;
          const sc = u.series[si].scale, smin = u.scales[sc].min, smax = u.scales[sc].max;
          let vmax = -Infinity, vmin = Infinity;
          for (const v of u.data[si] || []) if (v != null) { if (v > vmax) vmax = v; if (v < vmin) vmin = v; }
          // declutter: only show threshold lines for series that come within 15 % of them in this view
          const near = (t, hi) => t != null && (hi ? vmax >= t * 0.85 : vmin <= t * 1.15);
          const line = (v, color) => {
            if (v == null || v < smin || v > smax) return;
            const py = u.valToPos(v, sc, true);
            ctx.strokeStyle = color; ctx.lineWidth = dpr; ctx.setLineDash([6 * dpr, 4 * dpr]);
            ctx.beginPath(); ctx.moveTo(left, py); ctx.lineTo(left + width, py); ctx.stroke();
            ctx.setLineDash([]);
            ctx.fillStyle = color; ctx.font = `${10 * dpr}px system-ui`;
            ctx.fillText(`${u.series[si].label}: ${v}`, left + 4 * dpr, py - 3 * dpr);
          };
          if (near(lim.warn, true)) { line(lim.warn, 'rgba(245,165,36,0.8)'); line(lim.crit, 'rgba(229,72,77,0.9)'); }
          if (lim.kind === 'band' && near(lim.warn_lo, false)) { line(lim.warn_lo, 'rgba(245,165,36,0.8)'); line(lim.crit_lo, 'rgba(229,72,77,0.9)'); }
          const ys = u.data[si], xs = u.data[0];
          if (!ys) return;
          for (let j = 0; j < ys.length; j++) {
            const v = ys[j];
            if (v == null) continue;
            const crit = (lim.crit != null && v >= lim.crit) || (lim.crit_lo != null && lim.kind !== 'zero_while' && v <= lim.crit_lo);
            const warn = crit || (lim.warn != null && v >= lim.warn) || (lim.warn_lo != null && v <= lim.warn_lo);
            if (!warn) continue;
            ctx.fillStyle = crit ? '#e5484d' : '#f5a524';
            ctx.beginPath(); ctx.arc(x(xs[j]), u.valToPos(v, sc, true), 2.5 * dpr, 0, 7); ctx.fill();
          }
        });
        ctx.restore();
      }],
    },
  };
}

function selectHook(u) {
  const { left, width } = u.select;
  if (width < 4) return;
  const from = u.posToVal(left, 'x'), to = u.posToVal(left + width, 'x');
  u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
  state.sel = { from, to };
  for (const c of state.charts.values()) c.u && c.u.redraw(false, false);
  $('#zoomSel').disabled = $('#clearSel').disabled = false;
  loadPanel();
}

function baseOpts(title, height = 200) {
  return {
    width: chartWidth(), height, ms: 1,
    cursor: { sync: { key: 'bb', setSeries: false }, drag: { x: true, y: false, setScale: false }, points: { size: 5 } },
    select: { show: true },
    scales: { x: { time: true, range: () => [state.from, state.to] } },
    legend: { live: true },
    hooks: { setSelect: [selectHook] },
  };
}

// ---------- sensor charts ----------
function chartSensors(def) {
  if (def.custom) return state.custom.map(id => state.sensorById.get(id)).filter(Boolean);
  if (def.filter) return state.sensors.filter(s => def.filter(s) && s.tier !== 0);
  const out = [];
  for (const alts of def.roles) {
    for (const r of alts.split('|')) {
      const hit = state.sensors.filter(s => s.role === r && s.tier !== 0);
      if (hit.length) { out.push(...hit); break; }
    }
  }
  return out;
}

function chartBox(def) {
  let box = document.getElementById('c-' + def.id);
  if (!box) {
    box = document.createElement('div');
    box.className = 'chart'; box.id = 'c-' + def.id;
    box.innerHTML = `<h3>${esc(def.title)}</h3><div class="plot"></div>`;
    $('#charts').appendChild(box);
  }
  return box;
}

async function loadSensorChart(def) {
  const sensors = chartSensors(def);
  const box = chartBox(def);
  box.hidden = sensors.length === 0 && !def.custom;
  const plot = box.querySelector('.plot');
  const prev = state.charts.get(def.id);
  if (sensors.length === 0) {
    if (prev?.u) prev.u.destroy();
    state.charts.delete(def.id);
    plot.innerHTML = def.custom ? '<div class="empty">Pick sensors with “Sensors…” to build a custom chart.</div>' : '';
    return;
  }
  const maxPoints = Math.min(4000, Math.round(chartWidth() * 1.5));
  const ids = sensors.map(s => s.id).join(',');
  const d = await api(`/api/series?ids=${ids}&from=${state.from}&to=${state.to}&maxPoints=${maxPoints}`);
  const data = [d.t, ...d.series.map(s => s[state.agg])];
  const key = ids + '|' + state.agg;
  if (prev && prev.key === key && prev.u) { prev.u.setData(data, true); return; }
  if (prev?.u) prev.u.destroy();
  plot.innerHTML = '';

  const scaleOf = def.scaleOf || (s => s.unit);
  const units = [...new Set(sensors.map(scaleOf))];
  const opts = baseOpts(def.title);
  const lims = sensors.map(s => state.limitsBySensor.get(s.id));
  opts.plugins = [overlayPlugin(() => lims)];
  opts.series = [{}, ...sensors.map((s, i) => ({
    label: label(s), scale: scaleOf(s), stroke: PALETTE[i % PALETTE.length], width: 1.25,
    value: (u, v) => v == null ? '–' : `${fmt(v, s.unit === 'V' ? 3 : s.unit === 'RPM' || s.unit === 'MHz' ? 0 : 1)} ${s.unit}`,
  }))];
  opts.axes = [{ stroke: '#8a94a0', grid: { stroke: '#232a32' }, ticks: { stroke: '#2a3038' } },
    ...units.slice(0, 2).map((unit, i) => ({
      scale: unit, side: i === 0 ? 3 : 1, stroke: '#8a94a0', size: 56, label: unit, labelSize: 14,
      grid: { show: i === 0, stroke: '#232a32' }, ticks: { stroke: '#2a3038' },
    }))];
  opts.scales = { ...opts.scales };
  for (const unit of units) if (unit === '%') opts.scales[unit] = { range: [0, 100] };
  const u = new uPlot(opts, data, plot);
  state.charts.set(def.id, { u, key });
}

// ---------- process attribution (stacked) ----------
async function loadProcChart(def) {
  const box = chartBox(def), plot = box.querySelector('.plot');
  const d = await api(`/api/proc-series?from=${state.from}&to=${state.to}&top=8&maxPoints=${Math.round(chartWidth())}`);
  const raw = [d.t, ...d.series];
  const n = d.series.length;
  // cumulative stack, (other) at the bottom
  const order = [n - 1, ...Array.from({ length: n - 1 }, (_, i) => i)];
  const acc = new Array(d.t.length).fill(0);
  const stacked = [d.t];
  const origIdx = [];
  for (const k of order) {
    const col = d.series[k];
    stacked.push(col.map((v, j) => v == null ? null : (acc[j] += v)));
    origIdx.push(k);
  }
  const prev = state.charts.get(def.id);
  if (prev?.u) prev.u.destroy();
  plot.innerHTML = '';
  if (d.t.length === 0) { plot.innerHTML = '<div class="empty">No process samples in range.</div>'; state.charts.delete(def.id); return; }
  const opts = baseOpts(def.title, 220);
  opts.plugins = [overlayPlugin(null)];
  opts.series = [{}, ...origIdx.map((k, i) => {
    const color = k === n - 1 ? '#5b6470' : PALETTE[k % PALETTE.length];
    return {
      label: d.names[k], stroke: color, fill: color + 'aa', width: 0, scale: 'W', points: { show: false },
      value: (u, v, si, idx) => idx == null ? '–' : `${fmt(raw[k + 1][idx], 1)} W`,
    };
  })];
  opts.bands = origIdx.slice(1).map((_, i) => ({ series: [i + 2, i + 1] }));
  opts.axes = [{ stroke: '#8a94a0', grid: { stroke: '#232a32' } }, { scale: 'W', stroke: '#8a94a0', size: 56, label: 'est. W', labelSize: 14, grid: { stroke: '#232a32' } }];
  const u = new uPlot(opts, stacked, plot);
  state.charts.set(def.id, { u, key: 'procs' });
}

// ---------- overview strip ----------
async function loadOverview() {
  const now = Date.now(), from = now - 72 * 3600e3;
  const ids = ['cpu.tctl', 'gpu.hotspot', 'power.total'].map(r => state.sensors.find(s => s.role === r)).filter(Boolean);
  if (!ids.length) return;
  const d = await api(`/api/series?ids=${ids.map(s => s.id).join(',')}&from=${from}&to=${now}&maxPoints=${Math.round(chartWidth())}`);
  const data = [d.t, ...d.series.map(s => s.max)];
  if (state.overview) { state.overview.setData(data, true); return; }
  const opts = {
    width: chartWidth(), height: 90, ms: 1, legend: { show: false },
    cursor: { drag: { x: true, y: false, setScale: false }, points: { show: false } },
    scales: { x: { time: true, range: () => [Date.now() - 72 * 3600e3, Date.now()] } },
    series: [{}, ...ids.map((s, i) => ({ label: label(s), stroke: PALETTE[i], width: 1, scale: s.unit }))],
    axes: [{ stroke: '#8a94a0', grid: { show: false }, size: 28 }, { show: false, scale: '°C' }, { show: false, scale: 'W' }],
    plugins: [{ hooks: { draw: [u => {
      const ctx = u.ctx, { top, height, left, width } = u.bbox;
      const x0 = u.valToPos(state.from, 'x', true), x1 = u.valToPos(state.to, 'x', true);
      ctx.save();
      ctx.fillStyle = 'rgba(78,156,255,0.12)'; ctx.fillRect(x0, top, x1 - x0, height);
      for (const e of state.allEvents) {
        if (!['power_loss', 'kernel_power_41', 'bugcheck', 'whea', 'gpu_tdr', 'unexpected_shutdown'].includes(e.kind)) continue;
        const px = u.valToPos(e.ts, 'x', true);
        if (px < left || px > left + width) continue;
        ctx.fillStyle = e.severity === 'crit' ? '#e5484d' : '#f5a524';
        ctx.fillRect(px - 1, top, 2 * (devicePixelRatio || 1), height);
      }
      ctx.restore();
    }] } }],
    hooks: { setSelect: [u => {
      const { left, width } = u.select;
      if (width < 3) return;
      const f = u.posToVal(left, 'x'), t = u.posToVal(left + width, 'x');
      u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
      state.shutdown = null; state.sel = null;
      setRange(f, t, false);
    }] },
  };
  state.overview = new uPlot(opts, data, $('#overview'));
}

// ---------- side panel ----------
async function loadPanel() {
  const r = state.sel || { from: state.from, to: state.to };
  $('#selTitle').textContent = `${state.sel ? 'Selection' : state.shutdown ? 'Before shutdown' : 'Visible range'}: ${fmtTime(r.from)} → ${fmtTime(r.to, false)} (${fmtDur(r.to - r.from)})`;
  const q = `from=${Math.round(r.from)}&to=${Math.round(r.to)}`;
  const [procs, lims] = await Promise.all([api(`/api/processes?${q}`), api(`/api/limit-summary?${q}`)]);

  let h = '<tr><th>Process</th><th title="Estimated energy (CPU+GPU)">est. Wh</th><th>CPU avg</th><th>CPU pk</th><th>GPU avg</th><th>GPU pk</th><th>RAM MB</th></tr>';
  for (const p of procs.rows.slice(0, 25)) {
    h += `<tr title="${esc(p.path || '')}\nest. CPU ${fmt(p.cpu_wh, 2)} Wh, est. GPU ${fmt(p.gpu_wh, 2)} Wh, peak est. ${fmt(p.w_peak, 0)} W (single process)\nCPU/GPU avg = all instances combined; peak = single process">
      <td>${esc(p.name)}</td><td>${fmt(p.cpu_wh + p.gpu_wh, 2)}</td><td>${fmt(p.cpu_avg)}%</td><td>${fmt(p.cpu_peak)}%</td>
      <td>${fmt(p.gpu_avg)}%</td><td>${fmt(p.gpu_peak)}%</td><td>${fmt(p.ws_peak_mb, 0)}</td></tr>`;
  }
  $('#procTable').innerHTML = procs.rows.length ? h : '<tr><td class="muted">No process samples.</td></tr>';

  h = '<tr><th>Sensor</th><th>Peak</th><th>Warn</th><th>Crit</th><th>&gt; warn</th><th>Verdict</th></tr>';
  for (const l of lims) {
    const cls = 'v-' + l.verdict.replace(' ', '');
    h += `<tr title="${esc(l.source || '')}"><td>${esc(l.label)}</td><td>${fmt(l.peak, l.unit === 'V' ? 2 : l.unit === 'RPM' ? 0 : 1)} ${esc(l.unit)}</td>
      <td>${l.kind === 'band' ? `±${l.warn_pct}%` : fmt(l.warn, 0)}</td><td>${l.kind === 'band' ? `±${l.crit_pct}%` : l.kind === 'zero_while' ? 'stall' : fmt(l.crit, 0)}</td>
      <td>${fmtDur(l.warn_s * 1000)}</td><td class="${cls}">${esc(l.verdict)}</td></tr>`;
  }
  $('#limitTable').innerHTML = h;

  const evs = state.events.filter(e => e.ts >= r.from && e.ts <= r.to && e.kind !== 'limit_end').slice(-200).reverse();
  $('#eventList').innerHTML = evs.length ? evs.map(e => `<div class="ev ${esc(e.severity)}" data-ts="${e.ts}">
      <span class="k">${esc(e.kind)}</span> <span class="muted">${fmtTime(e.ts)}</span>
      ${e.kind === 'power_loss' ? `<a href="#" data-shutdown="${e.id}">last 60 s ▸</a>` : ''}
      <div class="m">${esc(e.message)}</div></div>`).join('') : '<div class="muted">No events.</div>';

  if (state.shutdown) loadFinals(); else $('#finals').hidden = true;
}

async function loadFinals() {
  const t1 = state.sensors.filter(s => s.tier === 1);
  const d = await api(`/api/series?ids=${t1.slice(0, 64).map(s => s.id).join(',')}&from=${state.from}&to=${state.to}&maxPoints=4000`);
  let h = '<tr><th>Sensor</th><th>Last</th><th>Min</th><th>Max</th><th>Limit</th></tr>';
  d.series.forEach(ser => {
    const s = state.sensorById.get(ser.id), vals = ser.avg.filter(v => v != null);
    if (!s || !vals.length) return;
    const lim = state.limitsBySensor.get(s.id);
    const last = vals[vals.length - 1], dec = s.unit === 'V' ? 3 : 1;
    const bad = lim && lim.kind === 'high' && ((lim.crit != null && Math.max(...ser.max.filter(v => v != null)) >= lim.crit) ? 'v-Crit'
      : (lim.warn != null && Math.max(...ser.max.filter(v => v != null)) >= lim.warn) ? 'v-Warn' : 'v-OK');
    h += `<tr><td>${esc(label(s))}</td><td>${fmt(last, dec)} ${esc(s.unit)}</td><td>${fmt(Math.min(...ser.min.filter(v => v != null)), dec)}</td>
      <td>${fmt(Math.max(...ser.max.filter(v => v != null)), dec)}</td><td class="${bad || ''}">${lim ? lim.kind === 'band' ? `${fmt(lim.warn_lo, 2)}–${fmt(lim.warn, 2)}` : `${fmt(lim.warn ?? lim.warn_lo, 0)} / ${fmt(lim.crit ?? lim.crit_lo, 0)}` : ''}</td></tr>`;
  });
  $('#finalsTable').innerHTML = h;
  $('#finals').hidden = false;
}

// ---------- events / jump ----------
async function loadEvents() {
  const now = Date.now();
  state.allEvents = await api(`/api/events?from=${now - 73 * 3600e3}&to=${now + 60e3}`);
  state.events = state.allEvents.filter(e => e.ts >= state.from - 60e3 && e.ts <= state.to + 60e3);
  const sel = $('#jump'), cur = sel.value;
  const items = state.allEvents.filter(e => JUMP_KINDS.has(e.kind)).slice(-300).reverse();
  sel.innerHTML = '<option value="">Jump to event…</option>' + items.map(e =>
    `<option value="${e.id}">${fmtTime(e.ts)} · ${esc(e.kind)}${e.kind === 'power_loss' ? ' (last 60 s view)' : ''} · ${esc((e.message || '').slice(0, 70))}</option>`).join('');
  sel.value = cur;
}

function showShutdown(ev) {
  state.shutdown = ev; state.sel = null;
  const b = $('#banner');
  b.hidden = false;
  b.innerHTML = `<b>Last 60 s before shutdown</b> — recording stopped at ${fmtTime(ev.ts)}; gap ${fmt(ev.value, 0)} s. ` +
    `All tier-1 sensors are at full 1 Hz resolution.<button id="exitShutdown">Back to live</button>`;
  $('#exitShutdown').onclick = () => { state.shutdown = null; b.hidden = true; presetRange(); setRange(state.from, state.to, true); };
  setRange(ev.ts - 60e3, ev.ts + 5e3, false);
}

function jumpTo(ev) {
  if (ev.kind === 'power_loss') return showShutdown(ev);
  state.shutdown = null; $('#banner').hidden = true;
  state.sel = null;
  setRange(ev.ts - 5 * 60e3, ev.ts + 2 * 60e3, false);
}

// ---------- status ----------
async function loadStatus() {
  try {
    const s = await api('/api/status');
    const bad = (v, lim) => v != null && v > lim ? ' class="bad"' : '';
    $('#status').innerHTML = `${s.simulate ? '<b>SIMULATED</b> · ' : ''}svc CPU <span${bad(s.cpu_pct_avg, s.cpu_budget_pct)}>${fmt(s.cpu_pct_avg, 2)}%</span>` +
      ` · <span${bad(s.private_mb, s.private_budget_mb)}>${fmt(s.private_mb, 0)} MB</span>` +
      ` · DB <span${bad(s.db_projected_mb, s.db_budget_mb)}>${fmt(s.db_mb, 0)} MB${s.db_projected_mb ? ` (→ ${fmt(s.db_projected_mb, 0)} MB/${s.retention_h} h)` : ''}</span>` +
      ` · lag ${s.sample_lag_ms != null ? fmt(s.sample_lag_ms / 1000, 1) + ' s' : '–'} · ${esc(s.driver)}`;
  } catch (e) { $('#status').innerHTML = '<span class="bad">service unreachable</span>'; }
}

// ---------- main refresh ----------
async function refresh() {
  if (state.loading) { state.pending = true; return; }
  state.loading = true;
  try {
    if (state.live) presetRange();
    $('#rangeLabel').textContent = `${fmtTime(state.from)} → ${fmtTime(state.to)} (${fmtDur(state.to - state.from)})${state.live ? ' · live' : ''}`;
    await loadEvents();
    await Promise.all(CHARTS.map(def => def.procs ? loadProcChart(def) : loadSensorChart(def)));
    await Promise.all([loadPanel(), loadOverview()]);
    state.overview && state.overview.redraw(false, false);
  } catch (e) {
    console.error(e);
  } finally {
    state.loading = false;
    if (state.pending) { state.pending = false; refresh(); }
  }
  schedule();
}

function schedule() {
  clearTimeout(state.timer);
  // live mode only, and never while the tab is hidden
  if (state.live && !document.hidden && state.tab === 'rec') state.timer = setTimeout(refresh, state.dur <= 3600e3 ? 5000 : 30000);
}

async function loadMeta() {
  const [sensors, limits] = await Promise.all([api('/api/sensors'), api('/api/limits')]);
  state.sensors = sensors;
  state.sensorById = new Map(sensors.map(s => [s.id, s]));
  state.limitsBySensor = new Map(limits.map(l => [l.sensor_id, l]));
}

// ---------- sensor picker ----------
function renderPicker() {
  const f = $('#pickFilter').value.toLowerCase();
  const byHw = new Map();
  for (const s of state.sensors) {
    if (f && !(`${s.hw} ${s.name} ${s.type}`.toLowerCase().includes(f))) continue;
    if (!byHw.has(s.hw)) byHw.set(s.hw, []);
    byHw.get(s.hw).push(s);
  }
  $('#pickList').innerHTML = [...byHw].map(([hw, list]) => `<div class="hw"><b>${esc(hw)}</b>${list.map(s =>
    `<label class="${s.present ? '' : 'gone'}"><input type="checkbox" value="${s.id}" ${state.custom.includes(s.id) ? 'checked' : ''}>
     ${esc(s.name)} <span class="muted">${esc(s.type)}${s.last != null ? ' · ' + fmt(s.last, 2) + ' ' + esc(s.unit) : ''}${s.tier === 0 ? ' · not stored' : s.tier === 2 ? ' · tier 2' : ''}</span></label>`).join('')}</div>`).join('');
}

// ---------- wiring ----------
function wire() {
  for (const b of document.querySelectorAll('#presets button')) {
    b.classList.toggle('on', +b.dataset.dur === state.dur);
    b.onclick = () => {
      state.dur = +b.dataset.dur; store.set('bb.dur', state.dur);
      document.querySelectorAll('#presets button').forEach(x => x.classList.toggle('on', x === b));
      state.sel = null; state.shutdown = null; $('#banner').hidden = true;
      $('#zoomSel').disabled = $('#clearSel').disabled = true;
      presetRange(); setRange(state.from, state.to, true);
    };
  }
  for (const b of document.querySelectorAll('#agg button')) {
    b.classList.toggle('on', b.dataset.agg === state.agg);
    b.onclick = () => {
      state.agg = b.dataset.agg; store.set('bb.agg', state.agg);
      document.querySelectorAll('#agg button').forEach(x => x.classList.toggle('on', x === b));
      refresh();
    };
  }
  $('#live').onchange = e => { state.live = e.target.checked; if (state.live) { state.dur = Math.max(state.dur, 15 * 60e3); refresh(); } else schedule(); };
  $('#jump').onchange = e => { const ev = state.allEvents.find(x => x.id === +e.target.value); if (ev) jumpTo(ev); };
  $('#zoomSel').onclick = () => { if (!state.sel) return; const s = state.sel; state.sel = null; $('#zoomSel').disabled = $('#clearSel').disabled = true; setRange(s.from, s.to, false); };
  $('#clearSel').onclick = () => { state.sel = null; $('#zoomSel').disabled = $('#clearSel').disabled = true; for (const c of state.charts.values()) c.u?.redraw(false, false); loadPanel(); };
  $('#export').onclick = () => {
    const r = state.sel || state.from && { from: state.from, to: state.to };
    const ids = new Set();
    for (const def of CHARTS) if (!def.procs) chartSensors(def).forEach(s => ids.add(s.id));
    location.href = `/api/export.csv?ids=${[...ids].join(',')}&from=${Math.round(r.from)}&to=${Math.round(r.to)}`;
  };
  $('#pick').onclick = () => { renderPicker(); $('#picker').showModal(); };
  $('#pickFilter').oninput = renderPicker;
  $('#pickList').onchange = e => {
    const id = +e.target.value;
    state.custom = e.target.checked ? [...new Set([...state.custom, id])] : state.custom.filter(x => x !== id);
    store.set('bb.custom', state.custom);
  };
  $('#pickClear').onclick = () => { state.custom = []; store.set('bb.custom', []); renderPicker(); };
  $('#picker').addEventListener('close', () => loadSensorChart(CHARTS.find(c => c.custom)));
  $('#eventList').onclick = e => {
    const a = e.target.closest('[data-shutdown]');
    if (a) { e.preventDefault(); const ev = state.allEvents.find(x => x.id === +a.dataset.shutdown); if (ev) showShutdown(ev); return; }
    const row = e.target.closest('.ev');
    if (row) { const ts = +row.dataset.ts; state.sel = { from: ts - 30e3, to: ts + 30e3 }; for (const c of state.charts.values()) c.u?.redraw(false, false); $('#zoomSel').disabled = $('#clearSel').disabled = false; loadPanel(); }
  };
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { if (state.tab === 'rec') refresh(); loadStatus(); } else clearTimeout(state.timer); });
  let rt;
  addEventListener('resize', () => {
    clearTimeout(rt);
    rt = setTimeout(() => {
      const w = chartWidth();
      for (const c of state.charts.values()) c.u?.setSize({ width: w, height: c.u.height });
      state.overview?.setSize({ width: w, height: 90 });
    }, 150);
  });
  setInterval(() => { if (!document.hidden) loadStatus(); }, 15000);
}

(async function main() {
  wire();
  await loadMeta();
  presetRange();
  loadStatus();
  await refresh();
  setInterval(() => { if (!document.hidden) loadMeta(); }, 5 * 60e3);
})();
