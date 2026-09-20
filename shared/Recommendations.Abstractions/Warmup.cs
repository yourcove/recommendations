namespace Recommendations.Abstractions;

/// <summary>
/// How ready a recommender's precomputed per-user state is.
/// </summary>
/// <param name="State">
/// "ready" — the feed is served from precomputed state and returns immediately;
/// "warming" — a precompute pass is running now;
/// "building" — there is no model to serve yet and one is being built, so the feed is empty until it lands.
/// Reported only when nothing is servable: a rebuild behind a model the user can already see stays invisible,
/// because the feed keeps working throughout and a progress banner over it would be a lie;
/// "cold" — nothing precomputed, so the next read pays for it.
/// </param>
/// <param name="ModelBuiltUtc">When the user's taste model was last (re)built, if there is one.</param>
/// <param name="WarmedUtc">When the precomputed scoring pass was last completed, if it is still valid.</param>
/// <param name="ItemCount">How many items the precomputed pass covers.</param>
/// <param name="Detail">A short human-readable explanation for the UI.</param>
public sealed record WarmStatus(
    string State,
    DateTime? ModelBuiltUtc,
    DateTime? WarmedUtc,
    int ItemCount,
    string? Detail = null);

/// <summary>
/// Optional capability for recommenders that PRECOMPUTE per-user state rather than deriving it per request.
/// Cove calls <see cref="WarmAsync"/> once at startup so the work happens before anyone opens the page, and the
/// UI reads <see cref="GetWarmStatus"/> to show progress instead of hanging on a cold first request.
/// A recommender that implements this is detected by Core via a type check across the shared contract.
/// </summary>
public interface IRecommenderWarmup
{
    /// <summary>Precompute state for every user the recommender already knows about. Must be safe to run in the
    /// background (own scope, swallows its own errors) and must never block host startup.</summary>
    Task WarmAsync(CancellationToken cancellationToken = default);

    /// <summary>Cheap, synchronous read of the current warm state for one user.</summary>
    WarmStatus GetWarmStatus(int userId);
}
