using Piable.Models;
using Piable.Services;

namespace Piable.Tests.Services;

public class ConfigServiceTests
{
    private static ConfigService Create(TestWorkspace workspace) =>
        TestServices.Create(workspace).Config;

    [Fact]
    public async Task 首次初始化写入两个内置智能体()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        await service.InitializeAsync();
        var agents = await service.GetAgentsAsync();

        Assert.Equal(2, agents.Count);
        Assert.Contains(agents, a => a.Name == "通用助手");
        Assert.Contains(agents, a => a.Name == "代码助手");
        Assert.All(agents, a => Assert.True(a.IsBuiltIn));
    }

    [Fact]
    public async Task 通用助手为默认智能体()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);
        await service.InitializeAsync();

        var defaultAgent = await service.GetDefaultAgentAsync();

        Assert.NotNull(defaultAgent);
        Assert.Equal(ConfigService.GeneralAgentId, defaultAgent.Id);
    }

    [Fact]
    public async Task 重复初始化不会重复写入内置智能体()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        await service.InitializeAsync();
        await service.InitializeAsync();

        Assert.Equal(2, (await service.GetAgentsAsync()).Count);
    }

    [Fact]
    public async Task 内置智能体的名称与提示词可被用户修改且不会被重置()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);
        await service.InitializeAsync();

        var agent = await service.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.Name = "我的助手";
        agent.SystemPrompt = "自定义提示词";
        await service.SaveAgentAsync(agent);

        // 再次"启动"
        await service.InitializeAsync();

        var reloaded = await service.GetAgentAsync(ConfigService.GeneralAgentId);
        Assert.Equal("我的助手", reloaded!.Name);
        Assert.Equal("自定义提示词", reloaded.SystemPrompt);
    }

    [Fact]
    public async Task 首个创建的供应商自动成为默认()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);
        await service.InitializeAsync();

        var provider = await service.GetOrCreateProviderAsync(ProviderPresets.OpenAi);

        Assert.True(provider.IsDefault);
        Assert.Equal(ProviderPresets.OpenAi, (await service.GetDefaultProviderAsync())!.PresetId);
    }

    [Fact]
    public async Task 第二个供应商不会抢占默认位置()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        await service.GetOrCreateProviderAsync(ProviderPresets.OpenAi);
        var second = await service.GetOrCreateProviderAsync(ProviderPresets.DeepSeek);

        Assert.False(second.IsDefault);
    }

    [Fact]
    public async Task 同一预设重复获取返回同一条配置()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        var first = await service.GetOrCreateProviderAsync(ProviderPresets.OpenAi);
        var second = await service.GetOrCreateProviderAsync(ProviderPresets.OpenAi);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await service.GetProvidersAsync());
    }

    [Fact]
    public async Task 新建的供应商继承预设的默认值()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        var provider = await service.GetOrCreateProviderAsync(ProviderPresets.DeepSeek);

        Assert.Equal("https://api.deepseek.com/v1", provider.Endpoint);
        Assert.Equal("deepseek-chat", provider.DefaultModel);
        Assert.Contains("deepseek-chat", provider.Models);
        Assert.True(provider.InputPricePer1K > 0);
    }

    [Fact]
    public async Task Ollama预设无默认模型且无需Key()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        var provider = await service.GetOrCreateProviderAsync(ProviderPresets.Ollama);

        Assert.Empty(provider.Models);
        Assert.Equal(string.Empty, provider.DefaultModel);
        Assert.Null(provider.ApiKey);
        Assert.False(provider.IsUsable);
    }

    [Fact]
    public async Task 未知预设抛出可读异常()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetOrCreateProviderAsync("不存在的预设"));
    }

    [Fact]
    public async Task 未配置任何供应商时默认供应商为null()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        Assert.Null(await service.GetDefaultProviderAsync());
    }

    [Fact]
    public async Task 供应商配置可修改并保留()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        var provider = await service.GetOrCreateProviderAsync(ProviderPresets.OpenAi);
        provider.ApiKey = "sk-new";
        provider.DefaultModel = "gpt-4o";
        provider.Models = ["gpt-4o", "gpt-4o-mini"];
        await service.SaveProviderAsync(provider);

        var reloaded = await service.GetProviderAsync(provider.Id);
        Assert.Equal("sk-new", reloaded!.ApiKey);
        Assert.Equal("gpt-4o", reloaded.DefaultModel);
        Assert.Equal(2, reloaded.Models.Count);
    }

    [Fact]
    public async Task 偏好可保存并读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = Create(workspace);

        var prefs = await service.GetPreferencesAsync();
        prefs.Theme = "Dark";
        prefs.LeftPanelCollapsed = true;
        await service.SavePreferencesAsync(prefs);

        var reloaded = await service.GetPreferencesAsync();
        Assert.Equal("Dark", reloaded.Theme);
        Assert.True(reloaded.LeftPanelCollapsed);
    }

    [Fact]
    public void 内置预设清单完整且ID唯一()
    {
        var ids = ProviderPresets.All.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(ProviderPresets.OpenAi, ids);
        Assert.Contains(ProviderPresets.DeepSeek, ids);
        Assert.Contains(ProviderPresets.Ollama, ids);
        Assert.Contains(ProviderPresets.AzureOpenAi, ids);
    }

    [Fact]
    public void 除Ollama外均要求APIKey()
    {
        foreach (var preset in ProviderPresets.All.Where(p => p.Id != ProviderPresets.Ollama))
        {
            Assert.True(preset.RequiresApiKey, $"{preset.DisplayName} 应当要求 API Key");
        }

        Assert.False(ProviderPresets.Get(ProviderPresets.Ollama).RequiresApiKey);
    }

    [Fact]
    public void 支持动态获取的预设必须声明端点()
    {
        foreach (var preset in ProviderPresets.All)
        {
            if (preset.SupportsModelFetch)
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.ModelsEndpoint));
            }
        }

        // Azure 用部署名而非模型名，只能手动填写
        Assert.False(ProviderPresets.Get(ProviderPresets.AzureOpenAi).SupportsModelFetch);
    }
}
