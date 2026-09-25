using Cove.Core.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recommendations.Abstractions;
using System.Text.Json;
using EntityRef = Recommendations.Abstractions.EntityRef;

namespace Recommendations.Core;

/// <summary>
/// Access policies for this extension's endpoints.
///
/// Cove requires every extension endpoint to DECLARE its intent; one that declares nothing is left anonymous
/// for backward compatibility (and warned about at registration), which is not a default we want to inherit.
/// The conventions are mutually exclusive — an endpoint carrying both a permission requirement and an escape is
/// denied at request time — so each endpoint gets exactly one, applied individually rather than as a group
/// default that a per-endpoint override would then conflict with.
/// </summary>
internal static class RecommendationsEndpointPolicies
{
    /// <summary>Read access to something recommendable. <see cref="PermissionMode.Any"/> rather than All because
    /// the recommenders target videos and images independently — an images-only feed shouldn't demand
    /// <c>videos.read</c> — and Cove's own read-only roles grant both together anyway.</summary>
    private static readonly string[] MediaRead = [Permissions.VideosRead, Permissions.ImagesRead];

    /// <summary>For anything derived from the LIBRARY: feeds, item scores, taste clusters, the taste profile,
    /// training probes, self-evaluation.</summary>
    public static TBuilder RequiresLibraryRead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.RequireCovePermission(PermissionMode.Any, MediaRead);

    /// <summary>For anything that is purely the CALLER'S OWN state — which recommender they last picked, their
    /// rating neutrals, their warm status. No library permission, but still an authenticated principal: these
    /// read and write per-user rows and must never be reachable anonymously.</summary>
    public static TBuilder RequiresSignIn<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AllowWithoutCovePermission();
}

