namespace Piable.Helpers;

/// <summary>API Key 等敏感字段的加密存取。</summary>
public interface ISecretProtector
{
    /// <summary>加密明文。空值原样返回。</summary>
    string? Protect(string? plaintext);

    /// <summary>解密。空值返回 null；无法解密时抛出 <see cref="SecretProtectionException"/>。</summary>
    string? Unprotect(string? ciphertext);

    /// <summary>尝试解密，失败时返回 null 而不抛异常（读取历史数据时使用）。</summary>
    string? TryUnprotect(string? ciphertext);
}

/// <summary>密钥缺失、损坏或密文无法解密时抛出。</summary>
public sealed class SecretProtectionException : Exception
{
    public SecretProtectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
