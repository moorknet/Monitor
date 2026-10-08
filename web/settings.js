'use strict';
// Settings tab: edits are collected in `pend` and sent in one POST; the server validates, applies live and writes config.json.

const set = { data: null, pend: null, timer: 0, now: null, open: false };
const resetPend = () => { set.pend = { electricity: null, sensors: {}, limits: {}, general: null }; };
resetPend();

const AREAS = { SE1: 'SE1 – Luleå', SE2: 'SE2 – Sundsvall', SE3: 'SE3 – Stockholm', SE4: 'SE4 – Malmö' };
const TIERS = [[1, 'Every second'], [2, 'Every 10 s'], [0, 'Off']];
const ROLE_LABEL = {
  'cpu.tctl': 'CPU temperature', 'cpu.ccd': 'CPU CCD temp', 'cpu.package_power': 'CPU power (cost)', 'cpu.load': 'CPU load',
  'cpu.core_clock': 'core clock → max/avg', 'cpu.core_load': 'core load → max', 'cpu.clock_max': 'CPU clock max', 'cpu.clock_avg': 'CPU clock avg',
  'cpu.core_load_max': 'CPU core load max', 'mb.vcore': 'Vcore', 'mb.vsoc': 'VSOC', 'mb.12v': '+12 V rail',
  'gpu.board_power': 'GPU power (cost)', 'gpu.edge': 'GPU edge temp', 'gpu.hotspot': 'GPU hotspot', 'gpu.mem_temp': 'GPU memory temp',
  'gpu.clock': 'GPU clock', 'gpu.load': 'GPU load', 'mb.vrm': 'VRM temp', 'mb.chipset': 'Chipset temp', 'fan': 'fan chart',
  'fan.cpu': 'CPU fan (stall check)', 'nvme.temp': 'NVMe temp', 'dimm.temp': 'RAM temp', 'power.total': 'total power',
};
const roleName = r => ROLE_LABEL[r] || r;

// readings that are physically implausible for this kind of sensor → probably a misreporting sensor
function implausible(s) {
  const v = s.last;
  if (v == null) return null;
  switch (s.type) {
    case 'Temperature': return v < -5 || v > 125 ? 'temperature outside −5…125 °C' : v === 0 ? 'reads exactly 0 °C' : null;
    case 'Voltage': return v < 0 || v > 20 ? 'voltage outside 0…20 V' : s.role === 'mb.12v' && (v < 10 || v > 14) ? '+12 V rail reads far from 12 V' : null;
    case 'Fan': return v < 0 || v > 10000 ? 'fan speed outside 0…10 000 RPM' : null;
    case 'Power': return v < 0 || v > 1500 ? 'power outside 0…1500 W' : null;
    case 'Clock': return v < 0 || v > 10000 ? 'clock outside 0…10 000 MHz' : null;
    case 'Load': case 'Control': return v < 0 || v > 100.5 ? 'percentage outside 0…100' : null;
    default: return null;
  }
}

