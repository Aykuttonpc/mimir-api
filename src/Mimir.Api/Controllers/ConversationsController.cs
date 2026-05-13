using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Hubs;
using Mimir.Api.Services.Conversations;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

/// <summary>
/// Sprint #14: Conversation yönetimi (create, list, detail, rename, member ops, read).
/// Friendship gating (ADR-016) sadece create üzerinde uygulanır — üye olduğun konuşmada
/// her şey serbest. Group oluştururken eklediğin her kullanıcı arkadaşın olmalı.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly IMessageCrypto _crypto;
    private readonly IConversationService _convs;
    private readonly IFriendshipChecker _friends;
    private readonly IHubContext<DmHub> _hub;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        MimirDbContext db,
        IMessageCrypto crypto,
        IConversationService convs,
        IFriendshipChecker friends,
        IHubContext<DmHub> hub,
        ILogger<ConversationsController> logger)
    {
        _db = db;
        _crypto = crypto;
        _convs = convs;
        _friends = friends;
        _hub = hub;
        _logger = logger;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("user_id_missing");

    // GET /api/conversations — kullanıcının aktif konuşmaları
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = CurrentUserId;

        var myMemberships = await _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == me && m.LeftAt == null)
            .Select(m => new { m.ConversationId, m.LastReadAt })
            .ToListAsync(ct);

        if (myMemberships.Count == 0) return Ok(Array.Empty<ConversationDto>());

        var convIds = myMemberships.Select(x => x.ConversationId).ToList();
        var convs = await _db.Conversations
            .AsNoTracking()
            .Where(c => convIds.Contains(c.Id))
            .ToListAsync(ct);

        // DM "other user" lookup için 2-üyeli conv'larda diğer üyeyi bulmamız lazım
        var allMembers = await _db.ConversationMembers
            .AsNoTracking()
            .Where(m => convIds.Contains(m.ConversationId) && m.LeftAt == null)
            .ToListAsync(ct);

        var allUserIds = allMembers.Select(m => m.UserId).Distinct().ToHashSet();
        var users = await _db.Users
            .AsNoTracking()
            .Where(u => allUserIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        // Her conv için son non-deleted mesajı al — toplu fetch, sonra group-by son
        var lastMessages = await _db.Messages
            .AsNoTracking()
            .Where(m => convIds.Contains(m.ConversationId) && m.DeletedAt == null)
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .ToListAsync(ct);
        var lastByConv = lastMessages.ToDictionary(m => m.ConversationId);

        var result = new List<ConversationDto>(convs.Count);
        foreach (var c in convs)
        {
            var myMembership = myMemberships.First(x => x.ConversationId == c.Id);
            var membersOfThis = allMembers.Where(m => m.ConversationId == c.Id).ToList();

            Guid? otherUserId = null;
            string? otherUsername = null;
            if (c.Type == ConversationType.Dm)
            {
                var other = membersOfThis.FirstOrDefault(m => m.UserId != me);
                if (other is not null)
                {
                    otherUserId = other.UserId;
                    otherUsername = users.GetValueOrDefault(other.UserId, "(silinmiş)");
                }
            }

            lastByConv.TryGetValue(c.Id, out var last);

            int unread = 0;
            if (myMembership.LastReadAt is { } lastRead)
            {
                unread = await _db.Messages
                    .AsNoTracking()
                    .CountAsync(m => m.ConversationId == c.Id
                                     && m.DeletedAt == null
                                     && m.SenderId != me
                                     && m.CreatedAt > lastRead, ct);
            }
            else
            {
                unread = await _db.Messages
                    .AsNoTracking()
                    .CountAsync(m => m.ConversationId == c.Id
                                     && m.DeletedAt == null
                                     && m.SenderId != me, ct);
            }

            result.Add(new ConversationDto(
                Id: c.Id,
                Type: c.Type.ToString(),
                Name: c.Name,
                OtherUserId: otherUserId,
                OtherUsername: otherUsername,
                MemberCount: membersOfThis.Count,
                LastMessageContent: last is null ? null : TryDecryptPreview(last),
                LastMessageAt: last?.CreatedAt,
                LastMessageFromMe: last?.SenderId == me,
                UnreadCount: unread
            ));
        }

        return Ok(result
            .OrderByDescending(x => x.LastMessageAt ?? DateTime.MinValue)
            .ToList());
    }

    // GET /api/conversations/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        if (await _convs.GetActiveMemberAsync(id, me, ct) is null) return Forbid();

        var conv = await _db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conv is null) return NotFound();

        var members = await _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == id && m.LeftAt == null)
            .ToListAsync(ct);
        var memberIds = members.Select(m => m.UserId).ToList();
        var usernames = await _db.Users
            .AsNoTracking()
            .Where(u => memberIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var memberDtos = members
            .Select(m => new ConversationMemberDto(
                UserId: m.UserId,
                Username: usernames.GetValueOrDefault(m.UserId, "(silinmiş)"),
                Role: m.Role.ToString(),
                JoinedAt: m.JoinedAt,
                LastReadAt: m.LastReadAt
            ))
            .ToList();

        return Ok(new ConversationDetailDto(
            Id: conv.Id,
            Type: conv.Type.ToString(),
            Name: conv.Name,
            CreatedById: conv.CreatedById,
            CreatedAt: conv.CreatedAt,
            LastActivityAt: conv.LastActivityAt,
            Members: memberDtos
        ));
    }

    // POST /api/conversations — DM (idempotent) veya Group
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest req, CancellationToken ct)
    {
        var me = CurrentUserId;
        if (!Enum.TryParse<ConversationType>(req.Type, ignoreCase: true, out var type))
            return BadRequest(new { error = "invalid_type" });

        var distinctMembers = req.MemberIds.Distinct().Where(id => id != me).ToList();
        if (distinctMembers.Count == 0) return BadRequest(new { error = "no_members" });

        // Tüm üyeler arkadaş olmalı (kapalı ağ — ADR-016)
        foreach (var memberId in distinctMembers)
        {
            if (!await _friends.AreAcceptedAsync(me, memberId, ct))
                return BadRequest(new { error = "not_friends", userId = memberId });
        }

        if (type == ConversationType.Dm)
        {
            if (distinctMembers.Count != 1)
                return BadRequest(new { error = "dm_requires_exactly_one_other" });

            // Idempotent: aynı 2 user için zaten DM varsa onu döndür
            var existing = await _convs.FindDmAsync(me, distinctMembers[0], ct);
            if (existing is not null)
            {
                return Ok(new { id = existing.Id, type = existing.Type.ToString() });
            }
        }
        else // Group
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { error = "group_requires_name" });
            if (distinctMembers.Count + 1 > 50)
                return BadRequest(new { error = "group_too_large", max = 50 });
        }

        var conv = new Conversation
        {
            Type = type,
            Name = type == ConversationType.Group ? req.Name!.Trim() : null,
            CreatedById = me,
            LastActivityAt = DateTime.UtcNow,
        };
        _db.Conversations.Add(conv);

        // Members — me Owner, diğerleri Member
        _db.ConversationMembers.Add(new ConversationMember
        {
            ConversationId = conv.Id,
            UserId = me,
            Role = ConversationMemberRole.Owner,
        });
        foreach (var memberId in distinctMembers)
        {
            _db.ConversationMembers.Add(new ConversationMember
            {
                ConversationId = conv.Id,
                UserId = memberId,
                Role = ConversationMemberRole.Member,
            });
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Conversation created: {ConvId} type={Type} byUser={UserId}",
            conv.Id, conv.Type, me);

        return CreatedAtAction(nameof(Detail), new { id = conv.Id },
            new { id = conv.Id, type = conv.Type.ToString() });
    }

    // PATCH /api/conversations/{id} — rename (group only, Admin+)
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Rename(
        Guid id,
        [FromBody] RenameConversationRequest req,
        CancellationToken ct)
    {
        var me = CurrentUserId;
        var member = await _convs.GetActiveMemberAsync(id, me, ct);
        if (member is null) return Forbid();
        if (member.Role == ConversationMemberRole.Member)
            return Forbid();

        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conv is null) return NotFound();
        if (conv.Type != ConversationType.Group)
            return BadRequest(new { error = "dm_cannot_be_renamed" });

        conv.Name = req.Name.Trim();
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group($"conv-{id}")
            .SendAsync("ConversationRenamed", new ConversationRenamedEvent(id, conv.Name), ct);
        return NoContent();
    }

    // POST /api/conversations/{id}/members — add (group only, Admin+)
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(
        Guid id,
        [FromBody] AddMemberRequest req,
        CancellationToken ct)
    {
        var me = CurrentUserId;
        var actor = await _convs.GetActiveMemberAsync(id, me, ct);
        if (actor is null) return Forbid();
        if (actor.Role == ConversationMemberRole.Member)
            return Forbid();

        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conv is null) return NotFound();
        if (conv.Type != ConversationType.Group)
            return BadRequest(new { error = "dm_cannot_be_modified" });

        if (req.UserId == me)
            return BadRequest(new { error = "already_member" });

        // Arkadaşlık zorunlu (ADR-016)
        if (!await _friends.AreAcceptedAsync(me, req.UserId, ct))
            return BadRequest(new { error = "not_friends" });

        // Hedef kullanıcı zaten aktif üye mi?
        var existing = await _db.ConversationMembers
            .FirstOrDefaultAsync(m => m.ConversationId == id && m.UserId == req.UserId, ct);

        if (existing is not null)
        {
            if (existing.LeftAt is null) return Conflict(new { error = "already_member" });
            // Re-join — soft-leave'i geri al
            existing.LeftAt = null;
            existing.JoinedAt = DateTime.UtcNow;
            existing.Role = ConversationMemberRole.Member;
        }
        else
        {
            _db.ConversationMembers.Add(new ConversationMember
            {
                ConversationId = id,
                UserId = req.UserId,
                Role = ConversationMemberRole.Member,
            });
        }

        // Member sayısı limiti
        var activeCount = await _db.ConversationMembers
            .CountAsync(m => m.ConversationId == id && m.LeftAt == null, ct);
        if (activeCount + (existing is null ? 1 : 0) > 50)
            return BadRequest(new { error = "group_full", max = 50 });

        await _db.SaveChangesAsync(ct);

        var username = await _db.Users.AsNoTracking()
            .Where(u => u.Id == req.UserId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct) ?? "(silinmiş)";

        var memberDto = new ConversationMemberDto(
            req.UserId, username, ConversationMemberRole.Member.ToString(),
            DateTime.UtcNow, null);
        await _hub.Clients.Group($"conv-{id}")
            .SendAsync("ConversationMemberAdded", new ConversationMemberAddedEvent(id, memberDto), ct);

        return NoContent();
    }

    // DELETE /api/conversations/{id}/members/{userId} — remove (Admin) veya self-leave
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var me = CurrentUserId;
        var actor = await _convs.GetActiveMemberAsync(id, me, ct);
        if (actor is null) return Forbid();

        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conv is null) return NotFound();
        if (conv.Type != ConversationType.Group)
            return BadRequest(new { error = "dm_cannot_be_modified" });

        var isSelf = userId == me;
        if (!isSelf && actor.Role == ConversationMemberRole.Member)
            return Forbid();

        var target = await _db.ConversationMembers
            .FirstOrDefaultAsync(m => m.ConversationId == id && m.UserId == userId && m.LeftAt == null, ct);
        if (target is null) return NotFound();

        // Owner kendini silemez — önce owner devretmeli (out of scope bu sprintte)
        if (target.Role == ConversationMemberRole.Owner)
            return BadRequest(new { error = "owner_cannot_leave_or_be_removed" });

        target.LeftAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group($"conv-{id}")
            .SendAsync("ConversationMemberRemoved", new ConversationMemberRemovedEvent(id, userId), ct);
        return NoContent();
    }

    // POST /api/conversations/{id}/read — mark all read up to now
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var member = await _convs.GetActiveMemberAsync(id, me, ct);
        if (member is null) return Forbid();

        var now = DateTime.UtcNow;
        member.LastReadAt = now;
        await _db.SaveChangesAsync(ct);

        // DM'de okunma karşı tarafa relevant (group'ta da broadcast — UI dilerse gösterir)
        await _hub.Clients.Group($"conv-{id}")
            .SendAsync("ConversationRead", new ConversationReadEvent(id, me, now), ct);
        return NoContent();
    }

    // ────── helpers ──────

    private string? TryDecryptPreview(Message m)
    {
        try
        {
            var plain = _crypto.Decrypt(m.Iv, m.Ciphertext, m.Tag);
            return plain.Length > 80 ? plain[..80] + "…" : plain;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decrypt fail for message {MessageId}", m.Id);
            return "(deşifre hatası)";
        }
    }
}
