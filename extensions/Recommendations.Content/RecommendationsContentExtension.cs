using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Recommendations.Abstractions;

namespace Recommendations.Content;

/// <summary>
/// A satellite recommender extension. It contributes its recommenders to the cross-extension service
/// exchange; Recommendations.Core discovers them live and drives the shared UI/orchestration. It needs
/// nothing from Core's DI — Core hands each recommender an <see cref="ICoreServices"/> via the request.
/// </summary>
public sealed class RecommendationsContentExtension : CoveExtensionBase
{
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton<IRecommender, SimplePreferenceRecommender>();
        services.AddSingleton<IRecommender, ContentRecommender>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        PublishContributions<IRecommender>(services);
        return Task.CompletedTask;
    }
}
