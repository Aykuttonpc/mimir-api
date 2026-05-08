namespace Mimir.Api.Domain;

/// <summary>
/// 4-aşama gate'in son adımı: admin (Aykut) yeni kullanıcının başvurusunu onayladığında log.
/// Audit trail + admin paneli görüntüleme için.
/// </summary>
public class AdminApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary> Onaylayan admin (genelde Aykut, ama gelecekte ek admin olabilir). </summary>
    public Guid ApprovedByUserId { get; set; }

    public ApprovalDecision Decision { get; set; }

    /// <summary> Reject veya Suspend için sebep. </summary>
    public string? Reason { get; set; }

    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}

public enum ApprovalDecision
{
    Approved,
    Rejected,
    Suspended
}
