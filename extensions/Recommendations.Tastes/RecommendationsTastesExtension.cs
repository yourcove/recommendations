using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;

namespace Recommendations.Tastes;

/// <summary>Satellite extension publishing the cluster + global-affinity + niche recommenders. It also owns the
/// niche recommender's DURABLE taste-model persistence: the KV store is handed to <see cref="TasteModelStore"/>
/// on init, every user with a persisted model is force-refreshed on startup, and each rating event schedules a
/// debounced (≤1/hour) background rebuild so the model tracks new ratings without recomputing on every request.</summary>
public sealed class RecommendationsTastesExtension : FullExtensionBase
{
    private NicheRecommender? _niche;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton<TasteModelStore>();
        services.AddSingleton<IRecommender, ClusterRecommender>();
        services.AddSingleton<IRecommender, AffinityRecommender>();
        services.AddSingleton<IRecommender, NicheRecommender>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Hand the (non-DI) KV store to the persistence service, then publish recommenders to the exchange.
        services.GetRequiredService<TasteModelStore>().SetStore(Store);
        PublishContributions<IRecommender>(services);

        _niche = services.GetServices<IRecommender>().OfType<NicheRecommender>().FirstOrDefault();
        // Startup refresh: rebuild every user with a persisted model so it reflects changes made while down.
        // Background so it never blocks host startup; unknown users just build lazily on first request.
        if (_niche is not null)
            _ = _niche.WarmStartAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    // A rating change (any aspect, any content type) invalidates the rater's taste model — schedule a debounced
    // background refresh. The recommender itself enforces the ≤1/hour gate and does the work off the event thread.
    protected override void DefineEventHandlers()
    {
        Task OnRating(ExtensionEvent evt, CancellationToken ct)
        {
            if (UserIdOf(evt) is { } userId) _niche?.OnEngagementChanged(userId);
            return Task.CompletedTask;
        }
        OnEvent("rating.created", OnRating);
        OnEvent("rating.updated", OnRating);
        OnEvent("rating.deleted", OnRating);
    }

    /// <summary>Extract the rating's userId from the event payload (the host publishes it in the Entity dict).</summary>
    private static int? UserIdOf(ExtensionEvent evt)
    {
        if (evt.Data is null) return null;
        var payload = evt.Data.TryGetValue("entity", out var ent) ? ent : null;
        if (payload is IDictionary<string, object?> d && d.TryGetValue("userId", out var uid) && uid is not null)
            return uid switch
            {
                int i => i,
                long l => (int)l,
                _ => int.TryParse(uid.ToString(), out var p) ? p : null,
            };
        return null;
    }
}
