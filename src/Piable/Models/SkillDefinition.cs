namespace Piable.Models;

/// <summary>
/// 技能：以 JSON Schema 描述、由本地实现执行的自定义工具。
/// 首版仅定义数据结构与持久化，执行由后续阶段的 SkillService 实现。
/// </summary>
public sealed class SkillDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = "新技能";

    /// <summary>技能描述，会作为工具说明提供给模型。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>工具的 JSON Schema 定义（参数部分）。</summary>
    public string ToolSpec { get; set; } = "{}";

    /// <summary>本地执行入口标识，由 SkillService 解析为具体实现。</summary>
    public string Handler { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
