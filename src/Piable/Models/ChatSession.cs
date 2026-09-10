using System.Text.Json.Serialization;

namespace Piable.Models;

/// <summary>一次完整的对话会话。</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>对话标题。新建时为"新对话"，首条用户消息后自动命名。</summary>
    public string Title { get; set; } = "新对话";

    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>使用的供应商配置 ID。</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>使用的智能体 ID。</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>智能体配置的 JSON 快照，用于历史回放时还原当时的系统提示词与参数。</summary>
    public string? AgentSnapshot { get; set; }

    /// <summary>实际使用的模型名。</summary>
    public string? ModelUsed { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>是否为尚未产生任何消息的空白会话。</summary>
    [JsonIgnore]
    public bool IsEmpty => Messages.Count == 0;

    /// <summary>会话累计 token（内存态使用；列表查询另有聚合查询）。</summary>
    [JsonIgnore]
    public long TotalTokens => Messages.Sum(m => (long)(m.TotalTokens ?? 0));

    /// <summary>会话累计费用（美元）。</summary>
    [JsonIgnore]
    public decimal TotalCost => Messages.Sum(m => m.EstimatedCost ?? 0m);
}

/// <summary>
/// 会话列表用的轻量摘要。侧边栏只需这些字段，
/// 避免为渲染列表而加载全部消息（对应设计文档 11.1 的懒加载策略）。
/// </summary>
public sealed class ChatSessionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string? ModelUsed { get; set; }
    public int MessageCount { get; set; }
    public long TotalTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
