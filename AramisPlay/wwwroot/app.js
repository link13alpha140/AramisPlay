// ============================================================
// AramisPlay v2 — Logique Frontend (Zéro inline JSON, Multi-Filtres, Découverte)
// ============================================================

const mediaRegistry = new Map();

function registerMedia(item) {
  if (item && item.aniListId) {
    mediaRegistry.set(item.aniListId, item);
  }
  return item;
}

const SVG = {
  play: `<svg class="btn-svg" viewBox="0 0 24 24" style="fill:currentColor;stroke:none"><polygon points="6 4 20 12 6 20 6 4"/></svg>`,
  heart: `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>`,
  ext: `<svg class="btn-svg" viewBox="0 0 24 24"><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>`
};

const DISCOVERY_THEMES = [
  'Isekai', 'Action', 'Fantaisie', 'Vengeance', 'Réincarnation', 'Seinen',
  'Psychologique', 'Thriller', 'Arts Martiaux', 'Donjons', 'Démons', 'Anti-Héros',
  'Cyberpunk', 'Science-Fiction', 'Post-Apocalyptique', 'Survie', 'Voyage Temporel',
  'Romance', 'Drame', 'Comédie', 'Tranche de vie', 'Mystère', 'Enquête & Détective',
  'Horreur', 'Mafia & Yakuza', 'Samouraïs', 'Sport', 'Super-Pouvoirs'
];

const state = {
  currentView: 'home',
  stateVersion: 0,
  animeSamaBaseUrl: 'https://anime-sama.to',
  settings: {
    animeSamaBaseUrl: 'https://anime-sama.to',
    closeBehavior: 'tray',
    checkIntervalMinutes: 20,
    enableWindowsNotifications: true,
    autoIncrementOnWatchClick: true
  },
  allGenres: [],
  allTags: [],
  tagSearchQuery: '',

  library: [],
  notifications: [],

  // Catalogue & Multi-filtres
  catalogItems: [],
  catalogPreset: 'trending',
  catalogSearch: '',
  selectedGenres: [],
  selectedTags: [],
  catalogSort: '',
  catalogStatus: '',
  catalogFormat: '',
  catalogYear: '',
  catalogHideOwned: false,
  catalogPage: 1,

  // Découverte
  discoveryThemes: ['Isekai'],
  discoverySeenIds: [],
  discoveryPicks: [],

  // Calendrier & Autres
  scheduleItems: [],
  selectedScheduleDay: new Date().getDay(),
  libFilterStatus: 'ALL',
  libSearch: '',
  libSort: 'updated',
  quickItems: [],
  quickPage: 1,
  homeTrending: []
};

function esc(str) {
  if (str === null || str === undefined) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

async function api(path, options = {}) {
  const opts = {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options
  };
  const res = await fetch(path, opts);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const ct = res.headers.get('content-type') || '';
  return ct.includes('application/json') ? res.json() : res.text();
}

function showToast(msg) {
  const wrap = document.getElementById('toastContainer');
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = msg;
  wrap.appendChild(el);
  setTimeout(() => {
    el.style.opacity = '0';
    setTimeout(() => el.remove(), 220);
  }, 3000);
}

function statusLabelFr(status) {
  switch ((status || '').toUpperCase()) {
    case 'WATCHING': return 'En cours';
    case 'PLANNING': return 'À voir';
    case 'COMPLETED': return 'Terminé';
    case 'PAUSED': return 'En pause';
    case 'DROPPED': return 'Abandonné';
    default: return status;
  }
}

function releaseStatusFr(rs, startDateLabel) {
  switch ((rs || '').toUpperCase()) {
    case 'RELEASING': return 'En diffusion';
    case 'NOT_YET_RELEASED': return `Prochainement (${startDateLabel || 'Annoncé'})`;
    case 'FINISHED': return 'Terminé';
    case 'HIATUS': return 'En pause';
    default: return startDateLabel || '';
  }
}

function findLibEntryForCatalog(item) {
  if (!item) return null;
  return state.library.find(e =>
    (item.aniListId && e.aniListId === item.aniListId) ||
    (!e.aniListId && item.title && e.title && e.title.toLowerCase() === item.title.toLowerCase())
  ) || null;
}

// --- Navigation ---
function switchView(viewName) {
  state.currentView = viewName;
  document.querySelectorAll('.nav-item').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.view === viewName);
  });
  document.querySelectorAll('.view').forEach(sec => {
    sec.classList.toggle('active', sec.id === `view-${viewName}`);
  });

  if (viewName === 'library') renderLibrary();
  else if (viewName === 'catalog' && state.catalogItems.length === 0) loadCatalog(true);
  else if (viewName === 'discovery') loadTasteProfile();
  else if (viewName === 'schedule') {
    if (state.scheduleItems.length === 0) loadSchedule();
    else renderSchedule();
  }
  else if (viewName === 'quickpick') {
    if (state.quickItems.length === 0) loadQuickPick(true);
    else renderQuickPick();
  }
  else if (viewName === 'notifications') loadNotifications();
  else if (viewName === 'settings') renderSettings();
}
window.switchView = switchView;

// --- Démarrage ---
document.addEventListener('DOMContentLoaded', async () => {
  bindStaticEvents();
  bindDelegatedClicks();
  updateDayTabActive();
  renderDiscoveryThemeChips();

  await Promise.all([
    loadSettings(),
    loadLibrary(),
    loadNotifications(),
    loadGenresAndTagsMeta(),
    refreshSourceHealth()
  ]);

  loadHomeExtras();
  setInterval(pollBackendChanges, 3500);
});

// Délégation globale d'événements : 100% insensible aux apostrophes / caractères spéciaux !
function bindDelegatedClicks() {
  document.addEventListener('click', e => {
    const openEntryEl = e.target.closest('[data-open-entry]');
    if (openEntryEl) {
      e.stopPropagation();
      openEntryModal(openEntryEl.getAttribute('data-open-entry'));
      return;
    }

    const openMediaEl = e.target.closest('[data-open-anilist]');
    if (openMediaEl) {
      e.stopPropagation();
      const alId = parseInt(openMediaEl.getAttribute('data-open-anilist'), 10);
      const item = mediaRegistry.get(alId);
      if (!item) return;
      const existing = findLibEntryForCatalog(item);
      if (existing) openEntryModal(existing.id);
      else openCatalogModal(item);
      return;
    }

    const quickAddEl = e.target.closest('[data-quick-add]');
    if (quickAddEl) {
      e.stopPropagation();
      const alId = parseInt(quickAddEl.getAttribute('data-quick-add'), 10);
      const status = quickAddEl.getAttribute('data-watch-status') || 'PLANNING';
      const fav = quickAddEl.getAttribute('data-fav') === 'true';
      quickAddById(alId, status, fav);
      return;
    }
  });
}

