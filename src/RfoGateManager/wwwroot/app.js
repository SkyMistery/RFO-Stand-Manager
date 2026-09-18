'use strict';

const state = {
  plan: null,
  planSig: '',
  depSig: '',
  seenChanges: null,
  status: null,
  filter: '',
  selected: null,
};

const $ = (id) => document.getElementById(id);

// --- Utilità ----------------------------------------------------------------

const hhmm = (iso) => {
  if (!iso) return '--:--';
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? '--:--'
    : `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}`;
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
));

let toastTimer;
function toast(message, kind = '') {
  const el = $('toast');
  el.textContent = message;
  el.className = `toast ${kind}`;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 4500);
}

async function api(path, options = {}) {
  const res = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
  return data;
}

// --- Stato / chip -----------------------------------------------------------

function renderStatus(s) {
  state.status = s;
  $('subtitle').textContent =
    `${s.airport} · evento ${s.eventDate} · ${s.operatorName}`;

  const chips = [];

  chips.push(chip(
    s.aurora.connected ? 'ok' : 'bad',
    s.aurora.connected ? `Aurora ${s.aurora.host}` : 'Aurora non connesso',
    s.aurora.connected ? 'disconnect' : 'connect',
    s.aurora.error || (s.aurora.connected ? '' : 'Aurora aperto? F7 → Other → 3rd Party Software Access = YES')
  ));

  chips.push(chip(
    s.whazzup.lastFetch ? 'ok' : 'warn',
    `Whazzup ${s.whazzup.pilots} piloti`,
    null,
    s.whazzup.error || `Ultimo aggiornamento ${hhmm(s.whazzup.lastFetch)}Z`
  ));

  const bk = s.booking;
  chips.push(chip(
    bk.success ? 'ok' : bk.configured ? 'bad' : 'warn',
    bk.success ? `Booking ${bk.legs} tratte` : bk.configured ? 'Booking in errore' : 'Booking senza x-key',
    null,
    bk.error || bk.url || ''
  ));

  chips.push(chip(
    s.stands.count > 0 ? 'ok' : 'bad',
    `${s.stands.count} stand`,
    null,
    s.stands.file || 'Nessun file .gts in data/'
  ));

  chips.push(chip(
    s.sharedState.enabled ? (s.sharedState.error ? 'bad' : 'ok') : 'warn',
    s.sharedState.enabled ? `Condiviso v${s.sharedState.version}` : 'Solo locale',
    null,
    s.sharedState.error || s.sharedState.url || 'Nessun URL condiviso in secrets/booking.json'
  ));

  $('chips').innerHTML = chips.join('');

  document.querySelectorAll('.chip[data-action]').forEach((el) => {
    el.addEventListener('click', () => auroraToggle(el.dataset.action));
  });
}

function chip(kind, label, action, title) {
  const cls = `chip ${kind}${action ? ' clickable' : ''}`;
  const attr = action ? ` data-action="${action}"` : '';
  return `<span class="${cls}"${attr} title="${esc(title)}"><span class="dot"></span>${esc(label)}</span>`;
}

async function auroraToggle(action) {
  try {
    const r = await api(`/api/aurora/${action}`, { method: 'POST' });
    toast(r.connected ? 'Aurora connesso.' : (r.error || 'Aurora disconnesso.'), r.connected ? 'ok' : '');
  } catch (e) {
    toast(e.message, 'bad');
  }
  refreshStatus();
}

// --- Traffico selezionato ---------------------------------------------------

async function pollSelected() {
  if (!$('autoSelected').checked) return;
  if (!state.status?.aurora.connected) return;

  try {
    const r = await api('/api/aurora/selected');
    renderSelected(r);
  } catch {
    /* Aurora può sparire da un momento all'altro: lo stato lo dice già. */
  }
}

