using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mimir.Api.Contracts;
using Mimir.Api.Data;
using Mimir.Api.Domain;
using Mimir.Api.Services.Conversations;
using Mimir.Api.Services.Presence;
using Mimir.Api.Services.Push;
using Mimir.Api.Services.Security;

namespace Mimir.Api.Hubs;

/// <summary>
/// Sprint #14: Conversation-aware DM Hub. Üye olduğun her conversation için `conv-{id}` SignalR
/// group'una otomatik join. Mesaj broadcast bu group üzerinden tüm üyelere ulaşır.
/// `user-{id}` group'u da korunur — voice call signaling + direct user push için.
/// Voice call DM-only kalır (Sprint #15+ değerlendir).
/// </summary>
[Authorize]
public class DmHub : Hub
{
    private readonly MimirDbContext _db;
    private readonly IMessageCrypto _crypto;
    private readonly IConversationService _convs;
    private readonly IFriendshipChecker _friends;
    private readonly PresenceTracker _presence;
    private readonly IPushDispatcher _push;
    private readonly ILogger<DmHub> _logger;

    public DmHub(
        MimirDbContext db,
        IMessageCrypto crypto,
        IConversationService convs,
        IFriendshipChecker friends,
        PresenceTracker presence,
        IPushDispatcher push,
        ILogger<DmHub> logger)
    {
        _db = db;
        _crypto = crypto;
        _convs = convs;
        _friends = friends;
        _presence = presence;
        _push = push;
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

        // Conversation group'larına otomatik join — istemci tek tek subscribe etmez
        var convIds = await _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.ConversationId)
            .ToListAsync();
        foreach (var convId in convIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"conv-{convId}");
        }

        var transitioned = _presence.TrackConnect(userId);
        _logger.LogInformation("DM Hub connected: user={UserId} conn={ConnId} convs={ConvCount} firstConn={First}",
            userId, Context.ConnectionId, convIds.Count, transitioned);
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
    /// Conversation'a real-time mesaj gönder. Üye olmayan reddedilir.
    /// Server'da encrypt, conv group'una broadcast, offline üyelere FCM.
    /// </summary>
    public async Task SendMessage(string conversationId, string plaintext)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(conversationId, out var convId))
            throw new HubException("invalid_conversation_id");
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new HubException("empty_content");
        if (plaintext.Length > 4000)
            throw new HubException("content_too_long");

        if (await _convs.GetActiveMemberAsync(convId, me) is null)
            throw new HubException("not_member");

        var cipher = _crypto.Encrypt(plaintext);
        var msg = new Message
        {
            ConversationId = convId,
            SenderId = me,
            Iv = cipher.Iv,
            Ciphertext = cipher.Ciphertext,
            Tag = cipher.Tag,
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        await _convs.TouchActivityAsync(convId);

        var dto = new MessageDto(msg.Id, convId, me, plaintext, msg.CreatedAt, null, null);
        await Clients.Group($"conv-{convId}").SendAsync("MessageReceived", dto);

        var memberIds = await _convs.GetMemberIdsAsync(convId);
        foreach (var memberId in memberIds.Where(id => id != me))
        {
            await _push.SendNewMessageSignalAsync(memberId, me, convId);
        }
    }

    /// <summary>
    /// Conversation'ı şu ana kadar okunmuş işaretle. LastReadAt user'a özgü.
    /// </summary>
    public async Task MarkConversationRead(string conversationId)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(conversationId, out var convId))
            throw new HubException("invalid_conversation_id");

        var member = await _convs.GetActiveMemberAsync(convId, me);
        if (member is null) throw new HubException("not_member");

        var now = DateTime.UtcNow;
        member.LastReadAt = now;
        await _db.SaveChangesAsync();

        await Clients.Group($"conv-{convId}")
            .SendAsync("ConversationRead", new ConversationReadEvent(convId, me, now));
    }

    public async Task Typing(string conversationId, bool isTyping)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(conversationId, out var convId)) return;
        if (await _convs.GetActiveMemberAsync(convId, me) is null) return;

        await Clients.GroupExcept($"conv-{convId}", Context.ConnectionId)
            .SendAsync("Typing", new TypingEvent(convId, me, isTyping));
    }

    // ──────────────── WebRTC voice call signaling (Sprint #12 — DM only) ────────────────
    // Ephemeral — DB'ye HİÇBİR şey yazılmaz. Pure SignalR pass-through.
    // Friendship gating (ADR-016): sadece arkadaşlar birbirini arayabilir.

    public async Task OfferCall(string toUserId, string sdpOffer)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(toUserId, out var toGuid)) throw new HubException("invalid_user_id");
        if (me == toGuid) throw new HubException("cannot_call_self");
        if (!await _friends.AreAcceptedAsync(me, toGuid)) throw new HubException("not_friends");

        var callerUsername = await _db.Users.AsNoTracking()
            .Where(u => u.Id == me).Select(u => u.Username).FirstOrDefaultAsync() ?? "";

        await Clients.Group($"user-{toGuid}")
            .SendAsync("IncomingCall", new IncomingCallEvent(me, callerUsername, sdpOffer ?? ""));

        // App kapalıysa SignalR group'a üye değil — FCM ile cihazı uyandır + SDP payload'da.
        await _push.SendIncomingCallSignalAsync(toGuid, me, callerUsername, sdpOffer ?? "");
    }

    public async Task AnswerCall(string toUserId, string sdpAnswer)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(toUserId, out var toGuid)) throw new HubException("invalid_user_id");
        await Clients.Group($"user-{toGuid}")
            .SendAsync("CallAnswered", new CallAnsweredEvent(me, sdpAnswer ?? ""));
    }

    public async Task SendIceCandidate(string toUserId, string candidate)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(toUserId, out var toGuid)) return;
        await Clients.Group($"user-{toGuid}")
            .SendAsync("IceCandidate", new IceCandidateEvent(me, candidate ?? ""));
    }

    public async Task RejectCall(string toUserId)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(toUserId, out var toGuid)) return;
        await Clients.Group($"user-{toGuid}")
            .SendAsync("CallRejected", new CallSimpleEvent(me));
    }

    public async Task EndCall(string toUserId)
    {
        var me = CurrentUserId;
        if (!Guid.TryParse(toUserId, out var toGuid)) return;
        await Clients.Group($"user-{toGuid}")
            .SendAsync("CallEnded", new CallSimpleEvent(me));
    }
}
