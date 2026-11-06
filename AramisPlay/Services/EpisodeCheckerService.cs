using AramisPlay.Models;

namespace AramisPlay.Services;

public class EpisodeCheckerService
{
    private readonly StorageService _storage;
    private readonly AnimeSamaService _animeSama;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public bool IsChecking { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }

    public event Action<List<AppNotification>>? OnNewNotifications;

    public EpisodeCheckerService(StorageService storage, AnimeSamaService animeSama)
    {
        _storage = storage;
        _animeSama = animeSama;
    }

    public async Task<List<AppNotification>> CheckAllFollowedAsync(bool forceAll = false)
    {
        if (!await _checkGate.WaitAsync(0))
            return new List<AppNotification>();

        IsChecking = true;
        var createdNotifs = new List<AppNotification>();

        try
        {
            await _animeSama.CheckHealthAndAutoHealDomainAsync();

            var entries = _storage.GetAllEntries()
                .Where(e =>
                    forceAll ||
                    e.NotifyOnNewEpisode &&
                    (e.IsFavorite ||
                     e.WatchStatus.Equals("WATCHING", StringComparison.OrdinalIgnoreCase) ||
                     (e.WatchStatus.Equals("PLANNING", StringComparison.OrdinalIgnoreCase) && e.AvailableEpisodesVostfr == 0)))
                .ToList();

            // Parallélisation contrôlée (max 4 requêtes simultanées pour respecter Anime-Sama)
            using var throttler = new SemaphoreSlim(4, 4);
            var tasks = entries.Select(async entry =>
            {
                await throttler.WaitAsync();
                try
                {
                    var entryNotifs = await CheckSingleEntryInternalAsync(entry);
                    if (entryNotifs.Count > 0)
                    {
                        lock (createdNotifs)
                        {
                            createdNotifs.AddRange(entryNotifs);
                        }
                    }
                }
                finally
                {
                    throttler.Release();
                }
            });

            await Task.WhenAll(tasks);
            LastRunAt = DateTimeOffset.UtcNow;

            if (createdNotifs.Count > 0)
            {
                OnNewNotifications?.Invoke(createdNotifs);
            }
        }
        finally
        {
            IsChecking = false;
            _checkGate.Release();
        }

        return createdNotifs;
    }

