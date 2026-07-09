namespace Recommendations.Abstractions;

/// <summary>
/// The handle a recommender pulls Core's reusable services from. Recommenders opt into exactly what
/// they need. Grows over phases (feature access, candidate generation, taste helpers, …); for now it
/// exposes the engagement→preference scorer (Part 1).
/// </summary>
public interface ICoreServices
{
    /// <summary>Engagement → preference scoring (how much the user likes things they've interacted with).</summary>
    IPreferenceScorer Preference { get; }
}

/// <summary>
/// Part 1: turns a user's raw engagement signals into a normalized like-score with a confidence.
/// Missing signals never bias the score — they only lower confidence.
/// </summary>
public interface IPreferenceScorer
{
    /// <summary>Score how much the user likes specific entities they may have interacted with.</summary>
    Task<IReadOnlyDictionary<EntityRef, PreferenceScore>> ScoreAsync(
        int userId,
        IReadOnlyCollection<EntityRef> entities,
        PreferenceScoringOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>The user's most-liked entities (optionally of one type), highest score first.</summary>
    Task<IReadOnlyList<ScoredEntity>> GetTopLikedAsync(
        int userId,
        string? entityType = null,
        int limit = 100,
        PreferenceScoringOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A preference estimate for one entity: like-score in [-1,1], confidence in [0,1], optional why.</summary>
public sealed record PreferenceScore(double Score, double Confidence, Explanation? Why = null);

/// <summary>An entity paired with its preference score (used by GetTopLiked).</summary>
public sealed record ScoredEntity(EntityRef Entity, double Score, double Confidence, Explanation? Why = null);

/// <summary>
/// Options controlling preference scoring. <see cref="Method"/> selects the fusion method
/// ("m1" = explicit weighted average, "m2" = noisy-OR) so we can A/B them in the inspector.
/// </summary>
public sealed record PreferenceScoringOptions(string Method = "m2", bool IncludeExplanation = true);