function bindStaticEvents() {
  document.querySelectorAll('.nav-item').forEach(btn => {
    btn.addEventListener('click', () => switchView(btn.dataset.view));
  });

  // Recherche globale
  let searchDebounce = null;
  const gInput = document.getElementById('globalSearchInput');
  const clearBtn = document.getElementById('clearGlobalSearchBtn');

  gInput.addEventListener('input', () => {
    const val = gInput.value.trim();
    clearBtn.classList.toggle('hidden', !val);
    clearTimeout(searchDebounce);
    searchDebounce = setTimeout(() => {
      state.catalogSearch = val;
      if (state.currentView !== 'catalog') switchView('catalog');
      loadCatalog(true);
    }, 320);
  });

  clearBtn.addEventListener('click', () => {
    gInput.value = '';
    clearBtn.classList.add('hidden');
    state.catalogSearch = '';
    loadCatalog(true);
  });

  // Mode fermeture
  document.getElementById('toggleCloseModeBtn').addEventListener('click', async () => {
    const next = state.settings.closeBehavior === 'quit' ? 'tray' : 'quit';
    state.settings.closeBehavior = next;
    await saveSettings();
    showToast(next === 'tray'
      ? 'Fermeture : Veille légère active (~15 Mo RAM)'
      : 'Fermeture : Quitter complètement (0 Mo RAM)');
  });

  document.getElementById('checkNowTopBtn').addEventListener('click', triggerManualCheck);
  document.getElementById('notifCheckNowBtn').addEventListener('click', triggerManualCheck);

  document.getElementById('quitFullTopBtn').addEventListener('click', async () => {
    await api('/api/system/quit', { method: 'POST', body: '{}' });
  });

  document.getElementById('sourceStatusCard').addEventListener('click', async () => {
    document.getElementById('sourceStatusSub').textContent = 'Vérification du domaine…';
    const h = await api('/api/health/autodetect', { method: 'POST', body: '{}' });
    applyHealthUi(h);
    showToast(h.message || 'Domaine vérifié');
  });

  // Bibliothèque
  document.querySelectorAll('#libStatusTabs .tab-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#libStatusTabs .tab-pill').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.libFilterStatus = btn.dataset.status;
      renderLibrary();
    });
  });

  document.getElementById('libFilterInput').addEventListener('input', e => {
    state.libSearch = e.target.value.trim().toLowerCase();
    renderLibrary();
  });

  document.getElementById('libSortSelect').addEventListener('change', e => {
    state.libSort = e.target.value;
    renderLibrary();
  });

  // Catalogue & Tiroir Multi-Filtres
  document.getElementById('toggleFilterDrawerBtn').addEventListener('click', () => {
    document.getElementById('catalogFilterDrawer').classList.toggle('hidden');
  });

  document.getElementById('catalogHideOwnedChk').addEventListener('change', e => {
    state.catalogHideOwned = e.target.checked;
    renderCatalog();
  });

  document.querySelectorAll('#catalogPresetPills .tab-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#catalogPresetPills .tab-pill').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.catalogPreset = btn.dataset.preset;
      loadCatalog(true);
    });
  });

  document.getElementById('catSortSelect').addEventListener('change', e => {
    state.catalogSort = e.target.value;
    loadCatalog(true);
  });

  document.getElementById('catStatusSelect').addEventListener('change', e => {
    state.catalogStatus = e.target.value;
    loadCatalog(true);
  });

  document.getElementById('catFormatSelect').addEventListener('change', e => {
    state.catalogFormat = e.target.value;
    loadCatalog(true);
  });

  document.getElementById('catYearInput').addEventListener('change', e => {
    state.catalogYear = e.target.value.trim();
    loadCatalog(true);
  });

  document.getElementById('tagQuickFilterInput').addEventListener('input', e => {
    state.tagSearchQuery = e.target.value.trim().toLowerCase();
    renderFilterTagsChips();
  });

  document.getElementById('resetCatalogFiltersBtn').addEventListener('click', () => {
    state.selectedGenres = [];
    state.selectedTags = [];
    state.catalogSort = '';
    state.catalogStatus = '';
    state.catalogFormat = '';
    state.catalogYear = '';
    state.catalogSearch = '';
    document.getElementById('globalSearchInput').value = '';
    document.getElementById('clearGlobalSearchBtn').classList.add('hidden');
    document.getElementById('catSortSelect').value = '';
    document.getElementById('catStatusSelect').value = '';
    document.getElementById('catFormatSelect').value = '';
    document.getElementById('catYearInput').value = '';
    renderFilterGenreChips();
    renderFilterTagsChips();
    loadCatalog(true);
  });

  document.getElementById('catalogLoadMoreBtn').addEventListener('click', () => {
    state.catalogPage++;
    loadCatalog(false);
  });

  // Découverte
  document.getElementById('runDiscoveryBtn').addEventListener('click', () => {
    state.discoverySeenIds = [];
    runDiscovery();
  });

  document.getElementById('rerollDiscoveryBtn').addEventListener('click', () => {
    runDiscovery();
  });

  // Calendrier
  document.querySelectorAll('#scheduleDayTabs .tab-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      state.selectedScheduleDay = parseInt(btn.dataset.day, 10);
      updateDayTabActive();
      renderSchedule();
    });
  });

  // Ajout rapide
  document.getElementById('quickHideAddedToggle').addEventListener('change', renderQuickPick);
  document.getElementById('quickNextBatchBtn').addEventListener('click', () => {
    state.quickPage++;
    loadQuickPick(true);
  });

  // Notifications
  document.getElementById('notifReadAllBtn').addEventListener('click', async () => {
    await api('/api/notifications/read-all', { method: 'POST', body: '{}' });
    await loadNotifications();
    showToast('Notifications marquées comme lues');
  });

  document.getElementById('notifClearBtn').addEventListener('click', async () => {
    await api('/api/notifications', { method: 'DELETE' });
    await loadNotifications();
    showToast('Historique des notifications effacé');
  });

  // Paramètres
  document.querySelectorAll('input[name="closeBehavior"]').forEach(r => {
    r.addEventListener('change', async () => {
      state.settings.closeBehavior = r.value;
      await saveSettings();
    });
  });

  document.getElementById('setWinNotifs').addEventListener('change', async e => {
    state.settings.enableWindowsNotifications = e.target.checked;
    await saveSettings();
  });

  document.getElementById('setAutoInc').addEventListener('change', async e => {
    state.settings.autoIncrementOnWatchClick = e.target.checked;
    await saveSettings();
  });

  document.getElementById('setCheckInterval').addEventListener('change', async e => {
    state.settings.checkIntervalMinutes = parseInt(e.target.value, 10);
    await saveSettings();
    showToast('Intervalle enregistré');
  });

  document.getElementById('saveDomainBtn').addEventListener('click', async () => {
    const val = document.getElementById('setAnimeSamaUrl').value.trim();
    if (!val.startsWith('http')) {
      showToast('Indique une URL complète commençant par https://');
      return;
    }
    state.settings.animeSamaBaseUrl = val;
    await saveSettings();
    await refreshSourceHealth();
    showToast('Domaine enregistré');
  });

  document.getElementById('autoDetectDomainBtn').addEventListener('click', async () => {
    const fb = document.getElementById('domainDetectFeedback');
    fb.textContent = 'Recherche en cours…';
    const h = await api('/api/health/autodetect', { method: 'POST', body: '{}' });
    applyHealthUi(h);
    await loadSettings();
    renderSettings();
    fb.textContent = h.message || '';
  });

  document.getElementById('exportJsonBtn').addEventListener('click', async () => {
    const jsonText = await api('/api/library/export');
    const blob = new Blob([jsonText], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `aramisplay_backup_${new Date().toISOString().slice(0, 10)}.json`;
    a.click();
    showToast('Sauvegarde exportée');
  });

  document.getElementById('importJsonInput').addEventListener('change', async e => {
    const file = e.target.files[0];
    if (!file) return;
    const text = await file.text();
    const res = await api('/api/library/import', { method: 'POST', body: text });
    await loadLibrary();
    showToast(`${res.imported} animé(s) importés`);
    e.target.value = '';
  });

  // Modale
  document.getElementById('closeDetailModalBtn').addEventListener('click', closeDetailModal);
  document.getElementById('detailModalBackdrop').addEventListener('click', e => {
    if (e.target.id === 'detailModalBackdrop') closeDetailModal();
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') closeDetailModal();
  });
}

// --- Synchronisation Backend ---
async function pollBackendChanges() {
  try {
    const ping = await api('/api/ping');
    if (ping.stateVersion && ping.stateVersion !== state.stateVersion) {
      await loadLibrary();
      await loadNotifications();
      await refreshSourceHealth();
      if (state.currentView === 'catalog') renderCatalog();
      if (state.currentView === 'quickpick') renderQuickPick();
      if (state.currentView === 'discovery') loadTasteProfile();
    }
  } catch { }
}

async function loadSettings() {
  try {
    state.settings = await api('/api/settings');
    updateCloseModeChip();
  } catch { }
}

async function saveSettings() {
  state.settings = await api('/api/settings', {
    method: 'PUT',
    body: JSON.stringify(state.settings)
  });
  updateCloseModeChip();
}

function updateCloseModeChip() {
  const isQuit = state.settings.closeBehavior === 'quit';
  const dot = document.getElementById('closeModeDot');
  dot.classList.toggle('quit', isQuit);
  document.getElementById('closeModeText').textContent = isQuit
    ? 'Fermeture : 0 Mo'
    : 'Fermeture : Veille';
}

async function refreshSourceHealth() {
  try {
    const h = await api('/api/health/animesama');
    applyHealthUi(h);
  } catch { }
}

