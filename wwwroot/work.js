// Страница проверки: SignalR + логика рабочего стола упаковщика
document.getElementById('navbar').innerHTML = nav('/');

let state = { order: null, lines: [], scans: [] };
let mapModal = null, pendingRaw = null, codesModal = null;

async function load() {
    state = await apiGet('/api/state');
    setScannerBadge(state.scanner || '—');
    const has = !!state.order;
    document.getElementById('workPanel').classList.toggle('d-none', !has);
    document.getElementById('uploadPanel').classList.toggle('d-none', has);
    if (!has) return;
    document.getElementById('orderNum').textContent = state.order.orderNumber || ('#' + state.order.id);
    document.getElementById('orderMeta').textContent =
        ' · ' + (state.order.recipient || '') + ' · начато ' + (state.order.createdAt || '');
    renderLines();
    renderScans();
}

function renderLines() {
    const tb = document.getElementById('linesBody');
    let plan = 0, done = 0;
    tb.innerHTML = state.lines.map(l => {
        plan += l.qty; done += l.scannedQty;
        const cls = l.scannedQty >= l.qty ? 'row-ok row-done' : (l.scannedQty > 0 ? 'row-wait' : '');
        return `<tr class="${cls}" data-line="${l.id}">
          <td>${l.lineNo}</td><td class="mono">${esc(l.code1C)}</td><td>${esc(l.name)}</td>
          <td class="text-center">${l.qty}</td>
          <td class="text-center fw-bold">${l.scannedQty}</td>
          <td class="text-center">${l.requiresMarking ? '✔' : ''}</td></tr>`;
    }).join('');
    document.getElementById('progressText').textContent =
        `Проверено ${done} из ${plan} единиц (${plan ? Math.round(done / plan * 100) : 0}%)`;
}

function renderScans() {
    const tb = document.getElementById('scanBody');
    let ok = 0, bad = 0;
    tb.innerHTML = state.scans.map(s => {
        if (s.validationStatus === 'ok') ok++; else if (s.setAside || s.validationStatus === 'error') bad++;
        const cls = s.validationStatus === 'ok' ? 'row-ok' : (s.validationStatus === 'pending' ? 'row-wait' : 'row-err');
        return `<tr class="${cls}">
          <td class="small">${esc((s.createdAt || '').substring(11, 19))}</td>
          <td class="small">${esc(s.productName || s.gtin || '')}<div class="mono text-muted">${esc(s.serial || '')}</div></td>
          <td class="small">${esc(statusRu(s.validationStatus))}<div class="small text-muted">${esc(s.validationMessage || '')}</div></td>
          <td><button class="btn btn-sm btn-outline-danger" title="Удалить запись" onclick="removeScan(${s.id})">×</button></td>
        </tr>`;
    }).join('');
    document.getElementById('counters').textContent = `ок: ${ok} · ошибок/отложено: ${bad}`;
}

async function removeScan(id) {
    // Удаление доступно для любой записи (включая успешно проверенные ok) — оператор может
    // исправить ошибку сопоставления или сканирования в любой момент.
    if (!confirm('Удалить эту запись сканирования?')) return;
    await apiDelete('/api/scans/' + id);
    await load();
}

function showStatus(res) {
    const el = document.getElementById('bigStatus');
    let cls = 'st-idle', txt = res.message || '';
    if (res.status === 'ok') { cls = 'st-ok'; txt = '✔ ' + (res.productName || '') + '<br><small>' + esc(res.message || '') + '</small>'; Sound.ok(); }
    else if (res.status === 'pending') { cls = 'st-wait'; txt = 'Проверка в ЧЗ…'; Sound.wait(); }
    else if (res.status === 'need_mapping') { cls = 'st-wait'; txt = 'Требуется сопоставление GTIN'; Sound.wait(); }
    else { cls = 'st-err'; txt = '✖ ' + esc(res.message || 'Ошибка') + (res.needSetAside ? '<br><small>ОТЛОЖИТЬ ТОВАР</small>' : ''); Sound.error(); }
    el.className = 'big-status mb-2 ' + cls;
    el.innerHTML = txt;
}

function handleResult(res) {
    showStatus(res);
    if (res.status === 'need_mapping') openMapping(res);
    load();
}

