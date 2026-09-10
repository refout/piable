using System.Text.Json;
using Piable.Models;
using Piable.Services;

namespace Piable.Tests.Services;

/// <summary>
/// 端到端验证流式对话链路：真实的 OpenAI SDK 打到本地 mock 服务端。
/// 这一层同时覆盖了"禁用 STJ 反射回退"是否会影响第三方 SDK 的序列化。
/// </summary>
public class AgentOrchestratorTests
{
    private static ProviderConfig ProviderFor(MockOpenAiServer server) => new()
    {
        Id = "p1",
        PresetId = ProviderPresets.OpenAi,
        ApiKey = "sk-test-key",
        Endpoint = server.BaseUrl,
        DefaultModel = "gpt-4o-mini",
        Models = ["gpt-4o-mini"],
    };

    private static Agent TestAgent() => new()
    {
        Id = "a1",
        Name = "通用助手",
        SystemPrompt = "你是一个测试助手",
    };

    private static ChatRequest RequestFor(
        MockOpenAiServer server, Agent agent, IEnumerable<ChatMessage>? history = null) => new()
    {
        Provider = ProviderFor(server),
        Agent = agent,
        History = history?.ToList() ?? [new ChatMessage { Role = MessageRole.User, Content = "你好" }],
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
    public async Task 流式文本按增量拼接成完整回答()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["你好", "！我是", " AI 助手。"]);

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        var (text, _) = await CollectAsync(orchestrator, RequestFor(server, TestAgent()));

