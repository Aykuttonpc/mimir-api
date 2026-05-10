using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Services.Presence;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Hubs;

/// <summary>
/// 1-1 DM Hub — JWT'li kullanıcılar. Server-side AES-GCM encryption (ADR-012).
/// Online presence: connection sırasında "user-{id}" group'a join.
/// Same user multiple device → tüm bağlantılar aynı group'ta = mesaj broadcast.
/// </summary>
[Authorize]
public class DmHub : Hub
{
    private readonly MimirDbContext _db;
    private readonly IMessageCrypto _crypto;
    private readonly IFriendshipChecker _friends;
    private readonly PresenceTracker _presence;
    private readonly ILogger<DmHub> _logger;

    public DmHub(MimirDbContext db, IMessageCrypto crypto, IFriendshipChecker friends,
                 PresenceTracker presence, ILogger<DmHub> logger)
    {
        _db = db;
        _crypto = crypto;
        _friends = friends;
        _presence = presence;
        _logger = logger;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(
            Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub"),
            out var id)
            ? id
            : throw new HubException("user_id_missing");

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

        // Presence — bu user offline iken mi geldi? Evet → arkadaşlara broadcast
        var transitioned = _presence.TrackConnect(userId);
        _logger.LogInformation("DM Hub connected: user={UserId} conn={ConnId} firstConn={First}",
            userId, Context.ConnectionId, transitioned);
        if (transitioned)
        {
            await BroadcastPresenceToFriendsAsync(userId, online: true, lastSeenAt: null);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user-{userId}");

        // Presence — bu user'ın son connection'ı mı? Evet → LastSeenAt update + broadcast
        var transitioned = _presence.TrackDisconnect(userId);
        if (transitioned)
        {
            var now = DateTime.UtcNow;
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now));
            await BroadcastPresenceToFriendsAsync(userId, online: false, lastSeenAt: now);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastPresenceToFriendsAsync(Guid userId, bool online, DateTime? lastSeenAt)
    {
        // Sadece arkadaşlara presence yayını — ADR-016 kapalı ağ
        var friendIds = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();
        if (friendIds.Count == 0) return;

        var ev = new PresenceChangedEvent(userId, online, lastSeenAt);
        foreach (var fId in friendIds)
        {
            await Clients.Group($"user-{fId}").SendAsync("PresenceChanged", ev);
        }
    }

    /// <summary>
    /// Real-time gönderim. Plain text taşınır (TLS), server'da encrypt edilir.
    /// Recipient'ın açık tüm cihazlarına ve sender'ın diğer cihazlarına push.
    /// </summary>
    public async Task SendMessage(Guid toUserId, string plaintext)
    {
        var me = CurrentUserId;
        if (me == toUserId)
            throw new HubException("cannot_send_to_self");
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new HubException("empty_content");
        if (plaintext.Length > 4000)
            throw new HubException("content_too_long");

        var recipient = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == toUserId && u.Status == UserStatus.Active);
        if (recipient is null)
            throw new HubException("recipient_not_found_or_inactive");

        // ADR-016: arkadaş kontrolü
        if (!await _friends.AreAcceptedAsync(me, toUserId))
            throw new HubException("not_friends");

        var cipher = _crypto.Encrypt(plaintext);
        var msg = new Message
        {
            SenderId = me,
            RecipientId = toUserId,
            Iv = cipher.Iv,
            Ciphertext = cipher.Ciphertext,
            Tag = cipher.Tag,
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();

        var dto = new MessageDto(msg.Id, msg.SenderId, msg.RecipientId, plaintext,
            msg.CreatedAt, null, null, null);

        await Clients.Group($"user-{toUserId}").SendAsync("MessageReceived", dto);
        await Clients.Group($"user-{me}").SendAsync("MessageSent", dto);
    }

    public async Task MarkAsRead(Guid messageId)
    {
        var me = CurrentUserId;
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.RecipientId == me && m.DeletedAt == null);
        if (msg is null)
            throw new HubException("message_not_found");
        if (msg.ReadAt is not null) return;

        msg.ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await Clients.Group($"user-{msg.SenderId}")
            .SendAsync("MessageRead", new MessageReadEvent(msg.Id, msg.ReadAt.Value));
    }

    /// <summary>
    /// Typing indicator — DB'ye yazılmaz, sadece broadcast (T-033).
    /// </summary>
    public async Task Typing(Guid toUserId, bool isTyping)
    {
        var me = CurrentUserId;
        await Clients.Group($"user-{toUserId}").SendAsync("Typing", new TypingEvent(me, isTyping));
    }
}
