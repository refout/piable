using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;
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
        TestServices Services,
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

        return new Harness(workspace, services, vm, status, provider, agents);
    }

    /// <summary>对话流里的消息条目（不含工具调用）。</summary>
    private static List<MessageViewModel> MessagesOf(ChatSessionViewModel vm) =>
        [.. vm.Items.OfType<MessageViewModel>()];

    // ---------------- 危险工具逐次确认 ----------------

    /// <summary>装一个允许危险工具的智能体，让模型能请求到 run_shell。</summary>
    private static async Task<Agent> UseDangerousAgentAsync(Harness h)
    {
        var agent = new Agent
        {
            Id = "a-danger",
            Name = "危险助手",
            AllowDangerousTools = true,
            SkillIds = [SkillService.ShellSkillId],
        };

        await h.Services.Config.SaveAgentAsync(agent);
        h.ViewModel.SelectedAgent = agent;
        h.ViewModel.InputText = "帮我执行一条命令";

        return agent;
    }

    /// <summary>轮询等待确认卡片出现。生成是异步进行的，没有同步的介入点。</summary>
    private static async Task<ToolConfirmationViewModel> WaitForConfirmationAsync(
        ChatSessionViewModel vm, int timeoutMs = 5000)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            var card = vm.Items.OfType<ToolConfirmationViewModel>().LastOrDefault();
            if (card is not null)
            {
                return card;
            }

            await Task.Delay(20);
            waited += 20;
        }

        throw new TimeoutException("确认卡片没有出现");
    }

    [Fact]
    public async Task 危险工具执行前插入确认卡片_允许后真正执行()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse(
            "run_shell", """{"command":"echo confirm-allowed"}"""));
        server.SseBody = MockOpenAiServer.BuildSse(["已执行。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;
        await UseDangerousAgentAsync(h);

        var send = h.ViewModel.SendCommand.ExecuteAsync(null);

        var card = await WaitForConfirmationAsync(h.ViewModel);
        Assert.Contains("run_shell", card.Headline);
        Assert.Contains("confirm-allowed", card.ArgumentsText);
        Assert.False(card.IsResolved);

        card.AllowCommand.Execute(null);
        await send;

        Assert.True(card.IsResolved);
        var toolCall = h.ViewModel.Items.OfType<ToolCallViewModel>().Single();
        Assert.Equal(ToolInvocationStatusPayload.Succeeded, toolCall.Status);
        Assert.Contains("confirm-allowed", toolCall.ResultText);
    }

    [Fact]
    public async Task 拒绝后工具不执行且记录为用户拒绝()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse(
            "run_shell", """{"command":"echo must-not-run"}"""));
        server.SseBody = MockOpenAiServer.BuildSse(["那我换个方式。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;
        await UseDangerousAgentAsync(h);

        var send = h.ViewModel.SendCommand.ExecuteAsync(null);

        var card = await WaitForConfirmationAsync(h.ViewModel);
        card.RejectCommand.Execute(null);
        await send;

        var toolCall = h.ViewModel.Items.OfType<ToolCallViewModel>().Single();
        Assert.Equal(ToolInvocationStatusPayload.Declined, toolCall.Status);
        Assert.DoesNotContain("must-not-run", toolCall.ResultText);
    }

    [Fact]
    public async Task 关闭逐次确认后不再插入卡片()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse(
            "run_shell", """{"command":"echo no-confirm"}"""));
        server.SseBody = MockOpenAiServer.BuildSse(["已执行。"]);

        var h = await CreateAsync(server, new UserPreferences { ConfirmDangerousTools = false });
        await using var _ = h.Workspace;
        await UseDangerousAgentAsync(h);

        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.Empty(h.ViewModel.Items.OfType<ToolConfirmationViewModel>());
        Assert.Equal(
            ToolInvocationStatusPayload.Succeeded,
            h.ViewModel.Items.OfType<ToolCallViewModel>().Single().Status);
    }

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

        Assert.Contains("2条", h.ViewModel.HeaderStatistics);
        Assert.Contains("300", h.ViewModel.HeaderStatistics);
    }

    [Fact]
    public async Task 空会话不显示标题栏统计()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        // 新会话显示"0条 · 0 · 0.0s"只是噪音
        Assert.False(h.ViewModel.ShouldShowHeaderStatistics);

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        Assert.True(h.ViewModel.ShouldShowHeaderStatistics);
    }

    [Fact]
    public async Task 关闭统计偏好后标题栏统计一并隐藏()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var preferences = new UserPreferences { ShowStatistics = false };
        var h = await CreateAsync(server, preferences);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        // 该偏好声明控制"标题栏与消息"，两处都必须生效
        Assert.False(h.ViewModel.ShouldShowHeaderStatistics);
        Assert.False(MessagesOf(h.ViewModel)[1].ShouldShowStatistics);
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
    public async Task 导出为Markdown包含标题智能体与消息正文()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["你好，世界"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.InputText = "帮我写一个快速排序";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        var markdown = h.ViewModel.ExportAsMarkdown();

        // 会话已按首条消息自动命名，标题要跟着走
        Assert.Contains("# 帮我写一个快速排序", markdown);
        Assert.Contains("通用助手", markdown);
        Assert.Contains("帮我写一个快速排序", markdown);
        Assert.Contains("你好，世界", markdown);
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

    // ---------------- 会话内的模型选择 ----------------

    [Fact]
    public async Task 会话内选择模型会覆盖供应商默认并作用于请求()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        // 未覆盖时跟随供应商的默认模型
        Assert.Equal("gpt-4o-mini", h.ViewModel.EffectiveModel);
        Assert.False(h.ViewModel.HasModelOverride);

        h.ViewModel.ModelOverride = "gpt-4o";
        Assert.Equal("gpt-4o", h.ViewModel.EffectiveModel);
        Assert.True(h.ViewModel.HasModelOverride);
        Assert.Equal("gpt-4o", h.ViewModel.ModelButtonText);

        h.ViewModel.InputText = "问题";
        await h.ViewModel.SendCommand.ExecuteAsync(null);

        // 请求体里要真的换成新模型，而不是只在按钮上换了个字样
        var request = server.RequestBodies[^1];
        Assert.Contains("gpt-4o", request);
        Assert.DoesNotContain("gpt-4o-mini", request);
    }

    [Fact]
    public async Task 跟随默认后回到供应商默认模型()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.ModelOverride = "gpt-4o";

        Assert.True(h.ViewModel.ResetModelCommand.CanExecute(null));
        h.ViewModel.ResetModelCommand.Execute(null);

        Assert.Null(h.ViewModel.ModelOverride);
        Assert.False(h.ViewModel.HasModelOverride);
        Assert.Equal("gpt-4o-mini", h.ViewModel.EffectiveModel);
    }

    [Fact]
    public async Task 切换供应商后可用模型列表跟着刷新()
    {
        await using var server = MockOpenAiServer.Start();
        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        h.ViewModel.Provider = new ProviderConfig
        {
            Endpoint = server.BaseUrl,
            ApiKey = "sk-test",
            DefaultModel = "gpt-4o-mini",
            Models = ["gpt-4o-mini", "gpt-4o", "gpt-4o-mini"],
        };

        // 去重后保持原有顺序
        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o" }, h.ViewModel.AvailableModels);
        Assert.Equal("gpt-4o-mini", h.ViewModel.ModelButtonText);

        // 默认模型被配在了列表之外时也要补进去，
        // 否则界面上"正在用哪个模型"在下拉里找不到对应项
        h.ViewModel.Provider = new ProviderConfig
        {
            Endpoint = server.BaseUrl,
            ApiKey = "sk-test",
            DefaultModel = "o3-mini",
            Models = ["gpt-4o"],
        };

        Assert.Equal(new[] { "o3-mini", "gpt-4o" }, h.ViewModel.AvailableModels);
        Assert.Equal("o3-mini", h.ViewModel.EffectiveModel);
    }
}