// --- Сопоставление GTIN ---
function openMapping(res) {
    pendingRaw = res.dataMatrix;
    document.getElementById('mapGtin').textContent = res.gtin;

    document.getElementById('mapList').innerHTML = state.lines.map(l => `
        <button type="button" class="list-group-item list-group-item-action" onclick="doMap('${res.gtin}',${l.id})">
           <strong>${esc(l.name)}</strong><br><span class="mono small">${esc(l.code1C)}</span>
           <span class="badge bg-secondary float-end">${l.scannedQty} / ${l.qty}</span></button>`).join('');
    mapModal = mapModal || new bootstrap.Modal(document.getElementById('mapModal'));
    mapModal.show();
}

async function doMap(gtin, orderLineId) {
    mapModal.hide();
    // orderLineId — точный Id строки ордера, выбранной оператором — выбор однозначен,
    // не зависит от совпадения текста или кода 1С.
    const res = await apiPost('/api/map', { gtin, orderLineId, raw: pendingRaw });
    handleResult(res);
}

document.getElementById('btnMapAside').onclick = () => {
    mapModal.hide();
    showStatus({ status: 'error', message: 'Товар не относится к ордеру — отложите', needSetAside: true });
};

// --- Загрузка ордера ---
document.getElementById('btnUpload').onclick = async () => {
    const f = document.getElementById('orderFile').files[0];
    const msg = document.getElementById('uploadMsg');
    if (!f) { msg.textContent = 'Выберите файл'; return; }
    msg.textContent = 'Загрузка…';
    const fd = new FormData(); fd.append('file', f);
    let r = await fetch('/api/orders/upload', { method: 'POST', body: fd });
    let d = await r.json();
    if (r.status === 409) {
        if (!confirm(`Уже открыт ордер ${d.orderNumber}. Закрыть его и начать новый?`)) { msg.textContent = ''; return; }
        fd.append('force', '1');
        r = await fetch('/api/orders/upload', { method: 'POST', body: fd });
        d = await r.json();
    }
    if (!r.ok) { msg.textContent = 'Ошибка: ' + (d.error || r.status); return; }
    msg.textContent = `Загружено строк: ${d.lines}` + (d.warnings && d.warnings.length ? ' · ' + d.warnings.join(' ') : '');
    await load();
};

document.getElementById('btnCsv').onclick = async () => {
    const unfinished = state.lines.filter(l => l.scannedQty < l.qty);
    if (unfinished.length && !confirm('Есть недобранные строки (' + unfinished.length + '). Всё равно завершить?')) return;
    const orderId = state.order.id;
    await apiPost('/api/orders/' + orderId + '/close');
    const { codes } = await apiGet('/api/orders/' + orderId + '/codes');
    document.getElementById('codesCount').textContent = codes.length;
    document.getElementById('codesArea').value = codes.join('\n');
    document.getElementById('linkCsvCodes').href = '/api/orders/' + orderId + '/csv';
    codesModal = codesModal || new bootstrap.Modal(document.getElementById('codesModal'));
    codesModal.show();
    await load();
};

document.getElementById('btnCopyCodes').onclick = async () => {
    const area = document.getElementById('codesArea');
    try {
        await navigator.clipboard.writeText(area.value);
    } catch {
        area.select(); document.execCommand('copy');
    }
    const btn = document.getElementById('btnCopyCodes');
    const old = btn.textContent;
    btn.textContent = 'Скопировано ✓';
    setTimeout(() => btn.textContent = old, 1500);
};

document.getElementById('btnCancel').onclick = async () => {
    if (!confirm('Закрыть текущий ордер без выгрузки?')) return;
    await apiPost('/api/orders/' + state.order.id + '/close');
    await load();
};

// --- Ручной ввод / сканер в режиме клавиатуры ---
const manual = document.getElementById('manualCode');
async function sendManual() {
    const code = manual.value.trim();
    if (!code) return;
    manual.value = '';
    handleResult(await apiPost('/api/scan', { code }));
}
document.getElementById('btnManual').onclick = sendManual;
manual.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); sendManual(); } });

// --- SignalR ---
const hub = new signalR.HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build();
hub.on('scanResult', handleResult);
hub.on('scanUpdate', res => { showStatus(res); load(); });
hub.on('scannerState', setScannerBadge);
hub.start().catch(() => { });

load();
setInterval(() => { if (hub.state !== 'Connected') load(); }, 5000);