function applyHealthUi(h) {
  const dot = document.getElementById('sourceStatusDot');
  dot.className = 'status-dot ' + (h.state || '');
  const host = (h.domain || state.animeSamaBaseUrl).replace(/^https?:\/\//, '');
  document.getElementById('sourceDomainLabel').textContent = host;
  document.getElementById('sourceStatusSub').textContent = h.message || 'Prêt';
}

async function loadGenresAndTagsMeta() {
  try {
    const meta = await api('/api/catalog/genres');
    state.allGenres = meta.genres || [];
    state.allTags = meta.tags || [];

    if (meta.currentSeasonLabel) {
      document.getElementById('btnCurrentSeason').textContent = meta.currentSeasonLabel;
    }
    if (meta.nextSeasonLabel) {
      document.getElementById('btnNextSeason').textContent = meta.nextSeasonLabel;
    }

    renderFilterGenreChips();
    renderFilterTagsChips();
  } catch { }
}

// --- Chips Multi-Filtres Catalogue ---
function renderFilterGenreChips() {
  const wrap = document.getElementById('filterGenresContainer');
  wrap.innerHTML = state.allGenres.map(g => {
    const sel = state.selectedGenres.includes(g);
    return `<button class="filter-chip ${sel ? 'selected' : ''}" data-genre-chip="${esc(g)}">${esc(g)}</button>`;
  }).join('');

  wrap.querySelectorAll('[data-genre-chip]').forEach(btn => {
    btn.addEventListener('click', () => {
      const val = btn.getAttribute('data-genre-chip');
      if (state.selectedGenres.includes(val)) {
        state.selectedGenres = state.selectedGenres.filter(x => x !== val);
      } else {
        state.selectedGenres.push(val);
      }
      renderFilterGenreChips();
      loadCatalog(true);
    });
  });
}

function renderFilterTagsChips() {
  const wrap = document.getElementById('filterTagsContainer');
  const q = state.tagSearchQuery;
  const visibleTags = q
    ? state.allTags.filter(t => t.toLowerCase().includes(q) || state.selectedTags.includes(t))
    : state.allTags;

  wrap.innerHTML = visibleTags.map(t => {
    const sel = state.selectedTags.includes(t);
    return `<button class="filter-chip ${sel ? 'selected' : ''}" data-tag-chip="${esc(t)}">${esc(t)}</button>`;
  }).join('');

  wrap.querySelectorAll('[data-tag-chip]').forEach(btn => {
    btn.addEventListener('click', () => {
      const val = btn.getAttribute('data-tag-chip');
      if (state.selectedTags.includes(val)) {
        state.selectedTags = state.selectedTags.filter(x => x !== val);
      } else {
        state.selectedTags.push(val);
      }
      renderFilterTagsChips();
      loadCatalog(true);
    });
  });
}

function renderActiveFiltersBar() {
  const chips = [];
  if (state.catalogSearch) chips.push({ type: 'search', label: `Recherche : "${state.catalogSearch}"` });
  state.selectedGenres.forEach(g => chips.push({ type: 'genre', val: g, label: g }));
  state.selectedTags.forEach(t => chips.push({ type: 'tag', val: t, label: t }));
  if (state.catalogStatus) chips.push({ type: 'status', label: `Statut : ${releaseStatusFr(state.catalogStatus, '')}` });
  if (state.catalogFormat) chips.push({ type: 'format', label: `Format : ${state.catalogFormat}` });
  if (state.catalogYear) chips.push({ type: 'year', label: `Année : ${state.catalogYear}` });
  if (state.catalogSort) chips.push({ type: 'sort', label: `Tri personnalisé` });

  const countBadge = document.getElementById('activeFilterCountBadge');
  const filterCount = state.selectedGenres.length + state.selectedTags.length +
    (state.catalogStatus ? 1 : 0) + (state.catalogFormat ? 1 : 0) + (state.catalogYear ? 1 : 0) + (state.catalogSort ? 1 : 0);
  countBadge.textContent = filterCount;
  countBadge.classList.toggle('hidden', filterCount === 0);

  const bar = document.getElementById('activeFiltersBar');
  const list = document.getElementById('activeChipsList');
  bar.classList.toggle('hidden', chips.length === 0);

  list.innerHTML = chips.map((c, idx) =>
    `<button class="active-chip" data-remove-idx="${idx}">${esc(c.label)} <span>×</span></button>`
  ).join('');

  list.querySelectorAll('[data-remove-idx]').forEach(btn => {
    btn.addEventListener('click', () => {
      const c = chips[parseInt(btn.getAttribute('data-remove-idx'), 10)];
      if (c.type === 'search') {
        state.catalogSearch = '';
        document.getElementById('globalSearchInput').value = '';
        document.getElementById('clearGlobalSearchBtn').classList.add('hidden');
      } else if (c.type === 'genre') {
        state.selectedGenres = state.selectedGenres.filter(x => x !== c.val);
        renderFilterGenreChips();
      } else if (c.type === 'tag') {
        state.selectedTags = state.selectedTags.filter(x => x !== c.val);
        renderFilterTagsChips();
      } else if (c.type === 'status') {
        state.catalogStatus = '';
        document.getElementById('catStatusSelect').value = '';
      } else if (c.type === 'format') {
        state.catalogFormat = '';
        document.getElementById('catFormatSelect').value = '';
      } else if (c.type === 'year') {
        state.catalogYear = '';
        document.getElementById('catYearInput').value = '';
      } else if (c.type === 'sort') {
        state.catalogSort = '';
        document.getElementById('catSortSelect').value = '';
      }
      loadCatalog(true);
    });
  });
}

// --- Bibliothèque ---
async function loadLibrary() {
  const data = await api('/api/library');
  state.library = data.entries || [];
  state.stateVersion = data.stateVersion || state.stateVersion;
  state.animeSamaBaseUrl = data.animeSamaBaseUrl || state.animeSamaBaseUrl;

  document.getElementById('navLibCount').textContent = state.library.length;
  updateLibraryCounters();
  renderHome();
  if (state.currentView === 'library') renderLibrary();
}

function updateLibraryCounters() {
  const lib = state.library;
  document.getElementById('cnt-ALL').textContent = lib.length;
  document.getElementById('cnt-WATCHING').textContent = lib.filter(e => e.watchStatus === 'WATCHING').length;
  document.getElementById('cnt-FAVORITES').textContent = lib.filter(e => e.isFavorite).length;
  document.getElementById('cnt-PLANNING').textContent = lib.filter(e => e.watchStatus === 'PLANNING').length;
  document.getElementById('cnt-COMPLETED').textContent = lib.filter(e => e.watchStatus === 'COMPLETED').length;
  document.getElementById('cnt-PAUSED').textContent = lib.filter(e => e.watchStatus === 'PAUSED').length;
  document.getElementById('cnt-DROPPED').textContent = lib.filter(e => e.watchStatus === 'DROPPED').length;

  const catchupCount = lib.filter(e =>
    (e.watchStatus === 'WATCHING' || e.isFavorite) &&
    e.availableEpisodesVostfr > e.currentEpisode
  ).length;

  const badge = document.getElementById('navCatchupBadge');
  badge.textContent = catchupCount;
  badge.classList.toggle('hidden', catchupCount === 0);
}

async function loadHomeExtras() {
  try {
    const [trending, sched] = await Promise.all([
      api('/api/catalog?preset=trending&page=1&perPage=12'),
      api('/api/catalog/schedule')
    ]);
    state.homeTrending = (trending || []).map(registerMedia);
    state.scheduleItems = (sched || []).map(s => { registerMedia(s.media); return s; });
    renderHome();
  } catch { }
}

// --- Rendu Accueil ---
function renderHome() {
  const lib = state.library;
  document.getElementById('onboardingBanner').classList.toggle('hidden', lib.length > 0);

  const catchupItems = lib.filter(e =>
    (e.watchStatus === 'WATCHING' || (e.isFavorite && e.watchStatus !== 'COMPLETED' && e.watchStatus !== 'DROPPED')) &&
    e.availableEpisodesVostfr > e.currentEpisode
  );

  const catchupSec = document.getElementById('catchupSection');
  const catchupGrid = document.getElementById('catchupGrid');
  catchupSec.classList.toggle('hidden', catchupItems.length === 0);
  catchupGrid.innerHTML = catchupItems.map(e => renderContinueCardHtml(e, true)).join('');

  const watchingItems = lib.filter(e => e.watchStatus === 'WATCHING');
  const continueGrid = document.getElementById('continueGrid');
  if (watchingItems.length === 0) {
    continueGrid.innerHTML = `<div class="empty-state">Aucun animé en cours de lecture. Ajoute un titre depuis le Catalogue ou la Découverte.</div>`;
  } else {
    continueGrid.innerHTML = watchingItems.slice(0, 12).map(e => renderContinueCardHtml(e, false)).join('');
  }

  const todayGrid = document.getElementById('todayGrid');
  const todayNum = new Date().getDay();
  const todays = (state.scheduleItems || []).filter(s => new Date(s.airingAt * 1000).getDay() === todayNum);
  if (todays.length === 0) {
    todayGrid.innerHTML = `<div class="empty-state">Aucune diffusion programmée aujourd'hui ou chargement en cours.</div>`;
  } else {
    todayGrid.innerHTML = todays.slice(0, 8).map(s => {
      const timeStr = new Date(s.airingAt * 1000).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
      return renderPosterCardHtml(s.media, `${timeStr} · Ép. ${s.episode}`);
    }).join('');
  }

  const trGrid = document.getElementById('homeTrendingGrid');
  if (state.homeTrending.length > 0) {
    trGrid.innerHTML = state.homeTrending.map(m => renderPosterCardHtml(m)).join('');
  }
}

function renderContinueCardHtml(e, isCatchup) {
  const maxEp = e.availableEpisodesVostfr || e.totalEpisodesPlanned || Math.max(e.currentEpisode, 12);
  const pct = Math.min(100, Math.round((e.currentEpisode / Math.max(1, maxEp)) * 100));
  const diff = e.availableEpisodesVostfr - e.currentEpisode;
  const nextEp = e.availableEpisodesVostfr > 0
    ? Math.min(e.currentEpisode + 1, e.availableEpisodesVostfr)
    : e.currentEpisode + 1;

  const availText = e.availableEpisodesVostfr > 0
    ? `${e.availableEpisodesVostfr} dispo`
    : (e.totalEpisodesPlanned ? `${e.totalEpisodesPlanned} prévus` : 'VOSTFR');

  return `
    <div class="continue-card ${isCatchup ? 'catchup-highlight' : ''}">
      <img class="continue-poster" src="${esc(e.coverUrl)}" alt="" loading="lazy" data-open-entry="${esc(e.id)}" />
      <div class="continue-body">
        <div>
          <div class="continue-title" data-open-entry="${esc(e.id)}">${esc(e.title)}</div>
          <div class="continue-meta">
            <span>Épisode <strong>${e.currentEpisode}</strong> (${availText})</span>
            ${diff > 0 ? `<span class="badge-catchup">+${diff} NOUVEAU${diff > 1 ? 'X' : ''}</span>` : ''}
          </div>
          <div class="progress-track"><div class="progress-fill" style="width:${pct}%"></div></div>
        </div>
        <div class="continue-actions">
          <button class="mini-step-btn" title="Reculer d'un épisode" onclick="stepEpisode('${esc(e.id)}', -1)">−</button>
          <button class="mini-step-btn" title="Avancer d'un épisode" onclick="stepEpisode('${esc(e.id)}', 1)">+</button>
          <button class="watch-now-btn" onclick="watchEpisode('${esc(e.id)}', ${nextEp})">
            ${SVG.play}
            <span>Épisode ${nextEp}</span>
          </button>
        </div>
      </div>
    </div>
  `;
}

// --- Rendu Bibliothèque (avec notation directe /10 sur chaque carte !) ---
function renderLibrary() {
  let items = [...state.library];

  if (state.libFilterStatus === 'FAVORITES') {
    items = items.filter(e => e.isFavorite);
  } else if (state.libFilterStatus !== 'ALL') {
    items = items.filter(e => e.watchStatus === state.libFilterStatus);
  }

  if (state.libSearch) {
    items = items.filter(e =>
      (e.title || '').toLowerCase().includes(state.libSearch) ||
      (e.titleRomaji || '').toLowerCase().includes(state.libSearch) ||
      (e.titleEnglish || '').toLowerCase().includes(state.libSearch)
    );
  }

  if (state.libSort === 'catchup') {
    items.sort((a, b) => (b.availableEpisodesVostfr - b.currentEpisode) - (a.availableEpisodesVostfr - a.currentEpisode));
  } else if (state.libSort === 'title') {
    items.sort((a, b) => (a.title || '').localeCompare(b.title || ''));
  } else if (state.libSort === 'rating') {
    items.sort((a, b) => (b.personalRating || 0) - (a.personalRating || 0));
  }

  const grid = document.getElementById('libraryGrid');
  if (items.length === 0) {
    grid.innerHTML = `<div class="empty-state">Aucun animé dans cette section.</div>`;
    return;
  }

  grid.innerHTML = items.map(e => {
    const diff = e.availableEpisodesVostfr - e.currentEpisode;
    const maxEp = e.availableEpisodesVostfr || e.totalEpisodesPlanned || Math.max(e.currentEpisode, 12);
    const pct = Math.min(100, Math.round((e.currentEpisode / Math.max(1, maxEp)) * 100));
    const nextEp = e.availableEpisodesVostfr > 0
      ? Math.min(e.currentEpisode + 1, e.availableEpisodesVostfr)
      : e.currentEpisode + 1;

    const subStatus = e.availableEpisodesVostfr > 0
      ? `Ép. ${e.currentEpisode} / ${e.availableEpisodesVostfr} VOSTFR`
      : (e.releaseStatus === 'NOT_YET_RELEASED'
          ? `Sortie : ${e.startDateLabel || 'Prochainement'}`
          : `Ép. ${e.currentEpisode}${e.totalEpisodesPlanned ? ' / ' + e.totalEpisodesPlanned : ''}`);

    const ratingOpts = `<option value="0" ${!e.personalRating ? 'selected' : ''}>Noter</option>` +
      [10,9,8,7,6,5,4,3,2,1].map(n => `<option value="${n}" ${e.personalRating === n ? 'selected' : ''}>${n}/10</option>`).join('');

    return `
      <div class="lib-card">
        <img class="lib-card-poster" src="${esc(e.coverUrl)}" alt="" loading="lazy" data-open-entry="${esc(e.id)}" />
        <div class="lib-card-content">
          <div>
            <div class="lib-card-head">
              <div class="continue-title" data-open-entry="${esc(e.id)}">${esc(e.title)}</div>
              <div class="lib-head-tools">
                <select class="inline-rating-select" title="Noter cet animé (améliore tes recommandations)"
                        onchange="rateEntryInline('${esc(e.id)}', this.value)">
                  ${ratingOpts}
                </select>
                <button class="icon-toggle-btn ${e.isFavorite ? 'active' : ''}" title="Favori (suivi prioritaire)" onclick="toggleFavorite('${esc(e.id)}')">
                  ${SVG.heart}
                </button>
              </div>
            </div>
            <div class="continue-meta">
              <span>${statusLabelFr(e.watchStatus)}</span>
              ${e.animeSamaSeasonName ? `<span>· ${esc(e.animeSamaSeasonName)}</span>` : ''}
            </div>
            <div class="continue-meta" style="margin-top:4px">
              <span>${esc(subStatus)}</span>
              ${diff > 0 && e.watchStatus !== 'COMPLETED' ? `<span class="badge-catchup">+${diff} DISPO</span>` : ''}
            </div>
            <div class="progress-track"><div class="progress-fill" style="width:${pct}%"></div></div>
          </div>

          <div class="continue-actions">
            <button class="mini-step-btn" onclick="stepEpisode('${esc(e.id)}', -1)">−</button>
            <button class="mini-step-btn" onclick="stepEpisode('${esc(e.id)}', 1)">+</button>
            ${e.availableEpisodesVostfr === 0 && e.releaseStatus === 'NOT_YET_RELEASED'
              ? `<button class="action-btn ghost" style="flex:1;padding:5px 8px;font-size:11.5px" data-open-entry="${esc(e.id)}">Alerte programmée</button>`
              : `<button class="watch-now-btn" onclick="watchEpisode('${esc(e.id)}', ${nextEp})">${SVG.play} <span>Épisode ${nextEp}</span></button>`
            }
          </div>
        </div>
      </div>
    `;
  }).join('');
}

window.rateEntryInline = async function(id, valStr) {
  const rating = parseInt(valStr, 10);
  await api(`/api/library/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify({ personalRating: rating > 0 ? rating : null })
  });
  await loadLibrary();
  showToast(rating > 0 ? `Note enregistrée : ${rating}/10` : 'Note retirée');
};

// --- Catalogue Multi-Filtres ---
async function loadCatalog(reset = true) {
  renderActiveFiltersBar();

  if (reset) {
    state.catalogPage = 1;
    document.getElementById('catalogGrid').innerHTML = `<div class="empty-state">Recherche dans le catalogue…</div>`;
  }

  const filterBody = {
    preset: state.catalogPreset,
    search: state.catalogSearch || null,
    genresFr: state.selectedGenres,
    tagsFr: state.selectedTags,
    status: state.catalogStatus || null,
    format: state.catalogFormat || null,
    sort: state.catalogSort || null,
    year: state.catalogYear ? parseInt(state.catalogYear, 10) : null,
    page: state.catalogPage,
    perPage: 28
  };

  try {
    const res = await api('/api/catalog/query', {
      method: 'POST',
      body: JSON.stringify(filterBody)
    });
    const registered = (res || []).map(registerMedia);
    state.catalogItems = reset ? registered : [...state.catalogItems, ...registered];
    renderCatalog();
  } catch {
    document.getElementById('catalogGrid').innerHTML = `<div class="empty-state">Impossible de joindre le catalogue (vérifie ta connexion réseau).</div>`;
  }
}

function renderCatalog() {
  const grid = document.getElementById('catalogGrid');
  let list = state.catalogItems || [];
  if (state.catalogHideOwned) {
    list = list.filter(m => !findLibEntryForCatalog(m));
  }

  if (!list.length) {
    grid.innerHTML = `<div class="empty-state">Aucun animé ne correspond à cette combinaison de filtres.</div>`;
    return;
  }
  grid.innerHTML = list.map(m => renderPosterCardHtml(m)).join('');
}

function renderPosterCardHtml(m, customSub = '') {
  registerMedia(m);
  const inLib = findLibEntryForCatalog(m);
  let badgeRight = '';
  if (inLib) {
    if (inLib.watchStatus === 'COMPLETED') badgeRight = `<span class="pill-status-tag completed">Vu${inLib.personalRating ? ' · ' + inLib.personalRating + '/10' : ''}</span>`;
    else if (inLib.watchStatus === 'WATCHING') badgeRight = `<span class="pill-status-tag">Ép. ${inLib.currentEpisode}</span>`;
    else if (inLib.isFavorite) badgeRight = `<span class="pill-status-tag fav">Favori</span>`;
    else badgeRight = `<span class="pill-status-tag">À voir</span>`;
  } else if (m.releaseStatus === 'NOT_YET_RELEASED') {
    badgeRight = `<span class="pill-status-tag upcoming">${esc(m.startDateLabel)}</span>`;
  }

  const scoreStr = m.averageScore ? `${(m.averageScore / 10).toFixed(1)}` : '';
  const allLabels = [...(m.tagsFr || []).slice(0, 1), ...(m.genresFr || []).slice(0, 2)].slice(0, 2);
  const genresHtml = allLabels.map(g => `<span class="genre-mini">${esc(g)}</span>`).join('');

  return `
    <div class="poster-card" data-open-anilist="${m.aniListId}">
      <div class="poster-img-wrap">
        <img src="${esc(m.coverUrl)}" alt="" loading="lazy" />
        <div class="card-top-badges">
          ${scoreStr ? `<span class="pill-score">${scoreStr} / 10</span>` : '<span></span>'}
          ${badgeRight}
        </div>
      </div>
      <div class="poster-info">
        <div class="poster-title">${esc(m.title)}</div>
        <div class="poster-sub">
          ${customSub ? `<span>${esc(customSub)}</span>` : genresHtml}
        </div>
      </div>
    </div>
  `;
}

// ============================================================
// OUTIL DE DÉCOUVERTE (3 RECOMMANDATIONS PAR THÈME)
// ============================================================
function renderDiscoveryThemeChips() {
  const wrap = document.getElementById('discoveryThemeChips');
  wrap.innerHTML = DISCOVERY_THEMES.map(t => {
    const sel = state.discoveryThemes.includes(t);
    return `<button class="filter-chip ${sel ? 'selected' : ''}" data-disc-theme="${esc(t)}">${esc(t)}</button>`;
  }).join('');

  wrap.querySelectorAll('[data-disc-theme]').forEach(btn => {
    btn.addEventListener('click', () => {
      const val = btn.getAttribute('data-disc-theme');
      if (state.discoveryThemes.includes(val)) {
        if (state.discoveryThemes.length > 1) {
          state.discoveryThemes = state.discoveryThemes.filter(x => x !== val);
        }
      } else {
        if (state.discoveryThemes.length >= 3) {
          showToast('Sélectionne jusqu\'à 3 thèmes simultanés maximum');
          return;
        }
        state.discoveryThemes.push(val);
      }
      renderDiscoveryThemeChips();
    });
  });
}

async function loadTasteProfile() {
  try {
    const prof = await api('/api/discovery/profile');
    const summaryEl = document.getElementById('tasteSummaryLine');
    const tagsEl = document.getElementById('tasteTagsRow');

    if (prof.totalLibraryCount === 0) {
      summaryEl.textContent = 'Ta bibliothèque est encore vide. Note quelques animés pour personnaliser tes affinités !';
      tagsEl.innerHTML = '';
    } else if (prof.ratedEntriesCount === 0) {
      summaryEl.textContent = `Basé sur ${prof.totalLibraryCount} animé(s) suivi(s) — attribue des notes sur 10 dans ta bibliothèque pour affiner le profil.`;
      tagsEl.innerHTML = (prof.topTastes || []).map(t => `<span class="taste-pill">${esc(t.name)}</span>`).join('');
    } else {
      summaryEl.textContent = `Profil calculé sur ${prof.ratedEntriesCount} animé(s) noté(s) (${prof.totalLibraryCount} au total dans ta collection) :`;
      tagsEl.innerHTML = (prof.topTastes || []).map(t => `<span class="taste-pill">${esc(t.name)}</span>`).join('');
    }

    if (prof.dismissedCount > 0) {
      tagsEl.insertAdjacentHTML(
        'beforeend',
        `<button class="link-btn" style="font-size:11.5px;margin-left:8px" onclick="resetDismissedDiscovery()">Réinitialiser ${prof.dismissedCount} titre(s) masqué(s)</button>`
      );
    }
  } catch { }
}

async function runDiscovery() {
  const grid = document.getElementById('discoveryResultsGrid');
  const rerollBtn = document.getElementById('rerollDiscoveryBtn');
  grid.innerHTML = `<div class="empty-state">Analyse de ta bibliothèque, calcul des affinités et vérification VOSTFR sur Anime-Sama…</div>`;

  try {
    const res = await api('/api/discovery/recommend', {
      method: 'POST',
      body: JSON.stringify({
        themes: state.discoveryThemes,
        excludeAniListIds: state.discoverySeenIds
      })
    });

    const picks = res.picks || [];
    if (picks.length === 0) {
      grid.innerHTML = `<div class="empty-state">Aucun nouveau titre trouvé pour cette combinaison précise. Essaie un thème plus large !</div>`;
      return;
    }

    picks.forEach(p => {
      registerMedia(p.media);
      if (p.media.aniListId && !state.discoverySeenIds.includes(p.media.aniListId)) {
        state.discoverySeenIds.push(p.media.aniListId);
      }
    });

    state.discoveryPicks = picks;
    rerollBtn.classList.remove('hidden');
    renderDiscoveryPicks();
  } catch {
    grid.innerHTML = `<div class="empty-state">Une erreur est survenue lors du calcul des recommandations.</div>`;
  }
}

function renderDiscoveryPicks() {
  const grid = document.getElementById('discoveryResultsGrid');
  if (!state.discoveryPicks.length) {
    grid.innerHTML = `<div class="empty-state">Plus aucune proposition dans ce tirage. Relance une recherche !</div>`;
    return;
  }

  grid.innerHTML = state.discoveryPicks.map(p => {
    const m = p.media;
    const traits = [...(m.tagsFr || []).slice(0, 2), ...(m.genresFr || []).slice(0, 2)].slice(0, 4);
    const score = m.averageScore ? `${(m.averageScore / 10).toFixed(1)} / 10` : 'N/A';

    const ratingOptions = `<option value="">Déjà vu · Noter…</option>` +
      `<option value="0">Déjà vu (sans note)</option>` +
      [10,9,8,7,6,5,4,3,2,1].map(n => `<option value="${n}">Déjà vu · ${n}/10</option>`).join('');

    return `
      <div class="trio-card tier-${esc(p.tier)}" id="trio-slot-${esc(p.tier)}">
        <div class="trio-tier-header">
          <div>
            <div class="trio-tier-title">${esc(p.tierLabel)}</div>
            <div class="trio-tier-sub">${esc(p.tierSubtitle)}</div>
          </div>
          <span class="affinity-badge">${p.affinityPercent}% affinité</span>
        </div>

        <div class="trio-hero">
          <img class="trio-poster" src="${esc(m.coverUrl)}" alt="" data-open-anilist="${m.aniListId}" />
          <div class="trio-meta">
            <div class="trio-anime-title" data-open-anilist="${m.aniListId}">${esc(m.title)}</div>
            <div class="continue-meta">
              <span>Critique : <strong>${score}</strong></span>
              ${m.seasonYear ? `<span>· ${m.seasonYear}</span>` : ''}
              ${m.episodes ? `<span>· ${m.episodes} ép.</span>` : ''}
            </div>
            <div class="poster-sub" style="margin-top:4px">
              ${traits.map(tr => `<span class="genre-mini">${esc(tr)}</span>`).join('')}
            </div>
          </div>
        </div>

        <div class="trio-reason">${esc(p.reasonFr)}</div>
        <div class="trio-synopsis">${esc(m.synopsisFr || 'Aucun résumé disponible.')}</div>

        <div class="trio-footer">
          <div class="trio-avail-line">
            ${p.animeSamaFound
              ? `Disponible sur Anime-Sama (${p.availableEpisodesVostfr} ép. VOSTFR)`
              : `Fiche complète disponible au clic`
            }
          </div>
          <div class="trio-btn-row">
            <button class="action-btn primary" style="flex:1" data-open-anilist="${m.aniListId}">Voir la fiche</button>
            <button class="action-btn secondary" data-quick-add="${m.aniListId}" data-watch-status="PLANNING" data-fav="true">À voir</button>
          </div>
          <div class="trio-quick-bar">
            <select class="trio-seen-select" onchange="markDiscoverySeenAndRate('${esc(p.tier)}', ${m.aniListId}, this.value)">
              ${ratingOptions}
            </select>
            <button class="trio-dismiss-btn" title="Ne plus jamais me proposer cet animé" onclick="dismissDiscoveryPick('${esc(p.tier)}', ${m.aniListId})">
              Pas intéressé
            </button>
          </div>
        </div>
      </div>
    `;
  }).join('');
}

async function replaceDiscoverySlot(tier) {
  const slotEl = document.getElementById(`trio-slot-${tier}`);
  if (slotEl) slotEl.style.opacity = '0.45';

  try {
    const res = await api('/api/discovery/replace-one', {
      method: 'POST',
      body: JSON.stringify({
        tier,
        themes: state.discoveryThemes,
        excludeAniListIds: state.discoverySeenIds
      })
    });

    if (res && res.ok && res.pick) {
      registerMedia(res.pick.media);
      if (res.pick.media.aniListId && !state.discoverySeenIds.includes(res.pick.media.aniListId)) {
        state.discoverySeenIds.push(res.pick.media.aniListId);
      }
      const idx = state.discoveryPicks.findIndex(x => x.tier === tier);
      if (idx >= 0) state.discoveryPicks[idx] = res.pick;
    } else {
      state.discoveryPicks = state.discoveryPicks.filter(x => x.tier !== tier);
    }
  } catch {
    if (slotEl) slotEl.style.opacity = '1';
  }
  renderDiscoveryPicks();
}

window.markDiscoverySeenAndRate = async function(tier, aniListId, ratingStr) {
  if (ratingStr === '') return;
  const m = mediaRegistry.get(aniListId);
  if (!m) return;

  const rating = parseInt(ratingStr, 10);
  await api('/api/library/add', {
    method: 'POST',
    body: JSON.stringify({
      ...m,
      watchStatus: 'COMPLETED',
      currentEpisode: m.episodes || 0,
      personalRating: rating > 0 ? rating : null
    })
  });

  showToast(rating > 0 ? `${m.title} classé Déjà vu (${rating}/10)` : `${m.title} classé Déjà vu`);
  await loadLibrary();
  await loadTasteProfile();
  await replaceDiscoverySlot(tier);
};

window.dismissDiscoveryPick = async function(tier, aniListId) {
  const m = mediaRegistry.get(aniListId);
  await api('/api/discovery/dismiss', {
    method: 'POST',
    body: JSON.stringify({ aniListId })
  });
  showToast(m ? `${m.title} masqué définitivement` : 'Proposition masquée');
  await loadTasteProfile();
  await replaceDiscoverySlot(tier);
};

window.resetDismissedDiscovery = async function() {
  await api('/api/discovery/reset-dismissed', { method: 'POST', body: '{}' });
  showToast('Liste des titres masqués réinitialisée');
  await loadTasteProfile();
};

// --- Calendrier ---
function updateDayTabActive() {
  document.querySelectorAll('#scheduleDayTabs .tab-pill').forEach(btn => {
    btn.classList.toggle('active', parseInt(btn.dataset.day, 10) === state.selectedScheduleDay);
  });
}

async function loadSchedule() {
  document.getElementById('scheduleList').innerHTML = `<div class="empty-state">Chargement du programme…</div>`;
  try {
    const res = await api('/api/catalog/schedule');
    state.scheduleItems = (res || []).map(s => { registerMedia(s.media); return s; });
    renderSchedule();
  } catch {
    document.getElementById('scheduleList').innerHTML = `<div class="empty-state">Impossible de charger le calendrier.</div>`;
  }
}

function renderSchedule() {
  const list = document.getElementById('scheduleList');
  const dayItems = (state.scheduleItems || []).filter(s => new Date(s.airingAt * 1000).getDay() === state.selectedScheduleDay);

  if (dayItems.length === 0) {
    list.innerHTML = `<div class="empty-state">Aucune diffusion répertoriée ce jour.</div>`;
    return;
  }

  list.innerHTML = dayItems.map(s => {
    const timeStr = new Date(s.airingAt * 1000).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
    const inLib = findLibEntryForCatalog(s.media);
    return `
      <div class="sched-card ${inLib ? 'followed' : ''}" data-open-anilist="${s.media.aniListId}">
        <div class="sched-time">${timeStr}</div>
        <img class="sched-poster" src="${esc(s.media.coverUrl)}" alt="" loading="lazy" />
        <div style="flex:1;min-width:0">
          <div class="continue-title">${esc(s.media.title)}</div>
          <div class="continue-meta">
            <strong>Épisode ${s.episode}</strong>
            ${inLib ? `<span class="badge-catchup">SUIVI</span>` : ''}
          </div>
          <div class="poster-sub" style="margin-top:5px">
            ${(s.media.genresFr || []).slice(0, 3).map(g => `<span class="genre-mini">${esc(g)}</span>`).join('')}
          </div>
        </div>
      </div>
    `;
  }).join('');
}

// --- Ajout Rapide ---
async function loadQuickPick(reset = false) {
  const grid = document.getElementById('quickPickGrid');
  if (reset) grid.innerHTML = `<div class="empty-state">Chargement des titres populaires…</div>`;
  try {
    const res = await api(`/api/catalog?preset=popular&page=${state.quickPage}&perPage=30`);
    state.quickItems = (res || []).map(registerMedia);
    renderQuickPick();
  } catch { }
}

function renderQuickPick() {
  const hideAdded = document.getElementById('quickHideAddedToggle').checked;
  const grid = document.getElementById('quickPickGrid');

  let items = state.quickItems || [];
  if (hideAdded) items = items.filter(m => !findLibEntryForCatalog(m));

  if (items.length === 0) {
    grid.innerHTML = `<div class="empty-state">Tous les animés de cette page sont déjà classés. Passe à la série suivante !</div>`;
    return;
  }

  grid.innerHTML = items.map(m => {
    const inLib = findLibEntryForCatalog(m);
    return `
      <div class="quick-card">
        <div class="poster-img-wrap" style="cursor:pointer" data-open-anilist="${m.aniListId}">
          <img src="${esc(m.coverUrl)}" alt="" loading="lazy" />
          <div class="card-top-badges">
            ${m.averageScore ? `<span class="pill-score">${(m.averageScore / 10).toFixed(1)}</span>` : '<span></span>'}
            ${inLib ? `<span class="pill-status-tag completed">${statusLabelFr(inLib.watchStatus)}</span>` : ''}
          </div>
        </div>
        <div class="poster-info" style="padding:9px 10px">
          <div class="poster-title" style="font-size:13px">${esc(m.title)}</div>
        </div>
        <div class="quick-actions">
          <button class="q-btn done" data-quick-add="${m.aniListId}" data-watch-status="COMPLETED">Déjà vu</button>
          <button class="q-btn watch" data-quick-add="${m.aniListId}" data-watch-status="WATCHING">En cours</button>
          <button class="q-btn plan" data-quick-add="${m.aniListId}" data-watch-status="PLANNING">À voir</button>
        </div>
      </div>
    `;
  }).join('');
}

async function quickAddById(aniListId, watchStatus, isFav = false) {
  const m = mediaRegistry.get(aniListId);
  if (!m) return;
  await api('/api/library/add', {
    method: 'POST',
    body: JSON.stringify({
      ...m,
      watchStatus,
      isFavorite: isFav || (watchStatus === 'PLANNING' && m.releaseStatus === 'NOT_YET_RELEASED')
    })
  });
  await loadLibrary();
  if (state.currentView === 'quickpick') renderQuickPick();
  if (state.currentView === 'catalog') renderCatalog();
  showToast(`${m.title} classé (${statusLabelFr(watchStatus)})`);
}

// --- Notifications ---
async function loadNotifications() {
  try {
    state.notifications = await api('/api/notifications');
    const unread = state.notifications.filter(n => !n.isRead).length;
    const badge = document.getElementById('navNotifBadge');
    badge.textContent = unread;
    badge.classList.toggle('hidden', unread === 0);
    if (state.currentView === 'notifications') renderNotifications();
  } catch { }
}

function renderNotifications() {
  const list = document.getElementById('notificationsList');
  if (!state.notifications.length) {
    list.innerHTML = `<div class="empty-state">Aucune alerte pour le moment. Les sorties VOSTFR de tes animés suivis s'afficheront ici.</div>`;
    return;
  }

  list.innerHTML = state.notifications.map(n => {
    const dt = new Date(n.createdAt).toLocaleString('fr-FR', {
      day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit'
    });
    return `
      <div class="notif-item ${n.isRead ? '' : 'unread'}">
        <img class="notif-cover" src="${esc(n.coverUrl)}" alt="" />
        <div class="notif-body">
          <div class="notif-title">${esc(n.animeTitle)}</div>
          <div class="notif-msg">${esc(n.message)}</div>
          <div class="notif-date">${dt}</div>
        </div>
        ${n.watchUrl ? `<button class="action-btn primary" onclick="openExternalUrl('${esc(n.watchUrl)}')">${SVG.play} <span>Regarder</span></button>` : ''}
      </div>
    `;
  }).join('');
}

async function triggerManualCheck() {
  const icon = document.getElementById('checkIconSvg');
  icon.classList.add('spin');
  showToast('Vérification des sorties VOSTFR sur Anime-Sama…');
  try {
    const res = await api('/api/notifications/check-now', { method: 'POST', body: '{}' });
    await loadLibrary();
    await loadNotifications();
    await refreshSourceHealth();
    if (res.newCount > 0) {
      showToast(`${res.newCount} nouvel(s) épisode(s) VOSTFR disponible(s)`);
    } else {
      showToast('Tous les animés suivis sont à jour');
    }
  } catch {
    showToast('Échec de la vérification réseau');
  } finally {
    icon.classList.remove('spin');
  }
}

// --- Paramètres ---
function renderSettings() {
  const s = state.settings;
  document.querySelectorAll('input[name="closeBehavior"]').forEach(r => {
    r.checked = r.value === (s.closeBehavior || 'tray');
  });
  document.getElementById('setWinNotifs').checked = !!s.enableWindowsNotifications;
  document.getElementById('setAutoInc').checked = !!s.autoIncrementOnWatchClick;
  document.getElementById('setCheckInterval').value = String(s.checkIntervalMinutes || 20);
  document.getElementById('setAnimeSamaUrl').value = s.animeSamaBaseUrl || 'https://anime-sama.to';
}

// --- Actions épisodes ---
window.stepEpisode = async function(id, delta) {
  const entry = state.library.find(e => e.id === id);
  if (!entry) return;
  const next = Math.max(0, entry.currentEpisode + delta);
  await api(`/api/library/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify({ currentEpisode: next })
  });
  await loadLibrary();
};

window.toggleFavorite = async function(id) {
  const entry = state.library.find(e => e.id === id);
  if (!entry) return;
  const nextFav = !entry.isFavorite;
  await api(`/api/library/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify({ isFavorite: nextFav })
  });
  await loadLibrary();
  showToast(nextFav ? `${entry.title} ajouté aux favoris` : `${entry.title} retiré des favoris`);
};

