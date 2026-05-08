using System.ComponentModel.DataAnnotations;

namespace Mimir.Api.Contracts;

public record RegisterRequest(
    [Required, StringLength(128)] string InvitationToken,
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(50, MinimumLength = 3), RegularExpression("^[a-zA-Z0-9_]+$")] string Username,
    [Required, StringLength(72, MinimumLength = 8)] string Password,
    [StringLength(20)] string? Phone
);

public record RegisterResponse(Guid UserId, string Status, string Message);

public record LoginRequest(
    [Required] string UsernameOrEmail,
    [Required] string Password
);

public record AuthResponse(
    string AccessToken,
    DateTime AccessExpiresAt,
    string RefreshToken,
    DateTime RefreshExpiresAt,
    string Username,
    bool IsAdmin
);

public record RefreshRequest([Required] string RefreshToken);

public record VerifyEmailResponse(string Status, string Message);
