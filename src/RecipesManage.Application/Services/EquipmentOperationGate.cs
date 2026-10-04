using System.Collections.Concurrent;

namespace RecipesManage.Application.Services;

/// <summary>
/// Coordinates PLC reset writes with batch lease acquisition inside the single scheduler process.
/// Locks are acquired in equipment-id order so batches bound to multiple devices cannot deadlock.
/// </summary>
public sealed class EquipmentOperationGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public static EquipmentOperationGate Shared { get; } = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> equipmentIds,
        CancellationToken ct = default)
    {
        var locks = equipmentIds
            .Distinct()
            .OrderBy(id => id)
            .Select(id => _locks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1)))
            .ToArray();
        var acquired = 0;

        try
        {
            for (; acquired < locks.Length; acquired++)
                await locks[acquired].WaitAsync(ct);
            return new Releaser(locks);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
                locks[i].Release();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim[] locks) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                for (var i = locks.Length - 1; i >= 0; i--)
                    locks[i].Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
