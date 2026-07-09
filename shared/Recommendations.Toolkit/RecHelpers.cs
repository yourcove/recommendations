using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Recommendations.Abstractions;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Toolkit;

/// <summary>
/// Reusable "lego pieces" for building recommenders, shared across all recommender extensions: embedding /
/// metadata access, signed TF-IDF tag/studio affinity, performer affinity, name lookups, MMR diversity, and
/// the visual feature/semantic blend. Central improvements here (signed/negative affinities, IDF, the dwell
/// signal, …) flow to every recommender that uses them, with no duplication.
/// </summary>
public static class RecHelpers
{
    // Cove-owned visual kinds. feature.v1 is the PRIMARY visual signal; semantic.v1 is
    // a complementary signal blended in at a lower weight. Never default to semantic alone.
    public const string VisualFeatureFamily = EmbeddingKinds.VisualFeatureFamily;
    public const string VisualSemanticFamily = EmbeddingKinds.VisualSemanticFamily;

    // Height/BitRate are the denormalized objective-quality fields off the Video row (0 = unknown; images leave them 0).
    public sealed record VideoMeta(int Id, int[] TagIds, int[] PerformerIds, int? StudioId, int Height = 0, long BitRate = 0);

    /// <summary>Both asset-level visual vectors for a video. Either may be null if that kind wasn't produced.</summary>
    public sealed record VisualVec(float[]? Feature, float[]? Semantic);

    /// <summary>Feature-vs-semantic blend weights (feature-heavy by default).</summary>
    public static (double feature, double semantic) VisualWeights(IReadOnlyDictionary<string, double>? knobs)
        => (Knob(knobs, "featureWeight", 0.70), Knob(knobs, "semanticWeight", 0.30));

    /// <summary>Primary single-space vector (feature preferred, semantic fallback) — for clustering/diversity,
    /// which need one consistent space. Never mix the two spaces in a single k-means / cosine.</summary>
    public static float[]? Primary(VisualVec v) => v.Feature ?? v.Semantic;

    public static async Task<Dictionary<int, float[]>> GetAssetVectorsAsync(IEmbeddingRepository repo, IReadOnlyList<int> videoIds, EmbeddingModality modality, string? kindFamily, CancellationToken ct)
    {
        var result = new Dictionary<int, float[]>();
        if (videoIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter { HostType = EmbeddingHostType.Video, HostIds = videoIds, Modality = modality, KindFamily = kindFamily, SectionIndex = 0 }, ct);
        foreach (var e in rows.Where(e => e.SectionIndex == 0))
            result[e.HostId] = e.Vector.ToArray();
        return result;
    }

    /// <summary>Fetch BOTH visual spaces (feature + semantic) per video in one round-trip.</summary>
    public static async Task<Dictionary<int, VisualVec>> GetVisualVectorsAsync(IEmbeddingRepository repo, IReadOnlyList<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, VisualVec>();
        if (videoIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter { HostType = EmbeddingHostType.Video, HostIds = videoIds, Modality = EmbeddingModality.Visual, SectionIndex = 0 }, ct);
        var feat = new Dictionary<int, float[]>();
        var sem = new Dictionary<int, float[]>();
        foreach (var e in rows.Where(e => e.SectionIndex == 0))
        {
            if (e.KindFamily == VisualFeatureFamily) feat[e.HostId] = e.Vector.ToArray();
            else if (e.KindFamily == VisualSemanticFamily) sem[e.HostId] = e.Vector.ToArray();
        }
        foreach (var id in feat.Keys.Union(sem.Keys))
            result[id] = new VisualVec(feat.GetValueOrDefault(id), sem.GetValueOrDefault(id));
        return result;
    }

