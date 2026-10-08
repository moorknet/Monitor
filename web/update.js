'use strict';
// Update notice: polls /api/update (in-memory on the server) every 10 min while visible; "Update now" installs.

const upd = { timer: 0, info: null, installing: false, flash: null };

async function updFetch(path, post) {
  const r = await fetch(path, post ? { method: 'POST', headers: { 'X-BlackBox': '1' } } : { cache: 'no-store' });
  const j = await r.json();
  if (!r.ok) throw new Error(j.error || r.status);
  return j;
}

function notesHtml(md) {
  // release notes are plain "## heading" + "- item" lines from the release workflow
  let out = '', inList = false;
  for (const line of String(md || '').split(/\r?\n/)) {
    const li = line.match(/^\s*[-*]\s+(.*)$/);
    if (li) { if (!inList) { out += '<ul>'; inList = true; } out += `<li>${esc(li[1])}</li>`; continue; }
    if (inList) { out += '</ul>'; inList = false; }
    const h = line.match(/^#+\s+(.*)$/);
    if (h) out += `<b>${esc(h[1])}</b>`; else if (line.trim()) out += `<div>${esc(line)}</div>`;
  }
  return out + (inList ? '</ul>' : '');
}

function renderUpdate() {
  const u = upd.info, bar = $('#updateBar');
  if (!u) return;
  $('#version').textContent = `v${u.current}`;
  bar.className = 'update';
  const busy = ['downloading', 'verifying', 'applying'].includes(u.status);
  if (upd.flash) { bar.hidden = false; bar.className = 'update ' + (upd.flash.ok ? 'ok' : 'bad'); bar.innerHTML = `<div class="row1"><span class="msg">${upd.flash.html}</span><button id="updClose">Close</button></div>`; }
  else if (busy || upd.installing) {
    const step = { downloading: `Downloading… <progress max="100" value="${u.progress}"></progress> ${fmt(u.progress, 0)}%`, verifying: 'Verifying checksum…',
      applying: 'Installing — the service restarts now; recording pauses for a few seconds and this page reloads by itself.' }[u.status] || 'Restarting…';
    bar.hidden = false;
    bar.innerHTML = `<div class="row1"><span class="msg"><b>Updating to ${esc(u.latest?.version || '')}</b> — ${step}</span></div>`;
  } else if (u.last_result && Date.now() - u.last_result.ts < 3600e3 && store.get('bb.updSeen', 0) !== u.last_result.ts) {
    bar.hidden = false; bar.className = 'update ' + (u.last_result.ok ? 'ok' : 'bad');
    bar.innerHTML = `<div class="row1"><span class="msg">${u.last_result.ok ? '✔ ' : '✖ '}${esc(u.last_result.message)}${u.last_result.ok ? '' : ' — details in logs\\update.log'}</span><button id="updSeen">OK</button></div>`;
  } else if (u.status === 'staged') {
    bar.hidden = false;
    bar.innerHTML = `<div class="row1"><span class="msg">Update ${esc(u.latest.version)} downloaded and verified (dev/console run — only the installed service can swap its own files).</span><button id="updClose">Close</button></div>`;
  } else if (u.available && store.get('bb.updLater', '') !== u.latest.version) {
    bar.hidden = false;
    const pub = new Date(u.latest.published).toLocaleDateString();
    bar.innerHTML = `<div class="row1">
        <span class="msg"><b>Update available: ${esc(u.latest.version)}</b> <span class="muted">(you have ${esc(u.current)} · released ${pub} · ${fmt(u.latest.size_mb, 0)} MB)</span>
        ${u.status === 'error' ? `<div style="color:var(--crit)">${esc(u.error)}</div>` : ''}</span>
        <button id="updNotes">What’s new</button>
        <button id="updNow" class="primary">${u.status === 'error' ? 'Retry update' : 'Update now'}</button>
        <button id="updLater">Later</button></div>
      <div class="notes" id="updNotesBox" hidden>${notesHtml(u.latest.notes)}<div><a href="${esc(u.latest.url)}" target="_blank" rel="noopener" style="color:var(--accent)">Release on GitHub ↗</a></div></div>`;
  } else bar.hidden = true;
}

async function pollUpdate() {
  clearTimeout(upd.timer);
  try { upd.info = await updFetch('/api/update'); renderUpdate(); } catch { }
  if (!document.hidden) upd.timer = setTimeout(pollUpdate, upd.installing ? 1000 : 10 * 60e3);
}

async function installUpdate() {
  const v = upd.info.latest.version;
  if (!confirm(`Install BlackBox ${v}?\n\nThe service downloads the release from GitHub, verifies it, then restarts itself. ` +
    `Recording pauses for roughly 10–30 s. If the new version fails to start, the old one is restored automatically.`)) return;
  try { upd.info = await updFetch('/api/update/apply', true); }
  catch (e) { upd.flash = { ok: false, html: `Could not start the update: ${esc(e.message)}` }; renderUpdate(); return; }
  upd.installing = true; renderUpdate();
  const started = Date.now();
  // follow progress; once the old service goes away, wait for the new version to answer, then reload
  while (Date.now() - started < 180e3) {
    await new Promise(r => setTimeout(r, 1500));
    try {
      const u = await updFetch('/api/update');
      upd.info = u;
      if (u.status === 'error' || u.status === 'staged') { upd.installing = false; renderUpdate(); return; }
      if (u.current === v) { location.reload(); return; }
      renderUpdate();
    } catch { upd.info.status = 'applying'; renderUpdate(); } // service restarting
  }
  upd.installing = false;
  upd.flash = { ok: false, html: 'The update is taking longer than expected. Check <code>C:\\ProgramData\\BlackBox\\logs\\update.log</code> and reload this page.' };
  renderUpdate();
}

$('#updateBar').addEventListener('click', e => {
  const id = e.target.id;
  if (id === 'updNotes') { const b = $('#updNotesBox'); b.hidden = !b.hidden; }
  else if (id === 'updNow') installUpdate();
  else if (id === 'updLater') { store.set('bb.updLater', upd.info.latest.version); renderUpdate(); }
  else if (id === 'updSeen') { store.set('bb.updSeen', upd.info.last_result.ts); renderUpdate(); }
  else if (id === 'updClose') { upd.flash = null; if (upd.info) upd.info.status = 'idle'; $('#updateBar').hidden = true; }
});
$('#version').onclick = async () => {
  $('#version').textContent = 'checking…';
  try {
    upd.info = await updFetch('/api/update/check', true);
    store.set('bb.updLater', '');
    if (!upd.info.available) upd.flash = upd.info.status === 'error'
      ? { ok: false, html: esc(upd.info.error) }
      : { ok: true, html: `✔ BlackBox ${esc(upd.info.current)} is up to date${upd.info.latest ? '' : ' (no releases published yet)'}.` };
  } catch (e) { upd.flash = { ok: false, html: esc(e.message) }; }
  renderUpdate();
};
document.addEventListener('visibilitychange', () => { if (!document.hidden) pollUpdate(); else clearTimeout(upd.timer); });
pollUpdate();
