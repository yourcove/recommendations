using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;
using Recommendations.Toolkit;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Tastes;

/// <summary>
/// Multi-taste recommender. Clusters the user's liked videos into distinct visual niches (k-means), and
/// gives EACH cluster its own visual centroid AND its own TF-IDF tag affinity — so what a user likes
/// visually and tag-wise can vary cluster to cluster. Performer affinity is treated as global (one set of
/// people the user likes, across clusters). Candidate generation pulls from every cluster (or one, when a
/// cluster is selected). Implements ITasteClusters so the UI can show the user's tastes + per-cluster feeds.
/// </summary>
public sealed class ClusterRecommender(IServiceScopeFactory scopeFactory) : IRecommender, ITasteClusters, ITasteProfile
{
    public const string RecommenderId = "cove.community.recommendations.clusters";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Taste clusters",
        "Splits your taste into distinct clusters, each with its own visual + tag profile (performers stay global), and recommends across them or from one. Simpler than the full taste model — visuals and tags only, no per-aspect scoring — which makes it a clear way to see how your taste breaks up.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems],
        SourceEntityTypes: ["video"],
        TargetEntityTypes: ["video"],
        Knobs:
        [
            new RecommenderKnob("visualWeight", "Visual weight", 0, 1, 0.45, "Per-cluster visual similarity."),
            new RecommenderKnob("featureWeight", "Visual: feature", 0, 1, 0.70, "Pure-visual similarity (primary)."),
            new RecommenderKnob("semanticWeight", "Visual: semantic ", 0, 1, 0.30, "Semantic/related similarity (complementary)."),
            new RecommenderKnob("tagWeight", "Tag weight", 0, 1, 0.30, "Per-cluster tag affinity."),
            new RecommenderKnob("performerWeight", "Performer weight", 0, 1, 0.25, "Global performer affinity."),
            new RecommenderKnob("noveltySimilarityBalance", "Novelty vs similarity", 0, 1, 0.30, "Diversify the final list."),
        ],
        ScoreFields: RankedFeed.BasicScoreFields,
        SupportsRandomSort: true);

    /// <summary>Candidates pulled per cluster. Deliberately a FIXED size rather than derived from the page size:
    /// the pool is the universe this recommender ranks and pages through, so sizing it per request would make
    /// page 2 empty and report a total that cuts infinite scroll short.</summary>
    private const int PoolPerCluster = 400;
    private const int SeedPool = 600;
    /// <summary>How deep the diversity re-rank reaches. Fixed, so paging stays consistent.</summary>
    private const int MmrWindow = 400;

    public RecommenderDescriptor Describe() => Descriptor;

    // ── ITasteClusters: surface the user's clusters for the UI ───────────────
    public async Task<IReadOnlyList<TasteCluster>> GetClustersAsync(int userId, ICoreServices core, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var built = await BuildAsync(userId, core, sp, cancellationToken);
        return DescribeClusters(built);
    }

    // ── ITasteProfile: aggregate tag affinity across clusters + global performers ─
    public async Task<TasteProfile> GetProfileAsync(int userId, ICoreServices core, int topN = 60, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var built = await BuildAsync(userId, core, sp, cancellationToken);
        var tags = new Dictionary<int, double>();
        foreach (var c in built.Clusters)
            foreach (var (t, wgt) in c.TagAffinity)
                tags[t] = Math.Max(tags.GetValueOrDefault(t), wgt);
        return await RecHelpers.ToProfileAsync(sp.GetRequiredService<DbContext>(), tags, built.PerformerAffinity, new Dictionary<int, double>(), topN,
            $"Tags aggregated across {built.Clusters.Count} clusters (strongest per tag); performers are global.", cancellationToken);
    }

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.TargetEntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return Empty("videos only");

        var w = (visual: RecHelpers.Knob(request.Knobs, "visualWeight", 0.45),
                 tag: RecHelpers.Knob(request.Knobs, "tagWeight", 0.30),
                 perf: RecHelpers.Knob(request.Knobs, "performerWeight", 0.25));
        var novelty = RecHelpers.Knob(request.Knobs, "noveltySimilarityBalance", 0.30);
        var (wF, wS) = RecHelpers.VisualWeights(request.Knobs);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();

        var built = await BuildAsync(request.UserId, request.Core, sp, cancellationToken);
        if (built.Clusters.Count == 0)
            return Empty("no clustered taste yet (need liked videos with visual embeddings)");

        // SimilarToEntity: blended KNN from the seed (cluster-agnostic).
        if (request.Context == RecommendationContext.SimilarToEntity && request.Seed is { } s && s.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            var seedVec = (await RecHelpers.GetVisualVectorsAsync(repo, [s.EntityId], cancellationToken)).GetValueOrDefault(s.EntityId);
            if (seedVec is null || (seedVec.Feature is null && seedVec.Semantic is null)) return Empty("seed has no visual embedding");
            var candIds = await RecHelpers.KnnUnionAsync(search, seedVec.Feature, seedVec.Semantic, SeedPool, new HashSet<int> { s.EntityId }, cancellationToken);
            var candVecs = await RecHelpers.GetVisualVectorsAsync(repo, candIds, cancellationToken);
            var visualScore = new Dictionary<int, double>();
            foreach (var id in candIds)
                if (candVecs.TryGetValue(id, out var cv))
                    visualScore[id] = Math.Clamp(Vectors.BlendedCosine(cv.Feature, cv.Semantic, seedVec.Feature, seedVec.Semantic, wF, wS), 0, 1);
            return await RankAsync(candIds, candVecs, visualScore, clusterOf: null, built, w, novelty, request, db, cancellationToken);
        }

        // GlobalFeed (optionally scoped to one cluster): blended KNN per cluster, both visual spaces.
        var targets = request.ClusterId is { } cid && int.TryParse(cid, out var ci)
            ? built.Clusters.Where(c => c.Index == ci).ToList()
            : built.Clusters;
        if (targets.Count == 0) targets = built.Clusters;

        // A host-supplied universe (the standard filter/search) REPLACES candidate generation — otherwise a
        // filter would silently do nothing here, since the KNN pool has no idea what the user filtered to.
        var allCand = new HashSet<int>();
        if (request.CandidateIds is { Count: > 0 } filtered)
            foreach (var id in filtered) allCand.Add(id);
        else
            foreach (var cluster in targets)
                foreach (var id in await RecHelpers.KnnUnionAsync(search, cluster.FeatCentroid, cluster.SemCentroid, PoolPerCluster, built.Seen, cancellationToken))
                    allCand.Add(id);
        var candIds2 = allCand.ToList();
        var candVecs2 = await RecHelpers.GetVisualVectorsAsync(repo, candIds2, cancellationToken);
        var visualScore2 = new Dictionary<int, double>();
        var clusterOf2 = new Dictionary<int, int>();
        foreach (var id in candIds2)
        {
            if (!candVecs2.TryGetValue(id, out var cv)) continue;
            double best = -1; int bestCl = targets[0].Index;
            foreach (var cluster in targets)
            {
                var sc = Vectors.BlendedCosine(cv.Feature, cv.Semantic, cluster.FeatCentroid, cluster.SemCentroid, wF, wS);
                if (sc > best) { best = sc; bestCl = cluster.Index; }
            }
            visualScore2[id] = Math.Clamp(best, 0, 1); clusterOf2[id] = bestCl;
        }
        return await RankAsync(candIds2, candVecs2, visualScore2, clusterOf2, built, w, novelty, request, db, cancellationToken);
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var built = await BuildAsync(request.UserId, request.Core, sp, cancellationToken);
        var ids = request.Items.Where(i => i.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase)).Select(i => i.EntityId).ToList();
        var vecs = await RecHelpers.GetVisualVectorsAsync(repo, ids, cancellationToken);
        var (wF, wS) = RecHelpers.VisualWeights(null);
        return request.Items.Select(item =>
        {
            if (!vecs.TryGetValue(item.EntityId, out var v) || built.Clusters.Count == 0)
                return new ItemScore(item.EntityType, item.EntityId, 0, 0);
            var best = built.Clusters.Max(c => Math.Max(0, Vectors.BlendedCosine(v.Feature, v.Semantic, c.FeatCentroid, c.SemCentroid, wF, wS)));
            return new ItemScore(item.EntityType, item.EntityId, best, Math.Min(1.0, built.LikedCount / 15.0));
        }).ToList();
    }

    // ── Ranking ──────────────────────────────────────────────────────────────
    private async Task<RecommendationResult> RankAsync(
        List<int> ids, Dictionary<int, RecHelpers.VisualVec> candVecs, Dictionary<int, double> visualScore,
        Dictionary<int, int>? clusterOf, Built built, (double visual, double tag, double perf) w, double novelty,
        RecommendationRequest request, DbContext db, CancellationToken ct)
    {
        // Primary (feature-preferred) vectors for MMR diversity.
        var vectors = new Dictionary<int, float[]>();
        foreach (var id in ids)
            if (candVecs.TryGetValue(id, out var cv)) { var pv = RecHelpers.Primary(cv); if (pv is not null) vectors[id] = pv; }
        ids = ids.Where(visualScore.ContainsKey).ToList();
        if (ids.Count == 0) return Empty("no unseen candidates");
        var meta = await RecHelpers.FetchVideoMetaAsync(db, ids, ct);

        int ClusterFor(int id) => clusterOf != null && clusterOf.TryGetValue(id, out var c) ? c : (vectors.TryGetValue(id, out var v) ? NearestCluster(built, v) : built.Clusters[0].Index);

        // Raw signals.
        var rows = new List<(int id, int cluster, double visual, double tagRaw, double perf, List<int> topTags, List<int> topPerfs)>();
        foreach (var id in ids)
        {
            if (!meta.TryGetValue(id, out var m)) continue;
            var cl = built.Clusters.FirstOrDefault(c => c.Index == ClusterFor(id)) ?? built.Clusters[0];
            var visual = visualScore.GetValueOrDefault(id);
            var tagRaw = m.TagIds.Sum(t => cl.TagAffinity.GetValueOrDefault(t));
            var perf = m.PerformerIds.Select(p => built.PerformerAffinity.GetValueOrDefault(p)).OrderByDescending(Math.Abs).FirstOrDefault();
            var topTags = m.TagIds.Where(t => cl.TagAffinity.ContainsKey(t)).OrderByDescending(t => cl.TagAffinity[t]).Take(4).ToList();
            var topPerfs = m.PerformerIds.Where(p => built.PerformerAffinity.ContainsKey(p)).OrderByDescending(p => built.PerformerAffinity[p]).Take(2).ToList();
            rows.Add((id, cl.Index, visual, tagRaw, perf, topTags, topPerfs));
        }
        if (rows.Count == 0) return Empty("candidates have no metadata");

        var maxTag = Math.Max(1e-9, rows.Max(r => r.tagRaw));
        double den = Math.Max(1e-9, w.visual + w.tag + w.perf);
        var scored = rows.Select(r => (r.id, score: (w.visual * r.visual + w.tag * (r.tagRaw / maxTag) + w.perf * r.perf) / den)).ToList();

        var byId = rows.ToDictionary(r => r.id);
        var confidence = Math.Min(1.0, built.LikedCount / 15.0);
        var candidates = scored
            .Select(x => new ScoredCandidate(x.id, Math.Clamp(x.score, 0, 1), confidence,
                RankedFeed.BasicDimensions(Math.Clamp(x.score, 0, 1), confidence)))
            .ToList();

        // Names are only needed for the page RankedFeed actually returns, but the explanation callback is
        // synchronous, so resolve them for the whole (bounded) pool up front.
        var names = await GatherNamesAsync(db, rows.SelectMany(r => r.topTags), rows.SelectMany(r => r.topPerfs), ct);
        var labelByCluster = built.Clusters.ToDictionary(c => c.Index, c => c.Label);

        Explanation Explain(ScoredCandidate c)
        {
            var r = byId[c.Id];
            var tag = r.tagRaw / maxTag;
            var factors = new List<ExplanationFactor>
            {
                new("cluster", "Cluster", 0, labelByCluster.GetValueOrDefault(r.cluster, $"#{r.cluster}")),
                new("visual", "Visual match", w.visual * r.visual / den, $"{r.visual * 100:0}%"),
            };
            if (r.topTags.Count > 0)
                factors.Add(new("tags", "Tags (this cluster)", w.tag * tag / den, string.Join(", ", r.topTags.Select(t => names.Tags.GetValueOrDefault(t, $"#{t}")))));
            if (r.topPerfs.Count > 0)
                factors.Add(new("performers", "Performers", w.perf * r.perf / den, string.Join(", ", r.topPerfs.Select(p => names.Perfs.GetValueOrDefault(p, $"#{p}")))));
            return new Explanation($"{labelByCluster.GetValueOrDefault(r.cluster, "cluster")} · {Band(c.Score)}", factors);
        }

        return RankedFeed.Build(request, "video", candidates, RankedFeed.BasicDimensionKeys, Explain,
            new Dictionary<string, object>
            {
                ["recommender"] = RecommenderId,
                ["clusters"] = built.Clusters.Count,
                ["scopedCluster"] = request.ClusterId ?? "all",
                ["candidates"] = ids.Count,
            },
            // MMR diversity, over a bounded window so the greedy stays cheap and deterministic.
            ordered =>
            {
                if (novelty <= 1e-3) return ordered;
                var window = Math.Min(ordered.Count, MmrWindow);
                var head = ordered.Take(window).ToList();
                var picked = RecHelpers.MmrIds(head.Select(c => (c.Id, c.Score)).ToList(), vectors, novelty, window);
                var byCandidate = head.ToDictionary(c => c.Id);
                return picked.Select(id => byCandidate[id]).Concat(ordered.Skip(window)).ToList();
            });
    }

    // ── Clustering ───────────────────────────────────────────────────────────
    private sealed record Cluster(int Index, string Label, List<int> LikedIds, float[]? FeatCentroid, float[]? SemCentroid, Dictionary<int, double> TagAffinity);
    private sealed record Built(List<Cluster> Clusters, Dictionary<int, double> PerformerAffinity, HashSet<int> Seen, int LikedCount);

    private async Task<Built> BuildAsync(int userId, ICoreServices core, IServiceProvider sp, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();

        var engaged = await core.Preference.GetTopLikedAsync(userId, "video", 1500, null, ct);
        var seen = engaged.Select(e => e.Entity.EntityId).ToHashSet();
        var liked = engaged.Where(e => e.Score > 0.1).Take(80).ToList();
        var vectors = await RecHelpers.GetVisualVectorsAsync(repo, liked.Select(e => e.Entity.EntityId).ToList(), ct);
        var likedVec = liked.Where(e => vectors.ContainsKey(e.Entity.EntityId)).ToList();
        if (likedVec.Count == 0) return new Built([], new(), seen, 0);

        // k-means must run in ONE consistent space (feature and semantic have different dims). Cluster in the
        // better-covered space, preferring feature. Each cluster then keeps BOTH centroids so scoring
        // can blend; videos lacking the chosen space are excluded from clustering (rare — most have both).
        int featCount = likedVec.Count(e => vectors[e.Entity.EntityId].Feature is not null);
        int semCount = likedVec.Count(e => vectors[e.Entity.EntityId].Semantic is not null);
        bool clusterOnFeature = featCount >= semCount;
        float[]? Chosen(RecHelpers.VisualVec v) => clusterOnFeature ? v.Feature : v.Semantic;
        var clusterSet = likedVec.Where(e => Chosen(vectors[e.Entity.EntityId]) is not null).ToList();
        if (clusterSet.Count == 0) return new Built([], new(), seen, 0);

        var pts = clusterSet.Select(e => Chosen(vectors[e.Entity.EntityId])!).ToList();
        var wts = clusterSet.Select(e => Math.Max(0.01, e.Score)).ToList();
        var centroids = Vectors.KMeans(pts, wts, Math.Clamp(pts.Count / 8, 1, 12));
        var assignment = pts.Select(p => NearestCentroidIndex(centroids, p)).ToList();

        var likedIds = clusterSet.Select(e => e.Entity.EntityId).ToList();
        var meta = await RecHelpers.FetchVideoMetaAsync(db, likedIds, ct);
        var tagCounts = await RecHelpers.TagVideoCountsAsync(db, meta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var totalVideos = await RecHelpers.TotalVideosAsync(db, ct);

        var clusters = new List<Cluster>();
        for (var c = 0; c < centroids.Count; c++)
        {
            var memberIdx = Enumerable.Range(0, clusterSet.Count).Where(i => assignment[i] == c).ToList();
            if (memberIdx.Count == 0) continue;
            var memberIds = memberIdx.Select(i => clusterSet[i].Entity.EntityId).ToList();
            var memberWeighted = memberIdx.Select(i => (clusterSet[i].Entity.EntityId, Math.Max(0, clusterSet[i].Score))).ToList();
            var tagAff = RecHelpers.TagAffinity(memberWeighted, meta, tagCounts, totalVideos);
            var featC = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[clusterSet[i].Entity.EntityId].Feature, Math.Max(0.01, clusterSet[i].Score))));
            var semC = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[clusterSet[i].Entity.EntityId].Semantic, Math.Max(0.01, clusterSet[i].Score))));
            var idx = clusters.Count;
            clusters.Add(new Cluster(idx, $"Cluster {idx + 1}", memberIds, featC, semC, tagAff));
        }

        // Global performer affinity via CONTRASTIVE attribution: a joint ridge fit over signed engagement so a
        // performer is credited for their own pull, not the tags/studios they co-occur with (per-cluster tag
        // affinity above stays a local positives-only signal). Direct preference overrides.
        var signed = engaged.Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var signedMeta = await RecHelpers.FetchVideoMetaAsync(db, signed.Select(e => e.Entity.EntityId).ToList(), ct);
        var signedTagCounts = await RecHelpers.TagVideoCountsAsync(db, signedMeta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var signedStudioCounts = await RecHelpers.StudioVideoCountsAsync(db, signedMeta.Values.Where(m => m.StudioId.HasValue).Select(m => m.StudioId!.Value).Distinct().ToList(), ct);
        var perfCounts = await RecHelpers.PerformerVideoCountsAsync(db, signedMeta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList(), ct);
        var aspectTargets = await RecHelpers.FetchAspectTargetsAsync(db, userId, signedMeta.Keys.ToList(), ct);
        var attribution = await RecHelpers.AttributeAttributionAsync(core, userId, signed.Select(e => (e.Entity.EntityId, e.Score)), signedMeta, signedTagCounts, signedStudioCounts, perfCounts, totalVideos, null, ct, aspectTargets);
        var performerAff = attribution.Performers.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);
        return new Built(clusters, performerAff, seen, likedVec.Count);
    }

    private static IReadOnlyList<TasteCluster> DescribeClusters(Built built) =>
        built.Clusters.Select(c => new TasteCluster(
            c.Index.ToString(),
            c.Label,
            c.LikedIds.Count,
            $"{c.LikedIds.Count} liked videos")).ToList();

    // ── Small helpers ────────────────────────────────────────────────────────
    private static int NearestCentroidIndex(List<float[]> centroids, float[] p)
    {
        int best = 0; double bestSim = double.NegativeInfinity;
        for (var i = 0; i < centroids.Count; i++) { var s = Vectors.Cosine(p, centroids[i]); if (s > bestSim) { bestSim = s; best = i; } }
        return best;
    }
    private static int NearestCluster(Built built, float[] v)
    {
        int best = built.Clusters.Count > 0 ? built.Clusters[0].Index : 0; double bestSim = double.NegativeInfinity;
        foreach (var c in built.Clusters) { var cen = c.FeatCentroid ?? c.SemCentroid; if (cen is null) continue; var s = Vectors.Cosine(v, cen); if (s > bestSim) { bestSim = s; best = c.Index; } }
        return best;
    }

    private sealed record NameSets(Dictionary<int, string> Tags, Dictionary<int, string> Perfs);
    private static async Task<NameSets> GatherNamesAsync(DbContext db, IEnumerable<int> tagIds, IEnumerable<int> perfIds, CancellationToken ct)
        => new(await RecHelpers.TagNamesAsync(db, tagIds, ct), await RecHelpers.PerformerNamesAsync(db, perfIds, ct));

    private static RecommendationResult Empty(string note) => new([], Diagnostics: new Dictionary<string, object> { ["note"] = note });
    private static string Band(double s) => s >= 0.6 ? "Strong" : s >= 0.35 ? "Good" : s >= 0.15 ? "Possible" : "Weak";
}
