using AramisPlay.Models;

namespace AramisPlay.Services;

public class DiscoveryService
{
    private readonly StorageService _storage;
    private readonly AniListService _aniList;
    private readonly AnimeSamaService _animeSama;

    public DiscoveryService(StorageService storage, AniListService aniList, AnimeSamaService animeSama)
    {
        _storage = storage;
        _aniList = aniList;
        _animeSama = animeSama;
    }

    public (Dictionary<string, double> Weights, int RatedCount, int TotalCount) BuildTasteProfile()
    {
        var entries = _storage.GetAllEntries();
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int ratedCount = 0;

        foreach (var e in entries)
        {
            double w = 0;
            if (e.PersonalRating.HasValue && e.PersonalRating.Value > 0)
            {
                ratedCount++;
                int r = e.PersonalRating.Value;
                w = r switch
                {
                    10 => 3.6,
                    9 => 2.8,
                    8 => 2.0,
                    7 => 1.1,
                    6 => 0.3,
                    5 => -0.5,
                    _ => -2.2
                };
            }
            else
            {
                w = e.WatchStatus.ToUpperInvariant() switch
                {
                    "COMPLETED" => 0.9,
                    "WATCHING" => 0.7,
                    "DROPPED" => -1.8,
                    _ => 0.2
                };
            }

            if (e.IsFavorite) w += 1.5;
            if (Math.Abs(w) < 0.05) continue;

            var traits = e.GenresFr.Concat(e.TagsFr).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var trait in traits)
            {
                weights[trait] = weights.GetValueOrDefault(trait, 0) + w;
            }
        }

