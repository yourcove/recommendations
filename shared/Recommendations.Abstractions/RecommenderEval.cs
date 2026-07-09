namespace Recommendations.Abstractions;

/// <summary>A self-evaluation of the recommender against the user's own rated/engaged videos — so ranking quality
/// (and calibration health) is measured, not eyeballed. Ground truth is the user's explicit overall ratings when
/// there are enough of them, else engagement score (which the model trains on, so treat that correlation as a
/// sanity floor, not a grade).</summary>
public sealed record EvalReport(
    int RatedCount,                             // explicit overall video ratings available
    int EvaluatedCount,                         // videos actually scored in the eval set
    string Target,                              // "ratings" or "engagement" — what CorrWithTarget was measured against
    double? ScoreVsRating,                      // Spearman rank corr (recommender score vs explicit rating); null if too few
    double? ScoreVsEngagement,                  // Spearman (score vs engagement) — sanity floor
    IReadOnlyList<SignalEval> Signals,
    IReadOnlyList<AspectEval> Aspects,
    IReadOnlyList<EvalInversion> Inversions,    // biggest rating↔rank disagreements (buried favorites / surfaced duds)
    IReadOnlyList<string> Warnings);

/// <summary>Health of one calibrated sub-signal over the eval set.</summary>
/// <param name="PresentFrac">Fraction of eval videos where the signal is present (confidence &gt; 0).</param>
/// <param name="CorrWithTarget">Spearman of the signal's calibrated value vs the ground-truth target.</param>
/// <param name="Std">Spread of calibrated values — low ⇒ the signal barely discriminates.</param>
/// <param name="SilentFrac">Fraction of present values inside the dead band (|d| &lt; band) ⇒ contribute ~nothing.</param>
/// <param name="Degenerate">Present but near-constant ⇒ effectively dead weight.</param>
public sealed record SignalEval(string Key, string Aspect, double PresentFrac, double? CorrWithTarget,
    double Std, double Min, double Max, double SilentFrac, bool HasLearnedCurve, bool Degenerate);

/// <summary>Health of one aspect belief over the eval set.</summary>
public sealed record AspectEval(string Key, double? CorrWithTarget, double AvgCoverage);

/// <summary>A video whose recommender rank disagrees most with its ground-truth rank (percentiles in [0,1]).</summary>
public sealed record EvalInversion(int VideoId, double Target, double Score, double TargetPct, double ScorePct, string Note);

/// <summary>A recommender that can grade itself against the user's own ratings/engagement.</summary>
public interface IRecommenderEvaluable
{
    /// <param name="knobs">Optional knob overrides to score with (else the recommender's defaults) — lets a tuner
    /// sweep weightings and measure the effect without redeploying.</param>
    /// <param name="split">"all" (default), "train", or "test" — a deterministic half of the rated set, so a
    /// tuner can optimize on train and validate on test to avoid overfitting.</param>
    Task<EvalReport> EvaluateAsync(int userId, ICoreServices core, IReadOnlyDictionary<string, double>? knobs = null, string split = "all", CancellationToken cancellationToken = default);
}
