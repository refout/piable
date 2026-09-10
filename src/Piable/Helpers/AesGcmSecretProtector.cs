using System.Security.Cryptography;
using System.Text;

namespace Piable.Helpers;

/// <summary>
/// 基于 AES-256-GCM 的本地密钥保护。
///
/// 密钥保存在应用数据目录下的 <c>piable.key</c>（32 字节随机数，Base64 编码）。
/// 密文格式为 <c>v1.{Base64(nonce|tag|ciphertext)}</c>，带版本前缀以便将来更换算法时平滑迁移。
///
/// <para>
/// <b>安全边界（务必知悉）：</b>这是"防止密钥被明文抄走"级别的保护，不是硬件级密钥库。
/// Unix 上密钥文件权限被设为 0600；Windows 上依赖 %AppData% 目录继承的
/// 仅当前用户可访问 ACL，程序本身不额外收紧权限。
/// 能够以当前用户身份读取文件系统的攻击者仍可同时取走密钥与密文。
/// </para>
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string VersionPrefix = "v1.";
    private const int KeySizeBytes = 32;   // AES-256
    private const int NonceSize = 12;      // AesGcm.NonceByteSizes 仅允许 12
    private const int TagSize = 16;        // AesGcm.TagByteSizes 仅允许 12/13/14/15/16

    private readonly byte[] _key;

    private AesGcmSecretProtector(byte[] key) => _key = key;

    /// <summary>从密钥文件加载；文件不存在时生成一个新密钥并落盘。</summary>
    public static AesGcmSecretProtector LoadOrCreate(string keyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFilePath);

        if (File.Exists(keyFilePath))
        {
            var raw = File.ReadAllText(keyFilePath).Trim();
            byte[] key;
            try
            {
                key = Convert.FromBase64String(raw);
            }
            catch (FormatException ex)
            {
                throw new SecretProtectionException(
                    $"密钥文件已损坏，无法解析：{keyFilePath}。" +
                    "删除该文件会生成新密钥，但此前保存的 API Key 将无法解密，需要重新填写。", ex);
            }

            if (key.Length != KeySizeBytes)
            {
                throw new SecretProtectionException(
                    $"密钥文件长度异常（期望 {KeySizeBytes} 字节，实际 {key.Length} 字节）：{keyFilePath}");
            }

            return new AesGcmSecretProtector(key);
        }

        return new AesGcmSecretProtector(CreateKeyFile(keyFilePath));
    }

    /// <summary>生成新密钥并写入文件，同时尽力收紧文件权限。</summary>
    private static byte[] CreateKeyFile(string keyFilePath)
    {
        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);

        var directory = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写入临时文件再原子替换，避免并发启动时读到半截内容
        var tempPath = keyFilePath + ".tmp";
        File.WriteAllText(tempPath, Convert.ToBase64String(key), Encoding.UTF8);
        RestrictToCurrentUser(tempPath);
        File.Move(tempPath, keyFilePath, overwrite: true);
        RestrictToCurrentUser(keyFilePath);

        return key;
    }

    /// <summary>
    /// Unix 上把权限收紧为 0600。Windows 不做处理：%AppData% 本身已按用户隔离，
    /// 且 .NET 缺少免额外依赖的 ACL 设置途径。
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限收紧失败不应阻断启动；文件仍在仅用户可访问的目录内
        }
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);

        return VersionPrefix + Convert.ToBase64String(packed);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return ciphertext;
        }

        // 不带版本前缀的值视为历史遗留的明文，原样返回。
        // 这样手工写入或早期版本的数据仍可读取，并在下次保存时自动转为密文。
        if (!ciphertext.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return ciphertext;
        }

        try
        {
            var packed = Convert.FromBase64String(ciphertext[VersionPrefix.Length..]);
            if (packed.Length < NonceSize + TagSize)
            {
                throw new SecretProtectionException("密文长度不足，数据可能已损坏。");
            }

            var nonce = packed.AsSpan(0, NonceSize);
            var tag = packed.AsSpan(NonceSize, TagSize);
            var cipher = packed.AsSpan(NonceSize + TagSize);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            throw new SecretProtectionException(
                "API Key 解密失败：密钥文件与密文不匹配。通常是因为删除过 piable.key，" +
                "需要重新填写 API Key。", ex);
        }
        catch (FormatException ex)
        {
            throw new SecretProtectionException("密文格式错误，无法解析。", ex);
        }
    }

    public string? TryUnprotect(string? ciphertext)
    {
        try
        {
            return Unprotect(ciphertext);
        }
        catch (SecretProtectionException)
        {
            return null;
        }
    }
}
