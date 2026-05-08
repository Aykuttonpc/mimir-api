namespace Mimir.Api.Domain;

/// <summary>
/// Email verification ve SMS OTP code'ları için.
/// Hash olarak saklanır (ham kod kullanıcıya bir kez gösterilir, DB'de yok).
/// 4-aşama gate'in 2. ve 3. step'leri için kullanılır.
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public OtpType Type { get; set; }

    /// <summary> SHA-256 hash. </summary>
    public string CodeHash { get; set; } = null!;

    public int AttemptCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public enum OtpType
{
    Email,
    Sms
}
