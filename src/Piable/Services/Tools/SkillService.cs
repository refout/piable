using Piable.Helpers;
using Piable.Models;
using Piable.Services.Storage;

namespace Piable.Services.Tools;

/// <summary>技能的增删改查与工具化。</summary>
public interface ISkillService
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SkillDefinition>> GetAllAsync(CancellationToken ct = default);

    Task<SkillDefinition?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>保存技能。工具名或处理器非法时抛出 <see cref="ArgumentException"/>。</summary>
    Task SaveAsync(SkillDefinition skill, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>把已启用的技能解析成可交给模型的工具。</summary>
    IReadOnlyList<ToolDescriptor> ResolveTools(IEnumerable<SkillDefinition> skills);
}

/// <inheritdoc />
public sealed class SkillService : ISkillService
{
    /// <summary>内置技能的固定 ID，升级时可识别并保留用户对它们的修改。</summary>
    public const string DateTimeSkillId = "builtin-datetime";
    public const string ShellSkillId = "builtin-shell";

    private readonly SkillRepository _repository;
    private readonly ISkillHandler[] _handlers;

    public SkillService(SkillRepository repository, IEnumerable<ISkillHandler>? handlers = null)
    {
        _repository = repository;
        _handlers = handlers?.ToArray() ?? [.. SkillHandlers.All];
    }

    /// <summary>首次运行时写入内置技能。已存在的不覆盖，保留用户的修改。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var existing = await _repository.GetAllAsync(ct).ConfigureAwait(false);
        var existingIds = existing.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var handler in _handlers)
        {
            var id = BuiltInIdFor(handler);
            if (id is null || existingIds.Contains(id))
            {
                continue;
            }

            await _repository.UpsertAsync(new SkillDefinition
            {
                Id = id,
                Name = handler.DisplayName,
                ToolName = handler.Key,
                Description = handler.SuggestedDescription,
                ToolSpec = handler.SuggestedToolSpec,
                Handler = handler.Key,
                Enabled = true,
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetAllAsync(CancellationToken ct = default) =>
        await _repository.GetAllAsync(ct).ConfigureAwait(false);

    public Task<SkillDefinition?> GetAsync(string id, CancellationToken ct = default) =>
        _repository.GetAsync(id, ct);

    public Task SaveAsync(SkillDefinition skill, CancellationToken ct = default)
    {
        if (!ToolNaming.IsValid(skill.ToolName))
        {
            throw new ArgumentException(
                Loc.Get("Skill.InvalidToolName", skill.ToolName, ToolNaming.MaxLength),
                nameof(skill));
        }

        if (_handlers.All(h => !string.Equals(h.Key, skill.Handler, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(Loc.Get("Skill.UnknownHandlerKey", skill.Handler), nameof(skill));
        }

        skill.UpdatedAt = DateTimeOffset.Now;
        return _repository.UpsertAsync(skill, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _repository.DeleteAsync(id, ct);

    public IReadOnlyList<ToolDescriptor> ResolveTools(IEnumerable<SkillDefinition> skills)
    {
        var result = new List<ToolDescriptor>();

        foreach (var skill in skills.Where(s => s.Enabled))
        {
            var handler = _handlers.FirstOrDefault(
                h => string.Equals(h.Key, skill.Handler, StringComparison.OrdinalIgnoreCase));

            // 处理器被移除后遗留的技能定义：跳过，而不是让整轮对话起不来
            if (handler is null || !ToolNaming.IsValid(skill.ToolName))
            {
                continue;
            }

            result.Add(new ToolDescriptor
            {
                Tool = new SkillAIFunction(skill, handler),
                Name = skill.ToolName,
                Source = ToolSource.Skill,
                Risk = handler.Risk,
                SourceLabel = Loc.Get("Tool.SourceLabel", skill.Name),
                Description = skill.Description,
                // 展示名用技能名称（通常是中文），工具名才是给模型看的函数名
                OriginalName = skill.Name,
            });
        }

        return result;
    }

    private static string? BuiltInIdFor(ISkillHandler handler) => handler.Key switch
    {
        "current_datetime" => DateTimeSkillId,
        "run_shell" => ShellSkillId,
        _ => null,
    };
}
