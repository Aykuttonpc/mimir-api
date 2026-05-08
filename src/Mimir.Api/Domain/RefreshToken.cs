namespace Mimir.Api.Domain;

/// <summary>
/// Refresh token persistence — rotation pattern (her refresh'te eski revoke, yeni issue).
/// Plain token client'ta kalır, DB sadece SHA-256 hash tutar.
/// Audit zinciri: ReplacedByTokenId ile rotation track edilir → token reuse detection.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;     // SHA-256 hex
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public string? RevokedReason { get; set; }         // "rotated" | "logout" | "reuse_detected" | "admin_revoke"

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
