namespace Mimir.Api.Services.Push;

// ADR-017: Signal-only push. İçerik gönderilmez — mobile uyandığında
// kendi backend'inden çeker.
public interface IPushDispatcher
{
    Task SendNewMessageSignalAsync(Guid recipientUserId, Guid senderUserId, CancellationToken ct = default);

    // Sprint #12: incoming voice call — high-priority, app uyanmali (locked screen ringing)
    Task SendIncomingCallSignalAsync(Guid recipientUserId, Guid callerUserId, string callerUsername, CancellationToken ct = default);
}
