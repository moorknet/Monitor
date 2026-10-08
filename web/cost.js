'use strict';
// "Power & cost" tab. Uses helpers from app.js (api, fmt, esc, state, store). Refreshes only while the tab is visible.

const cost = { charts: {}, timer: 0, data: null };
const ACCENT = '#4e9cff', ACCENT2 = '#3ddc97';

function cur(s) { return { SEK: 'kr', NOK: 'kr', DKK: 'kr', EUR: '€' }[s.currency] || s.currency; }
// Swedish/Nordic convention: spot prices in öre (1/100 kr) per kWh
function priceTxt(v, s) {
  if (v == null) return '–';
  return ['SEK', 'NOK', 'DKK'].includes(s.currency) ? `${fmt(v * 100, 0)} öre/kWh` : `${fmt(v, 3)} ${cur(s)}/kWh`;
}
const money = (v, s, d = 2) => v == null ? '–' : `${fmt(v, d)} ${cur(s)}`;
const hhmm = ms => { const d = new Date(ms); return `${pad(d.getHours())}:${pad(d.getMinutes())}`; };
const costWidth = () => Math.max(300, document.querySelector('#tab-cost .cost-grid > div').clientWidth - 14);

function tile(k, v, unit, sub) {
  return `<div class="tile"><div class="k">${esc(k)}</div><div class="v">${v}<small>${esc(unit || '')}</small></div><div class="s">${sub || '&nbsp;'}</div></div>`;
}

function renderTiles(d) {
  const s = d.settings, n = d.now, p = d.periods;
  const rank = n.price_rank == null ? '' : n.price_rank < 0.25 ? 'one of today’s cheapest' : n.price_rank > 0.75 ? 'one of today’s priciest' : 'about average for today';
  const perYear = p.days30.hours > 24 ? p.days30.cost * 365 / 30 : null;
  $('#tiles').innerHTML = [
    tile('Drawing now (est. at wall)', fmt(n.wall_w, 0), 'W', n.cpu_w != null ? `CPU ${fmt(n.cpu_w, 0)} W · GPU ${fmt(n.gpu_w, 0)} W · rest ~${fmt(s.base_load_w, 0)} W` : 'no live reading'),
    tile('Price now', priceTxt(n.price, s).split(' ')[0], priceTxt(n.price, s).split(' ').slice(1).join(' '), rank),
    tile('Running cost now', fmt(n.cost_per_hour, 2), `${cur(s)}/h`, n.cost_per_hour != null ? `≈ ${money(n.cost_per_hour * 24, s, 1)} if left like this for a day` : ''),
    tile('Today', fmt(p.today.cost, 2), cur(s), `${fmt(p.today.kwh, 2)} kWh · avg ${fmt(p.today.avg_w, 0)} W`),
    tile('This month', fmt(p.month.cost, 2), cur(s), `${fmt(p.month.kwh, 1)} kWh over ${fmt(p.month.hours, 0)} h powered on`),
    tile('Last 30 days', fmt(p.days30.cost, 0), cur(s), perYear != null ? `at this pace ≈ ${money(perYear, s, 0)} / year` : `${fmt(p.days30.kwh, 1)} kWh`),
  ].join('');
}

