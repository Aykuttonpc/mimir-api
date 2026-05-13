namespace Mimir.Api.Services.Push;

// ADR-017: Signal-only push. İçerik gönderilmez — mobile uyandığında
// kendi backend'inden çeker.
public interface IPushDispatcher
{
    /// <summary>
    /// Sprint #14: Conversation-aware. recipientUserId = bildirimi alacak kişi.
    /// senderUserId = gönderen. conversationId = ChatScreen'i hangi konv için açacağımız.
    /// </summary>
    Task SendNewMessageSignalAsync(Guid recipientUserId, Guid senderUserId, Guid conversationId, CancellationToken ct = default);

    // Sprint #12+: incoming voice call. SDP offer payload'a girer — app uyandığında
    // SignalR'a bağlanmayı beklemeden CallManager state Incoming'e geçer.
    Task SendIncomingCallSignalAsync(Guid recipientUserId, Guid callerUserId, string callerUsername, string sdpOffer, CancellationToken ct = default);
}
