const connEl = document.getElementById('connStatus');
const connDot = document.getElementById('connDot');
const queueEl = document.getElementById('queueStatus');
const feedbackEl = document.getElementById('feedback');

function refreshStatus() {
  chrome.runtime.sendMessage({ type: 'CHECK_STATUS' }, res => {
    if (!res) return;
    if (res.online) {
      connEl.textContent = 'Connecté à AramisPlay';
      connEl.style.color = '#f1f5f9';
      connDot.className = 'dot on';
    } else {
      connEl.textContent = 'AramisPlay fermé (mémoire active)';
      connEl.style.color = '#cbd5e1';
      connDot.className = 'dot off';
    }
    queueEl.textContent = `Épisodes en attente : ${res.queueCount || 0}`;
  });
}

document.getElementById('syncQueueBtn').addEventListener('click', () => {
  feedbackEl.textContent = 'Synchronisation en cours…';
  chrome.runtime.sendMessage({ type: 'FLUSH_QUEUE' }, res => {
    refreshStatus();
    if (res && res.ok) {
      feedbackEl.textContent = `Synchronisation terminée (${res.flushed} envoyé(s)).`;
    } else {
      feedbackEl.textContent = 'AramisPlay est fermé : tes épisodes seront synchronisés automatiquement dès l\'ouverture de l\'application.';
    }
  });
});

document.getElementById('importHistoryBtn').addEventListener('click', async () => {
  feedbackEl.textContent = 'Lecture de l\'historique sur l\'onglet actif…';

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || !tab.url || !tab.url.toLowerCase().includes('anime-sama')) {
    feedbackEl.textContent = 'Ouvre d\'abord un onglet sur Anime-Sama, puis reclique sur ce bouton.';
    return;
  }

  chrome.tabs.sendMessage(tab.id, { type: 'EXTRACT_ANIMESAMA_DATA' }, extractRes => {
    if (chrome.runtime.lastError || !extractRes || !extractRes.ok) {
      feedbackEl.textContent = 'Actualise la page Anime-Sama (F5), puis reclique sur Importer.';
      return;
    }

    const items = extractRes.items || [];
    if (items.length === 0) {
      feedbackEl.textContent = 'Aucun historique trouvé dans le stockage local de cet onglet.';
      return;
    }

    chrome.runtime.sendMessage({ type: 'IMPORT_BULK', items }, importRes => {
      if (importRes && importRes.ok) {
        feedbackEl.textContent = `${importRes.imported} animé(s) importés dans AramisPlay.`;
      } else {
        feedbackEl.textContent = importRes?.error || 'Impossible de joindre AramisPlay.';
      }
    });
  });
});

refreshStatus();
