(() => {
  if (!window.location.hostname.toLowerCase().includes('anime-sama')) return;

  // Intercepte #ap_ep=X ajouté par AramisPlay pour ouvrir directement le bon épisode dans le lecteur Anime-Sama
  const hash = window.location.hash || '';
  const match = hash.match(/ap_ep=(\d+)/i);
  if (!match) return;

  const targetEp = parseInt(match[1], 10);
  if (!targetEp || targetEp <= 0) return;

  try {
    const path = window.location.pathname;
    const epName = `Episode ${targetEp}`;
    const epIndex = Math.max(0, targetEp - 1);

    localStorage.setItem('savedEpName' + path, JSON.stringify(epName));
    localStorage.setItem('savedEpNb' + path, String(epIndex));
    sessionStorage.setItem('__aramis_target_ep', String(targetEp));

    // Nettoie le hash visuellement sans recharger la page
    const cleanUrl = window.location.pathname + window.location.search;
    window.history.replaceState(null, '', cleanUrl);
  } catch (e) {
    // Ignore erreurs de stockage éventuelles
  }
})();