    public async Task<LibraryEntry?> RefreshEntryBindingAndEpisodesAsync(LibraryEntry entry, bool allowInitialNotification = false)
    {
        entry.NotifiedSeasonPaths ??= new List<string>();

        // Migration douce : si la fiche existait déjà avec des saisons connues, on les mémorise avant le refresh
        if (entry.NotifiedSeasonPaths.Count == 0 && entry.AvailableSeasons.Count > 0)
        {
            foreach (var existingSeason in entry.AvailableSeasons)
            {
                var norm = StorageService.NormalizeSeasonPath(existingSeason.Path);
                if (!string.IsNullOrEmpty(norm) && !entry.NotifiedSeasonPaths.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    entry.NotifiedSeasonPaths.Add(norm);
            }
        }

        bool wasNeverBound = string.IsNullOrWhiteSpace(entry.AnimeSamaSlug);
        bool hadPreviousCheck = entry.LastCheckedAt.HasValue;

        if (wasNeverBound || string.IsNullOrWhiteSpace(entry.AnimeSamaSeasonPath))
        {
            var (details, matchedSeason, epCount) = await _animeSama.AutoMatchAsync(
                entry.Title,
                entry.TitleRomaji,
                entry.TitleEnglish,
                entry.Synonyms,
                entry.Format);

            if (details != null)
            {
                entry.AnimeSamaSlug = details.Slug;
                entry.AvailableSeasons = details.VostfrSeasons;
                if (!string.IsNullOrWhiteSpace(details.SynopsisFr))
                    entry.SynopsisFr = details.SynopsisFr;

                if (matchedSeason != null)
                {
                    entry.AnimeSamaSeasonPath = matchedSeason.Path;
                    entry.AnimeSamaSeasonName = matchedSeason.Name;
                    entry.AvailableEpisodesVostfr = epCount;
                }

                if (!allowInitialNotification)
                {
                    foreach (var s in details.VostfrSeasons)
                    {
                        var norm = StorageService.NormalizeSeasonPath(s.Path);
                        if (!string.IsNullOrEmpty(norm) && !entry.NotifiedSeasonPaths.Contains(norm, StringComparer.OrdinalIgnoreCase))
                            entry.NotifiedSeasonPaths.Add(norm);
                    }
                }
            }

            entry.LastCheckedAt = DateTimeOffset.UtcNow;
            if (!allowInitialNotification)
            {
                entry.LastNotifiedEpisode = entry.AvailableEpisodesVostfr;
            }
            if (_storage.GetEntryById(entry.Id) == null) return null;
            return _storage.UpsertEntry(entry);
        }
        else
        {
            var normCurrent = StorageService.NormalizeSeasonPath(entry.AnimeSamaSeasonPath);
            if (!string.IsNullOrEmpty(normCurrent) && !entry.NotifiedSeasonPaths.Contains(normCurrent, StringComparer.OrdinalIgnoreCase))
            {
                entry.NotifiedSeasonPaths.Add(normCurrent);
            }

            // Rafraîchit aussi la liste des saisons si une nouvelle saison VOSTFR vient d'apparaître
            var details = await _animeSama.GetAnimeDetailsAsync(entry.AnimeSamaSlug);
            if (details != null && details.VostfrSeasons.Count > 0)
            {
                if (!hadPreviousCheck && !allowInitialNotification && entry.NotifiedSeasonPaths.Count <= 1)
                {
                    foreach (var s in details.VostfrSeasons)
                    {
                        var norm = StorageService.NormalizeSeasonPath(s.Path);
                        if (!string.IsNullOrEmpty(norm) && !entry.NotifiedSeasonPaths.Contains(norm, StringComparer.OrdinalIgnoreCase))
                            entry.NotifiedSeasonPaths.Add(norm);
                    }
                }

                entry.AvailableSeasons = details.VostfrSeasons;
                if (string.IsNullOrWhiteSpace(entry.SynopsisFr) && !string.IsNullOrWhiteSpace(details.SynopsisFr))
                    entry.SynopsisFr = details.SynopsisFr;
            }

            int epCount = await _animeSama.GetEpisodeCountAsync(entry.AnimeSamaSlug, entry.AnimeSamaSeasonPath);
            if (epCount > 0)
            {
                entry.AvailableEpisodesVostfr = epCount;
            }
            entry.LastCheckedAt = DateTimeOffset.UtcNow;

            if (!hadPreviousCheck && !allowInitialNotification)
            {
                entry.LastNotifiedEpisode = Math.Max(entry.LastNotifiedEpisode, entry.AvailableEpisodesVostfr);
            }

            if (_storage.GetEntryById(entry.Id) == null) return null;
            return _storage.UpsertEntry(entry);
        }
    }

    private async Task<List<AppNotification>> CheckSingleEntryInternalAsync(LibraryEntry entry)
    {
        var results = new List<AppNotification>();
        bool hadPreviousCheck = entry.LastCheckedAt.HasValue;
        int prevAvailable = entry.AvailableEpisodesVostfr;
        int prevNotified = entry.LastNotifiedEpisode;
        bool wasUpcomingWaiting = hadPreviousCheck && prevAvailable == 0;

        var updated = await RefreshEntryBindingAndEpisodesAsync(entry, allowInitialNotification: wasUpcomingWaiting);
        if (updated == null) return results;

        // 1. Détection d'une NOUVELLE SAISON / FILM VOSTFR apparu sur la fiche Anime-Sama
        if (hadPreviousCheck && !string.IsNullOrWhiteSpace(updated.AnimeSamaSlug) && updated.AvailableSeasons.Count > 0)
        {
            var siblingPaths = _storage.FindEntriesBySlug(updated.AnimeSamaSlug)
                .Select(x => StorageService.NormalizeSeasonPath(x.AnimeSamaSeasonPath))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool seasonChanged = false;
            foreach (var s in updated.AvailableSeasons)
            {
                var normS = StorageService.NormalizeSeasonPath(s.Path);
                if (string.IsNullOrEmpty(normS)) continue;
                if (updated.NotifiedSeasonPaths.Contains(normS, StringComparer.OrdinalIgnoreCase)) continue;

                if (siblingPaths.Contains(normS))
                {
                    updated.NotifiedSeasonPaths.Add(normS);
                    seasonChanged = true;
                    continue;
                }

                // Vérifie qu'au moins l'épisode 1 est réellement en ligne avant d'alerter
                int newSeasonEps = await _animeSama.GetEpisodeCountAsync(updated.AnimeSamaSlug, s.Path);
                if (newSeasonEps >= 1)
                {
                    updated.NotifiedSeasonPaths.Add(normS);
                    seasonChanged = true;

                    var seasonNotif = new AppNotification
                    {
                        EntryId = updated.Id,
                        AnimeTitle = $"{updated.Title} — {s.Name}",
                        CoverUrl = updated.CoverUrl,
                        NewEpisode = newSeasonEps,
                        UserStoppedAt = 0,
                        Message = $"Nouvelle sortie VOSTFR sur Anime-Sama : {s.Name} ({newSeasonEps} ép. disponible{(newSeasonEps > 1 ? "s" : "")}) !",
                        WatchUrl = _animeSama.BuildWatchUrl(updated.AnimeSamaSlug, s.Path, 1),
                        IsRead = false,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    results.Add(_storage.AddNotification(seasonNotif));
                }
            }

            if (seasonChanged)
            {
                _storage.UpsertEntry(updated);
            }
        }

        // 2. Détection d'un nouvel épisode VOSTFR sur la saison actuellement suivie
        int nowAvailable = updated.AvailableEpisodesVostfr;
        if (nowAvailable <= 0) return results;

        bool isBrandNewRelease = wasUpcomingWaiting && nowAvailable >= 1 && prevNotified == 0;
        bool isNewEpisodeDrop = hadPreviousCheck && nowAvailable > prevAvailable && nowAvailable > prevNotified && nowAvailable > updated.CurrentEpisode;

        if (!isBrandNewRelease && !isNewEpisodeDrop)
            return results;

        updated.LastNotifiedEpisode = nowAvailable;
        _storage.UpsertEntry(updated);

        int nextEpToWatch = Math.Min(updated.CurrentEpisode + 1, nowAvailable);
        string watchUrl = _animeSama.BuildWatchUrl(updated.AnimeSamaSlug, updated.AnimeSamaSeasonPath, nextEpToWatch);

        string message;
        if (isBrandNewRelease && updated.CurrentEpisode == 0)
        {
            message = nowAvailable == 1
                ? "L'épisode 1 (VOSTFR) vient de sortir sur Anime-Sama !"
                : $"{nowAvailable} épisodes (VOSTFR) sont désormais disponibles sur Anime-Sama !";
        }
        else if (updated.CurrentEpisode > 0)
        {
            message = $"L'épisode {nowAvailable} (VOSTFR) est sorti ! Tu t'es arrêté à l'épisode {updated.CurrentEpisode}.";
        }
        else
        {
            message = $"L'épisode {nowAvailable} (VOSTFR) est disponible sur Anime-Sama !";
        }

        var notif = new AppNotification
        {
            EntryId = updated.Id,
            AnimeTitle = updated.Title,
            CoverUrl = updated.CoverUrl,
            NewEpisode = nowAvailable,
            UserStoppedAt = updated.CurrentEpisode,
            Message = message,
            WatchUrl = watchUrl,
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        results.Add(_storage.AddNotification(notif));
        return results;
    }
}
