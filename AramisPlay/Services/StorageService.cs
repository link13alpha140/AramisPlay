using System.Text.Json;
using System.Text.Json.Serialization;
using AramisPlay.Models;

namespace AramisPlay.Services;

public class StorageService
{
    private readonly object _lock = new();
    private readonly string _dataDir;
    private readonly string _backupDir;
    private readonly string _dbPath;
    private readonly string _translationCachePath;

    private LibraryDatabase _db = new();
    private Dictionary<string, string> _translationCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public StorageService()
    {
        // Cherche la racine A:\AramisPlay pour que data\ reste proprement à la racine du projet
        var baseDir = AppContext.BaseDirectory;
        var rootCandidate = FindProjectRoot(baseDir) ?? baseDir;
        _dataDir = Path.Combine(rootCandidate, "data");
        _backupDir = Path.Combine(_dataDir, "backups");
        _dbPath = Path.Combine(_dataDir, "library.json");
        _translationCachePath = Path.Combine(_dataDir, "translations_fr.json");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_backupDir);
        Load();
    }

    public string DataDirectory => _dataDir;

    private static string? FindProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            if (dir.Name.Equals("AramisPlay", StringComparison.OrdinalIgnoreCase) &&
                (Directory.Exists(Path.Combine(dir.FullName, "extension")) ||
                 Directory.Exists(Path.Combine(dir.FullName, "AramisPlay"))))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_dbPath))
            {
                try
                {
                    var json = File.ReadAllText(_dbPath);
                    _db = JsonSerializer.Deserialize<LibraryDatabase>(json, JsonOpts) ?? new LibraryDatabase();
                }
                catch
                {
                    // Tentative de restauration depuis le dernier backup si fichier corrompu
                    var latestBackup = Directory.GetFiles(_backupDir, "library_*.json")
                        .OrderByDescending(f => f)
                        .FirstOrDefault();
                    if (latestBackup != null)
                    {
                        var backupJson = File.ReadAllText(latestBackup);
                        _db = JsonSerializer.Deserialize<LibraryDatabase>(backupJson, JsonOpts) ?? new LibraryDatabase();
                    }
                }
            }

            if (File.Exists(_translationCachePath))
            {
                try
                {
                    var trJson = File.ReadAllText(_translationCachePath);
                    _translationCache = JsonSerializer.Deserialize<Dictionary<string, string>>(trJson, JsonOpts)
                                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    _translationCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var tmp = _dbPath + ".tmp";
            var json = JsonSerializer.Serialize(_db, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _dbPath, overwrite: true);

            // Backup quotidien automatique (garde les 7 derniers jours)
            var todayBackup = Path.Combine(_backupDir, $"library_{DateTime.Now:yyyy-MM-dd}.json");
            if (!File.Exists(todayBackup))
            {
                File.Copy(_dbPath, todayBackup, overwrite: true);
                CleanupOldBackups();
            }
        }
    }

    private void CleanupOldBackups()
    {
        try
        {
            var backups = Directory.GetFiles(_backupDir, "library_*.json")
                .OrderByDescending(f => f)
                .Skip(7);
            foreach (var old in backups)
                File.Delete(old);
        }
        catch { /* Ignorer erreurs mineures de ménage */ }
    }

    public string? GetCachedTranslation(string key)
    {
        lock (_lock)
        {
            return _translationCache.TryGetValue(key, out var val) ? val : null;
        }
    }

    public void SetCachedTranslation(string key, string frenchText)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(frenchText)) return;
        lock (_lock)
        {
            _translationCache[key] = frenchText;
            try
            {
                var tmp = _translationCachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_translationCache, JsonOpts));
                File.Move(tmp, _translationCachePath, overwrite: true);
            }
            catch { }
        }
    }

    public AppSettings GetSettings()
    {
        lock (_lock) return _db.Settings;
    }

    public AppSettings UpdateSettings(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_db.Settings);
            Save();
            return _db.Settings;
        }
    }

    public List<LibraryEntry> GetAllEntries()
    {
        lock (_lock)
        {
            return _db.Entries
                .OrderByDescending(e => e.UpdatedAt)
                .ToList();
        }
    }

    public LibraryEntry? GetEntryById(string id)
    {
        lock (_lock)
        {
            return _db.Entries.FirstOrDefault(e => e.Id == id);
        }
    }

    public LibraryEntry? FindMatchingEntry(int? aniListId, string? slug, string? seasonPath, string? title)
    {
        lock (_lock)
        {
            if (aniListId.HasValue && aniListId.Value > 0)
            {
                var byAl = _db.Entries.FirstOrDefault(e => e.AniListId == aniListId.Value);
                if (byAl != null) return byAl;
            }

            var normSeason = NormalizeSeasonPath(seasonPath);
            int targetSeasonNum = ExtractSeasonNumber(normSeason);

            if (!string.IsNullOrWhiteSpace(slug))
            {
                var normSlug = slug.Trim('/').ToLowerInvariant();

                if (!string.IsNullOrEmpty(normSeason))
                {
                    var exactSeason = _db.Entries.FirstOrDefault(e =>
                        string.Equals(e.AnimeSamaSlug, normSlug, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(NormalizeSeasonPath(e.AnimeSamaSeasonPath), normSeason, StringComparison.OrdinalIgnoreCase));
                    if (exactSeason != null) return exactSeason;
                }

                // Correspondance avec une fiche déjà reliée au slug mais sans saison fixée (uniquement si le numéro de saison concorde)
                var unboundSeasonWithSlug = _db.Entries.FirstOrDefault(e =>
                    string.Equals(e.AnimeSamaSlug, normSlug, StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(e.AnimeSamaSeasonPath) &&
                    ExtractSeasonNumber(e.Title) == targetSeasonNum);
                if (unboundSeasonWithSlug != null) return unboundSeasonWithSlug;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var normTitle = NormalizeTitle(title);
                if (normTitle.Length >= 3)
                {
                    // 1. Recherche exacte par titre, mais uniquement si la fiche n'est pas déjà liée à une AUTRE saison !
                    var exactByTitle = _db.Entries.FirstOrDefault(e =>
                        (string.IsNullOrEmpty(normSeason) ||
                         string.IsNullOrWhiteSpace(e.AnimeSamaSeasonPath) ||
                         string.Equals(NormalizeSeasonPath(e.AnimeSamaSeasonPath), normSeason, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrEmpty(normSeason) || ExtractSeasonNumber(e.Title) == targetSeasonNum) &&
                        (NormalizeTitle(e.Title) == normTitle ||
                         NormalizeTitle(e.TitleRomaji) == normTitle ||
                         NormalizeTitle(e.TitleEnglish) == normTitle));
                    if (exactByTitle != null) return exactByTitle;

                    // 2. Si l'utilisateur lance la Saison 2+ sur Anime-Sama (où #titreOeuvre reste le titre de base sans "Season 2"),
                    //    on vérifie s'il avait pré-ajouté la Saison 2 depuis le Catalogue AniList (non encore reliée à un seasonPath différent)
                    if (targetSeasonNum >= 2)
                    {
                        var preAddedSequel = _db.Entries.FirstOrDefault(e =>
                            (string.IsNullOrWhiteSpace(e.AnimeSamaSeasonPath) ||
                             string.Equals(NormalizeSeasonPath(e.AnimeSamaSeasonPath), normSeason, StringComparison.OrdinalIgnoreCase)) &&
                            ExtractSeasonNumber(e.Title) == targetSeasonNum &&
                            (NormalizeTitle(e.Title).StartsWith(normTitle, StringComparison.Ordinal) ||
                             NormalizeTitle(e.TitleRomaji).StartsWith(normTitle, StringComparison.Ordinal) ||
                             NormalizeTitle(e.TitleEnglish).StartsWith(normTitle, StringComparison.Ordinal)));
                        if (preAddedSequel != null) return preAddedSequel;
                    }
                }
            }

            return null;
        }
    }

    public List<LibraryEntry> FindEntriesBySlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return new List<LibraryEntry>();
        var normSlug = slug.Trim('/').ToLowerInvariant();
        lock (_lock)
        {
            return _db.Entries
                .Where(e => string.Equals(e.AnimeSamaSlug, normSlug, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public HashSet<int> GetDismissedIds()
    {
        lock (_lock)
        {
            return (_db.DismissedAniListIds ?? new List<int>()).ToHashSet();
        }
    }

    public void DismissAniListId(int aniListId)
    {
        if (aniListId <= 0) return;
        lock (_lock)
        {
            _db.DismissedAniListIds ??= new List<int>();
            if (!_db.DismissedAniListIds.Contains(aniListId))
            {
                _db.DismissedAniListIds.Add(aniListId);
                Save();
            }
        }
    }

    public void ClearDismissedIds()
    {
        lock (_lock)
        {
            _db.DismissedAniListIds?.Clear();
            Save();
        }
    }

    public LibraryEntry UpsertEntry(LibraryEntry entry)
    {
        lock (_lock)
        {
            var existing = _db.Entries.FirstOrDefault(e => e.Id == entry.Id);
            if (existing == null && entry.AniListId.HasValue && entry.AniListId > 0)
            {
                existing = _db.Entries.FirstOrDefault(e => e.AniListId == entry.AniListId);
            }

            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (existing != null)
            {
                entry.Id = existing.Id;
                entry.AddedAt = existing.AddedAt;
                var idx = _db.Entries.IndexOf(existing);
                _db.Entries[idx] = entry;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                    entry.Id = entry.AniListId.HasValue ? $"al_{entry.AniListId}" : Guid.NewGuid().ToString("N");
                entry.AddedAt = DateTimeOffset.UtcNow;
                _db.Entries.Add(entry);
            }

            Save();
            return entry;
        }
    }

    public bool DeleteEntry(string id)
    {
        lock (_lock)
        {
            var removed = _db.Entries.RemoveAll(e => e.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public List<AppNotification> GetNotifications()
    {
        lock (_lock)
        {
            return _db.Notifications
                .OrderByDescending(n => n.CreatedAt)
                .Take(100)
                .ToList();
        }
    }

    public AppNotification AddNotification(AppNotification notif)
    {
        lock (_lock)
        {
            _db.Notifications.Insert(0, notif);
            if (_db.Notifications.Count > 150)
                _db.Notifications.RemoveRange(150, _db.Notifications.Count - 150);
            Save();
            return notif;
        }
    }

    public void MarkAllNotificationsRead()
    {
        lock (_lock)
        {
            foreach (var n in _db.Notifications)
                n.IsRead = true;
            Save();
        }
    }

    public void ClearNotifications()
    {
        lock (_lock)
        {
            _db.Notifications.Clear();
            Save();
        }
    }

    public string ExportJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_db, JsonOpts);
        }
    }

    public int ImportJson(string json)
    {
        var imported = JsonSerializer.Deserialize<LibraryDatabase>(json, JsonOpts);
        if (imported == null) return 0;

        int count = 0;
        lock (_lock)
        {
            foreach (var item in imported.Entries)
            {
                var match = FindMatchingEntry(item.AniListId, item.AnimeSamaSlug, item.AnimeSamaSeasonPath, item.Title);
                if (match == null)
                {
                    _db.Entries.Add(item);
                    count++;
                }
                else if (item.CurrentEpisode > match.CurrentEpisode)
                {
                    match.CurrentEpisode = item.CurrentEpisode;
                    match.WatchStatus = item.WatchStatus;
                    match.IsFavorite = match.IsFavorite || item.IsFavorite;
                    match.UpdatedAt = DateTimeOffset.UtcNow;
                    count++;
                }
            }
            Save();
        }
        return count;
    }

    public static string NormalizeSeasonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().Trim('/').ToLowerInvariant();
    }

    public static string NormalizeToVostfrSeasonPath(string? path)
    {
        var clean = NormalizeSeasonPath(path);
        if (string.IsNullOrEmpty(clean)) return "";

        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return $"{parts[0]}/vostfr";

        var lang = parts[1];
        if (lang.StartsWith("vf", StringComparison.OrdinalIgnoreCase) ||
            lang.Equals("va", StringComparison.OrdinalIgnoreCase) ||
            lang.Equals("vkr", StringComparison.OrdinalIgnoreCase) ||
            lang.Equals("vcn", StringComparison.OrdinalIgnoreCase))
        {
            parts[1] = "vostfr";
        }
        return $"{parts[0]}/{parts[1]}";
    }

    public static int ExtractSeasonNumber(string? textOrPath)
    {
        if (string.IsNullOrWhiteSpace(textOrPath)) return 1;
        var m = System.Text.RegularExpressions.Regex.Match(
            textOrPath,
            @"(?:saison|season|part)\s*(\d+)|\b(\d+)(?:nd|rd|th)\s+season\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var g = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(g, out int n) && n >= 1) return n;
        }
        return 1;
    }

    public static string NormalizeTitle(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        var chars = t.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray();
        return new string(chars);
    }
}
