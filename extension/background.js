const API_BASE = 'http://127.0.0.1:45780';

// Vérification périodique légère pour vider la file d'attente dès qu'AramisPlay est lancé
chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create('aramisplay_flush', { periodInMinutes: 1 });
  updateBadge();
});

chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create('aramisplay_flush', { periodInMinutes: 1 });
  flushPendingQueue();
});

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === 'aramisplay_flush') {
    flushPendingQueue();
  }
});

chrome.tabs.onActivated.addListener(() => {
  flushPendingQueue();
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || !msg.type) return false;

  if (msg.type === 'SYNC_EPISODE') {
    handleEpisodeSync(msg.payload).then(sendResponse);
    return true;
  }

  if (msg.type === 'CHECK_STATUS') {
    checkStatus().then(sendResponse);
    return true;
  }

  if (msg.type === 'FLUSH_QUEUE') {
    flushPendingQueue().then(sendResponse);
    return true;
  }

  if (msg.type === 'IMPORT_BULK') {
    importBulkToApp(msg.items || []).then(sendResponse);
    return true;
  }

  return false;
});

async function getQueue() {
  const res = await chrome.storage.local.get(['pendingQueue']);
  return res.pendingQueue || {};
}

async function setQueue(q) {
  await chrome.storage.local.set({ pendingQueue: q });
  await updateBadge(q);
}

async function updateBadge(queueObj = null) {
  const q = queueObj || (await getQueue());
  const count = Object.keys(q).length;
  if (count > 0) {
    chrome.action.setBadgeText({ text: String(count) });
    chrome.action.setBadgeBackgroundColor({ color: '#d4a853' });
  } else {
    chrome.action.setBadgeText({ text: '' });
  }
}

async function handleEpisodeSync(payload) {
  try {
    const res = await fetch(`${API_BASE}/api/extension/sync-episode`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (res.ok) {
      const serverData = await res.json().catch(() => ({}));
      flushPendingQueue();
      return { ok: true, synced: !serverData.jumpBlocked, ...serverData };
    }
  } catch {
    // AramisPlay est fermé (mode 0 Mo RAM) -> mise en file d'attente locale
  }

  const q = await getQueue();
  const key = `${payload.slug}::${payload.seasonPath}`;
  const prev = q[key];
  if (!prev || payload.episodeNumber >= prev.episodeNumber) {
    q[key] = payload;
    await setQueue(q);
  }
  return { ok: true, synced: false, queued: true };
}

async function flushPendingQueue() {
  const q = await getQueue();
  const keys = Object.keys(q);
  if (keys.length === 0) {
    await updateBadge(q);
    return { ok: true, flushed: 0, remaining: 0 };
  }

  // Test rapide si AramisPlay est ouvert
  try {
    const ping = await fetch(`${API_BASE}/api/ping`);
    if (!ping.ok) return { ok: false, flushed: 0, remaining: keys.length };
  } catch {
    return { ok: false, flushed: 0, remaining: keys.length };
  }

  let flushed = 0;
  for (const k of keys) {
    try {
      const res = await fetch(`${API_BASE}/api/extension/sync-episode`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(q[k])
      });
      if (res.ok) {
        delete q[k];
        flushed++;
      }
    } catch {
      break;
    }
  }

  await setQueue(q);
  return { ok: true, flushed, remaining: Object.keys(q).length };
}

async function checkStatus() {
  const q = await getQueue();
  const queueCount = Object.keys(q).length;

  try {
    const res = await fetch(`${API_BASE}/api/ping`);
    if (res.ok) {
      if (queueCount > 0) {
        const flushedRes = await flushPendingQueue();
        return { online: true, queueCount: flushedRes.remaining };
      }
      return { online: true, queueCount: 0 };
    }
  } catch {}

  return { online: false, queueCount };
}

async function importBulkToApp(items) {
  try {
    const res = await fetch(`${API_BASE}/api/extension/import-animesama`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ items })
    });
    if (!res.ok) return { ok: false, error: 'AramisPlay a renvoyé une erreur.' };
    return await res.json();
  } catch {
    return { ok: false, error: 'Lance AramisPlay sur ton PC avant de faire un import complet !' };
  }
}