public sealed class RecommendationsCoreExtension : FullExtensionBase
{
    // Id/Name/Version come from extension.json via FullExtensionBase.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        // The reusable foundation: Part 1 scorer + the recommender pull-handle (passed into each recommender via
        // the request). LEARNING recommenders live in satellite extensions (Recommendations.Tastes, …) and are
        // resolved live from the service exchange; the one recommender that ships here is the engagement baseline,
        // which is built entirely on the scorer below and so needs nothing beyond Core itself.
        services.AddSingleton<RecSettings>();
        services.AddSingleton<PreferenceScoringService>();
        services.AddSingleton<IPreferenceScorer>(sp => sp.GetRequiredService<PreferenceScoringService>());
        services.AddSingleton<ICoreServices, CoreServices>();
        services.AddSingleton<IRecommender, EngagementRecommender>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // The KV store is set on this instance (SetStore) before InitializeAsync but isn't in DI — hand it to
        // the settings singleton so the DI-resolved scorer can read per-user rating neutrals through it.
        services.GetRequiredService<RecSettings>().SetStore(Store);
        PublishContributions<IRecommender>(services);
        // Satellites live in their own isolated containers, so ICoreServices registered above is invisible to
        // them. Their background work (model rebuilds, warm-up) has no request to carry it in, so they pull it
        // from the exchange instead.
        PublishContributions<ICoreServices>(services);
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
            Results.Ok(exchange.GetAll<IRecommender>().Select(r => r.Describe()).ToList()))
            .RequiresSignIn();

        // How ready a recommender's precomputed state is (IRecommenderWarmup) — lets the page say "preparing your
        // recommendations…" instead of appearing to hang while a cold library-wide pass runs. A recommender that
        // doesn't precompute is always "ready": it has nothing to wait for.
        group.MapGet("/status", (
            string recommender, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not IRecommenderWarmup warmable)
                return Results.Ok(new WarmStatus("ready", null, null, 0, "This recommender computes results on demand."));
            return Results.Ok(warmable.GetWarmStatus(userId));
        }).RequiresSignIn();

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
        }).RequiresLibraryRead();

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
        }).RequiresLibraryRead();

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
        }).RequiresLibraryRead();

        // Self-evaluation (IRecommenderEvaluable): grade the recommender against the user's own ratings/engagement —
        // ranking alignment, per-signal calibration health, worst inversions. So quality is measured, not eyeballed.
        group.MapGet("/eval", async (
            string recommender, string? knobs, string? split, ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange,
            ICoreServices core, ILogger<RecommendationsCoreExtension> log, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is not IRecommenderEvaluable evaluable)
                return Results.Ok(new EvalReport(0, 0, "none", null, null, [], [], [], ["This recommender doesn't support self-evaluation."]));
            Dictionary<string, double>? knobDict = null;
            if (!string.IsNullOrWhiteSpace(knobs))
                // A bad knob string falls back to the recommender's defaults rather than failing the request,
                // so the only sign it happened is this line.
                try { knobDict = JsonSerializer.Deserialize<Dictionary<string, double>>(knobs); }
                catch (JsonException ex) { log.LogDebug(ex, "Ignoring malformed knob overrides on /eval; using defaults."); }
            return Results.Ok(await evaluable.EvaluateAsync(userId, core, knobDict, string.IsNullOrWhiteSpace(split) ? "all" : split, ct));
        }).RequiresLibraryRead();

        // ── Choice memory (per user + context + entity type) ─────────────────
        group.MapGet("/preference", async (
            string? context, string? entityType, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            var value = await Store.GetAsync(PreferenceKey(userId, context, entityType), ct);
            return Results.Ok(new { recommenderId = value });
        }).RequiresSignIn();

        group.MapPut("/preference", async (
            SetPreferenceDto dto, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();
            await Store.SetAsync(PreferenceKey(userId, dto.Context, dto.EntityType), dto.RecommenderId, ct);
            return Results.Ok(new { recommenderId = dto.RecommenderId });
        }).RequiresSignIn();

        // ── Settings: per-user rating "neutral" (like/dislike boundary) per entity type ──
        group.MapGet("/settings/rating-neutrals", async (ICurrentPrincipalAccessor principal, RecSettings settings, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId) return Results.Unauthorized();
            return Results.Ok(await settings.GetNeutralsAsync(userId, ct));
        }).RequiresSignIn();

        group.MapPut("/settings/rating-neutrals", async (Dictionary<string, double> neutrals, ICurrentPrincipalAccessor principal, RecSettings settings, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId) return Results.Unauthorized();
            foreach (var (type, value) in neutrals)
                if (RecSettings.RatingEntityTypes.Contains(type.ToLowerInvariant()))
                    await settings.SetNeutralAsync(userId, type, value, ct);
            return Results.Ok(await settings.GetNeutralsAsync(userId, ct));
        }).RequiresSignIn();

        // ── Feed ─────────────────────────────────────────────────────────────
        group.MapGet("/feed", async (
            string recommender, string? context, string entityType, int? limit, int? offset,
            string? seedType, int? seedId, string? knobs, string? clusterId, string? steerTags, string? steerPerformers,
            ICurrentPrincipalAccessor principal, IExtensionServiceExchange exchange, ICoreServices core,
            ILogger<RecommendationsCoreExtension> log, CancellationToken ct) =>
        {
            if (principal.Current?.UserId is not { } userId)
                return Results.Unauthorized();

            var rec = exchange.GetAll<IRecommender>().FirstOrDefault(r => r.Describe().Id == recommender);
            if (rec is null)
                return Results.NotFound(new { error = $"Unknown recommender '{recommender}'." });

            IReadOnlyDictionary<string, double>? knobDict = null;
            if (!string.IsNullOrWhiteSpace(knobs))
            {
                // As on /eval: fall back to defaults rather than failing the request, but say so.
                try { knobDict = JsonSerializer.Deserialize<Dictionary<string, double>>(knobs); }
                catch (JsonException ex) { log.LogDebug(ex, "Ignoring malformed knobs on /feed; using defaults."); }
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
        }).RequiresLibraryRead();

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
            var sort = string.IsNullOrWhiteSpace(ff.Sort) ? RecommenderSortPrefix + "overall" : ff.Sort;

            // Which sorts belong to the RECOMMENDER (its own score dimensions and its shuffle) versus cove's
            // standard video sorts. "recommended" is the legacy alias for the recommender's overall score.
            var recSortKey = sort switch
            {
                "recommended" => RecommendationRequest.SortByOverall,
                _ when sort.StartsWith(RecommenderSortPrefix, StringComparison.Ordinal) => sort[RecommenderSortPrefix.Length..],
                _ => null,
            };
            // Score and cluster criteria travel in the object filter as ordinary criteria, written by the standard
            // filter dialog. They ALWAYS force the recommender path — that's what makes "sort by date among videos
            // whose performer score is above X" work.
            var scoreFilters = ParseScoreCriteria(dto.ObjectFilter);
            var clusterId = string.IsNullOrWhiteSpace(dto.ClusterId) ? ParseClusterId(dto.ObjectFilter) : dto.ClusterId;

            // A standard cove sort with NO recommender-side constraint means "just browse my filtered library in
            // that order" — serve it straight from cove's canonical video query (full sort/filter/paging parity);
            // scores are omitted (the UI hides the badges).
            if (isVideo && recSortKey is null && scoreFilters.Count == 0 && clusterId is null)
            {
                var (vids, plainTotal) = await videos.FindAsync(objectFilter ?? new VideoFilter(), ff, ct);
                var plain = vids.Select(v => new ItemScore("video", v.Id, 0, 0)).ToList();
                return Results.Ok(new RecommendationResult(plain, TotalCount: plainTotal));
            }

            // A filter/search restricts the ranked universe via cove's canonical video query — full filter
            // parity. Only videos have this path today (IVideoRepository); other entity types fall back to the
            // recommender's own candidate pool, so their filters/search are simply not applied yet.
            //
            // When the sort is a STANDARD cove one but a score filter is active, cove also decides the ORDER: we
            // pull the universe already sorted and tell the recommender to preserve that order, so it only filters.
            var hostOrders = isVideo && recSortKey is null;
            IReadOnlyList<int>? candidateIds = null;
            var filterActive = isVideo && (HasAnyCriteria(objectFilter) || !string.IsNullOrWhiteSpace(ff.Q));
            if (filterActive || hostOrders)
            {
                var universeFilter = new FindFilter { Q = ff.Q, Page = 1, PerPage = UniverseCap, Seed = ff.Seed };
                if (hostOrders) { universeFilter.Sort = ff.Sort; universeFilter.Direction = ff.Direction; universeFilter.Sorts = ff.Sorts; }
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
                ClusterId = clusterId,
                SteerTags = string.IsNullOrWhiteSpace(dto.SteerTags) ? null : dto.SteerTags,
                SteerPerformers = string.IsNullOrWhiteSpace(dto.SteerPerformers) ? null : dto.SteerPerformers,
                CandidateIds = candidateIds,
                Ascending = ascending,
                SortKey = hostOrders ? RecommendationRequest.SortByCandidateOrder : recSortKey,
                RandomSeed = ff.Seed,
                ScoreFilters = scoreFilters.Count > 0 ? scoreFilters : null,
            };

            var result = await rec.RecommendAsync(request, ct);
            return Results.Ok(result);
        }).RequiresLibraryRead();

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
        }).RequiresLibraryRead();

    }

    /// <summary>Ceiling on how many filtered videos we pull into the ranked universe — a filter matching more
    /// than this is trimmed to the first page of cove's query; the recommender still ranks all it's given.</summary>
    private const int UniverseCap = 20000;

    /// <summary>Marks a sort value as belonging to the RECOMMENDER's declared score dimensions (or its shuffle)
    /// rather than to cove's standard video sorts, e.g. "rec:performers", "rec:random". Namespacing them keeps the
    /// two sets from ever colliding as either side gains fields.</summary>
    public const string RecommenderSortPrefix = "rec:";

    /// <summary>Prefix for the object-filter key of a score criterion, e.g. "recScore_performers". Each score field
    /// gets its OWN key holding an ordinary numeric criterion ({ value, value2, modifier }) — the identical shape
    /// cove's own numeric criteria use — so the standard filter dialog renders and edits them with its normal
    /// number editor, and saved filters / active-filter chips / clear-all work on them with no special cases. The
    /// prefix keeps them from ever colliding with a VideoFilter property.</summary>
    public const string ScoreCriterionPrefix = "recScore_";

    /// <summary>The object-filter key carrying the taste-cluster criterion (a standard enum criterion).</summary>
    public const string ClusterCriterionKey = "recCluster";

    /// <summary>Read the score criteria out of the object filter, treating each as a standard numeric criterion.
    /// Malformed entries are skipped rather than failing the request — a stale saved filter shouldn't break the
    /// page. Values are read as doubles because scores are continuous, where the host's IntCriterion is integral.</summary>
    internal static List<ScoreCriterion> ParseScoreCriteria(JsonElement? objectFilter)
    {
        var result = new List<ScoreCriterion>();
        if (objectFilter is not { ValueKind: JsonValueKind.Object } root) return result;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.StartsWith(ScoreCriterionPrefix, StringComparison.Ordinal)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var key = property.Name[ScoreCriterionPrefix.Length..];
            if (key.Length == 0) continue;

            double? Num(string name) => property.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : null;
            var value = Num("value");
            var value2 = Num("value2");
            var comparison = ParseComparison(property.Value.TryGetProperty("modifier", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null);
            if (comparison is not { } cmp) continue;                    // a modifier we can't honor → ignore, don't guess
            if (value is null) continue;                                 // an incomplete criterion filters nothing
            if (cmp is ScoreComparison.Between or ScoreComparison.NotBetween && value2 is null) continue;
            result.Add(new ScoreCriterion(key, cmp, value, value2));
        }
        return result;
    }

    /// <summary>Map the host's criterion modifier name onto a score comparison. Accepts any casing/underscore form
    /// (the filter UI sends "GREATER_THAN"), matching how cove parses its own modifiers. Equals/NotEquals are
    /// honored for robustness even though the UI doesn't offer them on a continuous value.</summary>
    internal static ScoreComparison? ParseComparison(string? modifier)
    {
        var normalized = new string((modifier ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized switch
        {
            // The UI always writes a modifier; an absent one (a hand-built request) takes the same default the
            // criterion declares rather than the generic numeric EQUALS, which here would match nothing.
            "" => ScoreComparison.GreaterThan,
            "equals" => ScoreComparison.Equals,
            "notequals" => ScoreComparison.NotEquals,
            "greaterthan" => ScoreComparison.GreaterThan,
            "lessthan" => ScoreComparison.LessThan,
            "between" => ScoreComparison.Between,
            "notbetween" => ScoreComparison.NotBetween,
            _ => null,
        };
    }

    /// <summary>Read the taste-cluster id out of the object filter's enum criterion, if set.</summary>
    internal static string? ParseClusterId(JsonElement? objectFilter)
    {
        if (objectFilter is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty(ClusterCriterionKey, out var c)
            || c.ValueKind != JsonValueKind.Object
            || !c.TryGetProperty("value", out var v)
            || v.ValueKind != JsonValueKind.String)
            return null;
        var id = v.GetString();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

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
    internal static bool HasAnyCriteria(VideoFilter? f)
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

    /// <summary>Key for one user's remembered recommender choice. <see cref="IExtensionStore"/> is scoped to the
    /// EXTENSION, not the user — one global key-value space shared by everyone on the server — so the user id has
    /// to be part of the key or one person's choice silently becomes everyone's. (Keys written before this was
    /// namespaced simply stop resolving, and the page falls back to its preferred recommender.)</summary>
    internal static string PreferenceKey(int userId, string? context, string? entityType) =>
        $"pref:{userId}:{(context ?? "globalfeed").ToLowerInvariant()}:{(entityType ?? "any").ToLowerInvariant()}";

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
