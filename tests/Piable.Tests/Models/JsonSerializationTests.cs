using System.Text.Json;
using Piable.Models;

namespace Piable.Tests.Models;

/// <summary>
/// 验证 JSON 源生成器路径可用。
/// 项目关闭了 STJ 的反射回退，因此这些测试同时充当"是否误用反射序列化"的回归防线：
/// 一旦有代码走不通源生成器，序列化会在运行期抛异常，而测试会先一步捕获。
/// </summary>
public class JsonSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = PiableJsonContext.Default,
    };

    [Fact]
    public void 反射序列化已被禁用_误用时会抛异常()
    {
        // 不使用 TypeInfoResolver 时，STJ 会尝试反射；由于项目禁用了反射回退，
        // 这里必须失败。若本测试开始通过，说明 csproj 中的开关被改掉了。
        var ex = Record.Exception(() => JsonSerializer.Serialize(new Agent()));
        Assert.NotNull(ex);
    }

    [Fact]
    public void 会话可以完整往返_含消息与统计字段()
    {
        var session = new ChatSession
        {
            Id = "s1",
            Title = "关于AI的讨论",
            ProviderId = "openai",
            AgentId = "agent-general",
            ModelUsed = "gpt-4o-mini",
            AgentSnapshot = "{\"name\":\"通用助手\"}",
        };
        session.Messages.Add(new ChatMessage
        {
            Id = "m1",
            Role = MessageRole.User,
            Content = "你好",
        });
        session.Messages.Add(new ChatMessage
        {
            Id = "m2",
            Role = MessageRole.Assistant,
            Content = "你好！我是 AI 助手。",
            DurationMs = 2340,
            PromptTokens = 45,
            CompletionTokens = 156,
            TotalTokens = 201,
            EstimatedCost = 0.0003m,
            ModelUsed = "gpt-4o-mini",
        });

        var json = JsonSerializer.Serialize(session, PiableJsonContext.Default.ChatSession);
        var back = JsonSerializer.Deserialize(json, PiableJsonContext.Default.ChatSession);

        Assert.NotNull(back);
        Assert.Equal(session.Title, back.Title);
        Assert.Equal(2, back.Messages.Count);
        Assert.Equal(MessageRole.User, back.Messages[0].Role);
        Assert.Equal(MessageRole.Assistant, back.Messages[1].Role);
        Assert.Equal(201, back.Messages[1].TotalTokens);
        Assert.Equal(0.0003m, back.Messages[1].EstimatedCost);
        Assert.Equal(2340, back.Messages[1].DurationMs);
    }

    [Theory]
    [InlineData(McpTransport.Stdio, "\"Stdio\"")]
    [InlineData(McpTransport.Sse, "\"Sse\"")]
    [InlineData(McpTransport.StreamableHttp, "\"StreamableHttp\"")]
    public void 枚举以稳定的成员名序列化_不受驼峰命名策略影响(McpTransport transport, string expected)
    {
        var json = JsonSerializer.Serialize(
            new McpServerConfig { Transport = transport }, PiableJsonContext.Default.McpServerConfig);

        Assert.Contains(expected, json);
    }

    [Theory]
    [InlineData(MessageRole.User)]
    [InlineData(MessageRole.Assistant)]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.Tool)]
    public void 消息角色可往返(MessageRole role)
    {
        var json = JsonSerializer.Serialize(
            new ChatMessage { Role = role }, PiableJsonContext.Default.ChatMessage);
        var back = JsonSerializer.Deserialize(json, PiableJsonContext.Default.ChatMessage);

        Assert.Equal(role, back!.Role);
    }

    [Fact]
    public void 智能体可往返_含工具关联列表()
    {
        var agent = new Agent
        {
            Name = "代码助手",
            SystemPrompt = "你是一个代码助手",
            McpServerIds = ["mcp-1", "mcp-2"],
            SkillIds = ["skill-code"],
            Temperature = 0.2,
            IsBuiltIn = false,
        };

        var back = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(agent, PiableJsonContext.Default.Agent),
            PiableJsonContext.Default.Agent);

        Assert.Equal("代码助手", back!.Name);
        Assert.Equal(["mcp-1", "mcp-2"], back.McpServerIds);
        Assert.Equal(["skill-code"], back.SkillIds);
        Assert.Equal(0.2, back.Temperature);
        Assert.True(back.HasTools);
    }

    [Fact]
    public void 空值字段被省略_减小快照体积()
    {
        var json = JsonSerializer.Serialize(new Agent(), PiableJsonContext.Default.Agent);
        Assert.DoesNotContain("\"description\"", json);
    }

    [Fact]
    public void 属性名使用驼峰命名()
    {
        var json = JsonSerializer.Serialize(new UserPreferences(), PiableJsonContext.Default.UserPreferences);
        Assert.Contains("\"showStatistics\"", json);
        Assert.Contains("\"defaultAgentId\"", json);
    }

    [Fact]
    public void 用户偏好默认值符合设计文档()
    {
        var p = new UserPreferences();
        Assert.True(p.ShowStatistics);
        Assert.True(p.ShowCost);
        Assert.False(p.ShowDetailedTokens);
        Assert.False(p.ExpandStatisticsByDefault);
        Assert.False(p.LeftPanelCollapsed);
        Assert.Equal("USD", p.Currency);
        Assert.Equal("System", p.Theme);
        Assert.Equal("zh-CN", p.Language);
    }

    [Fact]
    public void 会话摘要属性_由消息聚合得出()
    {
        var session = new ChatSession();
        Assert.True(session.IsEmpty);

        session.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, TotalTokens = 100, EstimatedCost = 0.001m });
        session.Messages.Add(new ChatMessage { Role = MessageRole.User });

        Assert.False(session.IsEmpty);
        Assert.Equal(100, session.TotalTokens);
        Assert.Equal(0.001m, session.TotalCost);
    }
}
