namespace Recommendations.Abstractions;

/// <summary>A scored entity a recommender believes the user likes — surfaced for transparency/inspection.</summary>
public sealed record ProfileEntity(
    string EntityType,        // "tag" | "performer" | "studio"
    int EntityId,
    string Name,
    double Weight,            // 0..1 affinity (how strongly the recommender thinks you like it)
    string? Detail = null);   // optional human note, e.g. "in 5 liked, rare" or "you favorite them"

/// <summary>
/// The tag / performer / studio affinities a recommender derived for a user, surfaced so the user can SEE
/// what each recommender thinks they like and judge what's working. Complements <see cref="ITasteClusters"/>
/// (visual niches). A recommender that implements this is detected by Core via a type check across the
/// shared contract.
/// </summary>
public sealed record TasteProfile(
    IReadOnlyList<ProfileEntity> Tags,
    IReadOnlyList<ProfileEntity> Performers,
    IReadOnlyList<ProfileEntity> Studios,
    string? Summary = null);

/// <summary>Optional capability: expose the user's derived tag/performer/studio affinities for inspection.</summary>
public interface ITasteProfile
{
    /// <param name="topN">How many of the strongest signals (by magnitude, so dislikes surface too) to return
    /// per facet. The inspector can raise this to see more of what the model believes — including the negatives.</param>
    Task<TasteProfile> GetProfileAsync(int userId, ICoreServices core, int topN = 60, CancellationToken cancellationToken = default);
}
