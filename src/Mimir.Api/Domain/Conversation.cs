namespace Mimir.Api.Domain;

/// <summary>
/// Sprint #14: Unified conversation entity — hem 1-1 DM hem grup sohbeti.
/// DM: Type=Dm, Name=null (UI "diğer üye" adını gösterir), tam 2 ConversationMember.
/// Group: Type=Group, Name zorunlu, 1+ ConversationMember.
/// Baseline: GetStream/stream-chat-android `messaging` channel pattern (ADR-022).
/// </summary>
public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ConversationType Type { get; set; } = ConversationType.Dm;

    /// <summary> DM'de null; grup için 1-100 char. </summary>
    public string? Name { get; set; }

    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary> Son mesaj veya üye değişikliği zamanı — ChatList sıralaması için. </summary>
    public DateTime? LastActivityAt { get; set; }
}

public enum ConversationType
{
    Dm = 0,
    Group = 1,
}
