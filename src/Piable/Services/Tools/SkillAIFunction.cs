using System.Text.Json;
using Microsoft.Extensions.AI;
using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>
/// 把一个技能定义包装成模型可调用的工具。
///
/// 名称与参数结构来自用户的配置，实际动作来自注册表中的处理器——
/// 模型看到的"工具"与真正执行的东西因此是一一对应的，不存在配置里写 A、
/// 实际执行 B 的空间。
/// </summary>
public sealed class SkillAIFunction : AIFunction
{
    private readonly ISkillHandler _handler;
    private readonly JsonElement _schema;

    public SkillAIFunction(SkillDefinition skill, ISkillHandler handler)
    {
        _handler = handler;
        Name = skill.ToolName;
        Description = skill.Description;
        _schema = ParseSchema(skill.ToolSpec);
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // AIFunctionArguments 的取值可能来自 JSON 反序列化，统一摊平成只读字典后
        // 交给处理器；处理器只关心"有哪些参数、值是什么"。
        var flattened = arguments.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return await _handler.ExecuteAsync(flattened, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析用户填写的参数 Schema。解析失败时退化为"无参数对象"而不是抛异常——
    /// 一个技能的定义写坏了，不应该让整个智能体的对话都起不来。
    /// </summary>
    private static JsonElement ParseSchema(string? toolSpec)
    {
        if (!string.IsNullOrWhiteSpace(toolSpec))
        {
            try
            {
                using var document = JsonDocument.Parse(toolSpec);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 落到下面的默认 Schema
            }
        }

        using var fallback = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return fallback.RootElement.Clone();
    }
}
