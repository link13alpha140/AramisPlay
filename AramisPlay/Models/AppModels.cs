namespace AramisPlay.Models;

public class LibraryDatabase
{
    public List<LibraryEntry> Entries { get; set; } = new();
    public List<AppNotification> Notifications { get; set; } = new();
    public List<int> DismissedAniListIds { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public class LibraryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int? AniListId { get; set; }

    public string Title { get; set; } = "";
    public string TitleRomaji { get; set; } = "";
    public string TitleEnglish { get; set; } = "";
    public string TitleNative { get; set; } = "";
    public List<string> Synonyms { get; set; } = new();

    public string CoverUrl { get; set; } = "";
    public string BannerUrl { get; set; } = "";
    public string SynopsisFr { get; set; } = "";
    public List<string> GenresFr { get; set; } = new();
    public List<string> TagsFr { get; set; } = new();

    public string Format { get; set; } = "TV";
    public string ReleaseStatus { get; set; } = "FINISHED";
    public string Season { get; set; } = "";
    public int? SeasonYear { get; set; }
    public int? TotalEpisodesPlanned { get; set; }
    public int? AverageScore { get; set; }
    public long? NextAiringAt { get; set; }
    public int? NextAiringEpisode { get; set; }
    public string StartDateLabel { get; set; } = "";
    public string TrailerYoutubeId { get; set; } = "";

    // Suivi utilisateur
    public string WatchStatus { get; set; } = "PLANNING"; // WATCHING, PLANNING, COMPLETED, PAUSED, DROPPED
    public bool IsFavorite { get; set; } = false;
    public bool NotifyOnNewEpisode { get; set; } = true;
    public int CurrentEpisode { get; set; } = 0;
    public int? PersonalRating { get; set; }
    public string Notes { get; set; } = "";

    // Liaison Anime-Sama (VOSTFR)
    public string AnimeSamaSlug { get; set; } = "";
    public string AnimeSamaSeasonPath { get; set; } = "";
    public string AnimeSamaSeasonName { get; set; } = "";
    public List<AnimeSamaSeasonInfo> AvailableSeasons { get; set; } = new();
    public List<string> NotifiedSeasonPaths { get; set; } = new();
    public int AvailableEpisodesVostfr { get; set; } = 0;
    public int LastNotifiedEpisode { get; set; } = 0;
    public DateTimeOffset? LastCheckedAt { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AnimeSamaSeasonInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public class CatalogMediaItem
{
    public int AniListId { get; set; }
    public string Title { get; set; } = "";
    public string TitleRomaji { get; set; } = "";
    public string TitleEnglish { get; set; } = "";
    public string TitleNative { get; set; } = "";
    public List<string> Synonyms { get; set; } = new();
    public string CoverUrl { get; set; } = "";
    public string BannerUrl { get; set; } = "";
    public string SynopsisFr { get; set; } = "";
    public string SynopsisEn { get; set; } = "";
    public List<string> GenresFr { get; set; } = new();
    public List<string> TagsFr { get; set; } = new();
    public string Format { get; set; } = "TV";
    public string ReleaseStatus { get; set; } = "";
    public string Season { get; set; } = "";
    public int? SeasonYear { get; set; }
    public int? Episodes { get; set; }
    public int? AverageScore { get; set; }
    public int? Popularity { get; set; }
    public List<string> Studios { get; set; } = new();
    public long? NextAiringAt { get; set; }
    public int? NextAiringEpisode { get; set; }
    public string StartDateLabel { get; set; } = "";
    public string TrailerYoutubeId { get; set; } = "";
    public bool HasPrequel { get; set; }
}

public class CatalogQueryFilter
{
    public string Preset { get; set; } = "trending"; // trending, current_season, next_season, upcoming, popular, top_rated, custom
    public string? Search { get; set; }
    public List<string> GenresFr { get; set; } = new();
    public List<string> TagsFr { get; set; } = new();
    public string? Status { get; set; } // RELEASING, NOT_YET_RELEASED, FINISHED
    public string? Format { get; set; } // TV, MOVIE, ONA, OVA
    public string? Sort { get; set; } // TRENDING_DESC, POPULARITY_DESC, SCORE_DESC, START_DATE_DESC
    public int? Year { get; set; }
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 28;
}

public class DiscoveryRequest
{
    public List<string> Themes { get; set; } = new();
    public List<int> ExcludeAniListIds { get; set; } = new();
}

public class DiscoveryReplaceRequest
{
    public string Tier { get; set; } = "close";
    public List<string> Themes { get; set; } = new();
    public List<int> ExcludeAniListIds { get; set; } = new();
}

public class FranchiseRelationItem
{
    public int AniListId { get; set; }
    public string RelationLabelFr { get; set; } = "";
    public int OrderIndex { get; set; }
    public bool IsCurrent { get; set; }
    public CatalogMediaItem Media { get; set; } = new();
}

public class TasteStat
{
    public string Name { get; set; } = "";
    public double Weight { get; set; }
}

public class DiscoveryPick
{
    public string Tier { get; set; } = ""; // "close", "mid", "far"
    public string TierLabel { get; set; } = "";
    public string TierSubtitle { get; set; } = "";
    public int AffinityPercent { get; set; }
    public string ReasonFr { get; set; } = "";
    public CatalogMediaItem Media { get; set; } = new();
    public bool AnimeSamaFound { get; set; }
    public int AvailableEpisodesVostfr { get; set; }
    public string WatchUrl { get; set; } = "";
}

public class DiscoveryResponse
{
    public int RatedEntriesCount { get; set; }
    public int TotalLibraryCount { get; set; }
    public List<TasteStat> TopTastes { get; set; } = new();
    public List<DiscoveryPick> Picks { get; set; } = new();
}

public class AiringScheduleItem
{
    public int AiringId { get; set; }
    public long AiringAt { get; set; }
    public int Episode { get; set; }
    public CatalogMediaItem Media { get; set; } = new();
}

public class AppNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryId { get; set; } = "";
    public string AnimeTitle { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public int NewEpisode { get; set; }
    public int UserStoppedAt { get; set; }
    public string Message { get; set; } = "";
    public string WatchUrl { get; set; } = "";
    public bool IsRead { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AppSettings
{
    public string AnimeSamaBaseUrl { get; set; } = "https://anime-sama.to";
    public string CloseBehavior { get; set; } = "tray";
    public int CheckIntervalMinutes { get; set; } = 20;
    public bool EnableWindowsNotifications { get; set; } = true;
    public bool AutoIncrementOnWatchClick { get; set; } = true;
    public int ServerPort { get; set; } = 45780;
}

public class SourceHealthStatus
{
    public string Domain { get; set; } = "https://anime-sama.to";
    public string State { get; set; } = "unknown";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long LatencyMs { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string Message { get; set; } = "Non vérifié";
}

public class AnimeSamaAnimeDetails
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string AltTitles { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string SynopsisFr { get; set; } = "";
    public List<AnimeSamaSeasonInfo> VostfrSeasons { get; set; } = new();
}

public class AnimeSamaSearchCandidate
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Meta { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public double Score { get; set; }
}

public class ExtensionEpisodeSyncRequest
{
    public string Url { get; set; } = "";
    public string Slug { get; set; } = "";
    public string SeasonPath { get; set; } = "";
    public string SeasonName { get; set; } = "";
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public int EpisodeNumber { get; set; }
    public string EpisodeLabel { get; set; } = "";
    public int TotalAvailable { get; set; }
    public bool ForceJump { get; set; }
    public long Timestamp { get; set; }
}

public class ExtensionHistoryItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Kind { get; set; } = "Anime";
    public string Lang { get; set; } = "VOSTFR";
    public int EpisodeNumber { get; set; }
    public string Category { get; set; } = "history";
}

public class ExtensionBulkImportRequest
{
    public List<ExtensionHistoryItem> Items { get; set; } = new();
}