function renderSelected(r) {
  const box = $('selectedBox');
  state.selected = r;

  if (!r || !r.callsign) {
    box.className = 'selected-box empty';
    box.innerHTML = '<p>Seleziona un aereo in Aurora: qui comparirà lo stand suggerito.</p>';
    return;
  }

  const hasStand = Boolean(r.suggestion);
  const standClass = !r.inPlan ? 'selected-stand off'
                   : hasStand ? 'selected-stand' : 'selected-stand none';
  const standText = !r.inPlan ? 'fuori piano'
                  : hasStand ? esc(r.suggestion) : 'nessuno stand libero';
  box.className = 'selected-box';
  box.innerHTML = `
    <div class="selected-grid">
      <div class="selected-callsign">${esc(r.callsign)}</div>
      <div class="${standClass}">${standText}</div>
      <div class="selected-meta">
        ${esc(r.reason || '')}
        ${r.inPlan && !r.assumedByMe
          ? `<div class="assume-warn">${r.assumedBy
              ? `Assunto da ${esc(r.assumedBy)}: Aurora accetterà lo stand solo da chi l'ha assunto.`
              : 'Non assunto: assumilo in Aurora prima di assegnare lo stand.'}</div>`
          : ''}
      </div>
      <div class="actions">
        <input type="text" id="selStand" value="${esc(r.suggestion || '')}" placeholder="stand">
        <label class="toggle"><input type="checkbox" id="selPm"> <span>avvisa il pilota</span></label>
        <button class="btn" id="selNotify">Solo PM</button>
        <button class="btn btn-primary" id="selAssign">Assegna in Aurora</button>
      </div>
    </div>`;

  $('selNotify').addEventListener('click', () => {
    notifyPilot(r.callsign, $('selStand').value.trim(), r.key);
  });

  $('selAssign').addEventListener('click', () => {
    assign(r.callsign, $('selStand').value.trim(), r.key, $('selPm').checked);
  });
}

// --- Piano ------------------------------------------------------------------

async function refreshPlan() {
  try {
    const p = await api('/api/plan');
    state.plan = p;

    const sig = JSON.stringify([p.assignments, p.changes, p.warnings, p.occupants]);
    if (sig === state.planSig) return;
    state.planSig = sig;

    renderPlan();
  } catch (e) {
    toast(`Piano non aggiornato: ${e.message}`, 'bad');
  }
}

function visibleRows() {
  const rows = state.plan?.assignments ?? [];
  const f = state.filter.trim().toLowerCase();
  if (!f) return rows;

  return rows.filter((a) => [a.callsign, a.stand, a.aircraft, a.origin, a.destination]
    .some((v) => String(v ?? '').toLowerCase().includes(f)));
}

function renderPlan() {
  const p = state.plan;
  if (!p) return;

  const w = $('warnings');
  if (p.warnings?.length) {
    w.hidden = false;
    w.innerHTML = `<ul>${p.warnings.map((x) => `<li>${esc(x)}</li>`).join('')}</ul>`;
  } else {
    w.hidden = true;
  }

  renderChanges(p.changes ?? []);

  const rows = visibleRows();
  const body = $('planBody');

  if (!rows.length) {
    body.innerHTML = '<tr><td colspan="8" class="muted">Nessun volo. Carica le prenotazioni, oppure aspetta che i piloti si colleghino.</td></tr>';
    renderGantt();
    return;
  }

  body.innerHTML = rows.map((a) => {
    const badges = [
      a.inboundOnline ? `<span class="badge live">${esc(a.trackState || 'online')}</span>` : '',
      a.pinned ? '<span class="badge pin">fissato</span>' : '',
      a.bookedStand ? `<span class="badge booked">prenotato ${esc(a.bookedStand)}</span>` : '',
      a.actualStand ? `<span class="badge">a terra ${esc(a.actualStand)}</span>` : '',
      a.hasRotation ? '<span class="badge">rotazione</span>' : '',
      a.conflict ? '<span class="badge conflict">conflitto</span>' : '',
      a.oversize ? '<span class="badge oversize">fuori misura</span>' : '',
      a.unscheduled ? '<span class="badge oversize">non programmato</span>' : '',
      a.reassigned ? `<span class="badge moved">riassegnato: ${esc(a.displacedFrom)} occupato</span>` : '',
      a.notifiedStand && a.notifiedStand === a.stand ? '<span class="badge live">pilota avvisato</span>' : '',
      (a.notifiedStand && a.stand && a.notifiedStand !== a.stand) || (a.reassigned && a.wasPinned && !a.notifiedStand)
        ? `<span class="badge conflict">da ricomunicare${a.notifiedStand ? ` (detto ${esc(a.notifiedStand)})` : ''}</span>` : '',
      a.distanceNm ? `<span class="badge">${Math.round(a.distanceNm)} NM</span>` : '',
    ].filter(Boolean).join(' ');

    const route = `${esc(a.origin || '····')} → ${esc(a.destination || '····')}`;

    const rowClass = a.conflict ? 'conflict' : a.reassigned ? 'moved' : a.oversize ? 'oversize' : '';

    return `<tr class="${rowClass}">
      <td class="cs">${hhmm(a.from)}–${hhmm(a.to)}</td>
      <td class="cs">${esc(a.callsign)}</td>
      <td>${esc(a.aircraft || '—')} <span class="muted">${esc(a.size || '')}</span></td>
      <td class="muted">${route}</td>
      <td>${badges || '<span class="muted">—</span>'}</td>
      <td class="${a.stand ? 'stand-cell' : 'stand-cell none'}">${esc(a.stand || 'da trovare')}</td>
      <td class="reason">${esc(a.reason)}</td>
      <td>
        <div class="row-actions">
          <button class="btn btn-small" data-act="show" data-cs="${esc(a.callsign)}">Mostra</button>
          <button class="btn btn-small" data-act="notify" title="Manda al pilota un PM con lo stand da aspettarsi"
                  data-cs="${esc(a.callsign)}" data-stand="${esc(a.stand || '')}"
                  data-key="${esc(a.key)}" ${a.stand && !a.unscheduled ? '' : 'disabled'}>PM</button>
          <button class="btn btn-small btn-primary" data-act="assign"
                  data-cs="${esc(a.callsign)}" data-stand="${esc(a.stand || '')}"
                  data-key="${esc(a.key)}" ${a.stand && !a.unscheduled ? '' : 'disabled'}>Assegna</button>
        </div>
      </td>
    </tr>`;
  }).join('');

  body.querySelectorAll('button[data-act]').forEach((b) => {
    b.addEventListener('click', () => {
      if (b.dataset.act === 'show') showInAurora(b.dataset.cs);
      else if (b.dataset.act === 'notify') notifyPilot(b.dataset.cs, b.dataset.stand, b.dataset.key);
      else assign(b.dataset.cs, b.dataset.stand, b.dataset.key, false);
    });
  });

  renderGantt();
}

