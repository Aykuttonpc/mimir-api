using System.Security.Cryptography;
using System.Text;

namespace Mimir.Api.Services.Security;

/// <summary>
/// Davet, refresh token ve email-verify token üretimi + hash hesaplama.
/// Plain token sadece kullanıcıya 1 kez döner; DB hep hash saklar.
/// </summary>
public class TokenGenerator
{
    /// <summary> URL-safe random Base64. 32 byte = 256 bit entropi. </summary>
    public string GenerateUrlSafeToken(int byteSize = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteSize);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary> SHA-256 hex (lowercase, 64 char). </summary>
    public string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
