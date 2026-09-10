using Piable.Services.Tools.Handlers;

namespace Piable.Services.Tools;

/// <summary>
/// 内置技能处理器注册表。
/// 新增一个内置技能只需实现 <see cref="ISkillHandler"/> 并加到这里。
/// </summary>
public static class SkillHandlers
{
    public static IReadOnlyList<ISkillHandler> All { get; } =
    [
        new CurrentDateTimeHandler(),
        new RunShellHandler(),
    ];

    /// <summary>按标识查找处理器；找不到返回 null（技能可能引用了已移除的处理器）。</summary>
    public static ISkillHandler? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
}
