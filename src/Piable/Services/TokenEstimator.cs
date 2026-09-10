namespace Piable.Services;

/// <summary>
/// 在供应商未返回 usage 时按内容长度粗略估算 Token（设计文档 7.5 的最后兜底）。
/// 结果仅用于让统计栏不至于空着，误差可能相当大，因此界面会标注为估算值。
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// 估算文本的 Token 数。
    /// CJK 字符按 1 字符 ≈ 1 token 计；其余按 4 字符 ≈ 1 token 计——
    /// 这两条经验对不同分词器的偏差方向不同，但都优于"一刀切除以 4"。
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjkCount = 0;
        var otherCount = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjkCount++;
            }
            else
            {
                otherCount++;
            }
        }

        return cjkCount + (int)Math.Ceiling(otherCount / 4.0);
    }

    private static bool IsCjk(char ch) => ch switch
    {
        >= '一' and <= '鿿' => true,   // 中日韩统一表意文字
        >= '㐀' and <= '䶿' => true,   // 扩展 A 区
        >= '　' and <= '〿' => true,   // 中日韩符号与标点
        >= '぀' and <= 'ヿ' => true,   // 平假名与片假名
        >= '가' and <= '힯' => true,   // 谚文音节
        >= '＀' and <= '￯' => true,   // 全角字符
        _ => false,
    };
}
