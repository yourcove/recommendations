using System.Globalization;
using Cove.Plugins;

namespace Recommendations.Core;

/// <summary>
/// Per-user recommendation settings, backed by the extension's key-value store. The store is set on the
/// extension instance (after <c>SetStore</c>) but isn't in DI, so this singleton bridges it: the extension
/// hands it the store in <c>InitializeAsync</c>, and the DI-resolved <see cref="PreferenceScoringService"/>
/// (plus the settings endpoints) read/write through here. The store is a single global KV, so every key is
/// explicitly namespaced by user id.
/// </summary>
public sealed class RecSettings
{
    /// <summary>Entity types that carry a user-configurable rating "neutral" (like/dislike boundary).</summary>
    public static readonly string[] RatingEntityTypes = ["video", "image", "tag", "performer", "studio"];
    public const double DefaultNeutral = 50;

    private IExtensionStore? _store;
    public void SetStore(IExtensionStore store) => _store = store;

    private static string NeutralKey(int userId, string entityType) => $"rating-neutral:{userId}:{entityType.ToLowerInvariant()}";

    private static double Parse(string? raw) =>
        raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, 0, 100) : DefaultNeutral;

    /// <summary>The user's rating neutral for one entity type (falls back to the scale midpoint).</summary>
    public async Task<double> GetNeutralAsync(int userId, string entityType, CancellationToken ct = default)
        => _store is null ? DefaultNeutral : Parse(await _store.GetAsync(NeutralKey(userId, entityType), ct));

    /// <summary>All rating neutrals for the user (every entity type, defaulted).</summary>
    public async Task<Dictionary<string, double>> GetNeutralsAsync(int userId, CancellationToken ct = default)
    {
        var result = RatingEntityTypes.ToDictionary(t => t, _ => DefaultNeutral);
        if (_store is null) return result;
        foreach (var t in RatingEntityTypes)
            result[t] = Parse(await _store.GetAsync(NeutralKey(userId, t), ct));
        return result;
    }

    public async Task SetNeutralAsync(int userId, string entityType, double value, CancellationToken ct = default)
    {
        if (_store is null) return;
        await _store.SetAsync(NeutralKey(userId, entityType), Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture), ct);
    }
}
