using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Services.Presence;

namespace Mimir.Api.Controllers;

/// <summary>
/// ADR-016: arkadaşlık modeli endpoint'leri.
/// </summary>
[ApiController]
[Route("api/friends")]
[Authorize]
public class FriendsController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly PresenceTracker _presence;
    public FriendsController(MimirDbContext db, PresenceTracker presence)
    {
        _db = db;
        _presence = presence;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id : throw new InvalidOperationException("user_id_missing");

    // POST /api/friends/requests — başkasının friend key'iyle istek gönder
    [HttpPost("requests")]
    [EnableRateLimiting("friend-request")]
    public async Task<IActionResult> SendRequest([FromBody] SendFriendRequestRequest req, CancellationToken ct)
    {
        var me = CurrentUserId;
        var key = req.FriendKey.Trim();

        var target = await _db.Users.FirstOrDefaultAsync(
            u => u.FriendKey == key && u.Status == UserStatus.Active, ct);
        if (target is null) return NotFound(new { error = "friend_key_not_found" });
        if (target.Id == me) return BadRequest(new { error = "cannot_friend_self" });

        var existing = await _db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == me && f.AddresseeId == target.Id) ||
            (f.RequesterId == target.Id && f.AddresseeId == me), ct);
        if (existing is not null)
        {
            return existing.Status switch
            {
                FriendshipStatus.Accepted => BadRequest(new { error = "already_friends" }),
                FriendshipStatus.Pending => BadRequest(new { error = "request_already_pending" }),
                FriendshipStatus.Blocked => Forbid(),
                _ => await ResubmitRequestAsync(existing, me, ct),
            };
        }

        var f = new Friendship
        {
            RequesterId = me,
            AddresseeId = target.Id,
            Status = FriendshipStatus.Pending,
        };
        _db.Friendships.Add(f);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/friends/requests/{f.Id}", ToDto(f, target.Username, isMineSent: true));
    }

    private async Task<IActionResult> ResubmitRequestAsync(Friendship existing, Guid me, CancellationToken ct)
    {
        // Önce reject edildiyse, yeniden istek için Pending'e çevir + RequesterId/AddresseeId güncelle
        existing.RequesterId = me;
        existing.AddresseeId = existing.RequesterId == me ? existing.AddresseeId : existing.RequesterId;
        existing.Status = FriendshipStatus.Pending;
        existing.RespondedAt = null;
        existing.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = "resubmitted" });
    }

    // GET /api/friends/requests/pending — bana gelen + benim gönderdiğim
    [HttpGet("requests/pending")]
    public async Task<IActionResult> ListPending(CancellationToken ct)
    {
        var me = CurrentUserId;

        var requests = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending && (f.RequesterId == me || f.AddresseeId == me))
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);

        var otherIds = requests.Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId).ToHashSet();
        var users = await _db.Users.AsNoTracking()
            .Where(u => otherIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var dtos = requests.Select(f =>
        {
            var otherId = f.RequesterId == me ? f.AddresseeId : f.RequesterId;
            return new FriendRequestDto(
                Id: f.Id,
                OtherUserId: otherId,
                OtherUsername: users.GetValueOrDefault(otherId, "(silinmiş)"),
                Direction: f.RequesterId == me ? "Outgoing" : "Incoming",
                Status: f.Status.ToString(),
                CreatedAt: f.CreatedAt,
                RespondedAt: f.RespondedAt
            );
        }).ToList();

        return Ok(dtos);
    }

    // POST /api/friends/requests/{id}/accept
    [HttpPost("requests/{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var f = await _db.Friendships.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        if (f.AddresseeId != me) return Forbid();
        if (f.Status != FriendshipStatus.Pending)
            return BadRequest(new { error = "not_pending" });

        f.Status = FriendshipStatus.Accepted;
        f.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // POST /api/friends/requests/{id}/reject
    [HttpPost("requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var f = await _db.Friendships.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        if (f.AddresseeId != me) return Forbid();
        if (f.Status != FriendshipStatus.Pending)
            return BadRequest(new { error = "not_pending" });

        f.Status = FriendshipStatus.Rejected;
        f.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // GET /api/friends — kabul edilen arkadaşlar
    [HttpGet]
    public async Task<IActionResult> ListFriends(CancellationToken ct)
    {
        var me = CurrentUserId;

        var fs = await _db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
            .OrderByDescending(f => f.RespondedAt ?? f.CreatedAt)
            .ToListAsync(ct);

        var otherIds = fs.Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId).ToHashSet();
        var users = await _db.Users.AsNoTracking()
            .Where(u => otherIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.LastSeenAt })
            .ToDictionaryAsync(u => u.Id, u => u, ct);

        var onlineMap = _presence.AreOnline(otherIds);

        var dtos = fs.Select(f =>
        {
            var otherId = f.RequesterId == me ? f.AddresseeId : f.RequesterId;
            var info = users.GetValueOrDefault(otherId);
            return new FriendDto(
                UserId: otherId,
                Username: info?.Username ?? "(silinmiş)",
                FriendsSince: f.RespondedAt ?? f.CreatedAt,
                IsOnline: onlineMap.GetValueOrDefault(otherId, false),
                LastSeenAt: info?.LastSeenAt
            );
        }).ToList();

        return Ok(dtos);
    }

    // GET /api/friends/{userId}/presence — tek kullanıcının güncel durumu
    [HttpGet("{userId:guid}/presence")]
    public async Task<IActionResult> GetPresence(Guid userId, CancellationToken ct)
    {
        var me = CurrentUserId;
        var isFriend = await _db.Friendships.AsNoTracking().AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == userId) ||
             (f.RequesterId == userId && f.AddresseeId == me)), ct);
        if (!isFriend) return Forbid();

        var lastSeen = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.LastSeenAt).FirstOrDefaultAsync(ct);
        return Ok(new PresenceChangedEvent(userId, _presence.IsOnline(userId), lastSeen));
    }

    // DELETE /api/friends/{userId} — arkadaşlığı sonlandır
    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Unfriend(Guid userId, CancellationToken ct)
    {
        var me = CurrentUserId;
        var f = await _db.Friendships.FirstOrDefaultAsync(x =>
            x.Status == FriendshipStatus.Accepted &&
            ((x.RequesterId == me && x.AddresseeId == userId) ||
             (x.RequesterId == userId && x.AddresseeId == me)), ct);
        if (f is null) return NotFound();

        _db.Friendships.Remove(f);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static FriendRequestDto ToDto(Friendship f, string otherUsername, bool isMineSent) =>
        new(
            Id: f.Id,
            OtherUserId: isMineSent ? f.AddresseeId : f.RequesterId,
            OtherUsername: otherUsername,
            Direction: isMineSent ? "Outgoing" : "Incoming",
            Status: f.Status.ToString(),
            CreatedAt: f.CreatedAt,
            RespondedAt: f.RespondedAt
        );
}