    /// <summary>Per-video SECTION feature embeddings (SectionIndex &gt; 0) with their media time ranges — the
    /// toolkit's asset-level fetches hardcode SectionIndex 0, so this is the only path to per-scene vectors.
    /// Used to build an attention-weighted "video as you watched it" representation.</summary>
    public static async Task<Dictionary<int, List<(float[] vec, double start, double end)>>> GetSectionFeatureVectorsAsync(DbContext db, IReadOnlyCollection<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<(float[], double, double)>>();
        if (videoIds.Count == 0) return result;
        var rows = await db.Set<Embedding>().AsNoTracking()
            .Where(e => e.HostType == EmbeddingHostType.Video && videoIds.Contains(e.HostId)
                        && e.Modality == EmbeddingModality.Visual && e.KindFamily == VisualFeatureFamily
                        && e.SectionIndex > 0 && e.StartSec != null && e.EndSec != null)
            .Select(e => new { e.HostId, Start = e.StartSec!.Value, End = e.EndSec!.Value, e.Vector })
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.HostId, out var l)) result[r.HostId] = l = [];
            l.Add((r.Vector.ToArray(), r.Start, r.End));
        }
        return result;
    }

    /// <summary>Represent a video AS THE USER WATCHED IT: the watch-time-weighted average of its section
    /// embeddings (dwelled sections dominate). With no watch data, uniform (section-duration) average; with no
    /// sections, the asset-level <paramref name="fallback"/>. Not L2-normalized (downstream uses cosine).</summary>
    public static float[]? AttentionWeightedVisual(IReadOnlyList<(float[] vec, double start, double end)>? sections, IReadOnlyList<(double start, double end)>? watched, float[]? fallback)
    {
        if (sections is null || sections.Count == 0) return fallback;
        float[]? acc = null; double totalW = 0;
        foreach (var (vec, s, e) in sections)
        {
            if (vec is null || vec.Length == 0) continue;
            double w;
            if (watched is { Count: > 0 })
            {
                double overlap = 0;
                foreach (var iv in watched) overlap += Math.Max(0, Math.Min(e, iv.end) - Math.Max(s, iv.start));
                w = overlap;
            }
            else w = Math.Max(0, e - s);
            if (w <= 0) continue;
            acc ??= new float[vec.Length];
            for (var d = 0; d < vec.Length && d < acc.Length; d++) acc[d] += (float)(w * vec[d]);
            totalW += w;
        }
        if (acc is null || totalW <= 0) return fallback;
        for (var d = 0; d < acc.Length; d++) acc[d] = (float)(acc[d] / totalW);
        return acc;
    }

    /// <summary>A single DISCRIMINATIVE "taste direction" in embedding space: the (score-weighted) mean of your
    /// LIKED reps minus the mean of your DISLIKED reps, L2-normalized. Projecting a candidate onto it (cosine)
    /// scores "closer to what you like than what you don't" — capturing the LOOK you like that no tag names, and
    /// using dislikes (which plain cosine-to-centroid ignores). Needs both positive and negative samples.</summary>
    public static float[]? LearnTasteDirection(IReadOnlyList<(float[]? vec, double score)> samples)
    {
        int dim = 0;
        foreach (var s in samples) if (s.vec is { Length: > 0 }) { dim = s.vec.Length; break; }
        if (dim == 0) return null;
        var pos = new double[dim]; var neg = new double[dim];
        double posW = 0, negW = 0;
        foreach (var (vec, score) in samples)
        {
            if (vec is null || vec.Length != dim || score == 0) continue;
            var w = Math.Abs(score);
            if (score > 0) { posW += w; for (var d = 0; d < dim; d++) pos[d] += w * vec[d]; }
            else { negW += w; for (var d = 0; d < dim; d++) neg[d] += w * vec[d]; }
        }
        if (posW <= 0 || negW <= 0) return null;
        var dir = new float[dim]; double norm = 0;
        for (var d = 0; d < dim; d++) { var v = pos[d] / posW - neg[d] / negW; dir[d] = (float)v; norm += v * v; }
        norm = Math.Sqrt(norm);
        if (norm < 1e-9) return null;
        for (var d = 0; d < dim; d++) dir[d] = (float)(dir[d] / norm);
        return dir;
    }

    public static Task<IReadOnlyList<EmbeddingSearchResult>> KnnVideosAsync(IEmbeddingService search, float[] query, int k, string kindFamily, CancellationToken ct)
        => search.KnnAsync(new Vector(query), Math.Min(1500, k),
            new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = kindFamily, SectionIndex = 0 }, ct);

    /// <summary>Candidate host ids from feature-KNN ∪ semantic-KNN (each centroid probes its own space, so
    /// semantic surfaces "related but not look-alike" neighbours feature alone would miss).</summary>
    public static async Task<List<int>> KnnUnionAsync(IEmbeddingService search, float[]? featQuery, float[]? semQuery, int k, ISet<int> exclude, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (featQuery is not null)
            foreach (var h in await KnnVideosAsync(search, featQuery, k, VisualFeatureFamily, ct))
                if (!exclude.Contains(h.Embedding.HostId)) ids.Add(h.Embedding.HostId);
        if (semQuery is not null)
            foreach (var h in await KnnVideosAsync(search, semQuery, k, VisualSemanticFamily, ct))
                if (!exclude.Contains(h.Embedding.HostId)) ids.Add(h.Embedding.HostId);
        return ids.ToList();
    }

    /// <summary>Visual-KNN union that KEEPS the vectors the KNN already returned (feature→Feature,
    /// semantic→Semantic) so the caller scores candidates without a second embedding fetch. A candidate found
    /// in only one space keeps just that space; <see cref="Vectors.BlendedCosine"/> tolerates the null.</summary>
    public static async Task<Dictionary<int, VisualVec>> KnnUnionVecsAsync(IEmbeddingService search, float[]? featQuery, float[]? semQuery, int k, ISet<int> exclude, CancellationToken ct)
    {
        var feat = new Dictionary<int, float[]>();
        var sem = new Dictionary<int, float[]>();
        if (featQuery is not null)
            foreach (var h in await KnnVideosAsync(search, featQuery, k, VisualFeatureFamily, ct))
                if (!exclude.Contains(h.Embedding.HostId)) feat[h.Embedding.HostId] = h.Embedding.Vector.ToArray();
        if (semQuery is not null)
            foreach (var h in await KnnVideosAsync(search, semQuery, k, VisualSemanticFamily, ct))
                if (!exclude.Contains(h.Embedding.HostId)) sem[h.Embedding.HostId] = h.Embedding.Vector.ToArray();
        var result = new Dictionary<int, VisualVec>();
        foreach (var id in feat.Keys.Union(sem.Keys))
            result[id] = new VisualVec(feat.GetValueOrDefault(id), sem.GetValueOrDefault(id));
        return result;
    }

    public static async Task<Dictionary<int, VideoMeta>> FetchVideoMetaAsync(DbContext db, IReadOnlyCollection<int> ids, CancellationToken ct, bool includeAiTags = true, double minCoverage = 0.05)
    {
        if (ids.Count == 0) return new();
        var rows = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters().Where(v => ids.Contains(v.Id))
            .Select(v => new VideoMeta(v.Id, v.TagIds, v.PerformerIds, v.StudioId, v.MaxHeight, v.MaxBitRate)).ToListAsync(ct);
        var result = rows.ToDictionary(m => m.Id);

        // Video.TagIds is MANUAL tags only — merge in AI-applied tags (TagApplication) above a coverage floor so
        // EVERY recommender's attribution/profile sees them, not just the ones that enrich meta themselves.
        // (Binary presence here; callers that need coverage weights use FetchVideoEffectiveTagsAsync.)
        if (includeAiTags)
        {
            var ai = await db.Set<TagApplication>().AsNoTracking()
                .Where(ta => ta.HostType == AffinityHostType.Video && ids.Contains(ta.HostId) && ta.ContextType == null && ta.ContextId == null
                             && ta.TotalDurationSec != null && ta.HostDurationSec != null && ta.HostDurationSec > 0)
                .GroupBy(ta => new { ta.HostId, ta.TagId })
                .Select(g => new { g.Key.HostId, g.Key.TagId, Total = g.Max(x => x.TotalDurationSec), Host = g.Max(x => x.HostDurationSec) })
                .ToListAsync(ct);
            var extra = new Dictionary<int, HashSet<int>>();
            foreach (var r in ai)
            {
                if (r.Host is not > 0 || r.Total is not { } t || t / r.Host.Value < minCoverage) continue;
                if (!extra.TryGetValue(r.HostId, out var set)) extra[r.HostId] = set = [];
                set.Add(r.TagId);
            }
            foreach (var (id, aiTags) in extra)
                if (result.TryGetValue(id, out var m))
                    result[id] = m with { TagIds = m.TagIds.Concat(aiTags).Distinct().ToArray() };
        }
        return result;
    }

    /// <summary>Per-video EFFECTIVE tag weights: MANUAL tags (Video.TagIds, whole-video → weight 1.0) UNIONed
    /// with AI-applied tags (TagApplication, weight = coverage fraction = TotalDurationSec/HostDurationSec).
    /// Video.TagIds only holds MANUAL tags, so without this the recommenders never see AI tags at all. AI tags
    /// below <paramref name="minCoverage"/> are dropped as noise; a tag present both ways keeps the larger
    /// weight. Use the KEYS as the enriched tag set for attribution, and the WEIGHTS for coverage-aware
    /// clustering signatures.</summary>
    public static async Task<Dictionary<int, Dictionary<int, double>>> FetchVideoEffectiveTagsAsync(DbContext db, IReadOnlyCollection<int> videoIds, double minCoverage, CancellationToken ct, double aiWeight = 1.0)
    {
        var result = new Dictionary<int, Dictionary<int, double>>();
        if (videoIds.Count == 0) return result;

        var manual = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters().Where(v => videoIds.Contains(v.Id))
            .Select(v => new { v.Id, v.TagIds }).ToListAsync(ct);
        foreach (var v in manual)
        {
            var m = result[v.Id] = new Dictionary<int, double>(v.TagIds.Length);
            foreach (var t in v.TagIds) m[t] = 1.0;
        }

        var ai = await db.Set<TagApplication>().AsNoTracking()
            .Where(ta => ta.HostType == AffinityHostType.Video && videoIds.Contains(ta.HostId)
                         && ta.ContextType == null && ta.ContextId == null
                         && ta.TotalDurationSec != null && ta.HostDurationSec != null && ta.HostDurationSec > 0)
            .GroupBy(ta => new { ta.HostId, ta.TagId })
            .Select(g => new { g.Key.HostId, g.Key.TagId, Total = g.Max(ta => ta.TotalDurationSec), Host = g.Max(ta => ta.HostDurationSec) })
            .ToListAsync(ct);
        foreach (var r in ai)
        {
            if (r.Host is not > 0 || r.Total is not { } total) continue;
            var cover = Math.Clamp(total / r.Host.Value, 0, 1);
            if (cover < minCoverage) continue;
            if (!result.TryGetValue(r.HostId, out var m)) result[r.HostId] = m = new Dictionary<int, double>();
            // aiWeight (<1) keeps AI tags from over-weighting user-curated manual tags (which are always 1.0).
            m[r.TagId] = Math.Max(m.GetValueOrDefault(r.TagId), cover * aiWeight);
        }
        return result;
    }

    /// <summary>Faces present on each video with their on-screen duration (FaceAppearance) — "who is in this
    /// video, and for how long". The raw material for face-taste modelling.</summary>
    public static async Task<Dictionary<int, List<(int faceId, double durationSec)>>> FetchVideoFaceAppearancesAsync(DbContext db, IReadOnlyCollection<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<(int, double)>>();
        if (videoIds.Count == 0) return result;
        var rows = await db.Set<FaceAppearance>().AsNoTracking()
            .Where(a => a.HostType == FaceAppearanceHostType.Video && videoIds.Contains(a.HostId) && a.FirstSeenAtSec != null && a.LastSeenAtSec != null)
            .Select(a => new { a.HostId, a.FaceId, Dur = a.LastSeenAtSec!.Value - a.FirstSeenAtSec!.Value })
            .ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => new { r.HostId, r.FaceId }))
        {
            if (!result.TryGetValue(g.Key.HostId, out var l)) result[g.Key.HostId] = l = [];
            l.Add((g.Key.FaceId, g.Sum(x => Math.Max(0, x.Dur))));
        }
        return result;
    }

    /// <summary>Face ARCFace embeddings (Modality=Face, HostType=Face) for a set of face ids.</summary>
    public static async Task<Dictionary<int, float[]>> GetFaceEmbeddingsAsync(IEmbeddingRepository repo, IReadOnlyList<int> faceIds, CancellationToken ct)
    {
        var result = new Dictionary<int, float[]>();
        if (faceIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter { HostType = EmbeddingHostType.Face, HostIds = faceIds, Modality = EmbeddingModality.Face }, ct);
        foreach (var e in rows)
            if (!result.ContainsKey(e.HostId)) result[e.HostId] = e.Vector.ToArray();
        return result;
    }

    /// <summary>Faces LINKED to each of the given performers (Face.PerformerId), excluding ignored/merged
    /// tombstones — so a liked performer contributes their face(s) even to videos where they're not detected.</summary>
    public static async Task<Dictionary<int, List<int>>> FetchPerformerFacesAsync(DbContext db, IReadOnlyCollection<int> performerIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();
        if (performerIds.Count == 0) return result;
        var rows = await db.Set<Face>().AsNoTracking()
            .Where(f => f.PerformerId != null && performerIds.Contains(f.PerformerId.Value) && !f.Ignored && f.MergedIntoFaceId == null)
            .Select(f => new { PerformerId = f.PerformerId!.Value, f.Id }).ToListAsync(ct);
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.PerformerId, out var l)) result[r.PerformerId] = l = [];
            l.Add(r.Id);
        }
        return result;
    }

    /// <summary>Of the given faceIds, the subset that are UNLINKED identities — a real detected face (not ignored,
    /// not a merge tombstone) that isn't attached to any performer. These are pseudo-performer candidates.</summary>
    public static async Task<HashSet<int>> FetchUnlinkedFaceIdsAsync(DbContext db, IReadOnlyCollection<int> faceIds, CancellationToken ct)
    {
        if (faceIds.Count == 0) return new();
        return (await db.Set<Face>().AsNoTracking()
            .Where(f => faceIds.Contains(f.Id) && f.PerformerId == null && !f.Ignored && f.MergedIntoFaceId == null)
            .Select(f => f.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>How many distinct VIDEOS each face appears in (library-wide) — the IDF/rarity count for treating a
    /// recurring unlinked face as a pseudo-performer.</summary>
    public static async Task<Dictionary<int, int>> FaceVideoCountsAsync(DbContext db, IReadOnlyCollection<int> faceIds, CancellationToken ct)
    {
        if (faceIds.Count == 0) return new();
        var rows = await db.Set<FaceAppearance>().AsNoTracking()
            .Where(a => a.HostType == FaceAppearanceHostType.Video && faceIds.Contains(a.FaceId))
            .Select(a => new { a.FaceId, a.HostId }).Distinct().ToListAsync(ct);
        return rows.GroupBy(r => r.FaceId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>The user's explicit performer FACE-aspect ratings (Rating HostType=Performer, Aspect="face"),
    /// the strongest direct signal for face preference, mapped around <paramref name="neutral"/> to [-1,1].</summary>
    public static async Task<Dictionary<int, double>> FetchPerformerFaceRatingsAsync(DbContext db, int userId, IReadOnlyCollection<int> performerIds, double neutral, CancellationToken ct)
    {
        if (performerIds.Count == 0) return new();
        var rows = await db.Set<Rating>().AsNoTracking()
            .Where(r => r.UserId == userId && r.HostType == RatingHostType.Performer && r.Aspect == "face" && performerIds.Contains(r.HostId))
            .Select(r => new { r.HostId, r.Value, r.UpdatedAt }).ToListAsync(ct);
        // Dedupe duplicate (user,performer,face) rows — most recent wins.
        return rows.GroupBy(r => r.HostId)
            .ToDictionary(g => g.Key, g => MapRating(g.OrderByDescending(x => x.UpdatedAt).First().Value, neutral));
    }

    /// <summary>The user's explicit per-video ASPECT ratings for one aspect (Rating HostType=Video, Aspect=name,
    /// e.g. "audio" or "video_quality") — the strongest direct signal for that facet — mapped around
    /// <paramref name="neutral"/> to [-1,1].</summary>
    public static async Task<Dictionary<int, double>> FetchVideoAspectRatingsAsync(DbContext db, int userId, IReadOnlyCollection<int> videoIds, string aspect, double neutral, CancellationToken ct)
    {
        if (videoIds.Count == 0) return new();
        var rows = await db.Set<Rating>().AsNoTracking()
            .Where(r => r.UserId == userId && r.HostType == RatingHostType.Video && r.Aspect == aspect && videoIds.Contains(r.HostId))
            .Select(r => new { r.HostId, r.Value, r.UpdatedAt }).ToListAsync(ct);
        // The Rating table can hold duplicate (user,host,aspect) rows — take the most recent per host, don't crash.
        return rows.GroupBy(r => r.HostId)
            .ToDictionary(g => g.Key, g => MapRating(g.OrderByDescending(x => x.UpdatedAt).First().Value, neutral));
    }

    private static double MapRating(double value, double neutral)
        => Math.Clamp((value - neutral) / Math.Max(1e-9, value >= neutral ? 100 - neutral : neutral), -1, 1);

    /// <summary>Signed fit of a vector to LIKED vs DISLIKED centroid sets, PLUS a confidence: how close it is to
    /// ANY known centroid (low ⇒ novel/unfamiliar ⇒ we can't really judge it). fit = bestLikedCos − ½·bestDislikedCos
    /// (both cosines clamped to [0,1]); confidence = max(bestLikedCos, bestDislikedCos). (0,0) when unscorable.</summary>
    public static (double fit, double confidence) CentroidFit(float[]? vec, IReadOnlyList<float[]> liked, IReadOnlyList<float[]> disliked)
    {
        if (vec is null || (liked.Count == 0 && disliked.Count == 0)) return (0, 0);
        double bl = 0, bd = 0;
        foreach (var c in liked) bl = Math.Max(bl, Math.Clamp(Vectors.Cosine(vec, c), 0, 1));
        foreach (var c in disliked) bd = Math.Max(bd, Math.Clamp(Vectors.Cosine(vec, c), 0, 1));
        return (bl - 0.5 * bd, Math.Max(bl, bd));
    }

    /// <summary>Which (videoId, aspect) pairs already have an EXPLICIT rating — so an active-learning picker can
    /// skip aspects it's already been told about and probe only the still-derived ones.</summary>
    public static async Task<HashSet<(int, string)>> FetchRatedAspectsAsync(DbContext db, int userId, IReadOnlyCollection<int> hostIds, IReadOnlyCollection<string> aspects, CancellationToken ct, RatingHostType hostType = RatingHostType.Video)
    {
        if (hostIds.Count == 0) return new();
        var rows = await db.Set<Rating>().AsNoTracking()
            .Where(r => r.UserId == userId && r.HostType == hostType && r.Aspect != null && hostIds.Contains(r.HostId) && aspects.Contains(r.Aspect))
            .Select(r => new { r.HostId, r.Aspect }).ToListAsync(ct);
        return rows.Select(r => (r.HostId, r.Aspect!)).ToHashSet();
    }

    /// <summary>How much a video's faces match your LIKED faces: the best (max over the video's faces) cosine to
    /// a liked-face centroid, minus a fraction of the best match to a DISLIKED centroid. In [-1,1]. Used BOTH as
    /// an attribution covariate (de-confound "you'd like the content if not for the face") and as a scoring
    /// term. Returns 0 when the video has no known faces or there's no liked-face model.</summary>
    public static double FaceFit(IReadOnlyList<(int faceId, double durationSec)>? faces, IReadOnlyDictionary<int, float[]> faceEmb, IReadOnlyList<float[]> likedCentroids, IReadOnlyList<float[]> dislikedCentroids, double dislikePenalty = 0.5)
    {
        if (faces is null || faces.Count == 0 || likedCentroids.Count == 0) return 0;
        // PROMINENCE-aware: a fleeting background face shouldn't read like the main performer. Ignore faces on
        // screen for < 20% as long as the most-prominent one (background noise that otherwise inflates every video
        // toward "typical"), then take the best fit among the remaining PROMINENT faces. Images (no duration) keep
        // all faces. Uses the on-screen time we already have — no new data required.
        double maxDur = 0;
        foreach (var (fid, dur) in faces) if (faceEmb.ContainsKey(fid)) maxDur = Math.Max(maxDur, dur);
        var durThresh = maxDur * 0.2;
        var best = double.NegativeInfinity;
        foreach (var (fid, dur) in faces)
        {
            if (!faceEmb.TryGetValue(fid, out var v) || dur < durThresh) continue;
            double liked = 0; foreach (var c in likedCentroids) { var s = Vectors.Cosine(v, c); if (s > liked) liked = s; }
            double disliked = 0; foreach (var c in dislikedCentroids) { var s = Vectors.Cosine(v, c); if (s > disliked) disliked = s; }
            var val = liked - dislikePenalty * disliked;
            if (val > best) best = val;
        }
        return best == double.NegativeInfinity ? 0 : Math.Clamp(best, -1, 1);
    }

    /// <summary>A user's WATCHED time ranges per video (from PlaybackInterval) — the raw material for
    /// attention-localized attribution ("what was on screen where they actually watched").</summary>
    public static async Task<Dictionary<int, List<(double start, double end)>>> FetchWatchedIntervalsAsync(DbContext db, int userId, IReadOnlyCollection<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<(double, double)>>();
        if (videoIds.Count == 0) return result;
        var rows = await db.Set<PlaybackInterval>().AsNoTracking()
            .Where(pi => pi.UserId == userId && pi.HostType == InteractionHostType.Video && videoIds.Contains(pi.HostId) && pi.EndSec > pi.StartSec)
            .Select(pi => new { pi.HostId, pi.StartSec, pi.EndSec }).ToListAsync(ct);
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.HostId, out var l)) result[r.HostId] = l = [];
            l.Add((r.StartSec, r.EndSec));
        }
        return result;
    }

    /// <summary>Timed tag segments per video (a tag present over [StartSec,EndSec]) — the timed source that
    /// TagApplication (aggregate) isn't. Intersect with watched ranges to weight tags by attention.</summary>
    public static async Task<Dictionary<int, List<(int tagId, double start, double end)>>> FetchTimedTagSegmentsAsync(DbContext db, IReadOnlyCollection<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<(int, double, double)>>();
        if (videoIds.Count == 0) return result;
        var rows = await db.Set<Segment>().AsNoTracking()
            .Where(s => s.HostType == SegmentHostType.Video && videoIds.Contains(s.HostId) && s.TagId != null && s.EndSec != null && s.EndSec > s.StartSec)
            .Select(s => new { s.HostId, TagId = s.TagId!.Value, s.StartSec, EndSec = s.EndSec!.Value }).ToListAsync(ct);
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.HostId, out var l)) result[r.HostId] = l = [];
            l.Add((r.TagId, r.StartSec, r.EndSec));
        }
        return result;
    }

    /// <summary>Attention-weighted tag presence for ONE video: for each timed tag, the fraction of the user's
    /// WATCHED time that tag was on screen. A tag you dwelled on scores high; a tag only in a part you skipped
    /// scores ~0 — far sharper credit assignment than "this tag is somewhere in the video".</summary>
    public static Dictionary<int, double> AttentionWeightedTags(IReadOnlyList<(double start, double end)> watched, IReadOnlyList<(int tagId, double start, double end)> segments)
    {
        var result = new Dictionary<int, double>();
        double totalWatched = 0; foreach (var w in watched) totalWatched += Math.Max(0, w.end - w.start);
        if (totalWatched <= 0 || segments.Count == 0) return result;
        foreach (var g in segments.GroupBy(s => s.tagId))
        {
            double overlap = 0;
            foreach (var seg in g)
                foreach (var w in watched)
                    overlap += Math.Max(0, Math.Min(seg.end, w.end) - Math.Max(seg.start, w.start));
            if (overlap > 0) result[g.Key] = Math.Clamp(overlap / totalWatched, 0, 1);
        }
        return result;
    }

    /// <summary>Per-(video,tag) attention MULTIPLIERS for the attribution: a moderate factor in
    /// [1−strength, 1] scaling how much a liked video credits each TIMED tag, by how much of the WATCHED time
    /// that tag was on screen — a tag mostly in a part you skipped credits less (never zero); one you watched
    /// credits fully. Only timed tags with watch data get a factor; everything else is absent → full 1.0.
    /// <paramref name="strength"/> (~0.4) keeps it a nudge on WHAT YOU LIKED, not a takeover — and it lives in
    /// the preference model, deliberately NOT in the cluster geometry.</summary>
    public static async Task<Dictionary<int, IReadOnlyDictionary<int, double>>> BuildAttentionTagCellsAsync(DbContext db, int userId, IReadOnlyCollection<int> videoIds, double strength, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<int, double>>();
        if (videoIds.Count == 0 || strength <= 0) return result;
        var watched = await FetchWatchedIntervalsAsync(db, userId, videoIds, ct);
        var segs = await FetchTimedTagSegmentsAsync(db, videoIds, ct);
        foreach (var (vid, s) in segs)
        {
            if (!watched.TryGetValue(vid, out var w)) continue;
            var att = AttentionWeightedTags(w, s);
            var cells = new Dictionary<int, double>();
            foreach (var t in s.Select(x => x.tagId).Distinct())
                cells[t] = 1 - strength * (1 - att.GetValueOrDefault(t)); // watched→1.0, skipped→1−strength
            if (cells.Count > 0) result[vid] = cells;
        }
        return result;
    }

    /// <summary>Unseen video ids that carry ANY of the given tags — MANUAL (VideoTag join) or AI-applied
    /// (TagApplication). Lets a tag-defined niche pull candidates that actually match its content, which a
    /// visual-only KNN off the niche centroid would miss when the niche's look isn't distinctive.</summary>
    public static async Task<List<int>> VideosByTagsAsync(DbContext db, IReadOnlyCollection<int> tagIds, ISet<int> exclude, int take, CancellationToken ct)
    {
        if (tagIds.Count == 0 || take <= 0) return [];
        var tagArr = tagIds.ToArray();
        var manual = await db.Set<VideoTag>().AsNoTracking()
            .Where(vt => tagArr.Contains(vt.TagId)).Select(vt => vt.VideoId).Distinct().Take(take * 4).ToListAsync(ct);
        var ai = await db.Set<TagApplication>().AsNoTracking()
            .Where(ta => ta.HostType == AffinityHostType.Video && tagArr.Contains(ta.TagId)).Select(ta => ta.HostId).Distinct().Take(take * 4).ToListAsync(ct);
        return manual.Concat(ai).Distinct().Where(id => !exclude.Contains(id)).Take(take).ToList();
    }

    /// <summary>Image tag/performer/studio metadata in the same shape as <see cref="VideoMeta"/> — images carry
    /// the same vocabulary, so they slot straight into attribution and probe selection.</summary>
    public static async Task<Dictionary<int, VideoMeta>> FetchImageMetaAsync(DbContext db, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var rows = await db.Set<Image>().AsNoTracking().IgnoreQueryFilters().Where(i => ids.Contains(i.Id))
            .Select(i => new VideoMeta(i.Id, i.TagIds, i.PerformerIds, i.StudioId)).ToListAsync(ct);
        return rows.ToDictionary(m => m.Id);
    }

    /// <summary>Both visual spaces (feature + semantic) per IMAGE id — same space as video frames.</summary>
    public static async Task<Dictionary<int, VisualVec>> GetImageVisualVectorsAsync(IEmbeddingRepository repo, IReadOnlyList<int> imageIds, CancellationToken ct)
    {
        var result = new Dictionary<int, VisualVec>();
        if (imageIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter { HostType = EmbeddingHostType.Image, HostIds = imageIds, Modality = EmbeddingModality.Visual, SectionIndex = 0 }, ct);
        var feat = new Dictionary<int, float[]>(); var sem = new Dictionary<int, float[]>();
        foreach (var e in rows.Where(e => e.SectionIndex == 0))
        {
            if (e.KindFamily == VisualFeatureFamily) feat[e.HostId] = e.Vector.ToArray();
            else if (e.KindFamily == VisualSemanticFamily) sem[e.HostId] = e.Vector.ToArray();
        }
        foreach (var id in feat.Keys.Union(sem.Keys))
            result[id] = new VisualVec(feat.GetValueOrDefault(id), sem.GetValueOrDefault(id));
        return result;
    }

    /// <summary>Visual-KNN over IMAGES, union of both spaces.</summary>
    public static async Task<List<int>> KnnImageUnionAsync(IEmbeddingService search, float[]? featQuery, float[]? semQuery, int k, ISet<int> exclude, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        async Task ProbeAsync(float[]? q, string family)
        {
            if (q is null) return;
            foreach (var h in await search.KnnAsync(new Vector(q), Math.Min(1500, k),
                new EmbeddingSearchOptions { HostType = EmbeddingHostType.Image, Modality = EmbeddingModality.Visual, KindFamily = family, SectionIndex = 0 }, ct))
                if (!exclude.Contains(h.Embedding.HostId)) ids.Add(h.Embedding.HostId);
        }
        await ProbeAsync(featQuery, VisualFeatureFamily);
        await ProbeAsync(semQuery, VisualSemanticFamily);
        return ids.ToList();
    }

    /// <summary>Image ids are offset by this when mixed into the video id-space (attribution rows) so the two
    /// id spaces can't collide. Attribute ids (tags/performers/studios) are shared and stay as-is.</summary>
    public const int ImageIdOffset = 1_000_000_000;

    /// <summary>Append the user's engaged IMAGES to a video-attribution input set (offset ids, shared
    /// attribute vocabulary). This is how rating images teaches the SAME taste model that drives video recs —
    /// a single frame is an unambiguous like/dislike, so it's a fast way to pin tag/performer preferences.</summary>
    public static async Task<(List<(int id, double score)> Scored, Dictionary<int, VideoMeta> Meta)> MergeEngagedImagesAsync(
        ICoreServices core, DbContext db, int userId,
        IEnumerable<(int id, double score)> videoScored, IReadOnlyDictionary<int, VideoMeta> videoMeta, CancellationToken ct,
        IReadOnlyList<ScoredEntity>? engagedImages = null)
    {
        var scored = videoScored.ToList();
        var meta = new Dictionary<int, VideoMeta>(videoMeta);
        var imgs = (engagedImages ?? await core.Preference.GetTopLikedAsync(userId, "image", 1500, null, ct)).Where(e => Math.Abs(e.Score) > 0.1).ToList();
        if (imgs.Count == 0) return (scored, meta);
        var imgMeta = await FetchImageMetaAsync(db, imgs.Select(e => e.Entity.EntityId).ToList(), ct);
        foreach (var e in imgs)
        {
            if (!imgMeta.TryGetValue(e.Entity.EntityId, out var m)) continue;
            var key = e.Entity.EntityId + ImageIdOffset;
            scored.Add((key, e.Score));
            meta[key] = m;
        }
        return (scored, meta);
    }

    public static async Task<int> TotalVideosAsync(DbContext db, CancellationToken ct)
        => Math.Max(1, await db.Set<Video>().IgnoreQueryFilters().CountAsync(ct));

    public static async Task<Dictionary<int, int>> TagVideoCountsAsync(DbContext db, IReadOnlyCollection<int> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return new();
        return (await db.Set<Tag>().AsNoTracking().IgnoreQueryFilters().Where(t => tagIds.Contains(t.Id))
            .Select(t => new { t.Id, t.VideoCount }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.VideoCount);
    }

    public static async Task<Dictionary<int, string>> TagNamesAsync(DbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return (await db.Set<Tag>().AsNoTracking().IgnoreQueryFilters().Where(t => list.Contains(t.Id))
            .Select(t => new { t.Id, t.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
    }

    public static async Task<Dictionary<int, string>> PerformerNamesAsync(DbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return (await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters().Where(p => list.Contains(p.Id))
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
    }

    /// <summary>Taste-relevant performer attributes for the attribute cold-start model (enums stringified,
    /// performer-tag ids included). Free-text fields are returned raw; the caller normalizes/buckets them.</summary>
    public sealed record PerfAttrs(int Id, string? Gender, string? Ethnicity, string? Country, string? HairColor,
        string? EyeColor, int? HeightCm, string? FakeTits, string? Circumcised, double? PenisLength, int[] TagIds);

    public static async Task<Dictionary<int, PerfAttrs>> FetchPerformerAttributesAsync(DbContext db, IReadOnlyCollection<int> performerIds, CancellationToken ct)
    {
        if (performerIds.Count == 0) return new();
        var rows = await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => performerIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id, p.Gender, p.Ethnicity, p.Country, p.HairColor, p.EyeColor, p.HeightCm,
                p.FakeTits, p.Circumcised, p.PenisLength,
                TagIds = p.PerformerTags.Select(t => t.TagId).ToArray(),
            }).ToListAsync(ct);
        return rows.ToDictionary(p => p.Id, p => new PerfAttrs(p.Id,
            p.Gender?.ToString(), p.Ethnicity, p.Country, p.HairColor, p.EyeColor, p.HeightCm,
            p.FakeTits, p.Circumcised?.ToString(), p.PenisLength, p.TagIds));
    }

    /// <summary>Every performer that appears on at least one video or image — the universe the attribute prior
    /// must cover so an unrated performer on a candidate still gets a read.</summary>
    public static async Task<List<int>> AllContentPerformerIdsAsync(DbContext db, CancellationToken ct)
        => await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.VideoCount > 0 || p.ImageCount > 0).Select(p => p.Id).ToListAsync(ct);

    // ── Universe helpers (video + image): the ranked candidate pool and the strided calibration sample. Shared so
    // any recommender can pull a whole-library universe or a representative reference the same way. ──────────────
    public static async Task<List<int>> AllVideoIdsAsync(DbContext db, int cap, CancellationToken ct)
        => await db.Set<Video>().AsNoTracking().OrderBy(v => v.Id).Select(v => v.Id).Take(cap).ToListAsync(ct);

    /// <summary>A representative video sample (strided across all ids) for building calibration references.</summary>
    public static async Task<List<int>> SampleVideoIdsAsync(DbContext db, int cap, CancellationToken ct)
    {
        var all = await db.Set<Video>().AsNoTracking().OrderBy(v => v.Id).Select(v => v.Id).ToListAsync(ct);
        if (all.Count <= cap) return all;
        var stride = (double)all.Count / cap;
        var sample = new List<int>(cap);
        for (var i = 0; i < cap; i++) sample.Add(all[(int)(i * stride)]);
        return sample;
    }

    // Images share the taste vocabulary but have their own visual embedding space + no audio/temporal signals.
    public static async Task<List<int>> AllImageIdsAsync(DbContext db, int cap, CancellationToken ct)
        => await db.Set<Image>().AsNoTracking().OrderBy(i => i.Id).Select(i => i.Id).Take(cap).ToListAsync(ct);

    /// <summary>A representative image sample (strided) for building image calibration references.</summary>
    public static async Task<List<int>> SampleImageIdsAsync(DbContext db, int cap, CancellationToken ct)
    {
        var all = await db.Set<Image>().AsNoTracking().OrderBy(i => i.Id).Select(i => i.Id).ToListAsync(ct);
        if (all.Count <= cap) return all;
        var stride = (double)all.Count / cap;
        var sample = new List<int>(cap);
        for (var i = 0; i < cap; i++) sample.Add(all[(int)(i * stride)]);
        return sample;
    }

    /// <summary>Faces present on each IMAGE (FaceAppearanceHostType.Image). Images are single-frame, so there's no
    /// on-screen duration — each face gets weight 1. Same shape as the video variant so scoring can share code.</summary>
    public static async Task<Dictionary<int, List<(int faceId, double durationSec)>>> FetchImageFaceAppearancesAsync(DbContext db, IReadOnlyCollection<int> imageIds, CancellationToken ct)
    {
        if (imageIds.Count == 0) return new();
        var rows = await db.Set<FaceAppearance>().AsNoTracking()
            .Where(a => a.HostType == FaceAppearanceHostType.Image && imageIds.Contains(a.HostId))
            .Select(a => new { a.HostId, a.FaceId }).ToListAsync(ct);
        var res = new Dictionary<int, List<(int faceId, double durationSec)>>();
        foreach (var g in rows.GroupBy(r => r.HostId))
            res[g.Key] = g.Select(x => x.FaceId).Distinct().Select(f => (f, 1.0)).ToList();
        return res;
    }

    public static async Task<Dictionary<int, string>> StudioNamesAsync(DbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return (await db.Set<Studio>().AsNoTracking().IgnoreQueryFilters().Where(s => list.Contains(s.Id))
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
    }

    /// <summary>Turn raw tag/performer/studio affinity maps into a named, top-N <see cref="TasteProfile"/>.</summary>
    /// <summary>Profile with EVIDENCE-GATED negatives: an attribute whose affinity is negative but has NO real
    /// negative signal (zero disliked occurrences and not an explicit rating) is anti-correlation with your
    /// favourites, not a dislike — soften it toward neutral for display so it reads as "less preferred", not a
    /// scary −100. Real dislikes (disliked occurrences) and explicit negative ratings keep full magnitude. This
    /// is presentation only; the ungated affinities still drive ranking.</summary>
    public static Task<TasteProfile> ToProfileAsync(DbContext db, IReadOnlyDictionary<int, AffinityDetail> tags, IReadOnlyDictionary<int, AffinityDetail> performers, IReadOnlyDictionary<int, AffinityDetail> studios, int topN, string? summary, CancellationToken ct)
    {
        static double Display(AffinityDetail d) => d.Affinity < 0 && d.DislikedCount == 0 && d.Source != "direct" ? d.Affinity * 0.2 : d.Affinity;
        return ToProfileAsync(db,
            tags.ToDictionary(kv => kv.Key, kv => Display(kv.Value)),
            performers.ToDictionary(kv => kv.Key, kv => Display(kv.Value)),
            studios.ToDictionary(kv => kv.Key, kv => Display(kv.Value)), topN, summary, ct);
    }

    public static async Task<TasteProfile> ToProfileAsync(DbContext db, IReadOnlyDictionary<int, double> tags, IReadOnlyDictionary<int, double> performers, IReadOnlyDictionary<int, double> studios, int topN, string? summary, CancellationToken ct)
    {
        // Take the strongest by MAGNITUDE (so dislikes surface too), then present likes-first / dislikes-last.
        var topTags = tags.OrderByDescending(kv => Math.Abs(kv.Value)).Take(topN).OrderByDescending(kv => kv.Value).ToList();
        var topPerfs = performers.OrderByDescending(kv => Math.Abs(kv.Value)).Take(topN).OrderByDescending(kv => kv.Value).ToList();
        var topStudios = studios.OrderByDescending(kv => Math.Abs(kv.Value)).Take(topN).OrderByDescending(kv => kv.Value).ToList();
        var tagNames = await TagNamesAsync(db, topTags.Select(kv => kv.Key), ct);
        var perfNames = await PerformerNamesAsync(db, topPerfs.Select(kv => kv.Key), ct);
        var studioNames = await StudioNamesAsync(db, topStudios.Select(kv => kv.Key), ct);
        return new TasteProfile(
            topTags.Select(kv => new ProfileEntity("tag", kv.Key, tagNames.GetValueOrDefault(kv.Key, $"#{kv.Key}"), kv.Value)).ToList(),
            topPerfs.Select(kv => new ProfileEntity("performer", kv.Key, perfNames.GetValueOrDefault(kv.Key, $"#{kv.Key}"), kv.Value)).ToList(),
            topStudios.Select(kv => new ProfileEntity("studio", kv.Key, studioNames.GetValueOrDefault(kv.Key, $"#{kv.Key}"), kv.Value)).ToList(),
            summary);
    }

    /// <summary>TF-IDF tag affinity over a SIGNED-weighted set of ENGAGED videos (liked add, disliked
    /// subtract), normalized by max magnitude to [-1, 1]. Common tags are down-weighted by IDF, so one dislike
    /// barely dents a tag you have ten likes for, while a tag only in disliked content goes net-negative.</summary>
    public static Dictionary<int, double> TagAffinity(IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, IReadOnlyDictionary<int, int> tagCounts, int totalVideos)
    {
        var raw = new Dictionary<int, double>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m)) continue;
            foreach (var t in m.TagIds)
            {
                var idf = Math.Max(0, Math.Log(totalVideos / (1.0 + tagCounts.GetValueOrDefault(t, 1))));
                raw[t] = raw.GetValueOrDefault(t) + w * idf;
            }
        }
        NormalizeSigned(raw);
        return raw;
    }

    public static async Task<Dictionary<int, int>> StudioVideoCountsAsync(DbContext db, IReadOnlyCollection<int> studioIds, CancellationToken ct)
    {
        if (studioIds.Count == 0) return new();
        return (await db.Set<Studio>().AsNoTracking().IgnoreQueryFilters().Where(s => studioIds.Contains(s.Id))
            .Select(s => new { s.Id, s.VideoCount }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.VideoCount);
    }

    /// <summary>TF-IDF studio affinity over SIGNED engagement — IDF so a studio that just dominates your
    /// library doesn't rank high by base rate. Normalized by max magnitude to [-1, 1].</summary>
    public static Dictionary<int, double> StudioAffinity(IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, IReadOnlyDictionary<int, int> studioCounts, int totalVideos)
    {
        var raw = new Dictionary<int, double>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m) || m.StudioId is not { } sid) continue;
            var idf = Math.Max(0, Math.Log(totalVideos / (1.0 + studioCounts.GetValueOrDefault(sid, 1))));
            raw[sid] = raw.GetValueOrDefault(sid) + w * idf;
        }
        NormalizeSigned(raw);
        return raw;
    }

    /// <summary>Performer affinity from SIGNED engagement (propagated), overridden by any direct preference on
    /// the performer (which can itself be negative). Normalized by max magnitude to [-1, 1]. NO IDF — a
    /// prolific performer isn't "common noise" the way a frequent tag is.</summary>
    public static async Task<Dictionary<int, double>> PerformerAffinityAsync(ICoreServices core, int userId, IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, CancellationToken ct)
    {
        var prop = new Dictionary<int, double>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m)) continue;
            foreach (var p in m.PerformerIds) prop[p] = prop.GetValueOrDefault(p) + w;
        }
        NormalizeSigned(prop);
        var result = new Dictionary<int, double>(prop);
        // Direct preference on the performer (rated/favorited/disliked) overrides the propagated estimate.
        foreach (var e in await core.Preference.GetTopLikedAsync(userId, "performer", 600, null, ct))
            if (Math.Abs(e.Score) > 0.05)
                result[e.Entity.EntityId] = Math.Clamp(e.Score, -1, 1);
        return result;
    }

    /// <summary>An affinity value plus the evidence behind it — for recommenders that explain their picks.
    /// <paramref name="Confidence"/> (0..1) reflects how much evidence backs the estimate (occurrence count
    /// shrunk by the ridge strength) — useful for display and for prioritizing what to probe next.</summary>
    public sealed record AffinityDetail(double Affinity, int LikedCount, int DislikedCount, double Idf, int LibraryCount, string Source = "propagated", double Confidence = 0);

    public static async Task<Dictionary<int, int>> PerformerVideoCountsAsync(DbContext db, IReadOnlyCollection<int> performerIds, CancellationToken ct)
    {
        if (performerIds.Count == 0) return new();
        return (await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters().Where(p => performerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.VideoCount }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.VideoCount);
    }

    /// <summary>Signed TF-IDF tag affinity WITH the evidence (liked/disliked counts, IDF, library frequency).</summary>
    public static Dictionary<int, AffinityDetail> TagAffinityDetailed(IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, IReadOnlyDictionary<int, int> tagCounts, int totalVideos)
    {
        var raw = new Dictionary<int, double>();
        var liked = new Dictionary<int, int>();
        var disliked = new Dictionary<int, int>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m)) continue;
            foreach (var t in m.TagIds)
            {
                raw[t] = raw.GetValueOrDefault(t) + w * Idf(totalVideos, tagCounts.GetValueOrDefault(t, 1));
                if (w > 0) liked[t] = liked.GetValueOrDefault(t) + 1; else disliked[t] = disliked.GetValueOrDefault(t) + 1;
            }
        }
        var maxAbs = raw.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();
        return raw.Keys.ToDictionary(t => t, t => new AffinityDetail(maxAbs > 0 ? raw[t] / maxAbs : 0,
            liked.GetValueOrDefault(t), disliked.GetValueOrDefault(t), Idf(totalVideos, tagCounts.GetValueOrDefault(t, 1)), tagCounts.GetValueOrDefault(t, 0)));
    }

    /// <summary>Signed TF-IDF studio affinity WITH evidence.</summary>
    public static Dictionary<int, AffinityDetail> StudioAffinityDetailed(IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, IReadOnlyDictionary<int, int> studioCounts, int totalVideos)
    {
        var raw = new Dictionary<int, double>();
        var liked = new Dictionary<int, int>();
        var disliked = new Dictionary<int, int>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m) || m.StudioId is not { } sid) continue;
            raw[sid] = raw.GetValueOrDefault(sid) + w * Idf(totalVideos, studioCounts.GetValueOrDefault(sid, 1));
            if (w > 0) liked[sid] = liked.GetValueOrDefault(sid) + 1; else disliked[sid] = disliked.GetValueOrDefault(sid) + 1;
        }
        var maxAbs = raw.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();
        return raw.Keys.ToDictionary(s => s, s => new AffinityDetail(maxAbs > 0 ? raw[s] / maxAbs : 0,
            liked.GetValueOrDefault(s), disliked.GetValueOrDefault(s), Idf(totalVideos, studioCounts.GetValueOrDefault(s, 1)), studioCounts.GetValueOrDefault(s, 0)));
    }

    /// <summary>Signed performer affinity (no IDF) WITH evidence; direct preference overrides propagated.</summary>
    public static async Task<Dictionary<int, AffinityDetail>> PerformerAffinityDetailedAsync(ICoreServices core, int userId, IEnumerable<(int videoId, double weight)> engaged, IReadOnlyDictionary<int, VideoMeta> meta, IReadOnlyDictionary<int, int> performerCounts, CancellationToken ct)
    {
        var raw = new Dictionary<int, double>();
        var liked = new Dictionary<int, int>();
        var disliked = new Dictionary<int, int>();
        foreach (var (vid, w) in engaged)
        {
            if (w == 0 || !meta.TryGetValue(vid, out var m)) continue;
            foreach (var p in m.PerformerIds)
            {
                raw[p] = raw.GetValueOrDefault(p) + w;
                if (w > 0) liked[p] = liked.GetValueOrDefault(p) + 1; else disliked[p] = disliked.GetValueOrDefault(p) + 1;
            }
        }
        var maxAbs = raw.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();
        var result = raw.Keys.ToDictionary(p => p, p => new AffinityDetail(maxAbs > 0 ? raw[p] / maxAbs : 0,
            liked.GetValueOrDefault(p), disliked.GetValueOrDefault(p), 0, performerCounts.GetValueOrDefault(p, 0), "propagated"));
        foreach (var e in await core.Preference.GetTopLikedAsync(userId, "performer", 600, null, ct))
            if (Math.Abs(e.Score) > 0.05)
            {
                var existing = result.GetValueOrDefault(e.Entity.EntityId);
                result[e.Entity.EntityId] = new AffinityDetail(Math.Clamp(e.Score, -1, 1), existing?.LikedCount ?? 0, existing?.DislikedCount ?? 0,
                    0, existing?.LibraryCount ?? performerCounts.GetValueOrDefault(e.Entity.EntityId, 0), "direct");
            }
        return result;
    }

    /// <summary>Dislikes propagate to a video's tags/performers/studios more WEAKLY than likes do — clicking
    /// and skimming a video (e.g. while browsing, or just not in the mood) doesn't mean you dislike everything
    /// in it. Multiply a negative engagement weight by this before aggregating into attribute affinity.</summary>
    public const double NegativeDamping = 0.35;
    public static double DampNeg(double weight) => weight >= 0 ? weight : weight * NegativeDamping;

    // ── Multivariate (contrastive) attribution ────────────────────────────────
    // The univariate *Affinity helpers above average each attribute independently, so a tag inherits the
    // score of whatever it co-occurs with (confounding). The model below fixes that: it fits ONE per-user
    // ridge regression  score ≈ baseline + Σ wᵢ·attributeᵢ  over the engaged videos, so attributes are
    // solved JOINTLY and co-occurrence is partialled out — we credit the piece that actually moved the
    // needle, not every tag/performer/studio in the video. This is the foundation for faster learning and
    // for future "training-grounds" probing (the confidence it returns says what we still don't know).

    /// <summary>Tuning for <see cref="AttributeAttribution"/>.</summary>
    /// <param name="Lambda">Ridge strength. Roughly "how many occurrences an attribute needs before its
    /// weight is taken at face value" — thinly-evidenced attributes are shrunk toward neutral (0), which is
    /// what stops a couple of skims from branding a tag as disliked.</param>
    /// <param name="MinOccurrences">Attributes appearing fewer than this many times in the engaged set get no
    /// column at all (left perfectly neutral) — we don't guess from one or two data points.</param>
    /// <param name="MaxFeatures">Cap on the design width (highest-evidence attributes kept) to bound the solve.</param>
    /// <param name="ConfidenceFloor">Floor on a row's weight so weak signals still contribute a little while
    /// strong/explicit ones (|score| → 1) dominate the fit.</param>
    public sealed record AttributionOptions(double Lambda = 8.0, int MinOccurrences = 3, int MaxFeatures = 600, double ConfidenceFloor = 0.2);

    /// <summary>Per-type attributed weights (with evidence) plus the fitted baseline (the user's average
    /// engagement, absorbed by the intercept so the weights are deviations from it).</summary>
    public sealed record AttributionResult(
        Dictionary<int, AffinityDetail> Tags,
        Dictionary<int, AffinityDetail> Performers,
        Dictionary<int, AffinityDetail> Studios,
        double Baseline);

    /// <summary>Explicit per-component ("aspect") ratings for one video, each mapped to [-1, 1] (null =
    /// unrated). This is the "depth of ratings" supervision: when present, an aspect rating pins that
    /// component's target DIRECTLY (high confidence) instead of having to infer it from the overall score —
    /// so rating a video's performers high but its content low teaches the two components separately.</summary>
    public sealed record ComponentRatings(double? Content = null, double? Performers = null);

    private const int KindTag = 0, KindPerf = 1, KindStudio = 2;

    /// <summary>
    /// CONTRASTIVE / multivariate attribution. Fits a confidence-weighted ridge regression of preference onto
    /// the presence of each tag/performer/studio, jointly, so co-occurring attributes don't all inherit one
    /// another's credit. Three refinements beyond the base joint fit:
    /// <list type="bullet">
    /// <item>(2b) <paramref name="aspectTargets"/> — explicit content/performers aspect ratings enter as
    /// high-confidence component-specific rows (each with its own intercept, so their scale can differ from
    /// the overall score) that supervise just the tag or just the performer weights.</item>
    /// <item>(2c) <paramref name="visualFit"/> — a per-video cosine-to-taste covariate is added to the overall
    /// rows so attribute weights are NET of "it just looks like your taste"; a tag only earns weight if videos
    /// with it are liked beyond what their visual similarity already explains.</item>
    /// </list>
    /// Returns each type's weights max-magnitude-normalized to [-1, 1] (sign preserved), with liked/disliked
    /// evidence and a confidence. Pure/synchronous; <see cref="AttributeAttributionAsync"/> layers direct
    /// entity preferences on top.
    /// </summary>
    public static AttributionResult AttributeAttribution(
        IEnumerable<(int videoId, double score)> engaged,
        IReadOnlyDictionary<int, VideoMeta> meta,
        IReadOnlyDictionary<int, int> tagCounts,
        IReadOnlyDictionary<int, int> studioCounts,
        IReadOnlyDictionary<int, int> performerCounts,
        int totalVideos,
        AttributionOptions? options = null,
        IReadOnlyDictionary<int, ComponentRatings>? aspectTargets = null,
        IReadOnlyDictionary<int, double>? visualFit = null,
        IReadOnlyDictionary<int, double>? rowWeights = null,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>? attentionTagCells = null,
        IReadOnlyDictionary<int, double>? faceFit = null,
        IReadOnlyDictionary<int, double>? unlinkedLoad = null)
    {
        var opt = options ?? new AttributionOptions();
        var empty = new AttributionResult(new(), new(), new(), 0);

        // 1. Materialize per-video attribute lists + count occurrences / liked-disliked evidence.
        var occ = new Dictionary<(int kind, int id), int>();
        var liked = new Dictionary<(int kind, int id), int>();
        var disliked = new Dictionary<(int kind, int id), int>();
        var samples = new List<(int vid, double score, List<(int kind, int id)> attrs)>();
        foreach (var (vid, score) in engaged)
        {
            if (score == 0 || !meta.TryGetValue(vid, out var m)) continue;
            var attrs = new List<(int, int)>(m.TagIds.Length + m.PerformerIds.Length + 1);
            foreach (var t in m.TagIds) attrs.Add((KindTag, t));
            foreach (var p in m.PerformerIds) attrs.Add((KindPerf, p));
            if (m.StudioId is { } sid) attrs.Add((KindStudio, sid));
            if (attrs.Count == 0) continue;
            foreach (var a in attrs)
            {
                occ[a] = occ.GetValueOrDefault(a) + 1;
                if (score > 0) liked[a] = liked.GetValueOrDefault(a) + 1; else disliked[a] = disliked.GetValueOrDefault(a) + 1;
            }
            samples.Add((vid, score, attrs));
        }
        if (samples.Count == 0) return empty;

        // 2. Columns: three per-view intercepts (overall/content/performers), an optional visual-fit
        //    covariate, then the kept attribute columns (those meeting MinOccurrences, capped by evidence).
        const int IntOverall = 0, IntContent = 1, IntPerf = 2;
        int baseCols = 3;
        bool useVisual = visualFit is { Count: > 0 };
        int visualCol = -1;
        if (useVisual) visualCol = baseCols++;
        bool useFace = faceFit is { Count: > 0 };   // 2nd covariate: "who is on screen" — de-confounds face preference from content
        int faceCol = -1;
        if (useFace) faceCol = baseCols++;
        // 3rd covariate: UNLINKED-cast load (faces present beyond the linked performers). Absorbs the systematic
        // effect of people we can't credit — e.g. an unattractive, unlinked co-star dragging a video's rating —
        // so that effect doesn't get misattributed to the LINKED performer(s) who share the scene.
        bool useUnlinked = unlinkedLoad is { Count: > 0 };
        int unlinkedCol = -1;
        if (useUnlinked) unlinkedCol = baseCols++;

        var kept = occ.Where(kv => kv.Value >= opt.MinOccurrences)
                      .OrderByDescending(kv => kv.Value)
                      .Take(Math.Max(0, opt.MaxFeatures))
                      .Select(kv => kv.Key).ToList();
        if (kept.Count == 0) return empty;
        var col = new Dictionary<(int kind, int id), int>(kept.Count);
        for (var i = 0; i < kept.Count; i++) col[kept[i]] = baseCols + i;
        int d = baseCols + kept.Count;

        // Center the visual-fit covariate so the overall intercept absorbs its mean (better conditioning, and
        // the covariate's weight reads as the marginal effect of being more/less visually on-taste than usual).
        double visualMean = 0;
        if (useVisual)
        {
            double sum = 0; int cnt = 0;
            foreach (var s in samples) if (visualFit!.TryGetValue(s.vid, out var f)) { sum += f; cnt++; }
            visualMean = cnt > 0 ? sum / cnt : 0;
        }
        double faceMean = 0;
        if (useFace)
        {
            double sum = 0; int cnt = 0;
            foreach (var s in samples) if (faceFit!.TryGetValue(s.vid, out var f)) { sum += f; cnt++; }
            faceMean = cnt > 0 ? sum / cnt : 0;
        }
        double unlinkedMean = 0;
        if (useUnlinked)
        {
            double sum = 0; int cnt = 0;
            foreach (var s in samples) if (unlinkedLoad!.TryGetValue(s.vid, out var u)) { sum += u; cnt++; }
            unlinkedMean = cnt > 0 ? sum / cnt : 0;
        }

        // 3. Build weighted rows: (target, confidence, [(col, value)…]). Each engaged video contributes an
        //    OVERALL row (all attrs + visual covariate); plus, when present, high-confidence CONTENT (tag-only)
        //    and PERFORMERS (performer-only) rows driven by explicit aspect ratings.
        var rows = new List<(double target, double conf, List<(int col, double val)> cells)>(samples.Count);
        foreach (var (vid, score, attrs) in samples)
        {
            // Per-row weight (default 1). Used for LOCAL regressions: weight each engaged video by how close it
            // is to a taste niche, so a niche gets its own attribute model (with the user's nearby negatives as
            // real contrast). A zero weight drops the row entirely.
            var rw = rowWeights is null ? 1.0 : rowWeights.GetValueOrDefault(vid, 1.0);
            if (rw <= 0) continue;

            var overall = new List<(int, double)>(attrs.Count + 2) { (IntOverall, 1.0) };
            // Attention: a tag's cell value can be scaled by how much of the WATCHED time it was on screen, so a
            // liked video credits the tags you actually attended to more than ones in a part you skipped. Values
            // are a moderate multiplier (never zero) supplied by the caller; absent → full presence (1.0).
            var vidAtt = attentionTagCells is not null && attentionTagCells.TryGetValue(vid, out var va) ? va : null;
            foreach (var at in attrs) if (col.TryGetValue(at, out var c))
                overall.Add((c, at.Item1 == KindTag && vidAtt is not null && vidAtt.TryGetValue(at.Item2, out var mult) ? mult : 1.0));
            if (useVisual && visualFit!.TryGetValue(vid, out var fit)) overall.Add((visualCol, fit - visualMean));
            if (useFace && faceFit!.TryGetValue(vid, out var ff)) overall.Add((faceCol, ff - faceMean));
            if (useUnlinked && unlinkedLoad!.TryGetValue(vid, out var ul)) overall.Add((unlinkedCol, ul - unlinkedMean));
            rows.Add((score, Math.Clamp(Math.Abs(score), opt.ConfidenceFloor, 1.0) * rw, overall));

            if (aspectTargets is null || !aspectTargets.TryGetValue(vid, out var asp)) continue;
            if (asp.Content is { } cr)
            {
                var cells = new List<(int, double)> { (IntContent, 1.0) };
                foreach (var at in attrs) if (at.Item1 == KindTag && col.TryGetValue(at, out var c)) cells.Add((c, 1.0));
                rows.Add((Math.Clamp(cr, -1, 1), rw, cells)); // explicit → full confidence (× locality)
            }
            if (asp.Performers is { } pr)
            {
                var cells = new List<(int, double)> { (IntPerf, 1.0) };
                foreach (var at in attrs) if (at.Item1 == KindPerf && col.TryGetValue(at, out var c)) cells.Add((c, 1.0));
                rows.Add((Math.Clamp(pr, -1, 1), rw, cells));
            }
        }

        // 4. Accumulate the weighted normal equations  G = XᵀWX (+ λI off the intercepts),  b = XᵀWy.
        var gram = new double[d * d];
        var b = new double[d];
        foreach (var (target, conf, cells) in rows)
        {
            for (var ii = 0; ii < cells.Count; ii++)
            {
                var (ci, vi) = cells[ii];
                b[ci] += conf * target * vi;
                gram[ci * d + ci] += conf * vi * vi;
                for (var jj = ii + 1; jj < cells.Count; jj++)
                {
                    var (cj, vj) = cells[jj];
                    var x = conf * vi * vj;
                    gram[ci * d + cj] += x;
                    gram[cj * d + ci] += x;
                }
            }
        }
        for (var i = 3; i < d; i++) gram[i * d + i] += opt.Lambda; // ridge on covariates + attrs, NOT the 3 intercepts (indices 0-2)

        // 5. Solve the SPD system for the weight vector.
        var w = SolveSpd(gram, b, d);

        // 6. Split per type, normalize each to [-1, 1], attach evidence + confidence.
        var tagW = new Dictionary<int, double>();
        var perfW = new Dictionary<int, double>();
        var studioW = new Dictionary<int, double>();
        foreach (var (key, c) in col)
        {
            var v = w[c];
            if (key.kind == KindTag) tagW[key.id] = v;
            else if (key.kind == KindPerf) perfW[key.id] = v;
            else studioW[key.id] = v;
        }

        Dictionary<int, AffinityDetail> ToDetail(Dictionary<int, double> raw, int kind, IReadOnlyDictionary<int, int> libCounts)
        {
            var maxAbs = raw.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();
            var result = new Dictionary<int, AffinityDetail>(raw.Count);
            foreach (var (id, v) in raw)
            {
                var n = occ.GetValueOrDefault((kind, id));
                var confidence = n / (n + opt.Lambda);                       // shrinkage-aligned, in [0, 1)
                var idf = kind == KindPerf ? 0 : Idf(totalVideos, libCounts.GetValueOrDefault(id, n));
                result[id] = new AffinityDetail(maxAbs > 0 ? v / maxAbs : 0,
                    liked.GetValueOrDefault((kind, id)), disliked.GetValueOrDefault((kind, id)),
                    idf, libCounts.GetValueOrDefault(id, 0), "attributed", confidence);
            }
            return result;
        }

        return new AttributionResult(
            ToDetail(tagW, KindTag, tagCounts),
            ToDetail(perfW, KindPerf, performerCounts),
            ToDetail(studioW, KindStudio, studioCounts),
            w[IntOverall]);
    }

    /// <summary>Multivariate attribution PLUS direct entity preferences: when the user has rated/favorited a
    /// performer or studio itself, that explicit signal (which can be negative) overrides the attributed
    /// estimate. This is where "explicit interaction helps significantly" lands.</summary>
    public static async Task<AttributionResult> AttributeAttributionAsync(
        ICoreServices core, int userId,
        IEnumerable<(int videoId, double score)> engaged,
        IReadOnlyDictionary<int, VideoMeta> meta,
        IReadOnlyDictionary<int, int> tagCounts,
        IReadOnlyDictionary<int, int> studioCounts,
        IReadOnlyDictionary<int, int> performerCounts,
        int totalVideos,
        AttributionOptions? options,
        CancellationToken ct,
        IReadOnlyDictionary<int, ComponentRatings>? aspectTargets = null,
        IReadOnlyDictionary<int, double>? visualFit = null,
        DirectPrefs? directPrefs = null,
        IReadOnlyDictionary<int, List<int>>? tagChildren = null,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>? attentionTagCells = null,
        IReadOnlyDictionary<int, double>? faceFit = null,
        IReadOnlyDictionary<int, double>? unlinkedLoad = null)
    {
        var r = AttributeAttribution(engaged, meta, tagCounts, studioCounts, performerCounts, totalVideos, options, aspectTargets, visualFit, null, attentionTagCells, faceFit, unlinkedLoad);
        // Direct entity preferences (rated/favorited tag/performer/studio) override the attributed estimate.
        // Pass them pre-fetched (DirectPrefs) to overlap those lookups with the rest of the profile build.
        var direct = directPrefs ?? await FetchDirectPrefsAsync(core, userId, ct);
        var tags = OverrideWithDirect(direct.Tags, r.Tags, tagCounts);
        // Then let explicit tag ratings flow DOWN the tag hierarchy as weak, override-able priors.
        if (tagChildren is not null) ApplyTagHierarchyPriors(tags, tagChildren);
        return r with
        {
            Tags = tags,
            Performers = OverrideWithDirect(direct.Performers, r.Performers, performerCounts),
            Studios = OverrideWithDirect(direct.Studios, r.Studios, studioCounts),
        };
    }

    /// <summary>The user's direct entity preferences (rated/favorited tags/performers/studios), all fetched
    /// CONCURRENTLY. Start this early and pass the result into <see cref="AttributeAttributionAsync"/> so these
    /// preference-scorer passes overlap the embedding/metadata work instead of running sequentially after it.</summary>
    public sealed record DirectPrefs(IReadOnlyList<ScoredEntity> Tags, IReadOnlyList<ScoredEntity> Performers, IReadOnlyList<ScoredEntity> Studios);

    public static async Task<DirectPrefs> FetchDirectPrefsAsync(ICoreServices core, int userId, CancellationToken ct)
    {
        var t = core.Preference.GetTopLikedAsync(userId, "tag", 600, null, ct);
        var p = core.Preference.GetTopLikedAsync(userId, "performer", 600, null, ct);
        var s = core.Preference.GetTopLikedAsync(userId, "studio", 600, null, ct);
        await Task.WhenAll(t, p, s);
        return new DirectPrefs(t.Result, p.Result, s.Result);
    }

    /// <summary>Read explicit per-component aspect ratings (content→tags, performers→performers) for a set of
    /// videos and map 0–100 → [-1, 1] (50 = neutral). Backs the 2b supervision; "overall" is excluded (that's
    /// already the main target). Other aspects (video_quality, audio) aren't taste-attribute components.</summary>
    public static async Task<Dictionary<int, ComponentRatings>> FetchAspectTargetsAsync(DbContext db, int userId, IReadOnlyCollection<int> videoIds, CancellationToken ct)
    {
        if (videoIds.Count == 0) return new();
        var ratings = await db.Set<Rating>().AsNoTracking()
            .Where(r => r.UserId == userId && r.HostType == RatingHostType.Video && videoIds.Contains(r.HostId)
                        && (r.Aspect == "content" || r.Aspect == "performers"))
            .Select(r => new { r.HostId, r.Aspect, r.Value }).ToListAsync(ct);
        var result = new Dictionary<int, ComponentRatings>();
        foreach (var g in ratings.GroupBy(r => r.HostId))
        {
            double? content = null, performers = null;
            foreach (var r in g)
            {
                var mapped = Math.Clamp((r.Value - 50) / 50.0, -1, 1);
                if (r.Aspect == "content") content = mapped; else performers = mapped;
            }
            result[g.Key] = new ComponentRatings(content, performers);
        }
        return result;
    }

    private static Dictionary<int, AffinityDetail> OverrideWithDirect(
        IReadOnlyList<ScoredEntity> direct, Dictionary<int, AffinityDetail> attributed, IReadOnlyDictionary<int, int> libCounts)
    {
        var result = new Dictionary<int, AffinityDetail>(attributed);
        foreach (var e in direct)
            if (Math.Abs(e.Score) > 0.05)
            {
                var ex = result.GetValueOrDefault(e.Entity.EntityId);
                result[e.Entity.EntityId] = new AffinityDetail(Math.Clamp(e.Score, -1, 1),
                    ex?.LikedCount ?? 0, ex?.DislikedCount ?? 0, 0,
                    ex?.LibraryCount ?? libCounts.GetValueOrDefault(e.Entity.EntityId, 0), "direct", 1);
            }
        return result;
    }

    /// <summary>Tag hierarchy as parentId → child tag ids (from the TagParent join). Small table; fetch once
    /// per profile build and pass into <see cref="AttributeAttributionAsync"/> to enable hierarchy priors.</summary>
    public static async Task<Dictionary<int, List<int>>> FetchTagChildrenAsync(DbContext db, CancellationToken ct)
    {
        var edges = await db.Set<TagParent>().AsNoTracking().Select(t => new { t.ParentId, t.ChildId }).ToListAsync(ct);
        var map = new Dictionary<int, List<int>>();
        foreach (var e in edges)
        {
            if (!map.TryGetValue(e.ParentId, out var list)) map[e.ParentId] = list = [];
            list.Add(e.ChildId);
        }
        return map;
    }

    /// <summary>Propagate explicit tag ratings DOWN the tag hierarchy as weak, OVERRIDE-able priors: rate
    /// "bondage" highly and its child "metal bondage" inherits a decayed positive prior — a soft nudge, not a
    /// verdict. Seeds are the directly-rated tags (source "direct"); each descendant's prior is the seed
    /// affinity decayed by <paramref name="decay"/> per level (so the magnitude itself is already weak), then
    /// blended into the descendant by ITS OWN attribution confidence — real engagement signal wins where we
    /// have it, the inherited prior only fills the gap. A directly-rated tag governs itself (never a target).
    /// Mutates and returns <paramref name="tags"/>.</summary>
    public static Dictionary<int, AffinityDetail> ApplyTagHierarchyPriors(
        Dictionary<int, AffinityDetail> tags, IReadOnlyDictionary<int, List<int>> children, double decay = 0.5, int maxDepth = 3)
    {
        if (children.Count == 0) return tags;
        var seeds = tags.Where(kv => kv.Value.Source == "direct" && Math.Abs(kv.Value.Affinity) > 0.05)
            .Select(kv => (Id: kv.Key, kv.Value.Affinity)).ToList();
        if (seeds.Count == 0) return tags;
        var direct = tags.Where(kv => kv.Value.Source == "direct").Select(kv => kv.Key).ToHashSet();

        // BFS each seed's subtree; keep the STRONGEST inherited magnitude per descendant.
        var prior = new Dictionary<int, double>();
        foreach (var (seedId, seedAff) in seeds)
        {
            var frontier = new Queue<(int id, double aff, int depth)>();
            frontier.Enqueue((seedId, seedAff, 0));
            var walked = new HashSet<int> { seedId };
            while (frontier.Count > 0)
            {
                var (id, aff, depth) = frontier.Dequeue();
                if (depth >= maxDepth || !children.TryGetValue(id, out var kids)) continue;
                foreach (var child in kids)
                {
                    if (!walked.Add(child)) continue;                  // guard cycles
                    var childAff = aff * decay;
                    if (!direct.Contains(child) && (!prior.TryGetValue(child, out var ex) || Math.Abs(childAff) > Math.Abs(ex)))
                        prior[child] = childAff;
                    frontier.Enqueue((child, childAff, depth + 1));
                }
            }
        }

        foreach (var (id, p) in prior)
        {
            var ex = tags.GetValueOrDefault(id);
            var c = ex?.Confidence ?? 0;                               // trust real attribution where confident
            var blended = (ex?.Affinity ?? 0) * c + p * (1 - c);
            tags[id] = new AffinityDetail(Math.Clamp(blended, -1, 1),
                ex?.LikedCount ?? 0, ex?.DislikedCount ?? 0, ex?.Idf ?? 0,
                ex?.LibraryCount ?? 0, ex?.Source ?? "hierarchy", Math.Max(c, Math.Abs(p)));
        }
        return tags;
    }

    /// <summary>Active-learning probe selection — recommender-AGNOSTIC, so any recommender can expose
    /// <c>ITrainingGround</c> by delegating here. Picks on-taste candidate videos/images that hinge on the
    /// attribute the model is least sure about (uncertainty = 1 − confidence from the shared attribution),
    /// one per attribute. Engaged images are merged in so image ratings sharpen the same model.</summary>
    public static async Task<TrainingProbeSet> BuildTrainingProbesAsync(
        ICoreServices core, IEmbeddingRepository repo, IEmbeddingService search, DbContext db,
        int userId, int limit, string mediaType, CancellationToken ct)
    {
        bool images = string.Equals(mediaType, "image", StringComparison.OrdinalIgnoreCase);

        var likedVideos = (await core.Preference.GetTopLikedAsync(userId, "video", 600, null, ct)).Where(e => e.Score > 0.1).Take(60).ToList();
        var centroidVecs = await GetVisualVectorsAsync(repo, likedVideos.Select(e => e.Entity.EntityId).ToList(), ct);
        var centroidSrc = likedVideos.Where(e => centroidVecs.ContainsKey(e.Entity.EntityId)).Select(e => (vec: centroidVecs[e.Entity.EntityId], score: e.Score)).ToList();
        if (centroidSrc.Count == 0 && images)
        {
            var likedImgs = (await core.Preference.GetTopLikedAsync(userId, "image", 600, null, ct)).Where(e => e.Score > 0.1).Take(60).ToList();
            var iv = await GetImageVisualVectorsAsync(repo, likedImgs.Select(e => e.Entity.EntityId).ToList(), ct);
            centroidSrc = likedImgs.Where(e => iv.ContainsKey(e.Entity.EntityId)).Select(e => (vec: iv[e.Entity.EntityId], score: e.Score)).ToList();
        }
        if (centroidSrc.Count < 3)
            return new TrainingProbeSet([], "Rate or watch a handful of items first, then the trainer can find useful examples.");
        var featCentroid = Vectors.WeightedCentroid(centroidSrc.Select(s => (s.vec.Feature, Math.Max(0.01, s.score))));
        var semCentroid = Vectors.WeightedCentroid(centroidSrc.Select(s => (s.vec.Semantic, Math.Max(0.01, s.score))));

        var engagedVideos = (await core.Preference.GetTopLikedAsync(userId, "video", 1500, null, ct)).Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var videoMeta = await FetchVideoMetaAsync(db, engagedVideos.Select(e => e.Entity.EntityId).ToList(), ct);
        var (mergedScored, mergedMeta) = await MergeEngagedImagesAsync(core, db, userId, engagedVideos.Select(e => (e.Entity.EntityId, e.Score)), videoMeta, ct);
        var total = await TotalVideosAsync(db, ct);
        var tagCounts = await TagVideoCountsAsync(db, mergedMeta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var studioCounts = await StudioVideoCountsAsync(db, mergedMeta.Values.Where(m => m.StudioId.HasValue).Select(m => m.StudioId!.Value).Distinct().ToList(), ct);
        var perfCounts = await PerformerVideoCountsAsync(db, mergedMeta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList(), ct);
        var attribution = AttributeAttribution(mergedScored, mergedMeta, tagCounts, studioCounts, perfCounts, total);

        var uncertainty = new Dictionary<(int type, int id), double>();
        void AddUnc(int type, Dictionary<int, AffinityDetail> dict)
        {
            foreach (var (id, det) in dict)
            {
                if (det.LibraryCount < 2) continue;
                var u = 1.0 - det.Confidence;
                if (u < 0.25) continue;
                uncertainty[(type, id)] = u;
            }
        }
        AddUnc(0, attribution.Tags); AddUnc(1, attribution.Performers); AddUnc(2, attribution.Studios);
        if (uncertainty.Count == 0)
            return new TrainingProbeSet([], "The model is already fairly confident about the attributes in your taste — nothing high-value to probe right now.");

        var seen = (await core.Preference.GetTopLikedAsync(userId, images ? "image" : "video", 1500, null, ct)).Select(e => e.Entity.EntityId).ToHashSet();
        var candList = images
            ? await KnnImageUnionAsync(search, featCentroid, semCentroid, limit * 12 + 200, seen, ct)
            : await KnnUnionAsync(search, featCentroid, semCentroid, limit * 12 + 200, seen, ct);
        var candMeta = images ? await FetchImageMetaAsync(db, candList, ct) : await FetchVideoMetaAsync(db, candList, ct);
        var candVisual = images ? await GetImageVisualVectorsAsync(repo, candList, ct) : await GetVisualVectorsAsync(repo, candList, ct);

        double Unc(int type, int id) => uncertainty.GetValueOrDefault((type, id));
        var ranked = new List<(int cand, int type, int id, double unc, double fit)>();
        foreach (var id in candList)
        {
            if (!candMeta.TryGetValue(id, out var m) || !candVisual.TryGetValue(id, out var cv)) continue;
            var fit = Math.Max(0, Vectors.BlendedCosine(cv.Feature, cv.Semantic, featCentroid, semCentroid, 0.70, 0.30));
            if (fit < 0.2) continue;
            (int type, int id, double unc) best = (-1, -1, 0);
            foreach (var t in m.TagIds) { var u = Unc(0, t); if (u > best.unc) best = (0, t, u); }
            foreach (var p in m.PerformerIds) { var u = Unc(1, p); if (u > best.unc) best = (1, p, u); }
            if (m.StudioId is { } sid) { var u = Unc(2, sid); if (u > best.unc) best = (2, sid, u); }
            if (best.type < 0) continue;
            ranked.Add((id, best.type, best.id, best.unc, fit));
        }
        if (ranked.Count == 0)
            return new TrainingProbeSet([], "Couldn't find on-taste items that probe an uncertain attribute right now — engage a bit more and retry.");

        var chosen = new List<(int cand, int type, int id, double unc)>();
        var usedAttr = new HashSet<(int, int)>();
        foreach (var r in ranked.OrderByDescending(r => r.unc * (0.5 + 0.5 * r.fit)))
        {
            if (chosen.Count >= limit) break;
            if (!usedAttr.Add((r.type, r.id))) continue;
            chosen.Add((r.cand, r.type, r.id, r.unc));
        }

        var tagNames = await TagNamesAsync(db, chosen.Where(c => c.type == 0).Select(c => c.id), ct);
        var perfNames = await PerformerNamesAsync(db, chosen.Where(c => c.type == 1).Select(c => c.id), ct);
        var studioNames = await StudioNamesAsync(db, chosen.Where(c => c.type == 2).Select(c => c.id), ct);
        var et = images ? "image" : "video";
        var probes = chosen.Select(c =>
        {
            var (typeName, aspect, attrName) = c.type switch
            {
                0 => ("tag", "content", tagNames.GetValueOrDefault(c.id, $"#{c.id}")),
                1 => ("performer", "performers", perfNames.GetValueOrDefault(c.id, $"#{c.id}")),
                _ => ("studio", "overall", studioNames.GetValueOrDefault(c.id, $"#{c.id}")),
            };
            var rationale = $"An on-taste {et} that hinges on the {typeName} \"{attrName}\" — the model isn't sure how you feel about it yet. Rate it to teach it.";
            return new TrainingProbe(new EntityRef(et, c.cand), null, 0, typeName, c.id, attrName, true, aspect, c.unc, rationale);
        }).ToList();
        return new TrainingProbeSet(probes, $"{probes.Count} on-taste {et}s targeting the attributes the model is least sure about.");
    }

    /// <summary>Solve a symmetric positive-definite system A·x = b (A row-major d×d) via Cholesky. The ridge
    /// term makes A SPD; a tiny diagonal floor guards against round-off making a pivot non-positive.</summary>
    private static double[] SolveSpd(double[] a, double[] b, int d)
    {
        var l = new double[d * d]; // lower-triangular Cholesky factor, A = L·Lᵀ
        for (var j = 0; j < d; j++)
        {
            double diag = a[j * d + j];
            for (var k = 0; k < j; k++) diag -= l[j * d + k] * l[j * d + k];
            l[j * d + j] = Math.Sqrt(Math.Max(diag, 1e-12));
            for (var i = j + 1; i < d; i++)
            {
                double s = a[i * d + j];
                for (var k = 0; k < j; k++) s -= l[i * d + k] * l[j * d + k];
                l[i * d + j] = s / l[j * d + j];
            }
        }
        var y = new double[d]; // forward solve L·y = b
        for (var i = 0; i < d; i++)
        {
            double s = b[i];
            for (var k = 0; k < i; k++) s -= l[i * d + k] * y[k];
            y[i] = s / l[i * d + i];
        }
        var x = new double[d]; // back solve Lᵀ·x = y
        for (var i = d - 1; i >= 0; i--)
        {
            double s = y[i];
            for (var k = i + 1; k < d; k++) s -= l[k * d + i] * x[k];
            x[i] = s / l[i * d + i];
        }
        return x;
    }

    private static double Idf(int totalVideos, int libraryCount) => Math.Max(0, Math.Log(totalVideos / (1.0 + libraryCount)));

    /// <summary>Index of the centroid most similar (cosine) to a point — for assigning candidates to niches.</summary>
    public static int NearestCentroidIndex(IReadOnlyList<float[]> centroids, float[] p)
    {
        int best = 0; double bestSim = double.NegativeInfinity;
        for (var i = 0; i < centroids.Count; i++) { var s = Vectors.Cosine(p, centroids[i]); if (s > bestSim) { bestSim = s; best = i; } }
        return best;
    }

    // ── Refined taste clustering ───────────────────────────────────────────────
    /// <summary>A clustering with per-point membership: centroids + the cluster index each input point landed in.</summary>
    public sealed record ClusterResult(List<float[]> Centroids, int[] Assignment);

    /// <summary>
    /// k-means at a generous k, then make the niche count HONEST: iteratively MERGE centroids that are too
    /// similar (cosine ≥ <paramref name="mergeCos"/>) and ABSORB sub-minimum niches into their nearest
    /// neighbour. The final count falls out of the data — no near-duplicate niches, no orphan ones — which is
    /// the failure mode of plain k-means-with-arbitrary-k. Returns final centroids + each point's cluster.
    /// </summary>
    public static ClusterResult RefinedClusters(IReadOnlyList<float[]> points, IReadOnlyList<double> weights, int maxK, double mergeCos = 0.82, int minMembers = 3)
    {
        int n = points.Count;
        if (n == 0) return new ClusterResult([], []);
        var init = Vectors.KMeans(points, weights, Math.Clamp(maxK, 1, n));
        if (init.Count == 0) return new ClusterResult([], []);

        // Group point indices by nearest initial centroid, drop empties, recompute centroids.
        var assign0 = points.Select(p => NearestCentroidIndex(init, p)).ToArray();
        var clusters = Enumerable.Range(0, init.Count)
            .Select(c => Enumerable.Range(0, n).Where(i => assign0[i] == c).ToList())
            .Where(m => m.Count > 0).ToList();
        float[] CentroidOf(List<int> m) => Vectors.WeightedCentroid(m.Select(i => ((float[]?)points[i], weights[i])))!;
        var cents = clusters.Select(CentroidOf).ToList();

        // 1. Merge near-duplicate niches.
        bool changed = true;
        while (changed && cents.Count > 1)
        {
            changed = false;
            for (var a = 0; a < cents.Count && !changed; a++)
                for (var b = a + 1; b < cents.Count && !changed; b++)
                    if (Vectors.Cosine(cents[a], cents[b]) >= mergeCos)
                    {
                        clusters[a].AddRange(clusters[b]);
                        clusters.RemoveAt(b); cents.RemoveAt(b);
                        cents[a] = CentroidOf(clusters[a]);
                        changed = true;
                    }
        }

        // 2. Absorb under-supported niches into their nearest neighbour.
        changed = true;
        while (changed && cents.Count > 1)
        {
            changed = false;
            var small = clusters.FindIndex(m => m.Count < minMembers);
            if (small >= 0)
            {
                int near = -1; double best = double.NegativeInfinity;
                for (var c = 0; c < cents.Count; c++)
                    if (c != small) { var s = Vectors.Cosine(cents[small], cents[c]); if (s > best) { best = s; near = c; } }
                if (near >= 0)
                {
                    clusters[near].AddRange(clusters[small]);
                    clusters.RemoveAt(small); cents.RemoveAt(small);
                    var ni = small < near ? near - 1 : near;
                    cents[ni] = CentroidOf(clusters[ni]);
                    changed = true;
                }
            }
        }

        var finalAssign = new int[n];
        for (var c = 0; c < clusters.Count; c++) foreach (var i in clusters[c]) finalAssign[i] = c;
        return new ClusterResult(cents, finalAssign);
    }

    /// <summary>
    /// Cluster items in a MULTIMODAL space: each item's L2-normalized visual embedding CONCATENATED with its
    /// L2-normalized tag signature (a sparse vector over the item-set's tag vocabulary; the caller supplies each
    /// tag's weight, e.g. coverage% × IDF). The two blocks are scaled by √(1−α) and √α so cosine in the combined
    /// space is exactly (1−α)·visualCos + α·tagCos — <paramref name="tagBlend"/> α dials "group by look" ↔ "group
    /// by content". This lets niches split on CO-OCCURRING DISTINCTIVE TAGS even when the visual embeddings look
    /// alike (the fix for a visually-homogeneous library). Returns per-item cluster assignment; the caller
    /// recomputes each niche's visual centroid from its members for candidate generation.
    /// </summary>
    public static ClusterResult RefinedMultimodalClusters(
        IReadOnlyList<float[]?> visual,
        IReadOnlyList<IReadOnlyDictionary<int, double>> tagWeights,
        IReadOnlyList<double> itemWeights,
        double tagBlend, int maxK, double mergeCos = 0.88, int minMembers = 2)
    {
        int n = visual.Count;
        if (n == 0) return new ClusterResult([], []);
        tagBlend = Math.Clamp(tagBlend, 0, 1);

        var vocab = tagWeights.SelectMany(d => d.Keys).Distinct().OrderBy(x => x).ToList();
        var vocabIndex = new Dictionary<int, int>(vocab.Count);
        for (var i = 0; i < vocab.Count; i++) vocabIndex[vocab[i]] = i;

        int visualDim = 0;
        foreach (var v in visual) if (v is not null) { visualDim = v.Length; break; }
        double wV = Math.Sqrt(1 - tagBlend), wT = Math.Sqrt(tagBlend);

        var points = new List<float[]>(n);
        for (var i = 0; i < n; i++)
        {
            var combined = new float[visualDim + vocab.Count];
            var v = visual[i];
            if (v is not null && visualDim > 0)
            {
                double norm = 0; for (var d = 0; d < v.Length; d++) norm += v[d] * (double)v[d];
                norm = Math.Sqrt(norm);
                if (norm > 1e-9) for (var d = 0; d < visualDim && d < v.Length; d++) combined[d] = (float)(wV * v[d] / norm);
            }
            var tw = tagWeights[i];
            double tnorm = 0; foreach (var kv in tw) tnorm += kv.Value * kv.Value;
            tnorm = Math.Sqrt(tnorm);
            if (tnorm > 1e-9)
                foreach (var (tag, weight) in tw)
                    if (vocabIndex.TryGetValue(tag, out var idx))
                        combined[visualDim + idx] = (float)(wT * weight / tnorm);
            points.Add(combined);
        }

        return RefinedClusters(points, itemWeights, maxK, mergeCos, minMembers);
    }

    public static void Normalize(Dictionary<int, double> map)
    {
        var max = map.Values.DefaultIfEmpty(0).Max();
        if (max <= 0) return;
        foreach (var k in map.Keys.ToList()) map[k] /= max;
    }

    /// <summary>Scale a signed map by its max magnitude so values land in [-1, 1] (preserving sign).</summary>
    public static void NormalizeSigned(Dictionary<int, double> map)
    {
        var max = map.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (max <= 0) return;
        foreach (var k in map.Keys.ToList()) map[k] /= max;
    }

    public static double Knob(IReadOnlyDictionary<string, double>? knobs, string key, double fallback)
        => knobs != null && knobs.TryGetValue(key, out var v) ? v : fallback;

    // ── Soft steering ("find a taste/mood within your recommendations") ────────
    // A steer is NOT a hard filter. It resolves the user's terms to tag/performer ids, EXPANDS them by
    // co-occurrence (tags that appear alongside the steer tag; performers who co-appear with the steer
    // performer — a cheap "similar" proxy), and yields a weighted target set. The recommender then surfaces
    // matching content and blends a steer-match score in, leaning the feed toward that neighbourhood while
    // keeping the user's broader taste.

    /// <summary>A weighted target set the feed leans toward: steer terms (weight 1) plus co-occurring
    /// tags / co-appearing performers (fractional). <see cref="IsEmpty"/> when nothing resolved.</summary>
    public sealed record SteerProfile(Dictionary<int, double> Tags, Dictionary<int, double> Performers)
    {
        public bool IsEmpty => Tags.Count == 0 && Performers.Count == 0;
    }

    private static (List<int> ids, List<string> names) SplitTerms(string? terms)
    {
        var ids = new List<int>(); var names = new List<string>();
        if (string.IsNullOrWhiteSpace(terms)) return (ids, names);
        foreach (var raw in terms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(raw, out var id)) ids.Add(id); else names.Add(raw);
        return (ids, names);
    }

    public static async Task<SteerProfile> BuildSteerProfileAsync(DbContext db, string? steerTags, string? steerPerformers, CancellationToken ct)
    {
        var (tagIds, tagNames) = SplitTerms(steerTags);
        var (perfIds, perfNames) = SplitTerms(steerPerformers);
        var tagSet = new HashSet<int>(tagIds);
        var perfSet = new HashSet<int>(perfIds);
        foreach (var name in tagNames)
        {
            var lower = name.ToLowerInvariant();
            var m = await db.Set<Tag>().AsNoTracking().IgnoreQueryFilters()
                .Where(t => t.Name.ToLower() == lower || t.Name.ToLower().Contains(lower))
                .OrderBy(t => t.Name.Length).Select(t => t.Id).FirstOrDefaultAsync(ct);
            if (m != 0) tagSet.Add(m);
        }
        foreach (var name in perfNames)
        {
            var lower = name.ToLowerInvariant();
            var m = await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.Name.ToLower() == lower || p.Name.ToLower().Contains(lower))
                .OrderBy(p => p.Name.Length).Select(p => p.Id).FirstOrDefaultAsync(ct);
            if (m != 0) perfSet.Add(m);
        }

        var tags = tagSet.ToDictionary(t => t, _ => 1.0);
        var perfs = perfSet.ToDictionary(p => p, _ => 1.0);

        if (tagSet.Count > 0)
        {
            var seedTags = tagSet.ToList();
            var sample = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters()
                .Where(v => v.TagIds.Any(t => seedTags.Contains(t))).Select(v => v.TagIds).Take(800).ToListAsync(ct);
            var co = new Dictionary<int, int>();
            foreach (var arr in sample) foreach (var t in arr) if (!tags.ContainsKey(t)) co[t] = co.GetValueOrDefault(t) + 1;
            var max = Math.Max(1, co.Values.DefaultIfEmpty(0).Max());
            foreach (var kv in co.OrderByDescending(k => k.Value).Take(10)) tags[kv.Key] = 0.6 * kv.Value / max;
        }
        if (perfSet.Count > 0)
        {
            var seedPerfs = perfSet.ToList();
            var sample = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters()
                .Where(v => v.PerformerIds.Any(p => seedPerfs.Contains(p))).Select(v => v.PerformerIds).Take(800).ToListAsync(ct);
            var co = new Dictionary<int, int>();
            foreach (var arr in sample) foreach (var p in arr) if (!perfs.ContainsKey(p)) co[p] = co.GetValueOrDefault(p) + 1;
            var max = Math.Max(1, co.Values.DefaultIfEmpty(0).Max());
            foreach (var kv in co.OrderByDescending(k => k.Value).Take(8)) perfs[kv.Key] = 0.6 * kv.Value / max;
        }
        return new SteerProfile(tags, perfs);
    }

    /// <summary>Unseen videos that match the steer (any steer/expanded tag or performer) — added to the
    /// candidate pool so steered content surfaces even when it sits outside the user's visual taste niches.</summary>
    public static async Task<List<int>> SteerMatchingVideosAsync(DbContext db, SteerProfile steer, ISet<int> exclude, int take, CancellationToken ct)
    {
        if (steer.IsEmpty || take <= 0) return new();
        var tagIds = steer.Tags.Keys.ToList();
        var perfIds = steer.Performers.Keys.ToList();
        var ids = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters()
            .Where(v => v.TagIds.Any(t => tagIds.Contains(t)) || v.PerformerIds.Any(p => perfIds.Contains(p)))
            .OrderByDescending(v => v.Id).Select(v => v.Id).Take(take * 3).ToListAsync(ct);
        return ids.Where(id => !exclude.Contains(id)).Take(take).ToList();
    }

    /// <summary>How well a candidate matches the steer (0..1) — summed target weights over its tags/performers,
    /// squashed so a couple of strong matches saturate. 0 when there's no steer.</summary>
    public static double SteerScore(SteerProfile steer, int[] tagIds, int[] performerIds)
    {
        if (steer.IsEmpty) return 0;
        double s = 0;
        foreach (var t in tagIds) if (steer.Tags.TryGetValue(t, out var w)) s += w;
        foreach (var p in performerIds) if (steer.Performers.TryGetValue(p, out var w)) s += w;
        return s <= 0 ? 0 : 1.0 - Math.Exp(-s);   // diminishing returns; ~0.63 at s=1, ~0.86 at s=2
    }

    /// <summary>Incremental MMR over (id, score), returning ids in diversified order.</summary>
    public static List<int> MmrIds(IReadOnlyList<(int id, double score)> scored, Dictionary<int, float[]> vectors, double novelty, int take)
    {
        var pool = scored.OrderByDescending(s => s.score).Take(Math.Min(scored.Count, Math.Max(take, 1) * 3)).ToList();
        var target = Math.Min(take, pool.Count);
        if (pool.Count == 0) return [];
        var lambda = Math.Clamp(novelty, 0, 1);
        if (lambda <= 0) return pool.Take(target).Select(p => p.id).ToList();

        var used = new bool[pool.Count];
        var maxSim = new double[pool.Count];
        var selected = new List<int>(target) { pool[0].id };
        used[0] = true;

        while (selected.Count < target)
        {
            vectors.TryGetValue(selected[^1], out var lastVec);
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i] || lastVec is null) continue;
                if (vectors.TryGetValue(pool[i].id, out var pv)) maxSim[i] = Math.Max(maxSim[i], Vectors.Cosine(pv, lastVec));
            }
            int bestIdx = -1; double best = double.NegativeInfinity;
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                var mmr = (1 - lambda) * pool[i].score - lambda * maxSim[i];
                if (mmr > best) { best = mmr; bestIdx = i; }
            }
            if (bestIdx < 0) break;
            used[bestIdx] = true;
            selected.Add(pool[bestIdx].id);
        }
        return selected;
    }
}
