using System.Text.Json;
using Cove.Plugins;

namespace Recommendations.Tastes;

/// <summary>
/// Durable per-user storage for a recommender's serialized taste model, backed by the extension KV store
/// (<see cref="IExtensionStore"/> → the <c>extension_data</c> table, so it survives restarts). The model JSON
/// is a few MB and lives directly in the value; a tiny separate index key lists which users have a persisted
/// model so startup can warm them without loading every blob. All operations are best-effort — a store miss
/// or a serialization mishap simply means the model gets rebuilt, never a crash.
/// </summary>
public sealed class TasteModelStore
{
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
        catch { return null; }
    }

    public async Task SaveAsync(int userId, string modelJson, CancellationToken ct)
    {
        if (_store is null) return;
        await _store.SetAsync(ModelKey(userId), modelJson, ct);
        await AddToIndexAsync(userId, ct);
    }

    public async Task DeleteAsync(int userId, CancellationToken ct)
    {
        if (_store is null) return;
        try { await _store.DeleteAsync(ModelKey(userId), ct); } catch { /* best effort */ }
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
        catch { return []; }
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
        catch { /* index is a best-effort convenience for startup warming */ }
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
        catch { /* best effort */ }
        finally { _indexLock.Release(); }
    }
}
