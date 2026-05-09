namespace Mimir.Api.Domain;

/// <summary>
/// ADR-016: Arkadaşlık modeli (gizli key + onay-bazlı). ADR-013 supersede.
/// Tek kayıt patterni: A → B isteği varsa RequesterId=A, AddresseeId=B, Status=Pending.
/// B kabul ederse Status=Accepted. DM gating: çift için Accepted varsa.
/// </summary>
public class Friendship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequesterId { get; set; }
    public Guid AddresseeId { get; set; }
    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
}

public enum FriendshipStatus
{
    Pending,
    Accepted,
    Rejected,
    Blocked,
}