function field(id, label, input, help = '') {
  return `<div class="field" id="f-${id}"><label for="${id}">${label}</label>${input}${help ? `<div class="help">${help}</div>` : ''}</div>`;
}
const num = (id, v, min, max, step) => `<input type="number" id="${id}" value="${v ?? ''}" min="${min}" max="${max}" step="${step}">`;
const sel = (id, opts, v) => `<select id="${id}">${opts.map(([k, l]) => `<option value="${k}"${String(k) === String(v) ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>`;

// ---------- electricity ----------
function renderElec() {
  const e = set.data.electricity, cur = e.currency === 'EUR' ? '€' : 'kr';
  const prov = [['elprisetjustnu', 'Nord Pool spot price (Sweden, elprisetjustnu.se)'], ['fixed', 'Fixed price per kWh']];
  if (e.provider === 'sim') prov.push(['sim', 'Simulated prices (dev)']);
  $('#setElec').innerHTML =
    field('eProv', 'Price source', sel('eProv', prov, e.provider)) +
    field('eArea', 'Price area', sel('eArea', Object.entries(AREAS), e.area), 'Your elområde — shown on your electricity bill.') +
    field('eCur', 'Currency', sel('eCur', [['SEK', 'SEK (kr, prices in öre)'], ['EUR', 'EUR']], e.currency)) +
    field('eSur', `Surcharge per kWh (${cur}, excl. VAT)`, num('eSur', e.surcharge_per_kwh, 0, 20, 0.01),
      'Grid transfer fee + energy tax + supplier markup per kWh, from your bill. 0 = spot price only.') +
    field('eVat', 'VAT %', num('eVat', e.vat_pct, 0, 50, 1)) +
    field('eFix', `Fixed price per kWh (${cur}, all-inclusive)`, num('eFix', e.fixed_price_per_kwh, 0, 50, 0.01), 'Used only with “Fixed price”.') +
    field('eBase', 'Rest of system (W)', num('eBase', e.base_load_w, 0, 500, 1),
      'Board, RAM, SSDs, fans, pump — everything the CPU/GPU sensors don’t cover. ~40–80 W for a gaming PC at the wall.') +
    field('eEff', 'PSU efficiency (%)', num('eEff', Math.round(e.psu_efficiency * 100), 50, 100, 1), '80+ Bronze ≈ 85 %, Gold ≈ 90 %, Platinum ≈ 92 %, Titanium ≈ 94 % at typical load (see the PSU label).');
  const sync = () => {
    const p = $('#eProv').value;
    for (const id of ['eArea', 'eCur', 'eSur', 'eVat']) $('#f-' + id).hidden = p === 'fixed';
    $('#f-eFix').hidden = p !== 'fixed';
  };
  sync();
  $('#setElec').oninput = () => {
    sync();
    set.pend.electricity = {
      provider: $('#eProv').value, area: $('#eArea').value, currency: $('#eCur').value,
      surcharge_per_kwh: +$('#eSur').value, vat_pct: +$('#eVat').value, fixed_price_per_kwh: +$('#eFix').value,
      base_load_w: +$('#eBase').value, psu_efficiency: +$('#eEff').value / 100,
    };
    markDirty(); previewElec();
  };
  previewElec();
}

function previewElec() {
  const e = { ...set.data.electricity, ...(set.pend.electricity || {}) };
  const sek = e.currency !== 'EUR', unit = sek ? 'öre/kWh' : '€/kWh', k = sek ? 100 : 1;
  const total = spot => e.provider === 'fixed' ? e.fixed_price_per_kwh : (spot + e.surcharge_per_kwh) * (1 + e.vat_pct / 100);
  let html = e.provider === 'fixed'
    ? `You pay <b>${fmt(e.fixed_price_per_kwh * k, sek ? 0 : 3)} ${unit}</b> at all times.`
    : `Example: a spot price of ${sek ? '50 öre' : '0.05 €'} becomes <b>${fmt(total(0.5 / (sek ? 1 : 10)) * k, sek ? 0 : 3)} ${unit}</b> with your surcharge and VAT.`;
  const n = set.now;
  if (n && n.cpu_w != null) {
    const wall = (n.cpu_w + n.gpu_w + e.base_load_w) / e.psu_efficiency;
    html += ` Right now: CPU ${fmt(n.cpu_w, 0)} W + GPU ${fmt(n.gpu_w, 0)} W + ${fmt(e.base_load_w, 0)} W rest ÷ ${fmt(e.psu_efficiency * 100, 0)} % ≈ <b>${fmt(wall, 0)} W at the wall</b>` +
      (n.spot != null || e.provider === 'fixed' ? ` ≈ <b>${fmt(wall / 1000 * total(n.spot ?? 0), 2)} ${e.currency === 'EUR' ? '€' : 'kr'}/h</b>.` : '.');
  }
  html += ' Changing these re-prices your whole history (energy is stored as CPU/GPU watt-hours).';
  $('#setElecPreview').innerHTML = html;
}

// ---------- sensors ----------
function effTier(s) { return s.identifier in set.pend.sensors ? (set.pend.sensors[s.identifier] ?? s.default_tier) : s.tier; }

function renderSensors() {
  const f = $('#setFilter').value.toLowerCase(), onlyChanged = $('#setOnlyChanged').checked, onlyFlagged = $('#setOnlyFlagged').checked;
  let h = '<tr><th>Sensor</th><th>Type</th><th>Live</th><th>Used as</th><th>Recording</th><th></th></tr>', lastHw = null;
  for (const s of set.data.sensors) {
    const t = effTier(s), flag = implausible(s);
    if (f && !`${s.hw} ${s.name} ${s.type} ${s.role || ''}`.toLowerCase().includes(f)) continue;
    if (onlyChanged && t === s.default_tier && !s.user) continue;
    if (onlyFlagged && !flag) continue;
    if (s.hw !== lastHw) { h += `<tr class="hwrow"><td colspan="6">${esc(s.hw)}</td></tr>`; lastHw = s.hw; }
    const dirty = s.identifier in set.pend.sensors, role = s.role ? esc(roleName(s.role)) : (t === 0 && s.default_role ? `<s>${esc(roleName(s.default_role))}</s>` : '');
    h += `<tr class="${dirty ? 'dirty' : ''} ${t === 0 ? 'off' : ''}" data-ident="${esc(s.identifier)}">
      <td>${esc(s.name)}${flag ? ` <span class="flag" title="${esc(flag)}">⚠</span>` : ''}</td><td>${esc(s.type)}</td>
      <td class="live">${s.last == null ? '–' : `${fmt(s.last, s.unit === 'V' ? 3 : 1)} ${esc(s.unit)}`}</td>
      <td class="muted" title="${esc(s.role || s.default_role || '')}">${role}</td>
      <td>${sel('t-' + s.identifier, TIERS.map(([k, l]) => [k, l + (k === s.default_tier ? ' (default)' : '')]), t)}</td>
      <td>${s.user || dirty ? '<button class="linkbtn" data-reset="1" title="Back to default">↺</button>' : ''}</td></tr>`;
  }
  $('#setSensors').innerHTML = h;
}

// ---------- limits ----------
function renderLimits() {
  let h = '<tr><th>Sensor</th><th>Live</th><th>Warn</th><th>Crit</th><th>Default</th><th></th></tr>';
  for (const l of set.data.limits) {
    if (!l.role) continue;
    const p = set.pend.limits[l.role] || {}, d = l.default || {};
    const v = k => p[k] ?? l[k];
    let warn, crit, def;
    if (l.kind === 'band') {
      warn = `±${num(`lw-${l.role}`, v('warn_pct'), 0.5, 50, 0.5)} %`; crit = `±${num(`lc-${l.role}`, v('crit_pct'), 0.5, 50, 0.5)} %`;
      def = `±${d.warn_pct ?? '–'} / ±${d.crit_pct ?? '–'} %`;
    } else if (l.kind === 'zero_while') {
      warn = '–'; crit = `0 RPM while CPU &gt; ${num(`lc-${l.role}`, v('while_above'), 20, 100, 1)} °C`;
      def = `CPU &gt; ${d.while_above ?? '–'} °C`;
    } else {
      warn = num(`lw-${l.role}`, v('warn'), -50, 2000, 1); crit = num(`lc-${l.role}`, v('crit'), -50, 2000, 1);
      def = `${d.warn ?? '–'} / ${d.crit ?? '–'}`;
    }
    const dirty = l.role in set.pend.limits;
    h += `<tr class="lim ${dirty ? 'dirty' : ''}" data-role="${esc(l.role)}" data-kind="${l.kind}" title="${esc(l.source || '')}">
      <td>${esc(l.label)} <span class="muted">${esc(l.unit)}</span></td><td>${l.last == null ? '–' : fmt(l.last, l.unit === 'V' ? 2 : 1)}</td>
      <td>${warn}</td><td>${crit}</td><td class="muted">${def}${d.profile ? ` · ${esc(d.profile)}` : ''}</td>
      <td>${l.overridden || dirty ? '<button class="linkbtn" data-lreset="1" title="Back to profile default">↺</button>' : ''}</td></tr>`;
  }
  $('#setLimits').innerHTML = h;
}

// ---------- general ----------
function renderGeneral() {
  const d = set.data, g = set.pend.general || {};
  $('#setGeneral').innerHTML =
    field('gRet', 'Keep detailed history (hours)', num('gRet', g.retention_hours ?? d.retention_hours, 12, 336, 1),
      'Per-second sensor and process data. Energy/cost history is kept separately for 2 years. Longer = bigger database.') +
    field('gUpd', 'Automatic update check', sel('gUpd', [['true', 'On'], ['false', 'Off']], String(g.update?.enabled ?? d.update.enabled))) +
    field('gUpdH', 'Check every (hours)', num('gUpdH', g.update?.check_hours ?? d.update.check_hours, 1, 168, 1));
  $('#setGeneral').oninput = () => {
    set.pend.general = { retention_hours: +$('#gRet').value, update: { enabled: $('#gUpd').value === 'true', check_hours: +$('#gUpdH').value } };
    markDirty();
  };
  $('#setPath').innerHTML = `Saved to <code>${esc(d.config_path)}</code> · BlackBox ${esc(d.update.current)} · changes apply immediately, no restart needed.`;
}

// ---------- save bar ----------
function pendingCount() {
  const p = set.pend;
  return (p.electricity ? 1 : 0) + Object.keys(p.sensors).length + Object.keys(p.limits).length + (p.general ? 1 : 0);
}
function markDirty(msg) {
  const n = pendingCount(), bar = $('#saveBar');
  bar.className = 'savebar';
  bar.hidden = n === 0 && !msg;
  $('#saveMsg').innerHTML = msg || `${n} unsaved change${n === 1 ? '' : 's'}`;
  $('#saveBtn').hidden = $('#discardBtn').hidden = n === 0;
}

async function save() {
  const p = set.pend, body = {};
  if (p.electricity) body.electricity = p.electricity;
  if (p.general) { body.retention_hours = p.general.retention_hours; body.update = p.general.update; }
  if (Object.keys(p.sensors).length) body.sensors = p.sensors;
  const lims = Object.entries(p.limits).map(([role, v]) => ({ role, ...v }));
  if (lims.length) body.limits = lims;
  $('#saveBtn').disabled = true; $('#saveMsg').textContent = 'Saving…';
  try {
    const r = await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-BlackBox': '1' }, body: JSON.stringify(body) });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(j.error || `HTTP ${r.status}`);
    set.data = j.settings; resetPend(); renderAll();
    markDirty('✔ Saved and applied.');
    $('#saveBar').className = 'savebar ok';
    setTimeout(() => { if (!pendingCount()) $('#saveBar').hidden = true; }, 3000);
    loadMeta?.();   // recorder charts pick up sensor/limit changes
  } catch (e) {
    markDirty(`✖ Not saved: ${esc(e.message)}`);
    $('#saveBar').className = 'savebar bad';
  } finally { $('#saveBtn').disabled = false; }
}

function renderAll() { renderElec(); renderSensors(); renderLimits(); renderGeneral(); }

async function loadSettings(full) {
  try {
    const [d, sum] = await Promise.all([api('/api/settings'), api('/api/cost/summary').catch(() => null)]);
    if (sum) set.now = { cpu_w: sum.now.cpu_w, gpu_w: sum.now.gpu_w, spot: null };
    if (sum && sum.now.price != null && sum.settings.provider !== 'fixed')   // back out spot from the current total price
      set.now.spot = sum.now.price / (1 + sum.settings.vat_pct / 100) - sum.settings.surcharge_per_kwh;
    if (full || !set.data) { set.data = d; renderAll(); }
    else {
      // live refresh: only touch value cells so inputs keep focus
      set.data.sensors = d.sensors; set.data.limits = d.limits;
      const by = new Map(d.sensors.map(s => [s.identifier, s]));
      for (const tr of document.querySelectorAll('#setSensors tr[data-ident]')) {
        const s = by.get(tr.dataset.ident);
        if (s) tr.querySelector('.live').textContent = s.last == null ? '–' : `${fmt(s.last, s.unit === 'V' ? 3 : 1)} ${s.unit}`;
      }
      previewElec();
    }
  } catch (e) {
    $('#setElec').innerHTML = `<div class="cost-err"><b>Loading settings failed:</b> ${esc(e.message)}</div>`;
  }
  clearTimeout(set.timer);
  if (set.open && !document.hidden) set.timer = setTimeout(() => loadSettings(false), 5000);
}

function settingsTab(open) {
  set.open = open;
  clearTimeout(set.timer);
  if (open) loadSettings(!pendingCount());
}

// ---------- events ----------
$('#setSensors').addEventListener('change', e => {
  const tr = e.target.closest('tr[data-ident]');
  if (!tr || e.target.tagName !== 'SELECT') return;
  const s = set.data.sensors.find(x => x.identifier === tr.dataset.ident), t = +e.target.value;
  if (t === s.tier) delete set.pend.sensors[s.identifier]; else set.pend.sensors[s.identifier] = t === s.default_tier ? null : t;
  renderSensors(); markDirty();
});
$('#setSensors').addEventListener('click', e => {
  if (!e.target.dataset.reset) return;
  const s = set.data.sensors.find(x => x.identifier === e.target.closest('tr').dataset.ident);
  if (s.user) set.pend.sensors[s.identifier] = null; else delete set.pend.sensors[s.identifier];
  renderSensors(); markDirty();
});
for (const id of ['#setFilter', '#setOnlyChanged', '#setOnlyFlagged']) $(id).addEventListener('input', renderSensors);
$('#setLimits').addEventListener('input', e => {
  const tr = e.target.closest('tr[data-role]');
  if (!tr) return;
  const role = tr.dataset.role, kind = tr.dataset.kind, w = tr.querySelector(`[id="lw-${role}"]`), c = tr.querySelector(`[id="lc-${role}"]`);
  set.pend.limits[role] = kind === 'band' ? { warn_pct: +w.value, crit_pct: +c.value }
    : kind === 'zero_while' ? { while_above: +c.value } : { warn: +w.value, crit: +c.value };
  tr.classList.add('dirty'); markDirty();
});
$('#setLimits').addEventListener('click', e => {
  if (!e.target.dataset.lreset) return;
  const role = e.target.closest('tr').dataset.role, l = set.data.limits.find(x => x.role === role);
  if (l.overridden) set.pend.limits[role] = { reset: true }; else delete set.pend.limits[role];
  renderLimits(); markDirty();
});
$('#saveBtn').onclick = save;
$('#discardBtn').onclick = () => { resetPend(); renderAll(); markDirty(); };
addEventListener('beforeunload', e => { if (pendingCount()) { e.preventDefault(); e.returnValue = ''; } });
document.addEventListener('visibilitychange', () => { if (!document.hidden && set.open) loadSettings(false); else clearTimeout(set.timer); });
