using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.Tests.Tools;

public class ToolCatalogTests
{
    private static async Task<(TestWorkspace Workspace, TestServices Services, ToolCatalog Catalog)>
        CreateAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        return (workspace, services, services.Tools);
    }

    [Fact]
    public async Task 无工具的智能体不产生任何工具()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        var resolution = await catalog.ResolveAsync(agent!);

        Assert.Empty(resolution.Tools);
        Assert.Empty(resolution.Warnings);
    }

    [Fact]
    public async Task 关联技能后解析出对应工具()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.SkillIds = [SkillService.DateTimeSkillId];
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        Assert.Single(resolution.Tools);
        Assert.Equal("current_datetime", resolution.Tools[0].Name);
        Assert.Equal(ToolRisk.Safe, resolution.Tools[0].Risk);
    }

    [Fact]
    public async Task 只解析被关联的技能()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.SkillIds = [SkillService.DateTimeSkillId];  // 不含 ShellSkillId
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        Assert.DoesNotContain(resolution.Tools, t => t.Name == "run_shell");
    }

    [Fact]
    public async Task 停用的技能关联了也不会出现()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var skill = await services.Skills.GetAsync(SkillService.DateTimeSkillId);
        skill!.Enabled = false;
        await services.Skills.SaveAsync(skill);

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.SkillIds = [SkillService.DateTimeSkillId];
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        Assert.Empty(resolution.Tools);
    }

    [Fact]
    public async Task MCP服务器连不上时给警告但不影响技能工具()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        // 指向一个不存在的命令，必然连接失败
        var server = new McpServerConfig
        {
            Name = "不存在的服务器",
            Transport = McpTransport.Stdio,
            Command = "definitely-not-a-real-command-xyz",
            Enabled = true,
        };
        await services.Config.SaveMcpServerAsync(server);

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.SkillIds = [SkillService.DateTimeSkillId];
        agent.McpServerIds = [server.Id];
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        // 技能工具照常可用
        Assert.Contains(resolution.Tools, t => t.Name == "current_datetime");
        // 失败的服务器被记成警告，而不是让整轮对话失败
        Assert.NotEmpty(resolution.Warnings);
        Assert.Contains(resolution.Warnings, w => w.Contains("不存在的服务器"));
    }

    [Fact]
    public async Task 关联的MCP服务器已被删除时给出警告()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.McpServerIds = ["早已删除的服务器"];
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        Assert.Empty(resolution.Tools);
        Assert.Contains(resolution.Warnings, w => w.Contains("不存在"));
    }

    [Fact]
    public async Task 停用的MCP服务器被静默跳过()
    {
        var (workspace, services, catalog) = await CreateAsync();
        await using var _ = workspace;

        var server = new McpServerConfig
        {
            Name = "已停用",
            Transport = McpTransport.Stdio,
            Command = "definitely-not-a-real-command-xyz",
            Enabled = false,
        };
        await services.Config.SaveMcpServerAsync(server);

        var agent = await services.Config.GetAgentAsync(ConfigService.GeneralAgentId);
        agent!.McpServerIds = [server.Id];
        await services.Config.SaveAgentAsync(agent);

        var resolution = await catalog.ResolveAsync(agent);

        // 用户主动停用的服务器不该每次对话都报一次错
        Assert.Empty(resolution.Tools);
        Assert.Empty(resolution.Warnings);
    }

    [Fact]
    public async Task 工具名重复时只保留先注册的并给出警告()
    {
        var (workspace, services, _) = await CreateAsync();
        await using var _ = workspace;

        // 造一个与内置技能同名的技能
        await workspace.Skills.UpsertAsync(new SkillDefinition
        {
            Name = "重名技能",
            ToolName = "current_datetime",
            Handler = "current_datetime",
            Enabled = true,
        });

        var skills = await services.Skills.GetAllAsync();
        var tools = services.Skills.ResolveTools(skills);

        // 两个内置技能 + 刚加入的重名技能
        Assert.Equal(3, tools.Count);
        Assert.Equal(2, tools.Count(t => t.Name == "current_datetime"));

        // 去重发生在目录层：重名的一个被丢掉，剩下 current_datetime 与 run_shell
        var resolved = await services.Tools.ResolveAsync(new Agent
        {
            Id = "a1",
            SkillIds = [.. skills.Select(s => s.Id)],
        });

        Assert.Equal(2, resolved.Tools.Count);
        Assert.Single(resolved.Tools, t => t.Name == "current_datetime");
        Assert.Contains(resolved.Warnings, w => w.Contains("重复"));
    }
}
