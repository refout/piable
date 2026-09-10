using Piable.Models;

namespace Piable.Tests.Storage;

public class ProviderRepositoryTests
{
    private static ProviderConfig NewProvider(string id = "p1", bool isDefault = false) => new()
    {
        Id = id,
        PresetId = ProviderPresets.OpenAi,
        ApiKey = "sk-secret-key-value",
        DefaultModel = "gpt-4o-mini",
        Models = ["gpt-4o", "gpt-4o-mini"],
        InputPricePer1K = 0.0025m,
        OutputPricePer1K = 0.01m,
        IsDefault = isDefault,
    };

    [Fact]
    public async Task 保存后可完整读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var provider = NewProvider();
        provider.Endpoint = "https://example.com/v1";
        provider.DeploymentName = "dep-1";
        provider.Temperature = 0.7;
        provider.MaxTokens = 2048;
        provider.TopP = 0.9;
        provider.ModelListUpdatedAt = DateTimeOffset.Now;

        await workspace.Providers.UpsertAsync(provider);
        var loaded = await workspace.Providers.GetByIdAsync(provider.Id);

        Assert.NotNull(loaded);
        Assert.Equal(ProviderPresets.OpenAi, loaded.PresetId);
        Assert.Equal("sk-secret-key-value", loaded.ApiKey);
        Assert.Equal("https://example.com/v1", loaded.Endpoint);
        Assert.Equal("dep-1", loaded.DeploymentName);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], loaded.Models);
        Assert.Equal("gpt-4o-mini", loaded.DefaultModel);
        Assert.Equal(0.7, loaded.Temperature);
        Assert.Equal(2048, loaded.MaxTokens);
        Assert.Equal(0.9, loaded.TopP);
        Assert.Equal(0.0025m, loaded.InputPricePer1K);
        Assert.Equal(0.01m, loaded.OutputPricePer1K);
    }

    [Fact]
    public async Task APIKey以密文落库_磁盘上任何文件都不含明文()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(NewProvider());

        // 库跑在 WAL 模式下，刚写入的数据可能还在 -wal 里没并回主库文件，
        // 因此必须把数据目录下的每个文件都翻一遍，只查 piable.db 会漏。
        var files = Directory.GetFiles(workspace.Root);
        Assert.NotEmpty(files);

        var combined = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            combined.Append(System.Text.Encoding.UTF8.GetString(await ReadAllowingSharedWriteAsync(file)));
        }

        var rawText = combined.ToString();
        Assert.DoesNotContain("sk-secret-key-value", rawText);
        Assert.Contains("v1.", rawText);
    }

    /// <summary>
    /// SQLite 正持有这些文件的句柄，且共享模式比 File.ReadAllBytes 的默认值宽松，
    /// 直接读会撞上"文件被另一进程占用"。这里显式放宽容忍度。
    /// </summary>
    private static async Task<byte[]> ReadAllowingSharedWriteAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task 无Key的本地供应商存NULL并可读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var ollama = new ProviderConfig
        {
            Id = "ollama-1",
            PresetId = ProviderPresets.Ollama,
            ApiKey = null,
            DefaultModel = "llama3",
            Models = ["llama3"],
        };

        await workspace.Providers.UpsertAsync(ollama);
        var loaded = await workspace.Providers.GetByIdAsync("ollama-1");

        Assert.NotNull(loaded);
        Assert.Null(loaded.ApiKey);
    }

    [Fact]
    public async Task 更新同一条配置不会产生重复行()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var provider = NewProvider();
        await workspace.Providers.UpsertAsync(provider);

        provider.DefaultModel = "gpt-4o";
        provider.ApiKey = "sk-rotated";
        await workspace.Providers.UpsertAsync(provider);

        var all = await workspace.Providers.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("gpt-4o", all[0].DefaultModel);
        Assert.Equal("sk-rotated", all[0].ApiKey);

        // CreatedAt 不应被更新语句覆盖
        Assert.Equal(provider.CreatedAt.ToUniversalTime().ToString("O"),
            all[0].CreatedAt.ToUniversalTime().ToString("O"));
    }

    [Fact]
    public async Task 全局默认供应商唯一_设置新的会自动取消旧的()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(NewProvider("p1", isDefault: true));
        await workspace.Providers.UpsertAsync(NewProvider("p2", isDefault: true));

        var all = await workspace.Providers.GetAllAsync();

        Assert.Single(all, p => p.IsDefault);
        Assert.Equal("p2", all.Single(p => p.IsDefault).Id);
    }

    [Fact]
    public async Task 默认供应商排在列表最前()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(NewProvider("p1"));
        await workspace.Providers.UpsertAsync(NewProvider("p2", isDefault: true));

        var all = await workspace.Providers.GetAllAsync();

        Assert.Equal("p2", all[0].Id);
    }

    [Fact]
    public async Task 删除后可统计引用它的会话数()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(NewProvider());
        // 会话对 Agents 也有外键，被引用的智能体必须先存在
        await workspace.Agents.UpsertAsync(new Agent { Id = "a1", Name = "通用助手" });

        await workspace.Sessions.UpsertAsync(new ChatSession
        {
            Id = "s1",
            ProviderId = "p1",
            AgentId = "a1",
        });

        Assert.Equal(1, await workspace.Providers.CountReferencingSessionsAsync("p1"));
    }

    [Fact]
    public async Task 不存在的ID返回null()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        Assert.Null(await workspace.Providers.GetByIdAsync("nope"));
    }
}