window.watchEpisode = async function(id, ep) {
  try {
    const res = await api(`/api/library/${encodeURIComponent(id)}/watch`, {
      method: 'POST',
      body: JSON.stringify({ episode: ep })
    });
    await loadLibrary();
    showToast(`Ouverture de l'épisode ${ep} sur Anime-Sama`);
    return res;
  } catch {
    showToast('Impossible d\'ouvrir Anime-Sama');
  }
};

window.openExternalUrl = async function(url) {
  await api('/api/system/open-external', {
    method: 'POST',
    body: JSON.stringify({ url })
  });
};

// ============================================================
// MODALE FICHE DÉTAILLÉE
// ============================================================
function buildTrailerBoxHtml(ytId) {
  if (!ytId) return '';
  const cleanId = esc(ytId);
  return `
    <div class="modal-box">
      <div class="trailer-head-row">
        <h4>Bande-annonce officielle (VO)</h4>
        <button class="action-btn ghost" style="padding:4px 10px;font-size:11.5px"
                onclick="openExternalUrl('https://www.youtube.com/watch?v=${cleanId}')">
          ${SVG.ext} <span>YouTube</span>
        </button>
      </div>
      <div id="trailerMountArea">
        <div class="trailer-preview-card"
             style="background-image:url('https://i.ytimg.com/vi/${cleanId}/hqdefault.jpg')"
             onclick="playInlineTrailer('${cleanId}')">
          <div class="trailer-play-pill">
            ${SVG.play}
            <span>Visionner la bande-annonce</span>
          </div>
        </div>
      </div>
    </div>
  `;
}

