namespace Recommendations.Abstractions;

/// <summary>A characterizing tag for a taste cluster (name + how strongly it defines the cluster, 0..1).</summary>
public sealed record ClusterTag(string Name, double Weight);

/// <summary>
/// One of a user's taste clusters (niches), surfaced for inspection and cluster-scoped recommendations.
/// </summary>
public sealed record TasteCluster(
    string Id,
    string Label,
    int Size,                               // number of the user's liked items in this cluster
    IReadOnlyList<EntityRef> Samples,       // representative member items (the user's own liked content)
    IReadOnlyList<ClusterTag> TopTags,      // tags that characterize this cluster vs the others
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
