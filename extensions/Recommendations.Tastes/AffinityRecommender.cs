using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;
using Recommendations.Toolkit;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Tastes;

/// <summary>
/// The deliberate opposite of clustering: ONE global taste profile — your overall affinity toward tags
/// (TF-IDF), visual embeddings (a single centroid), performers, and studios — with no niches. Useful as a
/// baseline and to compare against the cluster recommender. Exposes rich per-result reasons.
/// </summary>
public sealed class AffinityRecommender(IServiceScopeFactory scopeFactory) : IRecommender, ITasteProfile
{
    public const string RecommenderId = "cove.community.recommendations.affinity";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Global affinity (no clustering)",
        "One global taste profile: overall tag, visual, performer, and studio affinity — no niches. A baseline to compare clustering against.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems],
        SourceEntityTypes: ["video"],
        TargetEntityTypes: ["video"],
        Knobs:
        [
            new RecommenderKnob("visualWeight", "Visual weight", 0, 1, 0.40, "Overall visual similarity."),
            new RecommenderKnob("featureWeight", "Visual: feature", 0, 1, 0.70, "Pure-visual similarity (primary)."),
            new RecommenderKnob("semanticWeight", "Visual: semantic", 0, 1, 0.30, "Semantic/related similarity (complementary)."),
            new RecommenderKnob("tagWeight", "Tag weight", 0, 1, 0.30, "Overall tag affinity."),
            new RecommenderKnob("performerWeight", "Performer weight", 0, 1, 0.25, "Overall performer affinity."),
            new RecommenderKnob("studioWeight", "Studio weight", 0, 1, 0.05, "Overall studio affinity."),
            new RecommenderKnob("noveltySimilarityBalance", "Novelty vs similarity", 0, 1, 0.30, "Diversify the final list."),
        ]);

    public RecommenderDescriptor Describe() => Descriptor;

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.TargetEntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return Empty("videos only");

        var w = (visual: RecHelpers.Knob(request.Knobs, "visualWeight", 0.40),
                 tag: RecHelpers.Knob(request.Knobs, "tagWeight", 0.30),
                 perf: RecHelpers.Knob(request.Knobs, "performerWeight", 0.25),
                 studio: RecHelpers.Knob(request.Knobs, "studioWeight", 0.05));
        var novelty = RecHelpers.Knob(request.Knobs, "noveltySimilarityBalance", 0.30);
        var (wF, wS) = RecHelpers.VisualWeights(request.Knobs);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();

        var profile = await BuildAsync(request.UserId, request.Core, repo, db, cancellationToken);
        if (profile.FeatCentroid is null && profile.SemCentroid is null)
            return Empty("no global taste yet (need liked videos with visual embeddings)");

        // Candidate generation: from the seed (SimilarToEntity) or the global centroids. Probe BOTH visual
        // spaces (feature primary + semantic complementary) and union the neighbours.
        HashSet<int> exclude = profile.Seen;
        float[]? featQuery = profile.FeatCentroid, semQuery = profile.SemCentroid;
        int k = request.Limit * 3 + 50;
        if (request.Context == RecommendationContext.SimilarToEntity && request.Seed is { } s && s.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            var seedVec = (await RecHelpers.GetVisualVectorsAsync(repo, [s.EntityId], cancellationToken)).GetValueOrDefault(s.EntityId);
            if (seedVec is null || (seedVec.Feature is null && seedVec.Semantic is null)) return Empty("seed has no visual embedding");
            featQuery = seedVec.Feature; semQuery = seedVec.Semantic; exclude = [s.EntityId]; k = request.Limit * 6 + 50;
        }

        var candIds = await RecHelpers.KnnUnionAsync(search, featQuery, semQuery, k, exclude, cancellationToken);
        if (candIds.Count == 0) return Empty("no unseen candidates");
        var candVecs = await RecHelpers.GetVisualVectorsAsync(repo, candIds, cancellationToken);
        var vectors = new Dictionary<int, float[]>();
        var visualScore = new Dictionary<int, double>();
        foreach (var id in candIds)
        {
            if (!candVecs.TryGetValue(id, out var cv)) continue;
            visualScore[id] = Math.Clamp(Vectors.BlendedCosine(cv.Feature, cv.Semantic, featQuery, semQuery, wF, wS), 0, 1);
            var pv = RecHelpers.Primary(cv); if (pv is not null) vectors[id] = pv;
        }
        var ids = visualScore.Keys.ToList();
        if (ids.Count == 0) return Empty("candidates have no visual embeddings");

        var meta = await RecHelpers.FetchVideoMetaAsync(db, ids, cancellationToken);
        var rows = ids.Where(meta.ContainsKey).Select(id =>
        {
            var m = meta[id];
            return (id,
                visual: visualScore[id],
                tagRaw: m.TagIds.Sum(t => profile.TagAffinity.GetValueOrDefault(t)),
                perf: m.PerformerIds.Select(p => profile.PerformerAffinity.GetValueOrDefault(p)).OrderByDescending(Math.Abs).FirstOrDefault(),
                studio: m.StudioId is { } sid ? profile.StudioAffinity.GetValueOrDefault(sid) : 0,
                topTags: m.TagIds.Where(t => profile.TagAffinity.ContainsKey(t)).OrderByDescending(t => profile.TagAffinity[t]).Take(4).ToList(),
                topPerfs: m.PerformerIds.Where(p => profile.PerformerAffinity.ContainsKey(p)).OrderByDescending(p => profile.PerformerAffinity[p]).Take(2).ToList());
        }).ToList();
        if (rows.Count == 0) return Empty("candidates have no metadata");

        var maxTag = Math.Max(1e-9, rows.Max(r => Math.Abs(r.tagRaw)));
        var den = Math.Max(1e-9, w.visual + w.tag + w.perf + w.studio);
        var scored = rows.Select(r => (r.id, score: (w.visual * r.visual + w.tag * (r.tagRaw / maxTag) + w.perf * r.perf + w.studio * r.studio) / den)).ToList();

        var order = RecHelpers.MmrIds(scored, vectors, novelty, request.Limit + request.Offset).Skip(request.Offset).Take(request.Limit).ToList();
        var byId = rows.ToDictionary(r => r.id);
        var scoreById = scored.ToDictionary(s => s.id, s => s.score);
        var names = new { Tags = await RecHelpers.TagNamesAsync(db, order.SelectMany(id => byId[id].topTags), cancellationToken), Perfs = await RecHelpers.PerformerNamesAsync(db, order.SelectMany(id => byId[id].topPerfs), cancellationToken) };

        var items = order.Select(id =>
        {
            var r = byId[id];
            var tag = r.tagRaw / maxTag;
            var factors = new List<ExplanationFactor> { new("visual", "Visual match", w.visual * r.visual / den, $"{r.visual * 100:0}%") };
            if (r.topTags.Count > 0) factors.Add(new("tags", "Tags", w.tag * tag / den, string.Join(", ", r.topTags.Select(t => names.Tags.GetValueOrDefault(t, $"#{t}")))));
            if (r.topPerfs.Count > 0) factors.Add(new("performers", "Performers", w.perf * r.perf / den, string.Join(", ", r.topPerfs.Select(p => names.Perfs.GetValueOrDefault(p, $"#{p}")))));
            return new ItemScore("video", id, Math.Clamp(scoreById[id], 0, 1), Math.Min(1.0, profile.LikedCount / 15.0), new Explanation(Band(scoreById[id]), factors));
        }).ToList();

        return new RecommendationResult(items, Diagnostics: new Dictionary<string, object> { ["recommender"] = RecommenderId, ["candidates"] = ids.Count });
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();
        var profile = await BuildAsync(request.UserId, request.Core, repo, db, cancellationToken);
        var ids = request.Items.Where(i => i.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase)).Select(i => i.EntityId).ToList();
        var vecs = await RecHelpers.GetVisualVectorsAsync(repo, ids, cancellationToken);
        var (wF, wS) = RecHelpers.VisualWeights(null);
        return request.Items.Select(item =>
        {
            if ((profile.FeatCentroid is null && profile.SemCentroid is null) || !vecs.TryGetValue(item.EntityId, out var v))
                return new ItemScore(item.EntityType, item.EntityId, 0, 0);
            return new ItemScore(item.EntityType, item.EntityId, Math.Max(0, Vectors.BlendedCosine(v.Feature, v.Semantic, profile.FeatCentroid, profile.SemCentroid, wF, wS)), Math.Min(1.0, profile.LikedCount / 15.0));
        }).ToList();
    }

    public async Task<TasteProfile> GetProfileAsync(int userId, ICoreServices core, int topN = 60, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();
        var profile = await BuildAsync(userId, core, repo, db, cancellationToken);
        return await RecHelpers.ToProfileAsync(db, profile.TagAffinity, profile.PerformerAffinity, profile.StudioAffinity, topN,
            $"One global taste profile from {profile.LikedCount} liked videos.", cancellationToken);
    }

    private sealed record Profile(float[]? FeatCentroid, float[]? SemCentroid, Dictionary<int, double> TagAffinity, Dictionary<int, double> PerformerAffinity, Dictionary<int, double> StudioAffinity, HashSet<int> Seen, int LikedCount);

    private async Task<Profile> BuildAsync(int userId, ICoreServices core, IEmbeddingRepository repo, DbContext db, CancellationToken ct)
    {
        var engaged = await core.Preference.GetTopLikedAsync(userId, "video", 1500, null, ct);
        var seen = engaged.Select(e => e.Entity.EntityId).ToHashSet();

        // Signed engagement: positives pull toward, negatives push away. Visual centroids use POSITIVES only
        // (you cluster what you like); tag/performer/studio affinities use the SIGNED set so dislikes subtract.
        var signed = engaged.Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var signedIds = signed.Select(e => e.Entity.EntityId).ToList();
        var liked = signed.Where(e => e.Score > 0.1).Take(80).ToList();

        // Fetch visual vectors for the strongest-signal videos once: positives build the centroid (you cluster
        // what you like), and each one's cosine-to-centroid becomes the 2c visual-fit covariate. Capped to keep
        // the profile build fast — the omitted low-signal tail barely moves the covariate.
        var vectorIds = signed.OrderByDescending(e => Math.Abs(e.Score)).Take(600).Select(e => e.Entity.EntityId).ToList();
        var vectors = await RecHelpers.GetVisualVectorsAsync(repo, vectorIds, ct);
        var likedVec = liked.Where(e => vectors.ContainsKey(e.Entity.EntityId)).ToList();
        var featCentroid = Vectors.WeightedCentroid(likedVec.Select(e => (vectors[e.Entity.EntityId].Feature, Math.Max(0.01, e.Score))));
        var semCentroid = Vectors.WeightedCentroid(likedVec.Select(e => (vectors[e.Entity.EntityId].Semantic, Math.Max(0.01, e.Score))));

        // Contrastive attribution: ONE joint ridge fit over tags/performers/studios (co-occurrence partialled
        // out), confidence-weighted so strong/explicit signals dominate, with direct entity prefs overriding.
        // The raw signed score is the target — the regression supersedes the old DampNeg confounding hack.
        // 2b: explicit content/performers aspect ratings supervise those components directly. 2c: visual-fit
        // covariate nets out "it just looks like your taste" so tag/performer weights are earned beyond visuals.
        var signedScored = signed.Select(e => (e.Entity.EntityId, e.Score)).ToList();
        var meta = await RecHelpers.FetchVideoMetaAsync(db, signedIds, ct);
        var total = await RecHelpers.TotalVideosAsync(db, ct);
        var tagCounts = await RecHelpers.TagVideoCountsAsync(db, meta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var studioCounts = await RecHelpers.StudioVideoCountsAsync(db, meta.Values.Where(m => m.StudioId.HasValue).Select(m => m.StudioId!.Value).Distinct().ToList(), ct);
        var perfCounts = await RecHelpers.PerformerVideoCountsAsync(db, meta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList(), ct);
        var aspectTargets = await RecHelpers.FetchAspectTargetsAsync(db, userId, signedIds, ct);
        var (wF, wS) = RecHelpers.VisualWeights(null);
        var visualFit = (featCentroid is null && semCentroid is null) ? null : signed
            .Where(e => vectors.ContainsKey(e.Entity.EntityId))
            .ToDictionary(e => e.Entity.EntityId, e => Vectors.BlendedCosine(vectors[e.Entity.EntityId].Feature, vectors[e.Entity.EntityId].Semantic, featCentroid, semCentroid, wF, wS));

        var attribution = await RecHelpers.AttributeAttributionAsync(core, userId, signedScored, meta, tagCounts, studioCounts, perfCounts, total, null, ct, aspectTargets, visualFit);
        var tagAff = attribution.Tags.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);
        var perfAff = attribution.Performers.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);
        var studioAff = attribution.Studios.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);

        return new Profile(featCentroid, semCentroid, tagAff, perfAff, studioAff, seen, likedVec.Count);
    }

    private static RecommendationResult Empty(string note) => new([], Diagnostics: new Dictionary<string, object> { ["note"] = note });
    private static string Band(double s) => s >= 0.6 ? "Strong match" : s >= 0.35 ? "Good match" : s >= 0.15 ? "Possible match" : "Weak match";
}
