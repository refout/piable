using Microsoft.Extensions.AI;
using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>
/// 一个可供模型调用的工具，附带调度所需的元数据。
///
/// <see cref="AITool"/> 本身只有名称和描述，不足以支撑"要不要放行这次调用"
/// 以及"在界面上怎么标注它从哪来"，因此把这两件事显式带在描述符上。
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>交给模型的工具本体。</summary>
    public required AITool Tool { get; init; }

    /// <summary>工具名。模型调用时使用的标识，同一轮对话内必须唯一。</summary>
    public required string Name { get; init; }

    public required ToolSource Source { get; init; }

    public required ToolRisk Risk { get; init; }

    /// <summary>界面上展示的来源说明，如「MCP · 本地工具服务」。</summary>
    public required string SourceLabel { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// 展示用的名称：MCP 工具为服务端上的原始名，技能为技能名称。
    /// 与 <see cref="Name"/> 的区别在于后者是给模型调用的函数名（必须符合命名限制），
    /// 而它是给人看的。
    /// </summary>
    public string? OriginalName { get; init; }
}
