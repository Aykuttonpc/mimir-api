namespace Mimir.Api.Domain;

/// <summary>
/// Sohbet mesajı (DM veya Group). Sprint #14: Conversation modeline taşındı —
/// `ConversationId` taşıyıcı, broadcast tüm üyelere SignalR group ile yapılır.
/// AES-256-GCM at-rest encrypted (ADR-012). Plain text DB'ye yazılmaz.
/// </summary>
public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary> Sprint #14: DM veya Group — tek kolon. </summary>
    public Guid ConversationId { get; set; }

    public Guid SenderId { get; set; }

    /// <summary> AES-GCM nonce, 12 byte. Her mesaj için yeniden üretilir. </summary>
    public byte[] Iv { get; set; } = null!;

    public byte[] Ciphertext { get; set; } = null!;

    /// <summary> AES-GCM authentication tag, 16 byte. Tampering tespiti. </summary>
    public byte[] Tag { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
