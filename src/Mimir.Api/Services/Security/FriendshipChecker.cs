using Microsoft.EntityFrameworkCore;
using Mimir.Api.Data;
using Mimir.Api.Domain;

namespace Mimir.Api.Services.Security;

public interface IFriendshipChecker
{
    Task<bool> AreAcceptedAsync(Guid a, Guid b, CancellationToken ct = default);
}

/// <summary>
/// ADR-016: DM gating. İki kullanıcı `Friendship.Accepted` ise mesajlaşma serbest.
/// Tek kayıt — istek hangi yönde gelmiş olursa olsun (RequesterId/AddresseeId).
/// </summary>
public class FriendshipChecker : IFriendshipChecker
{
    private readonly MimirDbContext _db;
    public FriendshipChecker(MimirDbContext db) => _db = db;

    public Task<bool> AreAcceptedAsync(Guid a, Guid b, CancellationToken ct = default)
    {
        if (a == b) return Task.FromResult(false);
        return _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == a && f.AddresseeId == b) || (f.RequesterId == b && f.AddresseeId == a)),
            ct);
    }
}
