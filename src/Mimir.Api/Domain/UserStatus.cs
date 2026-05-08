namespace Mimir.Api.Domain;

/// <summary>
/// 4-aşama onboarding gate (ARCHITECTURE.md "Yeni Kullanıcı Onboarding"):
/// PendingEmail → PendingSms → PendingAdmin → Active.
/// Admin manuel onayı sonrası Active.
/// </summary>
public enum UserStatus
{
    PendingEmail,
    PendingSms,
    PendingAdmin,
    Active,
    Suspended
}
