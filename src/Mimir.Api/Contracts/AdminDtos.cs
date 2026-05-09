using System.ComponentModel.DataAnnotations;

namespace Mimir.Api.Contracts;

public record InvitationCreateRequest(
    [StringLength(255)] string? Note,
    [Range(1, 30)] int? ExpiryDays
);

/// <summary>
/// Token plain text sadece bu yanıtta döner. Bir daha gösterilmez (DB sadece hash tutar).
/// Admin'in token'ı kopyalayıp davet edilenle paylaşması beklenir.
/// </summary>
public record InvitationCreateResponse(
    Guid Id,
    string Token,
    DateTime ExpiresAt
);

public record PendingUserDto(
    Guid Id,
    string Username,
    string Email,
    string? Phone,
    DateTime CreatedAt
);

public record ApprovalDecisionRequest(
    [Required, RegularExpression("^(approve|reject|suspend)$")] string Decision,
    [StringLength(500)] string? Reason
);

/// <summary>
/// Davet liste dönüşü (T-040). Token PLAIN TEXT DÖNMEZ — sadece hash DB'de.
/// Kayıp token = revoke + yeni üret.
/// </summary>
public record InvitationSummaryDto(
    Guid Id,
    string? Note,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RedeemedAt,
    string? RedeemedByUsername,
    string Status   // "Active" | "Used" | "Expired"
);
