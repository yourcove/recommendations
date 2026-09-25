using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recommendations.Abstractions;
using Recommendations.Toolkit;
using System.Text.Json;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Tastes;

/// <summary>
/// "Full taste model" — the primary recommender, and the only one still under active development.
/// <para>
/// It scores every candidate on FOUR independent aspects — performers (faces, learned per-performer affinity, an
/// attribute cold-start prior), content (tags/actions, the learned look axis, cluster fit, studio), video quality
/// (a learned craft axis plus objective fidelity) and audio (an ECAPA voice axis) — each carrying its own
/// CONFIDENCE, each calibrated against the user's own library so a value reads as "vs your typical" rather than
/// as anything absolute, then fuses them into one ranking key (see docs/FUSION_DESIGN.md). Those four aspects are
/// what the UI exposes as sortable, filterable score dimensions and as the per-result "Why" breakdown.
/// </para>
/// <para>
/// Clustering is one INPUT to that model rather than its organising idea — the class name is historical. Three
/// things still set its clustering apart from the older <see cref="ClusterRecommender"/>:
/// </para>
/// <list type="number">
/// <item>MULTIMODAL, honest niches — clusters on each liked video's visual embedding CONCATENATED with its
/// coverage×IDF tag signature (<see cref="RecHelpers.RefinedMultimodalClusters"/>), so niches split by CONTENT
/// (co-occurring distinctive tags) not just look — the fix for a visually-homogeneous library. Crucially the
/// tag signal is EFFECTIVE tags: manual tags PLUS AI-applied tags (TagApplication, coverage-weighted), which
/// Video.TagIds omits — so the whole model finally sees them. The niche count falls out of the data (merge
/// near-duplicates, absorb orphans) rather than an arbitrary k.</item>
/// <item>GLOBAL + cluster-specific factorization — each niche runs its OWN weighted attribution: every engaged
/// video (likes AND dislikes) is weighted by visual proximity to the niche, so the niche learns the attribute
/// affinities that hold near it. That local estimate is then shrunk toward the global model by the niche's
/// local evidence per attribute (a thin niche stays near global; a rich one expresses its own flavour). So the
/// same tag can be a positive in one niche and a negative in another — a single blended affinity space can't do
/// that. (v1 approximated this with a frequency over-representation boost; v2 is the real per-niche regression.)</item>
/// <item>Niche-spread novelty — candidates are drawn per niche and the final feed is interleaved across them,
/// so you see your whole range of tastes, not just the dominant one.</item>
/// </list>
/// Built to be the substrate later ideas layer onto (per-niche embedding directions, dwell-localised
/// attribution, compound-concept tags). Videos and images.
/// </summary>
public sealed class NicheRecommender(IServiceScopeFactory scopeFactory, TasteModelStore modelStore, ILogger<NicheRecommender> logger)
    : IRecommender, ITasteClusters, ITasteProfile, ITrainingGround, IRecommenderEvaluable, IRecommenderWarmup
{
    public const string RecommenderId = "cove.community.recommendations.niche";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TasteModelStore _modelStore = modelStore;
    private readonly ILogger<NicheRecommender> _logger = logger;

    private static readonly RecommenderDescriptor Descriptor = new(
        RecommenderId,
        "Full taste model",
        "Judges every video on four separate things — who's in it, what happens, how it's shot, and how it sounds — and combines them into one score. Each aspect is learned from your ratings and engagement, calibrated against your own library (so \"good\" means good *for you*, not in the abstract), and reported per result with a confidence, so you can see which aspect drove a recommendation and filter or sort on any of them. Taste clusters are one input among many.",
        [RecommendationContext.GlobalFeed, RecommendationContext.SimilarToEntity, RecommendationContext.ScoreItems, RecommendationContext.Training],
        SourceEntityTypes: ["video", "image"],
        TargetEntityTypes: ["video", "image"],
        Knobs:
        [
            // Four aspect levers (the "who / what / how-it's-shot / how-it-sounds" a person judges a video on).
            // Non-negative importances; each aspect's signals are centered so they push the score UP or DOWN.
            // Defaults reflect the self-eval: content is the strongest predictor of ratings, performers moderate,
            // quality/audio weak (thin explicit-rating supervision) — so they lead, but don't dominate.
            new RecommenderKnob("performersWeight", "Performers", 0, 2, 0.75, "WHO is in it — faces + your learned per-performer affinity.", "Aspects"),
            new RecommenderKnob("contentWeight", "Content", 0, 2, 1.00, "WHAT happens — tags/actions, the visual look, your look axis, and liked/disliked content clusters.", "Aspects"),
            new RecommenderKnob("qualityWeight", "Video quality", 0, 2, 0.40, "How it's shot — the learned craft/cinematography axis plus objective file fidelity (resolution + bitrate).", "Aspects"),
            new RecommenderKnob("audioWeight", "Audio", 0, 2, 0.25, "The voice/audio axis (ECAPA), learned mainly from your explicit \"audio\" aspect ratings.", "Aspects"),

            // Diversity: spread the top results across different clusters / performers instead of the same few.
            new RecommenderKnob("contentDiversity", "Content diversity", 0, 2, 0.00, "Higher pulls the top results from a VARIETY of content clusters (and content that fits no cluster) instead of repeating the same look.", "Diversity"),
            new RecommenderKnob("performerDiversity", "Performer diversity", 0, 2, 0.00, "Higher spreads the top results across DIFFERENT performers instead of the same few dominating.", "Diversity"),

            // Advanced: what makes up each aspect (the blend inside it). Defaults reproduce the standard mix.
            new RecommenderKnob("faceWeight", "→ Face match", 0, 1, 0.45, "Within Performers: visual FACE similarity to faces you like (tends to be more diverse — many performers can share a look).", "Performers (advanced)", true),
            new RecommenderKnob("performerAffinityWeight", "→ Performer affinity", 0, 1, 0.55, "Within Performers: your learned per-performer preference from ratings/engagement (tends to concentrate on your specific favorites).", "Performers (advanced)", true),
            new RecommenderKnob("performerAttributeWeight", "→ Performer attributes", 0, 1, 0.15, "Within Performers: a COLD-START prior from the cast's attributes (gender, ethnicity, hair, height, body, performer tags…) learned from performers you already rate — so a performer you've never rated still gets a sensible read. (Low default: needs many rated performers before it predicts well.)", "Performers (advanced)", true),
            new RecommenderKnob("tagWeight", "→ Tags", 0, 1, 0.45, "Within Content: niche-local tag/action affinity.", "Content (advanced)", true),
            new RecommenderKnob("tasteWeight", "→ Look axis", 0, 1, 0.30, "Within Content: the learned liked-vs-disliked visual look direction.", "Content (advanced)", true),
            new RecommenderKnob("nicheWeight", "→ Cluster fit", 0, 1, 0.35, "Within Content: fits a liked content cluster (+) / disliked cluster (−) / none (0). Carries the visual-match signal.", "Content (advanced)", true),
            new RecommenderKnob("studioWeight", "→ Studio", 0, 1, 0.15, "Within Content: studio affinity.", "Content (advanced)", true),
            new RecommenderKnob("qualityCraftWeight", "→ Craft axis", 0, 1, 1.00, "Within Video quality: the learned cinematography/look axis from your explicit \"video quality\" ratings.", "Video quality (advanced)", true),
            new RecommenderKnob("objectiveQualityWeight", "→ Fidelity (resolution + bitrate)", 0, 1, 1.00, "Within Video quality: objective file fidelity — resolution and bitrate — calibrated to whether higher fidelity actually tracks your enjoyment.", "Video quality (advanced)", true),
            new RecommenderKnob("audioLift", "→ Audio assertiveness", 0, 3, 1.60, "Multiplies the Audio belief so it contributes more decisively in BOTH directions (surfacing liked audio and letting disliked audio drag a video down) instead of being suppressed by the shared dead-band. 1 = neutral; higher = more assertive.", "Audio (advanced)", true),
        ],
        // The dimensions you can sort and range-filter on. The four aspect beliefs are CENTERED: 0 is your
        // library-typical, so "> 0.2" reads as "clearly better than typical for me on this aspect" — the same scale
        // the per-result Why breakdown shows. Overall and confidence are plain 0…1 magnitudes.
        ScoreFields:
        [
            new RecommenderScoreField("overall", "Overall score", 0, 1, false, "The fused ranking score across all four aspects."),
            new RecommenderScoreField("performers", "Performers", -1, 1, true, "WHO is in it — faces, learned per-performer affinity, and the attribute prior. 0 = typical for your library."),
            new RecommenderScoreField("content", "Content", -1, 1, true, "WHAT happens — tags/actions, look axis, cluster fit, studio. 0 = typical for your library."),
            new RecommenderScoreField("quality", "Video quality", -1, 1, true, "How it's shot — the craft axis plus objective fidelity. 0 = typical for your library."),
            new RecommenderScoreField("audio", "Audio", -1, 1, true, "The voice/audio axis. 0 = typical for your library."),
            new RecommenderScoreField("confidence", "Confidence", 0, 1, false, "How much data backs the score — filter this up to see only items the model is sure about."),
        ],
        SupportsRandomSort: true);

    public RecommenderDescriptor Describe() => Descriptor;

    // ── ITrainingGround: ASPECT-centric active learning. Instead of "learn one uncertain tag", we surface videos
    // where the model has a STRONG but DERIVED (unrated) belief about ONE aspect — both predicted-likes (to
    // reinforce) and predicted-dislikes (to catch false negatives) — and ask you to rate exactly that aspect.
    // Confirming/refuting a per-aspect belief is the most data-efficient de-confounding signal we can get.
    private static readonly (string key, string label)[] ProbeAspects =
        [("performers", "Performers"), ("content", "Content"), ("video_quality", "Video quality"), ("audio", "Audio")];

    public async Task<TrainingProbeSet> GetProbesAsync(int userId, ICoreServices core, int limit, string mediaType, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        // Both video and image go through the fast aspect-centric path (cached model + a small candidate pool).
        if (string.Equals(mediaType, "image", StringComparison.OrdinalIgnoreCase))
            return await GetImageProbesAsync(userId, core, sp, limit, cancellationToken);
        if (!string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase))
            return new TrainingProbeSet([], "Training grounds supports videos and images.");

        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();
        var built = await GetOrBuildBuiltAsync(userId, core, sp, cancellationToken);
        if (built.Niches.Count == 0) return new TrainingProbeSet([], "Engage with more videos first — there's no taste model to probe yet.");

        // FRESH candidate pool spanning predicted-likes AND predicted-dislikes (so we can probe both extremes):
        // neighbours of your niches + your taste axis + your disliked-content clusters. Exclude already-seen.
        var exclude = built.Seen;
        var pool = new Dictionary<int, RecHelpers.VisualVec>();
        foreach (var niche in built.Niches)
            foreach (var kv in await RecHelpers.KnnUnionVecsAsync(search, niche.FeatCentroid, niche.SemCentroid, 50, exclude, cancellationToken))
                pool.TryAdd(kv.Key, kv.Value);
        if (built.TasteVector is not null)
            foreach (var kv in await RecHelpers.KnnUnionVecsAsync(search, built.TasteVector, null, 150, exclude, cancellationToken))
                pool.TryAdd(kv.Key, kv.Value);
        foreach (var dc in built.DislikedNicheCentroids.Take(4))
            foreach (var kv in await RecHelpers.KnnUnionVecsAsync(search, dc, null, 50, exclude, cancellationToken))
                pool.TryAdd(kv.Key, kv.Value);
        if (pool.Count == 0) return new TrainingProbeSet([], "No fresh candidates to probe right now.");

        var rows = await BuildScoredRowsAsync(pool, built, null, null, null, repo, db, cancellationToken);
        if (rows.Count == 0) return new TrainingProbeSet([], "No scorable candidates to probe.");
        // Probe beliefs read calibrated signals; the probe pool isn't library-wide, so calibrate from a sample
        // (a no-op once the feed has populated it for this user this cycle).
        built = await EnsureCalibratedAsync(built, userId, null, repo, db, cancellationToken);
        var aspects = AspectScores(rows, built);                      // id -> [performers, content, quality, audio]
        var rated = await RecHelpers.FetchRatedAspectsAsync(db, userId, rows.Select(r => r.Id).ToList(), ProbeAspects.Select(a => a.key).ToList(), cancellationToken);
        var hasAudio = built.LikedAudioCentroids.Count > 0 || built.DislikedAudioCentroids.Count > 0;
        return RankAspectProbes(rows, aspects, rated, limit, "video", built.QualityVector is not null, hasAudio);
    }

    /// <summary>Fast aspect-centric IMAGE probes: reuse the cached model (shared taste + image visual clusters/look
    /// axis) and a SMALL candidate pool (neighbours of your image taste axis + liked image clusters) — instead of
    /// the old picker's full attribution rebuild + KNN union over the whole image library on every open.</summary>
    private async Task<TrainingProbeSet> GetImageProbesAsync(int userId, ICoreServices core, IServiceProvider sp, int limit, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();
        var built = await GetOrBuildBuiltAsync(userId, core, sp, ct);
        if (built.ImageTasteVector is null && (built.ImageLikedCentroids?.Count ?? 0) == 0 && built.GlobalTags.Count == 0)
            return new TrainingProbeSet([], "Engage with / rate some images or videos first — there's no taste model to probe yet.");

        var seen = (await core.Preference.GetTopLikedAsync(userId, "image", 1500, null, ct)).Select(e => e.Entity.EntityId).ToHashSet();
        var pool = new HashSet<int>();
        if (built.ImageTasteVector is not null)
            foreach (var h in await RecHelpers.KnnImageUnionAsync(search, built.ImageTasteVector, null, limit * 8 + 150, seen, ct)) pool.Add(h);
        foreach (var cn in (built.ImageLikedCentroids ?? []).Take(6))
            foreach (var h in await RecHelpers.KnnImageUnionAsync(search, cn, null, 60, seen, ct)) pool.Add(h);
        if (pool.Count == 0) return new TrainingProbeSet([], "No fresh images to probe right now.");

        var poolVecs = await RecHelpers.GetImageVisualVectorsAsync(repo, pool.ToList(), ct);
        var rows = await BuildImageRowsAsync(poolVecs, built, repo, db, ct);
        if (rows.Count == 0) return new TrainingProbeSet([], "No scorable images to probe.");
        built = await EnsureImageCalibratedAsync(built, userId, null, repo, db, ct);
        var aspects = AspectScores(rows, built);
        var rated = await RecHelpers.FetchRatedAspectsAsync(db, userId, rows.Select(r => r.Id).ToList(), ProbeAspects.Select(a => a.key).ToList(), ct, RatingHostType.Image);
        // Images have no quality/audio model → those aspects don't produce probes.
        return RankAspectProbes(rows, aspects, rated, limit, "image", hasQuality: false, hasAudio: false);
    }

    /// <summary>Shared active-learning ranker: per aspect, take UNRATED candidates whose probe VALUE = max(|derived
    /// belief|, novelty) is high (EXPLOIT a strong-but-unconfirmed belief, or EXPLORE something the model can't yet
    /// guess), then round-robin across aspects for a balanced batch. Used by both the video and image probes.</summary>
    private static TrainingProbeSet RankAspectProbes(IReadOnlyList<Cand> rows, Dictionary<int, double[]> aspects,
        HashSet<(int, string)> rated, int limit, string entityType, bool hasQuality, bool hasAudio)
    {
        static double Novelty(Cand r, string key) => key switch { "performers" => r.FaceNovelty, "content" => r.ClusterNovelty, _ => 0 };
        var perAspect = new Dictionary<string, List<(int id, double score, double novelty)>>();
        for (var a = 0; a < ProbeAspects.Length; a++)
        {
            var key = ProbeAspects[a].key;
            if (key == "video_quality" && !hasQuality) continue;
            if (key == "audio" && !hasAudio) continue;
            var idx = a;
            perAspect[key] = rows
                .Where(r => !rated.Contains((r.Id, key)))
                .Select(r => (r.Id, score: aspects[r.Id][idx], novelty: Novelty(r, key)))
                .Where(x => Math.Max(Math.Abs(x.score), x.novelty) > 0.20)
                .OrderByDescending(x => Math.Max(Math.Abs(x.score), x.novelty))
                .ToList();
        }

        var probes = new List<TrainingProbe>();
        var used = new HashSet<int>();
        var cursor = perAspect.Keys.ToDictionary(k => k, _ => 0);
        while (probes.Count < limit && cursor.Any(c => c.Value < perAspect[c.Key].Count))
            foreach (var key in perAspect.Keys.ToList())
            {
                if (probes.Count >= limit) break;
                var list = perAspect[key];
                while (cursor[key] < list.Count && used.Contains(list[cursor[key]].id)) cursor[key]++;
                if (cursor[key] >= list.Count) continue;
                var (vid, score, novelty) = list[cursor[key]++];
                used.Add(vid);
                var label = ProbeAspects.First(x => x.key == key).label.ToLowerInvariant();
                var explore = novelty > Math.Abs(score);   // novelty drove the pick → the model is UNSURE, not confident
                var high = score > 0;
                string tag, rationale;
                if (explore)
                {
                    tag = "unfamiliar ❓";
                    rationale = key == "performers"
                        ? "This has a face unlike anyone you've rated — the model can't guess how you feel about the performers here. Your rating is high-value new info."
                        : key == "content"
                            ? $"This content doesn't match any of your liked OR disliked clusters — it's new territory for the model. Rate the {label} to place it."
                            : $"The model has little to go on for the {label} here — rating it adds new information.";
                }
                else
                {
                    tag = high ? "likely 👍" : "likely 👎";
                    rationale = high
                        ? $"The model predicts you'd LIKE the {label} here — from derived data, not a rating. Rate it to reinforce that."
                        : $"The model predicts you'd DISLIKE the {label} here — from derived data, not a rating. Rate it: if that's wrong, you fix a false assumption.";
                }
                probes.Add(new TrainingProbe(new EntityRef(entityType, vid), null, 0, "aspect", 0,
                    tag, high, key, Math.Clamp(Math.Max(Math.Abs(score), novelty), 0, 1), rationale));
            }

        return probes.Count == 0
            ? new TrainingProbeSet([], "Nothing high-value to probe right now — rate a few from the feed and come back.")
            : new TrainingProbeSet(probes, "Each item targets ONE aspect: some are strong-but-unconfirmed beliefs to confirm/correct (👍/👎), others are unfamiliar faces/content the model can't yet guess (❓). Rate the highlighted aspect.");
    }

    /// <summary>The four signed aspect scores per candidate (Performers/Content/Quality/Audio), centered on the
    /// given pool with the default sub-blend — the model's belief, independent of the user's feed knobs.</summary>
    private static Dictionary<int, double[]> AspectScores(IReadOnlyList<Cand> rows, Built built)
    {
        var res = new Dictionary<int, double[]>(rows.Count);
        foreach (var r in rows)
        {
            var (p, c, q, a) = CandAspects(r, built, DefaultSubWeights);
            res[r.Id] = [p.Value, c.Value, q.Value, a.Value];
        }
        return res;
    }

    // ── ITasteClusters ────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<TasteCluster>> GetClustersAsync(int userId, ICoreServices core, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var built = await BuildAsync(userId, core, sp, cancellationToken);
        return built.Niches.Select(n => new TasteCluster(
            n.Index.ToString(),
            n.Label,
            n.MemberIds.Count,
            $"{n.MemberIds.Count} liked videos")).ToList();
    }

    // ── ITasteProfile: the shared/global affinity ─────────────────────────────
    public async Task<TasteProfile> GetProfileAsync(int userId, ICoreServices core, int topN = 60, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var built = await BuildAsync(userId, core, sp, cancellationToken);
        return await RecHelpers.ToProfileAsync(sp.GetRequiredService<DbContext>(), built.GlobalTags, built.GlobalPerfs, built.GlobalStudios, topN,
            $"Global taste baseline across {built.Niches.Count} niches.", cancellationToken);
    }

    public async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TargetEntityType.Equals("image", StringComparison.OrdinalIgnoreCase))
            return await RecommendImagesAsync(request, cancellationToken);
        if (!request.TargetEntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
            return Empty("videos and images only");

        // FOUR aspect levers (who/what/how-shot/how-sounds), their ADVANCED sub-blends, and two DIVERSITY levers.
        double Kb(string key, double def) => RecHelpers.Knob(request.Knobs, key, def);
        var w = (performers: Kb("performersWeight", 0.75), content: Kb("contentWeight", 1.00),
                 quality: Kb("qualityWeight", 0.40), audio: Kb("audioWeight", 0.25));
        var sub = (face: Kb("faceWeight", 0.45), perfAff: Kb("performerAffinityWeight", 0.55),
                   tag: Kb("tagWeight", 0.45), taste: Kb("tasteWeight", 0.30),
                   niche: Kb("nicheWeight", 0.35), studio: Kb("studioWeight", 0.15),
                   perfAttr: Kb("performerAttributeWeight", 0.15),
                   qualityCraft: Kb("qualityCraftWeight", 1.00), objQual: Kb("objectiveQualityWeight", 1.00),
                   audioLift: Kb("audioLift", 1.60));
        var div = (content: Kb("contentDiversity", 0.0), performer: Kb("performerDiversity", 0.0));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var search = sp.GetRequiredService<IEmbeddingService>();
        var db = sp.GetRequiredService<DbContext>();

        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        var built = await GetOrBuildBuiltAsync(request.UserId, request.Core, sp, cancellationToken);
        var msBuild = swBuild.ElapsedMilliseconds;
        if (built.Niches.Count == 0)
            return Empty("no taste model yet (rate or watch some videos first)");

        // Candidate generation: pull per niche (or one when scoped), keeping the KNN vectors so we don't refetch.
        var targets = request.ClusterId is { } cid && int.TryParse(cid, out var ci)
            ? built.Niches.Where(n => n.Index == ci).ToList()
            : built.Niches;
        if (targets.Count == 0) targets = built.Niches;
        // "Recommend from this" scopes to one niche: score every candidate against IT, not its best-of-all niche.
        var scopedNiche = request.ClusterId is not null && targets.Count == 1 ? targets[0] : null;

        // For SimilarToEntity, seed from the entity instead of the niches.
        float[]? seedFeat = null, seedSem = null;
        var exclude = built.Seen;
        if (request.Context == RecommendationContext.SimilarToEntity && request.Seed is { } s && s.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            var sv = (await RecHelpers.GetVisualVectorsAsync(repo, [s.EntityId], cancellationToken)).GetValueOrDefault(s.EntityId);
            if (sv is null || (sv.Feature is null && sv.Semantic is null)) return Empty("seed has no visual embedding");
            seedFeat = sv.Feature; seedSem = sv.Semantic; exclude = [s.EntityId];
        }

        // Determine the UNIVERSE to rank, then compute each candidate's RAW per-signal values. That work
        // (fetching embeddings/meta/faces + niche assignment) is knob-INDEPENDENT and by far the most expensive
        // part, so it is PRECOMPUTED at startup and after every model rebuild.
        //
        // Crucially the warm pass covers the WHOLE library, so a filter or search — which can only ever narrow it —
        // is served by taking the matching subset of those rows rather than re-scoring anything. Only two cases
        // genuinely need fresh scoring: a SEED (its neighbours are scored against the seed) and a niche SCOPE (rows
        // are scored against that one niche, not each candidate's best-fitting one). Those fall back to the
        // short-lived row cache, which paging, sort direction and ALL knob tuning still reuse.
        var unscoped = scopedNiche is null && seedFeat is null && seedSem is null;
        bool libraryWide = request.CandidateIds is not { Count: > 0 } && unscoped;
        var warmHit = false;
        List<Cand> rows;
        if (seedFeat is not null || seedSem is not null)
        {
            // SimilarToEntity: rank the seed's visual neighbours (per-seed, so not cached).
            var seedVecs = await RecHelpers.KnnUnionVecsAsync(search, seedFeat, seedSem, request.Limit * 6 + 50, exclude, cancellationToken);
            rows = await BuildScoredRowsAsync(seedVecs, built, scopedNiche, seedFeat, seedSem, repo, db, cancellationToken);
        }
        else if (unscoped && TryTakeWarm(_warmVideo, request.UserId, built, needsFullLibrary: !libraryWide) is { } warmRows)
        {
            if (request.CandidateIds is { Count: > 0 } filtered)
            {
                var allowed = filtered.ToHashSet();
                rows = warmRows.Where(r => allowed.Contains(r.Id)).ToList();
            }
            else rows = warmRows;
            warmHit = true;
        }
        else
        {
            // Universe = the pre-filtered ids (standard filter/search) OR the whole library by default, so the
            // ranked list spans everything you have (like any list page) rather than a small candidate pool.
            var universeKey = request.CandidateIds is { Count: > 0 } fu ? $"f{fu.Count}:{HashIds(fu)}" : "all";
            var rowKey = $"{request.UserId}:{universeKey}:{scopedNiche?.Index.ToString() ?? "-"}";
            if (!TryGetCachedRows(rowKey, out rows!))
            {
                var universeIds = request.CandidateIds is { Count: > 0 } cu
                    ? cu.Distinct().ToList()
                    : await RecHelpers.AllVideoIdsAsync(db, UniverseCap, cancellationToken);
                var vecs = await RecHelpers.GetVisualVectorsAsync(repo, universeIds, cancellationToken);
                rows = await BuildScoredRowsAsync(vecs, built, scopedNiche, null, null, repo, db, cancellationToken);
                StoreCachedRows(rowKey, rows);
            }
        }
        if (rows.Count == 0) return Empty("no scorable candidates (no unseen videos)");
        var candCount = rows.Count;

        // Stage-0 calibration, folded into THIS pass when it's the whole (unfiltered) library — a filtered/seeded/
        // scoped pool isn't a valid library-wide reference, so those calibrate from a strided sample instead. A warm
        // hit is already calibrated (the warm pass folds it in), so this is a no-op there.
        built = await EnsureCalibratedAsync(built, request.UserId, libraryWide ? rows : null, repo, db, cancellationToken);
        // A cold library-wide read just did the warm pass's work by hand — publish it so the background warm this
        // miss scheduled sees the model is already warm and returns instead of scanning the library a second time.
        if (libraryWide && !warmHit) _warmVideo[request.UserId] = new WarmSet(rows, built, DateTime.UtcNow, rows.Count < UniverseCap);

        // FUSION v5 (recommendations/docs/FUSION_DESIGN.md). Per candidate: calibrate signals → per-aspect belief
        // (Stage 1) → dead-band + gate + atanh-accumulate into a ranking key K (Stage 2). Score = σ(τ·K) is display
        // only; ranking is K. Confidence is a separate lever-weighted mean of aspect confidences. Scoring reads
        // (never mutates) cached rows, so concurrent requests with different levers are safe.
        var subW = new SubWeights(sub.face, sub.perfAff, sub.tag, sub.taste, sub.niche, sub.studio, sub.perfAttr, sub.qualityCraft, sub.objQual, sub.audioLift);
        // Every declared score dimension for every candidate, computed once (see DimIndex for the layout): the four
        // aspect beliefs, the fused overall score, and its confidence. Sorting and range-filtering both read this,
        // so "shuffle among videos whose performer score is above 0.3" costs one pass, not two.
        var dims = new Dictionary<int, double[]>(rows.Count);
        var confById = new Dictionary<int, double>(rows.Count);
        foreach (var r in rows)
        {
            var (p, cc, q, au) = CandAspects(r, built, subW);
            var o = FuseOverall([(w.performers, p), (w.content, cc), (w.quality, q), (w.audio, au)]);
            confById[r.Id] = o.conf;
            dims[r.Id] = [1.0 / (1.0 + Math.Exp(-DisplayGain * o.key)), p.Value, cc.Value, q.Value, au.Value, o.conf];
        }

        // Filtering, sorting, diversity and paging are the SHARED pipeline every recommender runs — see
        // RankedFeed. Only the diversity rule below is specific to this model; the rest must not differ between
        // recommenders, which is exactly why it doesn't live here any more.
        var byRow = rows.ToDictionary(r => r.Id);
        var candidates = rows
            .Select(r => new ScoredCandidate(r.Id, dims[r.Id][0], confById[r.Id], dims[r.Id]))
            .ToList();

        var (pageCandidates, total) = RankedFeed.SelectPage(request, candidates, DimensionKeys, ordered =>
        {
            if (div.content <= 1e-3 && div.performer <= 1e-3) return ordered;
            // Greedily fill a FIXED-size top window: each pick's score is penalized by how many already-picked
            // results share its content cluster (contentDiversity) or its performers (performerDiversity), so the
            // top spreads across clusters/people instead of the same few. The window is fixed and the greedy is
            // deterministic → pagination stays consistent. Below the window it's pure score order.
            int poolSize = Math.Min(ordered.Count, 1200);
            int window = Math.Min(poolSize, 400);
            var pool = ordered.Take(poolSize).ToList();
            var used = new bool[pool.Count];
            var nicheCount = new Dictionary<int, int>();
            var perfCount = new Dictionary<int, int>();
            var picked = new List<ScoredCandidate>(window);
            for (var k = 0; k < window; k++)
            {
                double best = double.NegativeInfinity; int bi = -1;
                for (var i = 0; i < pool.Count; i++)
                {
                    if (used[i]) continue;
                    var cr = byRow[pool[i].Id];
                    var pen = div.content * 0.20 * nicheCount.GetValueOrDefault(cr.Niche)
                            + div.performer * 0.20 * cr.PerfIds.Sum(pp => perfCount.GetValueOrDefault(pp));
                    var adj = pool[i].Score - pen;
                    if (adj > best) { best = adj; bi = i; }
                }
                if (bi < 0) break;
                used[bi] = true;
                var pr = byRow[pool[bi].Id];
                nicheCount[pr.Niche] = nicheCount.GetValueOrDefault(pr.Niche) + 1;
                foreach (var pp in pr.PerfIds) perfCount[pp] = perfCount.GetValueOrDefault(pp) + 1;
                picked.Add(pool[bi]);
            }
            var pickedIds = picked.Select(x => x.Id).ToHashSet();
            return picked.Concat(ordered.Where(x => !pickedIds.Contains(x.Id))).ToList();
        });
        if (total == 0) return Empty("no videos match those score filters");
        var sortKey = string.IsNullOrWhiteSpace(request.SortKey) ? RecommendationRequest.SortByOverall : request.SortKey!;
        var paged = pageCandidates.Select(c => (r: byRow[c.Id], score: c.Score)).ToList();

        var labelByNiche = built.Niches.ToDictionary(n => n.Index, n => n.Label);
        var names = new
        {
            Tags = await RecHelpers.TagNamesAsync(db, paged.SelectMany(x => x.r.TopTags.Select(t => t.id)), cancellationToken),
            Perfs = await RecHelpers.PerformerNamesAsync(db, paged.SelectMany(x => x.r.TopPerfs.Append(x.r.PerfDriverId)), cancellationToken),
        };

        static string Rel(double d) => $"{d * 100:+0;-0}% vs your typical";       // calibrated: 0 = your library-typical
        static string ConfTag(double c) => $"conf {c * 100:0}%";
        // An ABSENT signal (conf≈0) carries no belief — its calibrated value is just where raw-0 lands in the
        // reference of videos that HAVE the signal (usually the bottom → a spurious big negative). Show neutral
        // instead, matching the "no data" detail, so an absent face/attribute doesn't LOOK like a disliked one.
        static double Shown(double d, double conf) => conf > 1e-6 ? d : 0;
        var hasAudioModel = built.LikedAudioCentroids.Count > 0 || built.DislikedAudioCentroids.Count > 0;
        var items = paged.Select(x =>
        {
            var r = x.r;
            var (pAsp, cAsp, qAsp, aAsp) = CandAspects(r, built, subW);
            // The "Why" reads out the four ASPECTS: each row's number is the aspect's signed BELIEF (calibrated —
            // 0 = your library-typical), the detail carries its CONFIDENCE. ↳ sub-rows show each signal's own
            // calibrated value + conf, so you can see WHICH signal drove the belief and how sure it is. Numbers are
            // RELATIVE to your library ("vs your typical"), NOT absolute like/dislike claims.
            var perfNames = r.TopPerfs.Count > 0 ? string.Join(", ", r.TopPerfs.Select(p => names.Perfs.GetValueOrDefault(p, $"#{p}"))) : "—";
            var pseudoNote = r.PseudoCount > 0 ? $"{r.PseudoCount} recurring unknown face{(r.PseudoCount > 1 ? "s" : "")}" : "";
            var castDesc = perfNames != "—" ? (pseudoNote == "" ? perfNames : $"{perfNames} +{pseudoNote}")
                                            : (pseudoNote == "" ? "no known performers" : pseudoNote);
            // Show the driver tags with a −/+ sign so a dragging-down tag is visible (explains a below-typical Tags value).
            var tagNames = r.TopTags.Count > 0 ? string.Join(", ", r.TopTags.Select(t => (t.aff < 0 ? "−" : "") + names.Tags.GetValueOrDefault(t.id, $"#{t.id}"))) : "—";
            var nicheLabel = labelByNiche.GetValueOrDefault(r.Niche, $"#{r.Niche}");
            double dAff = r.PerfDirect ? Math.Clamp(r.Perf, -0.999, 0.999) : built.Cal("perfAff", r.Perf);
            double dFace = built.Cal("face", r.Face), dAttr = built.Cal("perfAttr", r.PerfAttr);
            double dTag = built.Cal("tag", r.TagRaw), dClus = built.Cal("cluster", r.NicheFit), dLook = built.Cal("look", r.Taste), dStud = built.Cal("studio", r.Studio);
            double dQual = built.Cal("quality", r.Quality), dAud = built.Cal("audio", r.AudioFit);
            var clusterDetail = dClus > 0.1 ? $"fits {nicheLabel}" : dClus < -0.1 ? "unlike your clusters" : "typical fit";
            // Explain the affinity: WHO drives it, your explicit/learned baseline for them, the value it became
            // after the niche blend (an explicit rating gets shrunk toward niche-local engagement), then the
            // calibrated readout — so a diluted or compressed high rating is visible instead of just a number.
            string PerfAffDetail()
            {
                if (r.PerfKnownFrac <= 0) return "no rated/watched performers";
                var who = r.PerfDriverId >= PseudoPerfOffset ? "recurring unknown face"
                          : names.Perfs.GetValueOrDefault(r.PerfDriverId, "top performer");
                // Explicit rating: honored directly (not percentile-compressed), so show it as your rating, not "vs typical".
                if (r.PerfDirect)
                    return $"{who}: your explicit rating {r.Perf:+0.00}, used directly · known {r.PerfKnownFrac * 100:0}%";
                var basis = built.GlobalPerfs.TryGetValue(r.PerfDriverId, out var gd)
                    ? (Math.Abs(gd.Affinity - r.Perf) > 0.08 ? $"learned {gd.Affinity:+0.00} → niche-blended {r.Perf:+0.00}" : $"learned {gd.Affinity:+0.00}")
                    : $"{r.Perf:+0.00}";
                return $"{who}: {basis} → {Rel(dAff)} · known {r.PerfKnownFrac * 100:0}%";
            }
            var factors = new List<ExplanationFactor>
            {
                new("performers", "Performers", pAsp.Value, $"{castDesc} · {ConfTag(pAsp.Conf)}"),
                new("perf.aff", "↳ Performer affinity", Shown(dAff, r.PerfKnownFrac), PerfAffDetail()),
            };
            if (built.LikedFaceCentroids.Count > 0 || built.DislikedFaceCentroids.Count > 0)
                factors.Add(new("perf.face", "↳ Face match", Shown(dFace, r.FaceConf), r.FaceConf > 0 ? $"{Rel(dFace)} · {ConfTag(r.FaceConf)}" : "novel/absent face — low conf"));
            if (built.PerfAttrPrior is { Count: > 0 })
                factors.Add(new("perf.attr", "↳ Performer attributes", Shown(dAttr, r.PerfAttrConf), r.PerfAttrConf > 0 ? $"{Rel(dAttr)} · {ConfTag(r.PerfAttrConf)}" : "no attribute data for this cast"));
            factors.Add(new("content", "Content", cAsp.Value, $"{tagNames} · {clusterDetail} · {ConfTag(cAsp.Conf)}"));
            factors.Add(new("content.tag", "↳ Tags", Shown(dTag, r.TagKnownFrac), r.TagKnownFrac > 0 ? $"{Rel(dTag)} · known {r.TagKnownFrac * 100:0}% · {tagNames}" : "no rated tags"));
            factors.Add(new("content.niche", "↳ Cluster fit", Shown(dClus, r.ClusterConf), $"{Rel(dClus)} · {clusterDetail}"));
            if (built.TasteVector is not null) factors.Add(new("content.look", "↳ Look axis", Shown(dLook, r.LookConf), r.LookConf > 0 ? Rel(dLook) : "no visual embedding"));
            if (r.StudioPresent) factors.Add(new("content.studio", "↳ Studio", Shown(dStud, r.StudioConf), Rel(dStud)));
            factors.Add(new("quality", "Quality", qAsp.Value, built.QualityVector is not null && r.QualityConf > 0 ? $"{Rel(dQual)} · {ConfTag(qAsp.Conf)}" : "rate the “video quality” aspect to train"));
            factors.Add(new("audio", "Audio", aAsp.Value, !hasAudioModel ? "rate the “audio” aspect to train" : r.AudioPresent ? $"{Rel(dAud)} · {ConfTag(r.AudioConf)}" : "no audio embedding for this video"));
            return new ItemScore("video", r.Id, Math.Clamp(x.score, 0, 1), Math.Clamp(confById[r.Id], 0, 1),
                new Explanation($"{nicheLabel} · {Band(x.score)}", factors));
        }).ToList();

        return new RecommendationResult(items, TotalCount: total, Diagnostics: new Dictionary<string, object>
        {
            ["recommender"] = RecommenderId,
            ["niches"] = built.Niches.Count,
            ["candidates"] = candCount,
            ["matched"] = total,
            ["sort"] = sortKey,
            ["taste_vector"] = built.TasteVector is not null,
            ["face_centroids"] = built.LikedFaceCentroids.Count,
            ["disliked_clusters"] = built.DislikedNicheCentroids.Count,
            ["quality_model"] = built.QualityVector is not null,
            ["audio_model"] = built.LikedAudioCentroids.Count + built.DislikedAudioCentroids.Count,
            ["audio_coverage"] = candCount > 0 ? $"{rows.Count(r => r.AudioPresent) * 100 / candCount}%" : "0%",
            ["calibrated"] = built.Calibrations?.Count ?? 0,
            ["learned_curves"] = built.Calibrations?.Values.Count(c => c.HasCurve) ?? 0,
            ["attr_cells"] = built.AttrCells?.Count ?? 0,
            ["attr_priors"] = built.PerfAttrPrior?.Count ?? 0,
            ["pseudo_perfs"] = built.PseudoPerfIds?.Count ?? 0,
            ["ms_build"] = msBuild,
            ["warm"] = warmHit,
        });
    }

    public async Task<IReadOnlyList<ItemScore>> ScoreItemsAsync(ScoreRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        // The cached/persisted model, NOT a fresh BuildAsync — scoring a handful of items must never trigger a full
        // model rebuild (auto-curation calls this per item, which made every call pay for clustering).
        var built = await GetOrBuildBuiltAsync(request.UserId, request.Core, sp, cancellationToken);
        var ids = request.Items.Where(i => i.EntityType.Equals("video", StringComparison.OrdinalIgnoreCase)).Select(i => i.EntityId).ToList();
        var vecs = await RecHelpers.GetVisualVectorsAsync(repo, ids, cancellationToken);
        var (wF, wS) = RecHelpers.VisualWeights(null);
        return request.Items.Select(item =>
        {
            if (built.Niches.Count == 0 || !vecs.TryGetValue(item.EntityId, out var v))
                return new ItemScore(item.EntityType, item.EntityId, 0, 0);
            var best = built.Niches.Max(n => Math.Max(0, Vectors.BlendedCosine(v.Feature, v.Semantic, n.FeatCentroid, n.SemCentroid, wF, wS)));
            return new ItemScore(item.EntityType, item.EntityId, best, Math.Min(1.0, built.Niches.Sum(n => n.MemberIds.Count) / 15.0));
        }).ToList();
    }

    // ── Self-evaluation ─────────────────────────────────────────────────────────
    /// <summary>Grade the recommender against the user's own rated/engaged videos: rank them with the DEFAULT knobs
    /// and report score↔rating alignment, per-signal calibration health, per-aspect correlation, and the biggest
    /// rating↔rank inversions — so systemic issues (compressed/dead signals, buried favorites) surface as numbers
    /// instead of one Why-panel at a time.</summary>
    public async Task<EvalReport> EvaluateAsync(int userId, ICoreServices core, IReadOnlyDictionary<string, double>? knobs = null, string split = "all", CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();
        var warnings = new List<string>();

        var built = await GetOrBuildBuiltAsync(userId, core, sp, cancellationToken);
        if (built.Niches.Count == 0)
            return new EvalReport(0, 0, "none", double.NaN, double.NaN, [], [], [], ["No taste model yet — engage with / rate some videos first."]);

        // Eval set = your signed engaged videos (what the model learns from) + their engagement scores.
        var engaged = (await core.Preference.GetTopLikedAsync(userId, "video", 2500, null, cancellationToken))
            .Where(e => Math.Abs(e.Score) > 0.05).ToList();
        var engagementById = new Dictionary<int, double>();
        foreach (var e in engaged) engagementById[e.Entity.EntityId] = e.Score;
        var ids = engagementById.Keys.ToList();
        if (ids.Count < 15)
            return new EvalReport(0, ids.Count, "none", double.NaN, double.NaN, [], [], [], ["Too few engaged videos to evaluate yet."]);

        // Explicit overall ratings (ground truth; rank-only, so the neutral/scale is irrelevant to correlation).
        var ratingRows = await db.Set<Rating>().AsNoTracking()
            .Where(rr => rr.UserId == userId && rr.HostType == RatingHostType.Video && rr.Aspect == "overall" && ids.Contains(rr.HostId))
            .Select(rr => new { rr.HostId, rr.Value }).ToListAsync(cancellationToken);
        var ratings = ratingRows.GroupBy(x => x.HostId).ToDictionary(g => g.Key, g => (double)g.Max(x => x.Value));

        var built2 = await EnsureCalibratedAsync(built, userId, null, repo, db, cancellationToken);
        var vecs = await RecHelpers.GetVisualVectorsAsync(repo, ids, cancellationToken);
        var rows = await BuildScoredRowsAsync(vecs, built2, null, null, null, repo, db, cancellationToken);
        if (rows.Count < 15)
            return new EvalReport(ratings.Count, rows.Count, "none", double.NaN, double.NaN, [], [], [], ["Too few scorable engaged videos (missing embeddings?) to evaluate."]);

        // Score with the given knobs (else the tuned defaults) so a sweep can measure alternatives live.
        double Kb(string k, double def) => knobs != null && knobs.TryGetValue(k, out var v) ? v : def;
        var (wp, wc, wq, wa) = (Kb("performersWeight", 0.75), Kb("contentWeight", 1.00), Kb("qualityWeight", 0.40), Kb("audioWeight", 0.25));
        var subW = new SubWeights(Kb("faceWeight", 0.45), Kb("performerAffinityWeight", 0.55), Kb("tagWeight", 0.45),
            Kb("tasteWeight", 0.30), Kb("nicheWeight", 0.35), Kb("studioWeight", 0.15), Kb("performerAttributeWeight", 0.15),
            Kb("qualityCraftWeight", 1.00), Kb("objectiveQualityWeight", 1.00), Kb("audioLift", 1.60));
        var score = new Dictionary<int, double>(rows.Count);
        var aspV = new Dictionary<int, (double pv, double cv, double qv, double av, double pcov, double ccov, double qcov, double acov)>(rows.Count);
        foreach (var r in rows)
        {
            var (p, c, q, a) = CandAspects(r, built2, subW);
            var o = FuseOverall([(wp, p), (wc, c), (wq, q), (wa, a)]);
            score[r.Id] = 1.0 / (1.0 + Math.Exp(-DisplayGain * o.key));
            aspV[r.Id] = (p.Value, c.Value, q.Value, a.Value, p.Coverage, c.Coverage, q.Coverage, a.Coverage);
        }

        // Ground truth: explicit ratings when there are enough, else engagement (a sanity floor — the model trains on it).
        var haveRatings = ratings.Count >= 25;
        IReadOnlyDictionary<int, double> target = haveRatings ? ratings : engagementById;
        if (!haveRatings)
            warnings.Add($"Only {ratings.Count} explicit overall ratings — grading against engagement, which the model trains on, so treat these as a sanity floor. Rate more videos for a true grade.");

        bool InSplit(int id) => split == "train" ? id % 2 == 0 : split == "test" ? id % 2 == 1 : true;
        var scored = rows.Where(r => target.ContainsKey(r.Id) && InSplit(r.Id)).Select(r => r.Id).ToList();
        double? sVsRating = ratings.Count >= 25 ? NN(Spearman(scored.Select(i => score[i]).ToArray(), scored.Select(i => ratings[i]).ToArray())) : null;
        var engIds = rows.Select(r => r.Id).Where(engagementById.ContainsKey).ToList();
        var sVsEng = NN(Spearman(engIds.Select(i => score[i]).ToArray(), engIds.Select(i => engagementById[i]).ToArray()));

        // Per-signal calibration health.
        var signals = new List<SignalEval>();
        foreach (var (key, aspectIdx, raw, conf) in SigDefs)
        {
            var present = rows.Where(r => conf(r) > 1e-9).ToList();
            var ds = present.Select(r => built2.Cal(key, raw(r))).ToArray();
            var presentFrac = (double)present.Count / rows.Count;
            var std = Std(ds);
            var silent = ds.Length > 0 ? ds.Count(d => Math.Abs(d) < DeadBand) / (double)ds.Length : 0;
            var pc = present.Where(r => target.ContainsKey(r.Id)).ToList();
            var corr = pc.Count >= 10 ? NN(Spearman(pc.Select(r => built2.Cal(key, raw(r))).ToArray(), pc.Select(r => target[r.Id]).ToArray())) : null;
            var hasCurve = built2.Calibrations is { } cals && cals.TryGetValue(key, out var cal) && cal.HasCurve;
            var degenerate = presentFrac > 0.02 && std < 0.05;
            signals.Add(new SignalEval(key, AspectName(aspectIdx), presentFrac, corr, std, ds.Length > 0 ? ds.Min() : 0, ds.Length > 0 ? ds.Max() : 0, silent, hasCurve, degenerate));
            if (degenerate) warnings.Add($"Signal '{key}' barely varies (std {std:0.00}) — effectively dead weight.");
            if (presentFrac > 0.1 && corr is { } cv && cv < -0.1) warnings.Add($"Signal '{key}' correlates NEGATIVELY with your target ({cv:0.00}) — possible miscalibration.");
        }

        // Per-aspect correlation + coverage.
        var aspNames = new[] { "performers", "content", "quality", "audio" };
        double AspVal(int id, int idx) => idx switch { 0 => aspV[id].pv, 1 => aspV[id].cv, 2 => aspV[id].qv, _ => aspV[id].av };
        double AspCov(int id, int idx) => idx switch { 0 => aspV[id].pcov, 1 => aspV[id].ccov, 2 => aspV[id].qcov, _ => aspV[id].acov };
        var aspects = new List<AspectEval>();
        for (var a = 0; a < 4; a++)
            aspects.Add(new AspectEval(aspNames[a],
                haveRatings && scored.Count >= 10 ? NN(Spearman(scored.Select(i => AspVal(i, a)).ToArray(), scored.Select(i => target[i]).ToArray())) : null,
                rows.Average(r => AspCov(r.Id, a))));

        // Inversions: biggest gaps between ground-truth percentile and recommender-score percentile.
        var tRank = PctRank(scored, target);
        var sRank = PctRank(scored, score);
        var inversions = scored
            .Select(id => (id, gap: tRank[id] - sRank[id], t: target[id], s: score[id], tp: tRank[id], sp: sRank[id]))
            .OrderByDescending(x => Math.Abs(x.gap)).Take(15)
            .Select(x => new EvalInversion(x.id, x.t, x.s, x.tp, x.sp, x.gap > 0 ? "you rate it high, we rank it low" : "you rate it low, we rank it high"))
            .ToList();

        return new EvalReport(ratings.Count, rows.Count, haveRatings ? "ratings" : "engagement", sVsRating, sVsEng, signals, aspects, inversions, warnings);
    }

    private static string AspectName(int idx) => idx switch { 0 => "performers", 1 => "content", 2 => "quality", _ => "audio" };
    private static double? NN(double v) => double.IsNaN(v) ? null : v;   // NaN → null (JSON-safe)
    internal static double Std(double[] x)
    {
        if (x.Length < 2) return 0;
        double m = x.Average(), s = 0;
        foreach (var v in x) s += (v - m) * (v - m);
        return Math.Sqrt(s / x.Length);
    }
    internal static double Spearman(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length < 3) return double.NaN;
        double[] ra = RankArray(a), rb = RankArray(b);
        double ma = ra.Average(), mb = rb.Average(), num = 0, da = 0, db = 0;
        for (var i = 0; i < ra.Length; i++) { double xa = ra[i] - ma, xb = rb[i] - mb; num += xa * xb; da += xa * xa; db += xb * xb; }
        return da <= 1e-12 || db <= 1e-12 ? double.NaN : num / Math.Sqrt(da * db);
    }
    internal static double[] RankArray(double[] x)
    {
        var idx = Enumerable.Range(0, x.Length).OrderBy(i => x[i]).ToArray();
        var r = new double[x.Length];
        var i = 0;
        while (i < idx.Length) { var j = i; while (j + 1 < idx.Length && x[idx[j + 1]] == x[idx[i]]) j++; var avg = (i + j) / 2.0; for (var k = i; k <= j; k++) r[idx[k]] = avg; i = j + 1; }
        return r;
    }
    private static Dictionary<int, double> PctRank(List<int> ids, IReadOnlyDictionary<int, double> val)
    {
        var sorted = ids.OrderBy(i => val[i]).ToList();
        var res = new Dictionary<int, double>(ids.Count);
        var n = Math.Max(1, ids.Count - 1);
        for (var k = 0; k < sorted.Count; k++) res[sorted[k]] = (double)k / n;
        return res;
    }

    // ── Build ──────────────────────────────────────────────────────────────────
    private sealed class Cand
    {
        public int Id; public int Niche; public double FeatureSim, SemanticSim, TagRaw, Perf, Studio, Taste, Face, NicheFit, Quality;
        public double AudioFit, AudioConf, FaceConf, PerfKnownFrac, TagKnownFrac, ClusterConf, LookConf, QualityConf, StudioConf;  // per-signal confidences [0,1]
        public double ObjQual, ObjQualConf;     // objective fidelity (resolution + bitrate), within Quality; conf 0 when neither is known
        public double PerfAttr, PerfAttrConf;   // attribute cold-start prior for the cast + its coverage-scaled confidence
        public int PseudoCount;                 // how many recurring unlinked (pseudo-performer) faces are on this video
        public int PerfDriverId;                // the cast member whose (blended) affinity drives Perf (for the Why)
        public double PerfConf;                 // confidence of the perfAff SIGNAL = the driver's own confidence
        public bool PerfDirect;                 // the driver is an EXPLICITLY-rated performer (bypass percentile compression)
        public bool IsImage;                    // scored against the IMAGE model/calibration (no audio/quality)
        public double FaceNovelty, ClusterNovelty;   // 1 = unlike anything you've rated (high value to ask about)
        public bool TagPresent, PerfPresent, StudioPresent, AudioPresent;
        public List<(int id, double aff)> TopTags = []; public List<int> TopPerfs = []; public List<int> PerfIds = [];
    }

    // Fixed blend used only to ASSIGN a candidate to its best niche (knob-independent so the cached rows don't
    // depend on the visual knobs). The knobs then weight feature/semantic similarity in the score itself.
    private const double NicheFeatBlend = 0.7;
    private const double NicheSemBlend = 0.3;
    // Cosine below which a candidate is treated as fitting a content cluster NOT AT ALL (membership 0).
    private const double NicheFitThreshold = 0.35;

    // Per-niche local-regression knobs: how sharply visual proximity concentrates membership, and how much
    // local evidence (effective near-video count) it takes for the niche's own signal to overtake the global.
    private const double LocalitySharpness = 4.0;
    private const double BlendLambda = 4.0;
    // Hard ceiling on how many videos the unfiltered feed ranks (the whole library up to this). Guards against a
    // pathologically huge library; a normal collection is well under it and gets fully ranked.
    private const int UniverseCap = 20000;
    // Clustering: how much the tag signature (vs visual) drives niche formation (0 = look only, 1 = tags only),
    // and the minimum AI-tag coverage fraction to count a tag as present (drops fleeting detections).
    private const double TagBlend = 0.5;
    private const double MinTagCoverage = 0.05;
    // AI tags carry extra info but are less trusted than user-curated manual tags — discount them a little so
    // they enrich the signature without over-weighting the manual ones (which stay at full 1.0).
    private const double AiTagTrust = 0.8;
    // Watch-attention lives in the PREFERENCE model (attribution), NOT the cluster geometry: it moderately
    // scales how much a liked video credits the tags you actually watched. A nudge (skipped tags credit ~60%),
    // not a takeover — because where you watched isn't reliably what you liked most.
    private const double AttentionStrength = 0.4;
    // Face-taste model: the neutral rating for the (strongest) performer FACE-aspect signal, and how heavily an
    // explicit face rating weighs vs a face merely appearing in a liked video (in on-screen-second units).
    private const double FaceRatingNeutral = 50;
    private const double FaceRatingWeight = 300;
    // Audio-taste model: neutral for the explicit per-video "audio" aspect rating, and how much an explicit audio
    // rating outweighs an audio sample derived from mere overall engagement (the aspect rating is authoritative).
    private const double AudioRatingNeutral = 50;
    private const double AudioRatingWeight = 4.0;
    // How much an explicit "content" aspect rating outweighs an overall-engagement-derived sample when learning
    // the content look-axis — so a video disliked only for its performers/audio doesn't taint the CONTENT axis.
    private const double ContentRatingWeight = 4.0;

    private sealed record Niche(int Index, string Label, List<int> MemberIds, float[]? FeatCentroid, float[]? SemCentroid,
        Dictionary<int, double> Tags, Dictionary<int, double> Performers, Dictionary<int, double> Studios, List<(int id, double weight)> TopTags);

    private sealed record Built(List<Niche> Niches, Dictionary<int, RecHelpers.AffinityDetail> GlobalTags,
        Dictionary<int, RecHelpers.AffinityDetail> GlobalPerfs, Dictionary<int, RecHelpers.AffinityDetail> GlobalStudios, HashSet<int> Seen,
        float[]? TasteVector, List<float[]> LikedFaceCentroids, List<float[]> DislikedFaceCentroids,
        List<float[]> LikedAudioCentroids, List<float[]> DislikedAudioCentroids,
        List<float[]> DislikedNicheCentroids, float[]? QualityVector,
        Dictionary<string, Calib>? Calibrations = null,
        // Attribute cold-start model: learned per-attribute-cell affinities (SERIALIZED — compact), and the
        // per-performer prior derived from them for every content performer (IN-MEMORY — recomputed on load).
        Dictionary<string, (double aff, double conf)>? AttrCells = null,
        Dictionary<int, (double prior, double conf)>? PerfAttrPrior = null,
        // Raw faceIds that became pseudo-performers (recurring unlinked faces). At scoring, a candidate face in
        // this set is injected into the cast as (faceId + PseudoPerfOffset). Serialized. See BuildAsync.
        HashSet<int>? PseudoPerfIds = null,
        // Signed engagement score per rated video — the supervision for the learned calibration curves. Serialized.
        Dictionary<int, double>? EngagedScores = null,
        // IMAGE half: images share the taste vocabulary (tags/performers/studios/faces/attrs) but have their OWN
        // visual space + no audio/temporal — so they get their own look axis, content clusters, and calibration.
        // Serialized (centroids/vectors); ImageCalibrations is re-folded lazily like the video ones.
        float[]? ImageTasteVector = null,
        List<float[]>? ImageLikedCentroids = null, List<float[]>? ImageDislikedCentroids = null,
        Dictionary<string, Calib>? ImageCalibrations = null)
    {
        /// <summary>Calibrate a raw signal value to d ∈ [-1,1] against the library-wide reference (0 if none).
        /// Images use their own calibration set (different visual distribution); shared signals still calibrate
        /// against the image reference so scales stay commensurable within the image feed.</summary>
        public double Cal(string key, double raw, bool image = false)
        {
            var cals = image ? ImageCalibrations : Calibrations;
            return cals is not null && cals.TryGetValue(key, out var c) ? c.D(raw) : 0;
        }

        /// <summary>A model with nothing learned in it, returned while a real one is still being built in the
        /// background. Every caller already guards on <c>Niches.Count == 0</c> (there is no model until the user
        /// has engaged with something), so this needs no new handling — it just routes through those paths.</summary>
        public static Built Empty { get; } = new([], [], [], [], [], null, [], [], [], [], [], null);
    }

    private async Task<Built> BuildAsync(int userId, ICoreServices core, IServiceProvider sp, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();

        var engaged = await core.Preference.GetTopLikedAsync(userId, "video", 1500, null, ct);
        var seen = engaged.Select(e => e.Entity.EntityId).ToHashSet();
        var liked = engaged.Where(e => e.Score > 0.1).Take(120).ToList();

        var vectors = await RecHelpers.GetVisualVectorsAsync(repo, liked.Select(e => e.Entity.EntityId).ToList(), ct);
        var likedVec = liked.Where(e => vectors.TryGetValue(e.Entity.EntityId, out var v) && v.Feature is not null).ToList();
        if (likedVec.Count == 0) return new Built([], new(), new(), new(), seen, null, [], [], [], [], [], null);

        // Signed engagement + EFFECTIVE tags. Video.TagIds is MANUAL tags only — enrich the metadata with
        // AI-applied tags (TagApplication, coverage-weighted) so the whole model (clustering, attribution,
        // profile) actually sees them; MinTagCoverage drops fleeting AI tags as noise.
        var signed = engaged.Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var signedIds = signed.Select(e => e.Entity.EntityId).ToList();
        var signedMeta = await RecHelpers.FetchVideoMetaAsync(db, signedIds, ct);          // AI tags now included by default
        var effTags = await RecHelpers.FetchVideoEffectiveTagsAsync(db, signedIds, MinTagCoverage, ct, AiTagTrust); // coverage WEIGHTS for clustering
        var total = await RecHelpers.TotalVideosAsync(db, ct);
        var tagCounts = await RecHelpers.TagVideoCountsAsync(db, signedMeta.Values.SelectMany(m => m.TagIds).Distinct().ToList(), ct);
        var studioCounts = await RecHelpers.StudioVideoCountsAsync(db, signedMeta.Values.Where(m => m.StudioId.HasValue).Select(m => m.StudioId!.Value).Distinct().ToList(), ct);
        var perfCounts = await RecHelpers.PerformerVideoCountsAsync(db, signedMeta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList(), ct);
        var aspectTargets = await RecHelpers.FetchAspectTargetsAsync(db, userId, signedMeta.Keys.ToList(), ct);
        var tagChildren = await RecHelpers.FetchTagChildrenAsync(db, ct);
        // Watch-attention as a PREFERENCE nudge: credit tags you actually watched a bit more when learning affinity.
        var attentionCells = await RecHelpers.BuildAttentionTagCellsAsync(db, userId, signedIds, AttentionStrength, ct);

        // ── FACE-TASTE MODEL (built BEFORE attribution so face-fit can DE-CONFOUND: a video you rate low only
        // because of the faces shouldn't drag down its content/tags). Liked faces = faces in your liked videos
        // (score × on-screen time) + faces of performers you rate high on FACE (the strongest explicit signal);
        // disliked = faces in disliked videos / face-rated-low performers.
        var faceApp = await RecHelpers.FetchVideoFaceAppearancesAsync(db, signedIds, ct);
        var faceWeight = new Dictionary<int, double>();
        foreach (var e in signed)
            if (faceApp.TryGetValue(e.Entity.EntityId, out var fs))
            {
                // DE-CONTAMINATE the liked-face centroids: distribute each video's like/dislike among its faces by
                // PROMINENCE (duration share) rather than raw duration — so a fleeting co-star/background face in a
                // video you liked contributes almost nothing to the "faces you like" model. Prevents co-stars from
                // polluting the centroids (the same de-confounding we do for performers, applied to the face model).
                var totalDur = fs.Sum(x => Math.Max(1.0, x.durationSec));
                foreach (var (fid, dur) in fs) faceWeight[fid] = faceWeight.GetValueOrDefault(fid) + e.Score * (Math.Max(1.0, dur) / totalDur);
            }
        var signedPerfIds = signedMeta.Values.SelectMany(m => m.PerformerIds).Distinct().ToList();
        var faceRatings = await RecHelpers.FetchPerformerFaceRatingsAsync(db, userId, signedPerfIds, FaceRatingNeutral, ct);
        if (faceRatings.Count > 0)
        {
            var perfFaces = await RecHelpers.FetchPerformerFacesAsync(db, faceRatings.Keys.ToList(), ct);
            foreach (var (pid, rating) in faceRatings)
                if (perfFaces.TryGetValue(pid, out var fids))
                    foreach (var fid in fids) faceWeight[fid] = faceWeight.GetValueOrDefault(fid) + rating * FaceRatingWeight;
        }
        var faceEmb = await RecHelpers.GetFaceEmbeddingsAsync(repo, faceWeight.Keys.ToList(), ct);
        var likedFaces = faceWeight.Where(kv => kv.Value > 0 && faceEmb.ContainsKey(kv.Key)).Select(kv => (vec: faceEmb[kv.Key], w: kv.Value)).ToList();
        var dislikedFaces = faceWeight.Where(kv => kv.Value < 0 && faceEmb.ContainsKey(kv.Key)).Select(kv => faceEmb[kv.Key]).ToList();
        var likedFaceCentroids = likedFaces.Count >= 2
            ? RecHelpers.RefinedClusters(likedFaces.Select(x => x.vec).ToList(), likedFaces.Select(x => x.w).ToList(), Math.Clamp(likedFaces.Count / 8, 2, 10)).Centroids
            : likedFaces.Select(x => x.vec).ToList();
        var dislikedFaceCentroids = dislikedFaces.Count >= 3
            ? Vectors.KMeans(dislikedFaces, dislikedFaces.Select(_ => 1.0).ToList(), Math.Clamp(dislikedFaces.Count / 8, 1, 5))
            : dislikedFaces;
        var faceFit = new Dictionary<int, double>();
        if (likedFaceCentroids.Count > 0)
            foreach (var vid in signedIds)
                faceFit[vid] = RecHelpers.FaceFit(faceApp.GetValueOrDefault(vid), faceEmb, likedFaceCentroids, dislikedFaceCentroids);

        // ── PSEUDO-PERFORMERS: an UNLINKED face identity (Face.PerformerId == null) that RECURS across ≥N of your
        // engaged videos is promoted to a first-class performer-like entity (its faceId offset into a reserved id
        // range) and injected into the attribution's performer columns. The ridge then learns its OWN affinity —
        // so a disliked recurring co-star absorbs his own rating drag instead of it landing on the LINKED cast
        // (real de-confounding, beyond the average-effect covariate below), and a LOVED unlinked face becomes a
        // positive match signal. Requires face detection to have run.
        var allFaceIds = faceApp.Values.SelectMany(l => l.Select(x => x.faceId)).Distinct().ToList();
        var unlinkedFaces = await RecHelpers.FetchUnlinkedFaceIdsAsync(db, allFaceIds, ct);
        var faceEngagedCount = new Dictionary<int, int>();
        foreach (var faces in faceApp.Values)
            foreach (var f in faces.Select(x => x.faceId).Distinct())
                if (unlinkedFaces.Contains(f)) faceEngagedCount[f] = faceEngagedCount.GetValueOrDefault(f) + 1;
        var pseudoFaceIds = faceEngagedCount.Where(kv => kv.Value >= MinPseudoAppearances).Select(kv => kv.Key).ToHashSet();

        // UNLINKED-CAST load: distinct detected faces beyond the linked cast AND beyond the recurring pseudo-
        // performers (those now have their own columns) — i.e. the RESIDUAL one-off unlinked faces. A de-confound
        // covariate so a video's transient extra people don't drag its content/linked cast down.
        var unlinkedLoad = new Dictionary<int, double>();
        foreach (var vid in signedIds)
        {
            if (!faceApp.TryGetValue(vid, out var fl)) continue;
            var distinctFaces = fl.Select(x => x.faceId).Distinct().ToList();
            var perfCount = signedMeta.TryGetValue(vid, out var mm) ? mm.PerformerIds.Length : 0;
            var extra = distinctFaces.Count - perfCount - distinctFaces.Count(pseudoFaceIds.Contains);
            if (extra > 0) unlinkedLoad[vid] = Math.Min(1.0, extra / 2.0);
        }

        // Inject the recurring unlinked faces into each engaged video's cast (offset ids) so attribution + niche
        // blending + scoring treat them exactly like performers; give them library appearance counts for IDF.
        if (pseudoFaceIds.Count > 0)
        {
            var pseudoCounts = await RecHelpers.FaceVideoCountsAsync(db, pseudoFaceIds.ToList(), ct);
            foreach (var f in pseudoFaceIds) perfCounts[f + PseudoPerfOffset] = pseudoCounts.GetValueOrDefault(f, 1);
            foreach (var vid in signedIds)
            {
                if (!signedMeta.TryGetValue(vid, out var m) || !faceApp.TryGetValue(vid, out var fl)) continue;
                var extra = fl.Select(x => x.faceId).Where(pseudoFaceIds.Contains).Distinct().Select(f => f + PseudoPerfOffset).ToArray();
                if (extra.Length > 0) signedMeta[vid] = m with { PerformerIds = [.. m.PerformerIds, .. extra] };
            }
        }

        // GLOBAL attribution (shared baseline), now with AI tags, watch-attention, face-fit AND unlinked-cast de-confounding.
        var global = await RecHelpers.AttributeAttributionAsync(core, userId, signed.Select(e => (e.Entity.EntityId, e.Score)), signedMeta, tagCounts, studioCounts, perfCounts, total, null, ct, aspectTargets, tagChildren: tagChildren, attentionTagCells: attentionCells, faceFit: faceFit, unlinkedLoad: unlinkedLoad);

        // MULTIMODAL clustering: each liked video's visual embedding ⊕ its coverage×IDF tag signature, so niches
        // separate by CONTENT (co-occurring distinctive tags) even when they look alike. NOTE: watch-attention
        // was tried in this signature (weight tags by where you watched) but it reshaped the clusters away from
        // the content-based grouping that works better — "where you watched" isn't reliably "what you liked", so
        // it belongs in the preference model (attribution/scoring), not the cluster geometry.
        double Idf(int t) => Math.Max(0.0, Math.Log(total / (1.0 + tagCounts.GetValueOrDefault(t, 1))));
        var tagSigs = likedVec.Select(e =>
        {
            var sig = new Dictionary<int, double>();
            if (effTags.TryGetValue(e.Entity.EntityId, out var et))
                foreach (var (t, cover) in et) sig[t] = cover * Idf(t);
            return (IReadOnlyDictionary<int, double>)sig;
        }).ToList();
        var visualPts = likedVec.Select(e => (float[]?)vectors[e.Entity.EntityId].Feature).ToList();
        var wts = likedVec.Select(e => Math.Max(0.01, e.Score)).ToList();
        var cr = RecHelpers.RefinedMultimodalClusters(visualPts, tagSigs, wts, TagBlend, Math.Clamp(visualPts.Count / 7, 3, 12), mergeCos: 0.90, minMembers: 2);
        var globalTags = global.Tags.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);
        var globalPerfs = global.Performers.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);
        var globalStudios = global.Studios.ToDictionary(kv => kv.Key, kv => kv.Value.Affinity);

        // Visual vectors for the (capped) signed set → per-niche SOFT MEMBERSHIP for the local regressions.
        var signedForNiche = signed.OrderByDescending(e => Math.Abs(e.Score)).Take(600).ToList();
        var signedVecs = await RecHelpers.GetVisualVectorsAsync(repo, signedForNiche.Select(e => e.Entity.EntityId).ToList(), ct);
        var nicheOpts = new RecHelpers.AttributionOptions(MaxFeatures: 150);

        // Shrink each niche's local estimate toward the global prior by the niche's LOCAL evidence for that
        // attribute (effective near-video count). Thin niches lean on global; rich ones express their own taste.
        static double Blend(double global, double local, double eff) { var conf = eff / (eff + BlendLambda); return global * (1 - conf) + local * conf; }

        var niches = new List<Niche>();
        for (var c = 0; c < cr.Centroids.Count; c++)
        {
            var memberIdx = Enumerable.Range(0, likedVec.Count).Where(i => cr.Assignment[i] == c).ToList();
            if (memberIdx.Count == 0) continue;
            var memberIds = memberIdx.Select(i => likedVec[i].Entity.EntityId).ToList();
            var featC = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[likedVec[i].Entity.EntityId].Feature, Math.Max(0.01, likedVec[i].Score))));
            var semC = Vectors.WeightedCentroid(memberIdx.Select(i => (vectors[likedVec[i].Entity.EntityId].Semantic, Math.Max(0.01, likedVec[i].Score))));

            // Weight every signed video by visual proximity to this niche (sharpened), and tally per-attribute
            // local evidence. Nearby likes AND dislikes both shape the niche's own attribute affinities.
            var weights = new Dictionary<int, double>(signedForNiche.Count);
            var effTag = new Dictionary<int, double>(); var effPerf = new Dictionary<int, double>(); var effStudio = new Dictionary<int, double>();
            foreach (var e in signedForNiche)
            {
                var vid = e.Entity.EntityId;
                double wgt = 0;
                if (featC is not null && signedVecs.TryGetValue(vid, out var sv) && sv.Feature is { } fv)
                {
                    var sim = Vectors.Cosine(fv, featC);
                    wgt = sim > 0 ? Math.Pow(sim, LocalitySharpness) : 0;
                }
                weights[vid] = wgt;
                if (wgt <= 0 || !signedMeta.TryGetValue(vid, out var m)) continue;
                foreach (var t in m.TagIds.Distinct()) effTag[t] = effTag.GetValueOrDefault(t) + wgt;
                foreach (var p in m.PerformerIds.Distinct()) effPerf[p] = effPerf.GetValueOrDefault(p) + wgt;
                if (m.StudioId is { } sid) effStudio[sid] = effStudio.GetValueOrDefault(sid) + wgt;
            }
            var local = RecHelpers.AttributeAttribution(signedForNiche.Select(e => (e.Entity.EntityId, e.Score)), signedMeta,
                tagCounts, studioCounts, perfCounts, total, nicheOpts, aspectTargets, null, weights, attentionCells, faceFit, unlinkedLoad);

            var nTags = new Dictionary<int, double>(); var nPerfs = new Dictionary<int, double>(); var nStudios = new Dictionary<int, double>();
            foreach (var t in globalTags.Keys.Union(local.Tags.Keys))
                nTags[t] = Blend(globalTags.GetValueOrDefault(t), local.Tags.TryGetValue(t, out var dt) ? dt.Affinity : globalTags.GetValueOrDefault(t), effTag.GetValueOrDefault(t));
            foreach (var p in globalPerfs.Keys.Union(local.Performers.Keys))
                nPerfs[p] = Blend(globalPerfs.GetValueOrDefault(p), local.Performers.TryGetValue(p, out var dp) ? dp.Affinity : globalPerfs.GetValueOrDefault(p), effPerf.GetValueOrDefault(p));
            foreach (var s in globalStudios.Keys.Union(local.Studios.Keys))
                nStudios[s] = Blend(globalStudios.GetValueOrDefault(s), local.Studios.TryGetValue(s, out var ds) ? ds.Affinity : globalStudios.GetValueOrDefault(s), effStudio.GetValueOrDefault(s));

            // For the cluster UI: the tags that DISTINGUISH this niche — biggest positive divergence from your
            // global baseline — and the chip weight IS that divergence, so generic tags you like everywhere
            // (which sit near your baseline in every niche) don't dominate and the niches read as distinct.
            var topTags = nTags.Select(kv => (id: kv.Key, delta: kv.Value - globalTags.GetValueOrDefault(kv.Key)))
                .Where(x => x.delta > 0.03)
                .OrderByDescending(x => x.delta)
                .Take(8).Select(x => (x.id, x.delta)).ToList();
            niches.Add(new Niche(niches.Count, $"Niche {niches.Count + 1}", memberIds, featC, semC, nTags, nPerfs, nStudios, topTags));
        }

        // ── Visual taste direction (§1 over §5): learn the axis from your disliked LOOK to your liked look,
        // each rep built "as watched" (section embeddings weighted by watch time). Scored per candidate.
        var dislikedVec = engaged.Where(e => e.Score < -0.1).OrderBy(e => e.Score).Take(120).ToList();
        var tasteIds = likedVec.Select(e => e.Entity.EntityId).Concat(dislikedVec.Select(e => e.Entity.EntityId)).Distinct().ToList();
        var dislikedAsset = await RecHelpers.GetVisualVectorsAsync(repo, dislikedVec.Select(e => e.Entity.EntityId).ToList(), ct);
        var sectionVecs = await RecHelpers.GetSectionFeatureVectorsAsync(db, tasteIds, ct);
        var tasteWatched = await RecHelpers.FetchWatchedIntervalsAsync(db, userId, tasteIds, ct);
        float[]? AssetFeat(int id) => vectors.TryGetValue(id, out var v) ? v.Feature : dislikedAsset.TryGetValue(id, out var dv) ? dv.Feature : null;
        // Weight each sample by the explicit CONTENT aspect rating (amplified) when present, else overall
        // engagement — so a video disliked only for its performers/audio doesn't drag the content look-axis down.
        var contentRatings = await RecHelpers.FetchVideoAspectRatingsAsync(db, userId, seen.ToList(), "content", 50, ct);
        double ContentWeight(int id, double overall) => contentRatings.TryGetValue(id, out var cwr) ? cwr * ContentRatingWeight : overall;
        var tasteSamples = likedVec.Select(e => (e.Entity.EntityId, e.Score))
            .Concat(dislikedVec.Select(e => (e.Entity.EntityId, e.Score)))
            .Select(x => (RecHelpers.AttentionWeightedVisual(sectionVecs.GetValueOrDefault(x.Item1), tasteWatched.GetValueOrDefault(x.Item1), AssetFeat(x.Item1)), ContentWeight(x.Item1, x.Item2)))
            .ToList();
        var tasteVector = RecHelpers.LearnTasteDirection(tasteSamples);

        // ── Audio taste direction (ECAPA speaker/voice embeddings, asset-level): the axis from your disliked to
        // your liked SOUND — whose voices / what audio you prefer, orthogonal to the visuals. Each engaged video's
        // audio embedding is weighted by its EXPLICIT "audio" aspect rating when present (the strongest, most
        // direct audio signal — amplified so it dominates), else by the overall engagement score. Null when the
        // library has no audio vectors / too few samples.
        var overallById = engaged.ToDictionary(e => e.Entity.EntityId, e => e.Score);
        var audioRatings = await RecHelpers.FetchVideoAspectRatingsAsync(db, userId, seen.ToList(), "audio", AudioRatingNeutral, ct);
        var audioIds = tasteIds.Concat(audioRatings.Keys).Distinct().ToList();
        var audioEmb = await RecHelpers.GetAssetVectorsAsync(repo, audioIds, EmbeddingModality.Audio, null, ct);
        var audioSamples = audioIds
            .Where(id => audioEmb.ContainsKey(id))
            .Select(id => (vec: audioEmb[id], w: audioRatings.TryGetValue(id, out var ar) ? ar * AudioRatingWeight : overallById.GetValueOrDefault(id)))
            .Where(x => Math.Abs(x.w) > 1e-6)
            .ToList();
        // Audio as a CENTROID model (like faces), not a single taste direction: cluster the voices/audio of your
        // liked vs disliked videos. A candidate near a known audio cluster scores confidently; audio unlike anything
        // you've engaged with is LOW-confidence (contributes little) instead of being forced to a spurious extreme.
        var likedAudio = audioSamples.Where(x => x.w > 0).ToList();
        var dislikedAudio = audioSamples.Where(x => x.w < 0).Select(x => x.vec).ToList();
        var likedAudioCentroids = likedAudio.Count >= 2
            ? RecHelpers.RefinedClusters(likedAudio.Select(x => x.vec).ToList(), likedAudio.Select(x => Math.Abs(x.w)).ToList(), Math.Clamp(likedAudio.Count / 8, 2, 8)).Centroids
            : likedAudio.Select(x => x.vec).ToList();
        var dislikedAudioCentroids = dislikedAudio.Count >= 3
            ? Vectors.KMeans(dislikedAudio, dislikedAudio.Select(_ => 1.0).ToList(), Math.Clamp(dislikedAudio.Count / 8, 1, 5))
            : dislikedAudio;

        // ── DISLIKED-content niche centroids (mirror of the disliked-FACE model, but on the visual "look" of your
        // disliked videos). With the LIKED niche centroids this makes the niche signal BIDIRECTIONAL: near a liked
        // cluster → +, near a disliked cluster → −, near neither → 0 (content that fits no niche is fine).
        var dislikedFeatures = dislikedVec.Select(e => dislikedAsset.GetValueOrDefault(e.Entity.EntityId)?.Feature).Where(f => f is not null).Select(f => f!).ToList();
        var dislikedNicheCentroids = dislikedFeatures.Count >= 3
            ? Vectors.KMeans(dislikedFeatures, dislikedFeatures.Select(_ => 1.0).ToList(), Math.Clamp(dislikedFeatures.Count / 8, 1, 6))
            : dislikedFeatures;

        // ── QUALITY (cinematography) taste direction: the axis from your low-rated to high-rated craft, learned
        // from explicit "video_quality" aspect ratings applied to each video's visual embedding. This is the real
        // Quality signal (v1: a learned visual quality axis; camera-dynamism via section-embedding variance is a
        // planned refinement). Null when you haven't rated video_quality on enough videos.
        var qualityRatings = await RecHelpers.FetchVideoAspectRatingsAsync(db, userId, seen.ToList(), "video_quality", 50, ct);
        var qualityFeat = await RecHelpers.GetVisualVectorsAsync(repo, qualityRatings.Keys.ToList(), ct);
        var qualitySamples = qualityRatings.Keys
            .Where(id => qualityFeat.TryGetValue(id, out var v) && v.Feature is not null)
            .Select(id => ((float[]?)qualityFeat[id].Feature, qualityRatings[id]))
            .ToList();
        var qualityVector = qualitySamples.Count >= 4 ? RecHelpers.LearnTasteDirection(qualitySamples) : null;
        // De-confound quality from CONTENT: the visual-feature quality axis inevitably absorbs your content-look
        // preference (you tend to rate quality high on videos you like). Project the content "look axis" (visual
        // taste vector) OUT of the quality direction, leaving the quality-specific variance orthogonal to it — so
        // Quality stops just re-encoding Content. (A camera-dynamism proxy via section-embedding variance is the
        // deeper planned fix; this removes the biggest, cheapest source of leakage.)
        qualityVector = Orthogonalize(qualityVector, tasteVector);

        // ── PERFORMER-ATTRIBUTE cold-start model: from the performers you've REVEALED a preference on (the global
        // attribution), learn which ATTRIBUTES you gravitate to (gender/ethnicity/hair/height/body/performer-tags),
        // then project that onto EVERY content performer so an UNRATED performer still gets a read. It's a distinct
        // calibrated sub-signal within Performers (its own confidence, silent where attribute coverage is thin) —
        // NOT a fallback that overrides real per-performer affinity.
        var trainPerfAttrs = await RecHelpers.FetchPerformerAttributesAsync(db, global.Performers.Keys.ToList(), ct);
        var attrCells = LearnAttrCells(global.Performers, trainPerfAttrs);
        var perfAttrPrior = await ComputePerfAttrPriorsAsync(db, attrCells, ct);

        // Rated-video engagement scores — supervision for the learned calibration curves (see FitLearnedCurves).
        var engagedScores = new Dictionary<int, double>();
        foreach (var e in signed) engagedScores[e.Entity.EntityId] = e.Score;

        // ── IMAGE half: images share the tag/performer/studio/face/attribute taste learned above, but live in their
        // OWN visual embedding space — so learn an image LOOK axis + liked/disliked image content clusters from your
        // engaged images (no audio/temporal). The rest of the image feed reuses the shared affinities + face model.
        var imgEngaged = await core.Preference.GetTopLikedAsync(userId, "image", 1200, null, ct);
        var imgSigned = imgEngaged.Where(e => Math.Abs(e.Score) > 0.1).ToList();
        var imgVecs = await RecHelpers.GetImageVisualVectorsAsync(repo, imgSigned.Select(e => e.Entity.EntityId).ToList(), ct);
        var imgFeat = imgSigned
            .Where(e => imgVecs.TryGetValue(e.Entity.EntityId, out var v) && v.Feature is not null)
            .Select(e => (feat: imgVecs[e.Entity.EntityId].Feature!, score: e.Score)).ToList();
        var imgTaste = imgFeat.Count >= 6 ? RecHelpers.LearnTasteDirection(imgFeat.Select(x => ((float[]?)x.feat, x.score)).ToList()) : null;
        var likedImgFeat = imgFeat.Where(x => x.score > 0.1).ToList();
        var dislikedImgFeat = imgFeat.Where(x => x.score < -0.1).Select(x => x.feat).ToList();
        var imgLikedCentroids = likedImgFeat.Count >= 2
            ? Vectors.KMeans(likedImgFeat.Select(x => x.feat).ToList(), likedImgFeat.Select(x => Math.Max(0.01, x.score)).ToList(), Math.Clamp(likedImgFeat.Count / 8, 2, 10))
            : new List<float[]>();
        var imgDislikedCentroids = dislikedImgFeat.Count >= 3
            ? Vectors.KMeans(dislikedImgFeat, dislikedImgFeat.Select(_ => 1.0).ToList(), Math.Clamp(dislikedImgFeat.Count / 8, 1, 5))
            : new List<float[]>();

        var built = new Built(niches, global.Tags, global.Performers, global.Studios, seen, tasteVector, likedFaceCentroids, dislikedFaceCentroids,
            likedAudioCentroids, dislikedAudioCentroids, dislikedNicheCentroids, qualityVector,
            AttrCells: attrCells, PerfAttrPrior: perfAttrPrior, PseudoPerfIds: pseudoFaceIds, EngagedScores: engagedScores,
            ImageTasteVector: imgTaste, ImageLikedCentroids: imgLikedCentroids, ImageDislikedCentroids: imgDislikedCentroids);

        // Stage-0 calibration is built LAZILY (EnsureCalibratedAsync) and FOLDED INTO the first library-wide
        // scoring pass the feed already runs — the cold model build no longer does its own extra sample scan
        // (that duplicated the universe pass's fetch+score work and dominated cold-load time). A filtered/seeded/
        // scoped first request, which is NOT a valid library-wide reference, still falls back to a strided sample
        // there. Whichever path builds it, the calibration is published back onto the cached Built for all callers.
        return built;
    }

    /// <summary>Remove the component of <paramref name="v"/> along the unit direction <paramref name="basis"/>
    /// (Gram-Schmidt), then renormalize. Used to strip content-preference leakage out of the quality axis.</summary>
    private static float[]? Orthogonalize(float[]? v, float[]? basis)
    {
        if (v is null || basis is null || v.Length != basis.Length) return v;
        double dot = 0; for (var i = 0; i < v.Length; i++) dot += v[i] * basis[i];
        var outv = new float[v.Length]; double norm = 0;
        for (var i = 0; i < v.Length; i++) { outv[i] = (float)(v[i] - dot * basis[i]); norm += outv[i] * outv[i]; }
        norm = Math.Sqrt(norm);
        if (norm < 1e-6) return v; // fully collinear — keep original rather than return noise
        for (var i = 0; i < outv.Length; i++) outv[i] /= (float)norm;
        return outv;
    }

    /// <summary>The per-candidate dimension array's layout, and the keys sorting and score filters resolve
    /// against. Index order IS the array order, and must match the ScoreFields the descriptor advertises.</summary>
    private static readonly string[] DimensionKeys = ["overall", "performers", "content", "quality", "audio", "confidence"];

    private static RecommendationResult Empty(string note) => new([], Diagnostics: new Dictionary<string, object> { ["note"] = note });
    private static string Band(double s) => s >= 0.6 ? "Strong" : s >= 0.35 ? "Good" : s >= 0.15 ? "Possible" : "Weak";

    // ── Performer-attribute cold-start model ──────────────────────────────────
    private const double AttrCellShrink = 2.0;       // pseudo-count pulling thin attribute cells toward neutral
    private const double AttrPriorK = 1.5;           // maps a performer's accumulated cell-confidence to a prior conf in [0,1)
    private const int AttrCellMinPerformers = 2;     // an attribute VALUE needs this many revealed performers to be trusted

    // Pseudo-performers: an unlinked face gets its own performer-like column once it recurs across this many of
    // your engaged videos; its faceId is offset into a reserved range so it never collides with a real id.
    private const int MinPseudoAppearances = 3;
    private const int PseudoPerfOffset = 1_500_000_000;

    private static string? NormAttr(string? s) { s = s?.Trim().ToLowerInvariant(); return string.IsNullOrEmpty(s) ? null : s; }

    /// <summary>The categorical attribute "cells" a performer belongs to (free text normalized, numerics bucketed).</summary>
    private static IEnumerable<string> AttrCellsOf(RecHelpers.PerfAttrs a)
    {
        if (NormAttr(a.Gender) is { } g) yield return $"gender:{g}";
        if (NormAttr(a.Ethnicity) is { } e) yield return $"eth:{e}";
        if (NormAttr(a.Country) is { } c) yield return $"country:{c}";
        if (NormAttr(a.HairColor) is { } h) yield return $"hair:{h}";
        if (NormAttr(a.EyeColor) is { } y) yield return $"eye:{y}";
        if (a.HeightCm is { } hc && hc is > 120 and < 230) yield return $"height:{hc / 5 * 5}";     // 5cm buckets
        if (NormAttr(a.FakeTits) is { } f) yield return $"fake:{f}";
        if (NormAttr(a.Circumcised) is { } ci) yield return $"circ:{ci}";
        if (a.PenisLength is { } pl && pl is > 5 and < 40) yield return $"penis:{(int)(pl / 2) * 2}";  // 2cm buckets
        foreach (var t in a.TagIds) yield return $"ptag:{t}";
    }

    /// <summary>Learn a signed affinity per attribute cell = the confidence-weighted, pseudo-count-shrunk mean of the
    /// affinities of the revealed performers carrying that cell. conf = evidence mass / (evidence + shrink).</summary>
    private static Dictionary<string, (double aff, double conf)> LearnAttrCells(
        IReadOnlyDictionary<int, RecHelpers.AffinityDetail> perfAff, IReadOnlyDictionary<int, RecHelpers.PerfAttrs> attrs)
    {
        var acc = new Dictionary<string, (double num, double w, int n)>();
        foreach (var (pid, det) in perfAff)
        {
            if (!attrs.TryGetValue(pid, out var a)) continue;
            var ev = det.Confidence;
            if (ev <= 1e-6) continue;
            foreach (var cell in AttrCellsOf(a))
            {
                var s = acc.GetValueOrDefault(cell);
                acc[cell] = (s.num + det.Affinity * ev, s.w + ev, s.n + 1);
            }
        }
        var cells = new Dictionary<string, (double aff, double conf)>();
        foreach (var (cell, s) in acc)
            if (s.n >= AttrCellMinPerformers)
                cells[cell] = (s.num / (s.w + AttrCellShrink), s.w / (s.w + AttrCellShrink));
        return cells;
    }

    /// <summary>Project the learned cells onto one performer: prior = confidence-weighted mean of its cells'
    /// affinities; conf grows with matched cell-confidence. Needs ≥2 known cells or it stays silent (conf 0).</summary>
    private static (double prior, double conf) AttrPrior(RecHelpers.PerfAttrs a, IReadOnlyDictionary<string, (double aff, double conf)> cells)
    {
        double num = 0, den = 0; var known = 0;
        foreach (var cell in AttrCellsOf(a))
            if (cells.TryGetValue(cell, out var cc)) { num += cc.aff * cc.conf; den += cc.conf; known++; }
        return known < 2 || den <= 1e-9 ? (0, 0) : (num / den, Math.Min(1.0, den / (den + AttrPriorK)));
    }

    /// <summary>Per-performer attribute prior for every content performer (fetched once). Empty when nothing learned.</summary>
    private static async Task<Dictionary<int, (double prior, double conf)>> ComputePerfAttrPriorsAsync(
        DbContext db, Dictionary<string, (double aff, double conf)> cells, CancellationToken ct)
    {
        var res = new Dictionary<int, (double prior, double conf)>();
        if (cells.Count == 0) return res;
        var ids = await RecHelpers.AllContentPerformerIdsAsync(db, ct);
        var attrs = await RecHelpers.FetchPerformerAttributesAsync(db, ids, ct);
        foreach (var (pid, a) in attrs)
        {
            var (prior, conf) = AttrPrior(a, cells);
            if (conf > 0) res[pid] = (prior, conf);
        }
        return res;
    }

    // ── Fusion v5 (see recommendations/docs/FUSION_DESIGN.md) ─────────────────
    // Each signal is (rawValue, confidence). Stage 0: CALIBRATE each raw value to d ∈ [-1,1] against a FIXED
    // library-wide reference (percentile), so "typical" reads 0, presence is mean-neutral, and signals are
    // commensurable. Stage 1 (WITHIN an aspect, correlated estimators of one latent): value = c²-weighted mean of
    // the present d's (missing sub-signal drops out → the rest carry fully; junk c≈0.1 barely counts); coverage =
    // Σk·c/Σk; disagreement δ (junk-immune pairwise) lowers CONFIDENCE only, never the score (monotonicity).
    // Stage 2 (ACROSS the four independent aspects): a DEAD BAND makes near-typical aspects silent (== missing);
    // a saturating gate means adequate evidence = full strength (confidence-yes-score-no); then accumulate
    // K = Σ atanh(u), u = (lever/2)·gate·deadbanded — the owner's validated additive combiner, made presence-
    // neutral + bounded. Score = σ(τ·K) (display only; ranking is K). Confidence is a separate lever-weighted mean.
    private const double DeadBand = 0.25;       // aspects within this of typical are silent (owner: neutral == unknown)
    private const double GateMid = 0.5;         // coverage at which an aspect reaches ~full influence
    private const double DisplayGain = 1.6;     // τ in σ(τ·K); cosmetic (ranking uses K directly)
    private const int CalibSampleCap = 4000;    // library sample size for the reference distributions
    internal readonly record struct AspectBelief(double Value, double Coverage, double Conf);
    private readonly record struct SubWeights(double Face, double PerfAff, double Tag, double Taste, double Niche, double Studio, double PerfAttr, double QualityCraft, double ObjQual, double AudioLift);
    private static readonly SubWeights DefaultSubWeights = new(0.45, 0.55, 0.45, 0.30, 0.35, 0.15, 0.15, 1.00, 1.00, 1.60);

    /// <summary>Fixed-reference percentile calibration for one signal: maps a raw value to d ∈ [-1,1] by its
    /// position in the library-wide distribution of that signal (over videos that HAVE it). Pseudo-count smoothed;
    /// |d| capped so no video hits ±1 by rank alone. Empty (no reference) → 0 (neutral).</summary>
    private sealed class Calib
    {
        private readonly double[] _sorted;   // ascending reference values
        private double[]? _curve;            // learned percentile→value transfer, uniform grid over [0,1] (null ⇒ rank-linear)
        private double _center;              // subtracted so the DEAD-BANDED curve is mean-neutral over the reference
        public Calib(IEnumerable<double> values) => _sorted = values.OrderBy(v => v).ToArray();
        public int Count => _sorted.Length;
        public bool HasCurve => _curve is not null;

        /// <summary>Empirical percentile of <paramref name="raw"/> in the reference (pseudo-count smoothed), in [0,1].</summary>
        public double Percentile(double raw)
        {
            var n = _sorted.Length;
            int lo = 0, hi = n;
            while (lo < hi) { var mid = (lo + hi) / 2; if (_sorted[mid] < raw) lo = mid + 1; else hi = mid; }
            const double m = 4.0;                                  // pseudo-counts (stabilizes tails/thin refs)
            return (lo + 0.5 * m) / (n + m);
        }

        /// <summary>Attach a learned monotone transfer curve (uniform grid over [0,1]) + its dead-band center.</summary>
        public void SetCurve(double[] grid, double center) { _curve = grid; _center = center; }

        public double D(double raw)
        {
            var n = _sorted.Length;
            if (n < 8) return 0;                                   // too little reference to calibrate → neutral
            var q = Percentile(raw);
            // Learned shape follows your real enjoyment (e.g. accelerating at the top); else the rank-linear map.
            if (_curve is not null) return Math.Clamp(InterpGrid(_curve, q) - _center, -0.999, 0.999);
            var cap = 1.0 - 1.0 / n;
            return Math.Clamp(2 * q - 1, -cap, cap);
        }
    }

    /// <summary>Linear interpolation over a uniform grid at position <paramref name="q"/>∈[0,1].</summary>
    internal static double InterpGrid(double[] grid, double q)
    {
        var g = grid.Length;
        if (g == 0) return 0;
        if (g == 1) return grid[0];
        var t = Math.Clamp(q, 0, 1) * (g - 1);
        var i = (int)t;
        if (i >= g - 1) return grid[g - 1];
        var f = t - i;
        return grid[i] * (1 - f) + grid[i + 1] * f;
    }

    // The 8 sub-signals: key, aspect index (0=perf,1=content,2=quality,3=audio), raw value + confidence readers.
    // (raw is fed to the calibrator; confidence gates its influence and defines its reference population.)
    private static readonly (string key, int aspect, Func<Cand, double> raw, Func<Cand, double> conf)[] SigDefs =
    [
        ("perfAff", 0, r => r.Perf,     r => r.PerfConf),
        ("face",    0, r => r.Face,     r => r.FaceConf),
        ("perfAttr",0, r => r.PerfAttr, r => r.PerfAttrConf),
        ("tag",     1, r => r.TagRaw,   r => r.TagKnownFrac),
        ("cluster", 1, r => r.NicheFit, r => r.ClusterConf),
        ("look",    1, r => r.Taste,    r => r.LookConf),
        ("studio",  1, r => r.Studio,   r => r.StudioConf),
        ("quality", 2, r => r.Quality,  r => r.QualityConf),
        ("objqual", 2, r => r.ObjQual,  r => r.ObjQualConf),
        ("audio",   3, r => r.AudioFit, r => r.AudioConf),
    ];

    /// <summary>Objective fidelity from the denormalized Video row: log-compressed resolution (height) and bitrate,
    /// each mapped to ~[0,1] and averaged. Monotone in "how high-fidelity the file is" — the calibrator then
    /// percentile-ranks it and the learned curve decides whether higher fidelity actually tracks YOUR enjoyment
    /// (so this never assumes bigger=better). Confidence 0.6 when both are known, 0.4 with one, 0 with neither
    /// (a video with no dimension data contributes nothing rather than a spurious low). Duration is deliberately
    /// excluded — length is not a quality axis.</summary>
    internal static (double raw, double conf) ObjectiveQuality(int height, long bitRate)
    {
        // Both scales SATURATE at their reference point by design — past ~3000p and ~30 Mbit/s, more of either is
        // not more fidelity to a viewer — so they clamp there rather than running on. Without the clamp an 8K /
        // 120 Mbit file reads >1, which is outside the [0,1] every other raw signal is calibrated on.
        double res = height > 0 ? Math.Min(1.0, Math.Log(1.0 + height) / Math.Log(3001.0)) : double.NaN;          // ~0 (SD) → 1 (3000p+)
        double br = bitRate > 0 ? Math.Min(1.0, Math.Log(1.0 + bitRate) / Math.Log(30_000_001.0)) : double.NaN;   // ~0 → 1 (30 Mbit/s+)
        bool hr = !double.IsNaN(res), hb = !double.IsNaN(br);
        if (hr && hb) return (0.5 * res + 0.5 * br, 0.6);
        if (hr) return (res, 0.4);
        if (hb) return (br, 0.4);
        return (0, 0);
    }

    /// <summary>Build a percentile-calibration table per signal from a library sample — reference population is
    /// the videos where the signal is present (confidence &gt; 0).</summary>
    private static Dictionary<string, Calib> BuildCalibrations(IReadOnlyList<Cand> sample)
    {
        var d = new Dictionary<string, Calib>();
        foreach (var (key, _, raw, conf) in SigDefs)
            d[key] = new Calib(sample.Where(r => conf(r) > 1e-9).Select(raw));
        return d;
    }

    // Learned per-signal calibration: activates a signal's transfer curve once it has this many rated reference
    // samples (else the plain percentile map stays); grid resolution of the stored curve.
    private const int LearnedCalibMinPairs = 40;
    private const int LearnedCalibGrid = 33;

    /// <summary>Fit each signal's learned transfer curve from the RATED videos in the reference set — the signal's
    /// PERCENTILE mapped to your overall engagement score — so calibration follows your ACTUAL enjoyment shape
    /// (e.g. top performers disproportionately) instead of the fixed rank-linear map. Isotonic (monotone), dead-band
    /// centered (presence stays mean-neutral), and only where enough rated samples exist. Mutates Calib in place.</summary>
    private static void FitLearnedCurves(Dictionary<string, Calib> calibs, IReadOnlyList<Cand> reference, IReadOnlyDictionary<int, double>? engagedScores)
    {
        if (engagedScores is not { Count: > 0 }) return;
        foreach (var (key, _, raw, conf) in SigDefs)
        {
            if (!calibs.TryGetValue(key, out var calib) || calib.Count < 8) continue;
            // (signal percentile, this video's engagement score) over the rated videos that HAVE the signal.
            var pairs = new List<(double x, double score)>();
            foreach (var r in reference)
                if (conf(r) > 1e-9 && engagedScores.TryGetValue(r.Id, out var sc))
                    pairs.Add((calib.Percentile(raw(r)), sc));
            if (pairs.Count < LearnedCalibMinPairs) continue;
            // RANK-transform the target scores to a full [-1,1] spread first: a curated library's raw scores are
            // compressed (you like most things), which would flatten the curve and under-power the signal. Ranking
            // restores discrimination and keeps the learned curve on the SAME scale as the percentile fallback, so
            // the top of a signal can genuinely expand (top performers pushed toward +1 when your data supports it).
            var n = pairs.Count;
            var order = Enumerable.Range(0, n).OrderBy(i => pairs[i].score).ToArray();
            var xy = new List<(double x, double y)>(n);
            for (var r = 0; r < n; r++)
                xy.Add((pairs[order[r]].x, n > 1 ? 2.0 * r / (n - 1) - 1 : 0));
            var grid = FitIsotonicGrid(xy, LearnedCalibGrid);
            calib.SetCurve(grid, CenterForDeadband(grid));
        }
    }

    /// <summary>Isotonic regression (PAVA) of (x∈[0,1], y) pairs → a monotone non-decreasing fit resampled onto a
    /// uniform <paramref name="gridN"/>-point grid over [0,1].</summary>
    internal static double[] FitIsotonicGrid(List<(double x, double y)> pairs, int gridN)
    {
        var pts = pairs.OrderBy(p => p.x).ToList();
        var nn = pts.Count;
        var val = new List<double>(); var wt = new List<double>(); var cnt = new List<int>();
        foreach (var (_, y) in pts)                                // pool adjacent violators
        {
            double cv = y, cw = 1; var cc = 1;
            while (val.Count > 0 && val[^1] >= cv)
            {
                double pv = val[^1], pw = wt[^1]; var pc = cnt[^1];
                val.RemoveAt(val.Count - 1); wt.RemoveAt(wt.Count - 1); cnt.RemoveAt(cnt.Count - 1);
                cv = (pv * pw + cv * cw) / (pw + cw); cw += pw; cc += pc;
            }
            val.Add(cv); wt.Add(cw); cnt.Add(cc);
        }
        var fx = new double[nn]; var fy = new double[nn]; var idx = 0;
        for (var b = 0; b < val.Count; b++)                        // expand block means back to sorted points
            for (var k = 0; k < cnt[b]; k++) { fx[idx] = pts[idx].x; fy[idx] = val[b]; idx++; }
        var grid = new double[gridN];
        for (var gi = 0; gi < gridN; gi++) grid[gi] = InterpAt(fx, fy, (double)gi / (gridN - 1));
        return grid;
    }

    private static double InterpAt(double[] xs, double[] ys, double q)
    {
        var n = xs.Length;
        if (n == 0) return 0;
        if (q <= xs[0]) return ys[0];
        if (q >= xs[n - 1]) return ys[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { var mid = (lo + hi) / 2; if (xs[mid] <= q) lo = mid; else hi = mid; }
        var span = xs[hi] - xs[lo];
        var f = span > 1e-12 ? (q - xs[lo]) / span : 0;
        return ys[lo] * (1 - f) + ys[hi] * f;
    }

    /// <summary>Center c such that the DEAD-BANDED curve averages ~0 over the (uniform-percentile) reference — so
    /// presence stays mean-neutral (T5) even for a skewed convex curve. Mean is monotone in c ⇒ bisection.</summary>
    internal static double CenterForDeadband(double[] grid)
    {
        double MeanDb(double c) { double s = 0; foreach (var v in grid) s += DeadBanded(v - c); return s / grid.Length; }
        double lo = -1.5, hi = 1.5;
        for (var it = 0; it < 40; it++) { var mid = (lo + hi) / 2; if (MeanDb(mid) > 0) lo = mid; else hi = mid; }
        return (lo + hi) / 2;
    }

    /// <summary>Stage 1 — fuse the present sub-signals of one aspect. value = c²-weighted mean of the calibrated
    /// d's; coverage = configured-evidence fraction present; conf = coverage·(1 − ½·pairwise-disagreement).</summary>
    internal static AspectBelief FuseAspect(ReadOnlySpan<(double k, double c, double d)> sigs)
    {
        double cnum = 0, cden = 0, wsum = 0, wd = 0;
        Span<(double w, double d)> present = stackalloc (double, double)[sigs.Length];
        var np = 0;
        foreach (var (k, c, d) in sigs)
        {
            if (k <= 1e-9) continue;                    // lever 0 → signal removed entirely
            cden += k; cnum += k * c;
            var w = k * c * c;                          // c² — junk (c≈0.1) contributes ~1%
            if (w > 1e-12) { present[np++] = (w, d); wsum += w; wd += w * d; }
        }
        var coverage = cden > 1e-12 ? Math.Clamp(cnum / cden, 0, 1) : 0;
        if (np == 0 || wsum <= 1e-12) return new AspectBelief(0, coverage, 0);
        var value = wd / wsum;
        double pair = 0;
        for (var i = 0; i < np; i++)
            for (var j = i + 1; j < np; j++)
                pair += present[i].w * present[j].w * Math.Abs(present[i].d - present[j].d);
        var disagree = 2 * pair / (wsum * wsum);
        return new AspectBelief(value, coverage, Math.Clamp(coverage * (1 - 0.5 * Math.Min(1, disagree)), 0, 1));
    }

    /// <summary>The four aspect beliefs for one candidate: calibrate each sub-signal, then FuseAspect per aspect.</summary>
    private static (AspectBelief performers, AspectBelief content, AspectBelief quality, AspectBelief audio) CandAspects(Cand r, Built built, SubWeights sub)
    {
        double D(string key, double raw) => built.Cal(key, raw, r.IsImage);
        // An EXPLICIT performer rating is already on your preference scale — percentile-calibrating it would drown a
        // deliberate, rare signal in the sea of engagement-derived affinities. So honor the direct value as-is.
        var dPerf = r.PerfDirect ? Math.Clamp(r.Perf, -0.999, 0.999) : D("perfAff", r.Perf);
        var performers = FuseAspect([
            (sub.PerfAff, r.PerfConf, dPerf),
            (sub.Face, r.FaceConf, D("face", r.Face)),
            (sub.PerfAttr, r.PerfAttrConf, D("perfAttr", r.PerfAttr))]);
        var content = FuseAspect([
            (sub.Tag, r.TagKnownFrac, D("tag", r.TagRaw)),
            (sub.Niche, r.ClusterConf, D("cluster", r.NicheFit)),
            (sub.Taste, r.LookConf, D("look", r.Taste)),
            (sub.Studio, r.StudioConf, D("studio", r.Studio))]);
        var quality = FuseAspect([
            (sub.QualityCraft, r.QualityConf, D("quality", r.Quality)),
            (sub.ObjQual, r.ObjQualConf, D("objqual", r.ObjQual))]);
        // Audio is a GENUINE but secondary signal (it predicts your audio ratings ~as well as quality does), yet the
        // shared dead-band suppresses ~80% of its reads so it rarely fires. Give the audio belief a modest gain so it
        // contributes more decisively in BOTH directions (surfacing liked audio, and letting disliked audio drag a
        // video down) — a lift, not a veto. The learned calibration keeps the direction honest.
        var audioRaw = FuseAspect([(1.0, r.AudioConf, D("audio", r.AudioFit))]);
        var audio = audioRaw with { Value = Math.Clamp(audioRaw.Value * sub.AudioLift, -0.999, 0.999) };
        return (performers, content, quality, audio);
    }

    // Stage 2 helpers.
    internal static double DeadBanded(double v) => Math.Sign(v) * Math.Max(0, Math.Abs(v) - DeadBand) / (1 - DeadBand);
    internal static double Smoothstep(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>Stage 2 — accumulate the four aspect beliefs into a ranking key K and an overall confidence.</summary>
    internal static (double key, double conf) FuseOverall((double lever, AspectBelief a)[] aspects)
    {
        double k = 0, cnum = 0, cden = 0;
        foreach (var (lever, a) in aspects)
        {
            if (lever <= 1e-9) continue;
            cden += lever; cnum += lever * a.Conf;
            var g = Smoothstep(a.Coverage / GateMid);
            var u = Math.Clamp((lever / 2.0) * g * DeadBanded(a.Value), -0.98, 0.98);
            k += Math.Atanh(u);
        }
        return (k, cden > 1e-12 ? cnum / cden : 0);
    }

    // ── Caching + durable persistence ─────────────────────────────────────────
    // Three layers, because the two expensive things are independent.
    // (1) The per-user MODEL (Built): clustering + attribution + centroids. Held in memory AND persisted to the KV
    //     store, so it survives restarts. Reads serve the cached/persisted model even if somewhat stale (fast cold
    //     load); freshness comes from event-driven REFRESH — a rating, debounced to ≤ once/hour, rebuilds and
    //     re-persists in the BACKGROUND. A model past the serve ceiling is rebuilt synchronously on read.
    // (2) The WARM SET: the whole library scored against that model (embeddings + meta + faces + audio for every
    //     video), plus the calibration folded out of it. This is what the default feed reads, and it is far too
    //     expensive to pay on a page open — so it is precomputed at STARTUP and re-computed in the background
    //     whenever the model is rebuilt. It has no TTL: it is valid exactly as long as the model instance it was
    //     scored against is the current one (reference identity), which is what makes "warm" mean "correct".
    // (3) The per-request ROW cache: the same knob-independent scoring for a FILTERED/scoped universe, which can't
    //     be precomputed because the filter isn't known ahead of time. Short TTL + small LRU, so paging, sort
    //     direction and knob tuning within a session reuse one pass.
    private const long CacheTtlMs = 5 * 60 * 1000;                              // row-cache freshness
    private const int MaxRowCacheEntries = 8;
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromHours(1);   // ≤ one rebuild/user/hour from ratings
    private static readonly TimeSpan MaxServeAge = TimeSpan.FromHours(24);      // beyond this a read schedules a background rebuild
    /// <summary>The longest a REQUEST will wait for the per-user model lock. Generous for the only fast path that
    /// holds it (loading the persisted model), far short of a build — so a request that arrives mid-build gives up
    /// quickly and reports "building" instead of inheriting the wait this design removed.</summary>
    private static readonly TimeSpan ModelLoadWait = TimeSpan.FromSeconds(5);
    private sealed record RowCacheEntry(List<Cand> Rows, long AtMs);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RowCacheEntry> _rowCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (Built Built, DateTime BuiltUtc)> _builtCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();
    private static SemaphoreSlim UserLock(int userId) => _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    /// <summary>A library-wide scored pass, tied to the exact <see cref="Built"/> instance it was scored against.
    /// <paramref name="Model"/> doubles as the validity token: once the model is rebuilt the reference changes and
    /// the set is ignored (and re-warmed), so a warm read can never serve rows from a superseded model.
    /// <paramref name="Complete"/> is false when the pass hit <see cref="UniverseCap"/> and therefore covers only
    /// part of the library — such a set can still serve the default feed (which was always capped the same way)
    /// but must NOT be used to answer a filtered query, since matches beyond the cap would silently vanish.</summary>
    private sealed record WarmSet(List<Cand> Rows, Built Model, DateTime WarmedUtc, bool Complete);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, WarmSet> _warmVideo = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, WarmSet> _warmImage = new();
    /// <summary>Serializes warm passes per user, so startup, a model refresh and a cold read don't each kick off
    /// their own duplicate library scan. A background caller skips when it's held; a refresh WAITS for it, because
    /// its whole job is to leave the new model warm.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _warmGates = new();
    private static SemaphoreSlim WarmGate(int userId) => _warmGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
    /// <summary>Users with a warm pass actually running, for the status endpoint.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _warming = new();
    /// <summary>Users whose MODEL is being (re)built right now. Distinct from <see cref="_warming"/>, which is the
    /// cheaper library-scoring pass that follows it: with no model yet, a build is the whole wait the page is
    /// sitting through, and the status endpoint has to be able to say so.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _building = new();

    // ── Eviction ─────────────────────────────────────────────────────────────
    // Everything above is keyed by user and lives for the process. That's free on a single-user server, but a
    // shared one would accumulate a whole scored library per user who ever opened the page and never give it
    // back. So a user's in-memory state is dropped once they've been idle a while: the MODEL is persisted and
    // reloads in milliseconds, and the warm set rebuilds in the background on their next visit, so eviction costs
    // a returning user nothing that isn't already paid for on a cold start.
    private static readonly TimeSpan IdleEvictionAfter = TimeSpan.FromHours(6);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastSeen = new();

    /// <summary>Record activity for a user and opportunistically evict everyone who's gone quiet.</summary>
    private static void Touch(int userId)
    {
        var now = DateTime.UtcNow;
        _lastSeen[userId] = now;
        foreach (var (otherId, seen) in _lastSeen)
        {
            if (otherId == userId || now - seen < IdleEvictionAfter) continue;
            // Never evict mid-pass: the warm gate is held for the duration, and dropping the model underneath it
            // would have the pass publish a set keyed to a model nothing else references.
            if (_warming.ContainsKey(otherId)) continue;
            if (!_lastSeen.TryRemove(otherId, out _)) continue;
            _builtCache.TryRemove(otherId, out _);
            DropCachedRows(otherId);
            _userLocks.TryRemove(otherId, out _);
            _warmGates.TryRemove(otherId, out _);
        }
    }

    /// <summary>Warm-up state for one user, so the UI can say "preparing…" instead of hanging on a cold library
    /// pass. <paramref name="State"/> is "ready" (the default feed is served from precomputed rows), "warming"
    /// (a pass is running now) or "cold" (nothing precomputed — the next read pays for it).</summary>
    public WarmStatus GetWarmStatus(int userId)
    {
        var hasModel = _builtCache.TryGetValue(userId, out var m);
        var warm = _warmVideo.TryGetValue(userId, out var w) && hasModel && ReferenceEquals(w.Model, m.Built) ? w : null;
        // "building" only when there is nothing to serve. A rebuild behind a model the user can already see is
        // invisible to them by design — reporting it would put a progress banner over a working feed.
        var servable = hasModel && m.Built.Niches.Count > 0;
        var state = warm is not null ? "ready"
            : _warming.ContainsKey(userId) ? "warming"
            : !servable && _building.ContainsKey(userId) ? "building"
            : "cold";
        return new WarmStatus(state, servable ? m.BuiltUtc : null, warm?.WarmedUtc, warm?.Rows.Count ?? 0,
            state switch
            {
                "ready" => "Recommendations are precomputed and serve instantly.",
                "warming" => "Scoring your library — results will be fast once this finishes.",
                "building" => "Building your taste model from what you've rated and watched.",
                _ => "Nothing precomputed yet; the next open scores your library first.",
            });
    }

    /// <summary>Score the whole library against the user's current model and publish it as the warm set (plus the
    /// calibration folded out of that same pass). Idempotent — returns immediately when the current model is
    /// already warm or another pass is in flight — and best-effort: a failure just leaves reads to do it lazily.</summary>
    /// <param name="wait">True to queue behind an in-flight pass instead of skipping. A refresh must wait — its
    /// job is to leave the NEW model warm, and the pass it would skip is warming the OLD one.</param>
    private async Task WarmUserAsync(int userId, CancellationToken ct, bool wait = false)
    {
        var gate = WarmGate(userId);
        if (wait) { try { await gate.WaitAsync(ct); } catch (OperationCanceledException) { return; } }
        else if (!await gate.WaitAsync(0, CancellationToken.None)) return;   // a pass is already running
        _warming[userId] = 0;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var core = ResolveCore(sp);
            var repo = sp.GetRequiredService<IEmbeddingRepository>();
            var db = sp.GetRequiredService<DbContext>();
            // Already off the request path, so this is one of the callers that SHOULD build if there's no model.
            var built = await GetOrBuildBuiltAsync(userId, core, sp, ct, allowBlockingBuild: true);
            if (_warmVideo.TryGetValue(userId, out var existing) && ReferenceEquals(existing.Model, built))
                return;                                        // this exact model is already warm
            if (built.Niches.Count == 0) return;               // no taste model yet — nothing to score against

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ids = await RecHelpers.AllVideoIdsAsync(db, UniverseCap, ct);
            var complete = ids.Count < UniverseCap;            // hit the cap ⇒ this pass may not span the library
            if (!complete)
                _logger.LogWarning("User {UserId}'s library exceeds the {UniverseCap}-item scoring cap; the warm pass covers only part of it, and filtered queries will score their own candidates.", userId, UniverseCap);
            var vecs = await RecHelpers.GetVisualVectorsAsync(repo, ids, ct);
            var rows = await BuildScoredRowsAsync(vecs, built, null, null, null, repo, db, ct);
            if (rows.Count == 0)
            {
                _logger.LogInformation("Warm-up pass for user {UserId} scored nothing from {Candidates} candidate(s) — no visual embeddings yet?", userId, ids.Count);
                return;
            }
            // Calibrating from THESE rows is the whole point of doing it here: the library-wide pass is the correct
            // percentile reference, so the feed never pays for a separate sample scan.
            var calibrated = await EnsureCalibratedAsync(built, userId, rows, repo, db, ct);
            _warmVideo[userId] = new WarmSet(rows, calibrated, DateTime.UtcNow, complete);
            _logger.LogInformation("Warmed {Rows} video(s) for user {UserId} in {ElapsedMs}ms.", rows.Count, userId, sw.ElapsedMilliseconds);

            // Images are a much smaller universe and only worth warming once an image model exists.
            if (calibrated.ImageTasteVector is not null || calibrated.ImageLikedCentroids is { Count: > 0 })
            {
                var imgIds = await RecHelpers.AllImageIdsAsync(db, UniverseCap, ct);
                var imgComplete = imgIds.Count < UniverseCap;
                var imgVecs = await RecHelpers.GetImageVisualVectorsAsync(repo, imgIds, ct);
                var imgRows = await BuildImageRowsAsync(imgVecs, calibrated, repo, db, ct);
                if (imgRows.Count > 0)
                {
                    var imgCal = await EnsureImageCalibratedAsync(calibrated, userId, imgRows, repo, db, ct);
                    _warmImage[userId] = new WarmSet(imgRows, imgCal, DateTime.UtcNow, imgComplete);
                    // Image calibration republished the model, so re-point the video warm set at that same instance
                    // — otherwise the very next video read would see a reference mismatch and re-warm for nothing.
                    if (!ReferenceEquals(imgCal, calibrated)) _warmVideo[userId] = new WarmSet(rows, imgCal, DateTime.UtcNow, complete);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown, or the caller gave up — not a failure */ }
        catch (Exception ex)
        {
            // Best-effort: reads fall back to scoring lazily, which is slow but correct. Logged because "every page
            // open is slow" is otherwise a mystery with no trace of the pass that should have prevented it.
            _logger.LogError(ex, "Warm-up pass failed for user {UserId}; their reads will score the library lazily until it next runs.", userId);
        }
        // Release the semaphore we actually took, not a fresh one from the map: eviction can drop the entry, and
        // Release() on a re-created SemaphoreSlim(1, 1) throws SemaphoreFullException out of a fire-and-forget task.
        finally { _warming.TryRemove(userId, out _); gate.Release(); }
    }

    /// <summary>The precomputed library-wide rows for this user, or null when they'd be stale (model rebuilt), were
    /// never computed, or can't answer this particular query. A miss also schedules a background warm so the NEXT
    /// open is fast.</summary>
    /// <param name="needsFullLibrary">Set when the caller will take a SUBSET of these rows (a filter or search).
    /// A capped pass doesn't span the library, so answering a filter from it would silently drop every match past
    /// the cap — those queries fetch their own candidates instead.</param>
    private List<Cand>? TryTakeWarm(System.Collections.Concurrent.ConcurrentDictionary<int, WarmSet> slot, int userId, Built built, bool needsFullLibrary)
    {
        if (slot.TryGetValue(userId, out var w) && ReferenceEquals(w.Model, built))
            return needsFullLibrary && !w.Complete ? null : w.Rows;
        _ = WarmUserAsync(userId, CancellationToken.None);
        return null;
    }

    /// <summary>The user's taste model, for a REQUEST path — never blocking on a build.
    ///
    /// Building a model is clustering + attribution + centroids over the whole engaged history: tens of seconds on
    /// a small library, minutes on a large one. No page open may pay for that inline, so this never builds:
    ///
    /// <list type="bullet">
    /// <item>A cached or persisted model is served even PAST <see cref="MaxServeAge"/> — a day-old ranking is
    /// better than a hung page — and going stale only schedules a background rebuild on the way out.</item>
    /// <item>With no model at all (a first-ever visit) there is nothing to serve, so it starts the build in the
    /// background and returns <see cref="Built.Empty"/>. <see cref="GetWarmStatus"/> then reports "building" and
    /// the page shows progress and polls, instead of holding a request open for minutes.</item>
    /// </list>
    ///
    /// Background callers (startup warm, the debounced refresh) pass <paramref name="allowBlockingBuild"/> —
    /// they are the ones that SHOULD block, since building is their whole job.</summary>
    private async Task<Built> GetOrBuildBuiltAsync(int userId, ICoreServices core, IServiceProvider sp, CancellationToken ct, bool allowBlockingBuild = false)
    {
        Touch(userId);   // every read path funnels through here, so this is the one place activity must be noted
        var nowUtc = DateTime.UtcNow;
        if (_builtCache.TryGetValue(userId, out var e))
            return ServeAndRefreshIfStale(userId, e.Built, e.BuiltUtc, nowUtc);

        // Serialize per-user so concurrent first-requests don't stampede a rebuild.
        //
        // A build holds this same lock for its whole duration, so a request must NOT queue on it unboundedly —
        // that is the hang this method exists to avoid, just moved from the build to the lock. It waits only long
        // enough for the one fast thing that happens under the lock (read + deserialize the persisted model, and
        // re-derive the attribute prior), then gives up and lets the page report "building" and poll.
        var gate = UserLock(userId);
        if (allowBlockingBuild) await gate.WaitAsync(ct);
        else if (!await gate.WaitAsync(ModelLoadWait, ct))
            return _builtCache.TryGetValue(userId, out var published) ? published.Built : Built.Empty;
        Built? serve = null;
        var serveStamp = default(DateTime);
        try
        {
            if (_builtCache.TryGetValue(userId, out e)) { serve = e.Built; serveStamp = e.BuiltUtc; }
            // Durable store: deserialize (version-checked) + re-hydrate the in-memory-only attribute prior. Serving
            // a persisted model skips the whole expensive build (clustering/attribution/centroids) on cold start.
            else if (await LoadPersistedAsync(userId, sp, ct) is { } loaded)
            {
                _builtCache[userId] = (loaded.Built, loaded.BuiltUtc);
                serve = loaded.Built;
                serveStamp = loaded.BuiltUtc;
                _logger.LogInformation("Loaded the persisted taste model for user {UserId} (built {BuiltUtc:u}, {Niches} niches).",
                    userId, loaded.BuiltUtc, loaded.Built.Niches.Count);
            }
            else if (allowBlockingBuild)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var built = await BuildAsync(userId, core, sp, ct);
                _builtCache[userId] = (built, nowUtc);
                await PersistAsync(userId, built, nowUtc, ct);
                _logger.LogInformation("Built the taste model for user {UserId} in {ElapsedMs}ms ({Niches} niches, {Engaged} engaged items).",
                    userId, sw.ElapsedMilliseconds, built.Niches.Count, built.Seen.Count);
                return built;
            }
        }
        finally { gate.Release(); }

        // Outside the lock, always: a refresh takes the SAME per-user lock with a zero timeout, so scheduling one
        // while still holding it would be a silent no-op.
        if (serve is null)
        {
            _logger.LogInformation("No taste model for user {UserId} yet — building it in the background.", userId);
            ScheduleRefresh(userId);
            return Built.Empty;
        }
        return ServeAndRefreshIfStale(userId, serve, serveStamp, nowUtc);
    }

    /// <summary>Serve a model, scheduling a background rebuild when it has aged past <see cref="MaxServeAge"/>.
    /// Must be called with no per-user lock held (see <see cref="ScheduleRefresh"/>).</summary>
    private Built ServeAndRefreshIfStale(int userId, Built built, DateTime builtUtc, DateTime nowUtc)
    {
        if (nowUtc - builtUtc >= MaxServeAge) ScheduleRefresh(userId);
        return built;
    }

    /// <summary>Core's services for a background pass. Core registers them in ITS container, which this extension's
    /// isolated container can't see — so they come from the exchange Core publishes them to. (Request paths don't
    /// need this: Core hands its own instance over on the request.)</summary>
    private static ICoreServices ResolveCore(IServiceProvider sp)
        => sp.GetRequiredService<IExtensionServiceExchange>().GetAll<ICoreServices>().FirstOrDefault()
           ?? throw new InvalidOperationException("Recommendations Core hasn't published its services — is it installed and enabled?");

    /// <summary>Kick off a background model rebuild. Fire-and-forget by design: the caller is a request that must
    /// not wait for it. <c>force: false</c> so the ≤1/hour debounce still applies — a rebuild that keeps failing
    /// then retries on an hourly cadence rather than on every page open.</summary>
    private void ScheduleRefresh(int userId) => _ = RefreshAsync(userId, force: false, CancellationToken.None);

    /// <summary>Rebuild + persist a user's model, honoring the ≤1/hour debounce unless <paramref name="force"/>.
    /// Background-safe: own scope, non-blocking per-user lock, swallows errors. Drives rating events + startup warm.</summary>
    private async Task RefreshAsync(int userId, bool force, CancellationToken ct)
    {
        var rebuilt = false;
        var gate = UserLock(userId);
        if (!await gate.WaitAsync(0, ct)) return;         // a build/refresh is already in flight for this user
        _building[userId] = 0;
        try
        {
            if (!force && _builtCache.TryGetValue(userId, out var e) && DateTime.UtcNow - e.BuiltUtc < RefreshDebounce)
                return;                                    // rebuilt recently — the "max once/hour" gate
            await using var scope = _scopeFactory.CreateAsyncScope();
            var core = ResolveCore(scope.ServiceProvider);
            var nowUtc = DateTime.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = await BuildAsync(userId, core, scope.ServiceProvider, ct);
            _builtCache[userId] = (built, nowUtc);
            // Everything scored against the OLD model is now stale. Only THIS user's entries — one user rating a
            // video used to wipe every other user's cached universe too.
            DropCachedRows(userId);
            await PersistAsync(userId, built, nowUtc, ct);
            rebuilt = true;
            _logger.LogInformation("Rebuilt the taste model for user {UserId} in {ElapsedMs}ms ({Niches} niches, {Engaged} engaged items).",
                userId, sw.ElapsedMilliseconds, built.Niches.Count, built.Seen.Count);
        }
        catch (OperationCanceledException) { /* shutdown, or the caller gave up — not a failure */ }
        catch (Exception ex)
        {
            // Best-effort: the prior model stays in place and the next trigger retries. Logged because a model
            // that never rebuilds is indistinguishable from one that has nothing new to learn.
            _logger.LogError(ex, "Failed to rebuild the taste model for user {UserId}; the previous model stays in use.", userId);
        }
        finally { _building.TryRemove(userId, out _); gate.Release(); }
        // Re-warm OUTSIDE the lock (warming re-enters GetOrBuildBuiltAsync), so the next feed open is fast against
        // the new model instead of paying for a full library pass.
        if (rebuilt) await WarmUserAsync(userId, ct, wait: true);
    }

    /// <summary>Re-point warm sets from an old model instance to the calibrated copy that replaced it.
    ///
    /// Warm validity is reference identity against the current model, and folding in a calibration produces a NEW
    /// instance. Without this, opening the images feed (which adds the image calibration table) would invalidate
    /// the VIDEO warm set — costing a full library rescan on the next video open — even though nothing that
    /// affects video row scoring changed. Only the calibration tables differ, and rows don't depend on those.</summary>
    private static void RepointWarm(int userId, Built oldModel, Built newModel)
    {
        if (_warmVideo.TryGetValue(userId, out var v) && ReferenceEquals(v.Model, oldModel))
            _warmVideo[userId] = v with { Model = newModel };
        if (_warmImage.TryGetValue(userId, out var i) && ReferenceEquals(i.Model, oldModel))
            _warmImage[userId] = i with { Model = newModel };
    }

    /// <summary>Drop one user's cached scored universes (row cache + warm sets).</summary>
    private static void DropCachedRows(int userId)
    {
        _warmVideo.TryRemove(userId, out _);
        _warmImage.TryRemove(userId, out _);
        foreach (var key in _rowCache.Keys)
            if (key.StartsWith($"{userId}:", StringComparison.Ordinal) || key.StartsWith($"img:{userId}:", StringComparison.Ordinal))
                _rowCache.TryRemove(key, out _);
    }

    /// <summary>A rating/engagement change for this user — schedule a debounced background model refresh.</summary>
    public void OnEngagementChanged(int userId) => _ = RefreshAsync(userId, force: false, CancellationToken.None);

    /// <summary>On startup, for every user with a persisted model: bring the model up to date and precompute the
    /// library-wide scored pass, so opening the recommendations page reads precomputed rows instead of scoring the
    /// whole library first. Sequential and background — it never blocks host startup, and unknown users still build
    /// lazily on first use.
    ///
    /// Hydrating the persisted model FIRST is what keeps this to a single expensive pass: it gives the refresh
    /// below a real build timestamp to debounce against, so a model that's already fresh (the normal restart) skips
    /// straight to warming instead of rebuilding and then warming a second time.</summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        List<int> users;
        try { users = await _modelStore.GetKnownUserIdsAsync(ct); }
        catch (Exception ex) { _logger.LogError(ex, "Startup warm-up could not list users with a persisted taste model; skipping it."); return; }
        if (users.Count == 0) return;

        _logger.LogInformation("Warming recommendations for {UserCount} user(s) with a persisted taste model.", users.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var userId in users)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var core = ResolveCore(scope.ServiceProvider);
                await GetOrBuildBuiltAsync(userId, core, scope.ServiceProvider, ct, allowBlockingBuild: true);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Startup warm-up could not load the taste model for user {UserId}; it will build lazily.", userId); }
            await RefreshAsync(userId, force: false, ct);   // rebuilds (and re-warms) only a stale model
            await WarmUserAsync(userId, ct);                // no-op when the refresh already warmed this model
        }
        _logger.LogInformation("Startup warm-up finished in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }

    private async Task<(Built Built, DateTime BuiltUtc)?> LoadPersistedAsync(int userId, IServiceProvider sp, CancellationToken ct)
    {
        var json = await _modelStore.LoadAsync(userId, ct);
        if (json is null) return null;
        if (TryDeserializeModel(json) is not { } parsed)
        {
            // Version mismatch (the expected case after a ModelVersion bump) or corruption — either way, rebuild.
            _logger.LogInformation("Discarding the persisted taste model for user {UserId}: it is not readable at model version {ModelVersion}. Rebuilding.", userId, ModelVersion);
            return null;
        }
        // PerfAttrPrior is in-memory-only — recompute it from the persisted AttrCells + current performer attributes.
        var db = sp.GetRequiredService<DbContext>();
        var priors = parsed.Built.AttrCells is { Count: > 0 } cells ? await ComputePerfAttrPriorsAsync(db, cells, ct) : new();
        return (parsed.Built with { PerfAttrPrior = priors }, parsed.BuiltUtc);
    }

    private async Task PersistAsync(int userId, Built built, DateTime builtUtc, CancellationToken ct)
    {
        if (!_modelStore.Ready) return;
        try { await _modelStore.SaveAsync(userId, SerializeModel(built, builtUtc), ct); }
        catch (OperationCanceledException) { /* shutdown — the in-memory model is still current */ }
        catch (Exception ex)
        {
            // Best-effort: the in-memory model is unaffected. Logged because the cost lands on the NEXT restart,
            // which silently rebuilds from scratch instead of loading in milliseconds.
            _logger.LogError(ex, "Could not persist the taste model for user {UserId}; it will be rebuilt after the next restart.", userId);
        }
    }

    // ── Model (de)serialization ───────────────────────────────────────────────
    // A version-stamped DTO mirror of Built with STJ-friendly types (no ValueTuples/private Calib). Bump
    // ModelVersion whenever Built's shape or the build logic changes so stale persisted models are discarded
    // (→ rebuilt) rather than deserialized into the wrong shape. Calibrations (re-folded from the feed pass) and
    // PerfAttrPrior (recomputed on load) are intentionally NOT serialized.
    private const int ModelVersion = 10;  // v10: audio-direction/fps experiments reverted; audio-lift gain to reduce dead-band silence
    private sealed record AffinityDto(double A, int L, int D, double Idf, int Lib, string S, double C);
    private sealed record TopTagDto(int Id, double W);
    private sealed record NicheDto(int Index, string Label, List<int> Members, float[]? Feat, float[]? Sem,
        Dictionary<int, double> Tags, Dictionary<int, double> Perfs, Dictionary<int, double> Studios, List<TopTagDto> TopTags);
    private sealed record ModelDto(
        int Version, long BuiltUtcTicks, List<NicheDto> Niches,
        Dictionary<int, AffinityDto> GTags, Dictionary<int, AffinityDto> GPerfs, Dictionary<int, AffinityDto> GStudios,
        List<int> Seen, float[]? Taste,
        List<float[]> LikedFaces, List<float[]> DislikedFaces, List<float[]> LikedAudio, List<float[]> DislikedAudio,
        List<float[]> DislikedNiche, float[]? Quality, Dictionary<string, double[]> AttrCells, List<int> PseudoPerfIds,
        Dictionary<int, double> EngagedScores,
        float[]? ImageTaste, List<float[]> ImageLiked, List<float[]> ImageDisliked);

    private static string SerializeModel(Built b, DateTime builtUtc)
    {
        static AffinityDto A(RecHelpers.AffinityDetail d) => new(d.Affinity, d.LikedCount, d.DislikedCount, d.Idf, d.LibraryCount, d.Source, d.Confidence);
        var dto = new ModelDto(ModelVersion, builtUtc.Ticks,
            b.Niches.Select(n => new NicheDto(n.Index, n.Label, n.MemberIds, n.FeatCentroid, n.SemCentroid,
                n.Tags, n.Performers, n.Studios, n.TopTags.Select(t => new TopTagDto(t.id, t.weight)).ToList())).ToList(),
            b.GlobalTags.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            b.GlobalPerfs.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            b.GlobalStudios.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            b.Seen.ToList(), b.TasteVector,
            b.LikedFaceCentroids, b.DislikedFaceCentroids, b.LikedAudioCentroids, b.DislikedAudioCentroids,
            b.DislikedNicheCentroids, b.QualityVector,
            b.AttrCells is null ? new() : b.AttrCells.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.aff, kv.Value.conf }),
            b.PseudoPerfIds?.ToList() ?? [], b.EngagedScores ?? new(),
            b.ImageTasteVector, b.ImageLikedCentroids ?? [], b.ImageDislikedCentroids ?? []);
        return JsonSerializer.Serialize(dto);
    }

    private static (Built Built, DateTime BuiltUtc)? TryDeserializeModel(string json)
    {
        ModelDto? dto;
        try { dto = JsonSerializer.Deserialize<ModelDto>(json); } catch { return null; }
        if (dto is null || dto.Version != ModelVersion) return null;
        static RecHelpers.AffinityDetail A(AffinityDto d) => new(d.A, d.L, d.D, d.Idf, d.Lib, d.S, d.C);
        var niches = dto.Niches.Select(n => new Niche(n.Index, n.Label, n.Members, n.Feat, n.Sem,
            n.Tags, n.Perfs, n.Studios, n.TopTags.Select(t => (t.Id, t.W)).ToList())).ToList();
        var attrCells = dto.AttrCells.ToDictionary(kv => kv.Key,
            kv => (kv.Value.Length > 0 ? kv.Value[0] : 0.0, kv.Value.Length > 1 ? kv.Value[1] : 0.0));
        var built = new Built(niches,
            dto.GTags.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            dto.GPerfs.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            dto.GStudios.ToDictionary(kv => kv.Key, kv => A(kv.Value)),
            dto.Seen.ToHashSet(), dto.Taste,
            dto.LikedFaces, dto.DislikedFaces, dto.LikedAudio, dto.DislikedAudio, dto.DislikedNiche,
            dto.Quality, Calibrations: null, AttrCells: attrCells, PseudoPerfIds: (dto.PseudoPerfIds ?? []).ToHashSet(),
            EngagedScores: dto.EngagedScores ?? new(),
            ImageTasteVector: dto.ImageTaste, ImageLikedCentroids: dto.ImageLiked ?? [], ImageDislikedCentroids: dto.ImageDisliked ?? []);
        return (built, new DateTime(dto.BuiltUtcTicks, DateTimeKind.Utc));
    }

    /// <summary>Stage-0 calibration, built lazily and cached onto <see cref="Built"/>. The FIRST library-wide
    /// scoring pass (the default unfiltered feed) doubles as the reference distribution — so cold load no longer
    /// pays for a separate sample scan on top of the universe pass. <paramref name="libraryWideRows"/> is the
    /// already-scored whole-library rows when the caller has them (feed); anything filtered/seeded/scoped, or a
    /// probes-first open, passes null and falls back to a strided library sample. Idempotent, and published onto
    /// the shared model cache so every entry point (feed, Why, probes) reuses one table for its 5-min lifetime.</summary>
    private async Task<Built> EnsureCalibratedAsync(Built built, int userId, IReadOnlyList<Cand>? libraryWideRows,
        IEmbeddingRepository repo, DbContext db, CancellationToken ct)
    {
        if (built.Calibrations is not null) return built;
        IReadOnlyList<Cand> sample;
        if (libraryWideRows is not null)
            sample = libraryWideRows;                              // reuse the universe rows we just scored — no extra fetch
        else
        {
            var sampleIds = await RecHelpers.SampleVideoIdsAsync(db, CalibSampleCap, ct);
            var sampleVecs = await RecHelpers.GetVisualVectorsAsync(repo, sampleIds, ct);
            sample = await BuildScoredRowsAsync(sampleVecs, built, null, null, null, repo, db, ct);
        }
        var calibs = BuildCalibrations(sample);
        FitLearnedCurves(calibs, sample, built.EngagedScores);   // learned enjoyment-shape curves where rated data supports it
        var calibrated = built with { Calibrations = calibs };
        // Publish onto the shared cache so later requests skip this; keep the model's ORIGINAL build stamp so its
        // serve-age isn't silently extended (only publish if the entry is still the exact model we just calibrated).
        if (_builtCache.TryGetValue(userId, out var e) && ReferenceEquals(e.Built, built))
            _builtCache[userId] = (calibrated, e.BuiltUtc);
        RepointWarm(userId, built, calibrated);
        return calibrated;
    }

    /// <summary>Image analogue of <see cref="EnsureCalibratedAsync"/> — a separate percentile reference over the
    /// IMAGE library (image signals live on their own visual distribution). Learned curves are video-only for now.</summary>
    private async Task<Built> EnsureImageCalibratedAsync(Built built, int userId, IReadOnlyList<Cand>? libraryWideRows,
        IEmbeddingRepository repo, DbContext db, CancellationToken ct)
    {
        if (built.ImageCalibrations is not null) return built;
        IReadOnlyList<Cand> sample;
        if (libraryWideRows is not null) sample = libraryWideRows;
        else
        {
            var sampleIds = await RecHelpers.SampleImageIdsAsync(db, CalibSampleCap, ct);
            var sampleVecs = await RecHelpers.GetImageVisualVectorsAsync(repo, sampleIds, ct);
            sample = await BuildImageRowsAsync(sampleVecs, built, repo, db, ct);
        }
        var calibrated = built with { ImageCalibrations = BuildCalibrations(sample) };
        if (_builtCache.TryGetValue(userId, out var e) && ReferenceEquals(e.Built, built))
            _builtCache[userId] = (calibrated, e.BuiltUtc);
        RepointWarm(userId, built, calibrated);
        return calibrated;
    }

    /// <summary>Per-image RAW signals (knob-independent), reusing the SHARED taste (global tag/performer/studio
    /// affinities, face centroids, pseudo-performers, attribute prior) with the IMAGE visual model (look axis +
    /// image content clusters). No niches (images aren't in the video embedding space), no audio/quality/temporal.</summary>
    private static async Task<List<Cand>> BuildImageRowsAsync(
        IReadOnlyDictionary<int, RecHelpers.VisualVec> candVecs, Built built, IEmbeddingRepository repo, DbContext db, CancellationToken ct)
    {
        static double Sim(float[]? a, float[]? b) => a is not null && b is not null ? Math.Clamp(Vectors.Cosine(a, b), 0, 1) : 0;
        static double SoftFit(double s) => Math.Clamp((s - NicheFitThreshold) / (1 - NicheFitThreshold), 0, 1);
        var candIds = candVecs.Keys.ToList();
        if (candIds.Count == 0) return new();
        var meta = await RecHelpers.FetchImageMetaAsync(db, candIds, ct);
        var haveFaceData = built.LikedFaceCentroids.Count > 0 || built.DislikedFaceCentroids.Count > 0 || built.PseudoPerfIds is { Count: > 0 };
        var candFaceApp = haveFaceData ? await RecHelpers.FetchImageFaceAppearancesAsync(db, candIds, ct) : new();
        var candFaceEmb = built.LikedFaceCentroids.Count > 0 && candFaceApp.Count > 0
            ? await RecHelpers.GetFaceEmbeddingsAsync(repo, candFaceApp.Values.SelectMany(l => l.Select(x => x.faceId)).Distinct().ToList(), ct)
            : new();

        var rows = new List<Cand>(candIds.Count);
        foreach (var id in candIds)
        {
            if (!meta.TryGetValue(id, out var m) || !candVecs.TryGetValue(id, out var cv)) continue;
            var c = new Cand { Id = id, IsImage = true };
            // TAGS / STUDIO — GLOBAL affinity (images have no niches).
            double tagRaw = 0; foreach (var t in m.TagIds) tagRaw += built.GlobalTags.TryGetValue(t, out var td) ? td.Affinity : 0;
            c.TagRaw = tagRaw; c.TagPresent = m.TagIds.Length > 0;
            c.TagKnownFrac = m.TagIds.Length > 0 ? m.TagIds.Count(t => built.GlobalTags.ContainsKey(t)) / (double)m.TagIds.Length : 0;
            c.Studio = m.StudioId is { } sid && built.GlobalStudios.TryGetValue(sid, out var sd) ? sd.Affinity : 0;
            c.StudioPresent = m.StudioId is not null; c.StudioConf = c.StudioPresent ? 0.5 : 0;
            // PERFORMERS — global affinity + recurring unlinked pseudo-performers; direct-rating bypass preserved.
            var perfIds = m.PerformerIds; c.PseudoCount = 0;
            if (built.PseudoPerfIds is { Count: > 0 } pseudo && candFaceApp.TryGetValue(id, out var pf))
            {
                var px = pf.Select(x => x.faceId).Where(pseudo.Contains).Distinct().Select(f => f + PseudoPerfOffset).ToArray();
                if (px.Length > 0) { perfIds = [.. perfIds, .. px]; c.PseudoCount = px.Length; }
            }
            double bestPerf = 0, bestAbs = -1; var driver = 0;
            foreach (var p in perfIds) { var a = built.GlobalPerfs.TryGetValue(p, out var pd) ? pd.Affinity : 0; if (Math.Abs(a) > bestAbs) { bestAbs = Math.Abs(a); bestPerf = a; driver = p; } }
            c.Perf = bestPerf; c.PerfDriverId = driver; c.PerfPresent = perfIds.Length > 0;
            c.PerfKnownFrac = perfIds.Length > 0 ? perfIds.Count(p => built.GlobalPerfs.ContainsKey(p)) / (double)perfIds.Length : 0;
            if (driver != 0 && built.GlobalPerfs.TryGetValue(driver, out var gpd)) { c.PerfConf = gpd.Confidence > 0 ? gpd.Confidence : c.PerfKnownFrac; c.PerfDirect = gpd.Source == "direct"; }
            else c.PerfConf = c.PerfKnownFrac;
            double attrBest = 0, attrBestAbs = -1, attrBestConf = 0; var attrKnown = 0;
            if (built.PerfAttrPrior is { Count: > 0 } pap)
                foreach (var p in m.PerformerIds)
                    if (pap.TryGetValue(p, out var pa)) { attrKnown++; if (Math.Abs(pa.prior) > attrBestAbs) { attrBestAbs = Math.Abs(pa.prior); attrBest = pa.prior; attrBestConf = pa.conf; } }
            c.PerfAttr = attrBest;
            c.PerfAttrConf = m.PerformerIds.Length > 0 ? attrBestConf * (attrKnown / (double)m.PerformerIds.Length) : 0;
            // FACE — SHARED centroids (face embedding space is shared with video).
            c.Face = built.LikedFaceCentroids.Count > 0 ? RecHelpers.FaceFit(candFaceApp.GetValueOrDefault(id), candFaceEmb, built.LikedFaceCentroids, built.DislikedFaceCentroids) : 0;
            double maxFaceSim = 0; var hasFace = false;
            if ((built.LikedFaceCentroids.Count > 0 || built.DislikedFaceCentroids.Count > 0) && candFaceApp.TryGetValue(id, out var appFaces))
                foreach (var (fid, _) in appFaces)
                    if (candFaceEmb.TryGetValue(fid, out var fe)) { hasFace = true; foreach (var ce in built.LikedFaceCentroids) maxFaceSim = Math.Max(maxFaceSim, Vectors.Cosine(fe, ce)); foreach (var ce in built.DislikedFaceCentroids) maxFaceSim = Math.Max(maxFaceSim, Vectors.Cosine(fe, ce)); }
            c.FaceConf = hasFace ? Math.Clamp(maxFaceSim, 0, 1) : 0; c.FaceNovelty = hasFace ? 1 - c.FaceConf : 0;
            // IMAGE LOOK axis + content clusters (image-specific visual model).
            c.Taste = built.ImageTasteVector is not null && cv.Feature is not null ? Vectors.Cosine(cv.Feature, built.ImageTasteVector) : 0;
            c.LookConf = built.ImageTasteVector is not null && cv.Feature is not null ? 0.6 : 0;
            var likeSim = built.ImageLikedCentroids is { Count: > 0 } il ? il.Max(cn => Sim(cv.Feature, cn)) : 0;
            var disSim = built.ImageDislikedCentroids is { Count: > 0 } idl ? idl.Max(cn => Sim(cv.Feature, cn)) : 0;
            c.NicheFit = SoftFit(likeSim) - 0.5 * SoftFit(disSim);
            c.ClusterConf = Math.Clamp(Math.Max(likeSim, disSim), 0, 1); c.ClusterNovelty = 1 - c.ClusterConf;
            // No audio / quality for images → silent.
            c.Quality = 0; c.QualityConf = 0; c.AudioFit = 0; c.AudioConf = 0; c.AudioPresent = false;
            c.TopTags = m.TagIds.Where(t => built.GlobalTags.ContainsKey(t)).OrderByDescending(t => Math.Abs(built.GlobalTags[t].Affinity)).Take(5).Select(t => (t, built.GlobalTags[t].Affinity)).ToList();
            c.TopPerfs = m.PerformerIds.Where(p => built.GlobalPerfs.ContainsKey(p)).OrderByDescending(p => built.GlobalPerfs[p].Affinity).Take(2).ToList();
            c.PerfIds = m.PerformerIds.Take(4).ToList();
            rows.Add(c);
        }
        return rows;
    }

    /// <summary>The recommended-IMAGES feed: score the image universe with the shared taste + image visual model,
    /// reusing the same v5 fusion (audio/quality silent). Filters aren't applied yet (image filtering is future);
    /// an unfiltered universe or a pre-filtered CandidateIds set both work.</summary>
    private async Task<RecommendationResult> RecommendImagesAsync(RecommendationRequest request, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IEmbeddingRepository>();
        var db = sp.GetRequiredService<DbContext>();
        var built = await GetOrBuildBuiltAsync(request.UserId, request.Core, sp, cancellationToken);
        if (built.GlobalTags.Count == 0 && built.GlobalPerfs.Count == 0 && built.LikedFaceCentroids.Count == 0 && built.ImageTasteVector is null)
            return Empty("no taste model yet (rate/engage with videos or images first)");

        double Kb(string key, double def) => RecHelpers.Knob(request.Knobs, key, def);
        var w = (performers: Kb("performersWeight", 0.75), content: Kb("contentWeight", 1.00), quality: Kb("qualityWeight", 0.40), audio: Kb("audioWeight", 0.25));
        var sub = new SubWeights(Kb("faceWeight", 0.45), Kb("performerAffinityWeight", 0.55), Kb("tagWeight", 0.45),
            Kb("tasteWeight", 0.30), Kb("nicheWeight", 0.35), Kb("studioWeight", 0.15), Kb("performerAttributeWeight", 0.15),
            Kb("qualityCraftWeight", 1.00), Kb("objectiveQualityWeight", 1.00), Kb("audioLift", 1.60));

        var libraryWide = request.CandidateIds is not { Count: > 0 };
        var universeKey = request.CandidateIds is { Count: > 0 } fu ? $"f{fu.Count}:{HashIds(fu)}" : "all";
        var rowKey = $"img:{request.UserId}:{universeKey}";
        var warmHit = false;
        List<Cand> rows;
        // As with videos: the warm pass spans the whole image library, so a filtered universe is a subset of it.
        if (TryTakeWarm(_warmImage, request.UserId, built, needsFullLibrary: !libraryWide) is { } warmRows)
        {
            if (request.CandidateIds is { Count: > 0 } filtered)
            {
                var allowed = filtered.ToHashSet();
                rows = warmRows.Where(r => allowed.Contains(r.Id)).ToList();
            }
            else rows = warmRows;
            warmHit = true;
        }
        else if (!TryGetCachedRows(rowKey, out rows!))
        {
            var universeIds = request.CandidateIds is { Count: > 0 } cu ? cu.Distinct().ToList() : await RecHelpers.AllImageIdsAsync(db, UniverseCap, cancellationToken);
            var vecs = await RecHelpers.GetImageVisualVectorsAsync(repo, universeIds, cancellationToken);
            rows = await BuildImageRowsAsync(vecs, built, repo, db, cancellationToken);
            StoreCachedRows(rowKey, rows);
        }
        if (rows.Count == 0) return Empty("no scorable images (missing image embeddings?)");
        built = await EnsureImageCalibratedAsync(built, request.UserId, libraryWide ? rows : null, repo, db, cancellationToken);
        if (libraryWide && !warmHit) _warmImage[request.UserId] = new WarmSet(rows, built, DateTime.UtcNow, rows.Count < UniverseCap);

        // Same dimension/filter/sort pipeline as videos, so the sort menu and score filters behave identically on
        // both content types (audio simply has no signal on an image, so it sits at its neutral value).
        var confById = new Dictionary<int, double>(rows.Count);
        var dims = new Dictionary<int, double[]>(rows.Count);
        foreach (var r in rows)
        {
            var (p, c, q, a) = CandAspects(r, built, sub);
            var o = FuseOverall([(w.performers, p), (w.content, c), (w.quality, q), (w.audio, a)]);
            confById[r.Id] = o.conf;
            dims[r.Id] = [1.0 / (1.0 + Math.Exp(-DisplayGain * o.key)), p.Value, c.Value, q.Value, a.Value, o.conf];
        }
        // Same shared pipeline as the video path — no diversity re-rank for images.
        var byRow = rows.ToDictionary(r => r.Id);
        var candidates = rows.Select(r => new ScoredCandidate(r.Id, dims[r.Id][0], confById[r.Id], dims[r.Id])).ToList();
        var (pageCandidates, total) = RankedFeed.SelectPage(request, candidates, DimensionKeys);
        if (total == 0) return Empty("no images match those score filters");
        var sortKey = string.IsNullOrWhiteSpace(request.SortKey) ? RecommendationRequest.SortByOverall : request.SortKey!;
        var paged = pageCandidates.Select(c => (r: byRow[c.Id], score: c.Score)).ToList();

        var names = new
        {
            Tags = await RecHelpers.TagNamesAsync(db, paged.SelectMany(x => x.r.TopTags.Select(t => t.id)), cancellationToken),
            Perfs = await RecHelpers.PerformerNamesAsync(db, paged.SelectMany(x => x.r.TopPerfs.Append(x.r.PerfDriverId)), cancellationToken),
        };
        static string Rel(double d) => $"{d * 100:+0;-0}% vs your typical";
        static string ConfTag(double cf) => $"conf {cf * 100:0}%";
        static double Shown(double d, double cf) => cf > 1e-6 ? d : 0;
        var items = paged.Select(x =>
        {
            var r = x.r;
            var (pAsp, cAsp, qAsp, aAsp) = CandAspects(r, built, sub);
            var perfNames = r.TopPerfs.Count > 0 ? string.Join(", ", r.TopPerfs.Select(p => names.Perfs.GetValueOrDefault(p, $"#{p}")))
                : r.PseudoCount > 0 ? $"{r.PseudoCount} recurring unknown face{(r.PseudoCount > 1 ? "s" : "")}" : "—";
            var tagNames = r.TopTags.Count > 0 ? string.Join(", ", r.TopTags.Select(t => (t.aff < 0 ? "−" : "") + names.Tags.GetValueOrDefault(t.id, $"#{t.id}"))) : "—";
            double dAff = r.PerfDirect ? Math.Clamp(r.Perf, -0.999, 0.999) : built.Cal("perfAff", r.Perf, true);
            double dTag = built.Cal("tag", r.TagRaw, true), dClus = built.Cal("cluster", r.NicheFit, true), dLook = built.Cal("look", r.Taste, true);
            double dFace = built.Cal("face", r.Face, true), dStud = built.Cal("studio", r.Studio, true), dAttr = built.Cal("perfAttr", r.PerfAttr, true);
            var factors = new List<ExplanationFactor>
            {
                new("performers", "Performers", pAsp.Value, $"{perfNames} · {ConfTag(pAsp.Conf)}"),
                new("perf.aff", "↳ Performer affinity", Shown(dAff, r.PerfConf), r.PerfKnownFrac > 0 ? (r.PerfDirect ? $"your explicit rating {r.Perf:+0.00}, used directly" : $"{Rel(dAff)} · known {r.PerfKnownFrac * 100:0}%") : "no rated performers"),
            };
            if (built.LikedFaceCentroids.Count > 0 || built.DislikedFaceCentroids.Count > 0)
                factors.Add(new("perf.face", "↳ Face match", Shown(dFace, r.FaceConf), r.FaceConf > 0 ? $"{Rel(dFace)} · {ConfTag(r.FaceConf)}" : "novel/absent face"));
            if (built.PerfAttrPrior is { Count: > 0 })
                factors.Add(new("perf.attr", "↳ Performer attributes", Shown(dAttr, r.PerfAttrConf), r.PerfAttrConf > 0 ? $"{Rel(dAttr)} · {ConfTag(r.PerfAttrConf)}" : "no attribute data for this cast"));
            factors.Add(new("content", "Content", cAsp.Value, $"{tagNames} · {ConfTag(cAsp.Conf)}"));
            factors.Add(new("content.tag", "↳ Tags", Shown(dTag, r.TagKnownFrac), r.TagKnownFrac > 0 ? $"{Rel(dTag)} · known {r.TagKnownFrac * 100:0}%" : "no rated tags"));
            if (built.ImageTasteVector is not null) factors.Add(new("content.look", "↳ Look axis", Shown(dLook, r.LookConf), r.LookConf > 0 ? Rel(dLook) : "no image embedding"));
            factors.Add(new("content.cluster", "↳ Image clusters", Shown(dClus, r.ClusterConf), r.ClusterConf > 0.01 ? Rel(dClus) : "unlike your image clusters"));
            if (r.StudioPresent) factors.Add(new("content.studio", "↳ Studio", Shown(dStud, r.StudioConf), Rel(dStud)));
            return new ItemScore("image", r.Id, Math.Clamp(x.score, 0, 1), Math.Clamp(confById[r.Id], 0, 1), new Explanation(Band(x.score), factors));
        }).ToList();

        return new RecommendationResult(items, TotalCount: total, Diagnostics: new Dictionary<string, object>
        {
            ["recommender"] = RecommenderId,
            ["entity"] = "image",
            ["candidates"] = rows.Count,
            ["matched"] = total,
            ["sort"] = sortKey,
            ["image_taste"] = built.ImageTasteVector is not null,
            ["image_clusters"] = built.ImageLikedCentroids?.Count ?? 0,
            ["image_calibrated"] = built.ImageCalibrations?.Count ?? 0,
        });
    }

    private static bool TryGetCachedRows(string key, out List<Cand>? rows)
    {
        if (_rowCache.TryGetValue(key, out var e) && Environment.TickCount64 - e.AtMs < CacheTtlMs) { rows = e.Rows; return true; }
        rows = null; return false;
    }

    private static void StoreCachedRows(string key, List<Cand> rows)
    {
        _rowCache[key] = new RowCacheEntry(rows, Environment.TickCount64);
        if (_rowCache.Count > MaxRowCacheEntries)
            foreach (var kv in _rowCache.OrderBy(k => k.Value.AtMs).Take(_rowCache.Count - MaxRowCacheEntries).ToList())
                _rowCache.TryRemove(kv.Key, out _);
    }

    private static int HashIds(IReadOnlyList<int> ids)
    {
        // Order-independent, cheap — good enough to key a filtered universe.
        var h = 17; long sum = 0;
        foreach (var id in ids) { sum += id; h = unchecked(h * 31 + id); }
        return unchecked(h * 397 + (int)(sum & 0x7fffffff));
    }


    /// <summary>Compute each candidate's RAW per-signal values (knob-independent): fetch meta/faces, assign each
    /// to its best niche and read that niche's factorized affinities. No final Score yet (the knobs apply that).</summary>
    private static async Task<List<Cand>> BuildScoredRowsAsync(
        IReadOnlyDictionary<int, RecHelpers.VisualVec> candVecs, Built built, Niche? scopedNiche,
        float[]? seedFeat, float[]? seedSem,
        IEmbeddingRepository repo, DbContext db, CancellationToken ct)
    {
        static double Sim(float[]? a, float[]? b) => a is not null && b is not null ? Math.Clamp(Vectors.Cosine(a, b), 0, 1) : 0;
        // Membership ramp: 0 below the fit threshold (fits no cluster), rising to 1 at a perfect match.
        static double SoftFit(double s) => Math.Clamp((s - NicheFitThreshold) / (1 - NicheFitThreshold), 0, 1);
        var candIds = candVecs.Keys.ToList();
        if (candIds.Count == 0) return new();
        var meta = await RecHelpers.FetchVideoMetaAsync(db, candIds, ct);
        // Face appearances are needed for face-fit AND for pseudo-performer injection (recurring unlinked faces).
        var candFaceApp = built.LikedFaceCentroids.Count > 0 || built.PseudoPerfIds is { Count: > 0 }
            ? await RecHelpers.FetchVideoFaceAppearancesAsync(db, candIds, ct) : new();
        var candFaceEmb = built.LikedFaceCentroids.Count > 0 && candFaceApp.Count > 0
            ? await RecHelpers.GetFaceEmbeddingsAsync(repo, candFaceApp.Values.SelectMany(l => l.Select(x => x.faceId)).Distinct().ToList(), ct)
            : new();
        // Candidate audio (ECAPA) vectors only when we actually have an audio model to score against.
        var hasAudioModel = built.LikedAudioCentroids.Count > 0 || built.DislikedAudioCentroids.Count > 0;
        var candAudio = hasAudioModel ? await RecHelpers.GetAssetVectorsAsync(repo, candIds, EmbeddingModality.Audio, null, ct) : new();

        var rows = new List<Cand>(candIds.Count);
        foreach (var id in candIds)
        {
            if (!meta.TryGetValue(id, out var m) || !candVecs.TryGetValue(id, out var cv)) continue;
            var c = new Cand { Id = id };
            // When scoped to one niche ("recommend from this"), score against THAT niche; otherwise assign each
            // candidate to its best-matching niche using a FIXED visual blend (knob-independent → cacheable).
            Niche niche;
            if (scopedNiche is not null)
            {
                niche = scopedNiche;
            }
            else
            {
                double bestSim = -2; niche = built.Niches[0];
                foreach (var nb in built.Niches)
                {
                    var sim = Vectors.BlendedCosine(cv.Feature, cv.Semantic, nb.FeatCentroid, nb.SemCentroid, NicheFeatBlend, NicheSemBlend);
                    if (sim > bestSim) { bestSim = sim; niche = nb; }
                }
            }
            c.Niche = niche.Index;
            // Feature and semantic similarities kept SEPARATE so their knobs weight them
            // independently in the score. For SimilarToEntity, measure against the seed; else the niche centroid.
            var featRef = seedFeat is not null || seedSem is not null ? seedFeat : niche.FeatCentroid;
            var semRef = seedFeat is not null || seedSem is not null ? seedSem : niche.SemCentroid;
            c.FeatureSim = Sim(cv.Feature, featRef);
            c.SemanticSim = Sim(cv.Semantic, semRef);
            double tagRaw = 0;
            foreach (var t in m.TagIds) tagRaw += niche.Tags.GetValueOrDefault(t);
            c.TagRaw = tagRaw;
            // Cast for scoring = linked performers + any RECURRING UNLINKED faces on this video (pseudo-performers,
            // offset ids), so a learned unlinked-face affinity drives the same performer-affinity signal + coverage.
            var perfIds = m.PerformerIds;
            c.PseudoCount = 0;
            if (built.PseudoPerfIds is { Count: > 0 } pseudo && candFaceApp.TryGetValue(id, out var pf))
            {
                var px = pf.Select(x => x.faceId).Where(pseudo.Contains).Distinct().Select(f => f + PseudoPerfOffset).ToArray();
                if (px.Length > 0) { perfIds = [.. perfIds, .. px]; c.PseudoCount = px.Length; }
            }
            double bestPerf = 0, bestPerfAbs = -1; var perfDriver = 0;
            foreach (var p in perfIds) { var a = niche.Performers.GetValueOrDefault(p); if (Math.Abs(a) > bestPerfAbs) { bestPerfAbs = Math.Abs(a); bestPerf = a; perfDriver = p; } }
            c.Perf = bestPerf; c.PerfDriverId = perfDriver;
            c.Studio = m.StudioId is { } sid ? niche.Studios.GetValueOrDefault(sid) : 0;
            c.Taste = built.TasteVector is not null && cv.Feature is not null ? Vectors.Cosine(cv.Feature, built.TasteVector) : 0;
            c.Face = built.LikedFaceCentroids.Count > 0 ? RecHelpers.FaceFit(candFaceApp.GetValueOrDefault(id), candFaceEmb, built.LikedFaceCentroids, built.DislikedFaceCentroids) : 0;
            // Audio as a centroid FIT + CONFIDENCE: close to a known audio cluster ⇒ confident; unfamiliar ⇒ ~0 conf.
            c.AudioPresent = candAudio.ContainsKey(id);
            (c.AudioFit, c.AudioConf) = candAudio.TryGetValue(id, out var av)
                ? RecHelpers.CentroidFit(av, built.LikedAudioCentroids, built.DislikedAudioCentroids) : (0, 0);
            // NicheFit: how well this looks like a LIKED content cluster minus a fraction of a DISLIKED one, each
            // softly thresholded so a video that fits NO niche contributes ~0. Bidirectional. ClusterConf = how
            // close to ANY known cluster (novelty's complement).
            var likeSim = built.Niches.Count > 0 ? built.Niches.Max(nb => Sim(cv.Feature, nb.FeatCentroid)) : 0;
            var disSim = built.DislikedNicheCentroids.Count > 0 ? built.DislikedNicheCentroids.Max(dc => Sim(cv.Feature, dc)) : 0;
            c.NicheFit = SoftFit(likeSim) - 0.5 * SoftFit(disSim);
            c.ClusterConf = Math.Clamp(Math.Max(likeSim, disSim), 0, 1);
            c.ClusterNovelty = 1 - c.ClusterConf;
            // Face confidence = max similarity of any of this video's faces to any known (liked/disliked) centroid —
            // high ⇒ we recognize this face; low ⇒ novel face, we can't judge the performers. Novelty is its complement.
            double maxFaceSim = 0; var hasFace = false;
            if ((built.LikedFaceCentroids.Count > 0 || built.DislikedFaceCentroids.Count > 0) && candFaceApp.TryGetValue(id, out var appFaces))
                foreach (var (fid, _) in appFaces)
                    if (candFaceEmb.TryGetValue(fid, out var fe))
                    {
                        hasFace = true;
                        foreach (var ce in built.LikedFaceCentroids) maxFaceSim = Math.Max(maxFaceSim, Vectors.Cosine(fe, ce));
                        foreach (var ce in built.DislikedFaceCentroids) maxFaceSim = Math.Max(maxFaceSim, Vectors.Cosine(fe, ce));
                    }
            c.FaceConf = hasFace ? Math.Clamp(maxFaceSim, 0, 1) : 0;
            c.FaceNovelty = hasFace ? 1 - c.FaceConf : 0;
            c.Quality = built.QualityVector is not null && cv.Feature is not null ? Vectors.Cosine(cv.Feature, built.QualityVector) : 0;
            (c.ObjQual, c.ObjQualConf) = ObjectiveQuality(m.Height, m.BitRate);
            c.TagPresent = m.TagIds.Length > 0; c.PerfPresent = perfIds.Length > 0; c.StudioPresent = m.StudioId is not null;
            // How much of this video's cast / tags we actually have preference data for (confidence, not value).
            c.PerfKnownFrac = perfIds.Length > 0 ? perfIds.Count(p => niche.Performers.ContainsKey(p)) / (double)perfIds.Length : 0;
            // perfAff CONFIDENCE = the DRIVING performer's own confidence (a direct/explicit rating is certain; an
            // attributed one less so) — NOT halved by unknown co-stars. Cast coverage is a separate thing (shown as
            // "known %"); it shouldn't make an explicitly-loved performer read as uncertain. Also flag when the
            // driver is an EXPLICIT rating, so scoring can honor it directly instead of percentile-compressing it.
            if (c.PerfDriverId != 0 && built.GlobalPerfs.TryGetValue(c.PerfDriverId, out var gpc))
            { c.PerfConf = gpc.Confidence > 0 ? gpc.Confidence : c.PerfKnownFrac; c.PerfDirect = gpc.Source == "direct"; }
            else c.PerfConf = c.PerfKnownFrac;
            c.TagKnownFrac = m.TagIds.Length > 0 ? m.TagIds.Count(t => niche.Tags.ContainsKey(t)) / (double)m.TagIds.Length : 0;
            // Attribute cold-start prior: the cast member with the strongest-magnitude attribute prior (mirrors
            // perfAff's max-|affinity| pick); confidence scaled by how much of the cast we have attribute data for.
            double attrBest = 0, attrBestAbs = -1, attrBestConf = 0; var attrKnown = 0;
            if (built.PerfAttrPrior is { Count: > 0 } pap)
                foreach (var p in m.PerformerIds)
                    if (pap.TryGetValue(p, out var pa)) { attrKnown++; if (Math.Abs(pa.prior) > attrBestAbs) { attrBestAbs = Math.Abs(pa.prior); attrBest = pa.prior; attrBestConf = pa.conf; } }
            c.PerfAttr = attrBest;
            c.PerfAttrConf = m.PerformerIds.Length > 0 ? attrBestConf * (attrKnown / (double)m.PerformerIds.Length) : 0;
            // Model-level signal availability: the look/quality axes apply to any feature-embedded video; studio when tagged.
            c.LookConf = built.TasteVector is not null && cv.Feature is not null ? 0.6 : 0;
            c.QualityConf = built.QualityVector is not null && cv.Feature is not null ? 0.6 : 0;
            c.StudioConf = c.StudioPresent ? 0.5 : 0;
            // The tags that most DRIVE this niche's tag sum (largest |affinity|, either sign) — so the Why shows the
            // actual contributors (incl. any dragging-down negatives), not just the video's most-liked tags.
            c.TopTags = m.TagIds.Where(t => niche.Tags.ContainsKey(t)).OrderByDescending(t => Math.Abs(niche.Tags[t])).Take(5).Select(t => (t, niche.Tags[t])).ToList();
            c.TopPerfs = m.PerformerIds.Where(p => niche.Performers.ContainsKey(p)).OrderByDescending(p => niche.Performers[p]).Take(2).ToList();
            c.PerfIds = m.PerformerIds.Take(4).ToList();  // the actual cast (for performer-diversity re-ranking)
            rows.Add(c);
        }
        return rows;
    }
}
