(() => {
  if (!window.location.hostname.toLowerCase().includes('anime-sama')) return;

  // --- 1. Extraction de l'historique localStorage Anime-Sama à la demande du popup ---
  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg && msg.type === 'EXTRACT_ANIMESAMA_DATA') {
      try {
        const items = extractAllLocalStorageItems();
        sendResponse({ ok: true, items });
      } catch (err) {
        sendResponse({ ok: false, error: String(err) });
      }
      return true;
    }
  });

  function cdnImg(url) {
    if (!url) return '';
    return String(url).replace(
      '://raw.githubusercontent.com/Anime-Sama/IMG/img/',
      '://cdn.jsdelivr.net/gh/Anime-Sama/IMG@img/'
    );
  }

  function normalizeVostfrUrl(rawUrl) {
    if (!rawUrl) return '';
    let u = String(rawUrl);
    if (u.toLowerCase().includes('/scan')) return u;
    return u.replace(/\/(vf\d*|va|vkr|vcn)(\/|$)/i, '/vostfr$2');
  }

  function parseArr(key) {
    try {
      const raw = localStorage.getItem(key);
      const parsed = JSON.parse(raw || '[]');
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  function extractEpNum(epStr, numVal) {
    if (epStr) {
      const m = String(epStr).match(/(\d+)/);
      if (m) return parseInt(m[1], 10);
    }
    const idx = parseInt(numVal, 10);
    return !isNaN(idx) && idx >= 0 ? idx + 1 : 0;
  }

  function extractAllLocalStorageItems() {
    const out = [];

    const hNom = parseArr('histoNom');
    const hUrl = parseArr('histoUrl');
    const hImg = parseArr('histoImg');
    const hType = parseArr('histoType');
    const hEp = parseArr('histoEp');
    const hNum = parseArr('histoNum');

    for (let i = 0; i < hUrl.length; i++) {
      const rawUrl = hUrl[i] || '';
      if (!rawUrl) continue;
      const savedNb = localStorage.getItem('savedEpNb' + rawUrl);
      const savedNameRaw = localStorage.getItem('savedEpName' + rawUrl);
      let savedName = hEp[i] || '';
      try { if (savedNameRaw) savedName = JSON.parse(savedNameRaw); } catch {}

      out.push({
        title: hNom[i] || '',
        url: normalizeVostfrUrl(rawUrl),
        coverUrl: cdnImg(hImg[i] || ''),
        kind: hType[i] || (rawUrl.includes('/scan') ? 'Scans' : 'Anime'),
        lang: 'VOSTFR',
        episodeNumber: extractEpNum(savedName, savedNb !== null ? savedNb : hNum[i]),
        category: 'history'
      });
    }

    const groups = [
      { prefix: 'favori', category: 'favorite' },
      { prefix: 'watchlist', category: 'watchlist' },
      { prefix: 'vu', category: 'viewed' }
    ];

    for (const grp of groups) {
      const noms = parseArr(grp.prefix + 'Nom');
      const urls = parseArr(grp.prefix + 'Url');
      const imgs = parseArr(grp.prefix + 'Img');
      for (let i = 0; i < urls.length; i++) {
        const rawUrl = urls[i] || '';
        if (!rawUrl) continue;
        const savedNb = localStorage.getItem('savedEpNb' + rawUrl);
        out.push({
          title: noms[i] || '',
          url: normalizeVostfrUrl(rawUrl),
          coverUrl: cdnImg(imgs[i] || ''),
          kind: rawUrl.includes('/scan') ? 'Scans' : 'Anime',
          lang: 'VOSTFR',
          episodeNumber: savedNb !== null ? Math.max(0, parseInt(savedNb, 10) + 1) : 0,
          category: grp.category
        });
      }
    }

    return out;
  }

  // --- 2. Suivi intelligent sur les pages de lecture d'épisodes ---
  const selectEl = document.getElementById('selectEpisodes');
  if (!selectEl) return;

  const pathParts = window.location.pathname.split('/').filter(Boolean);
  const catIdx = pathParts.findIndex(p => p.toLowerCase() === 'catalogue');
  if (catIdx < 0 || catIdx + 2 >= pathParts.length) return;

  const slug = pathParts[catIdx + 1];
  const seasonPath = pathParts.slice(catIdx + 2).join('/');
  if (seasonPath.toLowerCase().includes('scan')) return;

  // Badge discret avec contrôles de confirmation anti-clic accidentel
  const badge = document.createElement('span');
  badge.style.cssText = [
    'display:inline-flex',
    'align-items:center',
    'gap:8px',
    'margin-left:10px',
    'padding:4px 10px',
    'border-radius:6px',
    'font-size:11.5px',
    'font-weight:600',
    'font-family:Segoe UI,sans-serif',
    'background:rgba(16,24,39,0.92)',
    'border:1px solid rgba(212,168,83,0.45)',
    'color:#e8c476',
    'vertical-align:middle'
  ].join(';');

  const badgeLabel = document.createElement('span');
  badgeLabel.textContent = 'AramisPlay actif';
  badge.appendChild(badgeLabel);

  const badgeBtn = document.createElement('button');
  badgeBtn.type = 'button';
  badgeBtn.style.cssText = [
    'display:none',
    'padding:2px 7px',
    'border-radius:4px',
    'border:1px solid rgba(212,168,83,0.55)',
    'background:rgba(212,168,83,0.18)',
    'color:#f3d58d',
    'font-size:11px',
    'font-weight:600',
    'cursor:pointer'
  ].join(';');
  badge.appendChild(badgeBtn);

  const anchorParent = selectEl.closest('label')?.parentElement || selectEl.parentElement;
  if (anchorParent) anchorParent.appendChild(badge);

  const WATCH_CONFIRM_DELAY_MS = 25000; // 25s de présence réelle avant validation auto
  let lastSyncedKey = '';
  let watchTimer = null;

  function setBadgeState(text, colorHex, borderRgba, actionLabel = null, onAction = null) {
    badgeLabel.textContent = text;
    badge.style.color = colorHex;
    badge.style.borderColor = borderRgba;
    if (actionLabel && onAction) {
      badgeBtn.textContent = actionLabel;
      badgeBtn.style.display = 'inline-block';
      badgeBtn.onclick = e => {
        e.preventDefault();
        e.stopPropagation();
        onAction();
      };
    } else {
      badgeBtn.style.display = 'none';
      badgeBtn.onclick = null;
    }
  }

  function getSelectedEpisodeInfo() {
    if (!selectEl.options || selectEl.options.length === 0) return null;
    const idx = Math.max(0, selectEl.selectedIndex);
    const optText = (selectEl.options[idx]?.text || '').trim();
    const epMatch = optText.match(/(\d+)/);
    const epNumber = epMatch ? parseInt(epMatch[1], 10) : idx + 1;

    let regularTotal = 0;
    for (let i = 0; i < selectEl.options.length; i++) {
      const txt = selectEl.options[i].text || '';
      if (!/\d+\.\d+/.test(txt)) regularTotal++;
    }
    if (regularTotal === 0) regularTotal = selectEl.options.length;

    return { epNumber, optText, regularTotal };
  }

  function applyTargetEpisodeIfNeeded() {
    const rawTarget = sessionStorage.getItem('__aramis_target_ep');
    if (!rawTarget || !selectEl.options || selectEl.options.length === 0) return;

    sessionStorage.removeItem('__aramis_target_ep');
    const targetNum = parseInt(rawTarget, 10);
    if (!targetNum) return;

    const wantedText = `episode ${targetNum}`;
    let foundIdx = -1;
    for (let i = 0; i < selectEl.options.length; i++) {
      if ((selectEl.options[i].text || '').trim().toLowerCase() === wantedText) {
        foundIdx = i;
        break;
      }
    }
    if (foundIdx === -1 && targetNum - 1 < selectEl.options.length) {
      foundIdx = targetNum - 1;
    }

    if (foundIdx >= 0 && selectEl.selectedIndex !== foundIdx) {
      selectEl.selectedIndex = foundIdx;
      selectEl.dispatchEvent(new Event('change', { bubbles: true }));
    }
  }

  function armEpisodeWatchTimer() {
    clearTimeout(watchTimer);
    const info = getSelectedEpisodeInfo();
    if (!info) return;

    const dedupKey = `${slug}:${seasonPath}:${info.epNumber}`;
    if (dedupKey === lastSyncedKey) return;

    setBadgeState(
      `AramisPlay · Lecture Ép. ${info.epNumber}…`,
      '#e8c476',
      'rgba(212,168,83,0.45)',
      'Valider',
      () => sendCurrentEpisode(true)
    );

    watchTimer = setTimeout(() => {
      sendCurrentEpisode(false);
    }, WATCH_CONFIRM_DELAY_MS);
  }

  function sendCurrentEpisode(forceJump = false) {
    clearTimeout(watchTimer);
    const info = getSelectedEpisodeInfo();
    if (!info) return;

    const dedupKey = `${slug}:${seasonPath}:${info.epNumber}`;
    if (!forceJump && dedupKey === lastSyncedKey) return;

    const titleEl = document.getElementById('titreOeuvre');
    const coverEl = document.getElementById('coverOeuvre');
    const seasonTitleEl = document.getElementById('avOeuvre');

    const payload = {
      url: window.location.pathname,
      slug,
      seasonPath,
      seasonName: seasonTitleEl ? seasonTitleEl.textContent.trim() : '',
      title: titleEl ? titleEl.textContent.trim() : slug,
      coverUrl: coverEl ? cdnImg(coverEl.getAttribute('src') || '') : '',
      episodeNumber: info.epNumber,
      episodeLabel: info.optText,
      totalAvailable: info.regularTotal,
      forceJump: !!forceJump,
      timestamp: Date.now()
    };

    chrome.runtime.sendMessage({ type: 'SYNC_EPISODE', payload }, resp => {
      if (chrome.runtime.lastError || !resp) return;

      if (resp.jumpBlocked) {
        setBadgeState(
          `AramisPlay · Tu étais à l'Ép. ${resp.currentEpisode} — passer à l'Ép. ${info.epNumber} ?`,
          '#fbbf24',
          'rgba(251,191,36,0.65)',
          'Confirmer',
          () => sendCurrentEpisode(true)
        );
        return;
      }

      if (resp.synced) {
        lastSyncedKey = dedupKey;
        const suffix = resp.autoCompleted ? ' (Terminé)' : '';
        setBadgeState(
          `AramisPlay · Ép. ${info.epNumber} synchronisé${suffix}`,
          '#6ee7b7',
          'rgba(16,185,129,0.55)'
        );
      } else if (resp.queued) {
        lastSyncedKey = dedupKey;
        setBadgeState(
          `AramisPlay · Ép. ${info.epNumber} mémorisé hors-ligne`,
          '#e8c476',
          'rgba(212,168,83,0.55)'
        );
      }
    });
  }

  let initTries = 0;
  const waitListTimer = setInterval(() => {
    initTries++;
    if (selectEl.options && selectEl.options.length > 0) {
      clearInterval(waitListTimer);
      setTimeout(() => {
        applyTargetEpisodeIfNeeded();
        armEpisodeWatchTimer();
      }, 350);
    } else if (initTries > 30) {
      clearInterval(waitListTimer);
    }
  }, 250);

  selectEl.addEventListener('change', () => armEpisodeWatchTimer());

  document.querySelectorAll('#nextEpisode, #prevEpisode, #lastEpisode').forEach(btn => {
    btn.addEventListener('click', () => setTimeout(armEpisodeWatchTimer, 250));
  });

  const playerParent = document.getElementById('playerDF')?.parentElement;
  if (playerParent) {
    const obs = new MutationObserver(() => setTimeout(armEpisodeWatchTimer, 300));
    obs.observe(playerParent, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
  }
})();
