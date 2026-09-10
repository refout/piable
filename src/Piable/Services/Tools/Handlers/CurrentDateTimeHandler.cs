using System.Globalization;
using Piable.Models;

namespace Piable.Services.Tools.Handlers;

/// <summary>
/// 返回当前日期与时间。
/// 模型本身不知道"现在"是什么时候，这是最常被需要、又完全无副作用的一个工具。
/// </summary>
public sealed class CurrentDateTimeHandler : ISkillHandler
{
    public string Key => "current_datetime";

    public string DisplayName => "当前时间";

    public ToolRisk Risk => ToolRisk.Safe;

    public string SuggestedDescription => "获取当前的日期与时间。当用户询问现在几点、今天几号、星期几时使用。";

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
        var culture = CultureInfo.GetCultureInfo("zh-CN");

        var text = $"""
            当前时间：{now.ToString("yyyy-MM-dd HH:mm:ss", culture)}
            星期：{now.ToString("dddd", culture)}
            时区：{now.ToString("zzz", culture)}（{TimeZoneInfo.Local.DisplayName}）
            UTC 时间：{now.ToUniversalTime():yyyy-MM-dd HH:mm:ss}
            """;

        return Task.FromResult(text);
    }
}
