namespace Piable.Models;

/// <summary>
/// 一次工具调用的持久化形式，序列化后存入 Role 为 <see cref="MessageRole.Tool"/> 的消息正文。
///
/// 之所以不把工具调用塞进助手消息的文本，是因为重新加载历史时需要在界面上还原出
/// 独立的工具调用条目（名称、参数、结果、是否被拒绝），纯文本无法可靠地反解出来。
/// </summary>
public sealed class ToolCallPayload
{
    /// <summary>模型实际调用的工具名（含 MCP 服务器前缀）。</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>展示用名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>来源说明，如「技能 · 执行命令」。</summary>
    public string SourceLabel { get; set; } = string.Empty;

    public ToolRisk Risk { get; set; }

    public ToolInvocationStatusPayload Status { get; set; }

    /// <summary>模型给出的参数，已摊平成可读文本。</summary>
    public string ArgumentsText { get; set; } = string.Empty;

    /// <summary>回填给模型的结果文本。</summary>
    public string ResultPayload { get; set; } = string.Empty;

    public long DurationMs { get; set; }
}

/// <summary>
/// 工具调用结局的持久化副本。
/// 与 <c>Piable.Services.ToolInvocationStatus</c> 取值一致，但分开定义是为了让
/// 领域模型不依赖服务层——存储层不应该反过来引用编排逻辑。
/// </summary>
public enum ToolInvocationStatusPayload
{
    Succeeded,
    Failed,
    Denied,
}