function renderFacts(d) {
  const s = d.settings, p = d.periods, m = p.month, facts = [];
  const add = (icon, html) => facts.push(`<li><span class="i">${icon}</span><span>${html}</span></li>`);
  const top = d.top_processes_month?.[0];
  if (top && top.cost > 0) add('🎮', `<b>${esc(top.name)}</b> has cost you <b>${money(top.cost, s)}</b> this month (${fmt(top.kwh, 1)} kWh over ${top.active_hours} h).`);
  if (d.cheapest_3h && d.now.price != null) {
    const c = d.cheapest_3h, pct = Math.round((1 - c.avg_price / d.now.price) * 100);
    const when = new Date(c.from).getDate() !== new Date().getDate() ? 'tomorrow ' : '';
    add('⏰', `Cheapest 3 h to play next: <b>${when}${hhmm(c.from)}–${hhmm(c.to)}</b> at ${priceTxt(c.avg_price, s)}${pct > 0 ? ` — ${pct}% cheaper than right now` : ' — now is already about as cheap as it gets'}.`);
  }
  if (d.savings_today > 0.01) add('💸', `If all of today’s use had been at today’s cheapest price, you’d have saved <b>${money(d.savings_today, s)}</b>.`);
  if (m.kwh > 0) {
    add('☕', `This month’s electricity could boil <b>${fmt(m.kwh / 0.11, 0)} litres</b> of water for coffee.`);
    add('🚗', `…or drive an electric car about <b>${fmt(m.kwh / 0.18, 0)} km</b>.`);
    add('📱', `…or charge a phone <b>${fmt(m.kwh * 1000 / 15, 0)} times</b>.`);
    const gpuShare = m.gpu_kwh / m.kwh * 100, cpuShare = m.cpu_kwh / m.kwh * 100;
    add('⚡', `The GPU accounts for <b>${fmt(gpuShare, 0)}%</b> of the energy and the CPU <b>${fmt(cpuShare, 0)}%</b>; the remaining ${fmt(100 - gpuShare - cpuShare, 0)}% is the rest of the system and PSU losses.`);
  }
  if (d.record_hour) add('🔥', `Priciest hour on record: <b>${fmtTime(d.record_hour.ts).slice(0, 13)}:00</b> — ${money(d.record_hour.cost, s)} for ${fmt(d.record_hour.kwh, 2)} kWh.`);
  if (d.record_day) add('📅', `Biggest day: <b>${esc(d.record_day.day)}</b> with ${fmt(d.record_day.kwh, 1)} kWh.`);
  if (p.all.hours > 0) add('🕒', `Tracked ${fmt(p.all.hours, 0)} powered-on hours since ${fmtTime(d.tracking_since).slice(0, 10)}, averaging ${fmt(p.all.avg_w, 0)} W.`);
  $('#facts').innerHTML = facts.join('') || '<li class="muted">Not enough data yet — check back after a gaming session.</li>';
}

function renderTables(d) {
  const s = d.settings;
  $('#costProcs').innerHTML = '<tr><th>Process</th><th>kWh</th><th>Cost</th><th>Hours</th></tr>' +
    (d.top_processes_month.map(p => `<tr><td>${esc(p.name)}</td><td>${fmt(p.kwh, 2)}</td><td>${money(p.cost, s)}</td><td>${p.active_hours}</td></tr>`).join('')
      || '<tr><td class="muted" colspan="4">No process data yet.</td></tr>');
  const names = { today: 'Today', yesterday: 'Yesterday', week: 'Last 7 days', month: 'This month', days30: 'Last 30 days', all: 'All recorded' };
  $('#periods').innerHTML = '<tr><th>Period</th><th>kWh</th><th>Cost</th><th>Avg price</th><th>Avg W</th></tr>' +
    Object.entries(names).map(([k, label]) => {
      const p = d.periods[k];
      return `<tr title="${p.unpriced_kwh > 0 ? `${fmt(p.unpriced_kwh, 2)} kWh has no price yet and is not in the cost` : ''}"><td>${label}${p.unpriced_kwh > 0 ? ' *' : ''}</td><td>${fmt(p.kwh, 2)}</td><td>${money(p.cost, s)}</td><td>${priceTxt(p.avg_price, s)}</td><td>${fmt(p.avg_w, 0)}</td></tr>`;
    }).join('');
  const priceDesc = s.provider === 'fixed' ? `a fixed ${money(s.fixed_price_per_kwh, s)}/kWh`
    : `(${s.provider === 'sim' ? 'SIMULATED spot' : `Nord Pool spot ${esc(s.area)}`} + ${fmt(s.surcharge_per_kwh, 2)} ${cur(s)} surcharge) × ${fmt(1 + s.vat_pct / 100, 2)} VAT`;
  $('#method').innerHTML = `All figures are estimates. Wall power ≈ (CPU package + GPU board + ${fmt(s.base_load_w, 0)} W rest of system) ÷ ${fmt(s.psu_efficiency * 100, 0)}% PSU efficiency. ` +
    `Price = ${priceDesc}.` + (s.provider !== 'fixed' && !s.surcharge_per_kwh ? ' <b>Tip:</b> add your grid fee + energy tax as <code>electricity.surcharge_per_kwh</code> in config.json to see your real bill.' : '') +
    ` Process costs use the attributed CPU/GPU share only.` + (d.price_status.error ? ` <span class="bad">Price fetch error: ${esc(d.price_status.error)}</span>` : '');
}