window.playInlineTrailer = function(ytId) {
  const mount = document.getElementById('trailerMountArea');
  if (!mount || !ytId) return;
  mount.innerHTML = `
    <div class="trailer-iframe-wrap">
      <iframe class="trailer-iframe"
              src="https://www.youtube-nocookie.com/embed/${encodeURIComponent(ytId)}?autoplay=1&rel=0"
              referrerpolicy="strict-origin-when-cross-origin"
              allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
              allowfullscreen></iframe>
    </div>
  `;
};

async function loadFranchiseAndTrailerIntoModal(aniListId, initialTrailerYtId) {
  if (!aniListId || aniListId <= 0) return;

  try {
    const data = await api(`/api/catalog/franchise/${aniListId}`);
    if (document.getElementById('detailModalBackdrop').classList.contains('hidden')) return;

    const trailerSlot = document.getElementById('modalTrailerSlot');
    if (trailerSlot && !initialTrailerYtId && data.trailerYoutubeId) {
      trailerSlot.innerHTML = buildTrailerBoxHtml(data.trailerYoutubeId);
    }

    const franchiseSlot = document.getElementById('modalFranchiseSlot');
    const rels = data.relations || [];
    if (!franchiseSlot || rels.length < 2) return;

    rels.forEach(r => registerMedia(r.media));

    franchiseSlot.innerHTML = `
      <div class="modal-box">
        <h4>Ordre de visionnage & Franchise (${rels.length} entrées)</h4>
        <div class="franchise-list">
          ${rels.map(r => {
            const m = r.media;
            const inLib = findLibEntryForCatalog(m);
            let rightTag = '';
            if (r.isCurrent) {
              rightTag = `<span class="tag-chip gold">Fiche ouverte</span>`;
            } else if (inLib) {
              if (inLib.watchStatus === 'COMPLETED') {
                rightTag = `<span class="pill-status-tag completed">Vu${inLib.personalRating ? ' · ' + inLib.personalRating + '/10' : ''}</span>`;
              } else if (inLib.watchStatus === 'WATCHING') {
                rightTag = `<span class="pill-status-tag">Ép. ${inLib.currentEpisode}</span>`;
              } else {
                rightTag = `<span class="pill-status-tag">À voir</span>`;
              }
            } else if (m.releaseStatus === 'NOT_YET_RELEASED') {
              rightTag = `<span class="pill-status-tag upcoming">${esc(m.startDateLabel)}</span>`;
            }

            const clickAttr = r.isCurrent ? '' : `onclick="openFranchiseStep(${m.aniListId})"`;
            return `
              <div class="franchise-step-card ${r.isCurrent ? 'is-current' : ''}" ${clickAttr}>
                <div class="franchise-step-num">${r.orderIndex}</div>
                <img class="franchise-poster" src="${esc(m.coverUrl)}" alt="" loading="lazy" />
                <div class="franchise-info">
                  <div class="franchise-title">${esc(m.title)}</div>
                  <div class="franchise-sub">
                    <span>${esc(r.relationLabelFr)}</span>
                    ${m.seasonYear ? `<span>· ${m.seasonYear}</span>` : ''}
                    ${m.episodes ? `<span>· ${m.episodes} ép.</span>` : ''}
                    ${m.averageScore ? `<span>· ${(m.averageScore / 10).toFixed(1)}/10</span>` : ''}
                  </div>
                </div>
                <div>${rightTag}</div>
              </div>
            `;
          }).join('')}
        </div>
      </div>
    `;
  } catch { }
}

