# ▶ AramisPlay — Catalogue & Suivi Anime-Sama (VOSTFR)

Le successeur spirituel d'*AramisScan* sur PC : un tableau de bord local rapide, autonome et économe en ressources pour suivre tes animés en **VOSTFR** sur **Anime-Sama**, explorer les prochaines sorties et recevoir des notifications dès qu'un épisode sort réellement.

---

## 🚀 Lancer l'application

Double-clique simplement sur :
* **`AramisPlay.lnk`** (ou **`Lancer AramisPlay.bat`**, ou directement `A:\AramisPlay\app\AramisPlay.exe` que tu peux épingler à ta barre des tâches Windows !).

### Mode Économie de Ressources (au choix dans l'appli)
Tu peux basculer à tout moment en haut à droite de la fenêtre (ou dans *Paramètres*) entre :
1. **🌙 Veille légère** : Fermer la fenêtre (`✕`) **détruit intégralement l'interface graphique WebView2** (0 % GPU, ~15–20 Mo RAM seulement). Une petite icône reste près de l'horloge Windows pour surveiller les sorties VOSTFR et recevoir la synchronisation de ton navigateur en direct.
2. **⚡ Fermeture totale (0 Mo RAM)** : Fermer la fenêtre coupe 100 % des processus. Si tu regardes des épisodes sur Anime-Sama pendant que l'appli est fermée, l'extension navigateur les garde en mémoire et **synchronise tout automatiquement** dès que tu rouvres AramisPlay !

---

## 🧩 Installer l'Extension Navigateur « AramisPlay Sync » (30 secondes)

Compatible **Chrome, Brave, Edge, Opera GX, Vivaldi** :
1. Ouvre `chrome://extensions` (ou `brave://extensions` / `edge://extensions`) dans ton navigateur.
2. Active le **Mode développeur** (en haut à droite).
3. Clique sur **Charger l'extension non empaquetée** et sélectionne le dossier :
   ```
   A:\AramisPlay\extension
   ```
4. **Bonus — Récupérer ton historique Anime-Sama actuel** : Ouvre un onglet sur Anime-Sama, clique sur l'icône **AramisPlay Sync** dans ton navigateur, puis clique sur **« 📥 Importer mon historique Anime-Sama »** !

---

## 📂 Organisation du dossier

* `app\` — Exécutable Windows compilé (`AramisPlay.exe`) prêt à lancer.
* `AramisPlay\` — Code source complet C# `.NET 10` + interface `wwwroot\`.
* `extension\` — Extension navigateur Chromium (Manifest V3).
* `data\` — Ta bibliothèque locale (`library.json`), le cache des résumés FR et les sauvegardes automatiques quotidiennes (`data\backups\`).
