using Recommendations.Abstractions;

namespace Recommendations.Core;

/// <summary>
/// Core's implementation of the recommender pull-handle. Grows as Core gains services
/// (feature access, candidate generation, taste helpers); for now it exposes preference scoring.
/// </summary>
public sealed class CoreServices(IPreferenceScorer preference) : ICoreServices
{
    public IPreferenceScorer Preference { get; } = preference;
}
