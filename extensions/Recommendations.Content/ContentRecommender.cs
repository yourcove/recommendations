using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Recommendations.Abstractions;

namespace Recommendations.Content;

/// <summary>
/// A content-based recommender that uses AI **visual embeddings** to surface content by similarity — the
/// first recommender that discovers *unseen* videos rather than re-ranking what the user already engaged
/// with. It blends the two Cove-owned visual kinds: feature.v1 (pure-visual, PRIMARY) and
/// semantic.v1 (joint vision-language, complementary). Candidates come from BOTH spaces (so
/// semantic surfaces "related but not look-alike" results feature alone would miss); scoring is a
/// feature-heavy weighted blend.
///   • GlobalFeed: builds taste centroids (niches, k-means) from the user's most-liked videos and returns
///     the nearest *unseen* videos.
///   • SimilarToEntity: "more like this" — nearest videos to a seed video.
/// Videos only for now (visual embeddings live on Video/Image). Uses the host's KNN over the asset-level
/// HNSW indexes.
/// </summary>
public sealed class ContentRecommender(IServiceScopeFactory scopeFactory) : IRecommender
{
    public const string RecommenderId = "cove.community.recommendations.content";

    private const string VisualFeatureFamily = EmbeddingKinds.VisualFeatureFamily;
    private const string VisualSemanticFamily = EmbeddingKinds.VisualSemanticFamily;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private sealed record VisualVec(float[]? Feature, float[]? Semantic);

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Content similarity (visual)",
        "Recommends visually-similar videos from AI embeddings (feature primary + semantic) — discovers unseen content near your taste, or 'more like this' from a video.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems],
        SourceEntityTypes: ["video"],
        TargetEntityTypes: ["video"],
        Knobs:
        [
            new RecommenderKnob("featureWeight", "Visual: feature", 0, 1, 0.70, "Pure-visual similarity (primary)."),
            new RecommenderKnob("semanticWeight", "Visual: semantic", 0, 1, 0.30, "Semantic/related similarity (complementary)."),
            new RecommenderKnob("noveltySimilarityBalance", "Novelty vs similarity", 0, 1, 0.5, "Higher favors less-similar (more novel) results."),
        ]);

    public RecommenderDescriptor Describe() => Descriptor;

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.TargetEntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return new RecommendationResult([], Diagnostics: Note("only 'video' is supported"));

        var (wF, wS) = (Knob(request.Knobs, "featureWeight", 0.70), Knob(request.Knobs, "semanticWeight", 0.30));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEmbeddingRepository>();
        var search = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        if (request.Context == RecommendationContext.SimilarToEntity)
        {
            if (request.Seed is not { } seed || !seed.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
                return new RecommendationResult([], Diagnostics: Note("similar-to-entity needs a video seed"));
            return await SimilarAsync(seed.EntityId, request, repo, search, wF, wS, cancellationToken);
        }

        return await DiscoverAsync(request, repo, search, wF, wS, cancellationToken);
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEmbeddingRepository>();

        var (featC, semC) = await BuildTasteCentroidsAsync(request.UserId, request.Core, repo, cancellationToken);
        var videoIds = request.Items.Where(i => i.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase)).Select(i => i.EntityId).ToList();
        var vecs = await GetVisualVectorsAsync(repo, videoIds, cancellationToken);
        var (wF, wS) = (0.70, 0.30);

        return request.Items.Select(item =>
        {
            if ((featC is null && semC is null) || !vecs.TryGetValue(item.EntityId, out var v))
                return new ItemScore(item.EntityType, item.EntityId, 0, 0);
            var sim = Math.Max(0, Vectors.BlendedCosine(v.Feature, v.Semantic, featC, semC, wF, wS));
            return new ItemScore(item.EntityType, item.EntityId, sim, 0.5,
                new Explanation($"{sim * 100:0}% visual match to your taste", []));
        }).ToList();
    }

    // ── GlobalFeed: discover unseen videos near the taste centroids ──────────
    private async Task<RecommendationResult> DiscoverAsync(
        RecommendationRequest request, IEmbeddingRepository repo, IEmbeddingService search, double wF, double wS, CancellationToken ct)
    {
        var engaged = await request.Core.Preference.GetTopLikedAsync(request.UserId, "video", 600, null, ct);
        var seen = engaged.Select(e => e.Entity.EntityId).ToHashSet();
        var likedTop = engaged.Where(e => e.Score > 0.1).Take(40).ToList();
        if (likedTop.Count == 0)
            return new RecommendationResult([], Diagnostics: Note("no liked videos with embeddings yet"));

        var vectors = await GetVisualVectorsAsync(repo, likedTop.Select(e => e.Entity.EntityId).ToList(), ct);
        var likedVec = likedTop.Where(e => vectors.ContainsKey(e.Entity.EntityId)).ToList();
        if (likedVec.Count == 0)
            return new RecommendationResult([], Diagnostics: Note("liked videos have no visual embeddings"));

        // k-means runs in ONE consistent space (feature/semantic differ in dim). Cluster in the better-covered
        // space (prefer feature); each niche keeps BOTH centroids so scoring can blend.
        int featCount = likedVec.Count(e => vectors[e.Entity.EntityId].Feature is not null);
        int semCount = likedVec.Count(e => vectors[e.Entity.EntityId].Semantic is not null);
        bool onFeature = featCount >= semCount;
        float[]? Chosen(VisualVec v) => onFeature ? v.Feature : v.Semantic;
        var clusterSet = likedVec.Where(e => Chosen(vectors[e.Entity.EntityId]) is not null).ToList();
        if (clusterSet.Count == 0)
            return new RecommendationResult([], Diagnostics: Note("liked videos have no visual embeddings"));

        var pts = clusterSet.Select(e => Chosen(vectors[e.Entity.EntityId])!).ToList();
        var wts = clusterSet.Select(e => Math.Max(0.01, e.Score)).ToList();
        var centroids = Vectors.KMeans(pts, wts, Math.Clamp(pts.Count / 8, 1, 12));
        var assignment = pts.Select(p => NearestCentroidIndex(centroids, p)).ToList();

        var centroidPairs = new List<(float[]? f, float[]? s)>();
        for (var c = 0; c < centroids.Count; c++)
        {
            var memberIdx = Enumerable.Range(0, clusterSet.Count).Where(i => assignment[i] == c).ToList();
            if (memberIdx.Count == 0) continue;
            var f = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[clusterSet[i].Entity.EntityId].Feature, Math.Max(0.01, clusterSet[i].Score))));
            var s = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[clusterSet[i].Entity.EntityId].Semantic, Math.Max(0.01, clusterSet[i].Score))));
            centroidPairs.Add((f, s));
        }

        var cand = new HashSet<int>();
        foreach (var (f, s) in centroidPairs)
            cand.UnionWith(await KnnUnionAsync(search, f, s, request.Limit * 3 + 50, seen, ct));
        var candVecs = await GetVisualVectorsAsync(repo, cand.ToList(), ct);

        var scored = new List<(int id, double score)>();
        foreach (var id in cand)
        {
            if (!candVecs.TryGetValue(id, out var cv)) continue;
            double best = 0;
            foreach (var (f, s) in centroidPairs)
            {
                var sc = Vectors.BlendedCosine(cv.Feature, cv.Semantic, f, s, wF, wS);
                if (sc > best) best = sc;
            }
            scored.Add((id, Math.Clamp(best, 0, 1)));
        }

        var items = scored.OrderByDescending(x => x.score).Skip(request.Offset).Take(request.Limit)
            .Select(x => new ItemScore("video", x.id, x.score, Math.Clamp(x.score, 0, 1), new Explanation($"{x.score * 100:0}% near your taste", []))).ToList();
        return new RecommendationResult(items, Diagnostics: new Dictionary<string, object>
        {
            ["recommender"] = RecommenderId,
            ["centroids"] = centroidPairs.Count,
            ["excludedSeen"] = seen.Count,
        });
    }

    // ── SimilarToEntity: more like this ──────────────────────────────────────
    private async Task<RecommendationResult> SimilarAsync(
        int seedVideoId, RecommendationRequest request, IEmbeddingRepository repo, IEmbeddingService search, double wF, double wS, CancellationToken ct)
    {
        var seedVec = (await GetVisualVectorsAsync(repo, [seedVideoId], ct)).GetValueOrDefault(seedVideoId);
        if (seedVec is null || (seedVec.Feature is null && seedVec.Semantic is null))
            return new RecommendationResult([], Diagnostics: Note("seed video has no visual embedding"));

        var cand = await KnnUnionAsync(search, seedVec.Feature, seedVec.Semantic, request.Limit + request.Offset + 50, new HashSet<int> { seedVideoId }, ct);
        var candVecs = await GetVisualVectorsAsync(repo, cand.ToList(), ct);
        var scored = cand.Where(candVecs.ContainsKey)
            .Select(id => (id, score: Math.Clamp(Vectors.BlendedCosine(candVecs[id].Feature, candVecs[id].Semantic, seedVec.Feature, seedVec.Semantic, wF, wS), 0, 1)))
            .ToList();

        var items = scored.OrderByDescending(x => x.score).Skip(request.Offset).Take(request.Limit)
            .Select(x => new ItemScore("video", x.id, x.score, Math.Clamp(x.score, 0, 1), new Explanation($"{x.score * 100:0}% visually similar", []))).ToList();
        return new RecommendationResult(items, Diagnostics: Note($"seed video {seedVideoId}"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(float[]? feature, float[]? semantic)> BuildTasteCentroidsAsync(int userId, ICoreServices core, IEmbeddingRepository repo, CancellationToken ct)
    {
        var liked = (await core.Preference.GetTopLikedAsync(userId, "video", 40, null, ct)).Where(e => e.Score > 0.1).ToList();
        if (liked.Count == 0)
            return (null, null);
        var vectors = await GetVisualVectorsAsync(repo, liked.Select(e => e.Entity.EntityId).ToList(), ct);
        var f = Vectors.WeightedCentroid(liked.Select(e => (vectors.GetValueOrDefault(e.Entity.EntityId)?.Feature, e.Score)));
        var s = Vectors.WeightedCentroid(liked.Select(e => (vectors.GetValueOrDefault(e.Entity.EntityId)?.Semantic, e.Score)));
        return (f, s);
    }

    /// <summary>Both asset-level (SectionIndex 0) visual vectors (feature + semantic) per video id.</summary>
    private static async Task<Dictionary<int, VisualVec>> GetVisualVectorsAsync(IEmbeddingRepository repo, IReadOnlyList<int> videoIds, CancellationToken ct)
    {
        var result = new Dictionary<int, VisualVec>();
        if (videoIds.Count == 0)
            return result;
        var rows = await repo.FindAsync(new EmbeddingFilter
        {
            HostType = EmbeddingHostType.Video,
            HostIds = videoIds,
            Modality = EmbeddingModality.Visual,
        }, ct);
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

    /// <summary>Candidate host ids from feature-KNN ∪ semantic-KNN.</summary>
    private static async Task<HashSet<int>> KnnUnionAsync(IEmbeddingService search, float[]? featQ, float[]? semQ, int k, ISet<int> exclude, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (featQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(featQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualFeatureFamily, SectionIndex = 0 }, ct))
                if (!exclude.Contains(h.Embedding.HostId)) ids.Add(h.Embedding.HostId);
        if (semQ is not null)
            foreach (var h in await search.KnnAsync(new Vector(semQ), Math.Min(1500, k), new EmbeddingSearchOptions { HostType = EmbeddingHostType.Video, Modality = EmbeddingModality.Visual, KindFamily = VisualSemanticFamily, SectionIndex = 0 }, ct))
                if (!exclude.Contains(h.Embedding.HostId)) ids.Add(h.Embedding.HostId);
        return ids;
    }

    private static int NearestCentroidIndex(List<float[]> centroids, float[] p)
    {
        int best = 0; double bestSim = double.NegativeInfinity;
        for (var i = 0; i < centroids.Count; i++) { var s = Vectors.Cosine(p, centroids[i]); if (s > bestSim) { bestSim = s; best = i; } }
        return best;
    }

    private static double Knob(IReadOnlyDictionary<string, double>? knobs, string key, double fallback)
        => knobs != null && knobs.TryGetValue(key, out var v) ? v : fallback;

    private static Dictionary<string, object> Note(string note) => new() { ["note"] = note };
}
