using Piable.Models;
using Piable.Services;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

/// <summary>
/// 让会话 ViewModel 打完整的一轮：真实 SDK → 本地 mock 服务端 → SQLite 落库。
/// 这是覆盖面最广的一层测试，发送、流式累积、统计、持久化、自动命名都在其中。
/// </summary>
public class ChatSessionViewModelTests
{
    private sealed record Harness(
        TestWorkspace Workspace,
        ChatSessionViewModel ViewModel,
        StubStatusReporter Status,
        ProviderConfig Provider,
        IReadOnlyList<Agent> Agents);

    private static async Task<Harness> CreateAsync(
        MockOpenAiServer server, UserPreferences? preferences = null)
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var provider = await services.Config.GetOrCreateProviderAsync(ProviderPresets.OpenAi);
        provider.Endpoint = server.BaseUrl;
        provider.ApiKey = "sk-test";
        provider.DefaultModel = "gpt-4o-mini";
        provider.InputPricePer1K = 0.0025m;
        provider.OutputPricePer1K = 0.01m;
        await services.Config.SaveProviderAsync(provider);

        var agents = await services.Config.GetAgentsAsync();
        var session = await services.Sessions.CreateAsync(provider.Id, ConfigService.GeneralAgentId);

        var status = new StubStatusReporter();
        var vm = services.CreateChatSessionViewModel(
            session, agents, provider, preferences ?? new UserPreferences(), status);

        return new Harness(workspace, vm, status, provider, agents);
    }

    /// <summary>对话流里的消息条目（不含工具调用）。</summary>
    private static List<MessageViewModel> MessagesOf(ChatSessionViewModel vm) =>
        [.. vm.Items.OfType<MessageViewModel>()];

    [Fact]
    public async Task 一轮完整对话产生用户与助手两条消息()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["你好", "！我是 AI。"], promptTokens: 10, completionTokens: 5);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "你好";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, MessagesOf(h.ViewModel).Count);
        Assert.True(MessagesOf(h.ViewModel)[0].IsUser);
        Assert.Equal("你好", MessagesOf(h.ViewModel)[0].Content);
        Assert.True(MessagesOf(h.ViewModel)[1].IsAssistant);
        Assert.Equal("你好！我是 AI。", MessagesOf(h.ViewModel)[1].Content);
    }

    [Fact]
    public async Task 生成结束后消息已落库()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        var sessions = new SessionService(h.Workspace.Sessions);
        var reloaded = await sessions.LoadAsync(h.ViewModel.Session.Id);

        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal("问题", reloaded.Messages[0].Content);
        Assert.Equal("回答", reloaded.Messages[1].Content);
        Assert.Equal(MessageRole.Assistant, reloaded.Messages[1].Role);
    }

    [Fact]
    public async Task 统计信息按供应商单价计算并写入消息()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"], promptTokens: 1000, completionTokens: 2000);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        var assistant = MessagesOf(h.ViewModel)[1];
        Assert.Equal(1000, assistant.PromptTokens);
        Assert.Equal(2000, assistant.CompletionTokens);
        Assert.Equal(3000, assistant.TotalTokens);
        // (1000/1000)*0.0025 + (2000/1000)*0.01 = 0.0025 + 0.02 = 0.0225
        Assert.Equal(0.0225m, assistant.EstimatedCost);
    }

    [Fact]
    public async Task 首条消息触发会话自动命名()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["好的"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        Assert.Equal(SessionTitleGenerator.DefaultTitle, h.ViewModel.Session.Title);

        h.ViewModel.InputText = "帮我写一个快速排序";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal("帮我写一个快速排序", h.ViewModel.Session.Title);
        Assert.Equal("帮我写一个快速排序", h.ViewModel.Title);
    }

    [Fact]
    public async Task 发送后清空输入框并恢复空闲状态()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, h.ViewModel.InputText);
        Assert.False(h.ViewModel.IsGenerating);
        Assert.False(h.ViewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task 空白输入不触发发送()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "   ";

        Assert.False(h.ViewModel.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task 系统提示词随请求发出()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Contains("你是一个专业、可靠的 AI 助手", server.RequestBodies.Single());
    }

    [Fact]
    public async Task 供应商未配置时给出提示且不产生消息()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.Provider = null;
        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Empty(MessagesOf(h.ViewModel));
        Assert.Contains(h.Status.Errors, e => e.Contains("供应商"));
    }

    [Fact]
    public async Task 请求失败时给出提示且不留下空气泡()
    {
        await using var server = MockOpenAiServer.Start();
        server.StatusCode = 401;
        server.SseBody = """{"error":{"message":"bad key"}}""";
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        // 用户消息保留，失败的空助手消息被撤掉
        Assert.Single(MessagesOf(h.ViewModel));
        Assert.True(MessagesOf(h.ViewModel)[0].IsUser);
        Assert.Contains(h.Status.Errors, e => e.Contains("API Key"));
    }

    [Fact]
    public async Task 用户消息在请求前就落库_失败也不丢失()
    {
        await using var server = MockOpenAiServer.Start();
        server.StatusCode = 500;
        server.SseBody = """{"error":{"message":"boom"}}""";
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "这条不该丢";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        var sessions = new SessionService(h.Workspace.Sessions);
        var reloaded = await sessions.LoadAsync(h.ViewModel.Session.Id);

        Assert.Single(reloaded!.Messages);
        Assert.Equal("这条不该丢", reloaded.Messages[0].Content);
    }

    [Fact]
    public async Task 标题栏统计汇总全部消息()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"], promptTokens: 100, completionTokens: 200);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Contains("💬 2条", h.ViewModel.HeaderStatistics);
        Assert.Contains("🔢 300", h.ViewModel.HeaderStatistics);
    }

    [Fact]
    public async Task 智能体按钮显示当前智能体名称()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        Assert.Contains("通用助手", h.ViewModel.AgentButtonText);
    }

    [Fact]
    public async Task 参数摘要反映当前取值()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.Temperature = 1.25;
        h.ViewModel.MaxTokens = 999;

        Assert.Contains("Temperature 1.25", h.ViewModel.ParameterSummary);
        Assert.Contains("Max Tokens 999", h.ViewModel.ParameterSummary);
    }

    [Fact]
    public async Task 重置参数恢复智能体的默认值()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        // 代码助手的 Temperature 为 0.2
        h.ViewModel.SelectedAgent = h.Agents.Single(a => a.Id == ConfigService.CoderAgentId);
        Assert.Equal(0.2, h.ViewModel.Temperature);

        h.ViewModel.Temperature = 1.8;
        h.ViewModel.MaxTokens = 123;

        h.ViewModel.ResetParametersCommand.Execute(null);

        Assert.Equal(0.2, h.ViewModel.Temperature);
        Assert.Equal(2048, h.ViewModel.MaxTokens);
    }

    [Fact]
    public async Task 切换智能体后参数取智能体的覆盖值()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        // 内置的代码助手 Temperature 为 0.2
        var coder = h.Agents.Single(a => a.Id == ConfigService.CoderAgentId);
        h.ViewModel.SelectedAgent = coder;

        Assert.Equal(0.2, h.ViewModel.Temperature);
        Assert.Equal(ConfigService.CoderAgentId, h.ViewModel.Session.AgentId);
    }

    [Fact]
    public async Task 生成过程会请求滚动到底部()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["一", "二", "三"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.IsActive = true;
        var scrollRequests = 0;
        h.ViewModel.ScrollToEndRequested += (_, _) => scrollRequests++;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.True(scrollRequests > 0);
    }
}
