using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Recommendations.Abstractions;
using Recommendations.Toolkit;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Deep;

/// <summary>
/// The "deep" recommender — blends several signals for the best recommendations we can get without a
/// learned model: multiple visual taste centroids (k-means niches), audio (voice) similarity, TF-IDF tag
/// affinity, performer affinity (direct preference ∪ propagated from liked content), and studio affinity.
/// Each signal has a user-tunable weight; signals are normalized across candidates, blended over the
/// signals actually present (missing data never drags a score down), then MMR-diversified. Every result
/// explains which tags/performers/studio drove it, with affinities and why. Videos only; brute-force KNN.
/// </summary>
public sealed class DeepRecommender(IServiceScopeFactory scopeFactory) : IRecommender, ITasteProfile, ITrainingGround
{
    public const string RecommenderId = "cove.community.recommendations.deep";
    private const string VisualFeatureFamily = EmbeddingKinds.VisualFeatureFamily;
    private const string VisualSemanticFamily = EmbeddingKinds.VisualSemanticFamily;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Deep blend (visual + audio + tags + performers)",
        "Blends visual taste niches, audio similarity, TF-IDF tags, and performer/studio affinity, with adjustable weights and diversity. The most nuanced recommender — improves as you engage more.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems, RecommendationContext.Training],
        SourceEntityTypes: ["video"],
        TargetEntityTypes: ["video"],
        Knobs:
        [
            new RecommenderKnob("visualWeight", "Visual weight", 0, 1, 0.40, "How much visual-embedding similarity matters."),
            new RecommenderKnob("featureWeight", "Visual: feature", 0, 1, 0.70, "Pure-visual similarity (primary)."),
            new RecommenderKnob("semanticWeight", "Visual: semantic", 0, 1, 0.30, "Semantic/related similarity (complementary)."),
            new RecommenderKnob("audioWeight", "Audio weight", 0, 1, 0.15, "How much audio (voice) similarity matters."),
            new RecommenderKnob("tagWeight", "Tag weight", 0, 1, 0.20, "How much tag affinity matters."),
            new RecommenderKnob("performerWeight", "Performer weight", 0, 1, 0.20, "How much performer affinity matters."),
            new RecommenderKnob("studioWeight", "Studio weight", 0, 1, 0.05, "How much studio affinity matters."),
            new RecommenderKnob("noveltySimilarityBalance", "Novelty vs similarity", 0, 1, 0.30, "Higher diversifies results away from near-duplicates."),
            new RecommenderKnob("steerStrength", "Steer strength", 0, 1, 0.6, "How hard the mood steer (tags/performers) pulls the feed vs your overall taste."),
        ]);

    public RecommenderDescriptor Describe() => Descriptor;

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.TargetEntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return Empty("videos only");

        var w = new Weights(
            Knob(request.Knobs, "visualWeight", 0.40),
            Knob(request.Knobs, "audioWeight", 0.15),
            Knob(request.Knobs, "tagWeight", 0.20),
            Knob(request.Knobs, "performerWeight", 0.20),
            Knob(request.Knobs, "studioWeight", 0.05));
        var novelty = Knob(request.Knobs, "noveltySimilarityBalance", 0.30);
        var steerStrength = Knob(request.Knobs, "steerStrength", 0.6);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // One engaged fetch reused for seen/liked AND the profile build.
        var engaged = await request.Core.Preference.GetTopLikedAsync(request.UserId, "video", 1500, null, cancellationToken);
        var msEngaged = sw.ElapsedMilliseconds;
        var seen = engaged.Select(e => e.Entity.EntityId).ToHashSet();
        var liked = engaged.Where(e => e.Score > 0.1).Take(60).ToList();
        if (liked.Count == 0)
            return Empty("no liked videos yet");

        var buildTimings = new long[3];
        var profile = await BuildProfileAsync(request.UserId, request.Core, db, repo, engaged, cancellationToken, buildTimings);
        if (profile.VisualCentroids.Count == 0)
            return Empty("liked videos have no visual embeddings");
        var msProfile = sw.ElapsedMilliseconds; var msBuild = msProfile - msEngaged; sw.Restart();

        // Candidate generation: blended visual KNN over BOTH spaces (feature primary + semantic), from each
        // centroid pair, or from the seed for SimilarToEntity.
        var (wF, wS) = (Knob(request.Knobs, "featureWeight", 0.70), Knob(request.Knobs, "semanticWeight", 0.30));
        // Soft steer (mood): bias toward steer tags/performers + their co-occurring neighbours. GlobalFeed only.
        RecHelpers.SteerProfile? steer = request.Context != RecommendationContext.SimilarToEntity && (request.SteerTags is not null || request.SteerPerformers is not null)
            ? await RecHelpers.BuildSteerProfileAsync(db, request.SteerTags, request.SteerPerformers, cancellationToken)
            : null;
        var visualVecs = new Dictionary<int, float[]>();
        var visualScore = new Dictionary<int, double>();
        if (request.Context == RecommendationContext.SimilarToEntity && request.Seed is { } s && s.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            var seedPair = (await GetVisualPairAsync(repo, [s.EntityId], cancellationToken)).GetValueOrDefault(s.EntityId);
            if (seedPair.Feat is null && seedPair.Sem is null)
                return Empty("seed has no visual embedding");
            var candPair = await KnnUnionVecs(search, seedPair.Feat, seedPair.Sem, request.Limit * 6 + 50, cancellationToken);
            foreach (var (id, cv) in candPair)
            { visualScore[id] = Math.Clamp(Vectors.BlendedCosine(cv.Feat, cv.Sem, seedPair.Feat, seedPair.Sem, wF, wS), 0, 1); var pv = cv.Feat ?? cv.Sem; if (pv is not null) visualVecs[id] = pv; }
            seen = [s.EntityId];
        }
        else
        {
            // Budget the candidate pool to the FEED size, not niches×limit×3 — with many niches the old
            // per-niche pull ballooned to ~1.8k candidates, and fetching all their vectors + scoring them was
            // the dominant cost. Spread a fixed budget (~MMR pool size) across the niches instead.
            int want = (request.Limit + request.Offset) * 6;  // ~MMR pool size
            int perNicheK = Math.Clamp(want / Math.Max(1, profile.VisualCentroids.Count) + 15, 30, 200);
            // Reuse the vectors the KNN already returned — no second fetch of every candidate's embedding.
            var candPair = new Dictionary<int, (float[]? Feat, float[]? Sem)>();
            foreach (var pair in profile.VisualCentroids)
                foreach (var kv in await KnnUnionVecs(search, pair.Feat, pair.Sem, perNicheK, cancellationToken))
                    candPair[kv.Key] = candPair.TryGetValue(kv.Key, out var ex) ? (ex.Feat ?? kv.Value.Feat, ex.Sem ?? kv.Value.Sem) : kv.Value;
            foreach (var (id, cv) in candPair)
            {
                double best = 0;
                foreach (var pair in profile.VisualCentroids) { var sc = Vectors.BlendedCosine(cv.Feat, cv.Sem, pair.Feat, pair.Sem, wF, wS); if (sc > best) best = sc; }
                visualScore[id] = Math.Clamp(best, 0, 1); var pv = cv.Feat ?? cv.Sem; if (pv is not null) visualVecs[id] = pv;
            }

            // Pull steer-matching videos into the pool too — they may sit outside your visual niches, and the
            // steer score below is what lifts the on-mood ones to the top.
            if (steer is { IsEmpty: false })
            {
                var steerIds = await RecHelpers.SteerMatchingVideosAsync(db, steer, seen, request.Limit * 4 + 100, cancellationToken);
                var steerPair = await GetVisualPairAsync(repo, steerIds.Where(id => !visualScore.ContainsKey(id)).ToList(), cancellationToken);
                foreach (var id in steerIds)
                {
                    if (visualScore.ContainsKey(id) || !steerPair.TryGetValue(id, out var cv)) continue;
                    double best = 0;
                    foreach (var pair in profile.VisualCentroids) { var sc = Vectors.BlendedCosine(cv.Feat, cv.Sem, pair.Feat, pair.Sem, wF, wS); if (sc > best) best = sc; }
                    visualScore[id] = Math.Clamp(best, 0, 1); var pv = cv.Feat ?? cv.Sem; if (pv is not null) visualVecs[id] = pv;
                }
            }
        }

        var msKnn = sw.ElapsedMilliseconds; sw.Restart();
        var candidateIds = visualScore.Keys.Where(id => !seen.Contains(id)).ToList();
        if (candidateIds.Count == 0)
            return Empty("no unseen candidates");

        var meta = await FetchVideoMetaAsync(db, candidateIds, cancellationToken);
        var audioVecs = await GetVectorsAsync(repo, candidateIds, EmbeddingModality.Audio, null, cancellationToken);

        // Score each candidate across signals.
        var cands = new List<Cand>();
        foreach (var id in candidateIds)
        {
            if (!meta.TryGetValue(id, out var m))
                continue;
            var c = new Cand { Id = id, StudioId = m.StudioId };
            c.Visual = visualScore.GetValueOrDefault(id);
            if (audioVecs.TryGetValue(id, out var av) && profile.AudioCentroid is { } ac) { c.Audio = Math.Max(0, Vectors.Cosine(av, ac)); c.AudioPresent = true; }
            c.TagPresent = m.TagIds.Length > 0;
            c.PerfPresent = m.PerformerIds.Length > 0;
            c.StudioPresent = m.StudioId is not null;
            c.TagRaw = m.TagIds.Sum(t => profile.Tags.GetValueOrDefault(t)?.Affinity ?? 0);
            c.Perf = m.PerformerIds.Select(p => profile.Performers.GetValueOrDefault(p)?.Affinity ?? 0).OrderByDescending(Math.Abs).FirstOrDefault();
            c.Studio = m.StudioId is { } sid ? profile.Studios.GetValueOrDefault(sid) : 0;
            c.TopTags = m.TagIds.Where(t => profile.Tags.ContainsKey(t)).OrderByDescending(t => profile.Tags[t].Affinity).Take(5).ToList();
            c.TopPerfs = m.PerformerIds.Where(p => profile.Performers.ContainsKey(p)).OrderByDescending(p => profile.Performers[p].Affinity).Take(3).ToList();
            c.Steer = steer is { IsEmpty: false } ? RecHelpers.SteerScore(steer, m.TagIds, m.PerformerIds) : 0;
            cands.Add(c);
        }
        if (cands.Count == 0)
            return Empty("candidates have no metadata");

        var maxTag = Math.Max(1e-9, cands.Max(c => Math.Abs(c.TagRaw)));
        foreach (var c in cands)
        {
            c.Tag = c.TagRaw / maxTag;
            // FIXED-denominator blend (matching the other recommenders): every signal divides by the SAME
            // denominator whether or not it fired. So adding a positive signal always RAISES the score — a
            // video that matches visually AND on liked tags/performers can never score below one that only
            // matches visually. Missing signals contribute 0 (neutral); negative affinities (dislikes) pull
            // the score down. The old present-only average diluted a strong signal with weaker-but-present
            // ones, which made single-signal videos outrank better-corroborated ones.
            double den = Math.Max(1e-9, w.Visual + w.Audio + w.Tag + w.Performer + w.Studio);
            double num = w.Visual * c.Visual
                       + (c.AudioPresent ? w.Audio * c.Audio : 0)
                       + (c.TagPresent ? w.Tag * c.Tag : 0)
                       + (c.PerfPresent ? w.Performer * c.Perf : 0)
                       + (c.StudioPresent ? w.Studio * c.Studio : 0);
            c.Score = Math.Clamp(num / den, 0, 1);
            // Soft steer: lean the score toward the requested mood while keeping taste in the mix. A candidate
            // with no steer match keeps (1−strength) of its taste score; a strong on-mood match rises to the top.
            if (steer is { IsEmpty: false })
                c.Score = Math.Clamp((1 - steerStrength) * c.Score + steerStrength * c.Steer, 0, 1);
            var present = (c.AudioPresent ? 1 : 0) + (c.TagPresent ? 1 : 0) + (c.PerfPresent ? 1 : 0) + (c.StudioPresent ? 1 : 0) + 1;
            c.Confidence = Math.Clamp(Math.Min(1.0, liked.Count / 15.0) * (0.4 + 0.6 * present / 5.0), 0, 1);
        }

        // MMR diversification, then page.
        var ordered = Mmr(cands, visualVecs, novelty, request.Limit + request.Offset)
            .Skip(request.Offset).Take(request.Limit).ToList();

        // Fetch names for the explanation of just the returned page.
        var names = await FetchNamesAsync(db,
            ordered.SelectMany(c => c.TopTags),
            ordered.SelectMany(c => c.TopPerfs),
            ordered.Where(c => c.StudioId is not null).Select(c => c.StudioId!.Value),
            cancellationToken);

        var items = ordered.Select(c => new ItemScore("video", c.Id, c.Score, c.Confidence, Explain(c, profile, names, w))).ToList();
        return new RecommendationResult(items, Diagnostics: new Dictionary<string, object>
        {
            ["recommender"] = RecommenderId,
            ["centroids"] = profile.VisualCentroids.Count,
            ["candidates"] = candidateIds.Count,
            ["ms_profile"] = msProfile,
            ["ms_engaged"] = msEngaged,
            ["ms_build"] = msBuild,
            ["ms_build_embed"] = buildTimings[0],
            ["ms_build_meta"] = buildTimings[1],
            ["ms_build_attr"] = buildTimings[2],
            ["ms_knn"] = msKnn,
            ["ms_rest"] = sw.ElapsedMilliseconds,
        });
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();

        var engaged = await request.Core.Preference.GetTopLikedAsync(request.UserId, "video", 1500, null, cancellationToken);
        var liked = engaged.Where(e => e.Score > 0.1).Take(60).ToList();
        var profile = await BuildProfileAsync(request.UserId, request.Core, db, repo, engaged, cancellationToken);
        var featCentroid = Vectors.WeightedCentroid(profile.VisualCentroids.Select(c => (c.Feat, 1.0)));
        var semCentroid = Vectors.WeightedCentroid(profile.VisualCentroids.Select(c => (c.Sem, 1.0)));

        var ids = request.Items.Where(i => i.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase)).Select(i => i.EntityId).ToList();
        var assets = await GetVisualPairAsync(repo, ids, cancellationToken);
        var meta = await FetchVideoMetaAsync(db, ids, cancellationToken);
        var maxTag = Math.Max(1e-9, meta.Values.Select(m => Math.Abs(m.TagIds.Sum(t => profile.Tags.GetValueOrDefault(t)?.Affinity ?? 0))).DefaultIfEmpty(0).Max());

        return request.Items.Select(item =>
        {
            if (!meta.TryGetValue(item.EntityId, out var m))
                return new ItemScore(item.EntityType, item.EntityId, 0, 0);
            var visual = assets.TryGetValue(item.EntityId, out var v) ? Math.Max(0, Vectors.BlendedCosine(v.Feat, v.Sem, featCentroid, semCentroid, 0.70, 0.30)) : 0;
            var tag = m.TagIds.Sum(t => profile.Tags.GetValueOrDefault(t)?.Affinity ?? 0) / maxTag;
            var perf = m.PerformerIds.Select(p => profile.Performers.GetValueOrDefault(p)?.Affinity ?? 0).OrderByDescending(Math.Abs).FirstOrDefault();
            var final = 0.25 * perf + 0.75 * (0.6 * visual + 0.3 * tag);
            return new ItemScore(item.EntityType, item.EntityId, Math.Clamp(final, 0, 1), Math.Min(1.0, liked.Count / 15.0));
        }).ToList();
    }

    // ── ITasteProfile: the derived tag/performer/studio affinities, with detail ──
    public async Task<TasteProfile> GetProfileAsync(int userId, ICoreServices core, int topN = 60, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();
        var engaged = await core.Preference.GetTopLikedAsync(userId, "video", 1500, null, cancellationToken);
        var liked = engaged.Where(e => e.Score > 0.1).Take(60).ToList();
        var profile = await BuildProfileAsync(userId, core, db, repo, engaged, cancellationToken);

        // Strongest by MAGNITUDE so dislikes (negative affinity) surface too, then likes-first / dislikes-last.
        // TODO: evidence-gate negatives here too (needs TagInfo/PerfInfo to carry DislikedCount + Source).
        var topTags = profile.Tags.OrderByDescending(kv => Math.Abs(kv.Value.Affinity)).Take(topN).OrderByDescending(kv => kv.Value.Affinity).ToList();
        var topPerfs = profile.Performers.OrderByDescending(kv => Math.Abs(kv.Value.Affinity)).Take(topN).OrderByDescending(kv => kv.Value.Affinity).ToList();
        var topStudios = profile.Studios.OrderByDescending(kv => Math.Abs(kv.Value)).Take(Math.Max(15, topN / 2)).OrderByDescending(kv => kv.Value).ToList();
        var names = await FetchNamesAsync(db, topTags.Select(kv => kv.Key), topPerfs.Select(kv => kv.Key), topStudios.Select(kv => kv.Key), cancellationToken);

        var tagEntities = topTags.Select(kv =>
        {
            var info = kv.Value;
            var rare = info.Idf >= 3 ? ", rare" : info.Idf < 0.7 ? ", common" : "";
            return new ProfileEntity("tag", kv.Key, names.Tag(kv.Key), info.Affinity, $"in {info.LikedCount} liked{rare}");
        }).ToList();
        var perfEntities = topPerfs.Select(kv =>
        {
            var info = kv.Value;
            var why = info.Source == "direct" ? "you rate/favorite them" : $"in {info.LikedCount} of your liked";
            return new ProfileEntity("performer", kv.Key, names.Perf(kv.Key), info.Affinity, why);
        }).ToList();
        var studioEntities = topStudios.Select(kv => new ProfileEntity("studio", kv.Key, names.Studio(kv.Key), kv.Value)).ToList();

        return new TasteProfile(tagEntities, perfEntities, studioEntities, $"Deep blend profile from {liked.Count} liked videos.");
    }

    // ── ITrainingGround: active learning — delegated to the shared toolkit picker ──
    public async Task<TrainingProbeSet> GetProbesAsync(int userId, ICoreServices core, int limit, string mediaType, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        return await RecHelpers.BuildTrainingProbesAsync(core, sp.GetRequiredService<IEmbeddingRepository>(),
            sp.GetRequiredService<IEmbeddingService>(), sp.GetRequiredService<DbContext>(), userId, limit, mediaType, cancellationToken);
    }

    // ── Explanation ──────────────────────────────────────────────────────────

    private static Explanation Explain(Cand c, Profile p, Names names, Weights w)
    {
        // Same FIXED denominator as scoring, so the factor contributions below sum to the displayed score.
        var den = Math.Max(1e-9, w.Visual + w.Audio + w.Tag + w.Performer + w.Studio);
        var factors = new List<ExplanationFactor>
        {
            new("visual", "Visual match", w.Visual * c.Visual / den, $"{c.Visual * 100:0}% similar to your taste"),
        };
        if (c.AudioPresent)
            factors.Add(new("audio", "Audio match", w.Audio * c.Audio / den, $"{c.Audio * 100:0}% voice/audio similar"));
        if (c.TagPresent && c.TopTags.Count > 0)
        {
            var detail = string.Join(", ", c.TopTags.Select(t =>
            {
                var info = p.Tags[t];
                var rare = info.Idf >= 3 ? ", rare" : info.Idf < 0.7 ? ", common" : "";
                return $"{names.Tag(t)} {info.Affinity:0.00} (in {info.LikedCount} liked{rare})";
            }));
            factors.Add(new("tags", "Tags", w.Tag * c.Tag / den, detail));
        }
        if (c.PerfPresent && c.TopPerfs.Count > 0)
        {
            var detail = string.Join(", ", c.TopPerfs.Select(pid =>
            {
                var info = p.Performers[pid];
                var why = info.Source == "direct" ? "you rate/favorite them" : $"in {info.LikedCount} of your liked";
                return $"{names.Perf(pid)} {info.Affinity:0.00} ({why})";
            }));
            factors.Add(new("performers", "Performers", w.Performer * c.Perf / den, detail));
        }
        if (c.StudioPresent && c.StudioId is { } sid && c.Studio > 0)
            factors.Add(new("studio", "Studio", w.Studio * c.Studio / den, $"{names.Studio(sid)} {c.Studio:0.00}"));

        return new Explanation(Band(c.Score), factors);
    }

    // ── Taste profile ────────────────────────────────────────────────────────

    private sealed record TagInfo(double Affinity, int LikedCount, double Idf);
    private sealed record PerfInfo(double Affinity, string Source, int LikedCount);
    private sealed record Profile(
        List<(float[]? Feat, float[]? Sem)> VisualCentroids,
        float[]? AudioCentroid,
        Dictionary<int, TagInfo> Tags,
        Dictionary<int, PerfInfo> Performers,
        Dictionary<int, double> Studios);

    private async Task<Profile> BuildProfileAsync(int userId, ICoreServices core, DbContext db, IEmbeddingRepository repo, IReadOnlyList<ScoredEntity> engaged, CancellationToken ct, long[]? timings = null)
    {
        var psw = System.Diagnostics.Stopwatch.StartNew();
        // Kick off the heavy preference-scorer lookups (engaged images + direct tag/performer/studio prefs) up
        // front — each opens its own DB scope, so they run concurrently with the embedding/metadata fetches
        // below instead of adding their latency sequentially at the end.
        var engagedImagesTask = core.Preference.GetTopLikedAsync(userId, "image", 1500, null, ct);
        var directPrefsTask = RecHelpers.FetchDirectPrefsAsync(core, userId, ct);

        // Derive both views from the SINGLE engaged fetch passed in (no second GetTopLikedAsync round-trip,
        // which previously re-loaded + re-scored the user's whole engagement).
        var liked = engaged.Where(e => e.Score > 0.1).Take(60).ToList();
        var likedIds = liked.Select(e => e.Entity.EntityId).ToList();
        var likedScore = liked.ToDictionary(e => e.Entity.EntityId, e => Math.Max(0, e.Score));

        // Build visual taste niches. k-means runs in ONE space (feature/semantic differ in dim): cluster in
        // the better-covered space (prefer feature); each niche keeps BOTH centroids so scoring blends.
        var visual = await GetVisualPairAsync(repo, likedIds, ct);
        var likedVis = liked.Where(e => visual.ContainsKey(e.Entity.EntityId)).ToList();
        int featCount = likedVis.Count(e => visual[e.Entity.EntityId].Feat is not null);
        int semCount = likedVis.Count(e => visual[e.Entity.EntityId].Sem is not null);
        bool onFeature = featCount >= semCount;
        float[]? Chosen((float[]? Feat, float[]? Sem) v) => onFeature ? v.Feat : v.Sem;
        var clusterSet = likedVis.Where(e => Chosen(visual[e.Entity.EntityId]) is not null).ToList();
        var pts = clusterSet.Select(e => Chosen(visual[e.Entity.EntityId])!).ToList();
        var wts = clusterSet.Select(e => Math.Max(0.01, e.Score)).ToList();
        var rawCentroids = pts.Count == 0 ? new List<float[]>() : Vectors.KMeans(pts, wts, Math.Clamp(pts.Count / 8, 1, 12));
        var assignment = pts.Select(p => NearestCentroidIndex(rawCentroids, p)).ToList();
        var visualCentroids = new List<(float[]? Feat, float[]? Sem)>();
        for (var c = 0; c < rawCentroids.Count; c++)
        {
            var memberIdx = Enumerable.Range(0, clusterSet.Count).Where(i => assignment[i] == c).ToList();
            if (memberIdx.Count == 0) continue;
            var featC = Vectors.WeightedCentroid(memberIdx.Select(i => (visual[clusterSet[i].Entity.EntityId].Feat, Math.Max(0.01, clusterSet[i].Score))));
            var semC = Vectors.WeightedCentroid(memberIdx.Select(i => (visual[clusterSet[i].Entity.EntityId].Sem, Math.Max(0.01, clusterSet[i].Score))));
            visualCentroids.Add((featC, semC));
        }

        var audio = await GetVectorsAsync(repo, likedIds, EmbeddingModality.Audio, null, ct);
        var audioCentroid = Vectors.WeightedCentroid(liked.Select(e => (audio.GetValueOrDefault(e.Entity.EntityId), likedScore.GetValueOrDefault(e.Entity.EntityId))));
        var msEmbed = psw.ElapsedMilliseconds;

        // Tag/performer/studio affinities by CONTRASTIVE attribution (one joint ridge fit over SIGNED
        // engagement, computed by the shared toolkit) — co-occurrence is partialled out so each attribute is
        // credited for its own effect, not its neighbours'. Direct entity preference (incl. negative)
        // overrides the attributed estimate.
        var engagedSigned = engaged.Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var signedIds = engagedSigned.Select(e => e.Entity.EntityId).ToList();
        var signedScored = engagedSigned.Select(e => (e.Entity.EntityId, e.Score)).ToList();
        var signedMeta = await RecHelpers.FetchVideoMetaAsync(db, signedIds, ct);
        // Fold the user's engaged IMAGES into the attribution (offset ids, shared attribute vocabulary): a
        // rated image is an unambiguous like/dislike of a single frame, so it teaches tag/performer/studio
        // preferences fast — and those drive video recs too.
        var (mergedScored, mergedMeta) = await RecHelpers.MergeEngagedImagesAsync(core, db, userId, signedScored, signedMeta, ct, await engagedImagesTask);
        var totalVideos = await RecHelpers.TotalVideosAsync(db, ct);
        var tagCounts = await RecHelpers.TagVideoCountsAsync(db, mergedMeta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var studioCounts = await RecHelpers.StudioVideoCountsAsync(db, mergedMeta.Values.Where(m => m.StudioId.HasValue).Select(m => m.StudioId!.Value).Distinct().ToList(), ct);
        var perfCounts = await RecHelpers.PerformerVideoCountsAsync(db, mergedMeta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList(), ct);

        // 2b: explicit content/performers aspect ratings supervise those components directly.
        var aspectTargets = await RecHelpers.FetchAspectTargetsAsync(db, userId, signedIds, ct);
        // 2c: visual-fit covariate — each signed video's similarity to its nearest taste niche, so attribute
        // weights are net of "it just looks like your taste". Reuses the niches built above.
        IReadOnlyDictionary<int, double>? visualFit = null;
        if (visualCentroids.Count > 0)
        {
            // Only the strongest-signal videos need the covariate (it just nets visual out of the attribute
            // weights); capping the vector fetch keeps the profile build snappy on large engagement sets.
            var visualFitIds = engagedSigned.OrderByDescending(e => Math.Abs(e.Score)).Take(300).Select(e => e.Entity.EntityId).ToList();
            var signedVisual = await GetVisualPairAsync(repo, visualFitIds, ct);
            visualFit = engagedSigned.Where(e => signedVisual.ContainsKey(e.Entity.EntityId)).ToDictionary(
                e => e.Entity.EntityId,
                e =>
                {
                    var cv = signedVisual[e.Entity.EntityId];
                    double best = 0;
                    foreach (var pair in visualCentroids) { var sc = Vectors.BlendedCosine(cv.Feat, cv.Sem, pair.Feat, pair.Sem, 0.70, 0.30); if (sc > best) best = sc; }
                    return best;
                });
        }

        var msMeta = psw.ElapsedMilliseconds - msEmbed;
        var tagChildren = await RecHelpers.FetchTagChildrenAsync(db, ct);
        var attribution = await RecHelpers.AttributeAttributionAsync(core, userId, mergedScored, mergedMeta, tagCounts, studioCounts, perfCounts, totalVideos, null, ct, aspectTargets, visualFit, await directPrefsTask, tagChildren);
        if (timings is { Length: >= 3 }) { timings[0] = msEmbed; timings[1] = msMeta; timings[2] = psw.ElapsedMilliseconds - msEmbed - msMeta; }

        var tags = attribution.Tags.ToDictionary(kv => kv.Key, kv => new TagInfo(kv.Value.Affinity, kv.Value.LikedCount, kv.Value.Idf));
        var performers = attribution.Performers.ToDictionary(kv => kv.Key, kv => new PerfInfo(kv.Value.Affinity, kv.Value.Source, kv.Value.LikedCount));
        var studios = attribution.Studios.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);

        return new Profile(visualCentroids, audioCentroid, tags, performers, studios);
    }

    // ── Embedding + metadata helpers ─────────────────────────────────────────

    private static async Task<HashSet<int>> KnnUnion(IEmbeddingService search, float[]? featQ, float[]? semQ, int k, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (featQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(featQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualFeatureFamily, SectionIndex = 0 }, ct))
                ids.Add(h.Embedding.HostId);
        if (semQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(semQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualSemanticFamily, SectionIndex = 0 }, ct))
                ids.Add(h.Embedding.HostId);
        return ids;
    }

    /// <summary>Like <see cref="KnnUnion"/> but KEEPS the vectors the KNN already returned (feature-KNN fills
    /// Feat, semantic-KNN fills Sem) — so the caller can score candidates without re-fetching every embedding.
    /// A candidate matched in only one space keeps just that space; BlendedCosine tolerates the null.</summary>
    private static async Task<Dictionary<int, (float[]? Feat, float[]? Sem)>> KnnUnionVecs(IEmbeddingService search, float[]? featQ, float[]? semQ, int k, CancellationToken ct)
    {
        var map = new Dictionary<int, (float[]? Feat, float[]? Sem)>();
        if (featQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(featQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualFeatureFamily, SectionIndex = 0 }, ct))
            {
                var cur = map.GetValueOrDefault(h.Embedding.HostId);
                map[h.Embedding.HostId] = (h.Embedding.Vector.ToArray(), cur.Sem);
            }
        if (semQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(semQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualSemanticFamily, SectionIndex = 0 }, ct))
            {
                var cur = map.GetValueOrDefault(h.Embedding.HostId);
                map[h.Embedding.HostId] = (cur.Feat, h.Embedding.Vector.ToArray());
            }
        return map;
    }

    /// <summary>Both asset-level visual vectors (feature + semantic) per video id.</summary>
    private static async Task<Dictionary<int, (float[]? Feat, float[]? Sem)>> GetVisualPairAsync(IEmbeddingRepository repo, IReadOnlyList<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, (float[]? Feat, float[]? Sem)>();
        if (videoIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter { HostType = EmbeddingHostType.Video, HostIds = videoIds, Modality = EmbeddingModality.Visual, SectionIndex = 0 }, ct);
        var feat = new Dictionary<int, float[]>(); var sem = new Dictionary<int, float[]>();
        foreach (var e in rows.Where(e => e.SectionIndex == 0))
        {
            if (e.KindFamily == VisualFeatureFamily) feat[e.HostId] = e.Vector.ToArray();
            else if (e.KindFamily == VisualSemanticFamily) sem[e.HostId] = e.Vector.ToArray();
        }
        foreach (var id in feat.Keys.Union(sem.Keys))
            result[id] = (feat.GetValueOrDefault(id), sem.GetValueOrDefault(id));
        return result;
    }

    private static int NearestCentroidIndex(List<float[]> centroids, float[] p)
    {
        int best = 0; double bestSim = double.NegativeInfinity;
        for (var i = 0; i < centroids.Count; i++) { var s = Vectors.Cosine(p, centroids[i]); if (s > bestSim) { bestSim = s; best = i; } }
        return best;
    }

    private static async Task<Dictionary<int, float[]>> GetVectorsAsync(IEmbeddingRepository repo, IReadOnlyList<int> videoIds, EmbeddingModality modality, string? kindFamily, CancellationToken ct)
    {
        var result = new Dictionary<int, float[]>();
        if (videoIds.Count == 0) return result;
        var rows = await repo.FindAsync(new EmbeddingFilter
        {
            HostType = EmbeddingHostType.Video,
            HostIds = videoIds,
            Modality = modality,
            KindFamily = kindFamily,
            SectionIndex = 0,
        }, ct);
        foreach (var e in rows.Where(e => e.SectionIndex == 0))
            result[e.HostId] = e.Vector.ToArray();
        return result;
    }

    private sealed record VideoMeta(int Id, int[] TagIds, int[] PerformerIds, int? StudioId);

    private static async Task<Dictionary<int, VideoMeta>> FetchVideoMetaAsync(DbContext db, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var rows = await db.Set<Video>().AsNoTracking().IgnoreQueryFilters()
            .Where(v => ids.Contains(v.Id)).Select(v => new VideoMeta(v.Id, v.TagIds, v.PerformerIds, v.StudioId)).ToListAsync(ct);
        return rows.ToDictionary(m => m.Id);
    }

    private sealed record Names(Dictionary<int, string> Tags, Dictionary<int, string> Performers, Dictionary<int, string> Studios)
    {
        public string Tag(int id) => Tags.GetValueOrDefault(id, $"tag#{id}");
        public string Perf(int id) => Performers.GetValueOrDefault(id, $"performer#{id}");
        public string Studio(int id) => Studios.GetValueOrDefault(id, $"studio#{id}");
    }

    private static async Task<Names> FetchNamesAsync(DbContext db, IEnumerable<int> tagIds, IEnumerable<int> perfIds, IEnumerable<int> studioIds, CancellationToken ct)
    {
        var t = tagIds.Distinct().ToList();
        var p = perfIds.Distinct().ToList();
        var s = studioIds.Distinct().ToList();
        var tags = t.Count == 0 ? new() : (await db.Set<Tag>().AsNoTracking().IgnoreQueryFilters().Where(x => t.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var perfs = p.Count == 0 ? new() : (await db.Set<Performer>().AsNoTracking().IgnoreQueryFilters().Where(x => p.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        var studios = s.Count == 0 ? new() : (await db.Set<Studio>().AsNoTracking().IgnoreQueryFilters().Where(x => s.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
        return new Names(tags, perfs, studios);
    }

    // ── Ranking ──────────────────────────────────────────────────────────────

    private sealed class Cand
    {
        public int Id;
        public double Score, Confidence;
        public double Visual, Audio, Tag, TagRaw, Perf, Studio, Steer;
        public bool AudioPresent, TagPresent, PerfPresent, StudioPresent;
        public List<int> TopTags = [];
        public List<int> TopPerfs = [];
        public int? StudioId;
    }

    private readonly record struct Weights(double Visual, double Audio, double Tag, double Performer, double Studio);

    // Incremental MMR: O(take × pool) instead of O(take² × pool). Each round folds only the
    // newly-selected item into a running max-similarity per candidate rather than rescanning all
    // selected items every time (the latter was the dominant feed cost at large limits).
    private static List<Cand> Mmr(List<Cand> scored, Dictionary<int, float[]> vectors, double novelty, int take)
    {
        var lambda = Math.Clamp(novelty, 0, 1);
        // Diversify across TASTES, not just visuals: pull a wider pool when diversifying so there's variety to
        // spread into (otherwise a dominant common tag like "solo female" fills the whole pool and nothing can
        // break it up), and fold tag overlap into the MMR similarity so we don't stack near-identical tastes.
        var poolMult = lambda > 0 ? 6 : 3;
        var pool = scored.OrderByDescending(s => s.Score).Take(Math.Min(scored.Count, Math.Max(take, 1) * poolMult)).ToList();
        var target = Math.Min(take, pool.Count);
        if (pool.Count == 0)
            return pool;
        if (lambda <= 0)
            return pool.Take(target).ToList();

        var used = new bool[pool.Count];
        var maxSim = new double[pool.Count];
        var selected = new List<Cand>(target) { pool[0] }; // pool is score-sorted → first pick = top score
        used[0] = true;

        while (selected.Count < target)
        {
            var last = selected[^1];
            vectors.TryGetValue(last.Id, out var lastVec);
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                double sim = 0;
                if (lastVec is not null && vectors.TryGetValue(pool[i].Id, out var pv))
                    sim = Vectors.Cosine(pv, lastVec);
                // Shared dominant tags count as similarity too, so the list spreads across tastes.
                sim = Math.Max(sim, TagJaccard(pool[i].TopTags, last.TopTags));
                maxSim[i] = Math.Max(maxSim[i], sim);
            }

            int bestIdx = -1; double best = double.NegativeInfinity;
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                var mmr = (1 - lambda) * pool[i].Score - lambda * maxSim[i];
                if (mmr > best) { best = mmr; bestIdx = i; }
            }
            if (bestIdx < 0) break;
            used[bestIdx] = true;
            selected.Add(pool[bestIdx]);
        }
        return selected;
    }

    /// <summary>Jaccard overlap of two small tag-id lists (the candidates' top tags) — a cheap taste-similarity.</summary>
    private static double TagJaccard(List<int> a, List<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = 0;
        foreach (var t in a) if (b.Contains(t)) inter++;
        return inter == 0 ? 0 : (double)inter / (a.Count + b.Count - inter);
    }

    private static void Normalize(Dictionary<int, double> map)
    {
        var max = map.Values.DefaultIfEmpty(0).Max();
        if (max <= 0) return;
        foreach (var k in map.Keys.ToList()) map[k] /= max;
    }

    private static double Knob(IReadOnlyDictionary<string, double>? knobs, string key, double fallback)
        => knobs != null && knobs.TryGetValue(key, out var v) ? v : fallback;

    private static RecommendationResult Empty(string note) => new([], Diagnostics: new Dictionary<string, object> { ["note"] = note });

    private static string Band(double score) => score switch
    {
        >= 0.6 => "Strong match",
        >= 0.35 => "Good match",
        >= 0.15 => "Possible match",
        _ => "Weak match",
    };
}
