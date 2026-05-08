namespace Mimir.Api.Domain;

/// <summary>
/// 3-aşama onboarding gate (ADR-010: SMS verify kaldırıldı):
/// PendingEmail → PendingAdmin → Active.
/// Admin manuel onayı sonrası Active.
/// </summary>
public enum UserStatus
{
    PendingEmail,
    PendingAdmin,
    Active,
    Suspended
}
