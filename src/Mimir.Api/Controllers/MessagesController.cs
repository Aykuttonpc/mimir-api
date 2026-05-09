using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Hubs;
using Mimir.Api.Services.Push;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

[ApiController]
[Route("api/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly IMessageCrypto _crypto;
    private readonly IHubContext<DmHub> _hub;
    private readonly IFriendshipChecker _friends;
    private readonly IPushDispatcher _push;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        MimirDbContext db,
        IMessageCrypto crypto,
        IHubContext<DmHub> hub,
        IFriendshipChecker friends,
        IPushDispatcher push,
        ILogger<MessagesController> logger)
    {
        _db = db;
        _crypto = crypto;
        _hub = hub;
        _friends = friends;
        _push = push;
        _logger = logger;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("user_id_missing");

    // GET /api/messages/conversations — sadece arkadaş olduğum çiftler (ADR-016)
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken ct)
    {
        var me = CurrentUserId;

        // Önce arkadaşlarımı al
        var friendIds = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
            .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
            .ToListAsync(ct);

        if (friendIds.Count == 0) return Ok(Array.Empty<ConversationDto>());

        var friendIdsSet = friendIds.ToHashSet();

        // Sadece arkadaş çiftleriyle olan mesajlar
        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => (m.SenderId == me || m.RecipientId == me) && m.DeletedAt == null
                && (friendIdsSet.Contains(m.SenderId) || friendIdsSet.Contains(m.RecipientId)))
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        var grouped = messages
            .GroupBy(m => m.SenderId == me ? m.RecipientId : m.SenderId)
            .Where(g => friendIdsSet.Contains(g.Key))
            .Select(g => g.First())
            .ToList();

        var peerIds = grouped
            .Select(m => m.SenderId == me ? m.RecipientId : m.SenderId)
            .ToHashSet();

        var users = await _db.Users
            .AsNoTracking()
            .Where(u => peerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var unread = await _db.Messages
            .AsNoTracking()
            .Where(m => m.RecipientId == me && m.ReadAt == null && m.DeletedAt == null)
            .GroupBy(m => m.SenderId)
            .Select(g => new { SenderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SenderId, x => x.Count, ct);

        var result = grouped.Select(m =>
        {
            var peerId = m.SenderId == me ? m.RecipientId : m.SenderId;
            return new ConversationDto(
                OtherUserId: peerId,
                OtherUsername: users.GetValueOrDefault(peerId, "(silinmiş)"),
                LastMessageContent: TryDecryptPreview(m),
                LastMessageAt: m.CreatedAt,
                LastMessageFromMe: m.SenderId == me,
                UnreadCount: unread.GetValueOrDefault(peerId, 0)
            );
        })
        .OrderByDescending(c => c.LastMessageAt)
        .ToList();

        return Ok(result);
    }

    // GET /api/messages/with/{userId}?before=...&limit=50
    [HttpGet("with/{userId:guid}")]
    public async Task<IActionResult> WithUser(
        Guid userId,
        [FromQuery] DateTime? before,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var me = CurrentUserId;
        if (me == userId) return BadRequest(new { error = "cannot_query_self" });

        // ADR-016: arkadaş kontrolü
        if (!await _friends.AreAcceptedAsync(me, userId, ct))
            return Forbid();

        var query = _db.Messages
            .AsNoTracking()
            .Where(m => ((m.SenderId == me && m.RecipientId == userId) ||
                         (m.SenderId == userId && m.RecipientId == me))
                        && m.DeletedAt == null);

        if (before.HasValue)
            query = query.Where(m => m.CreatedAt < before.Value);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        // Reverse → chronological order (eski → yeni) — UI için doğal sıra
        var dtos = messages
            .Select(m => new MessageDto(
                m.Id, m.SenderId, m.RecipientId,
                _crypto.Decrypt(m.Iv, m.Ciphertext, m.Tag),
                m.CreatedAt, m.ReadAt, m.EditedAt, m.DeletedAt))
            .Reverse()
            .ToList();

        return Ok(dtos);
    }

    // POST /api/messages/with/{userId}
    [HttpPost("with/{userId:guid}")]
    public async Task<IActionResult> Send(
        Guid userId,
        [FromBody] SendMessageRequest req,
        CancellationToken ct)
    {
        var me = CurrentUserId;
        if (me == userId)
            return BadRequest(new { error = "cannot_send_to_self" });

        var recipient = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);
        if (recipient is null)
            return NotFound(new { error = "recipient_not_found_or_inactive" });

        // ADR-016: arkadaş kontrolü
        if (!await _friends.AreAcceptedAsync(me, userId, ct))
            return Forbid();

        var cipher = _crypto.Encrypt(req.Content);
        var msg = new Message
        {
            SenderId = me,
            RecipientId = userId,
            Iv = cipher.Iv,
            Ciphertext = cipher.Ciphertext,
            Tag = cipher.Tag,
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync(ct);

        var dto = new MessageDto(msg.Id, msg.SenderId, msg.RecipientId, req.Content,
            msg.CreatedAt, null, null, null);

        // SignalR push — gerek recipient'a, gerek sender'ın diğer cihazlarına
        await _hub.Clients.Group($"user-{userId}").SendAsync("MessageReceived", dto, ct);
        await _hub.Clients.Group($"user-{me}").SendAsync("MessageSent", dto, ct);

        // ADR-017: FCM signal-only — uygulama kapalıysa cihaz bu sinyalle uyanır.
        // Hub.Clients.Group offline cihazlara gitmez; FCM o boşluğu kapatır.
        await _push.SendNewMessageSignalAsync(userId, me, ct);

        return Created($"/api/messages/{msg.Id}", dto);
    }

    // PATCH /api/messages/{id} — T-035: sadece sender düzenleyebilir
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, [FromBody] EditMessageRequest req, CancellationToken ct)
    {
        var me = CurrentUserId;
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null || msg.DeletedAt is not null) return NotFound();
        if (msg.SenderId != me) return Forbid();

        var cipher = _crypto.Encrypt(req.Content);
        msg.Iv = cipher.Iv;
        msg.Ciphertext = cipher.Ciphertext;
        msg.Tag = cipher.Tag;
        msg.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ev = new MessageEditedEvent(msg.Id, req.Content, msg.EditedAt.Value);
        await _hub.Clients.Group($"user-{msg.RecipientId}").SendAsync("MessageEdited", ev, ct);
        await _hub.Clients.Group($"user-{me}").SendAsync("MessageEdited", ev, ct);

        return NoContent();
    }

    // DELETE /api/messages/{id} — T-035: soft delete (DeletedAt set), sadece sender
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null || msg.DeletedAt is not null) return NotFound();
        if (msg.SenderId != me) return Forbid();

        msg.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ev = new MessageDeletedEvent(msg.Id, msg.DeletedAt.Value);
        await _hub.Clients.Group($"user-{msg.RecipientId}").SendAsync("MessageDeleted", ev, ct);
        await _hub.Clients.Group($"user-{me}").SendAsync("MessageDeleted", ev, ct);

        return NoContent();
    }

    // POST /api/messages/{id}/read
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == id && m.RecipientId == me && m.DeletedAt == null, ct);
        if (msg is null) return NotFound();
        if (msg.ReadAt is not null) return NoContent();

        msg.ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group($"user-{msg.SenderId}")
            .SendAsync("MessageRead", new MessageReadEvent(msg.Id, msg.ReadAt.Value), ct);

        return NoContent();
    }

    // ──────────────────── Helpers ────────────────────

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
