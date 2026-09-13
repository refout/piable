namespace Piable.Models;

/// <summary>单条对话消息，并承载该次生成的统计信息。</summary>
public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>模型的推理/思考内容（扩展思考模式）。不保证有值——取决于模型是否返回。</summary>
    public string? ThinkingContent { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    // ---- 以下统计字段仅助手消息会填充 ----

    /// <summary>请求发出的时刻。</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>响应结束的时刻。</summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>耗时（毫秒）。首字延迟与总耗时都由此派生。</summary>
    public long? DurationMs { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>按供应商单价估算的费用（美元）。无法计算时为 null。</summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>本次实际使用的模型名。</summary>
    public string? ModelUsed { get; set; }

    /// <summary>是否被用户中断。</summary>
    public bool IsInterrupted { get; set; }
}