// --- Cambi di stand ---------------------------------------------------------

function renderChanges(changes) {
  const el = $('changes');

  // Al primo caricamento non avvisiamo: quei cambi c'erano già prima di aprire la pagina.
  const fresh = state.seenChanges === null
    ? []
    : changes.filter((c) => !state.seenChanges.has(c.at + c.key));
  state.seenChanges = new Set(changes.map((c) => c.at + c.key));

  if (fresh.length === 1) {
    const c = fresh[0];
    toast(`${c.callsign}: stand ${c.from ?? '—'} → ${c.to ?? 'nessuno'}`, 'bad');
  } else if (fresh.length > 1) {
    toast(`${fresh.length} stand cambiati: guarda l'elenco in cima al piano.`, 'bad');
  }

  if (!changes.length) { el.hidden = true; return; }

  el.hidden = false;
  el.innerHTML = `<div class="changes-title">Ultimi cambi di stand</div><ul>${changes.slice(0, 8).map((c) => `
    <li>
      <span class="cs">${hhmm(c.at)}Z</span>
      <span class="cs">${esc(c.callsign)}</span>
      <span class="stand-cell">${esc(c.from ?? '—')} → ${esc(c.to ?? 'nessuno')}</span>
      <span class="muted">${esc(c.reason)}</span>
    </li>`).join('')}</ul>`;
}

// --- Gantt ------------------------------------------------------------------

