(function () {
  if (window.__welcomeStoresStockInstalled) return;
  window.__welcomeStoresStockInstalled = true;

  const css = document.createElement('style');
  css.textContent = `
    #wsStockCurtain{position:fixed;inset:0;background:var(--bg,#1e1e2e);z-index:1200;display:none;flex-direction:column;color:var(--text,#fff);font-family:Segoe UI,sans-serif}
    #wsStockCurtain.open{display:flex}
    #wsStockHeader{display:flex;align-items:center;gap:12px;padding:16px 20px;border-bottom:1px solid rgba(255,255,255,.08);background:#252538}
    #wsStockHeader h2{font-size:18px;margin:0;flex:1}
    #wsStockClose{border:0;background:rgba(255,255,255,.08);color:#fff;border-radius:8px;width:34px;height:34px;cursor:pointer}
    #wsStockBody{padding:18px 20px;overflow:auto;flex:1}
    .ws-search{display:flex;gap:10px;margin-bottom:14px}
    .ws-search input{flex:1;background:#161624;color:#fff;border:1px solid rgba(255,255,255,.12);border-radius:10px;padding:11px 13px;font-size:14px}
    .ws-btn{border:0;background:#8b7bff;color:#fff;border-radius:9px;padding:9px 14px;cursor:pointer;font-weight:600}
    .ws-btn.secondary{background:rgba(255,255,255,.10)}
    .ws-btn[disabled]{opacity:.45;cursor:not-allowed}
    #wsStockStatus{min-height:20px;color:#aaaac0;font-size:13px;margin-bottom:10px}
    #wsStockItems{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:16px}
    .ws-item{border:1px solid rgba(255,255,255,.10);background:#2a2a3d;color:#fff;border-radius:10px;padding:9px 12px;cursor:pointer;text-align:left}
    .ws-item strong{display:block}.ws-item span{font-size:12px;color:#aaaac0}
    #wsSelectedItem{display:none;margin:6px 0 14px;padding:12px 14px;border:1px solid rgba(139,123,255,.35);background:rgba(139,123,255,.08);border-radius:10px}
    .ws-table-wrap{overflow:auto;border:1px solid rgba(255,255,255,.08);border-radius:10px}
    .ws-table{width:100%;border-collapse:collapse;font-size:13px;white-space:nowrap}
    .ws-table th,.ws-table td{padding:9px 10px;border-bottom:1px solid rgba(255,255,255,.07);text-align:left}
    .ws-table th{position:sticky;top:0;background:#33334a;z-index:1}
    .ws-table tbody tr:hover{background:rgba(139,123,255,.09)}
    .ws-badge{display:inline-flex;align-items:center;border-radius:999px;padding:4px 8px;font-size:11px;font-weight:600}
    .ws-badge.ok{background:rgba(46,204,113,.14);color:#76e29e}
    .ws-badge.missing{background:rgba(255,170,70,.14);color:#ffc276}
    .ws-badge.current{background:rgba(120,160,255,.14);color:#9ebcff}
    .ws-qty{width:62px;background:#161624;color:#fff;border:1px solid rgba(255,255,255,.12);border-radius:7px;padding:7px}
    .ws-empty{padding:24px;text-align:center;color:#aaaac0}
  `;
  document.head.appendChild(css);

  const curtain = document.createElement('section');
  curtain.id = 'wsStockCurtain';
  curtain.innerHTML = `
    <div id="wsStockHeader"><h2>WelcomeStores Stores Inventory</h2><button id="wsStockClose" title="Κλείσιμο">✕</button></div>
    <div id="wsStockBody">
      <div class="ws-search"><input id="wsStockSearch" placeholder="Αναζήτηση με κωδικό ή περιγραφή..." autocomplete="off"><button id="wsStockSearchBtn" class="ws-btn">Αναζήτηση</button></div>
      <div id="wsStockStatus"></div>
      <div id="wsStockItems"></div>
      <div id="wsSelectedItem"></div>
      <div class="ws-table-wrap"><table class="ws-table"><thead><tr><th>Επωνυμία</th><th>ΑΦΜ</th><th>Τηλέφωνο</th><th>Είδος</th><th>Περιγραφή</th><th>Απόθεμα</th><th>Προμηθευτής</th><th>Ποσότητα</th><th>Ενέργεια</th></tr></thead><tbody id="wsStockRows"><tr><td colspan="9" class="ws-empty">Αναζητήστε ένα είδος για να δείτε διαθεσιμότητα.</td></tr></tbody></table></div>
    </div>`;
  document.body.appendChild(curtain);

  const search = document.getElementById('wsStockSearch');
  const status = document.getElementById('wsStockStatus');
  const items = document.getElementById('wsStockItems');
  const selected = document.getElementById('wsSelectedItem');
  const rows = document.getElementById('wsStockRows');

  function post(o) {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(o);
  }

  function esc(v) {
    return String(v == null ? '' : v).replace(/[&<>"']/g, c => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
  }

  function open() {
    curtain.classList.add('open');
    setTimeout(() => search.focus(), 20);
  }

  function close() {
    curtain.classList.remove('open');
  }

  window.openWelcomeStoresStock = open;
  document.getElementById('wsStockClose').addEventListener('click', close);

  function runSearch() {
    const q = search.value.trim();
    if (!q) return;
    status.textContent = 'Αναζήτηση στην κεντρική εταιρία...';
    items.innerHTML = '';
    selected.style.display = 'none';
    rows.innerHTML = '<tr><td colspan="9" class="ws-empty">Αναζήτηση...</td></tr>';
    post({ type: 'ws_stock_search', query: q });
  }

  document.getElementById('wsStockSearchBtn').addEventListener('click', runSearch);
  search.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      e.preventDefault();
      runSearch();
    }
  });

  function chooseItem(item) {
    selected.style.display = 'block';
    selected.innerHTML = '<strong>' + esc(item.Code) + '</strong> — ' + esc(item.Name);
    status.textContent = 'Ανάκτηση αποθέματος καταστημάτων...';
    rows.innerHTML = '<tr><td colspan="9" class="ws-empty">Φόρτωση αποθέματος...</td></tr>';
    post({ type: 'ws_stock_availability', itemCode: item.Code });
  }

  window.welcomeStoresStockReceive = function (kind, data) {
    if (kind === 'error' || !data || data.success === false) {
      status.textContent = (data && data.message) || 'Σφάλμα.';
      return;
    }

    if (kind === 'search') {
      status.textContent = data.items && data.items.length
        ? ('Βρέθηκαν ' + data.items.length + ' είδη.')
        : 'Δεν βρέθηκαν είδη.';
      items.innerHTML = '';
      (data.items || []).forEach(item => {
        const b = document.createElement('button');
        b.className = 'ws-item';
        b.innerHTML = '<strong>' + esc(item.Code) + '</strong><span>' + esc(item.Name) + '</span>';
        b.addEventListener('click', () => chooseItem(item));
        items.appendChild(b);
      });
      return;
    }

    if (kind === 'availability') {
      const list = data.rows || [];
      status.textContent = list.length
        ? ('Διαθεσιμότητα σε ' + list.length + ' γραμμές αποθήκης.')
        : 'Δεν βρέθηκε διαθέσιμο απόθεμα.';
      rows.innerHTML = '';

      if (!list.length) {
        rows.innerHTML = '<tr><td colspan="9" class="ws-empty">Δεν υπάρχει διαθέσιμο απόθεμα πάνω από το όριο.</td></tr>';
        return;
      }

      list.forEach(r => {
        const state = r.IsCurrentStore
          ? '<span class="ws-badge current">Τρέχον κατάστημα</span>'
          : (r.SupplierExists
              ? '<span class="ws-badge ok">✓ Προμηθευτής</span>'
              : '<button class="ws-btn secondary" disabled>Άνοιγμα</button>');
        const action = r.IsCurrentStore
          ? '<span style="color:#777">—</span>'
          : '<button class="ws-btn" disabled>Παραγγελία</button>';

        const tr = document.createElement('tr');
        tr.innerHTML =
          '<td>' + esc(r.StoreName) + '</td>' +
          '<td>' + esc(r.Afm) + '</td>' +
          '<td>' + esc(r.Phone) + '</td>' +
          '<td>' + esc(r.ItemCode) + '</td>' +
          '<td>' + esc(r.ItemName) + '</td>' +
          '<td>' + esc(r.Available) + '</td>' +
          '<td>' + state + '</td>' +
          '<td>' + (r.IsCurrentStore ? '—' : '<input class="ws-qty" type="number" min="1" max="' + esc(r.Available) + '" value="1">') + '</td>' +
          '<td>' + action + '</td>';
        rows.appendChild(tr);
      });
    }
  };

  function interceptStock(e) {
    const input = document.getElementById('promptInput');
    if (!input || !/^stock$/i.test(input.value.trim())) return false;
    e.preventDefault();
    e.stopImmediatePropagation();
    input.value = '';
    try { input.dispatchEvent(new Event('input', { bubbles: true })); } catch (_) { }
    open();
    return true;
  }

  const mainSend = document.getElementById('sendBtn');
  const mainInput = document.getElementById('promptInput');
  if (mainSend) mainSend.addEventListener('click', interceptStock, true);
  if (mainInput) {
    mainInput.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey) interceptStock(e);
    }, true);
  }
})();
