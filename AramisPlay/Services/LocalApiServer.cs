using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AramisPlay.Models;

namespace AramisPlay.Services;

public class LocalApiServer
{
    private readonly StorageService _storage;
    private readonly AniListService _aniList;
    private readonly AnimeSamaService _animeSama;
    private readonly EpisodeCheckerService _checker;
    private readonly DiscoveryService _discovery;
    private readonly Action _requestQuitApp;
    private readonly Action _requestShowWindow;

    private WebApplication? _app;
    private long _stateVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string BaseAddress { get; private set; } = "http://127.0.0.1:45780";

    public LocalApiServer(
        StorageService storage,
        AniListService aniList,
        AnimeSamaService animeSama,
        EpisodeCheckerService checker,
        DiscoveryService discovery,
        Action requestQuitApp,
        Action requestShowWindow)
    {
        _storage = storage;
        _aniList = aniList;
        _animeSama = animeSama;
        _checker = checker;
        _discovery = discovery;
        _requestQuitApp = requestQuitApp;
        _requestShowWindow = requestShowWindow;

        _checker.OnNewNotifications += _ => BumpState();
    }

    public void BumpState() => Interlocked.Exchange(ref _stateVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public async Task StartAsync()
    {
        int port = _storage.GetSettings().ServerPort;
        if (port <= 1024 || port > 65535) port = 45780;
        BaseAddress = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(BaseAddress);

        builder.Services.AddCors(opts =>
        {
            opts.AddDefaultPolicy(policy =>
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        app.UseCors();

        var wwwrootPath = ResolveWwwrootPath();
        if (Directory.Exists(wwwrootPath))
        {
            var provider = new PhysicalFileProvider(wwwrootPath);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
                }
            });
        }

        RegisterRoutes(app);

        _app = app;
        await app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(cts.Token);
                await _app.DisposeAsync();
            }
            catch { }
            _app = null;
        }
    }

    private static string ResolveWwwrootPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, "wwwroot");
        if (Directory.Exists(direct)) return direct;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var c1 = Path.Combine(dir.FullName, "wwwroot");
            if (Directory.Exists(c1)) return c1;
            var c2 = Path.Combine(dir.FullName, "AramisPlay", "wwwroot");
            if (Directory.Exists(c2)) return c2;
            dir = dir.Parent;
        }
        return direct;
    }

    private void RegisterRoutes(WebApplication app)
    {
        // --- SYSTEM & HEALTH ---
        app.MapGet("/api/ping", () =>
        {
            var s = _storage.GetSettings();
            return Results.Ok(new
            {
                ok = true,
                app = "AramisPlay",
                version = "2.0.0",
                stateVersion = Interlocked.Read(ref _stateVersion),
                animeSamaUrl = _animeSama.BaseUrl,
                closeBehavior = s.CloseBehavior,
                isChecking = _checker.IsChecking,
                lastCheckAt = _checker.LastRunAt
            });
        });

        app.MapGet("/api/health/animesama", () => Results.Ok(_animeSama.GetHealthStatus()));

        app.MapPost("/api/health/autodetect", async () =>
        {
            var health = await _animeSama.AutoDetectDomainAsync();
            BumpState();
            return Results.Ok(health);
        });

        app.MapPost("/api/system/open-external", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (doc.RootElement.TryGetProperty("url", out var uEl))
            {
                var url = uEl.GetString();
                if (!string.IsNullOrWhiteSpace(url) &&
                    (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                {
                    OpenBrowser(url);
                    return Results.Ok(new { ok = true });
                }
            }
            return Results.BadRequest(new { ok = false });
        });

        app.MapPost("/api/system/show-window", () =>
        {
            _requestShowWindow();
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/system/quit", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                _requestQuitApp();
            });
            return Results.Ok(new { ok = true });
        });

        // --- SETTINGS ---
        app.MapGet("/api/settings", () => Results.Ok(_storage.GetSettings()));

        app.MapPut("/api/settings", async (HttpContext ctx) =>
        {
            var incoming = await ctx.Request.ReadFromJsonAsync<AppSettings>();
            if (incoming == null) return Results.BadRequest();

            var updated = _storage.UpdateSettings(s =>
            {
                if (!string.IsNullOrWhiteSpace(incoming.AnimeSamaBaseUrl))
                    s.AnimeSamaBaseUrl = incoming.AnimeSamaBaseUrl.Trim().TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(incoming.CloseBehavior))
                    s.CloseBehavior = incoming.CloseBehavior;
                if (incoming.CheckIntervalMinutes >= 5)
                    s.CheckIntervalMinutes = incoming.CheckIntervalMinutes;
                s.EnableWindowsNotifications = incoming.EnableWindowsNotifications;
                s.AutoIncrementOnWatchClick = incoming.AutoIncrementOnWatchClick;
            });
            BumpState();
            return Results.Ok(updated);
        });

        // --- LIBRARY ---
        app.MapGet("/api/library", () =>
        {
            var entries = _storage.GetAllEntries();
            var notifs = _storage.GetNotifications();
            return Results.Ok(new
            {
                entries,
                unreadNotifications = notifs.Count(n => !n.IsRead),
                animeSamaBaseUrl = _animeSama.BaseUrl,
                stateVersion = Interlocked.Read(ref _stateVersion)
            });
        });

        app.MapPost("/api/library/add", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;

            int? aniListId = root.TryGetProperty("aniListId", out var alEl) && alEl.ValueKind == JsonValueKind.Number && alEl.GetInt32() > 0
                ? alEl.GetInt32()
                : null;

            string title = GetJsonString(root, "title");
            string watchStatus = GetJsonString(root, "watchStatus", "PLANNING");
            bool isFavorite = root.TryGetProperty("isFavorite", out var favEl) && favEl.ValueKind == JsonValueKind.True;
            int currentEpisode = root.TryGetProperty("currentEpisode", out var epEl) && epEl.ValueKind == JsonValueKind.Number
                ? epEl.GetInt32()
                : 0;
            int? personalRating = root.TryGetProperty("personalRating", out var prEl) && prEl.ValueKind == JsonValueKind.Number
                ? prEl.GetInt32()
                : null;

            var existing = _storage.FindMatchingEntry(aniListId, GetJsonString(root, "animeSamaSlug"), null, title);
            var entry = existing ?? new LibraryEntry
            {
                Id = aniListId.HasValue ? $"al_{aniListId.Value}" : Guid.NewGuid().ToString("N"),
                AniListId = aniListId
            };

            if (!string.IsNullOrWhiteSpace(title)) entry.Title = title;
            var romaji = GetJsonString(root, "titleRomaji");
            if (!string.IsNullOrWhiteSpace(romaji)) entry.TitleRomaji = romaji;
            var eng = GetJsonString(root, "titleEnglish");
            if (!string.IsNullOrWhiteSpace(eng)) entry.TitleEnglish = eng;
            var nat = GetJsonString(root, "titleNative");
            if (!string.IsNullOrWhiteSpace(nat)) entry.TitleNative = nat;

            if (root.TryGetProperty("synonyms", out var synEl) && synEl.ValueKind == JsonValueKind.Array)
            {
                entry.Synonyms = synEl.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            var cover = GetJsonString(root, "coverUrl");
            if (!string.IsNullOrWhiteSpace(cover)) entry.CoverUrl = cover;
            var banner = GetJsonString(root, "bannerUrl");
            if (!string.IsNullOrWhiteSpace(banner)) entry.BannerUrl = banner;

            var synFr = GetJsonString(root, "synopsisFr");
            if (!string.IsNullOrWhiteSpace(synFr)) entry.SynopsisFr = synFr;

            if (root.TryGetProperty("genresFr", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
            {
                entry.GenresFr = genEl.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            if (root.TryGetProperty("tagsFr", out var tagEl) && tagEl.ValueKind == JsonValueKind.Array)
            {
                entry.TagsFr = tagEl.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            var fmt = GetJsonString(root, "format");
            if (!string.IsNullOrWhiteSpace(fmt)) entry.Format = fmt;
            var rel = GetJsonString(root, "releaseStatus");
            if (!string.IsNullOrWhiteSpace(rel)) entry.ReleaseStatus = rel;
            var season = GetJsonString(root, "season");
            if (!string.IsNullOrWhiteSpace(season)) entry.Season = season;
            if (root.TryGetProperty("seasonYear", out var syEl) && syEl.ValueKind == JsonValueKind.Number)
                entry.SeasonYear = syEl.GetInt32();
            if (root.TryGetProperty("episodes", out var totEl) && totEl.ValueKind == JsonValueKind.Number)
                entry.TotalEpisodesPlanned = totEl.GetInt32();
            if (root.TryGetProperty("averageScore", out var scEl) && scEl.ValueKind == JsonValueKind.Number)
                entry.AverageScore = scEl.GetInt32();
            if (root.TryGetProperty("nextAiringAt", out var naEl) && naEl.ValueKind == JsonValueKind.Number)
                entry.NextAiringAt = naEl.GetInt64();
            if (root.TryGetProperty("nextAiringEpisode", out var neEl) && neEl.ValueKind == JsonValueKind.Number)
                entry.NextAiringEpisode = neEl.GetInt32();
            var sdl = GetJsonString(root, "startDateLabel");
            if (!string.IsNullOrWhiteSpace(sdl)) entry.StartDateLabel = sdl;
            var trailerYt = GetJsonString(root, "trailerYoutubeId");
            if (!string.IsNullOrWhiteSpace(trailerYt)) entry.TrailerYoutubeId = trailerYt;

            var explicitSlug = GetJsonString(root, "animeSamaSlug");
            if (!string.IsNullOrWhiteSpace(explicitSlug)) entry.AnimeSamaSlug = explicitSlug;
            var explicitSeasonPath = GetJsonString(root, "animeSamaSeasonPath");
            if (!string.IsNullOrWhiteSpace(explicitSeasonPath)) entry.AnimeSamaSeasonPath = StorageService.NormalizeToVostfrSeasonPath(explicitSeasonPath);
            var explicitSeasonName = GetJsonString(root, "animeSamaSeasonName");
            if (!string.IsNullOrWhiteSpace(explicitSeasonName)) entry.AnimeSamaSeasonName = explicitSeasonName;

            entry.WatchStatus = watchStatus;
            if (root.TryGetProperty("isFavorite", out _))
                entry.IsFavorite = isFavorite;
            if (personalRating.HasValue)
                entry.PersonalRating = personalRating;
            if (currentEpisode > 0 || existing == null)
                entry.CurrentEpisode = currentEpisode;

            if (watchStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) &&
                entry.CurrentEpisode == 0 &&
                entry.TotalEpisodesPlanned.HasValue)
            {
                entry.CurrentEpisode = entry.TotalEpisodesPlanned.Value;
            }

            string synEn = GetJsonString(root, "synopsisEn");
            var saved = _storage.UpsertEntry(entry);
            BumpState();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_storage.GetEntryById(saved.Id) == null) return;
                    if (string.IsNullOrWhiteSpace(saved.SynopsisFr) && saved.AniListId.HasValue && !string.IsNullOrWhiteSpace(synEn))
                    {
                        var syn = await _aniList.EnsureFrenchSynopsisAsync(saved.AniListId.Value, synEn);
                        if (_storage.GetEntryById(saved.Id) == null) return;
                        saved.SynopsisFr = syn;
                        _storage.UpsertEntry(saved);
                    }
                    if (_storage.GetEntryById(saved.Id) == null) return;
                    await _checker.RefreshEntryBindingAndEpisodesAsync(saved, allowInitialNotification: false);
                    if (_storage.GetEntryById(saved.Id) == null) return;
                    if (saved.WatchStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) &&
                        saved.CurrentEpisode == 0 &&
                        saved.AvailableEpisodesVostfr > 0)
                    {
                        saved.CurrentEpisode = saved.AvailableEpisodesVostfr;
                        _storage.UpsertEntry(saved);
                    }
                    BumpState();
                }
                catch { }
            });

            return Results.Ok(saved);
        });

        app.MapPatch("/api/library/{id}", async (string id, HttpContext ctx) =>
        {
            var entry = _storage.GetEntryById(id);
            if (entry == null) return Results.NotFound();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;

            bool seasonChanged = false;

            if (root.TryGetProperty("watchStatus", out var ws) && ws.ValueKind == JsonValueKind.String)
            {
                entry.WatchStatus = ws.GetString() ?? entry.WatchStatus;
                if (entry.WatchStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    int target = entry.AvailableEpisodesVostfr > 0
                        ? entry.AvailableEpisodesVostfr
                        : (entry.TotalEpisodesPlanned ?? entry.CurrentEpisode);
                    if (target > entry.CurrentEpisode) entry.CurrentEpisode = target;
                }
            }
            if (root.TryGetProperty("isFavorite", out var fav) && (fav.ValueKind == JsonValueKind.True || fav.ValueKind == JsonValueKind.False))
                entry.IsFavorite = fav.GetBoolean();
            if (root.TryGetProperty("notifyOnNewEpisode", out var notEl) && (notEl.ValueKind == JsonValueKind.True || notEl.ValueKind == JsonValueKind.False))
                entry.NotifyOnNewEpisode = notEl.GetBoolean();
            if (root.TryGetProperty("currentEpisode", out var ep) && ep.ValueKind == JsonValueKind.Number)
            {
                entry.CurrentEpisode = Math.Max(0, ep.GetInt32());
                if (!root.TryGetProperty("watchStatus", out _))
                {
                    TryAutoCompleteFinishedEntry(entry);
                }
            }
            if (root.TryGetProperty("personalRating", out var pr))
                entry.PersonalRating = pr.ValueKind == JsonValueKind.Number && pr.GetInt32() > 0 ? pr.GetInt32() : null;
            if (root.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.String)
                entry.Notes = nt.GetString() ?? "";

            if (root.TryGetProperty("animeSamaSlug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String)
            {
                var newSlug = (slugEl.GetString() ?? "").Trim('/');
                if (!string.Equals(newSlug, entry.AnimeSamaSlug, StringComparison.OrdinalIgnoreCase))
                {
                    entry.AnimeSamaSlug = newSlug;
                    seasonChanged = true;
                }
            }
            if (root.TryGetProperty("animeSamaSeasonPath", out var spEl) && spEl.ValueKind == JsonValueKind.String)
            {
                var newPath = (spEl.GetString() ?? "").Trim('/');
                if (!string.Equals(newPath, entry.AnimeSamaSeasonPath, StringComparison.OrdinalIgnoreCase))
                {
                    entry.AnimeSamaSeasonPath = newPath;
                    var foundSeason = entry.AvailableSeasons.FirstOrDefault(s => s.Path.Equals(newPath, StringComparison.OrdinalIgnoreCase));
                    if (foundSeason != null) entry.AnimeSamaSeasonName = foundSeason.Name;
                    seasonChanged = true;
                }
            }
            if (root.TryGetProperty("animeSamaSeasonName", out var snEl) && snEl.ValueKind == JsonValueKind.String)
            {
                entry.AnimeSamaSeasonName = snEl.GetString() ?? entry.AnimeSamaSeasonName;
            }

            if (seasonChanged && !string.IsNullOrWhiteSpace(entry.AnimeSamaSlug))
            {
                var details = await _animeSama.GetAnimeDetailsAsync(entry.AnimeSamaSlug);
                if (details != null && details.VostfrSeasons.Count > 0)
                {
                    entry.AvailableSeasons = details.VostfrSeasons;
                    if (string.IsNullOrWhiteSpace(entry.AnimeSamaSeasonPath) ||
                        !details.VostfrSeasons.Any(s => s.Path.Equals(entry.AnimeSamaSeasonPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        entry.AnimeSamaSeasonPath = details.VostfrSeasons[0].Path;
                        entry.AnimeSamaSeasonName = details.VostfrSeasons[0].Name;
                    }
                }
                entry.AvailableEpisodesVostfr = await _animeSama.GetEpisodeCountAsync(entry.AnimeSamaSlug, entry.AnimeSamaSeasonPath);
                entry.LastNotifiedEpisode = entry.AvailableEpisodesVostfr;
                entry.LastCheckedAt = DateTimeOffset.UtcNow;
            }

            var saved = _storage.UpsertEntry(entry);
            BumpState();
            return Results.Ok(saved);
        });

        app.MapPost("/api/library/{id}/watch", async (string id, HttpContext ctx) =>
        {
            var entry = _storage.GetEntryById(id);
            if (entry == null) return Results.NotFound();

            int requestedEp = 0;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("episode", out var epEl) && epEl.ValueKind == JsonValueKind.Number)
                    requestedEp = epEl.GetInt32();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(entry.AnimeSamaSlug))
            {
                entry = await _checker.RefreshEntryBindingAndEpisodesAsync(entry) ?? entry;
            }

            if (requestedEp <= 0)
            {
                requestedEp = entry.CurrentEpisode + 1;
                if (entry.AvailableEpisodesVostfr > 0 && requestedEp > entry.AvailableEpisodesVostfr)
                    requestedEp = entry.AvailableEpisodesVostfr;
            }

            string url;
            if (!string.IsNullOrWhiteSpace(entry.AnimeSamaSlug))
            {
                url = _animeSama.BuildWatchUrl(entry.AnimeSamaSlug, entry.AnimeSamaSeasonPath, requestedEp);
            }
            else
            {
                url = $"{_animeSama.BaseUrl}/catalogue/?search={Uri.EscapeDataString(entry.Title)}";
            }

            if (_storage.GetSettings().AutoIncrementOnWatchClick && !string.IsNullOrWhiteSpace(entry.AnimeSamaSlug))
            {
                if (requestedEp > entry.CurrentEpisode)
                    entry.CurrentEpisode = requestedEp;
                if (entry.WatchStatus.Equals("PLANNING", StringComparison.OrdinalIgnoreCase) ||
                    entry.WatchStatus.Equals("PAUSED", StringComparison.OrdinalIgnoreCase))
                {
                    entry.WatchStatus = "WATCHING";
                }
                TryAutoCompleteFinishedEntry(entry);
                entry = _storage.UpsertEntry(entry);
                BumpState();
            }

            OpenBrowser(url);
            return Results.Ok(new { ok = true, url, entry });
        });

        app.MapPost("/api/library/{id}/refresh", async (string id) =>
        {
            var entry = _storage.GetEntryById(id);
            if (entry == null) return Results.NotFound();

            var refreshed = await _checker.RefreshEntryBindingAndEpisodesAsync(entry);
            BumpState();
            return Results.Ok(refreshed);
        });

        app.MapDelete("/api/library/{id}", (string id) =>
        {
            bool ok = _storage.DeleteEntry(id);
            if (ok) BumpState();
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        app.MapGet("/api/library/export", () => Results.Text(_storage.ExportJson(), "application/json"));

        app.MapPost("/api/library/import", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            int count = _storage.ImportJson(json);
            BumpState();
            return Results.Ok(new { ok = true, imported = count });
        });

        // --- CATALOGUE MULTI-FILTRES, SAISONS & CALENDRIER ---
        app.MapGet("/api/catalog/genres", () =>
        {
            var (curS, curY) = AniListService.GetCurrentSeasonAndYear();
            var (nextS, nextY) = AniListService.GetNextSeasonAndYear();
            return Results.Ok(new
            {
                genres = AniListService.GetAllFrenchGenres(),
                tags = AniListService.GetAllFrenchTags(),
                currentSeasonLabel = $"{AniListService.TranslateSeason(curS)} {curY}",
                nextSeasonLabel = $"{AniListService.TranslateSeason(nextS)} {nextY}"
            });
        });

        app.MapGet("/api/catalog", async (HttpRequest req) =>
        {
            var genresParam = req.Query["genres"].FirstOrDefault() ?? req.Query["genre"].FirstOrDefault() ?? "";
            var tagsParam = req.Query["tags"].FirstOrDefault() ?? "";

            var filter = new CatalogQueryFilter
            {
                Preset = req.Query["preset"].FirstOrDefault() ?? "trending",
                Search = req.Query["search"].FirstOrDefault(),
                GenresFr = genresParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                TagsFr = tagsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Status = req.Query["status"].FirstOrDefault(),
                Format = req.Query["format"].FirstOrDefault(),
                Sort = req.Query["sort"].FirstOrDefault(),
                Year = int.TryParse(req.Query["year"].FirstOrDefault(), out int yr) && yr >= 1960 ? yr : null,
                Page = int.TryParse(req.Query["page"].FirstOrDefault(), out int p) && p > 0 ? p : 1,
                PerPage = int.TryParse(req.Query["perPage"].FirstOrDefault(), out int pp) && pp > 0 ? Math.Min(pp, 50) : 28
            };

            var items = await _aniList.BrowseCatalogAsync(filter);
            return Results.Ok(items);
        });

        app.MapPost("/api/catalog/query", async (HttpContext ctx) =>
        {
            var filter = await ctx.Request.ReadFromJsonAsync<CatalogQueryFilter>() ?? new CatalogQueryFilter();
            var items = await _aniList.BrowseCatalogAsync(filter);
            return Results.Ok(items);
        });

        app.MapGet("/api/catalog/schedule", async () => Results.Ok(await _aniList.GetWeeklyScheduleAsync()));

        app.MapGet("/api/catalog/franchise/{aniListId:int}", async (int aniListId) =>
        {
            var (trailerYoutubeId, relations) = await _aniList.GetFranchiseDetailsAsync(aniListId);
            return Results.Ok(new
            {
                aniListId,
                trailerYoutubeId,
                relations
            });
        });

        app.MapPost("/api/catalog/enrich", async (HttpContext ctx) =>
        {
            var item = await ctx.Request.ReadFromJsonAsync<CatalogMediaItem>();
            if (item == null) return Results.BadRequest();

            var synopsisTask = string.IsNullOrWhiteSpace(item.SynopsisFr) && !string.IsNullOrWhiteSpace(item.SynopsisEn)
                ? _aniList.EnsureFrenchSynopsisAsync(item.AniListId, item.SynopsisEn)
                : Task.FromResult(item.SynopsisFr);

            var matchTask = _animeSama.AutoMatchAsync(item.Title, item.TitleRomaji, item.TitleEnglish, item.Synonyms, item.Format);

            await Task.WhenAll(synopsisTask, matchTask);

            var (details, matchedSeason, epCount) = matchTask.Result;
            string finalSynopsis = details != null && !string.IsNullOrWhiteSpace(details.SynopsisFr)
                ? details.SynopsisFr
                : synopsisTask.Result;

            return Results.Ok(new
            {
                synopsisFr = finalSynopsis,
                animeSamaFound = details != null && matchedSeason != null,
                animeSamaSlug = details?.Slug ?? "",
                matchedSeasonPath = matchedSeason?.Path ?? "",
                matchedSeasonName = matchedSeason?.Name ?? "",
                availableSeasons = details?.VostfrSeasons ?? new List<AnimeSamaSeasonInfo>(),
                availableEpisodesVostfr = epCount,
                watchUrl = details != null && matchedSeason != null
                    ? _animeSama.BuildWatchUrl(details.Slug, matchedSeason.Path, 1)
                    : ""
            });
        });

        // --- OUTIL DE DÉCOUVERTE (3 RECOMMANDATIONS CALIBRÉES PAR THÈME) ---
        app.MapGet("/api/discovery/profile", () =>
        {
            var (weights, ratedCount, totalCount) = _discovery.BuildTasteProfile();
            var topTastes = weights
                .Where(kv => kv.Value > 0.3)
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new TasteStat { Name = kv.Key, Weight = Math.Round(kv.Value, 1) })
                .ToList();

            return Results.Ok(new
            {
                ratedEntriesCount = ratedCount,
                totalLibraryCount = totalCount,
                dismissedCount = _storage.GetDismissedIds().Count,
                topTastes
            });
        });

        app.MapPost("/api/discovery/recommend", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<DiscoveryRequest>() ?? new DiscoveryRequest();
            var res = await _discovery.RecommendTrioAsync(req);
            return Results.Ok(res);
        });

        app.MapPost("/api/discovery/replace-one", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<DiscoveryReplaceRequest>() ?? new DiscoveryReplaceRequest();
            var pick = await _discovery.ReplaceSingleTierAsync(req);
            return Results.Ok(new { ok = pick != null, pick });
        });

        app.MapPost("/api/discovery/dismiss", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (doc.RootElement.TryGetProperty("aniListId", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                int id = idEl.GetInt32();
                _storage.DismissAniListId(id);
                return Results.Ok(new { ok = true, dismissedCount = _storage.GetDismissedIds().Count });
            }
            return Results.BadRequest(new { ok = false });
        });

        app.MapPost("/api/discovery/reset-dismissed", () =>
        {
            _storage.ClearDismissedIds();
            return Results.Ok(new { ok = true });
        });

        // --- RECHERCHE MANUELLE ANIME-SAMA ---
        app.MapGet("/api/animesama/search", async (HttpRequest req) =>
        {
            var q = req.Query["q"].FirstOrDefault() ?? "";
            return Results.Ok(await _animeSama.SearchAnimeAsync(q));
        });

        app.MapGet("/api/animesama/details", async (HttpRequest req) =>
        {
            var slug = req.Query["slug"].FirstOrDefault() ?? "";
            var details = await _animeSama.GetAnimeDetailsAsync(slug);
            return details != null ? Results.Ok(details) : Results.NotFound();
        });

        // --- NOTIFICATIONS ---
        app.MapGet("/api/notifications", () => Results.Ok(_storage.GetNotifications()));

        app.MapPost("/api/notifications/check-now", async () =>
        {
            var created = await _checker.CheckAllFollowedAsync(forceAll: false);
            BumpState();
            return Results.Ok(new { ok = true, newCount = created.Count, notifications = created });
        });

        app.MapPost("/api/notifications/read-all", () =>
        {
            _storage.MarkAllNotificationsRead();
            BumpState();
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/notifications", () =>
        {
            _storage.ClearNotifications();
            BumpState();
            return Results.Ok(new { ok = true });
        });

        // --- EXTENSION CHROMIUM SYNC & IMPORT ---
        app.MapPost("/api/extension/sync-episode", async (HttpContext ctx) =>
        {
            var sync = await ctx.Request.ReadFromJsonAsync<ExtensionEpisodeSyncRequest>();
            if (sync == null || string.IsNullOrWhiteSpace(sync.Slug))
                return Results.BadRequest(new { ok = false });

            if (sync.SeasonPath.Contains("scan", StringComparison.OrdinalIgnoreCase) ||
                sync.Url.Contains("/scan", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { ok = true, ignored = true });
            }

            var slug = sync.Slug.Trim('/').ToLowerInvariant();
            var seasonPath = StorageService.NormalizeToVostfrSeasonPath(sync.SeasonPath);
            int ep = Math.Max(1, sync.EpisodeNumber);

            var existing = _storage.FindMatchingEntry(null, slug, seasonPath, sync.Title);
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.AnimeSamaSlug))
                    existing.AnimeSamaSlug = slug;
                if (string.IsNullOrWhiteSpace(existing.AnimeSamaSeasonPath) && !string.IsNullOrWhiteSpace(seasonPath))
                    existing.AnimeSamaSeasonPath = seasonPath;
                if (string.IsNullOrWhiteSpace(existing.AnimeSamaSeasonName) && !string.IsNullOrWhiteSpace(sync.SeasonName))
                    existing.AnimeSamaSeasonName = sync.SeasonName;

                if (sync.TotalAvailable > existing.AvailableEpisodesVostfr)
                {
                    existing.AvailableEpisodesVostfr = sync.TotalAvailable;
                    existing.LastNotifiedEpisode = Math.Max(existing.LastNotifiedEpisode, sync.TotalAvailable);
                }

                // Protection contre les clics de curiosité (saut > +3 épisodes sans confirmation explicite)
                if (!sync.ForceJump && existing.CurrentEpisode > 0 && ep > existing.CurrentEpisode + 3)
                {
                    _storage.UpsertEntry(existing);
                    return Results.Ok(new
                    {
                        ok = true,
                        jumpBlocked = true,
                        currentEpisode = existing.CurrentEpisode,
                        targetEpisode = ep
                    });
                }

                if (ep > existing.CurrentEpisode || sync.ForceJump)
                {
                    existing.CurrentEpisode = sync.ForceJump ? ep : Math.Max(existing.CurrentEpisode, ep);
                }

                if (existing.WatchStatus.Equals("PLANNING", StringComparison.OrdinalIgnoreCase) ||
                    existing.WatchStatus.Equals("PAUSED", StringComparison.OrdinalIgnoreCase))
                {
                    existing.WatchStatus = "WATCHING";
                }

                bool autoCompleted = TryAutoCompleteFinishedEntry(existing);

                var updated = _storage.UpsertEntry(existing);
                BumpState();
                return Results.Ok(new { ok = true, entry = updated, created = false, autoCompleted });
            }
            else
            {
                // Vérifie si l'utilisateur suivait déjà une saison précédente du même slug (ex: Saison 1 -> Saison 2)
                var siblingSeasons = _storage.FindEntriesBySlug(slug);
                bool inheritFav = siblingSeasons.Any(s => s.IsFavorite);

                foreach (var prev in siblingSeasons)
                {
                    if (prev.WatchStatus.Equals("WATCHING", StringComparison.OrdinalIgnoreCase) &&
                        prev.AvailableEpisodesVostfr > 0 &&
                        prev.CurrentEpisode >= prev.AvailableEpisodesVostfr)
                    {
                        prev.WatchStatus = "COMPLETED";
                        _storage.UpsertEntry(prev);
                    }
                }

                string rawTitle = !string.IsNullOrWhiteSpace(sync.Title) ? sync.Title : slug;
                string seasonLabel = !string.IsNullOrWhiteSpace(sync.SeasonName) ? sync.SeasonName : FormatSeasonLabel(seasonPath);
                int sNum = StorageService.ExtractSeasonNumber(seasonPath);
                bool isSpecialOrMovie = seasonPath.Contains("film", StringComparison.OrdinalIgnoreCase) ||
                                        seasonPath.Contains("oav", StringComparison.OrdinalIgnoreCase);

                string displayTitle = (sNum >= 2 || isSpecialOrMovie) &&
                                      !rawTitle.Contains(seasonLabel, StringComparison.OrdinalIgnoreCase)
                    ? $"{rawTitle} — {seasonLabel}"
                    : rawTitle;

                var newEntry = new LibraryEntry
                {
                    Id = $"as_{slug}_{seasonPath.Replace('/', '_')}",
                    Title = displayTitle,
                    CoverUrl = sync.CoverUrl,
                    AnimeSamaSlug = slug,
                    AnimeSamaSeasonPath = seasonPath,
                    AnimeSamaSeasonName = seasonLabel,
                    CurrentEpisode = ep,
                    AvailableEpisodesVostfr = Math.Max(ep, sync.TotalAvailable),
                    LastNotifiedEpisode = Math.Max(ep, sync.TotalAvailable),
                    WatchStatus = "WATCHING",
                    IsFavorite = inheritFav,
                    NotifyOnNewEpisode = true,
                    LastCheckedAt = DateTimeOffset.UtcNow
                };

                var saved = _storage.UpsertEntry(newEntry);
                BumpState();

                _ = Task.Run(() => EnrichImportedEntryWithAniListAsync(saved));
                return Results.Ok(new { ok = true, entry = saved, created = true });
            }
        });

        app.MapPost("/api/extension/import-animesama", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<ExtensionBulkImportRequest>();
            if (req == null || req.Items.Count == 0)
                return Results.Ok(new { ok = true, imported = 0 });

            int importedCount = 0;
            var toEnrich = new List<LibraryEntry>();

            foreach (var item in req.Items)
            {
                if (item.Kind.Equals("Scans", StringComparison.OrdinalIgnoreCase) ||
                    item.Url.Contains("/scan", StringComparison.OrdinalIgnoreCase))
                    continue;

                var (slug, rawSeasonPath) = ParseAnimeSamaRelativeUrl(item.Url);
                if (string.IsNullOrWhiteSpace(slug)) continue;

                var seasonPath = string.IsNullOrWhiteSpace(rawSeasonPath)
                    ? "saison1/vostfr"
                    : StorageService.NormalizeToVostfrSeasonPath(rawSeasonPath);

                var existing = _storage.FindMatchingEntry(null, slug, seasonPath, item.Title);
                if (existing != null)
                {
                    if (item.EpisodeNumber > existing.CurrentEpisode)
                        existing.CurrentEpisode = item.EpisodeNumber;
                    if (item.Category == "favorite")
                        existing.IsFavorite = true;
                    else if (item.Category == "viewed" && existing.WatchStatus == "PLANNING")
                        existing.WatchStatus = "COMPLETED";

                    _storage.UpsertEntry(existing);
                    continue;
                }

                string watchStatus = item.Category switch
                {
                    "viewed" => "COMPLETED",
                    "watchlist" => "PLANNING",
                    "favorite" => item.EpisodeNumber > 0 ? "WATCHING" : "PLANNING",
                    _ => item.EpisodeNumber > 0 ? "WATCHING" : "PLANNING"
                };

                var entry = new LibraryEntry
                {
                    Id = $"as_{slug}_{seasonPath.Replace('/', '_')}",
                    Title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : slug.Replace('-', ' '),
                    CoverUrl = item.CoverUrl,
                    AnimeSamaSlug = slug,
                    AnimeSamaSeasonPath = seasonPath,
                    AnimeSamaSeasonName = FormatSeasonLabel(seasonPath),
                    CurrentEpisode = Math.Max(0, item.EpisodeNumber),
                    WatchStatus = watchStatus,
                    IsFavorite = item.Category == "favorite",
                    NotifyOnNewEpisode = true
                };

                var saved = _storage.UpsertEntry(entry);
                toEnrich.Add(saved);
                importedCount++;
            }

            BumpState();

            _ = Task.Run(async () =>
            {
                foreach (var entry in toEnrich)
                {
                    await EnrichImportedEntryWithAniListAsync(entry);
                    await Task.Delay(350);
                }
                BumpState();
            });

            return Results.Ok(new { ok = true, imported = importedCount });
        });
    }

    private async Task EnrichImportedEntryWithAniListAsync(LibraryEntry entry)
    {
        try
        {
            var details = await _animeSama.GetAnimeDetailsAsync(entry.AnimeSamaSlug);
            if (details != null)
            {
                if (!string.IsNullOrWhiteSpace(details.Title))
                {
                    int sNum = StorageService.ExtractSeasonNumber(entry.AnimeSamaSeasonPath);
                    bool isSpecial = entry.AnimeSamaSeasonPath.Contains("film", StringComparison.OrdinalIgnoreCase);
                    entry.Title = (sNum >= 2 || isSpecial) && !string.IsNullOrWhiteSpace(entry.AnimeSamaSeasonName)
                        ? $"{details.Title} — {entry.AnimeSamaSeasonName}"
                        : details.Title;
                }
                if (!string.IsNullOrWhiteSpace(details.SynopsisFr))
                    entry.SynopsisFr = details.SynopsisFr;
                if (details.VostfrSeasons.Count > 0)
                    entry.AvailableSeasons = details.VostfrSeasons;
            }

            if (!string.IsNullOrWhiteSpace(entry.AnimeSamaSeasonPath))
            {
                int eps = await _animeSama.GetEpisodeCountAsync(entry.AnimeSamaSlug, entry.AnimeSamaSeasonPath);
                if (eps > 0)
                {
                    entry.AvailableEpisodesVostfr = eps;
                    entry.LastNotifiedEpisode = eps;
                    if (entry.WatchStatus == "COMPLETED" && entry.CurrentEpisode == 0)
                        entry.CurrentEpisode = eps;
                }
            }

            string searchTitle = entry.Title;
            if (!string.IsNullOrWhiteSpace(entry.AnimeSamaSeasonName) &&
                entry.AnimeSamaSeasonName.Contains("Saison", StringComparison.OrdinalIgnoreCase) &&
                !entry.AnimeSamaSeasonName.Contains("1"))
            {
                searchTitle = $"{entry.Title} {entry.AnimeSamaSeasonName.Replace("Saison", "Season", StringComparison.OrdinalIgnoreCase)}";
            }

            var alMatches = await _aniList.BrowseCatalogAsync(new CatalogQueryFilter
            {
                Preset = "trending",
                Search = searchTitle,
                Page = 1,
                PerPage = 3
            });
            var bestAl = alMatches.FirstOrDefault();
            if (bestAl != null)
            {
                entry.AniListId ??= bestAl.AniListId;
                entry.TitleRomaji = bestAl.TitleRomaji;
                entry.TitleEnglish = bestAl.TitleEnglish;
                entry.TitleNative = bestAl.TitleNative;
                entry.Synonyms = bestAl.Synonyms;
                if (!string.IsNullOrWhiteSpace(bestAl.CoverUrl)) entry.CoverUrl = bestAl.CoverUrl;
                if (!string.IsNullOrWhiteSpace(bestAl.BannerUrl)) entry.BannerUrl = bestAl.BannerUrl;
                entry.GenresFr = bestAl.GenresFr;
                entry.TagsFr = bestAl.TagsFr;
                entry.Format = bestAl.Format;
                entry.ReleaseStatus = bestAl.ReleaseStatus;
                entry.Season = bestAl.Season;
                entry.SeasonYear = bestAl.SeasonYear;
                entry.TotalEpisodesPlanned = bestAl.Episodes;
                entry.AverageScore = bestAl.AverageScore;
                entry.NextAiringAt = bestAl.NextAiringAt;
                entry.NextAiringEpisode = bestAl.NextAiringEpisode;
                entry.StartDateLabel = bestAl.StartDateLabel;
                if (!string.IsNullOrWhiteSpace(bestAl.TrailerYoutubeId))
                    entry.TrailerYoutubeId = bestAl.TrailerYoutubeId;
                TryAutoCompleteFinishedEntry(entry);
            }

            if (_storage.GetEntryById(entry.Id) == null) return;
            entry.LastCheckedAt = DateTimeOffset.UtcNow;
            _storage.UpsertEntry(entry);
            BumpState();
        }
        catch { }
    }

    private static bool TryAutoCompleteFinishedEntry(LibraryEntry entry)
    {
        if (!entry.WatchStatus.Equals("WATCHING", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!entry.ReleaseStatus.Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
            return false;

        int finalEp = entry.TotalEpisodesPlanned ?? entry.AvailableEpisodesVostfr;
        if (finalEp > 0 && entry.CurrentEpisode >= finalEp)
        {
            entry.WatchStatus = "COMPLETED";
            return true;
        }
        return false;
    }

    private static (string Slug, string SeasonPath) ParseAnimeSamaRelativeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("", "");
        try
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(url);
                url = uri.AbsolutePath;
            }
        }
        catch { }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int catIdx = Array.FindIndex(parts, p => p.Equals("catalogue", StringComparison.OrdinalIgnoreCase));
        if (catIdx < 0 || catIdx + 1 >= parts.Length) return ("", "");

        string slug = parts[catIdx + 1].ToLowerInvariant();
        string seasonPath = "";
        if (catIdx + 3 < parts.Length)
            seasonPath = $"{parts[catIdx + 2]}/{parts[catIdx + 3]}".ToLowerInvariant();
        else if (catIdx + 2 < parts.Length)
            seasonPath = $"{parts[catIdx + 2]}/vostfr".ToLowerInvariant();

        return (slug, seasonPath);
    }

    private static string FormatSeasonLabel(string seasonPath)
    {
        if (string.IsNullOrWhiteSpace(seasonPath)) return "Saison 1";
        var first = seasonPath.Split('/')[0];
        var m = System.Text.RegularExpressions.Regex.Match(first, @"saison\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return $"Saison {m.Groups[1].Value}";
        if (first.Contains("film", StringComparison.OrdinalIgnoreCase)) return "Films";
        return first;
    }

    private static string GetJsonString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
