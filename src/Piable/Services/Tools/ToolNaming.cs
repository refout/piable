using System.Text;
using System.Text.RegularExpressions;

namespace Piable.Services.Tools;

/// <summary>
/// 工具名的校验与推导。
///
/// OpenAI 兼容接口要求函数名匹配 <c>^[a-zA-Z0-9_-]{1,64}$</c>，
/// 而本应用面向中文用户，技能名称多半是中文——直接拿名称当工具名会被供应商拒绝，
/// 因此工具名必须单独维护并在此处校验。
/// </summary>
public static partial class ToolNaming
{
    public const int MaxLength = 64;

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && Pattern().IsMatch(name);

    /// <summary>由任意文本推导出合法工具名，供界面预填。结果保证通过 <see cref="IsValid"/>。</summary>
    public static string Suggest(string source, string fallback)
    {
        var builder = new StringBuilder(source.Length);

        foreach (var ch in source.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        var candidate = Collapse(builder.ToString()).Trim('_');
        if (candidate.Length > MaxLength)
        {
            candidate = candidate[..MaxLength].Trim('_');
        }

        // 纯中文名称清洗后会变成空串，或首字符是数字/连字符，此时退回处理器标识
        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            var fallbackCandidate = Collapse(fallback.ToLowerInvariant()).Trim('_');
            return IsValid(fallbackCandidate) ? fallbackCandidate : "tool";
        }

        return candidate;
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasUnderscore = false;

        foreach (var ch in value)
        {
            if (ch == '_')
            {
                if (!lastWasUnderscore)
                {
                    builder.Append(ch);
                }

                lastWasUnderscore = true;
                continue;
            }

            builder.Append(ch);
            lastWasUnderscore = false;
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex Pattern();
}
