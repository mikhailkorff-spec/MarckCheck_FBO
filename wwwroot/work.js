// Страница проверки: SignalR + логика рабочего стола упаковщика
document.getElementById('navbar').innerHTML = nav('/');

let state = { order: null, lines: [], scans: [] };
let codesModal = null, boxesModal = null, files = [];

async function load() {
    state = await apiGet('/api/state');
    setScannerBadge(state.scanner || '—');
    const has = !!state.order;
    document.getElementById('workPanel').classList.toggle('d-none', !has);
    document.getElementById('uploadPanel').classList.toggle('d-none', has);
    if (!has) { loadFiles(); return; }
    document.getElementById('orderNum').textContent = state.order.orderNumber || ('#' + state.order.id);
    document.getElementById('orderMeta').textContent =
        ' · ' + (state.order.recipient || '') + ' · начато ' + (state.order.createdAt || '');
    document.getElementById('orderInstr').textContent =
        (state.order.instruction ? 'Распоряжение: ' + state.order.instruction : '') +
        (state.order.sourcePath ? ' · файл: ' + state.order.sourcePath : '');
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
          <td>${l.lineNo}</td><td class="mono">${esc(l.article || l.code1C)}</td><td>${esc(l.name)}</td>
          <td class="text-center">${l.qty}</td>
          <td class="text-center fw-bold">${l.scannedQty}</td>
          <td class="text-center">${l.requiresMarking ? '✔' : ''}</td>
          <td class="text-center" title="${esc(l.matchInfo || '')}">${l.matchStatus === 'ok' ? '<span class="text-success">●</span>' : '<span class="text-danger fw-bold">✖</span>'}</td></tr>`;
    }).join('');
    const bad = state.lines.filter(l => l.matchStatus !== 'ok');
    const w = document.getElementById('matchWarn');
    w.classList.toggle('d-none', bad.length === 0);
    w.innerHTML = bad.length ? `Не найдено в базе ШК строк: ${bad.length}. Такие товары не примутся при сканировании — обновите отчёт по ШК в 1С.<br>` +
        bad.map(l => `№${l.lineNo}: ${esc(l.matchInfo || '')}`).join('<br>') : '';
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
    else { cls = 'st-err'; txt = '✖ ' + esc(res.message || 'Ошибка') + (res.needSetAside ? '<br><small>ОТЛОЖИТЬ ТОВАР</small>' : ''); Sound.error(); }
    el.className = 'big-status mb-2 ' + cls;
    el.innerHTML = txt;
}

function handleResult(res) {
    showStatus(res);
    load();
}

// --- Список печатных форм в общей папке ---
async function loadFiles() {
    const all = document.getElementById('showCollected').checked ? 1 : 0;
    let d;
    try { d = await apiGet('/api/files?all=' + all); }
    catch (e) {
        // Не оставляем пустой список молча — показываем причину.
        document.getElementById('filesMsg').textContent = 'Не удалось получить список файлов: ' + e.message;
        return;
    }
    files = d.files || [];
    document.getElementById('rootInfo').textContent = d.root ? 'Папка: ' + d.root : '';
    document.getElementById('filesMsg').textContent = d.error || '';
    renderFiles();
}
function renderFiles() {
    const q = document.getElementById('fileSearch').value.trim().toLowerCase();
    const list = files.filter(f => !q || f.path.toLowerCase().includes(q));
    document.getElementById('filesBody').innerHTML = list.map((f, i) => `
      <tr class="${f.collected ? 'text-muted' : ''}">
        <td>${esc(f.name)}</td><td class="small">${esc(f.folder)}</td><td class="small text-nowrap">${esc(f.modified)}</td>
        <td><button class="btn btn-sm ${f.collected ? 'btn-outline-secondary' : 'btn-primary'}" onclick="openFile(${files.indexOf(f)})">Собрать</button></td>
      </tr>`).join('') || '<tr><td colspan="4" class="text-muted small">Нет файлов для сборки</td></tr>';
}
async function openFile(i) {
    const f = files[i];
    if (f.collected && !confirm('Этот ордер уже собран. Открыть повторно?')) return;
    const msg = document.getElementById('uploadMsg');
    let r = await fetch('/api/orders/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: f.path, force: false }) });
    let d = await r.json();
    if (r.status === 409) {
        if (!confirm(`Уже открыт ордер ${d.orderNumber}. Прервать его и открыть новый?`)) return;
        r = await fetch('/api/orders/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: f.path, force: true }) });
        d = await r.json();
    }
    if (!r.ok) { msg.className = 'small mt-2 text-danger'; msg.textContent = 'Ошибка: ' + (d.error || r.status); return; }
    showWarnings(d);
    await load();
}
function showWarnings(d) {
    const msg = document.getElementById('uploadMsg');
    msg.className = 'small mt-2 text-warning';
    msg.textContent = d.warnings && d.warnings.length ? d.warnings.join(' · ') : '';
    if (d.warnings && d.warnings.length) setStatusText(d.warnings[0]);
}
function setStatusText(t) {
    const el = document.getElementById('bigStatus');
    el.className = 'big-status mb-2 st-wait'; el.textContent = t;
}
document.getElementById('fileSearch').addEventListener('input', renderFiles);
document.getElementById('showCollected').onchange = loadFiles;
document.getElementById('btnRefresh').onclick = loadFiles;
setInterval(() => { if (!state.order) loadFiles(); }, 15000);

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
    showWarnings(d);
    await load();
};

// --- Завершение сборки: спросить количество коробов ---
document.getElementById('btnCsv').onclick = () => {
    const unfinished = state.lines.filter(l => l.scannedQty < l.qty);
    const w = document.getElementById('boxesWarn');
    w.classList.toggle('d-none', !unfinished.length);
    w.textContent = unfinished.length ? `Внимание: недобрано строк — ${unfinished.length}.` : '';
    document.getElementById('boxesUnits').textContent = state.scans.filter(s => s.validationStatus === 'ok').length;
    document.getElementById('boxesInput').value = '';
    boxesModal = boxesModal || new bootstrap.Modal(document.getElementById('boxesModal'));
    boxesModal.show();
    setTimeout(() => document.getElementById('boxesInput').focus(), 300);
};
document.getElementById('boxesInput').addEventListener('keydown', e => { if (e.key === 'Enter') document.getElementById('btnFinish').click(); });
document.getElementById('btnFinish').onclick = async () => {
    const boxes = parseInt(document.getElementById('boxesInput').value, 10);
    if (!(boxes >= 1)) { document.getElementById('boxesInput').classList.add('is-invalid'); return; }
    document.getElementById('boxesInput').classList.remove('is-invalid');
    const btn = document.getElementById('btnFinish'); btn.disabled = true;
    try {
        const orderId = state.order.id;
        const res = await apiPost('/api/orders/' + orderId + '/finish', { boxes });
        boxesModal.hide();
        const { codes } = await apiGet('/api/orders/' + orderId + '/codes');
        document.getElementById('finishInfo').innerHTML =
            [`Единиц: ${res.units}, коробов: ${res.boxes}`].concat(res.info || []).map(t => `<li>${esc(t)}</li>`).join('');
        document.getElementById('codesCount').textContent = codes.length;
        document.getElementById('codesArea').value = codes.join('\n');
        document.getElementById('linkCsvCodes').href = '/api/orders/' + orderId + '/csv';
        codesModal = codesModal || new bootstrap.Modal(document.getElementById('codesModal'));
        codesModal.show();
        await load();
    } catch (e) { alert('Ошибка завершения: ' + e.message); }
    finally { btn.disabled = false; }
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
    if (!confirm('Прервать сборку? Файл КИЗ не будет создан, печатная форма не отметится как собранная.')) return;
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
