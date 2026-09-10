using Microsoft.Extensions.AI;
using Piable.Models;
using Piable.Services.Tools;

namespace Piable.Tests.Tools;

public class SkillServiceTests
{
    [Fact]
    public async Task 首次初始化写入内置技能()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        await service.InitializeAsync();
        var skills = await service.GetAllAsync();

        Assert.Contains(skills, s => s.ToolName == "current_datetime");
        Assert.Contains(skills, s => s.ToolName == "run_shell");
    }

    [Fact]
    public async Task 重复初始化不会重复写入()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        await service.InitializeAsync();
        await service.InitializeAsync();

        Assert.Equal(2, (await service.GetAllAsync()).Count);
    }

    [Fact]
    public async Task 内置技能可被用户修改且不会被重置()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        var skill = await service.GetAsync(SkillService.ShellSkillId);
        skill!.Name = "我的命令工具";
        skill.Description = "自定义描述";
        await service.SaveAsync(skill);

        await service.InitializeAsync();

        var reloaded = await service.GetAsync(SkillService.ShellSkillId);
        Assert.Equal("我的命令工具", reloaded!.Name);
        Assert.Equal("自定义描述", reloaded.Description);
    }

    [Fact]
    public async Task 保存时校验工具名()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        var skill = new SkillDefinition
        {
            Name = "测试",
            ToolName = "执行命令",  // 中文，非法
            Handler = "current_datetime",
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(skill));
        Assert.Contains("工具名", ex.Message);
    }

    [Fact]
    public async Task 保存时校验处理器存在()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        var skill = new SkillDefinition
        {
            Name = "测试",
            ToolName = "my_tool",
            Handler = "不存在的处理器",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(skill));
    }

    [Fact]
    public async Task 技能可完整往返()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        var skill = new SkillDefinition
        {
            Name = "时间查询",
            ToolName = "my_datetime",
            Description = "查询当前时间",
            ToolSpec = """{"type":"object","properties":{}}""",
            Handler = "current_datetime",
            Enabled = true,
        };

        await service.SaveAsync(skill);
        var reloaded = await service.GetAsync(skill.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("时间查询", reloaded.Name);
        Assert.Equal("my_datetime", reloaded.ToolName);
        Assert.Equal("current_datetime", reloaded.Handler);
        Assert.True(reloaded.Enabled);
    }

    [Fact]
    public async Task 解析为工具时携带正确的风险等级与来源()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        var skills = await service.GetAllAsync();
        var tools = service.ResolveTools(skills);

        Assert.Equal(2, tools.Count);

        var datetime = tools.Single(t => t.Name == "current_datetime");
        Assert.Equal(ToolRisk.Safe, datetime.Risk);
        Assert.Equal(ToolSource.Skill, datetime.Source);
        Assert.Contains("当前时间", datetime.SourceLabel);

        var shell = tools.Single(t => t.Name == "run_shell");
        Assert.Equal(ToolRisk.Dangerous, shell.Risk);
    }

    [Fact]
    public async Task 停用的技能不被解析为工具()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        var skill = await service.GetAsync(SkillService.ShellSkillId);
        skill!.Enabled = false;
        await service.SaveAsync(skill);

        var tools = service.ResolveTools(await service.GetAllAsync());

        Assert.DoesNotContain(tools, t => t.Name == "run_shell");
        Assert.Contains(tools, t => t.Name == "current_datetime");
    }

    [Fact]
    public async Task 处理器缺失的技能被跳过而不是让解析失败()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);

        // 直接写库绕过 SaveAsync 的校验，模拟处理器被移除后遗留的定义
        await workspace.Skills.UpsertAsync(new SkillDefinition
        {
            Name = "遗留技能",
            ToolName = "legacy_tool",
            Handler = "已经被删掉的处理器",
            Enabled = true,
        });

        var tools = service.ResolveTools(await service.GetAllAsync());

        Assert.DoesNotContain(tools, t => t.Name == "legacy_tool");
    }

    [Fact]
    public async Task 生成的工具可实际调用()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        var skill = (await service.GetAllAsync()).Single(s => s.ToolName == "current_datetime");
        var descriptor = service.ResolveTools([skill]).Single();

        var function = Assert.IsAssignableFrom<AIFunction>(descriptor.Tool);
        var result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Contains("当前时间：", result?.ToString());
    }

    [Fact]
    public async Task 参数结构损坏时退化为无参数工具()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        var skill = await service.GetAsync(SkillService.DateTimeSkillId);
        skill!.ToolSpec = "这不是 JSON";
        await service.SaveAsync(skill);

        // 一个技能的定义写坏了，不应该让整个智能体的对话起不来
        var descriptor = service.ResolveTools([skill]).Single();
        var function = Assert.IsAssignableFrom<AIFunction>(descriptor.Tool);

        Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task 删除技能()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var service = new SkillService(workspace.Skills);
        await service.InitializeAsync();

        await service.DeleteAsync(SkillService.ShellSkillId);

        Assert.Null(await service.GetAsync(SkillService.ShellSkillId));
        Assert.Single(await service.GetAllAsync());
    }
}
