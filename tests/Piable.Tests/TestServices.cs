using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;
using Piable.ViewModels;

namespace Piable.Tests;

/// <summary>
/// 在 <see cref="TestWorkspace"/> 之上装配出完整的服务栈。
///
/// 单独抽出来是因为服务之间的依赖关系会随功能演进变化（例如加入工具层后
/// ConfigService、会话 ViewModel 的构造参数都变了），集中在一处修改，
/// 不必逐个测试文件跟进。
/// </summary>
internal sealed class TestServices
{
    private TestServices(
        ConfigService config,
        SessionService sessions,
        SkillService skills,
        ToolCatalog tools,
        AgentResourceService resources,
        AgentOrchestrator orchestrator,
        McpClientService mcp)
    {
        Config = config;
        Sessions = sessions;
        Skills = skills;
        Tools = tools;
        Resources = resources;
        Orchestrator = orchestrator;
        Mcp = mcp;
        Calculator = new TokenCostCalculator();
    }

    public ConfigService Config { get; }

    public SessionService Sessions { get; }

    public SkillService Skills { get; }

    public ToolCatalog Tools { get; }

    public AgentResourceService Resources { get; }

    public TokenCostCalculator Calculator { get; }

    public AgentOrchestrator Orchestrator { get; }

    public McpClientService Mcp { get; }

    public static TestServices Create(TestWorkspace workspace)
    {
        var config = new ConfigService(
            workspace.Providers, workspace.Agents, workspace.Preferences, workspace.McpServers);
        var skills = new SkillService(workspace.Skills);
        var mcp = new McpClientService();
        var tools = new ToolCatalog(skills, mcp, config);
        var resources = new AgentResourceService(mcp, config);

        return new TestServices(
            config, new SessionService(workspace.Sessions), skills, tools, resources,
            new AgentOrchestrator(new ChatClientFactory()), mcp);
    }

    /// <summary>初始化内置数据（智能体与技能）。</summary>
    public async Task InitializeSeedDataAsync()
    {
        await Config.InitializeAsync().ConfigureAwait(false);
        await Skills.InitializeAsync().ConfigureAwait(false);
    }

    public MainWindowViewModel CreateMainWindowViewModel() => new(
        Config,
        Sessions,
        Orchestrator,
        Calculator,
        new ModelListService(new HttpClient()),
        Skills,
        Tools,
        Resources,
        Mcp);

    public ChatSessionViewModel CreateChatSessionViewModel(
        ChatSession session,
        IReadOnlyList<Agent> agents,
        ProviderConfig? provider,
        UserPreferences preferences,
        IStatusReporter status) =>
        new(session, agents, provider, Sessions, Orchestrator, Tools, Resources,
            Calculator, preferences, status);
}
