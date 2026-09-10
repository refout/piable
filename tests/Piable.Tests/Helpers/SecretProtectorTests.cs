using Piable.Helpers;

namespace Piable.Tests.Helpers;

public class SecretProtectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _keyPath;

    public SecretProtectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "piable-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        _keyPath = Path.Combine(_root, "piable.key");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void 加解密可往返()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);
        const string secret = "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789";

        var cipher = protector.Protect(secret);

        Assert.NotNull(cipher);
        Assert.NotEqual(secret, cipher);
        Assert.DoesNotContain(secret, cipher);
        Assert.Equal(secret, protector.Unprotect(cipher));
    }

    [Fact]
    public void 密文带版本前缀()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);
        Assert.StartsWith("v1.", protector.Protect("sk-test"));
    }

    [Fact]
    public void 相同明文两次加密产生不同密文()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);

        // 每次使用随机 nonce，避免相同 Key 在库中出现相同密文
        Assert.NotEqual(protector.Protect("sk-same"), protector.Protect("sk-same"));
    }

    [Fact]
    public void 重复加载同一密钥文件可解密既有密文()
    {
        var cipher = AesGcmSecretProtector.LoadOrCreate(_keyPath).Protect("sk-persist");
        var reopened = AesGcmSecretProtector.LoadOrCreate(_keyPath);

        Assert.Equal("sk-persist", reopened.Unprotect(cipher));
    }

    [Fact]
    public void 换密钥后解密失败_而不是返回错误内容()
    {
        var cipher = AesGcmSecretProtector.LoadOrCreate(_keyPath).Protect("sk-original");

        File.Delete(_keyPath);
        var other = AesGcmSecretProtector.LoadOrCreate(_keyPath);

        Assert.Throws<SecretProtectionException>(() => other.Unprotect(cipher));
        // 批量读取场景走 TryUnprotect，应安静地返回 null 而不是让整个列表加载失败
        Assert.Null(other.TryUnprotect(cipher));
    }

    [Fact]
    public void 密文被篡改时解密失败()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);
        var cipher = protector.Protect("sk-tamper")!;

        // 翻转 Base64 载荷中的一个字符，GCM 的认证标签应当识别出篡改
        var payload = cipher[3..].ToCharArray();
        payload[0] = payload[0] == 'A' ? 'B' : 'A';
        var tampered = "v1." + new string(payload);

        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空值原样返回(string? value)
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);

        Assert.Equal(value, protector.Protect(value));
        Assert.Equal(value, protector.Unprotect(value));
    }

    [Fact]
    public void 无版本前缀的值视为历史明文_可直接读取()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);

        // 手工写入或旧版本遗留的明文 Key 不应导致读取失败
        Assert.Equal("sk-legacy", protector.Unprotect("sk-legacy"));
    }

    [Fact]
    public void 支持中文与长文本()
    {
        var protector = AesGcmSecretProtector.LoadOrCreate(_keyPath);
        var secret = "密钥-" + new string('x', 4096);

        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void 密钥文件损坏时给出可操作的错误信息()
    {
        File.WriteAllText(_keyPath, "这不是合法的 Base64!!!");

        var ex = Assert.Throws<SecretProtectionException>(
            () => AesGcmSecretProtector.LoadOrCreate(_keyPath));
        Assert.Contains("重新填写", ex.Message);
    }

    [Fact]
    public void 非Windows平台下密钥文件权限为仅属主可读写()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        AesGcmSecretProtector.LoadOrCreate(_keyPath);
        var mode = File.GetUnixFileMode(_keyPath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
