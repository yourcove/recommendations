using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Logging;

namespace Recommendations.Tastes;

/// <summary>
/// Durable per-user storage for a recommender's serialized taste model, backed by the extension KV store
/// (<see cref="IExtensionStore"/> → the <c>extension_data</c> table, so it survives restarts). The model JSON
/// is a few MB and lives directly in the value; a tiny separate index key lists which users have a persisted
/// model so startup can warm them without loading every blob. All operations are best-effort — a store miss
/// or a serialization mishap simply means the model gets rebuilt, never a crash — but every failure is LOGGED,
/// because "the model silently rebuilds on every request" and "the model is fine" look identical from the UI.
/// </summary>
public sealed class TasteModelStore(ILogger<TasteModelStore> logger)
{
    private readonly ILogger<TasteModelStore> _logger = logger;
    private IExtensionStore? _store;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    private const string IndexKey = "taste-model-index";
    private static string ModelKey(int userId) => $"taste-model:{userId}";

    /// <summary>Wired in the extension's InitializeAsync (the KV store isn't in DI).</summary>
    public void SetStore(IExtensionStore store) => _store = store;
    public bool Ready => _store is not null;

    public async Task<string?> LoadAsync(int userId, CancellationToken ct)
    {
        if (_store is null) return null;
        try { return await _store.GetAsync(ModelKey(userId), ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the persisted taste model for user {UserId}; it will be rebuilt.", userId);
            return null;
        }
    }

    public async Task SaveAsync(int userId, string modelJson, CancellationToken ct)
    {
        if (_store is null) return;
        await _store.SetAsync(ModelKey(userId), modelJson, ct);
        _logger.LogDebug("Persisted taste model for user {UserId} ({Bytes} bytes).", userId, modelJson.Length);
        await AddToIndexAsync(userId, ct);
    }

    public async Task DeleteAsync(int userId, CancellationToken ct)
    {
        if (_store is null) return;
        try { await _store.DeleteAsync(ModelKey(userId), ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the persisted taste model for user {UserId}.", userId); }
        await RemoveFromIndexAsync(userId, ct);
    }

    /// <summary>Users with a persisted model (from the lightweight index) — the set to warm on startup.</summary>
    public async Task<List<int>> GetKnownUserIdsAsync(CancellationToken ct)
    {
        if (_store is null) return [];
        try
        {
            var raw = await _store.GetAsync(IndexKey, ct);
            return string.IsNullOrWhiteSpace(raw) ? [] : (JsonSerializer.Deserialize<List<int>>(raw) ?? []);
        }
        catch (Exception ex)
        {
            // Losing the index doesn't lose any model — it only means startup can't warm them ahead of time.
            _logger.LogWarning(ex, "Could not read the taste-model index; no user will be warmed at startup.");
            return [];
        }
    }

    private async Task AddToIndexAsync(int userId, CancellationToken ct)
    {
        if (_store is null) return;
        await _indexLock.WaitAsync(ct);
        try
        {
            var ids = await GetKnownUserIdsAsync(ct);
            if (ids.Contains(userId)) return;
            ids.Add(userId);
            await _store.SetAsync(IndexKey, JsonSerializer.Serialize(ids), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add user {UserId} to the taste-model index; their model is saved but won't be warmed at startup.", userId);
        }
        finally { _indexLock.Release(); }
    }

    private async Task RemoveFromIndexAsync(int userId, CancellationToken ct)
    {
        if (_store is null) return;
        await _indexLock.WaitAsync(ct);
        try
        {
            var ids = await GetKnownUserIdsAsync(ct);
            if (ids.Remove(userId))
                await _store.SetAsync(IndexKey, JsonSerializer.Serialize(ids), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove user {UserId} from the taste-model index.", userId);
        }
        finally { _indexLock.Release(); }
    }
}
