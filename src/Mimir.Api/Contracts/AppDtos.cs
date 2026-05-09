namespace Mimir.Api.Contracts;

/// <summary>
/// App version info — public endpoint. T-039: force-update mekanizması.
/// Mobile her açılışta çağırır; client < minSupportedVersion ise blocking screen.
/// </summary>
public record AppVersionInfoDto(
    string MinSupportedVersion,
    string LatestVersion,
    string DownloadUrl,
    string Platform
);
