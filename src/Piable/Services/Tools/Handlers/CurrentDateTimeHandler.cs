using System.Globalization;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Tools.Handlers;

/// <summary>
/// 返回当前日期与时间。
/// 模型本身不知道"现在"是什么时候，这是最常被需要、又完全无副作用的一个工具。
/// </summary>
public sealed class CurrentDateTimeHandler : ISkillHandler
{
    public string Key => "current_datetime";

    public string DisplayName => Loc.Get("Skill.CurrentDateTime.Name");

    public ToolRisk Risk => ToolRisk.Safe;

    public string SuggestedDescription => Loc.Get("Skill.CurrentDateTime.Description");

    public string SuggestedToolSpec => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.Now;

        // 跟随界面语言而不是写死 zh-CN：工具结果直接摆在对话里，
        // 界面已经切成英文了这里还蹦出"星期：星期三"会很突兀。
        var culture = CultureInfo.CurrentUICulture;

        var text = string.Join(Environment.NewLine,
            Loc.Get("Skill.CurrentDateTime.Now", now.ToString("yyyy-MM-dd HH:mm:ss", culture)),
            Loc.Get("Skill.CurrentDateTime.Weekday", now.ToString("dddd", culture)),
            Loc.Get("Skill.CurrentDateTime.TimeZone",
                now.ToString("zzz", culture), TimeZoneInfo.Local.DisplayName),
            Loc.Get("Skill.CurrentDateTime.Utc",
                now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", culture)));

        return Task.FromResult(text);
    }
}
