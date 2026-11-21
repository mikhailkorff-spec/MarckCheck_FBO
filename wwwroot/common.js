// Общие функции: запросы к API, звуки, навигация
async function api(url, opts) {
    const r = await fetch(url, opts);
    let data = null;
    try { data = await r.json(); } catch (e) { }
    if (!r.ok) throw new Error((data && (data.error || data.message)) || ('HTTP ' + r.status));
    return data;
}
const apiGet = (u) => api(u);
const apiPost = (u, body) => api(u, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {})
});
const apiDelete = (u) => api(u, { method: 'DELETE' });

// Звуковые сигналы генерируются программно — внешние файлы не нужны
const Sound = (() => {
    let ctx = null;
    function beep(freq, ms, vol) {
        try {
            ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
            const o = ctx.createOscillator(), g = ctx.createGain();
            o.type = 'square'; o.frequency.value = freq;
            g.gain.value = vol === undefined ? 0.15 : vol;
            o.connect(g); g.connect(ctx.destination);
            o.start(); o.stop(ctx.currentTime + ms / 1000);
        } catch (e) { }
    }
    return {
        ok: () => beep(1200, 90),
        wait: () => beep(700, 60, 0.08),
        error: () => { beep(300, 250, 0.25); setTimeout(() => beep(220, 350, 0.25), 280); }
    };
})();

function nav(active) {
    const items = [
        ['/', 'Сборка ФБО'],
        ['/nomenclature.html', 'База ШК'],
        ['/journal.html', 'Журнал'],
        ['/admin.html', 'Администрирование']
    ];
    return `<nav class="navbar navbar-expand navbar-dark bg-dark mb-3">
      <div class="container-fluid">
        <span class="navbar-brand">Станция сборки ФБО</span>
        <ul class="navbar-nav me-auto">
          ${items.map(([h, t]) => `<li class="nav-item"><a class="nav-link ${active === h ? 'active fw-bold' : ''}" href="${h}">${t}</a></li>`).join('')}
        </ul>
        <span id="scannerBadge" class="badge bg-secondary badge-scanner">сканер: —</span>
      </div>
    </nav>`;
}

function esc(s) {
    return (s === null || s === undefined) ? '' : String(s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function setScannerBadge(state) {
    const el = document.getElementById('scannerBadge');
    if (!el) return;
    el.textContent = 'сканер: ' + state;
    el.className = 'badge badge-scanner ' + (/подключ/.test(state) ? 'bg-success' : 'bg-warning text-dark');
}

function statusRu(s) {
    return { ok: 'Проверен', pending: 'Проверка…', error: 'Ошибка', setaside: 'Отложен', duplicate: 'Дубликат', need_mapping: 'Нужно сопоставление' }[s] || s;
}
