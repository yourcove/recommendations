using Cove.Core.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;
using System.Text.Json;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Core;

public sealed class RecommendationsCoreExtension : FullExtensionBase
{
    // Id/Name/Version come from extension.json via FullExtensionBase.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        // The reusable foundation only: Part 1 scorer + the recommender pull-handle (passed into each
        // recommender via the request). Recommenders themselves live in satellite extensions
        // (Recommendations.Content, Recommendations.Deep, …) and are resolved live from the service exchange.
        services.AddSingleton<RecSettings>();
        services.AddSingleton<PreferenceScoringService>();
        services.AddSingleton<IPreferenceScorer>(sp => sp.GetRequiredService<PreferenceScoringService>());
        services.AddSingleton<ICoreServices, CoreServices>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // The KV store is set on this instance (SetStore) before InitializeAsync but isn't in DI — hand it to
        // the settings singleton so the DI-resolved scorer can read per-user rating neutrals through it.
        services.GetRequiredService<RecSettings>().SetStore(Store);
        return Task.CompletedTask;
    }

    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddPage(
                route: "recommended",
                label: "Recommended",
                componentName: "RecommendedPage",
                icon: "sparkles",
                showInNav: true,
                navOrder: 5)
            .AddTab(
                pageType: "video",
                key: "recommended",
                label: "Recommended",
                componentName: "RecommendedTab",
                order: 50,
                icon: "sparkles")
            .AddSettingsTab(
                "extensions/recommendations",
                "Recommendations",
                order: 100,
                icon: "sparkles",
                description: "How your engagement and ratings are turned into recommendations.",
                searchKeywords: ["recommendations", "ratings", "neutral", "taste", "preferences"])
            .AddSettingsPanel(new UISettingsPanel(
                "recommendations-rating-neutrals",
                "Rating neutrals",
                Id,
                "RecommendationsSettings",
                Order: 40,
                TargetTab: "extensions/recommendations"))
            .AddSettingsPanel(new UISettingsPanel(
                "recommendations-health",
                "Recommender health",
                Id,
                "RecommenderHealthPage",
                Order: 50,
                TargetTab: "extensions/recommendations"))
            .Build();

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/recommendations").WithTags("Recommendations");

        // ── Recommenders registry ────────────────────────────────────────────
        group.MapGet("/recommenders", (IExtensionServiceExchange exchange) =>
            Results.Ok(exchange.GetAll<IRecommender>().Select(r => r.Describe()).ToList()));

        // Taste clusters for a recommender that exposes them (ITasteClusters). Empty if it doesn't.
        group.MapGet("/clusters", async (
            string recommender, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not ITasteClusters clusterable)
                return Results.Ok(Array.Empty<object>());
            return Results.Ok(await clusterable.GetClustersAsync(userId, core, ct));
        });

        // Tag/performer/studio affinities a recommender derived (ITasteProfile) — lets the user SEE what each
        // recommender thinks they like and judge what's working. Empty/explained if it doesn't expose one.
        group.MapGet("/profile", async (
            string recommender, int? limit, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not ITasteProfile profileable)
                return Results.Ok(new TasteProfile([], [], [], "This recommender doesn't expose a tag/performer profile (it may be visual-only)."));
            return Results.Ok(await profileable.GetProfileAsync(userId, core, Math.Clamp(limit ?? 60, 1, 500), ct));
        });

        // Training grounds (ITrainingGround): active-learning probes — what to rate next to learn the most per
        // click. Each probe is a near-minimal-pair targeting an uncertain attribute. Empty/explained otherwise.
        group.MapGet("/training", async (
            string recommender, int? limit, string? mediaType, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not ITrainingGround trainable)
                return Results.Ok(new TrainingProbeSet([], "This recommender doesn't support training grounds."));
            var media = string.IsNullOrWhiteSpace(mediaType) ? "video" : mediaType;
            return Results.Ok(await trainable.GetProbesAsync(userId, core, Math.Clamp(limit ?? 20, 1, 60), media, ct));
        });

        // Self-evaluation (IRecommenderEvaluable): grade the recommender against the user's own ratings/engagement —
        // ranking alignment, per-signal calibration health, worst inversions. So quality is measured, not eyeballed.
        group.MapGet("/eval", async (
            string recommender, string? knobs, string? split, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not IRecommenderEvaluable evaluable)
                return Results.Ok(new EvalReport(0, 0, "none", null, null, [], [], [], ["This recommender doesn't support self-evaluation."]));
            Dictionary<string, double>? knobDict = null;
            if (!string.IsNullOrWhiteSpace(knobs))
                try { knobDict = JsonSerializer.Deserialize<Dictionary<string, double>>(knobs); } catch { /* ignore malformed knob overrides */ }
            return Results.Ok(await evaluable.EvaluateAsync(userId, core, knobDict, string.IsNullOrWhiteSpace(split) ? "all" : split, ct));
        });

        // ── Choice memory (per context + entity type) ────────────────────────
        group.MapGet("/preference", async (string? context, string? entityType, CancellationToken ct) =>
        {
            var key = PreferenceKey(context, entityType);
            var value = await Store.GetAsync(key, ct);
            return Results.Ok(new { recommenderId = value });
        });

        group.MapPut("/preference", async (SetPreferenceDto dto, CancellationToken ct) =>
        {
            await Store.SetAsync(PreferenceKey(dto.Context, dto.EntityType), dto.RecommenderId, ct);
            return Results.Ok(new { recommenderId = dto.RecommenderId });
        });

        // ── Settings: per-user rating "neutral" (like/dislike boundary) per entity type ──
        group.MapGet("/settings/rating-neutrals", async (ICurrentPrincipalAccessor principal, RecSettings settings, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId) return Results.Unauthorized();
            return Results.Ok(await settings.GetNeutralsAsync(userId, ct));
        });

        group.MapPut("/settings/rating-neutrals", async (Dictionary<string, double> neutrals, ICurrentPrincipalAccessor principal, RecSettings settings, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId) return Results.Unauthorized();
            foreach (var (type, value) in neutrals)
                if (RecSettings.RatingEntityTypes.Contains(type.ToLowerInvariant()))
                    await settings.SetNeutralAsync(userId, type, value, ct);
            return Results.Ok(await settings.GetNeutralsAsync(userId, ct));
        });

        // ── Feed ─────────────────────────────────────────────────────────────
        group.MapGet("/feed", async (
            string recommender, string? context, string entityType, int? limit, int? offset,
            string? seedType, int? seedId, string? knobs, string? clusterId, string? steerTags, string? steerPerformers,
            ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is null)
                return Results.NotFound(new { error = $"Unknown recommender '{recommender}'." });

            IReadOnlyDictionary<string, double>? knobDict = null;
            if (!string.IsNullOrWhiteSpace(knobs))
            {
                try { knobDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(knobs); }
                catch { /* ignore malformed knobs */ }
            }

            var ctx = ParseContext(context);
            EntityRef? seed = seedType is not null && seedId is { } sid ? new EntityRef(seedType, sid) : null;
            var request = new RecommendationRequest(ctx, userId, entityType, seed,
                Math.Clamp(limit ?? 40, 1, 200), Math.Max(0, offset ?? 0), knobDict, core)
            {
                ClusterId = string.IsNullOrWhiteSpace(clusterId) ? null : clusterId,
                SteerTags = string.IsNullOrWhiteSpace(steerTags) ? null : steerTags,
                SteerPerformers = string.IsNullOrWhiteSpace(steerPerformers) ? null : steerPerformers,
            };

            var result = await rec.RecommendAsync(request, ct);
            return Results.Ok(result);
        });

        // ── Productionalized feed: standard list controls (real search, filters, sort direction, true
        //    pagination) over the recommender's ranking. A POST so it can carry the full VideoFilter +
        //    FindFilter the standard video list page produces. When a filter/search is active, cove's own
        //    IVideoRepository.FindAsync defines the candidate universe (full filter parity, for free) and the
        //    recommender RANKS that universe; otherwise the recommender generates its own pool as before.
        group.MapPost("/feed", async (
            FeedQueryDto dto, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange,
            IVideoRepository videos, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == dto.Recommender);
            if (rec is null)
                return Results.NotFound(new { error = $"Unknown recommender '{dto.Recommender}'." });

            // Minimal-API JSON options don't register the string-enum converter that controllers use, so the
            // VideoFilter's criterion modifiers and FindFilter.Direction wouldn't bind. Deserialize those two
            // nested objects ourselves with the same options the standard video list relies on.
            var objectFilter = dto.ObjectFilter is { ValueKind: System.Text.Json.JsonValueKind.Object } oe
                ? oe.Deserialize<VideoFilter>(FilterJson) : null;
            var ff = (dto.FindFilter is { ValueKind: System.Text.Json.JsonValueKind.Object } fe
                ? fe.Deserialize<FindFilter>(FilterJson) : null) ?? new FindFilter();
            var page = Math.Max(1, ff.Page);
            var perPage = ff.PerPage <= 0 ? 200 : Math.Clamp(ff.PerPage, 1, 200);
            var offset = (page - 1) * perPage;
            var ascending = ff.Direction == SortDirection.Asc;
            var isVideo = string.Equals(dto.EntityType ?? "video", "video", StringComparison.OrdinalIgnoreCase);
            var sort = string.IsNullOrWhiteSpace(ff.Sort) ? "recommended" : ff.Sort;

            // A standard sort field (date/title/random/duration/…) means "just browse my filtered library in that
            // order" — the recommender ranking only applies to the "Recommended" sort. Serve it straight from
            // cove's canonical video query (full sort/filter/paging parity); scores are omitted (UI hides them).
            if (isVideo && !string.Equals(sort, "recommended", StringComparison.OrdinalIgnoreCase))
            {
                var (vids, plainTotal) = await videos.FindAsync(objectFilter ?? new VideoFilter(), ff, ct);
                var plain = vids.Select(v => new ItemScore("video", v.Id, 0, 0)).ToList();
                return Results.Ok(new RecommendationResult(plain, TotalCount: plainTotal));
            }

            // A filter/search restricts the ranked universe via cove's canonical video query — full filter
            // parity. Only videos have this path today (IVideoRepository); other entity types fall back to the
            // recommender's own candidate pool, so their filters/search are simply not applied yet.
            IReadOnlyList<int>? candidateIds = null;
            var filterActive = isVideo && (HasAnyCriteria(objectFilter) || !string.IsNullOrWhiteSpace(ff.Q));
            if (filterActive)
            {
                var universeFilter = new FindFilter { Q = ff.Q, Page = 1, PerPage = UniverseCap, Seed = ff.Seed };
                var (vids, _) = await videos.FindAsync(objectFilter ?? new VideoFilter(), universeFilter, ct);
                candidateIds = vids.Select(v => v.Id).ToList();
                if (candidateIds.Count == 0)
                    return Results.Ok(new RecommendationResult([], TotalCount: 0));
            }

            var ctx = ParseContext(dto.Context);
            EntityRef? seed = dto.SeedType is not null && dto.SeedId is { } sid ? new EntityRef(dto.SeedType, sid) : null;
            var request = new RecommendationRequest(ctx, userId, dto.EntityType ?? "video", seed,
                perPage, offset, dto.Knobs, core)
            {
                ClusterId = string.IsNullOrWhiteSpace(dto.ClusterId) ? null : dto.ClusterId,
                SteerTags = string.IsNullOrWhiteSpace(dto.SteerTags) ? null : dto.SteerTags,
                SteerPerformers = string.IsNullOrWhiteSpace(dto.SteerPerformers) ? null : dto.SteerPerformers,
                CandidateIds = candidateIds,
                Ascending = ascending,
            };

            var result = await rec.RecommendAsync(request, ct);
            return Results.Ok(result);
        });

        // ── Score specific items (auto-curation use-case) ────────────────────
        group.MapPost("/score", async (
            ScoreItemsDto dto, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == dto.Recommender);
            if (rec is null)
                return Results.NotFound(new { error = $"Unknown recommender '{dto.Recommender}'." });

            var items = (dto.Items ?? []).Select(i => new EntityRef(i.EntityType, i.EntityId)).ToList();
            var scores = await rec.ScoreItemsAsync(new ScoreRequest(userId, items, null, core), ct);
            return Results.Ok(scores);
        });

        // ── Engagement inspector (Phase 2: see scores and WHY) ───────────────
        group.MapGet("/inspector/top-liked", async (
            string? entityType, int? limit, string? method,
            ICurrentPrincipalAccessor principal, IPreferenceScorer scorer, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var options = new PreferenceScoringOptions(Method: method ?? "m1");
            var top = await scorer.GetTopLikedAsync(userId, NormalizeType(entityType), Math.Clamp(limit ?? 50, 1, 500), options, ct);
            return Results.Ok(top);
        });

        group.MapGet("/inspector/score/{entityType}/{entityId:int}", async (
            string entityType, int entityId, string? method,
            ICurrentPrincipalAccessor principal, IPreferenceScorer scorer, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var options = new PreferenceScoringOptions(Method: method ?? "m1");
            var entity = new EntityRef(entityType, entityId);
            var scores = await scorer.ScoreAsync(userId, [entity], options, ct);
            return scores.TryGetValue(entity, out var score)
                ? Results.Ok(score)
                : Results.Ok(new PreferenceScore(0, 0));
        });

        // Raw diagnostics: what engagement data is actually readable for the current user.
        group.MapGet("/inspector/debug", async (
            ICurrentPrincipalAccessor principal, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            await using var scope = scopeFactory.CreateAsyncScope();
            var read = scope.ServiceProvider.GetRequiredService<Cove.Core.Interfaces.IUserEngagementReadService>();
            var affinities = await read.GetAffinitiesForUserAsync(userId, cancellationToken: ct);
            var ratings = await read.GetRatingsForUserAsync(userId, cancellationToken: ct);

            return Results.Ok(new
            {
                userId,
                affinityCount = affinities.Count,
                ratingCount = ratings.Count,
                affinityByType = affinities.GroupBy(a => a.HostType.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                withNonRatingSignal = affinities.Count(a => a.IsFavorite || a.ViewCount > 0 || a.LikeCount > 0 || a.DerivedLikeCount > 0 || a.CompleteCount > 0 || a.TotalConsumedSec > 0),
                sampleAffinities = affinities.Take(15).Select(a => new
                {
                    type = a.HostType.ToString(),
                    a.HostId,
                    a.IsFavorite,
                    a.ViewCount,
                    a.CompleteCount,
                    a.LikeCount,
                    a.DerivedLikeCount,
                    a.TotalConsumedSec,
                    a.IsBookmarked,
                }),
            });
        });

        // Compare both fusion methods side-by-side for one entity (tuning aid).
        group.MapGet("/inspector/compare/{entityType}/{entityId:int}", async (
            string entityType, int entityId,
            ICurrentPrincipalAccessor principal, IPreferenceScorer scorer, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var entity = new EntityRef(entityType, entityId);
            var m1 = (await scorer.ScoreAsync(userId, [entity], new PreferenceScoringOptions("m1"), ct)).GetValueOrDefault(entity);
            var m2 = (await scorer.ScoreAsync(userId, [entity], new PreferenceScoringOptions("m2"), ct)).GetValueOrDefault(entity);
            return Results.Ok(new { m1, m2 });
        });
    }

    /// <summary>Ceiling on how many filtered videos we pull into the ranked universe — a filter matching more
    /// than this is trimmed to the first page of cove's query; the recommender still ranks all it's given.</summary>
    private const int UniverseCap = 20000;

    /// <summary>The JSON options the standard video list page's filter relies on (camelCase + string enums) —
    /// used to deserialize the VideoFilter/FindFilter bodies, which minimal-API binding wouldn't handle.
    /// CriterionModifier needs the same lenient (any case / underscores / numeric) parsing cove uses.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions FilterJson =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            // The specific CriterionModifier converter MUST precede the general string-enum factory — the factory
            // matches ALL enums, so if it came first it would (wrongly) handle CriterionModifier and reject
            // values like "INCLUDES_ALL". First matching converter wins.
            Converters =
            {
                new CriterionModifierConverter(),
                new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase),
            },
        };

    /// <summary>Mirrors cove's (internal) CriterionModifier JSON converter: accepts the enum by name in any
    /// casing/underscore form, or by its numeric value. Needed because the standard filter UI sends modifiers
    /// like "INCLUDES_ALL" that the default string-enum converter can't parse.</summary>
    private sealed class CriterionModifierConverter : System.Text.Json.Serialization.JsonConverter<CriterionModifier>
    {
        public override CriterionModifier Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            {
                var normalized = Normalize(reader.GetString());
                foreach (var name in Enum.GetNames<CriterionModifier>())
                    if (string.Equals(Normalize(name), normalized, StringComparison.OrdinalIgnoreCase))
                        return Enum.Parse<CriterionModifier>(name);
            }
            if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(CriterionModifier), n))
                return (CriterionModifier)n;
            throw new System.Text.Json.JsonException($"Invalid criterion modifier token '{reader.TokenType}'.");
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, CriterionModifier value, JsonSerializerOptions options)
            => writer.WriteStringValue(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

        private static string Normalize(string? value)
            => value is null ? "" : new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private static readonly System.Reflection.PropertyInfo[] VideoFilterProps =
        typeof(VideoFilter).GetProperties();

    /// <summary>True if the caller set ANY video filter criterion (so we should restrict the ranked universe).</summary>
    private static bool HasAnyCriteria(VideoFilter? f)
    {
        if (f is null) return false;
        foreach (var p in VideoFilterProps)
        {
            var v = p.GetValue(f);
            if (v is null) continue;
            if (v is System.Collections.ICollection c) { if (c.Count > 0) return true; }
            else return true;
        }
        return false;
    }

    private static string PreferenceKey(string? context, string? entityType) =>
        $"pref:{(context ?? "globalfeed").ToLowerInvariant()}:{(entityType ?? "any").ToLowerInvariant()}";

    private static string? NormalizeType(string? entityType) =>
        string.IsNullOrWhiteSpace(entityType) || entityType.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? null
            : entityType;

    private static RecommendationContext ParseContext(string? context) =>
        context?.ToLowerInvariant() switch
        {
            "similartoentity" or "similar" => RecommendationContext.SimilarToEntity,
            "scoreitems" or "score" => RecommendationContext.ScoreItems,
            "rerank" => RecommendationContext.Rerank,
            "training" => RecommendationContext.Training,
            _ => RecommendationContext.GlobalFeed,
        };

    private sealed record SetPreferenceDto(string? Context, string? EntityType, string RecommenderId);
    private sealed record ScoreItemsDto(string Recommender, List<EntityRefDto>? Items);
    private sealed record EntityRefDto(string EntityType, int EntityId);

    /// <summary>The productionalized feed request: recommender params plus the standard video list page's
    /// <see cref="VideoFilter"/> (advanced filter criteria) and <see cref="FindFilter"/> (search/sort/paging).</summary>
    private sealed record FeedQueryDto(
        string Recommender,
        string? Context,
        string? EntityType,
        string? SeedType,
        int? SeedId,
        Dictionary<string, double>? Knobs,
        string? ClusterId,
        string? SteerTags,
        string? SteerPerformers,
        System.Text.Json.JsonElement? ObjectFilter,
        System.Text.Json.JsonElement? FindFilter);
}
