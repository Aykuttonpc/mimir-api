namespace Mimir.Api.Domain;

/// <summary>
/// Email verification kodları için. SMS yok (ADR-010).
/// Hash olarak saklanır (ham kod kullanıcıya bir kez gösterilir, DB'de yok).
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary> SHA-256 hash. </summary>
    public string CodeHash { get; set; } = null!;

    public int AttemptCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