function renderGantt() {
  const p = state.plan;
  const el = $('gantt');
  if (!p?.stands?.length) { el.innerHTML = ''; return; }

  const assigned = p.assignments.filter((a) => a.stand);
  const now = Date.now();

  // Finestra: da un'ora fa fino all'ultima partenza, minimo sei ore.
  let min = now - 3600e3;
  let max = now + 6 * 3600e3;
  for (const a of assigned) {
    max = Math.max(max, new Date(a.to).getTime());
    min = Math.min(min, new Date(a.from).getTime());
  }
  const span = max - min;
  const pct = (t) => ((new Date(t).getTime() - min) / span) * 100;

  $('ganttRange').textContent = `${hhmm(new Date(min).toISOString())}Z → ${hhmm(new Date(max).toISOString())}Z`;

  const byStand = new Map();
  for (const a of assigned) {
    if (!byStand.has(a.stand)) byStand.set(a.stand, []);
    byStand.get(a.stand).push(a);
  }

  // Asse: un tick ogni ora tonda.
  const ticks = [];
  const first = new Date(min);
  first.setUTCMinutes(0, 0, 0);
  for (let t = first.getTime(); t <= max; t += 3600e3) {
    if (t < min) continue;
    ticks.push(`<span class="gantt-tick" style="left:${((t - min) / span) * 100}%">${hhmm(new Date(t).toISOString())}</span>`);
  }

  const nowPct = ((now - min) / span) * 100;

  const rows = p.stands
    .filter((s) => !s.disabled || byStand.has(s.id))
    .map((s) => {
      const bars = (byStand.get(s.id) ?? []).map((a) => {
        const left = Math.max(0, pct(a.from));
        const right = Math.min(100, pct(a.to));
        const cls = a.conflict ? 'conflict'
                  : a.unscheduled ? 'unscheduled'
                  : a.reassigned ? 'moved'
                  : a.oversize ? 'oversize'
                  : a.pinned ? 'pinned' : '';
        return `<div class="gantt-bar ${cls}" style="left:${left}%;width:${Math.max(right - left, 1.2)}%"
                     title="${esc(a.callsign)} ${hhmm(a.from)}–${hhmm(a.to)}Z — ${esc(a.reason)}">${esc(a.callsign)}</div>`;
      }).join('');

      const used = byStand.has(s.id);
      return `<div class="gantt-row">
        <div class="gantt-label ${used ? 'used' : ''}" title="cat. ${esc(s.maxSize)}${s.contact ? ', pontile' : ''}">${esc(s.id)}</div>
        <div class="gantt-track">${bars}<div class="now-line" style="left:${nowPct}%"></div></div>
      </div>`;
    }).join('');

  el.innerHTML = `<div class="gantt-axis"><div></div><div class="gantt-axis-ticks">${ticks.join('')}</div></div>${rows}`;
}

// --- Azioni -----------------------------------------------------------------

async function assign(callsign, stand, key, privateMessage) {
  if (!stand) { toast('Nessuno stand da assegnare.', 'bad'); return; }

  try {
    const r = await api('/api/aurora/assign', {
      method: 'POST',
      body: JSON.stringify({ callsign, stand, key, privateMessage }),
    });
    toast(r.message, 'ok');
    refreshPlan();
  } catch (e) {
    toast(e.message, 'bad');
  }
}

/// Il PM arriva a un pilota vero: prima si fa vedere il testo esatto e si chiede conferma.
async function notifyPilot(callsign, stand, key) {
  if (!stand) { toast('Nessuno stand da comunicare.', 'bad'); return; }

  try {
    const preview = await api(`/api/aurora/message?callsign=${encodeURIComponent(callsign)}&stand=${encodeURIComponent(stand)}`);
    if (!confirm(`Inviare a ${callsign} questo messaggio privato?\n\n"${preview.text}"`)) return;

    const r = await api('/api/aurora/notify', {
      method: 'POST',
      body: JSON.stringify({ callsign, stand, key }),
    });
    toast(r.message, 'ok');
    refreshPlan();
  } catch (e) {
    toast(e.message, 'bad');
  }
}

// --- Strippiera partenze -----------------------------------------------------

async function refreshDepartures() {
  try {
    const d = await api('/api/departures');

    // Minuti all'EOBT a parte, perché cambiano ogni minuto: se il resto è uguale e il
    // minuto no, ridisegnare vale la pena solo una volta al minuto, non ogni 4 secondi.
    const sig = JSON.stringify([d, Math.floor(Date.now() / 60000)]);
    if (sig === state.depSig) return;
    state.depSig = sig;

    renderStrips('waitingStrips', 'waitingCount', d.waiting, true);
    renderStrips('calledStrips', 'calledCount', d.called, false);
  } catch {
    /* la strippiera riprova al prossimo giro */
  }
}

function duration(m) {
  if (m < 90) return `${m}'`;
  const h = Math.floor(m / 60);
  const r = m % 60;
  return r ? `${h} h ${r}'` : `${h} h`;
}

function eobtHint(m) {
  if (m === null || m === undefined) return '';
  if (m < -5) return `<span class="badge conflict">EOBT +${duration(-m)}</span>`;
  if (m <= 15) return `<span class="badge oversize">fra ${Math.max(m, 0)}'</span>`;
  return `<span class="badge">fra ${duration(m)}</span>`;
}

