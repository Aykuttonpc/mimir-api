namespace Mimir.Api.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    /// <summary> Opsiyonel — admin'in tanıdığını eşleştirme/bilgi için. Verify edilmez (ADR-010). </summary>
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = null!;
    public UserStatus Status { get; set; } = UserStatus.PendingEmail;
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
