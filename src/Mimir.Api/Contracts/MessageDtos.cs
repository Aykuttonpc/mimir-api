using System.ComponentModel.DataAnnotations;

namespace Mimir.Api.Contracts;

// ──────────────────── Messages ────────────────────

public record SendMessageRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Content
);

/// <summary>
/// Sprint #14: Message taşıyıcısı artık ConversationId. SenderId her zaman gönderen.
/// Read receipts ConversationMember.LastReadAt üzerinden gider (ConversationReadEvent).
/// </summary>
public record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string Content,
    DateTime CreatedAt,
    DateTime? EditedAt,
    DateTime? DeletedAt
);

public record EditMessageRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Content
);
public record MessageEditedEvent(Guid MessageId, Guid ConversationId, string Content, DateTime EditedAt);
public record MessageDeletedEvent(Guid MessageId, Guid ConversationId, DateTime DeletedAt);

// ──────────────────── Conversations (Sprint #14) ────────────────────

/// <summary>
/// ChatList satırı. DM: name=null, OtherUserId/OtherUsername doldurulur.
/// Group: name dolu, OtherUser* null, MemberCount geçerli.
/// </summary>
public record ConversationDto(
    Guid Id,
    string Type,                    // "Dm" | "Group"
    string? Name,                   // group adı (DM'de null)
    Guid? OtherUserId,              // DM için "diğer üye"
    string? OtherUsername,
    int MemberCount,
    string? LastMessageContent,
    DateTime? LastMessageAt,
    bool LastMessageFromMe,
    int UnreadCount
);

public record ConversationMemberDto(
    Guid UserId,
    string Username,
    string Role,                    // "Owner" | "Admin" | "Member"
    DateTime JoinedAt,
    DateTime? LastReadAt
);

public record ConversationDetailDto(
    Guid Id,
    string Type,
    string? Name,
    Guid CreatedById,
    DateTime CreatedAt,
    DateTime? LastActivityAt,
    List<ConversationMemberDto> Members
);

public record CreateConversationRequest(
    [Required] string Type,                         // "Dm" | "Group"
    [StringLength(100, MinimumLength = 1)] string? Name,
    [Required, MinLength(1)] List<Guid> MemberIds   // self hariç
);

public record RenameConversationRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name
);

public record AddMemberRequest([Required] Guid UserId);

// Realtime events
public record ConversationReadEvent(Guid ConversationId, Guid UserId, DateTime LastReadAt);
public record ConversationMemberAddedEvent(Guid ConversationId, ConversationMemberDto Member);
public record ConversationMemberRemovedEvent(Guid ConversationId, Guid UserId);
public record ConversationRenamedEvent(Guid ConversationId, string Name);

// Legacy compat (varsa eski client'ta okuma kaldı)
public record ActiveUserDto(Guid Id, string Username);

// ──────────────────── Typing / Presence ────────────────────

public record TypingEvent(Guid ConversationId, Guid FromUserId, bool IsTyping);
public record PresenceChangedEvent(Guid UserId, bool Online, DateTime? LastSeenAt);

// ──────────────────── WebRTC voice call signaling (Sprint #12 — DM only) ─────────

public record IncomingCallEvent(Guid CallerId, string CallerUsername, string SdpOffer);
public record CallAnsweredEvent(Guid AnswererId, string SdpAnswer);
public record IceCandidateEvent(Guid FromUserId, string Candidate);
public record CallSimpleEvent(Guid FromUserId);

public record TurnCredentialsDto(
    List<string> Urls,
    string Username,
    string Credential,
    long ExpiresAt
);
