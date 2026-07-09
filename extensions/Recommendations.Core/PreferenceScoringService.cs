using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;

namespace Recommendations.Core;

/// <summary>
/// Part 1 (engagement → preference). Turns a user's raw engagement signals (affinity counters +
/// ratings) into a normalized like-score in [-1,1] plus a confidence in [0,1], with a per-signal
/// explanation. Two fusion methods are implemented so we can A/B them in the inspector:
///   • "m1" — explicit weighted average of present signals (transparent, debuggable).
///   • "m2" — noisy-OR cumulative evidence (monotonic; naturally robust to missing positives).
/// Core principle: a missing signal is excluded from the aggregate (it never biases the score up or
/// down); it only lowers confidence.
/// </summary>
public sealed class PreferenceScoringService(IServiceScopeFactory scopeFactory, RecSettings settings) : IPreferenceScorer
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly RecSettings _settings = settings;
    private static readonly PreferenceWeights W = new();

    // Sub-entities that are not standalone recommendable items (they accrue watch time from their
    // parent video but should never appear as their own cards in a feed/inspector list).
    private static readonly HashSet<AffinityHostType> ExcludedHosts = [AffinityHostType.Segment, AffinityHostType.Face];

    private static double MedianConsumed(IEnumerable<UserEntityAffinity> affinities)
    {
        var values = affinities.Where(a => a.TotalConsumedSec > 0).Select(a => a.TotalConsumedSec).OrderBy(v => v).ToList();
        return values.Count == 0 ? W.ConsumedRefFallback : values[values.Count / 2];
    }

    /// <summary>Median watch percent (consumed/duration) across the entities that have both a watch time and a known duration.</summary>
    private static double MedianWatchPct(IEnumerable<UserEntityAffinity> affinities, Func<UserEntityAffinity, double?> duration)
    {
        var pcts = affinities
            .Select(a => { var d = duration(a); return a.TotalConsumedSec > 0 && d is > 0 ? a.TotalConsumedSec / d.Value : (double?)null; })
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .OrderBy(v => v)
            .ToList();
        return pcts.Count == 0 ? 0 : pcts[pcts.Count / 2];
    }

    /// <summary>Per-(host-type) watch-time normalization context for one entity.</summary>
    private readonly record struct WatchCtx(double? DurationSec, double MedianWatchPctValue, double MedianConsumedSec);

    public async Task<IReadOnlyDictionary<EntityRef, PreferenceScore>> ScoreAsync(
        int userId,
        IReadOnlyCollection<EntityRef> entities,
        PreferenceScoringOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PreferenceScoringOptions();
        var result = new Dictionary<EntityRef, PreferenceScore>();
        if (entities.Count == 0)
            return result;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<IUserEngagementReadService>();
        var dwellPositiveSec = await read.GetDwellPositiveSecAsync(userId, cancellationToken);
        var neutrals = await _settings.GetNeutralsAsync(userId, cancellationToken);

        foreach (var group in entities.GroupBy(e => e.EntityType.ToLowerInvariant()))
        {
            if (!EntityTypeMap.TryAffinity(group.Key, out var affinityHost))
            {
                foreach (var e in group)
                    result[e] = new PreferenceScore(0, 0);
                continue;
            }

            var ids = group.Select(e => e.EntityId).Distinct().ToList();
            var affinities = (await read.GetAffinitiesForEntitiesAsync(userId, affinityHost, ids, cancellationToken))
                .ToDictionary(a => a.HostId);

            // Watch-time normalization context for this host type: candidate durations + the user's
            // median watch percent (and median watch seconds as a no-duration fallback).
            var userAffinities = await read.GetAffinitiesForUserAsync(userId, affinityHost, take: 3000, cancellationToken: cancellationToken);
            var medianConsumed = MedianConsumed(userAffinities);
            var candidateDurations = await read.GetMediaDurationsAsync(affinityHost, ids, cancellationToken);
            var userDurations = await read.GetMediaDurationsAsync(affinityHost, userAffinities.Select(a => a.HostId).ToList(), cancellationToken);
            var medianWatchPct = MedianWatchPct(userAffinities, a => userDurations.TryGetValue(a.HostId, out var d) ? d : (double?)null);

            var ratingsByEntity = new Dictionary<int, int>();
            UserRatingStats stats = new(0, 0, 0, 0, 0);
            if (EntityTypeMap.TryRating(group.Key, out var ratingHost))
            {
                var ratings = await read.GetRatingsForEntitiesAsync(userId, ratingHost, ids, cancellationToken);
                foreach (var r in ratings.Where(r => r.Aspect == "overall"))
                    ratingsByEntity[r.HostId] = r.Value;
                stats = await read.GetRatingStatsAsync(userId, ratingHost, "overall", cancellationToken);
            }

            foreach (var e in group)
            {
                affinities.TryGetValue(e.EntityId, out var affinity);
                int? rating = ratingsByEntity.TryGetValue(e.EntityId, out var rv) ? rv : null;
                double? dur = candidateDurations.TryGetValue(e.EntityId, out var d) ? d : null;
                result[e] = Compute(affinity, rating, stats, new WatchCtx(dur, medianWatchPct, medianConsumed), dwellPositiveSec, options, neutrals.GetValueOrDefault(group.Key, RecSettings.DefaultNeutral));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ScoredEntity>> GetTopLikedAsync(
        int userId,
        string? entityType = null,
        int limit = 100,
        PreferenceScoringOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PreferenceScoringOptions();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<IUserEngagementReadService>();
        var dwellPositiveSec = await read.GetDwellPositiveSecAsync(userId, cancellationToken);
        var neutrals = await _settings.GetNeutralsAsync(userId, cancellationToken);
        double NeutralFor(AffinityHostType ht) => neutrals.GetValueOrDefault(EntityTypeMap.ToEntityType(ht), RecSettings.DefaultNeutral);

        AffinityHostType? affinityFilter = null;
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            if (!EntityTypeMap.TryAffinity(entityType, out var host))
                return [];
            affinityFilter = host;
        }

        // Pull a generous candidate pool of the user's engaged entities, then score + sort.
        var pool = Math.Clamp(limit * 8, 200, 4000);
        var affinities = await read.GetAffinitiesForUserAsync(userId, affinityFilter, take: pool, cancellationToken: cancellationToken);

        // Per-user, per-type median watch time for consumed-seconds normalization.
        var medianByType = affinities
            .Where(a => !ExcludedHosts.Contains(a.HostType))
            .GroupBy(a => a.HostType)
            .ToDictionary(g => g.Key, MedianConsumed);
        double RefFor(AffinityHostType ht) => medianByType.TryGetValue(ht, out var m) ? m : W.ConsumedRefFallback;

        // Durations for time-based entities in the pool → per-type median watch percent.
        var durationByEntity = new Dictionary<(AffinityHostType, int), double>();
        foreach (var ht in new[] { AffinityHostType.Video, AffinityHostType.Audio })
        {
            var idsForType = affinities.Where(a => a.HostType == ht).Select(a => a.HostId).ToList();
            if (idsForType.Count == 0)
                continue;
            foreach (var kv in await read.GetMediaDurationsAsync(ht, idsForType, cancellationToken))
                durationByEntity[(ht, kv.Key)] = kv.Value;
        }
        var medianWpByType = affinities
            .Where(a => !ExcludedHosts.Contains(a.HostType))
            .GroupBy(a => a.HostType)
            .ToDictionary(g => g.Key, g => MedianWatchPct(g, a => durationByEntity.TryGetValue((a.HostType, a.HostId), out var d) ? d : (double?)null));
        WatchCtx WatchFor(AffinityHostType ht, int id) => new(
            durationByEntity.TryGetValue((ht, id), out var d) ? d : (double?)null,
            medianWpByType.TryGetValue(ht, out var wp) ? wp : 0,
            RefFor(ht));

        // Per-rating-host stats cache (for z-scoring) and an overall-rating lookup keyed by (affinityHost, id).
        var statsCache = new Dictionary<AffinityHostType, UserRatingStats>();
        var ratingLookup = new Dictionary<(AffinityHostType, int), int>();
        var ratings = await read.GetRatingsForUserAsync(userId, cancellationToken: cancellationToken);
        foreach (var r in ratings.Where(r => r.Aspect == "overall"))
        {
            var ah = EntityTypeMap.RatingToAffinity(r.HostType);
            if (ah != 0)
                ratingLookup[(ah, r.HostId)] = r.Value;
        }

        async Task<UserRatingStats> StatsFor(AffinityHostType ah)
        {
            if (statsCache.TryGetValue(ah, out var s))
                return s;
            var et = EntityTypeMap.ToEntityType(ah);
            s = EntityTypeMap.TryRating(et, out var rh)
                ? await read.GetRatingStatsAsync(userId, rh, "overall", cancellationToken)
                : new UserRatingStats(0, 0, 0, 0, 0);
            statsCache[ah] = s;
            return s;
        }

        // Candidate set = affinities ∪ rated-but-no-affinity entities (respecting the type filter).
        var seen = new HashSet<(AffinityHostType, int)>();
        var scored = new List<ScoredEntity>();

        foreach (var a in affinities)
        {
            if (ExcludedHosts.Contains(a.HostType))
                continue;
            seen.Add((a.HostType, a.HostId));
            int? rating = ratingLookup.TryGetValue((a.HostType, a.HostId), out var rv) ? rv : null;
            var score = Compute(a, rating, await StatsFor(a.HostType), WatchFor(a.HostType, a.HostId), dwellPositiveSec, options, NeutralFor(a.HostType));
            scored.Add(new ScoredEntity(new EntityRef(EntityTypeMap.ToEntityType(a.HostType), a.HostId), score.Score, score.Confidence, score.Why));
        }

        foreach (var kv in ratingLookup)
        {
            var (ah, id) = kv.Key;
            if (ExcludedHosts.Contains(ah))
                continue;
            if (affinityFilter is { } f && ah != f)
                continue;
            if (!seen.Add((ah, id)))
                continue;
            var score = Compute(null, kv.Value, await StatsFor(ah), WatchFor(ah, id), dwellPositiveSec, options, NeutralFor(ah));
            scored.Add(new ScoredEntity(new EntityRef(EntityTypeMap.ToEntityType(ah), id), score.Score, score.Confidence, score.Why));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Confidence)
            .Take(limit)
            .ToList();
    }

    // ── Scoring core ─────────────────────────────────────────────────────────

    private static PreferenceScore Compute(UserEntityAffinity? a, int? overallRating, UserRatingStats stats, WatchCtx watch, double dwellPositiveSec, PreferenceScoringOptions options, double ratingNeutral)
    {
        var signals = new List<SignalEval>();

        if (overallRating is { } rating)
        {
            var (value, detail) = NormalizeRating(rating, ratingNeutral);
            // Bipolar: NormalizeRating returns [0,1] with 0.5 = the configured neutral. Map to a signed
            // spectrum so a rating above neutral pulls positive and one below pulls negative, proportional to
            // distance — never a hard like/dislike bucket. A rating at neutral contributes nothing.
            var bipolar = (value - 0.5) * 2.0;          // [-1, +1]
            var sign = bipolar >= 0 ? +1 : -1;
            // Presence/maturity grows with how many ratings the user has given (z-score reliability).
            var maturity = Math.Min(1.0, (stats.Count + 1) / (double)(W.MinRatingsForZScore + 1));
            signals.Add(new SignalEval("rating", "Rating", W.RatingWeight, W.RatingReliability, Math.Abs(bipolar), sign, maturity, detail));
        }

        if (a is not null)
        {
            if (a.IsFavorite)
                signals.Add(new SignalEval("favorite", "Favorited", W.FavoriteWeight, W.FavoriteReliability, 1.0, +1, 1.0, "favorited"));
            if (a.IsBookmarked)
                signals.Add(new SignalEval("bookmark", "Saved for later", W.BookmarkWeight, W.BookmarkReliability, 1.0, +1, 1.0, "saved for later"));
            if (a.LikeCount > 0)
                signals.Add(Count("like", "Likes", a.LikeCount, W.LikeWeight, W.LikeReliability, W.LikeRef, +1));
            if (a.DerivedLikeCount > 0)
                signals.Add(Count("derivedLike", "Implicit likes", a.DerivedLikeCount, W.DerivedLikeWeight, W.DerivedLikeReliability, W.DerivedLikeRef, +1));
            if (a.CompleteCount > 0)
                signals.Add(Count("complete", "Completed", a.CompleteCount, W.CompleteWeight, W.CompleteReliability, W.CompleteRef, +1));
            if (a.ViewCount > 0)
                signals.Add(Count("view", "Views", a.ViewCount, W.ViewWeight, W.ViewReliability, W.ViewRef, +1));
            if (a.TotalConsumedSec > 0)
            {
                double value;
                string detail;
                if (watch.DurationSec is > 0)
                {
                    // Watch percent normalized against the user's median watch percent (watch==median → 0.5).
                    var wp = a.TotalConsumedSec / watch.DurationSec.Value;   // can exceed 1.0 on rewatches
                    value = watch.MedianWatchPctValue > 0 ? wp / (wp + watch.MedianWatchPctValue) : Math.Min(1.0, wp);
                    detail = $"{wp * 100:0}% watched";
                }
                else
                {
                    // No known duration (e.g. images): fall back to seconds vs the user's median watch seconds.
                    var refSec = watch.MedianConsumedSec > 0 ? watch.MedianConsumedSec : W.ConsumedRefFallback;
                    value = a.TotalConsumedSec / (a.TotalConsumedSec + refSec);
                    detail = $"{a.TotalConsumedSec:0}s";
                }
                signals.Add(new SignalEval("watch", "Watch %", W.ConsumedWeight, W.ConsumedReliability, value, +1, value, detail));
            }

            // Dwell quality — a stronger read than raw watch %: did the user settle into a long contiguous
            // watch (positive: "found a part worth staying on"), or seek around a lot without ever staying
            // (negative: "sampled, not impressed")? Missing/short with few seeks contributes nothing.
            if (a.MaxDwellSec >= dwellPositiveSec)
            {
                var value = a.MaxDwellSec / (a.MaxDwellSec + W.DwellRefSec);
                signals.Add(new SignalEval("dwell", "Attention", W.DwellWeight, W.DwellReliability, value, +1, value, $"{a.MaxDwellSec:0}s longest watch"));
            }
            else if (a.SeekCount >= W.SkimMinSeeks)
            {
                // Skimming is only a WEAK negative: you may have been browsing, sampling, or just not in the
                // mood for that one — it doesn't mean you dislike the content.
                var value = a.SeekCount / (a.SeekCount + W.SkimRef);
                signals.Add(new SignalEval("dwell", "Skimmed", W.SkimWeight, W.SkimReliability, value, -1, value, $"{a.SeekCount} seeks, only {a.MaxDwellSec:0}s max watch"));
            }
        }

        if (signals.Count == 0)
            return new PreferenceScore(0, 0, options.IncludeExplanation ? new Explanation("No engagement signals", []) : null);

        var (score, factors) = options.Method.ToLowerInvariant() == "m2"
            ? FuseNoisyOr(signals)
            : FuseWeighted(signals);

        var confidence = Confidence(signals);
        Explanation? why = options.IncludeExplanation ? new Explanation(Summarize(score, factors), factors) : null;
        return new PreferenceScore(score, confidence, why);
    }

    private static SignalEval Count(string key, string label, int count, double weight, double reliability, double reference, double sign)
    {
        var value = LogScale(count, reference);
        return new SignalEval(key, label, weight, reliability, value, sign, value, $"{count}×");
    }

    private static double LogScale(double n, double reference) =>
        Math.Min(1.0, Math.Log(1 + Math.Max(0, n)) / Math.Log(1 + Math.Max(1.0, reference)));

    private static (double value, string detail) NormalizeRating(int rating, double neutral)
    {
        // ABSOLUTE mapping around a neutral point — NOT a z-score against the user's own average. The old
        // z-score made a merely-good rating land as a dislike (below your mean) and compressed everything toward
        // 0, so ratings barely moved the profile. Here rating == neutral → 0.5 (contributes nothing), scaling
        // to 1.0 at 100 and 0.0 at 0: a good rating reads as good and an explicit low rating reads as a dislike,
        // independent of how generously you rate everything else. The neutral is per-entity-type and
        // per-user-configurable (Recommendations settings); it defaults to the scale midpoint.
        double value = rating >= neutral
            ? 0.5 + 0.5 * (rating - neutral) / Math.Max(1e-9, 100 - neutral)
            : 0.5 * rating / Math.Max(1e-9, neutral);
        return (Math.Clamp(value, 0, 1), $"{rating}/100 (neutral {neutral:0})");
    }

    private static (double score, List<ExplanationFactor> factors) FuseWeighted(List<SignalEval> signals)
    {
        double posW = signals.Where(s => s.Sign > 0).Sum(s => s.Weight * s.Reliability);
        double negW = signals.Where(s => s.Sign < 0).Sum(s => s.Weight * s.Reliability);
        double pos = posW > 0 ? signals.Where(s => s.Sign > 0).Sum(s => s.Weight * s.Reliability * s.Value) / posW : 0;
        double neg = negW > 0 ? signals.Where(s => s.Sign < 0).Sum(s => s.Weight * s.Reliability * s.Value) / negW : 0;
        double score = Math.Clamp(pos - neg, -1, 1);

        var factors = signals
            .Select(s =>
            {
                var denom = s.Sign > 0 ? posW : negW;
                var contribution = denom > 0 ? s.Sign * (s.Weight * s.Reliability * s.Value) / denom : 0;
                return new ExplanationFactor(s.Key, s.Label, contribution, s.Detail);
            })
            .OrderByDescending(f => Math.Abs(f.Contribution))
            .ToList();
        return (score, factors);
    }

    private static (double score, List<ExplanationFactor> factors) FuseNoisyOr(List<SignalEval> signals)
    {
        double posProd = 1, negProd = 1;
        foreach (var s in signals)
        {
            var eff = s.Value * s.Reliability * Math.Min(1.0, s.Weight);
            if (s.Sign > 0) posProd *= 1 - eff;
            else negProd *= 1 - eff;
        }
        double pos = 1 - posProd, neg = 1 - negProd;
        double score = Math.Clamp(pos - neg, -1, 1);

        var factors = signals
            .Select(s =>
            {
                var eff = s.Value * s.Reliability * Math.Min(1.0, s.Weight);
                return new ExplanationFactor(s.Key, s.Label, s.Sign * eff, s.Detail);
            })
            .OrderByDescending(f => Math.Abs(f.Contribution))
            .ToList();
        return (score, factors);
    }

    private static double Confidence(List<SignalEval> signals)
    {
        double prod = 1;
        foreach (var s in signals)
            prod *= 1 - Math.Clamp(s.Reliability * s.Presence, 0, 1);
        return Math.Clamp(1 - prod, 0, 1);
    }

    private static string Summarize(double score, List<ExplanationFactor> factors)
    {
        var band = score switch
        {
            >= 0.6 => "Strong like",
            >= 0.25 => "Likes",
            > -0.25 => "Neutral / weak signal",
            > -0.6 => "Dislikes",
            _ => "Strong dislike",
        };
        var top = factors.Take(3).Select(f => f.Label.ToLowerInvariant()).ToList();
        return top.Count > 0 ? $"{band} — {string.Join(", ", top)}" : band;
    }

    private readonly record struct SignalEval(
        string Key, string Label, double Weight, double Reliability, double Value, double Sign, double Presence, string Detail);
}

/// <summary>Named, tunable weights/reliabilities for each engagement signal (no magic-number soup).</summary>
internal sealed record PreferenceWeights
{
    public double FavoriteWeight { get; init; } = 0.8;
    public double FavoriteReliability { get; init; } = 0.9;
    // "Save for later" — soft positive (interest at a glance, not consumption). Lower than a like/favorite.
    public double BookmarkWeight { get; init; } = 0.35;
    public double BookmarkReliability { get; init; } = 0.6;
    // Dwell quality — longest contiguous watch (positive) vs seek-heavy skimming (negative).
    public double DwellWeight { get; init; } = 0.5;
    public double DwellReliability { get; init; } = 0.6;
    public double DwellPositiveSec { get; init; } = 25;   // ≥ this contiguous = a real "settled in" dwell
    public double DwellRefSec { get; init; } = 180;       // saturation reference for the positive value
    public int SkimMinSeeks { get; init; } = 4;           // this many seeks + a tiny max-dwell = skimming
    public double SkimRef { get; init; } = 4;
    public double SkimWeight { get; init; } = 0.15;       // skimming is a WEAK negative (browsing ≠ dislike)
    public double SkimReliability { get; init; } = 0.4;
    public double RatingWeight { get; init; } = 1.0;
    public double RatingReliability { get; init; } = 1.0;
    public double LikeWeight { get; init; } = 0.7;
    public double LikeReliability { get; init; } = 0.85;
    public double LikeRef { get; init; } = 2;
    public double DerivedLikeWeight { get; init; } = 0.4;
    public double DerivedLikeReliability { get; init; } = 0.55;
    public double DerivedLikeRef { get; init; } = 3;
    public double CompleteWeight { get; init; } = 0.5;
    public double CompleteReliability { get; init; } = 0.6;
    public double CompleteRef { get; init; } = 3;
    public double ViewWeight { get; init; } = 0.3;
    public double ViewReliability { get; init; } = 0.3;
    public double ViewRef { get; init; } = 8;
    // Watch time (TotalConsumedSec) — often the only signal present on imported libraries. Normalized
    // per-user via a saturating function consumed/(consumed+ref), ref = the user's median watch time.
    public double ConsumedWeight { get; init; } = 0.5;
    public double ConsumedReliability { get; init; } = 0.5;
    public double ConsumedRefFallback { get; init; } = 120;
    public int MinRatingsForZScore { get; init; } = 4;
    // The absolute like/dislike boundary on the 0–100 rating scale (default = midpoint). A rating at this value
    // contributes nothing; above pulls positive, below pulls negative. Per-entity-type user overrides TODO.
    public double RatingNeutral { get; init; } = 50;
}