function barsOpts(title, height, fmtX, s, valueFmt) {
  return {
    width: costWidth(), height, ms: 1,
    cursor: { points: { show: false }, drag: { x: false, y: false } },
    legend: { show: true },
    scales: { x: { time: true }, y: { range: (u, min, max) => [0, Math.max(max * 1.1, 0.01)] } },
    axes: [{ stroke: '#8a94a0', grid: { show: false }, ticks: { stroke: '#2a3038' }, values: fmtX },
      { stroke: '#8a94a0', grid: { stroke: '#232a32' }, ticks: { stroke: '#2a3038' }, size: 52, values: (u, v) => v.map(x => fmt(x, x < 1 ? 2 : 0)) }],
    series: [{ value: (u, v) => v == null ? '–' : fmtTime(v).slice(0, 16) },
      { label: title, fill: ACCENT + 'cc', stroke: ACCENT, width: 0, paths: uPlot.paths.bars({ size: [0.75, 48], radius: 0.25 }), points: { show: false }, value: (u, v) => valueFmt(v) }],
  };
}

function mount(key, opts, data, el) {
  cost.charts[key]?.destroy();
  el.innerHTML = '';
  cost.charts[key] = new uPlot(opts, data, el);
}

async function renderPrice(d) {
  const s = d.settings;
  if (s.provider === 'fixed') {
    $('#priceChart').innerHTML = `<div class="empty">Fixed price: ${money(s.fixed_price_per_kwh, s)}/kWh — no price curve.</div>`;
    return;
  }
  const rows = await api('/api/cost/prices');
  if (!rows.length) { $('#priceChart').innerHTML = '<div class="empty">No prices yet (they are fetched every 30 min; tomorrow’s are published around 13:00).</div>'; return; }
  const sub = ['SEK', 'NOK', 'DKK'].includes(s.currency) ? 100 : 1;
  // stepped line: one point per interval start, plus the final end
  const xs = rows.map(r => r.ts).concat(rows[rows.length - 1].end);
  const ys = rows.map(r => r.total * sub).concat(rows[rows.length - 1].total * sub);
  const now = Date.now(), win = d.cheapest_3h, midnight = new Date(); midnight.setHours(24, 0, 0, 0);
  const unit = sub === 100 ? 'öre/kWh' : `${cur(s)}/kWh`;
  const opts = {
    width: costWidth(), height: 220, ms: 1,
    cursor: { points: { size: 6 }, drag: { x: false, y: false } },
    scales: { x: { time: true }, y: { range: (u, min, max) => [0, max * 1.1] } },
    axes: [{ stroke: '#8a94a0', grid: { stroke: '#232a32' }, ticks: { stroke: '#2a3038' } },
      { stroke: '#8a94a0', grid: { stroke: '#232a32' }, ticks: { stroke: '#2a3038' }, size: 52, label: unit, labelSize: 14 }],
    series: [{ value: (u, v) => v == null ? '–' : fmtTime(v).slice(0, 16) },
      { label: 'Price incl. surcharge + VAT', stroke: '#ff8a3d', width: 2, fill: '#ff8a3d1f', paths: uPlot.paths.stepped({ align: 1 }), value: (u, v) => v == null ? '–' : `${fmt(v, sub === 100 ? 0 : 3)} ${unit}` }],
    hooks: { draw: [u => {
      const ctx = u.ctx, { top, height, left, width } = u.bbox, dpr = devicePixelRatio || 1, x = v => u.valToPos(v, 'x', true);
      ctx.save();
      if (win) { ctx.fillStyle = 'rgba(61,220,151,0.14)'; ctx.fillRect(x(win.from), top, x(win.to) - x(win.from), height); }
      const mx = x(midnight.getTime());
      if (mx > left && mx < left + width) { ctx.strokeStyle = '#3a424c'; ctx.setLineDash([4 * dpr, 4 * dpr]); ctx.beginPath(); ctx.moveTo(mx, top); ctx.lineTo(mx, top + height); ctx.stroke(); ctx.setLineDash([]);
        ctx.fillStyle = '#8a94a0'; ctx.font = `${11 * dpr}px system-ui`; ctx.fillText('tomorrow', mx + 4 * dpr, top + 12 * dpr); }
      const nx = x(now);
      ctx.strokeStyle = '#d7dce2'; ctx.lineWidth = dpr; ctx.beginPath(); ctx.moveTo(nx, top); ctx.lineTo(nx, top + height); ctx.stroke();
      ctx.fillStyle = '#d7dce2'; ctx.font = `${11 * dpr}px system-ui`; ctx.fillText('now', nx + 4 * dpr, top + 12 * dpr);
      ctx.restore();
    }] },
  };
  mount('price', opts, [xs, ys], $('#priceChart'));
  const tomorrow = rows.some(r => r.ts >= midnight.getTime());
  $('#priceNote').innerHTML = (win ? `<span style="color:${ACCENT2}">■</span> cheapest 3 h ahead (${hhmm(win.from)}–${hhmm(win.to)}). ` : '') +
    (tomorrow ? '' : 'Tomorrow’s prices appear around 13:00.');
}

