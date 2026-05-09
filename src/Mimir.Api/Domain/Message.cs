namespace Mimir.Api.Domain;

/// <summary>
/// 1-1 DM mesajı. Server-side AES-256-GCM encrypted at-rest (ADR-012).
/// Plain text DB'ye yazılmaz — sadece IV + Ciphertext + Tag.
/// Decrypt = sadece read sırasında server'da; client'a plaintext döner.
/// </summary>
public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }

    /// <summary> AES-GCM nonce, 12 byte. Her mesaj için yeniden üretilir. </summary>
    public byte[] Iv { get; set; } = null!;

    public byte[] Ciphertext { get; set; } = null!;

    /// <summary> AES-GCM authentication tag, 16 byte. Tampering tespiti. </summary>
    public byte[] Tag { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
