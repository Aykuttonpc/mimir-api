namespace Mimir.Api.Domain;

/// <summary>
/// Admin'in (Aykut) ürettiği tek-kullanımlık davet token'ı.
/// Kayıt formuna gelen kullanıcı bu token'ı doğrularsa register endpoint'e geçer.
/// </summary>
public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary> Token'ın hash'i (SHA-256). Ham token DB'ye yazılmaz. </summary>
    public string TokenHash { get; set; } = null!;

    /// <summary> Kullanıcının görünmesi gereken not (admin'in kişiselleştirmesi için). </summary>
    public string? Note { get; set; }

    /// <summary> Daveti üreten admin user. </summary>
    public Guid IssuedByUserId { get; set; }

    /// <summary> Davet kullanıldıysa hangi user'a bağlandı. </summary>
    public Guid? RedeemedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
}
