using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>
/// 技能的本地实现。
///
/// 采用"处理器注册表"而非让用户自带脚本：技能的定义（名称、描述、JSON Schema）
/// 由用户在界面上填写，但真正执行的动作必须是本程序内置的、可审计的实现。
/// 这样"模型请求了什么"与"实际会执行什么"之间的对应关系是确定的。
/// </summary>
public interface ISkillHandler
{
    /// <summary>处理器标识，写入 <see cref="SkillDefinition.Handler"/>。</summary>
    string Key { get; }

    /// <summary>界面上展示的名称。</summary>
    string DisplayName { get; }

    /// <summary>风险等级，决定是否需要智能体显式授权。</summary>
    ToolRisk Risk { get; }

    /// <summary>建议的工具描述，新建技能时作为初值填入。</summary>
    string SuggestedDescription { get; }

    /// <summary>建议的参数 JSON Schema，新建技能时作为初值填入。</summary>
    string SuggestedToolSpec { get; }

    /// <summary>
    /// 执行技能。返回值会被原样回填给模型，因此应当是模型可读的文本。
    /// 执行失败时应抛出 <see cref="SkillExecutionException"/>，
    /// 由调用方转成错误结果反馈给模型，而不是让整轮对话失败。
    /// </summary>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
}

/// <summary>技能执行失败。信息会作为工具结果回填给模型。</summary>
public sealed class SkillExecutionException : Exception
{
    public SkillExecutionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