        return (weights, ratedCount, entries.Count);
    }

    public async Task<DiscoveryResponse> RecommendTrioAsync(DiscoveryRequest req)
    {
        var (tasteWeights, ratedCount, totalLibCount) = BuildTasteProfile();

        var topTastes = tasteWeights
            .Where(kv => kv.Value > 0.3)
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .Select(kv => new TasteStat { Name = kv.Key, Weight = Math.Round(kv.Value, 1) })
            .ToList();

        var themes = (req.Themes ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (themes.Count == 0)
            themes.Add("Action");

        var rawPool = await _aniList.FetchPoolForThemesAsync(themes);

        var libEntries = _storage.GetAllEntries();
        var ownedAniListIds = libEntries
            .Where(e => e.AniListId.HasValue)
            .Select(e => e.AniListId!.Value)
            .ToHashSet();
        var ownedNormTitles = libEntries
            .Select(e => StorageService.NormalizeTitle(e.Title))
            .Where(s => s.Length > 0)
            .ToHashSet();
        var excludedIds = (req.ExcludeAniListIds ?? new List<int>()).ToHashSet();
        var dismissedIds = _storage.GetDismissedIds();

        // Filtre : pas déjà dans la bibli, pas masqué définitivement, pas une suite/OVA annexe, déjà sorti, bonne qualité minimale
        var candidates = rawPool
            .Where(m =>
                m.AniListId > 0 &&
                !ownedAniListIds.Contains(m.AniListId) &&
                !dismissedIds.Contains(m.AniListId) &&
                !excludedIds.Contains(m.AniListId) &&
                !ownedNormTitles.Contains(StorageService.NormalizeTitle(m.Title)) &&
                !m.HasPrequel &&
                !IsSideFormat(m.Format) &&
                !IsLikelySequelTitle(m.Title) &&
                !string.Equals(m.ReleaseStatus, "NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase) &&
                (m.AverageScore ?? 70) >= 65)
            .ToList();

        if (candidates.Count < 3)
        {
            // Assouplit le filtre d'exclusion de session (mais conserve les titres masqués définitivement et déjà possédés)
            candidates = rawPool
                .Where(m =>
                    m.AniListId > 0 &&
                    !ownedAniListIds.Contains(m.AniListId) &&
                    !dismissedIds.Contains(m.AniListId) &&
                    !m.HasPrequel &&
                    !IsSideFormat(m.Format) &&
                    !string.Equals(m.ReleaseStatus, "NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var response = new DiscoveryResponse
        {
            RatedEntriesCount = ratedCount,
            TotalLibraryCount = totalLibCount,
            TopTastes = topTastes
        };

        if (candidates.Count == 0)
            return response;

        bool hasPersonalTaste = tasteWeights.Values.Any(v => Math.Abs(v) > 0.4);
        var themeSet = new HashSet<string>(themes, StringComparer.OrdinalIgnoreCase);

        var scored = candidates.Select(c =>
        {
            var allTraits = c.GenresFr.Concat(c.TagsFr).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var secondaryTraits = allTraits.Where(t => !themeSet.Contains(t)).ToList();

            double rawAffinity = 0;
            var likedMatches = new List<string>();
            var freshTraits = new List<string>();

            if (hasPersonalTaste && secondaryTraits.Count > 0)
            {
                double sum = 0;
                foreach (var tr in secondaryTraits)
                {
                    if (tasteWeights.TryGetValue(tr, out double tw))
                    {
                        sum += tw;
                        if (tw >= 1.0) likedMatches.Add(tr);
                        else if (tw <= 0.1) freshTraits.Add(tr);
                    }
                    else
                    {
                        freshTraits.Add(tr);
                    }
                }
                rawAffinity = sum / Math.Sqrt(Math.Max(2, secondaryTraits.Count));
            }
            else
            {
                // Sans historique noté : utilise la popularité grand public comme proxy de proximité
                rawAffinity = Math.Log10(Math.Max(100, c.Popularity ?? 1000));
                freshTraits = secondaryTraits.Take(3).ToList();
            }

            return new ScoredCandidate(c, rawAffinity, allTraits, likedMatches, freshTraits);
        }).OrderByDescending(x => x.RawAffinity).ToList();

        double maxAff = scored.Max(x => x.RawAffinity);
        double minAff = scored.Min(x => x.RawAffinity);
        double span = Math.Max(0.001, maxAff - minAff);

        foreach (var sc in scored)
        {
            sc.Normalized01 = (sc.RawAffinity - minAff) / span;
        }

        // 1. VALEUR SÛRE (Très proche de tes goûts) — top 20% combiné à la note critique
        var closePool = scored.Take(Math.Max(1, scored.Count / 4)).ToList();
        var closePick = closePool
            .OrderByDescending(x => x.Normalized01 * 0.7 + ((x.Media.AverageScore ?? 75) / 100.0) * 0.3)
            .First();

        // 2. ÉQUILIBRE (Mid — autour de 45-65% d'affinité, différent du 1er)
        var midPick = scored
            .Where(x => x.Media.AniListId != closePick.Media.AniListId)
            .OrderBy(x => Math.Abs(x.Normalized01 - 0.54) + TraitOverlapPenalty(x.AllTraits, closePick.AllTraits) * 0.25)
            .FirstOrDefault() ?? scored.Skip(1).FirstOrDefault();

        // 3. EXPLORATION (Loin de tes habitudes mais excellente critique >= 73/100 !)
        var farCandidates = scored
            .Where(x =>
                x.Media.AniListId != closePick.Media.AniListId &&
                (midPick == null || x.Media.AniListId != midPick.Media.AniListId))
            .ToList();

        var highQualityFar = farCandidates.Where(x => (x.Media.AverageScore ?? 0) >= 74).ToList();
        var effectiveFarPool = highQualityFar.Count > 0 ? highQualityFar : farCandidates;

        var farPick = effectiveFarPool
            .OrderBy(x =>
                Math.Abs(x.Normalized01 - 0.16) +
                TraitOverlapPenalty(x.AllTraits, closePick.AllTraits) * 0.35 -
                ((x.Media.AverageScore ?? 75) / 500.0))
            .FirstOrDefault();

        var chosenList = new List<(string Tier, string Label, string Sub, ScoredCandidate Cand)>();
        chosenList.Add(("close", "Valeur Sûre", "Très proche de ce que tu aimes", closePick));
        if (midPick != null)
            chosenList.Add(("mid", "Terrain Hybride", "À mi-chemin entre tes goûts et du neuf", midPick));
        if (farPick != null)
            chosenList.Add(("far", "Hors des Sentiers", "Pour découvrir un tout autre style dans ce thème", farPick));

        // Enrichit les 3 animés en parallèle (Synopsis FR + Dispo Anime-Sama VOSTFR)
        var pickTasks = chosenList.Select(item => EnrichPickAsync(item.Tier, item.Label, item.Sub, item.Cand, hasPersonalTaste, themes));

        response.Picks = (await Task.WhenAll(pickTasks)).ToList();
        return response;
    }

    public async Task<DiscoveryPick?> ReplaceSingleTierAsync(DiscoveryReplaceRequest req)
    {
        var (tasteWeights, _, _) = BuildTasteProfile();
        var themes = (req.Themes ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (themes.Count == 0) themes.Add("Action");

        var rawPool = await _aniList.FetchPoolForThemesAsync(themes);
        var libEntries = _storage.GetAllEntries();
        var ownedAniListIds = libEntries.Where(e => e.AniListId.HasValue).Select(e => e.AniListId!.Value).ToHashSet();
        var ownedNormTitles = libEntries.Select(e => StorageService.NormalizeTitle(e.Title)).Where(s => s.Length > 0).ToHashSet();
        var excludedIds = (req.ExcludeAniListIds ?? new List<int>()).ToHashSet();
        var dismissedIds = _storage.GetDismissedIds();

        var candidates = rawPool
            .Where(m =>
                m.AniListId > 0 &&
                !ownedAniListIds.Contains(m.AniListId) &&
                !dismissedIds.Contains(m.AniListId) &&
                !excludedIds.Contains(m.AniListId) &&
                !ownedNormTitles.Contains(StorageService.NormalizeTitle(m.Title)) &&
                !m.HasPrequel &&
                !IsSideFormat(m.Format) &&
                !IsLikelySequelTitle(m.Title) &&
                !string.Equals(m.ReleaseStatus, "NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase) &&
                (m.AverageScore ?? 70) >= 65)
            .ToList();

        if (candidates.Count == 0) return null;

        bool hasPersonalTaste = tasteWeights.Values.Any(v => Math.Abs(v) > 0.4);
        var themeSet = new HashSet<string>(themes, StringComparer.OrdinalIgnoreCase);

        var scored = candidates.Select(c =>
        {
            var allTraits = c.GenresFr.Concat(c.TagsFr).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var secondaryTraits = allTraits.Where(t => !themeSet.Contains(t)).ToList();
            double rawAffinity = 0;
            var likedMatches = new List<string>();
            var freshTraits = new List<string>();

            if (hasPersonalTaste && secondaryTraits.Count > 0)
            {
                double sum = 0;
                foreach (var tr in secondaryTraits)
                {
                    if (tasteWeights.TryGetValue(tr, out double tw))
                    {
                        sum += tw;
                        if (tw >= 1.0) likedMatches.Add(tr);
                        else if (tw <= 0.1) freshTraits.Add(tr);
                    }
                    else freshTraits.Add(tr);
                }
                rawAffinity = sum / Math.Sqrt(Math.Max(2, secondaryTraits.Count));
            }
            else
            {
                rawAffinity = Math.Log10(Math.Max(100, c.Popularity ?? 1000));
                freshTraits = secondaryTraits.Take(3).ToList();
            }
            return new ScoredCandidate(c, rawAffinity, allTraits, likedMatches, freshTraits);
        }).OrderByDescending(x => x.RawAffinity).ToList();

        double maxAff = scored.Max(x => x.RawAffinity);
        double minAff = scored.Min(x => x.RawAffinity);
        double span = Math.Max(0.001, maxAff - minAff);
        foreach (var sc in scored) sc.Normalized01 = (sc.RawAffinity - minAff) / span;

        string tier = (req.Tier ?? "close").ToLowerInvariant();
        ScoredCandidate chosen;
        string label, sub;

        if (tier == "mid")
        {
            chosen = scored.OrderBy(x => Math.Abs(x.Normalized01 - 0.54)).First();
            label = "Terrain Hybride";
            sub = "À mi-chemin entre tes goûts et du neuf";
        }
        else if (tier == "far")
        {
            var hq = scored.Where(x => (x.Media.AverageScore ?? 0) >= 74).ToList();
            var pool = hq.Count > 0 ? hq : scored;
            chosen = pool.OrderBy(x => Math.Abs(x.Normalized01 - 0.16) - ((x.Media.AverageScore ?? 75) / 500.0)).First();
            label = "Hors des Sentiers";
            sub = "Pour découvrir un tout autre style dans ce thème";
        }
        else
        {
            tier = "close";
            chosen = scored.Take(Math.Max(1, scored.Count / 4))
                .OrderByDescending(x => x.Normalized01 * 0.7 + ((x.Media.AverageScore ?? 75) / 100.0) * 0.3)
                .First();
            label = "Valeur Sûre";
            sub = "Très proche de ce que tu aimes";
        }

        return await EnrichPickAsync(tier, label, sub, chosen, hasPersonalTaste, themes);
    }

    private async Task<DiscoveryPick> EnrichPickAsync(
        string tier,
        string label,
        string sub,
        ScoredCandidate cand,
        bool hasPersonalTaste,
        List<string> themes)
    {
        var m = cand.Media;
        var synTask = string.IsNullOrWhiteSpace(m.SynopsisFr) && !string.IsNullOrWhiteSpace(m.SynopsisEn)
            ? _aniList.EnsureFrenchSynopsisAsync(m.AniListId, m.SynopsisEn)
            : Task.FromResult(m.SynopsisFr);

        var asTask = _animeSama.AutoMatchAsync(m.Title, m.TitleRomaji, m.TitleEnglish, m.Synonyms, m.Format);

        await Task.WhenAll(synTask, asTask);

        var (asDetails, asSeason, epCount) = asTask.Result;
        m.SynopsisFr = asDetails != null && !string.IsNullOrWhiteSpace(asDetails.SynopsisFr)
            ? asDetails.SynopsisFr
            : synTask.Result;

        int affPct = tier switch
        {
            "close" => Math.Clamp((int)Math.Round(78 + cand.Normalized01 * 19), 80, 98),
            "mid" => Math.Clamp((int)Math.Round(46 + cand.Normalized01 * 22), 48, 70),
            _ => Math.Clamp((int)Math.Round(14 + cand.Normalized01 * 24), 14, 36)
        };

        return new DiscoveryPick
        {
            Tier = tier,
            TierLabel = label,
            TierSubtitle = sub,
            AffinityPercent = affPct,
            ReasonFr = BuildFrenchReason(tier, cand, hasPersonalTaste, themes),
            Media = m,
            AnimeSamaFound = asDetails != null && asSeason != null && epCount > 0,
            AvailableEpisodesVostfr = epCount,
            WatchUrl = asDetails != null && asSeason != null
                ? _animeSama.BuildWatchUrl(asDetails.Slug, asSeason.Path, 1)
                : ""
        };
    }

    private static string BuildFrenchReason(string tier, ScoredCandidate cand, bool hasTaste, List<string> themes)
    {
        var themeStr = string.Join(" + ", themes);
        if (!hasTaste)
        {
            return tier switch
            {
                "close" => $"Incontournable majeur dans le thème {themeStr} (note tes animés dans ta bibliothèque pour affiner encore plus !).",
                "mid" => $"Excellent équilibre critique ({(cand.Media.AverageScore ?? 78) / 10.0:0.0}/10) mêlant {string.Join(", ", cand.FreshTraits.Take(2))}.",
                _ => $"Pépite saluée par la critique avec une approche très singulière ({string.Join(", ", cand.FreshTraits.Take(2))})."
            };
        }

        return tier switch
        {
            "close" => cand.LikedMatches.Count > 0
                ? $"Reprend les codes de tes animés préférés : {string.Join(", ", cand.LikedMatches.Take(3))}."
                : $"Forte compatibilité globale avec le profil de ta bibliothèque.",
            "mid" => cand.LikedMatches.Count > 0 && cand.FreshTraits.Count > 0
                ? $"Combine ce que tu apprécies ({cand.LikedMatches[0]}) avec d'autres registres ({string.Join(", ", cand.FreshTraits.Take(2))})."
                : $"Un pont intéressant qui renouvelle le thème {themeStr} sans te dépayser.",
            _ => cand.FreshTraits.Count > 0
                ? $"Respecte le thème {themeStr}, mais t'emmène vers un registre neuf ({string.Join(", ", cand.FreshTraits.Take(3))}) avec {(cand.Media.AverageScore ?? 78) / 10.0:0.0}/10."
                : $"Très éloigné de tes habitudes habituelles, mais acclamé pour ses qualités."
        };
    }

    private static double TraitOverlapPenalty(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        int union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union > 0 ? (double)inter / union : 0;
    }

    private static bool IsSideFormat(string? format) =>
        string.Equals(format, "SPECIAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "OVA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "MUSIC", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelySequelTitle(string title)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            title,
            @"\b(?:season\s*[2-9]|saison\s*[2-9]|[2-9](?:nd|rd|th)\s+season|part\s*[2-9]|cour\s*[2-9])\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private sealed class ScoredCandidate
    {
        public CatalogMediaItem Media { get; }
        public double RawAffinity { get; }
        public double Normalized01 { get; set; }
        public List<string> AllTraits { get; }
        public List<string> LikedMatches { get; }
        public List<string> FreshTraits { get; }

        public ScoredCandidate(
            CatalogMediaItem media,
            double rawAffinity,
            List<string> allTraits,
            List<string> likedMatches,
            List<string> freshTraits)
        {
            Media = media;
            RawAffinity = rawAffinity;
            AllTraits = allTraits;
            LikedMatches = likedMatches;
            FreshTraits = freshTraits;
        }
    }
}
