namespace Piable.Models;

/// <summary>
/// 智能体：对话的核心配置单元。每个智能体持有一套系统提示词与可选工具，
/// 并可就模型与采样参数覆盖供应商默认值。
/// </summary>
public sealed class Agent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = "新智能体";

    public string? Description { get; set; }

    /// <summary>系统提示词内容。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>指定模型；为 null 表示沿用供应商的默认模型。</summary>
    public string? Model { get; set; }

    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public double? TopP { get; set; }

    /// <summary>关联的 MCP 服务器 ID 列表。</summary>
    public List<string> McpServerIds { get; set; } = [];

    /// <summary>关联的技能 ID 列表。</summary>
    public List<string> SkillIds { get; set; } = [];

    /// <summary>是否为全局默认智能体。</summary>
    public bool IsDefault { get; set; }

    /// <summary>是否内置。内置智能体不可删除。</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>是否配置了任何工具（MCP 或技能）。无工具时对话退化为单轮流式生成。</summary>
    public bool HasTools => McpServerIds.Count > 0 || SkillIds.Count > 0;
}
