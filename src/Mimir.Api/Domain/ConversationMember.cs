namespace Mimir.Api.Domain;

/// <summary>
/// Conversation üyelik kaydı — kim hangi konuşmaya ne rolde ne zaman katıldı.
/// Composite key: (ConversationId, UserId).
/// LeftAt set olursa soft-leave (history korunur, üye listesi bu satırı gizler).
/// </summary>
public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public ConversationMemberRole Role { get; set; } = ConversationMemberRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary> Bu üyenin okuduğu son mesaj zamanı — unread count sayımı için. </summary>
    public DateTime? LastReadAt { get; set; }

    /// <summary> Soft-leave. Null = aktif üye. </summary>
    public DateTime? LeftAt { get; set; }
}

public enum ConversationMemberRole
{
    Member = 0,
    Admin = 1,
    /// <summary> Conversation'ı kuran kişi. Tek kişi olur. Silinemez. </summary>
    Owner = 2,
}
