using System.Text.Json;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.Tests.Tools;

/// <summary>
/// 工具调用循环的端到端验证：mock 服务端先请求工具、再基于结果给出回答，
/// 覆盖请求构造、工具执行、结果回填、危险工具授权与轮数上限。
/// </summary>
public class ToolLoopTests
{
    private sealed record Harness(
        TestWorkspace Workspace,
        IAgentOrchestrator Orchestrator,
        ProviderConfig Provider,
        IReadOnlyList<ToolDescriptor> Tools);

    private static async Task<Harness> CreateAsync(MockOpenAiServer server, bool includeShell = true)
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        var provider = new ProviderConfig
        {
            Id = "p1",
            PresetId = ProviderPresets.OpenAi,
            ApiKey = "sk-test",
            Endpoint = server.BaseUrl,
            DefaultModel = "gpt-4o-mini",
        };

        var skillIds = new List<string> { SkillService.DateTimeSkillId };
        if (includeShell)
        {
            skillIds.Add(SkillService.ShellSkillId);
        }

        var skills = (await services.Skills.GetAllAsync()).Where(s => skillIds.Contains(s.Id));
        var tools = services.Skills.ResolveTools(skills);

        return new Harness(workspace, services.Orchestrator, provider, tools);
    }

    private static ChatRequest RequestFor(
        Harness h, bool allowDangerous, int maxRounds = 5, string userText = "现在几点了？") => new()
    {
        Provider = h.Provider,
        Agent = new Agent { Id = "a1", SystemPrompt = "你是助手" },
        History = [new ChatMessage { Role = MessageRole.User, Content = userText }],
        Tools = h.Tools,
        AllowDangerousTools = allowDangerous,
        MaxToolRounds = maxRounds,
    };

    private static async Task<(string Text, List<ChatStreamChunk> Chunks)> CollectAsync(
        IAgentOrchestrator orchestrator, ChatRequest request, CancellationToken ct = default)
    {
        var chunks = new List<ChatStreamChunk>();
        var text = string.Empty;

        await foreach (var chunk in orchestrator.StreamAsync(request, ct))
        {
            chunks.Add(chunk);
            text += chunk.TextDelta ?? string.Empty;
        }

        return (text, chunks);
    }

    [Fact]
    public async Task 模型请求工具后执行并把结果回填_再产出最终回答()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.SseBody = MockOpenAiServer.BuildSse(["现在是", "下午三点。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (text, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        // 两轮都产生了文本
        Assert.Contains("现在是", text);

        var toolRecord = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal("current_datetime", toolRecord.ToolName);
        Assert.Equal(ToolInvocationStatus.Succeeded, toolRecord.Status);
        Assert.Contains("当前时间：", toolRecord.ResultPayload);

        // 服务端收到了两次请求
        Assert.Equal(2, server.RequestBodies.Count);
    }

    [Fact]
    public async Task 工具结果以tool角色回填给模型()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.SseBody = MockOpenAiServer.BuildSse(["好的"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        var secondRequest = JsonDocument.Parse(server.RequestBodies[1]).RootElement;
        var messages = secondRequest.GetProperty("messages").EnumerateArray().ToList();

        // system + user + assistant(含 tool_calls) + tool
        Assert.Equal(4, messages.Count);
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Contains("当前时间：", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task 工具定义随请求发出且工具名为合法函数名()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        var body = JsonDocument.Parse(server.RequestBodies[0]).RootElement;
        var tools = body.GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(2, tools.Count);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("function").GetProperty("name").GetString();
            Assert.True(ToolNaming.IsValid(name), $"工具名 {name} 必须是合法函数名");
        }
    }

    [Fact]
    public async Task 未授权时危险工具被拒绝且拒绝原因回填给模型()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse(
            "run_shell", """{"command":"echo should-not-run"}"""));
        server.SseBody = MockOpenAiServer.BuildSse(["我无法执行该操作。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (_, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        var record = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal(ToolInvocationStatus.Denied, record.Status);
        Assert.Contains("未获授权", record.ResultPayload);

        // 拒绝信息要回填给模型，它才能改走别的路子
        var secondRequest = JsonDocument.Parse(server.RequestBodies[1]).RootElement;
        var toolMessage = secondRequest.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("未获授权", toolMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task 已授权时危险工具真正执行()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse(
            "run_shell", """{"command":"echo piable-tool-ran"}"""));
        server.SseBody = MockOpenAiServer.BuildSse(["已执行。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (_, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: true));

        var record = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal(ToolInvocationStatus.Succeeded, record.Status);
        Assert.Contains("piable-tool-ran", record.ResultPayload);
    }

    [Fact]
    public async Task 工具执行失败时错误被回填而不是中断对话()
    {
        await using var server = MockOpenAiServer.Start();
        // 缺少必填参数，处理器会抛 SkillExecutionException
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("run_shell", "{}"));
        server.SseBody = MockOpenAiServer.BuildSse(["工具报错了，我换个方式。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (text, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: true));

        var record = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal(ToolInvocationStatus.Failed, record.Status);
        Assert.Contains("command", record.ResultPayload);

        // 对话继续走到了最终回答
        Assert.Contains("工具报错了", text);
    }

    [Fact]
    public async Task 模型调用未提供的工具时返回错误结果()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("nonexistent_tool"));
        server.SseBody = MockOpenAiServer.BuildSse(["抱歉，我重试。"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (_, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        var record = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal(ToolInvocationStatus.Failed, record.Status);
        Assert.Contains("不存在", record.ResultPayload);
    }

    [Fact]
    public async Task 轮数用尽后最后一轮不带工具_强制模型给出回答()
    {
        await using var server = MockOpenAiServer.Start();

        // 前两轮都请求工具，第三轮才回答
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.SseBody = MockOpenAiServer.BuildSse(["最终回答"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (text, _) = await CollectAsync(
            h.Orchestrator, RequestFor(h, allowDangerous: false, maxRounds: 2));

        Assert.Contains("最终回答", text);

        // 第三次请求必须不带 tools，否则模型可能继续要求调用工具、对话永远结束不了
        var lastRequest = JsonDocument.Parse(server.RequestBodies[^1]).RootElement;
        Assert.False(lastRequest.TryGetProperty("tools", out var _unusedTools));
        Assert.Equal(3, server.RequestBodies.Count);
    }

    [Fact]
    public async Task 无工具时退化为单轮生成()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["直接回答"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var request = RequestFor(h, allowDangerous: false) with { Tools = [] };
        var (text, chunks) = await CollectAsync(h.Orchestrator, request);

        Assert.Contains("直接回答", text);
        Assert.DoesNotContain(chunks, c => c.ToolCall is not null);
        Assert.Single(server.RequestBodies);

        var body = JsonDocument.Parse(server.RequestBodies[0]).RootElement;
        Assert.False(body.TryGetProperty("tools", out var _unusedTools2));
    }

    [Fact]
    public async Task 多次工具调用的用量被累计()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.SseBody = MockOpenAiServer.BuildSse(["回答"], promptTokens: 100, completionTokens: 20);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (_, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        // 只有第二轮带 usage，因此合计即该轮的值
        var usage = chunks.Last(c => c.Usage is not null).Usage!;
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(20, usage.CompletionTokens);
        Assert.Equal(120, usage.TotalTokens);
    }

    [Fact]
    public async Task 工具调用记录带有来源与耗时()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("current_datetime"));
        server.SseBody = MockOpenAiServer.BuildSse(["回答"]);

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        var (_, chunks) = await CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: false));

        var record = chunks.Single(c => c.ToolCall is not null).ToolCall!;
        Assert.Equal(ToolSource.Skill, h.Tools.Single(t => t.Name == record.ToolName).Source);
        Assert.Equal("当前时间", record.DisplayName);
        Assert.Contains("技能", record.SourceLabel);
        Assert.True(record.Duration >= TimeSpan.Zero);
        Assert.Equal(1, record.Iteration);
    }

    [Fact]
    public async Task 用户取消时立即抛出而不继续执行工具()
    {
        await using var server = MockOpenAiServer.Start();
        server.EnqueueResponse(MockOpenAiServer.BuildToolCallSse("run_shell", """{"command":"echo x"}"""));

        var h = await CreateAsync(server);
        await using var _ = h.Workspace;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(h.Orchestrator, RequestFor(h, allowDangerous: true), cts.Token));
    }
}
