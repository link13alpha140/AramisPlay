using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AramisPlay.Models;

namespace AramisPlay.Services;

public class AnimeSamaService
{
    private readonly StorageService _storage;
    private readonly HttpClient _http;
    private readonly SourceHealthStatus _health = new();

    private static readonly string[] CandidateDomains =
    {
        "https://anime-sama.to",
        "https://anime-sama.si",
        "https://anime-sama.org",
        "https://anime-sama.fr",
        "https://anime-sama.st",
        "https://anime-sama.me",
        "https://anime-sama.tv"
    };

    public AnimeSamaService(StorageService storage)
    {
        _storage = storage;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    public string BaseUrl => _storage.GetSettings().AnimeSamaBaseUrl.TrimEnd('/');

    public SourceHealthStatus GetHealthStatus()
    {
        _health.Domain = BaseUrl;
        return _health;
    }

    public async Task<SourceHealthStatus> CheckHealthAndAutoHealDomainAsync()
    {
        var current = BaseUrl;
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current + "/");
            using var resp = await _http.SendAsync(req);
            sw.Stop();

            var finalOrigin = resp.RequestMessage?.RequestUri != null
                ? $"{resp.RequestMessage.RequestUri.Scheme}://{resp.RequestMessage.RequestUri.Host}"
                : current;

            var html = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == HttpStatusCode.Forbidden ||
                resp.StatusCode == HttpStatusCode.ServiceUnavailable ||
                html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
            {
                _health.State = "cloudflare";
                _health.FailureCount++;
                _health.LatencyMs = sw.ElapsedMilliseconds;
                _health.LastCheckedAt = DateTimeOffset.UtcNow;
                _health.Message = "Protection Cloudflare active sur ce domaine";
                return _health;
            }

            if (resp.IsSuccessStatusCode && html.Contains("anime-sama", StringComparison.OrdinalIgnoreCase))
            {
                _health.SuccessCount++;
                _health.LatencyMs = sw.ElapsedMilliseconds;
                _health.LastCheckedAt = DateTimeOffset.UtcNow;

                if (!string.Equals(finalOrigin.TrimEnd('/'), current.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    _storage.UpdateSettings(s => s.AnimeSamaBaseUrl = finalOrigin);
                    _health.Domain = finalOrigin;
                    _health.State = "redirected";
                    _health.Message = $"Domaine mis à jour automatiquement vers {finalOrigin}";
                }
                else
                {
                    _health.Domain = current;
                    _health.State = "online";
                    _health.Message = $"En ligne ({sw.ElapsedMilliseconds} ms)";
                }
                return _health;
            }
        }
        catch { /* On tente l'auto-détection sur les autres extensions */ }

        return await AutoDetectDomainAsync();
    }

    public async Task<SourceHealthStatus> AutoDetectDomainAsync()
    {
        foreach (var candidate in CandidateDomains)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate + "/");
                using var resp = await _http.SendAsync(req);
                sw.Stop();
                if (!resp.IsSuccessStatusCode) continue;

                var html = await resp.Content.ReadAsStringAsync();
                if (!html.Contains("anime-sama", StringComparison.OrdinalIgnoreCase)) continue;

                var finalOrigin = resp.RequestMessage?.RequestUri != null
                    ? $"{resp.RequestMessage.RequestUri.Scheme}://{resp.RequestMessage.RequestUri.Host}"
                    : candidate;

