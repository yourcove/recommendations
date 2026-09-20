namespace Recommendations.Abstractions;

/// <summary>
/// One of a user's taste clusters (niches). Surfaced so the feed can offer "only this cluster" as a filter, and
/// scoped recommendations follow from that.
///
/// Deliberately minimal: representative samples and characterizing tags used to be carried here for a dedicated
/// "my tastes" browser, but that view is gone — everything it did is reachable from the main feed by filtering to
/// a cluster — so the extra payload was computed on every call and read by nobody.
/// </summary>
public sealed record TasteCluster(
    string Id,
    string Label,
    int Size,                               // number of the user's liked items in this cluster
    string? Summary = null);

/// <summary>
/// Optional capability for recommenders that model the user's tastes as distinct clusters. Lets the UI
/// show "your tastes" (and how well the clustering works) and request a feed scoped to one cluster.
/// A recommender that implements this is detected by Core via a type check across the shared contract.
/// </summary>
public interface ITasteClusters
{
    Task<IReadOnlyList<TasteCluster>> GetClustersAsync(int userId, ICoreServices core, CancellationToken cancellationToken = default);
}
