using Piable.Models;

namespace Piable.Tests.Storage;

public class AgentRepositoryTests
{
    [Fact]
    public async Task 保存后可完整读回_含工具关联列表()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var agent = new Agent
        {
            Id = "a1",
            Name = "代码助手",
            Description = "擅长写代码",
            SystemPrompt = "你是一个专业的代码助手",
            Model = "gpt-4o",
            Temperature = 0.2,
            MaxTokens = 4096,
            TopP = 0.95,
            McpServerIds = ["mcp-1", "mcp-2"],
            SkillIds = ["skill-code"],
        };

        await workspace.Agents.UpsertAsync(agent);
        var loaded = await workspace.Agents.GetByIdAsync("a1");

        Assert.NotNull(loaded);
        Assert.Equal("代码助手", loaded.Name);
        Assert.Equal("擅长写代码", loaded.Description);
        Assert.Equal("你是一个专业的代码助手", loaded.SystemPrompt);
        Assert.Equal("gpt-4o", loaded.Model);
        Assert.Equal(0.2, loaded.Temperature);
        Assert.Equal(4096, loaded.MaxTokens);
        Assert.Equal(0.95, loaded.TopP);
        Assert.Equal(["mcp-1", "mcp-2"], loaded.McpServerIds);
        Assert.Equal(["skill-code"], loaded.SkillIds);
        Assert.True(loaded.HasTools);
    }

    [Fact]
    public async Task 全局默认智能体唯一()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Agents.UpsertAsync(new Agent { Id = "a1", IsDefault = true });
        await workspace.Agents.UpsertAsync(new Agent { Id = "a2", IsDefault = true });

        var all = await workspace.Agents.GetAllAsync();

        Assert.Equal("a2", all.Single(a => a.IsDefault).Id);
    }

    [Fact]
    public async Task 内置智能体不可删除()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Agents.UpsertAsync(new Agent { Id = "builtin", Name = "通用助手", IsBuiltIn = true });
        await workspace.Agents.UpsertAsync(new Agent { Id = "custom", Name = "我的助手" });

        await workspace.Agents.DeleteAsync("builtin");
        await workspace.Agents.DeleteAsync("custom");

        Assert.NotNull(await workspace.Agents.GetByIdAsync("builtin"));
        Assert.Null(await workspace.Agents.GetByIdAsync("custom"));
    }

    [Fact]
    public async Task 更新不会把内置标记改掉()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var agent = new Agent { Id = "a1", Name = "通用助手", IsBuiltIn = true };
        await workspace.Agents.UpsertAsync(agent);

        // 界面上的编辑不会触碰 IsBuiltIn，但即便传了 false 也不应生效
        agent.Name = "改名后";
        agent.IsBuiltIn = false;
        await workspace.Agents.UpsertAsync(agent);

        var loaded = await workspace.Agents.GetByIdAsync("a1");
        Assert.Equal("改名后", loaded!.Name);
        Assert.True(loaded.IsBuiltIn);
    }

    [Fact]
    public async Task 空工具列表可往返()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.Agents.UpsertAsync(new Agent { Id = "a1", Name = "通用助手" });

        var loaded = await workspace.Agents.GetByIdAsync("a1");

        Assert.Empty(loaded!.McpServerIds);
        Assert.Empty(loaded.SkillIds);
        Assert.False(loaded.HasTools);
    }
}
