using System.Text;

namespace Piable.Services;

/// <summary>
/// 会话自动命名（设计文档 3.3.1）：取首条用户消息的前 20 字，
/// 并清除换行与多余空白，避免侧边栏出现多行标题。
/// </summary>
public static class SessionTitleGenerator
{
    public const string DefaultTitle = "新对话";
    private const int MaxLength = 20;

    public static string Generate(string? firstUserMessage)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return DefaultTitle;
        }

        var normalized = Normalize(firstUserMessage);
        if (normalized.Length == 0)
        {
            return DefaultTitle;
        }

        return normalized.Length <= MaxLength
            ? normalized
            : normalized[..MaxLength] + "…";
    }

    /// <summary>把任意空白（含换行、制表符、全角空格）折叠为单个半角空格。</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch) || ch == '　';

            if (isSpace)
            {
                // 连续空白只保留一个，且不放在开头
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