function renderStrips(listId, countId, strips, waiting) {
  $(countId).textContent = strips.length;
  const el = $(listId);

  if (!strips.length) {
    el.innerHTML = `<p class="muted strip-empty">${waiting ? 'Nessuna partenza in attesa.' : 'Nessuno ha ancora chiamato.'}</p>`;
    return;
  }

  el.innerHTML = strips.map((s) => `
    <div class="strip ${waiting ? '' : 'called'} ${s.online ? '' : 'offline'}">
      <div class="strip-time cs">${hhmm(s.eobt)}</div>
      <div class="strip-main">
        <div><span class="cs strip-cs">${esc(s.callsign)}</span>
             <span class="muted">${esc(s.aircraftType)} → ${esc(s.destination)}</span></div>
        <div class="strip-badges">
          <span class="badge ${s.stand ? 'booked' : ''}">stand ${esc(s.stand || '—')}</span>
          <span class="badge ${s.online ? 'live' : ''}">${s.online ? 'online' : 'non connesso'}</span>
          ${s.assumedBy ? `<span class="badge">assunto da ${esc(s.assumedBy)}</span>` : ''}
          ${s.calledSource === 'manuale' ? '<span class="badge pin">a mano</span>' : ''}
          ${eobtHint(s.minutesToEobt)}
        </div>
      </div>
      <div class="strip-actions">
        <button class="btn btn-small ${waiting ? 'btn-primary' : ''}" data-cs="${esc(s.callsign)}"
                data-called="${waiting}">${waiting ? 'Ha chiamato →' : '← Non ancora'}</button>
        ${s.calledSource === 'manuale'
          ? `<button class="btn btn-small" data-cs="${esc(s.callsign)}" data-called="auto"
                     title="Torna a dedurlo da chi ha assunto il traffico">auto</button>` : ''}
      </div>
    </div>`).join('');

  el.querySelectorAll('button[data-called]').forEach((b) => {
    b.addEventListener('click', () => setCalled(b.dataset.cs, b.dataset.called));
  });
}

async function setCalled(callsign, value) {
  const called = value === 'auto' ? null : value === 'true';
  try {
    await api('/api/departures/called', { method: 'POST', body: JSON.stringify({ callsign, called }) });
    state.depSig = '';
    refreshDepartures();
  } catch (e) {
    toast(e.message, 'bad');
  }
}

async function showInAurora(callsign) {
  try {
    await api('/api/aurora/show', { method: 'POST', body: JSON.stringify({ callsign }) });
  } catch (e) {
    toast(e.message, 'bad');
  }
}

async function refreshStatus() {
  try {
    renderStatus(await api('/api/status'));
  } catch (e) {
    toast(`Stato non disponibile: ${e.message}`, 'bad');
  }
}

// --- Avvio ------------------------------------------------------------------

$('filter').addEventListener('input', (e) => { state.filter = e.target.value; renderPlan(); });

$('btnBooking').addEventListener('click', async (e) => {
  e.target.disabled = true;
  try {
    const r = await api('/api/booking/refresh', { method: 'POST' });
    toast(r.success ? `Prenotazioni caricate: ${r.legs.length} tratte.` : r.error, r.success ? 'ok' : 'bad');
    await refreshPlan();
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    e.target.disabled = false;
    refreshStatus();
  }
});

$('btnStands').addEventListener('click', async () => {
  try {
    const r = await api('/api/stands/reload', { method: 'POST' });
    toast(`${r.count} stand caricati.`, r.count ? 'ok' : 'bad');
    await refreshPlan();
    refreshStatus();
  } catch (e) {
    toast(e.message, 'bad');
  }
});

$('btnPublish').addEventListener('click', async () => {
  try {
    const r = await api('/api/plan/publish', { method: 'POST' });
    toast(`Piano pubblicato (versione ${r.version}).`, 'ok');
    refreshStatus();
  } catch (e) {
    toast(e.message, 'bad');
  }
});

refreshStatus();
refreshPlan();
setInterval(refreshStatus, 10000);
setInterval(refreshPlan, 4000);
refreshDepartures();
setInterval(refreshDepartures, 4000);
setInterval(pollSelected, 1500);