window.openFranchiseStep = function(aniListId) {
  const m = mediaRegistry.get(aniListId);
  if (!m) return;
  const inLib = findLibEntryForCatalog(m);
  if (inLib) {
    openEntryModal(inLib.id);
  } else {
    openCatalogModal(m);
  }
};

window.openEntryModal = async function(entryId) {
  const entry = state.library.find(e => e.id === entryId);
  if (!entry) return;

  const backdrop = document.getElementById('detailModalBackdrop');
  backdrop.classList.remove('hidden');
  renderEntryModalInner(entry);

  if (!entry.synopsisFr || (!entry.animeSamaSlug && entry.releaseStatus !== 'NOT_YET_RELEASED')) {
    try {
      const refreshed = await api(`/api/library/${encodeURIComponent(entry.id)}/refresh`, { method: 'POST', body: '{}' });
      if (refreshed && refreshed.id) {
        await loadLibrary();
        const latest = state.library.find(e => e.id === entry.id) || refreshed;
        if (!backdrop.classList.contains('hidden')) renderEntryModalInner(latest);
      }
    } catch { }
  }
};

function renderEntryModalInner(e) {
  const content = document.getElementById('detailModalContent');
  const nextEp = e.availableEpisodesVostfr > 0
    ? Math.min(e.currentEpisode + 1, e.availableEpisodesVostfr)
    : e.currentEpisode + 1;

  const seasonsOptions = (e.availableSeasons || []).map(s =>
    `<option value="${esc(s.path)}" ${s.path === e.animeSamaSeasonPath ? 'selected' : ''}>${esc(s.name)} (${esc(s.path)})</option>`
  ).join('');

  const allChips = [
    ...(e.genresFr || []).map(g => `<span class="tag-chip gold">${esc(g)}</span>`),
    ...(e.tagsFr || []).slice(0, 6).map(t => `<span class="tag-chip">${esc(t)}</span>`)
  ].join('');

  content.innerHTML = `
    <div class="modal-banner" style="background-image:url('${esc(e.bannerUrl || e.coverUrl)}')"></div>
    <div class="modal-main">
      <div class="modal-top">
        <img class="modal-poster" src="${esc(e.coverUrl)}" alt="" />
        <div class="modal-title-block">
          <h2>${esc(e.title)}</h2>
          ${e.titleEnglish && e.titleEnglish !== e.title ? `<div class="modal-alt">${esc(e.titleEnglish)}</div>` : ''}
          <div class="modal-tags">
            ${e.averageScore ? `<span class="tag-chip gold">Score ${(e.averageScore / 10).toFixed(1)}/10</span>` : ''}
            <span class="tag-chip">${esc(releaseStatusFr(e.releaseStatus, e.startDateLabel) || e.format)}</span>
            ${allChips}
          </div>
          <div style="margin-top:15px;display:flex;gap:9px;flex-wrap:wrap">
            ${e.animeSamaSlug
              ? `<button class="action-btn primary" onclick="watchEpisode('${esc(e.id)}', ${nextEp})">${SVG.play} <span>Lancer l'Épisode ${nextEp} (VOSTFR)</span></button>`
              : `<button class="action-btn secondary" onclick="openExternalUrl('${esc(state.animeSamaBaseUrl)}/catalogue/?search=${encodeURIComponent(e.title)}')">${SVG.ext} <span>Chercher sur Anime-Sama</span></button>`
            }
            <button class="action-btn ${e.isFavorite ? 'primary' : 'secondary'}" onclick="modalToggleFav('${esc(e.id)}')">
              ${e.isFavorite ? 'Retirer des favoris' : 'Ajouter aux favoris (Alerte)'}
            </button>
            <button class="action-btn danger-ghost" onclick="modalDeleteEntry('${esc(e.id)}')">Supprimer</button>
          </div>
        </div>
      </div>

      <div class="modal-synopsis">${esc(e.synopsisFr || 'Aucun résumé disponible.')}</div>

      <div id="modalTrailerSlot">${buildTrailerBoxHtml(e.trailerYoutubeId)}</div>

      <div class="modal-box">
        <h4>Progression & Note personnelle</h4>
        <div class="modal-controls-grid">
          <div>
            <label class="field-lbl">Statut</label>
            <select class="small-select" style="width:100%" onchange="modalPatchEntry('${esc(e.id)}', { watchStatus: this.value })">
              <option value="WATCHING" ${e.watchStatus === 'WATCHING' ? 'selected' : ''}>En cours</option>
              <option value="PLANNING" ${e.watchStatus === 'PLANNING' ? 'selected' : ''}>À voir / Attendu</option>
              <option value="COMPLETED" ${e.watchStatus === 'COMPLETED' ? 'selected' : ''}>Terminé</option>
              <option value="PAUSED" ${e.watchStatus === 'PAUSED' ? 'selected' : ''}>En pause</option>
              <option value="DROPPED" ${e.watchStatus === 'DROPPED' ? 'selected' : ''}>Abandonné</option>
            </select>
          </div>

          <div>
            <label class="field-lbl">Épisode actuel ${e.availableEpisodesVostfr ? `(sur ${e.availableEpisodesVostfr} en ligne)` : ''}</label>
            <div style="display:flex;gap:6px">
              <button class="mini-step-btn" onclick="modalPatchEntry('${esc(e.id)}', { currentEpisode: Math.max(0, ${e.currentEpisode - 1}) })">−</button>
              <input type="number" min="0" class="small-input" style="width:80px;text-align:center" value="${e.currentEpisode}"
                     onchange="modalPatchEntry('${esc(e.id)}', { currentEpisode: parseInt(this.value || '0', 10) })" />
              <button class="mini-step-btn" onclick="modalPatchEntry('${esc(e.id)}', { currentEpisode: ${e.currentEpisode + 1} })">+</button>
            </div>
          </div>

          <div>
            <label class="field-lbl">Ma note (/10)</label>
            <select class="small-select" style="width:100%" onchange="modalPatchEntry('${esc(e.id)}', { personalRating: this.value ? parseInt(this.value, 10) : null })">
              <option value="">Non noté</option>
              ${[10,9,8,7,6,5,4,3,2,1].map(n => `<option value="${n}" ${e.personalRating === n ? 'selected' : ''}>${n} / 10</option>`).join('')}
            </select>
          </div>
        </div>

        <div style="margin-top:12px">
          <label class="field-lbl">Note personnelle / mémo</label>
          <input type="text" class="small-input" style="width:100%" placeholder="Arc actuel, commentaire, minute d'arrêt…"
                 value="${esc(e.notes || '')}"
                 onchange="modalPatchEntry('${esc(e.id)}', { notes: this.value })" />
        </div>
      </div>

      <div id="modalFranchiseSlot"></div>

      <div class="modal-box">
        <h4>Source Anime-Sama (VOSTFR)</h4>
        ${e.animeSamaSlug
          ? `<div style="display:flex;flex-wrap:wrap;gap:12px;align-items:end">
               <div style="flex:1;min-width:220px">
                 <label class="field-lbl">Fiche <code>/catalogue/${esc(e.animeSamaSlug)}/</code> — Saison suivie :</label>
                 ${seasonsOptions
                   ? `<select class="small-select" style="width:100%" onchange="modalPatchEntry('${esc(e.id)}', { animeSamaSeasonPath: this.value })">${seasonsOptions}</select>`
                   : `<div class="muted-note">${esc(e.animeSamaSeasonPath)}</div>`
                 }
               </div>
               <div>
                 <span class="tag-chip gold">${e.availableEpisodesVostfr} épisode(s) VOSTFR disponible(s)</span>
               </div>
             </div>`
          : `<p class="card-desc">Pas encore de fiche VOSTFR détectée sur Anime-Sama. S'il s'agit d'une sortie future, AramisPlay te notifiera dès la mise en ligne de l'épisode 1.</p>`
        }

        <details style="margin-top:10px">
          <summary style="cursor:pointer;color:var(--text-muted);font-size:12px">Relier manuellement une autre fiche Anime-Sama</summary>
          <div style="display:flex;gap:8px;margin-top:8px">
            <input type="text" id="manualAsQuery" class="small-input grow" value="${esc(e.title)}" placeholder="Titre sur Anime-Sama…" />
            <button class="action-btn secondary" onclick="searchManualAnimeSama('${esc(e.id)}')">Rechercher</button>
          </div>
          <div id="manualAsResults" style="margin-top:8px;display:flex;flex-direction:column;gap:6px"></div>
        </details>
      </div>
    </div>
  `;

  if (e.aniListId) {
    loadFranchiseAndTrailerIntoModal(e.aniListId, e.trailerYoutubeId);
  }
}

async function openCatalogModal(item) {
  registerMedia(item);
  const backdrop = document.getElementById('detailModalBackdrop');
  const content = document.getElementById('detailModalContent');
  backdrop.classList.remove('hidden');

  const allChips = [
    ...(item.genresFr || []).map(g => `<span class="tag-chip gold">${esc(g)}</span>`),
    ...(item.tagsFr || []).slice(0, 6).map(t => `<span class="tag-chip">${esc(t)}</span>`)
  ].join('');

  content.innerHTML = `
    <div class="modal-banner" style="background-image:url('${esc(item.bannerUrl || item.coverUrl)}')"></div>
    <div class="modal-main">
      <div class="modal-top">
        <img class="modal-poster" src="${esc(item.coverUrl)}" alt="" />
        <div class="modal-title-block">
          <h2>${esc(item.title)}</h2>
          ${item.titleEnglish && item.titleEnglish !== item.title ? `<div class="modal-alt">${esc(item.titleEnglish)}</div>` : ''}
          <div class="modal-tags">
            ${item.averageScore ? `<span class="tag-chip gold">Score ${(item.averageScore / 10).toFixed(1)}/10</span>` : ''}
            <span class="tag-chip">${esc(releaseStatusFr(item.releaseStatus, item.startDateLabel))}</span>
            ${(item.studios || []).slice(0, 1).map(s => `<span class="tag-chip">${esc(s)}</span>`).join('')}
            ${allChips}
          </div>

          <div style="margin-top:15px;display:flex;gap:9px;flex-wrap:wrap">
            <button class="action-btn primary" onclick="addFromCatalogModal(${item.aniListId}, 'WATCHING', false)">Commencer (En cours)</button>
            <button class="action-btn secondary" onclick="addFromCatalogModal(${item.aniListId}, 'PLANNING', true)">Favori & Alerte sorties</button>
            <button class="action-btn ghost" onclick="addFromCatalogModal(${item.aniListId}, 'COMPLETED', false)">Marquer déjà vu</button>
          </div>
        </div>
      </div>

      <div class="modal-synopsis" id="catModalSynopsis">${esc(item.synopsisFr || 'Chargement du résumé en français…')}</div>

      <div id="modalTrailerSlot">${buildTrailerBoxHtml(item.trailerYoutubeId)}</div>

      <div id="modalFranchiseSlot"></div>

      <div class="modal-box" id="catModalAsStatus">
        <h4>Disponibilité Anime-Sama (VOSTFR)</h4>
        <p class="card-desc">Vérification sur <code>${esc(state.animeSamaBaseUrl)}</code>…</p>
      </div>
    </div>
  `;

  if (item.aniListId) {
    loadFranchiseAndTrailerIntoModal(item.aniListId, item.trailerYoutubeId);
  }

  try {
    const enriched = await api('/api/catalog/enrich', {
      method: 'POST',
      body: JSON.stringify(item)
    });
    item.synopsisFr = enriched.synopsisFr || item.synopsisFr;
    const synEl = document.getElementById('catModalSynopsis');
    if (synEl) synEl.textContent = enriched.synopsisFr || 'Aucun résumé disponible.';

    const asBox = document.getElementById('catModalAsStatus');
    if (asBox) {
      if (enriched.animeSamaFound) {
        asBox.innerHTML = `
          <h4>Disponibilité Anime-Sama (VOSTFR)</h4>
          <div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:10px">
            <span class="tag-chip gold">Disponible — ${esc(enriched.matchedSeasonName)} (${enriched.availableEpisodesVostfr} ép. VOSTFR)</span>
            <button class="action-btn secondary" onclick="openExternalUrl('${esc(enriched.watchUrl)}')">${SVG.ext} <span>Ouvrir sur Anime-Sama</span></button>
          </div>
        `;
      } else {
        asBox.innerHTML = `
          <h4>Disponibilité Anime-Sama (VOSTFR)</h4>
          <p class="card-desc">Pas encore disponible en VOSTFR sur Anime-Sama (${esc(item.startDateLabel || 'Sortie à venir')}). Ajoute-le en Favori pour recevoir une alerte dès la sortie de l'épisode 1.</p>
        `;
      }
    }
  } catch { }
}

window.addFromCatalogModal = async function(aniListId, watchStatus, isFavorite) {
  const item = mediaRegistry.get(aniListId);
  if (!item) return;
  const saved = await api('/api/library/add', {
    method: 'POST',
    body: JSON.stringify({ ...item, watchStatus, isFavorite })
  });
  await loadLibrary();
  if (state.currentView === 'catalog') renderCatalog();
  showToast(`${item.title} ajouté à la bibliothèque`);
  openEntryModal(saved.id);
};

window.modalPatchEntry = async function(id, patch) {
  const updated = await api(`/api/library/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify(patch)
  });
  await loadLibrary();
  renderEntryModalInner(updated);
};

