using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;

namespace Recommendations.Deep;

/// <summary>
/// Satellite extension contributing the "Deep blend" recommender. Published to the service exchange and
/// discovered live by Recommendations.Core. Declares its own tunable knobs in the recommender descriptor,
/// which Core's UI renders generically — no recommender-specific UI code lives in Core.
/// </summary>
public sealed class RecommendationsDeepExtension : CoveExtensionBase
{
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton<IRecommender, DeepRecommender>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        PublishContributions<IRecommender>(services);
        return Task.CompletedTask;
    }
}
