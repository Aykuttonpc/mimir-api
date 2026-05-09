using System.Security.Cryptography;
using System.Text;

namespace Mimir.Api.Services.Security;

public record MessageCipher(byte[] Iv, byte[] Ciphertext, byte[] Tag);

public interface IMessageCrypto
{
    MessageCipher Encrypt(string plaintext);
    string Decrypt(byte[] iv, byte[] ciphertext, byte[] tag);
}

/// <summary>
/// AES-256-GCM at-rest encryption (ADR-012).
/// Key: 32 byte (256 bit), `Crypto:MessageKey` config'inden Base64-decoded.
/// IV: per-message random 12 byte. Tag: 16 byte (auth).
/// </summary>
public class AesGcmMessageCrypto : IMessageCrypto
{
    private const int IvSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmMessageCrypto(IConfiguration config)
    {
        var keyBase64 = config["Crypto:MessageKey"]
            ?? throw new InvalidOperationException("Crypto:MessageKey missing");
        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"Crypto:MessageKey must be 32 bytes after Base64-decode (got {_key.Length}).");
    }

    public MessageCipher Encrypt(string plaintext)
    {
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(iv, plain, cipher, tag);
        return new MessageCipher(iv, cipher, tag);
    }

    public string Decrypt(byte[] iv, byte[] ciphertext, byte[] tag)
    {
        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(iv, ciphertext, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