                _storage.UpdateSettings(s => s.AnimeSamaBaseUrl = finalOrigin);
                _health.Domain = finalOrigin;
                _health.State = "online";
                _health.SuccessCount++;
                _health.LatencyMs = sw.ElapsedMilliseconds;
                _health.LastCheckedAt = DateTimeOffset.UtcNow;
                _health.Message = $"Domaine actif détecté : {finalOrigin} ({sw.ElapsedMilliseconds} ms)";
                return _health;
            }
            catch { }
        }

        _health.State = "offline";
        _health.FailureCount++;
        _health.LastCheckedAt = DateTimeOffset.UtcNow;
        _health.Message = "Impossible de joindre Anime-Sama (vérifie ta connexion ou le domaine)";
        return _health;
    }

    public async Task<List<AnimeSamaSearchCandidate>> SearchAnimeAsync(string query)
    {
        var results = new List<AnimeSamaSearchCandidate>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/template-php/defaut/fetch.php");
            req.Headers.Referrer = new Uri($"{BaseUrl}/");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["query"] = query.Trim()
            });

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return results;

            var html = await resp.Content.ReadAsStringAsync();
            var anchorMatches = Regex.Matches(
                html,
                @"<a\s+href=[""']([^""']*?/catalogue/([^/""']+)/?)[""'][^>]*class=[""'][^""']*asn-search-result[^""']*[""'][^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in anchorMatches)
            {
                var slug = m.Groups[2].Value.Trim();
                var inner = m.Groups[3].Value;

                var titleMatch = Regex.Match(inner, @"class=[""']asn-search-result-title[""'][^>]*>(.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var subMatch = Regex.Match(inner, @"class=[""']asn-search-result-subtitle[""'][^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var metaMatch = Regex.Match(inner, @"class=[""']asn-search-meta[""'][^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var imgMatch = Regex.Match(inner, @"src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

                var title = StripTags(titleMatch.Success ? titleMatch.Groups[1].Value : slug);
                var subtitle = StripTags(subMatch.Success ? subMatch.Groups[1].Value : "");
                var meta = StripTags(metaMatch.Success ? metaMatch.Groups[1].Value : "");
                var cover = imgMatch.Success ? imgMatch.Groups[1].Value : "";

                // Si la fiche ne contient que "Scans" sans "Anime", on la pénalise fortement
                if (!string.IsNullOrEmpty(meta) &&
                    meta.Contains("Scans", StringComparison.OrdinalIgnoreCase) &&
                    !meta.Contains("Anime", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new AnimeSamaSearchCandidate
                {
                    Slug = slug,
                    Title = title,
                    Subtitle = subtitle,
                    Meta = meta,
                    CoverUrl = cover
                });
            }
        }
        catch { }

        return results;
    }

    public async Task<AnimeSamaAnimeDetails?> GetAnimeDetailsAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        slug = slug.Trim('/').ToLowerInvariant();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/catalogue/{slug}/");
            req.Headers.Referrer = new Uri($"{BaseUrl}/catalogue/");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync();

            var titleMatch = Regex.Match(html, @"id=[""']titreOeuvre[""'][^>]*>(.*?)</", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var alterMatch = Regex.Match(html, @"id=[""']titreAlter[""'][^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var synMatch = Regex.Match(html, @"id=[""']synopsisText[""'][^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var covMatch = Regex.Match(html, @"id=[""']coverOeuvre[""'][^>]*src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

            // Supprime les commentaires JS /* ... */ et // ... pour ignorer les exemples commentés de panneauAnime
            var cleanHtml = Regex.Replace(html, @"/\*.*?\*/", "", RegexOptions.Singleline);
            cleanHtml = Regex.Replace(cleanHtml, @"//[^\r\n]*", "");

            var seasons = new List<AnimeSamaSeasonInfo>();
            var panelMatches = Regex.Matches(
                cleanHtml,
                @"panneauAnime\(\s*[""']([^""']+)[""']\s*,\s*[""']([^""']+)[""']\s*\)",
                RegexOptions.IgnoreCase);

            foreach (Match pm in panelMatches)
            {
                var sName = WebUtility.HtmlDecode(pm.Groups[1].Value.Trim());
                var sPath = pm.Groups[2].Value.Trim().Trim('/');

                if (sName.Equals("nom", StringComparison.OrdinalIgnoreCase) ||
                    sPath.Equals("url", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Priorité stricte à la VOSTFR (ou VO/VJSTFR)
                if (sPath.EndsWith("/vostfr", StringComparison.OrdinalIgnoreCase) ||
                    sPath.EndsWith("/vj", StringComparison.OrdinalIgnoreCase) ||
                    sPath.EndsWith("/vjstfr", StringComparison.OrdinalIgnoreCase))
                {
                    if (!seasons.Any(x => x.Path.Equals(sPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        seasons.Add(new AnimeSamaSeasonInfo { Name = sName, Path = sPath });
                    }
                }
            }

            return new AnimeSamaAnimeDetails
            {
                Slug = slug,
                Title = StripTags(titleMatch.Success ? titleMatch.Groups[1].Value : slug),
                AltTitles = StripTags(alterMatch.Success ? alterMatch.Groups[1].Value : ""),
                SynopsisFr = StripTags(synMatch.Success ? synMatch.Groups[1].Value : ""),
                CoverUrl = covMatch.Success ? covMatch.Groups[1].Value : "",
                VostfrSeasons = seasons
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<int> GetEpisodeCountAsync(string slug, string seasonPath)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(seasonPath))
            return 0;

        slug = slug.Trim('/').ToLowerInvariant();
        seasonPath = seasonPath.Trim('/');

        try
        {
            var seasonUrl = $"{BaseUrl}/catalogue/{slug}/{seasonPath}/";
            var episodesJsUrl = $"{seasonUrl}episodes.js";

            using var req = new HttpRequestMessage(HttpMethod.Get, episodesJsUrl);
            req.Headers.Referrer = new Uri(seasonUrl); // Indispensable contre le 403 hotlink Anime-Sama
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return 0;

            var js = await resp.Content.ReadAsStringAsync();

            // Nettoyage des commentaires éventuels dans episodes.js
            var cleanJs = Regex.Replace(js, @"/\*.*?\*/", "", RegexOptions.Singleline);

            int maxCount = 0;
            var arrMatches = Regex.Matches(cleanJs, @"var\s+eps\d+\s*=\s*\[(.*?)\]\s*;", RegexOptions.Singleline);
            foreach (Match am in arrMatches)
            {
                var body = am.Groups[1].Value;
                var urlMatches = Regex.Matches(body, @"[""']https?://[^""']+[""']");
                if (urlMatches.Count > maxCount)
                    maxCount = urlMatches.Count;
            }

            if (maxCount == 0) return 0;

            // Déduit les épisodes spéciaux intercalés (ex: Episode 7.5 via newSP / newSPF) comme dans videos.js
            int specialsCount = Regex.Matches(cleanJs, @"\bnewSP(?:F)?\s*\(").Count;
            int regularCount = Math.Max(1, maxCount - specialsCount);
            return regularCount;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<(AnimeSamaAnimeDetails? Details, AnimeSamaSeasonInfo? MatchedSeason, int EpisodeCount)> AutoMatchAsync(
        string title,
        string? titleRomaji,
        string? titleEnglish,
        List<string>? synonyms = null,
        string? format = null)
    {
        var allTitles = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) allTitles.Add(title);
        if (!string.IsNullOrWhiteSpace(titleRomaji)) allTitles.Add(titleRomaji);
        if (!string.IsNullOrWhiteSpace(titleEnglish)) allTitles.Add(titleEnglish);
        if (synonyms != null) allTitles.AddRange(synonyms.Where(s => !string.IsNullOrWhiteSpace(s)).Take(4));

        int targetSeasonNum = DetectSeasonNumber(allTitles);
        bool isMovie = string.Equals(format, "MOVIE", StringComparison.OrdinalIgnoreCase) ||
                       allTitles.Any(t => Regex.IsMatch(t, @"\b(movie|film|gekijouban)\b", RegexOptions.IgnoreCase));

        var searchQueries = new List<string>();
        foreach (var raw in allTitles)
        {
            var cleaned = CleanBaseTitleForSearch(raw);
            if (cleaned.Length >= 2) searchQueries.Add(cleaned);

            // Si le titre contient un sous-titre d'arc après ":" (ex: "Kimetsu no Yaiba: Yuukaku-hen"),
            // on ajoute aussi la racine avant ":" pour que fetch.php d'Anime-Sama trouve bien la franchise
            int colonIdx = cleaned.IndexOf(':');
            if (colonIdx >= 3)
            {
                var prefix = cleaned[..colonIdx].Trim();
                if (prefix.Length >= 3) searchQueries.Add(prefix);
            }
        }

        searchQueries = searchQueries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        AnimeSamaSearchCandidate? bestCandidate = null;
        double bestScore = 0;

        foreach (var q in searchQueries)
        {
            var candidates = await SearchAnimeAsync(q);
            foreach (var c in candidates)
            {
                double score = ComputeMatchScore(allTitles, q, c);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = c;
                }
            }
            if (bestScore >= 0.90) break;
        }

        if (bestCandidate == null || bestScore < 0.42)
            return (null, null, 0);

        var details = await GetAnimeDetailsAsync(bestCandidate.Slug);
        if (details == null || details.VostfrSeasons.Count == 0)
            return (details, null, 0);

        var chosenSeason = PickBestSeason(details.VostfrSeasons, targetSeasonNum, isMovie, allTitles);
        int epCount = chosenSeason != null
            ? await GetEpisodeCountAsync(details.Slug, chosenSeason.Path)
            : 0;

        return (details, chosenSeason, epCount);
    }

    public string BuildWatchUrl(string slug, string seasonPath, int episodeNumber = 0)
    {
        slug = slug.Trim('/');
        seasonPath = string.IsNullOrWhiteSpace(seasonPath) ? "saison1/vostfr" : seasonPath.Trim('/');
        var baseUrl = $"{BaseUrl}/catalogue/{slug}/{seasonPath}/";
        return episodeNumber > 0 ? $"{baseUrl}#ap_ep={episodeNumber}" : baseUrl;
    }

    private static AnimeSamaSeasonInfo? PickBestSeason(
        List<AnimeSamaSeasonInfo> seasons,
        int targetSeasonNum,
        bool isMovie,
        List<string> allTitles)
    {
        if (seasons.Count == 0) return null;

        if (isMovie)
        {
            var movie = seasons.FirstOrDefault(s =>
                s.Path.Contains("film", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("film", StringComparison.OrdinalIgnoreCase));
            if (movie != null) return movie;
        }

        if (targetSeasonNum > 1)
        {
            var exactPath = seasons.FirstOrDefault(s =>
                Regex.IsMatch(s.Path, $@"saison\s*{targetSeasonNum}\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(s.Name, $@"saison\s*{targetSeasonNum}\b", RegexOptions.IgnoreCase));
            if (exactPath != null) return exactPath;
        }

        // Recherche par mots-clés de sous-titre d'arc (ex: "Mugen Train", "Shibuya", "Thousand-Year")
        var arcKeywords = allTitles
            .Where(t => t.Contains(':'))
            .Select(t => t[(t.IndexOf(':') + 1)..].Trim())
            .SelectMany(sub => Regex.Split(sub, @"[^a-zA-Z0-9]+"))
            .Where(w => w.Length >= 5 && !Regex.IsMatch(w, @"^(season|saison|movie|part|cour|hen|the|arc)$", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (arcKeywords.Count > 0)
        {
            var byArc = seasons.FirstOrDefault(s =>
                arcKeywords.Any(kw =>
                    s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    s.Path.Contains(kw, StringComparison.OrdinalIgnoreCase)));
            if (byArc != null) return byArc;
        }

        var s1 = seasons.FirstOrDefault(s =>
            s.Path.StartsWith("saison1/", StringComparison.OrdinalIgnoreCase));
        return s1 ?? seasons[0];
    }

    private static int DetectSeasonNumber(IEnumerable<string> titles)
    {
        foreach (var t in titles)
        {
            var m1 = Regex.Match(t, @"\b(?:season|saison)\s*(\d+)\b", RegexOptions.IgnoreCase);
            if (m1.Success && int.TryParse(m1.Groups[1].Value, out int n1)) return n1;

            var m2 = Regex.Match(t, @"\b(\d+)(?:nd|rd|th)\s+season\b", RegexOptions.IgnoreCase);
            if (m2.Success && int.TryParse(m2.Groups[1].Value, out int n2)) return n2;

            var m3 = Regex.Match(t, @"\bS(\d+)\b");
            if (m3.Success && int.TryParse(m3.Groups[1].Value, out int n3)) return n3;

            // Chiffres romains en fin de titre ou avant ":" (ex: Overlord IV, Mushoku Tensei II: ...)
            var mRoman = Regex.Match(t, @"\s+(II|III|IV|V|VI)(?:\s*[:\-–]|$)");
            if (mRoman.Success)
            {
                return mRoman.Groups[1].Value switch
                {
                    "II" => 2,
                    "III" => 3,
                    "IV" => 4,
                    "V" => 5,
                    "VI" => 6,
                    _ => 1
                };
            }
        }
        return 1;
    }

    private static string CleanBaseTitleForSearch(string raw)
    {
        var s = raw;
        s = Regex.Replace(s, @"\b(\d+)(?:st|nd|rd|th)\s+season\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:season|saison|part|partie|cour)\s*\d+\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(?:II|III|IV|V|VI)(?=\s*[:\-–]|$)", "");
        s = Regex.Replace(s, @"[:\-–—]\s*(?:the\s+final\s+season|next\s+generations).*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\((?:TV|\d{4})\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', ':', '-', '–');
        return s.Length >= 2 ? s : raw.Trim();
    }

    private static double ComputeMatchScore(List<string> targetTitles, string cleanQuery, AnimeSamaSearchCandidate c)
    {
        var candidateStrings = new List<string> { c.Title, c.Slug.Replace('-', ' ') };
        if (!string.IsNullOrWhiteSpace(c.Subtitle))
        {
            candidateStrings.AddRange(c.Subtitle.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var compareTargets = targetTitles
            .Concat(new[] { cleanQuery })
            .Select(CleanBaseTitleForSearch)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        double best = 0;
        foreach (var t in compareTargets)
        {
            var nt = StorageService.NormalizeTitle(t);
            if (nt.Length == 0) continue;

            foreach (var cs in candidateStrings)
            {
                var nc = StorageService.NormalizeTitle(cs);
                if (nc.Length == 0) continue;

                if (nt == nc) return 1.0;
                if (nc.Contains(nt) || nt.Contains(nc))
                {
                    double ratio = (double)Math.Min(nt.Length, nc.Length) / Math.Max(nt.Length, nc.Length);
                    best = Math.Max(best, 0.72 + ratio * 0.25);
                }

                int dist = Levenshtein(nt, nc);
                int maxLen = Math.Max(nt.Length, nc.Length);
                double sim = 1.0 - (double)dist / maxLen;
                if (sim > best) best = sim;
            }
        }
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[n, m];
    }

    private static string StripTags(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var noTags = Regex.Replace(html, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(noTags).Trim();
    }
}
