using System.ComponentModel.DataAnnotations;

namespace Mimir.Api.Contracts;

public record SendMessageRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Content
);

public record MessageDto(
    Guid Id,
    Guid SenderId,
    Guid RecipientId,
    string Content,
    DateTime CreatedAt,
    DateTime? ReadAt,
    DateTime? EditedAt,
    DateTime? DeletedAt
);

/// <summary>
/// Conversations endpoint özeti — peer kullanıcı + son mesaj preview + unread sayısı.
/// </summary>
public record ConversationDto(
    Guid OtherUserId,
    string OtherUsername,
    string? LastMessageContent,
    DateTime? LastMessageAt,
    bool LastMessageFromMe,
    int UnreadCount
);

public record ActiveUserDto(Guid Id, string Username);

public record MessageReadEvent(Guid MessageId, DateTime ReadAt);

// T-035 — edit / soft delete
public record EditMessageRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Content
);
public record MessageEditedEvent(Guid MessageId, string Content, DateTime EditedAt);
public record MessageDeletedEvent(Guid MessageId, DateTime DeletedAt);

// T-033 — typing
public record TypingEvent(Guid FromUserId, bool IsTyping);

// Sprint #11 — presence (online/offline)
public record PresenceChangedEvent(Guid UserId, bool Online, DateTime? LastSeenAt);

// Sprint #12 — WebRTC voice call signaling (ephemeral, DB'ye yazılmaz)
public record IncomingCallEvent(Guid CallerId, string CallerUsername, string SdpOffer);
public record CallAnsweredEvent(Guid AnswererId, string SdpAnswer);
public record IceCandidateEvent(Guid FromUserId, string Candidate);
public record CallSimpleEvent(Guid FromUserId);   // RejectCall, EndCall, BusyCall ortak shape

// TURN credentials response
public record TurnCredentialsDto(
    List<string> Urls,
    string Username,
    string Credential,
    long ExpiresAt
);
