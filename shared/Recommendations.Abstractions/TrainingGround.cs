namespace Recommendations.Abstractions;

/// <summary>
/// A single active-learning "probe": one candidate the system wants the user to rate, chosen because rating
/// it teaches the model the most per click. When a <see cref="Reference"/> is present the probe is a
/// near-minimal-pair — the candidate is very similar to a video the user already rated except in one
/// uncertain attribute, so the rating delta isolates that attribute's effect (the contrastive /
/// independent-variable setup). The <see cref="Aspect"/> tells the UI which component rating to ask for, so
/// the answer feeds component-level (2b) supervision directly.
/// </summary>
public sealed record TrainingProbe(
    EntityRef Candidate,
    EntityRef? Reference,
    double ReferenceScore,                 // the reference's known like-score in [-1,1] (0 when no reference)
    string AttributeType,                  // "tag" | "performer" | "studio"
    int AttributeId,
    string AttributeName,
    bool AttributePresentInCandidate,      // true: candidate ADDS the attribute vs the reference; false: lacks it
    string Aspect,                         // rating to elicit: "overall" | "content" | "performers"
    double Uncertainty,                    // 0..1 — how unsure the model currently is about this attribute
    string Rationale);

/// <summary>An ordered batch of probes (most-informative first) plus an optional human summary.</summary>
public sealed record TrainingProbeSet(IReadOnlyList<TrainingProbe> Probes, string? Summary = null);

/// <summary>
/// Optional recommender capability: propose what to show next to learn the most about the user's taste with
/// the fewest ratings. Detected at runtime (like <see cref="ITasteProfile"/>) and served at
/// GET /api/ext/recommendations/training.
/// </summary>
public interface ITrainingGround
{
    /// <param name="mediaType">"video" (default) or "image" — which kind of item to surface for rating. Image
    /// probes let the user judge single frames fast; their ratings feed the same taste model.</param>
    Task<TrainingProbeSet> GetProbesAsync(int userId, ICoreServices core, int limit, string mediaType = "video", CancellationToken cancellationToken = default);
}
