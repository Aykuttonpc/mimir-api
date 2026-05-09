namespace Mimir.Api.Domain;

// ADR-017: FCM signal-only push. Mobile cihaz login sonrası kendi token'ını
// kaydeder, logout'ta siler. Aynı kullanıcı birden fazla cihaza sahip olabilir.
public class UserDeviceToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string FcmToken { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public enum DevicePlatform
{
    Android,
    Ios,
}
