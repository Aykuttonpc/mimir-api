using System.ComponentModel.DataAnnotations;

namespace Mimir.Api.Contracts;

// ─────────────────────── /me ─────────────────────

public record MeDto(
    Guid Id,
    string Username,
    string Email,
    string? Phone,
    string Status,
    bool IsAdmin,
    string? FriendKey,
    DateTime CreatedAt
);

public record FriendKeyDto(string FriendKey);

// ─────────────────────── friend requests ─────────

public record SendFriendRequestRequest(
    [Required, StringLength(20, MinimumLength = 6)] string FriendKey
);

public record FriendRequestDto(
    Guid Id,
    Guid OtherUserId,
    string OtherUsername,
    string Direction,        // "Incoming" | "Outgoing"
    string Status,
    DateTime CreatedAt,
    DateTime? RespondedAt
);

public record FriendDto(
    Guid UserId,
    string Username,
    DateTime FriendsSince
);