window.modalToggleFav = async function(id) {
  const entry = state.library.find(e => e.id === id);
  if (!entry) return;
  await window.modalPatchEntry(id, { isFavorite: !entry.isFavorite });
};

window.modalDeleteEntry = async function(id) {
  await api(`/api/library/${encodeURIComponent(id)}`, { method: 'DELETE' });
  closeDetailModal();
  await loadLibrary();
  if (state.currentView === 'catalog') renderCatalog();
  showToast('Animé supprimé de la bibliothèque');
};

window.searchManualAnimeSama = async function(entryId) {
  const q = document.getElementById('manualAsQuery').value.trim();
  const wrap = document.getElementById('manualAsResults');
  wrap.innerHTML = `<span class="muted-note">Recherche…</span>`;
  const results = await api(`/api/animesama/search?q=${encodeURIComponent(q)}`);
  if (!results.length) {
    wrap.innerHTML = `<span class="muted-note">Aucun résultat trouvé.</span>`;
    return;
  }
  wrap.innerHTML = results.map(r => `
    <div style="display:flex;align-items:center;justify-content:space-between;background:rgba(255,255,255,0.03);padding:7px 10px;border-radius:7px">
      <div>
        <strong>${esc(r.title)}</strong>
        <span style="color:var(--text-muted);font-size:12px"> · /catalogue/${esc(r.slug)}/</span>
      </div>
      <button class="action-btn secondary" style="padding:4px 10px;font-size:12px"
              onclick="modalPatchEntry('${esc(entryId)}', { animeSamaSlug: '${esc(r.slug)}', animeSamaSeasonPath: '' })">
        Sélectionner
      </button>
    </div>
  `).join('');
};

function closeDetailModal() {
  document.getElementById('detailModalBackdrop').classList.add('hidden');
  document.getElementById('detailModalContent').innerHTML = '';
}
