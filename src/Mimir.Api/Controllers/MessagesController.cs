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
using Mimir.Api.Services.Push;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Controllers;

/// <summary>
/// Sprint #14: Conversation-based message API. DM ve Group aynı endpoint setini paylaşır —
/// authorization "membership" üzerinden gider, friendship gating ConversationsController.Create
/// üzerinde yapılır (mesaj göndermek için ekstra friend check yok; üye misin yeterli).
/// </summary>
[ApiController]
[Route("api/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly MimirDbContext _db;
    private readonly IMessageCrypto _crypto;
    private readonly IHubContext<DmHub> _hub;
    private readonly IConversationService _conversations;
    private readonly IPushDispatcher _push;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        MimirDbContext db,
        IMessageCrypto crypto,
        IHubContext<DmHub> hub,
        IConversationService conversations,
        IPushDispatcher push,
        ILogger<MessagesController> logger)
    {
        _db = db;
        _crypto = crypto;
        _hub = hub;
        _conversations = conversations;
        _push = push;
        _logger = logger;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("user_id_missing");

    // GET /api/messages/{conversationId}?before=...&limit=50
    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> ListMessages(
        Guid conversationId,
        [FromQuery] DateTime? before,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var me = CurrentUserId;
        if (await _conversations.GetActiveMemberAsync(conversationId, me, ct) is null)
            return Forbid();

        var query = _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.DeletedAt == null);

        if (before.HasValue)
            query = query.Where(m => m.CreatedAt < before.Value);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        // Eski → yeni sıra (UI scroll için doğal)
        var dtos = messages
            .Select(m => new MessageDto(
                m.Id, m.ConversationId, m.SenderId,
                _crypto.Decrypt(m.Iv, m.Ciphertext, m.Tag),
                m.CreatedAt, m.EditedAt, m.DeletedAt))
            .Reverse()
            .ToList();

        return Ok(dtos);
    }

    // POST /api/messages/{conversationId}
    [HttpPost("{conversationId:guid}")]
    public async Task<IActionResult> Send(
        Guid conversationId,
        [FromBody] SendMessageRequest req,
        CancellationToken ct)
    {
        var me = CurrentUserId;
        if (await _conversations.GetActiveMemberAsync(conversationId, me, ct) is null)
            return Forbid();

        var cipher = _crypto.Encrypt(req.Content);
        var msg = new Message
        {
            ConversationId = conversationId,
            SenderId = me,
            Iv = cipher.Iv,
            Ciphertext = cipher.Ciphertext,
            Tag = cipher.Tag,
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync(ct);
        await _conversations.TouchActivityAsync(conversationId, ct);

        var dto = new MessageDto(msg.Id, conversationId, me, req.Content,
            msg.CreatedAt, null, null);

        // SignalR broadcast — conv-{id} group'una tüm üye cihazları üye
        await _hub.Clients.Group($"conv-{conversationId}").SendAsync("MessageReceived", dto, ct);

        // FCM signal — offline cihazları uyandır (sender hariç)
        var memberIds = await _conversations.GetMemberIdsAsync(conversationId, ct);
        foreach (var memberId in memberIds.Where(id => id != me))
        {
            await _push.SendNewMessageSignalAsync(memberId, me, conversationId, ct);
        }

        return Created($"/api/messages/{msg.Id}", dto);
    }

    // PATCH /api/messages/{id}
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

        var ev = new MessageEditedEvent(msg.Id, msg.ConversationId, req.Content, msg.EditedAt.Value);
        await _hub.Clients.Group($"conv-{msg.ConversationId}").SendAsync("MessageEdited", ev, ct);

        return NoContent();
    }

    // DELETE /api/messages/{id} — soft delete
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = CurrentUserId;
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null || msg.DeletedAt is not null) return NotFound();
        if (msg.SenderId != me) return Forbid();

        msg.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ev = new MessageDeletedEvent(msg.Id, msg.ConversationId, msg.DeletedAt.Value);
        await _hub.Clients.Group($"conv-{msg.ConversationId}").SendAsync("MessageDeleted", ev, ct);

        return NoContent();
    }
}
