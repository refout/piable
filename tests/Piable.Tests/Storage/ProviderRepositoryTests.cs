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
    public async Task APIKey以密文落库_数据库文件中不含明文()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Providers.UpsertAsync(NewProvider());

        // 直接读原始库文件，确认明文 Key 没有以任何形式写入磁盘
        var rawBytes = await File.ReadAllBytesAsync(workspace.Paths.DatabasePath);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain("sk-secret-key-value", rawText);
        Assert.Contains("v1.", rawText);
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