        Assert.Equal("你好！我是 AI 助手。", text);
    }

    [Fact]
    public async Task 采集到供应商返回的usage()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["回答"], promptTokens: 45, completionTokens: 156);

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        var (_, chunks) = await CollectAsync(orchestrator, RequestFor(server, TestAgent()));

        var usage = chunks.Single(c => c.Usage is not null).Usage!;
        Assert.Equal(45, usage.PromptTokens);
        Assert.Equal(156, usage.CompletionTokens);
        Assert.Equal(201, usage.TotalTokens);
    }

    [Fact]
    public async Task 请求发送到chat_completions且带Bearer鉴权()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["ok"]);

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        await CollectAsync(orchestrator, RequestFor(server, TestAgent()));

        Assert.Equal("/chat/completions", server.LastPath);
        Assert.Equal("Bearer sk-test-key", server.LastAuthorization);
    }

    [Fact]
    public async Task 系统提示词与历史消息一并发出()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["ok"]);

        var agent = TestAgent();
        var history = new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = "第一个问题" },
            new() { Role = MessageRole.Assistant, Content = "第一个回答" },
            new() { Role = MessageRole.User, Content = "第二个问题" },
        };

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        await CollectAsync(orchestrator, RequestFor(server, agent, history));

        var body = JsonDocument.Parse(server.RequestBodies.Single());
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(4, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("你是一个测试助手", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("第一个问题", messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("第二个问题", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task 采样参数被发送到请求体()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["ok"]);

        var request = RequestFor(server, TestAgent()) with
        {
            Temperature = 0.3,
            MaxTokens = 512,
            TopP = 0.8,
        };

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        await CollectAsync(orchestrator, request);

        var body = JsonDocument.Parse(server.RequestBodies.Single()).RootElement;

        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.Equal(0.8, body.GetProperty("top_p").GetDouble(), precision: 3);

        // OpenAI SDK 2.x 发送的是 max_completion_tokens 而非旧的 max_tokens。
        // 这是 SDK 的行为，不是本项目可选的；对只认 max_tokens 的兼容端点
        // 可能被忽略甚至报错，已在 README 中记为已知兼容性风险。
        Assert.Equal(512, body.GetProperty("max_completion_tokens").GetInt32());

        // 流式请求必须带上 include_usage，否则部分供应商不会在末尾汇报 token 用量
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task 智能体指定的模型覆盖供应商默认模型()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["ok"]);

        var agent = TestAgent();
        agent.Model = "gpt-4o";

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        await CollectAsync(orchestrator, RequestFor(server, agent));

        var body = JsonDocument.Parse(server.RequestBodies.Single());
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task 无系统提示词时不发送system消息()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["ok"]);

        var agent = TestAgent();
        agent.SystemPrompt = string.Empty;

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        await CollectAsync(orchestrator, RequestFor(server, agent));

        var body = JsonDocument.Parse(server.RequestBodies.Single());
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Single(messages);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task 供应商返回401时抛出异常()
    {
        await using var server = MockOpenAiServer.Start();
        server.StatusCode = 401;
        server.SseBody = """{"error":{"message":"Invalid API key"}}""";

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectAsync(orchestrator, RequestFor(server, TestAgent())));

        // 状态码要能被错误映射识别为认证失败
        Assert.Contains("认证失败", ChatErrorMapper.ToUserMessage(ex));
    }

    [Fact]
    public async Task 供应商返回429时映射为配额提示()
    {
        await using var server = MockOpenAiServer.Start();
        server.StatusCode = 429;
        server.SseBody = """{"error":{"message":"Rate limit reached"}}""";

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectAsync(orchestrator, RequestFor(server, TestAgent())));

        Assert.Contains("配额", ChatErrorMapper.ToUserMessage(ex));
    }

    [Fact]
    public async Task 用户取消时抛出取消异常()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["这段内容不会被读完"]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(orchestrator, RequestFor(server, TestAgent()), cts.Token));
    }

    [Fact]
    public async Task 取消异常被映射为静默处理()
    {
        await using var server = MockOpenAiServer.Start();
        server.SseBody = MockOpenAiServer.BuildSse(["x"]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(orchestrator, RequestFor(server, TestAgent()), cts.Token));

        // 用户主动点"停止"是正常操作，不应弹出错误提示
        Assert.Null(ChatErrorMapper.ToUserMessage(ex, userCancelled: true));
    }

    [Fact]
    public async Task 测试连接成功时返回模型名()
    {
        await using var server = MockOpenAiServer.Start();
        // 测试连接走的是非流式调用，服务端需返回普通 JSON 而非 SSE
        server.JsonBody = MockOpenAiServer.BuildCompletionJson(model: "gpt-4o-mini");

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        var result = await orchestrator.TestConnectionAsync(ProviderFor(server), "gpt-4o-mini");

        Assert.True(result.Success, result.Message);
        Assert.Contains("gpt-4o-mini", result.Message);
    }

    [Fact]
    public async Task 测试连接失败时返回可读提示()
    {
        await using var server = MockOpenAiServer.Start();
        server.StatusCode = 401;
        server.SseBody = """{"error":{"message":"bad key"}}""";

        var orchestrator = new AgentOrchestrator(new ChatClientFactory());
        var result = await orchestrator.TestConnectionAsync(ProviderFor(server), "gpt-4o-mini");

        Assert.False(result.Success);
        Assert.Contains("API Key", result.Message);
    }

    [Fact]
    public async Task 测试连接时未选模型会直接给出提示()
    {
        var provider = new ProviderConfig { Id = "p1", PresetId = ProviderPresets.OpenAi };
        var orchestrator = new AgentOrchestrator(new ChatClientFactory());

        var result = await orchestrator.TestConnectionAsync(provider, model: null);

        Assert.False(result.Success);
        Assert.Contains("模型", result.Message);
    }

    [Fact]
    public void 无Endpoint且预设也缺失时抛出可读异常()
    {
        var provider = new ProviderConfig { Id = "p1", PresetId = "不存在的预设" };
        var factory = new ChatClientFactory();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(provider, "m"));
        Assert.Contains("Endpoint", ex.Message);
    }

    [Fact]
    public void 模型解析遵循智能体优先于供应商的顺序()
    {
        var factory = new ChatClientFactory();
        var provider = new ProviderConfig
        {
            Id = "p1",
            PresetId = ProviderPresets.OpenAi,
            DefaultModel = "gpt-4o-mini",
        };

        Assert.Equal("gpt-4o-mini", factory.ResolveModel(provider, agent: null));
        Assert.Equal("gpt-4o-mini", factory.ResolveModel(provider, new Agent()));

        var agent = new Agent { Model = "gpt-4o" };
        Assert.Equal("gpt-4o", factory.ResolveModel(provider, agent));
    }

    [Fact]
    public void 用户覆盖的Endpoint优先于预设默认值()
    {
        var factory = new ChatClientFactory();

        var withOverride = new ProviderConfig
        {
            PresetId = ProviderPresets.OpenAi,
            Endpoint = "https://custom.example.com/v1",
        };
        Assert.Equal("https://custom.example.com/v1", factory.ResolveEndpoint(withOverride));

        var withoutOverride = new ProviderConfig { PresetId = ProviderPresets.OpenAi };
        Assert.Equal("https://api.openai.com/v1", factory.ResolveEndpoint(withoutOverride));
    }
}
