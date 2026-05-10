using System.Collections.Concurrent;

namespace Mimir.Api.Services.Presence;

/// <summary>
/// In-memory online users tracker. SignalR Hub OnConnected/OnDisconnected'tan
/// çağrılır. Tek-instance MVP'de yeterli; multi-replica olunca Redis-distributed'a taşınır.
///
/// Connection count: aynı kullanıcının çoklu cihazı/tab'ı varsa, hepsi disconnect olunca offline sayılır.
/// </summary>
public class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    /// <summary> Yeni connection — userId zaten varsa count++. Returns true if user transitioned offline→online. </summary>
    public bool TrackConnect(Guid userId)
    {
        bool wasOffline = false;
        _connections.AddOrUpdate(
            userId,
            addValueFactory: _ => { wasOffline = true; return 1; },
            updateValueFactory: (_, count) => count + 1);
        return wasOffline;
    }

    /// <summary> Connection kapandı — count-- (0 olunca remove). Returns true if user transitioned online→offline. </summary>
    public bool TrackDisconnect(Guid userId)
    {
        bool wentOffline = false;
        _connections.AddOrUpdate(
            userId,
            addValueFactory: _ => 0,
            updateValueFactory: (_, count) =>
            {
                var newCount = count - 1;
                if (newCount <= 0) wentOffline = true;
                return newCount;
            });

        if (wentOffline) _connections.TryRemove(userId, out _);
        return wentOffline;
    }

    public bool IsOnline(Guid userId) => _connections.ContainsKey(userId);

    public IReadOnlyCollection<Guid> OnlineUserIds => _connections.Keys.ToArray();

    /// <summary> Bulk check — N user için single dictionary lookup yerine batch. </summary>
    public Dictionary<Guid, bool> AreOnline(IEnumerable<Guid> userIds) =>
        userIds.ToDictionary(id => id, id => _connections.ContainsKey(id));
}
