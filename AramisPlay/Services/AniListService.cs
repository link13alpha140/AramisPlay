using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AramisPlay.Models;

namespace AramisPlay.Services;

public class AniListService
{
    private readonly HttpClient _http;
    private readonly StorageService _storage;
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, object Data)> _memCache = new();

    public static readonly Dictionary<string, string> GenreEnToFr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Action",
        ["Adventure"] = "Aventure",
        ["Comedy"] = "Comédie",
        ["Drama"] = "Drame",
        ["Ecchi"] = "Ecchi",
        ["Fantasy"] = "Fantaisie",
        ["Horror"] = "Horreur",
        ["Mahou Shoujo"] = "Magical Girl",
        ["Mecha"] = "Mecha",
        ["Music"] = "Musique",
        ["Mystery"] = "Mystère",
        ["Psychological"] = "Psychologique",
        ["Romance"] = "Romance",
        ["Sci-Fi"] = "Science-Fiction",
        ["Slice of Life"] = "Tranche de vie",
        ["Sports"] = "Sport",
        ["Supernatural"] = "Surnaturel",
        ["Thriller"] = "Thriller"
    };

    public static readonly Dictionary<string, string> TagEnToFr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Isekai"] = "Isekai",
        ["Reincarnation"] = "Réincarnation",
        ["Revenge"] = "Vengeance",
        ["Dungeon"] = "Donjons",
        ["Magic"] = "Magie",
        ["Martial Arts"] = "Arts Martiaux",
        ["Super Power"] = "Super-Pouvoirs",
        ["Anti-Hero"] = "Anti-Héros",
        ["Demons"] = "Démons",
        ["Vampire"] = "Vampires",
        ["Necromancy"] = "Nécromancie",
        ["Dragons"] = "Dragons",
        ["Gods"] = "Dieux & Divinités",
        ["Mythology"] = "Mythologie",
        ["Cyberpunk"] = "Cyberpunk",
        ["Post-Apocalyptic"] = "Post-Apocalyptique",
        ["Time Manipulation"] = "Voyage Temporel",
        ["Survival"] = "Survie",
        ["Virtual World"] = "Monde Virtuel",
        ["Video Games"] = "Jeux Vidéo",
        ["Space"] = "Espace",
        ["Military"] = "Militaire",
        ["School"] = "Vie Scolaire",
        ["Detective"] = "Enquête & Détective",
        ["Crime"] = "Crime",
        ["Yakuza"] = "Mafia & Yakuza",
        ["Samurai"] = "Samouraïs",
        ["Ninja"] = "Ninjas",
        ["Pirates"] = "Pirates",
        ["Historical"] = "Historique",
        ["Royal Affairs"] = "Noblesse & Royauté",
        ["Villainess"] = "Vilaine / Otome",
        ["Cultivation"] = "Cultivation",
        ["Parody"] = "Parodie",
        ["Gore"] = "Gore",
        ["Tragedy"] = "Tragédie",
        ["Shounen"] = "Shōnen",
        ["Seinen"] = "Seinen",
        ["Shoujo"] = "Shōjo",
        ["Josei"] = "Josei"
    };

    public static readonly Dictionary<string, string> GenreFrToEn =
        GenreEnToFr.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static readonly Dictionary<string, string> TagFrToEn =
        TagEnToFr.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] FrenchMonths =
    {
        "", "Janvier", "Février", "Mars", "Avril", "Mai", "Juin",
        "Juillet", "Août", "Septembre", "Octobre", "Novembre", "Décembre"
    };

    private const string MediaFields = @"
      id
      title { romaji english native }
      synonyms
      coverImage { extraLarge large }
      bannerImage
      description(asHtml: false)
      genres
      tags { name rank isMediaSpoiler }
      relations { edges { relationType(version: 2) node { id type format } } }
      trailer { id site }
      format
      status
      season
      seasonYear
      episodes
      averageScore
      popularity
      startDate { year month day }
      studios(isMain: true) { nodes { name } }
      nextAiringEpisode { airingAt episode }
    ";

    public AniListService(StorageService storage)
    {
        _storage = storage;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(14) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AramisPlay/2.0 (Windows NT 10.0; Win64; x64)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static IReadOnlyList<string> GetAllFrenchGenres() =>
        GenreEnToFr.Values.OrderBy(x => x).ToList();

    public static IReadOnlyList<string> GetAllFrenchTags() =>
        TagEnToFr.Values.OrderBy(x => x).ToList();

    public static (string Season, int Year) GetCurrentSeasonAndYear()
    {
        var now = DateTime.Now;
        var season = now.Month switch
        {
            <= 3 => "WINTER",
            <= 6 => "SPRING",
            <= 9 => "SUMMER",
            _ => "FALL"
        };
        return (season, now.Year);
    }

    public static (string Season, int Year) GetNextSeasonAndYear()
    {
        var (cur, year) = GetCurrentSeasonAndYear();
        return cur switch
        {
            "WINTER" => ("SPRING", year),
            "SPRING" => ("SUMMER", year),
            "SUMMER" => ("FALL", year),
            _ => ("WINTER", year + 1)
        };
    }

    public async Task<List<CatalogMediaItem>> BrowseCatalogAsync(CatalogQueryFilter filter)
    {
        var genresClean = (filter.GenresFr ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var tagsClean = (filter.TagsFr ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var cacheKey = $"cat2:{filter.Preset}:{filter.Search}:{string.Join(",", genresClean)}:{string.Join(",", tagsClean)}:{filter.Status}:{filter.Format}:{filter.Sort}:{filter.Year}:{filter.Page}:{filter.PerPage}";
        if (_memCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return (List<CatalogMediaItem>)cached.Data;
        }

        var vars = new Dictionary<string, object?>
        {
            ["page"] = Math.Max(1, filter.Page),
            ["perPage"] = Math.Clamp(filter.PerPage, 1, 50)
        };

        var varDefs = new List<string> { "$page: Int", "$perPage: Int" };
        var args = new List<string> { "type: ANIME", "isAdult: false" };

        bool hasSearch = !string.IsNullOrWhiteSpace(filter.Search);
        if (hasSearch)
        {
            varDefs.Add("$search: String");
            args.Add("search: $search");
            vars["search"] = filter.Search!.Trim();
        }

        // Conversion des sélections FR (qui peuvent contenir à la fois des genres et des tags)
        var enGenres = new List<string>();
        var enTags = new List<string>();

        foreach (var g in genresClean)
        {
            if (GenreFrToEn.TryGetValue(g, out var eg)) enGenres.Add(eg);
            else if (TagFrToEn.TryGetValue(g, out var et)) enTags.Add(et);
        }
        foreach (var t in tagsClean)
        {
            if (TagFrToEn.TryGetValue(t, out var et)) enTags.Add(et);
            else if (GenreFrToEn.TryGetValue(t, out var eg)) enGenres.Add(eg);
        }

        if (enGenres.Count > 0)
        {
            varDefs.Add("$genres: [String]");
            args.Add("genre_in: $genres");
            vars["genres"] = enGenres.Distinct().ToList();
        }

        if (enTags.Count > 0)
        {
            varDefs.Add("$tags: [String]");
            args.Add("tag_in: $tags");
            vars["tags"] = enTags.Distinct().ToList();
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            varDefs.Add("$status: MediaStatus");
            args.Add("status: $status");
            vars["status"] = filter.Status.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(filter.Format))
        {
            varDefs.Add("$format: MediaFormat");
            args.Add("format: $format");
            vars["format"] = filter.Format.Trim().ToUpperInvariant();
        }

        if (filter.Year.HasValue && filter.Year.Value >= 1960)
        {
            varDefs.Add("$seasonYear: Int");
            args.Add("seasonYear: $seasonYear");
            vars["seasonYear"] = filter.Year.Value;
        }

        // Quand l'utilisateur tape une recherche texte explicite, on n'enferme pas sa recherche dans la saison en cours
        string effectivePreset = hasSearch && filter.Preset is "current_season" or "next_season" or "upcoming"
            ? "trending"
            : (filter.Preset ?? "trending").ToLowerInvariant();

        switch (effectivePreset)
        {
            case "current_season":
            {
                var (season, year) = GetCurrentSeasonAndYear();
                if (!vars.ContainsKey("seasonYear"))
                {
                    varDefs.Add("$season: MediaSeason");
                    varDefs.Add("$seasonYear: Int");
                    args.Add("season: $season");
                    args.Add("seasonYear: $seasonYear");
                    vars["season"] = season;
                    vars["seasonYear"] = year;
                }
                args.Add($"sort: [{ResolveSort(filter.Sort, "POPULARITY_DESC")}]");
                break;
            }
            case "next_season":
            {
                var (season, year) = GetNextSeasonAndYear();
                if (!vars.ContainsKey("seasonYear"))
                {
                    varDefs.Add("$season: MediaSeason");
                    varDefs.Add("$seasonYear: Int");
                    args.Add("season: $season");
                    args.Add("seasonYear: $seasonYear");
                    vars["season"] = season;
                    vars["seasonYear"] = year;
                }
                args.Add($"sort: [{ResolveSort(filter.Sort, "POPULARITY_DESC")}]");
                break;
            }
            case "upcoming":
            {
                if (!vars.ContainsKey("status"))
                {
                    args.Add("status: NOT_YET_RELEASED");
                }
                args.Add($"sort: [{ResolveSort(filter.Sort, "POPULARITY_DESC")}]");
                break;
            }
            case "popular":
            {
                args.Add($"sort: [{ResolveSort(filter.Sort, "POPULARITY_DESC")}]");
                break;
            }
            case "top_rated":
            {
                args.Add($"sort: [{ResolveSort(filter.Sort, "SCORE_DESC, POPULARITY_DESC")}]");
                break;
            }
            default:
            {
                if (!string.IsNullOrWhiteSpace(filter.Sort))
                {
                    args.Add($"sort: [{ResolveSort(filter.Sort, "POPULARITY_DESC")}]");
                }
                else
                {
                    args.Add(hasSearch
                        ? "sort: [SEARCH_MATCH, POPULARITY_DESC]"
                        : "sort: [TRENDING_DESC, POPULARITY_DESC]");
                }
                break;
            }
        }

        var query = $@"
            query ({string.Join(", ", varDefs)}) {{
              Page(page: $page, perPage: $perPage) {{
                media({string.Join(", ", args)}) {{
                  {MediaFields}
                }}
              }}
            }}";

        using var doc = await ExecuteGraphQlAsync(query, vars);
        var list = new List<CatalogMediaItem>();
        if (doc != null &&
            doc.RootElement.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("Page", out var pageEl) &&
            pageEl.TryGetProperty("media", out var mediaArr) &&
            mediaArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in mediaArr.EnumerateArray())
            {
                list.Add(ParseMediaElement(el));
            }
        }

        _memCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(15), list);
        return list;
    }

    public async Task<List<CatalogMediaItem>> FetchPoolForThemesAsync(List<string> themesFr)
    {
        var pool = new Dictionary<int, CatalogMediaItem>();

        var p1Task = BrowseCatalogAsync(new CatalogQueryFilter
        {
            Preset = "popular",
            GenresFr = themesFr,
            Sort = "POPULARITY_DESC",
            Page = 1,
            PerPage = 50
        });

        var p2Task = BrowseCatalogAsync(new CatalogQueryFilter
        {
            Preset = "top_rated",
            GenresFr = themesFr,
            Sort = "SCORE_DESC",
            Page = 1,
            PerPage = 50
        });

        var p3Task = BrowseCatalogAsync(new CatalogQueryFilter
        {
            Preset = "popular",
            GenresFr = themesFr,
            Sort = "POPULARITY_DESC",
            Page = 2,
            PerPage = 50
        });

        await Task.WhenAll(p1Task, p2Task, p3Task);

        foreach (var item in p1Task.Result.Concat(p2Task.Result).Concat(p3Task.Result))
        {
            if (item.AniListId > 0)
                pool[item.AniListId] = item;
        }

        return pool.Values.ToList();
    }

    private static string ResolveSort(string? requested, string fallback)
    {
        return (requested ?? "").Trim().ToUpperInvariant() switch
        {
            "POPULARITY_DESC" => "POPULARITY_DESC",
            "SCORE_DESC" => "SCORE_DESC, POPULARITY_DESC",
            "TRENDING_DESC" => "TRENDING_DESC, POPULARITY_DESC",
            "START_DATE_DESC" => "START_DATE_DESC, POPULARITY_DESC",
            _ => fallback
        };
    }

    public async Task<List<AiringScheduleItem>> GetWeeklyScheduleAsync()
    {
        const string cacheKey = "weekly_schedule_v2";
        if (_memCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return (List<AiringScheduleItem>)cached.Data;
        }

        var nowLocal = DateTime.Now;
        int diffToMonday = ((int)nowLocal.DayOfWeek + 6) % 7;
        var monday = nowLocal.Date.AddDays(-diffToMonday);
        var nextMonday = monday.AddDays(7);

        long startTs = new DateTimeOffset(monday).ToUnixTimeSeconds();
        long endTs = new DateTimeOffset(nextMonday).ToUnixTimeSeconds();

        var results = new List<AiringScheduleItem>();
        for (int page = 1; page <= 3; page++)
        {
            var query = $@"
                query ($page: Int, $start: Int, $end: Int) {{
                  Page(page: $page, perPage: 50) {{
                    pageInfo {{ hasNextPage }}
                    airingSchedules(airingAt_greater: $start, airingAt_lesser: $end, sort: [TIME]) {{
                      id
                      airingAt
                      episode
                      media {{
                        isAdult
                        {MediaFields}
                      }}
                    }}
                  }}
                }}";

            using var doc = await ExecuteGraphQlAsync(query, new Dictionary<string, object?>
            {
                ["page"] = page,
                ["start"] = startTs,
                ["end"] = endTs
            });

            if (doc == null ||
                !doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("Page", out var pageEl))
                break;

            if (pageEl.TryGetProperty("airingSchedules", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in arr.EnumerateArray())
                {
                    if (!itemEl.TryGetProperty("media", out var mediaEl) || mediaEl.ValueKind != JsonValueKind.Object)
                        continue;

                    if (mediaEl.TryGetProperty("isAdult", out var adultEl) && adultEl.ValueKind == JsonValueKind.True)
                        continue;

                    var media = ParseMediaElement(mediaEl);
                    if ((media.Popularity ?? 0) < 800) continue;

                    results.Add(new AiringScheduleItem
                    {
                        AiringId = itemEl.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0,
                        AiringAt = itemEl.TryGetProperty("airingAt", out var atEl) ? atEl.GetInt64() : 0,
                        Episode = itemEl.TryGetProperty("episode", out var epEl) ? epEl.GetInt32() : 1,
                        Media = media
                    });
                }
            }

            bool hasNext = pageEl.TryGetProperty("pageInfo", out var pi) &&
                           pi.TryGetProperty("hasNextPage", out var hn) &&
                           hn.ValueKind == JsonValueKind.True;
            if (!hasNext) break;
        }

        _memCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(20), results);
        return results;
    }

    public async Task<string> EnsureFrenchSynopsisAsync(int aniListId, string englishDescription)
    {
        if (string.IsNullOrWhiteSpace(englishDescription)) return "";

        var cacheKey = $"al_desc_{aniListId}";
        var existing = _storage.GetCachedTranslation(cacheKey);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        try
        {
            var clean = CleanDescription(englishDescription);
            if (clean.Length > 1400) clean = clean[..1400] + "…";

            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=fr&dt=t&q=" +
                      Uri.EscapeDataString(clean);

            var resp = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            var sb = new StringBuilder();
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                foreach (var seg in doc.RootElement[0].EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(seg[0].GetString());
                    }
                }
            }

            var translated = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(translated))
            {
                _storage.SetCachedTranslation(cacheKey, translated);
                return translated;
            }
        }
        catch { }

        return CleanDescription(englishDescription);
    }

    public async Task<(string TrailerYoutubeId, List<FranchiseRelationItem> Relations)> GetFranchiseDetailsAsync(int rootAniListId)
    {
        if (rootAniListId <= 0)
            return ("", new List<FranchiseRelationItem>());

        var cacheKey = $"franchise:{rootAniListId}";
        if (_memCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return ((string, List<FranchiseRelationItem>))cached.Data;
        }

        var visited = new HashSet<int> { rootAniListId };
        var frontier = new List<int> { rootAniListId };
        var collected = new Dictionary<int, (CatalogMediaItem Media, long SortDateKey)>();

        string query = $@"
            query ($ids: [Int]) {{
              Page(page: 1, perPage: 50) {{
                media(id_in: $ids, type: ANIME, isAdult: false) {{
                  {MediaFields}
                }}
              }}
            }}";

        for (int hop = 0; hop < 4 && frontier.Count > 0 && collected.Count < 24; hop++)
        {
            using var doc = await ExecuteGraphQlAsync(query, new Dictionary<string, object?>
            {
                ["ids"] = frontier
            });

            var nextFrontier = new List<int>();
            if (doc != null &&
                doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("Page", out var pageEl) &&
                pageEl.TryGetProperty("media", out var mediaArr) &&
                mediaArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var mEl in mediaArr.EnumerateArray())
                {
                    var item = ParseMediaElement(mEl);
                    if (item.AniListId <= 0) continue;

                    long sortKey = ExtractChronologicalSortKey(mEl, item.ReleaseStatus);
                    collected[item.AniListId] = (item, sortKey);

                    if (mEl.TryGetProperty("relations", out var relEl) &&
                        relEl.ValueKind == JsonValueKind.Object &&
                        relEl.TryGetProperty("edges", out var edgesEl) &&
                        edgesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var edge in edgesEl.EnumerateArray())
                        {
                            var relType = GetStr(edge, "relationType").ToUpperInvariant();
                            if (!edge.TryGetProperty("node", out var nodeEl) || nodeEl.ValueKind != JsonValueKind.Object)
                                continue;

                            if (!GetStr(nodeEl, "type").Equals("ANIME", StringComparison.OrdinalIgnoreCase))
                                continue;

                            int relId = GetInt(nodeEl, "id") ?? 0;
                            if (relId <= 0 || visited.Contains(relId)) continue;

                            var relFormat = GetStr(nodeEl, "format").ToUpperInvariant();
                            if (relFormat is "MUSIC" or "SPECIAL") continue;

                            bool isMainChain = relType is "PREQUEL" or "SEQUEL";
                            bool isCanonMovieOrParent = (relType is "SIDE_STORY" or "PARENT") && relFormat == "MOVIE";
                            bool isParentSeries = relType == "PARENT" && relFormat is "TV" or "ONA";

                            if (isMainChain || isCanonMovieOrParent || isParentSeries)
                            {
                                visited.Add(relId);
                                nextFrontier.Add(relId);
                            }
                        }
                    }
                }
            }

            frontier = nextFrontier;
        }

        string rootTrailer = collected.TryGetValue(rootAniListId, out var rootTuple)
            ? rootTuple.Media.TrailerYoutubeId
            : "";

        var ordered = new List<FranchiseRelationItem>();
        if (collected.Count >= 2)
        {
            int idx = 1;
            foreach (var kv in collected.Values.OrderBy(x => x.SortDateKey).ThenBy(x => x.Media.AniListId))
            {
                var m = kv.Media;
                string label = m.ReleaseStatus.Equals("NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase)
                    ? "À venir"
                    : m.Format.ToUpperInvariant() switch
                    {
                        "MOVIE" => "Film",
                        "OVA" => "OAV",
                        "ONA" => "ONA",
                        _ => "Série"
                    };

                ordered.Add(new FranchiseRelationItem
                {
                    AniListId = m.AniListId,
                    OrderIndex = idx++,
                    IsCurrent = m.AniListId == rootAniListId,
                    RelationLabelFr = label,
                    Media = m
                });
            }
        }

        var result = (rootTrailer, ordered);
        _memCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(60), result);
        return result;
    }

    private static long ExtractChronologicalSortKey(JsonElement el, string releaseStatus)
    {
        if (el.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.Object)
        {
            int? y = GetInt(sd, "year");
            int m = GetInt(sd, "month") ?? 12;
            int d = GetInt(sd, "day") ?? 28;
            if (y.HasValue && y.Value > 1900)
                return y.Value * 10000L + m * 100L + d;
        }
        return string.Equals(releaseStatus, "NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase)
            ? 99991231L
            : 99981231L;
    }

    private CatalogMediaItem ParseMediaElement(JsonElement el)
    {
        int id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;

        string romaji = "", english = "", nativeTitle = "";
        if (el.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
        {
            romaji = GetStr(tEl, "romaji");
            english = GetStr(tEl, "english");
            nativeTitle = GetStr(tEl, "native");
        }

        var synonyms = new List<string>();
        if (el.TryGetProperty("synonyms", out var synEl) && synEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in synEl.EnumerateArray())
            {
                var sv = s.GetString();
                if (!string.IsNullOrWhiteSpace(sv)) synonyms.Add(sv);
            }
        }

        string cover = "";
        if (el.TryGetProperty("coverImage", out var covEl) && covEl.ValueKind == JsonValueKind.Object)
        {
            cover = GetStr(covEl, "extraLarge");
            if (string.IsNullOrEmpty(cover)) cover = GetStr(covEl, "large");
        }

        var genresFr = new List<string>();
        if (el.TryGetProperty("genres", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in genEl.EnumerateArray())
            {
                var gv = g.GetString();
                if (!string.IsNullOrWhiteSpace(gv))
                {
                    genresFr.Add(GenreEnToFr.TryGetValue(gv, out var fr) ? fr : gv);
                }
            }
        }

        var tagsFr = new List<string>();
        if (el.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagObj in tagsEl.EnumerateArray())
            {
                bool spoiler = tagObj.TryGetProperty("isMediaSpoiler", out var sp) && sp.ValueKind == JsonValueKind.True;
                int rank = tagObj.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.Number ? rk.GetInt32() : 0;
                if (spoiler || rank < 50) continue;

                var tName = GetStr(tagObj, "name");
                if (TagEnToFr.TryGetValue(tName, out var frTag) && !tagsFr.Contains(frTag))
                {
                    tagsFr.Add(frTag);
                }
            }
        }

        bool hasPrequel = false;
        if (el.TryGetProperty("relations", out var relEl) &&
            relEl.ValueKind == JsonValueKind.Object &&
            relEl.TryGetProperty("edges", out var edgesEl) &&
            edgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edgesEl.EnumerateArray())
            {
                var relType = GetStr(edge, "relationType");
                var nodeType = edge.TryGetProperty("node", out var nEl) && nEl.ValueKind == JsonValueKind.Object
                    ? GetStr(nEl, "type")
                    : "ANIME";

                if ((relType.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase) ||
                     relType.Equals("PARENT", StringComparison.OrdinalIgnoreCase)) &&
                    nodeType.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
                {
                    hasPrequel = true;
                    break;
                }
            }
        }

        var studios = new List<string>();
        if (el.TryGetProperty("studios", out var stEl) &&
            stEl.ValueKind == JsonValueKind.Object &&
            stEl.TryGetProperty("nodes", out var nodesEl) &&
            nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nodesEl.EnumerateArray())
            {
                var name = GetStr(n, "name");
                if (!string.IsNullOrWhiteSpace(name)) studios.Add(name);
            }
        }

        long? nextAiringAt = null;
        int? nextAiringEp = null;
        if (el.TryGetProperty("nextAiringEpisode", out var naEl) && naEl.ValueKind == JsonValueKind.Object)
        {
            if (naEl.TryGetProperty("airingAt", out var aAt) && aAt.ValueKind == JsonValueKind.Number)
                nextAiringAt = aAt.GetInt64();
            if (naEl.TryGetProperty("episode", out var aEp) && aEp.ValueKind == JsonValueKind.Number)
                nextAiringEp = aEp.GetInt32();
        }

        string season = GetStr(el, "season");
        int? seasonYear = GetInt(el, "seasonYear");
        string startDateLabel = FormatStartDate(el, season, seasonYear);
        string rawDesc = CleanDescription(GetStr(el, "description"));
        string cachedFr = id > 0 ? (_storage.GetCachedTranslation($"al_desc_{id}") ?? "") : "";

        string trailerYtId = "";
        if (el.TryGetProperty("trailer", out var trEl) && trEl.ValueKind == JsonValueKind.Object)
        {
            var site = GetStr(trEl, "site");
            var tid = GetStr(trEl, "id");
            if (site.Equals("youtube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tid))
            {
                trailerYtId = tid.Trim();
            }
        }

        string displayTitle = !string.IsNullOrWhiteSpace(romaji) ? romaji
                            : !string.IsNullOrWhiteSpace(english) ? english
                            : nativeTitle;

        return new CatalogMediaItem
        {
            AniListId = id,
            Title = displayTitle,
            TitleRomaji = romaji,
            TitleEnglish = english,
            TitleNative = nativeTitle,
            Synonyms = synonyms,
            CoverUrl = cover,
            BannerUrl = GetStr(el, "bannerImage"),
            SynopsisEn = rawDesc,
            SynopsisFr = cachedFr,
            GenresFr = genresFr,
            TagsFr = tagsFr,
            Format = GetStr(el, "format"),
            ReleaseStatus = GetStr(el, "status"),
            Season = season,
            SeasonYear = seasonYear,
            Episodes = GetInt(el, "episodes"),
            AverageScore = GetInt(el, "averageScore"),
            Popularity = GetInt(el, "popularity"),
            Studios = studios,
            NextAiringAt = nextAiringAt,
            NextAiringEpisode = nextAiringEp,
            StartDateLabel = startDateLabel,
            TrailerYoutubeId = trailerYtId,
            HasPrequel = hasPrequel
        };
    }

    private static string FormatStartDate(JsonElement el, string season, int? seasonYear)
    {
        if (el.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.Object)
        {
            int? y = GetInt(sd, "year");
            int? m = GetInt(sd, "month");
            int? d = GetInt(sd, "day");

            if (y.HasValue && m.HasValue && m >= 1 && m <= 12 && d.HasValue)
                return $"{d.Value} {FrenchMonths[m.Value]} {y.Value}";
            if (y.HasValue && m.HasValue && m >= 1 && m <= 12)
                return $"{FrenchMonths[m.Value]} {y.Value}";
            if (y.HasValue)
            {
                if (!string.IsNullOrEmpty(season))
                    return $"{TranslateSeason(season)} {y.Value}";
                return y.Value.ToString();
            }
        }

        if (seasonYear.HasValue && !string.IsNullOrEmpty(season))
            return $"{TranslateSeason(season)} {seasonYear.Value}";

        return "Date à confirmer";
    }

    public static string TranslateSeason(string? season) => (season ?? "").ToUpperInvariant() switch
    {
        "WINTER" => "Hiver",
        "SPRING" => "Printemps",
        "SUMMER" => "Été",
        "FALL" => "Automne",
        _ => ""
    };

    private static string CleanDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\(Source:.*?\)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"Note:.*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";

    private static int? GetInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : null;

    private async Task<JsonDocument?> ExecuteGraphQlAsync(string query, Dictionary<string, object?> variables)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { query, variables });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("https://graphql.anilist.co", content);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch
        {
            return null;
        }
    }
}