async function loadCost() {
  try {
    const d = await api('/api/cost/summary');
    cost.data = d;
    const s = d.settings;
    renderTiles(d); renderFacts(d); renderTables(d);
    await renderPrice(d);
    const now = Date.now();
    const hours = await api(`/api/cost/hourly?from=${now - 48 * 3600e3}&to=${now}`);
    mount('hour', barsOpts('Cost', 200, null, s, v => v == null ? '–' : money(v, s)),
      [hours.map(h => h.ts + 1800e3), hours.map(h => h.cost)], $('#hourChart'));
    const days = await api('/api/cost/daily?days=30');
    mount('day', barsOpts('Cost', 200, (u, v) => v.map(t => { const x = new Date(t); return `${x.getDate()}/${x.getMonth() + 1}`; }), s, v => v == null ? '–' : money(v, s)),
      [days.map(x => x.ts + 43200e3), days.map(x => x.cost)], $('#dayChart'));
  } catch (e) { console.error(e); }
  clearTimeout(cost.timer);
  if (state.tab === 'cost' && !document.hidden) cost.timer = setTimeout(loadCost, 60e3);
}

function showTab(tab) {
  state.tab = tab;
  store.set('bb.tab', tab);
  document.querySelectorAll('#tabs button').forEach(b => b.classList.toggle('on', b.dataset.tab === tab));
  $('#tab-rec').hidden = tab !== 'rec';
  $('#tab-cost').hidden = tab !== 'cost';
  clearTimeout(cost.timer); clearTimeout(state.timer);
  if (tab === 'cost') loadCost();
  else {
    const w = chartWidth();
    for (const c of state.charts.values()) c.u?.setSize({ width: w, height: c.u.height });
    state.overview?.setSize({ width: w, height: 90 });
    refresh();
  }
}

document.querySelectorAll('#tabs button').forEach(b => b.onclick = () => showTab(b.dataset.tab));
document.addEventListener('visibilitychange', () => { if (!document.hidden && state.tab === 'cost') loadCost(); else if (document.hidden) clearTimeout(cost.timer); });
addEventListener('resize', () => { if (state.tab === 'cost') { clearTimeout(cost.rt); cost.rt = setTimeout(loadCost, 200); } });
if (store.get('bb.tab', 'rec') === 'cost') showTab('cost');
