using NetRelay.Models;

namespace NetRelay.Services;

public sealed class NativeConnectionInventory
{
    private readonly int _missingRefreshLimit;
    private readonly Dictionary<Guid, InventoryEntry> _entries = [];
    private readonly object _lock = new();

    public NativeConnectionInventory(int missingRefreshLimit = 12)
    {
        if (missingRefreshLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(missingRefreshLimit));
        }

        _missingRefreshLimit = missingRefreshLimit;
    }

    public IReadOnlyList<NativeConnectionInfo> MergeObserved(IEnumerable<NativeConnectionInfo> observedConnections)
    {
        lock (_lock)
        {
            var observed = observedConnections
                .GroupBy(connection => connection.Id)
                .Select(group => group.First())
                .ToDictionary(connection => connection.Id);

            foreach (var connection in observed.Values)
            {
                _entries[connection.Id] = new InventoryEntry(connection, 0);
            }

            foreach (var id in _entries.Keys.Where(id => !observed.ContainsKey(id)).ToArray())
            {
                var entry = _entries[id];
                var missingRefreshes = entry.MissingRefreshes + 1;
                if (missingRefreshes > _missingRefreshLimit)
                {
                    _entries.Remove(id);
                    continue;
                }

                _entries[id] = entry with { MissingRefreshes = missingRefreshes };
            }

            return _entries.Values
                .Select(entry => entry.Connection)
                .ToArray();
        }
    }

    public void RememberExpectedState(Guid id, string name, string deviceName, bool enabled)
    {
        lock (_lock)
        {
            var status = enabled
                ? NativeConnectionStatus.Disconnected
                : NativeConnectionStatus.HardwareDisabled;
            var connection = _entries.TryGetValue(id, out var existing)
                ? existing.Connection with { Status = status }
                : new NativeConnectionInfo(id, name, deviceName, status);
            _entries[id] = new InventoryEntry(connection, 0);
        }
    }

    private sealed record InventoryEntry(NativeConnectionInfo Connection, int MissingRefreshes);
}
