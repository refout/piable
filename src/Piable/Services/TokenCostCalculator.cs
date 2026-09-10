using System.Globalization;

namespace Piable.Services;

/// <summary>Token 统计与费用的计算及展示格式。</summary>
public interface ITokenCostCalculator
{
    /// <summary>
    /// 按设计文档 9.1 的公式估算费用：
    /// <c>(输入 Tokens / 1000) × 输入单价 + (输出 Tokens / 1000) × 输出单价</c>。
    /// 任一输入缺失时返回 null（界面显示为 <c>--</c>），而不是当作 0 处理——
    /// "未知"与"免费"是两种不同的含义。
    /// </summary>
    decimal? CalculateCost(
        int? promptTokens,
        int? completionTokens,
        decimal inputPricePer1K,
        decimal outputPricePer1K);

    /// <summary>按设计文档 9.4 的规则格式化费用。</summary>
    string FormatCost(decimal? cost, string currency = "USD");

    /// <summary>格式化 Token 数量，超过一千时缩写为 2.3K 形式。</summary>
    string FormatTokens(long? tokens);

    /// <summary>格式化耗时，如 8.7s。</summary>
    string FormatDuration(long? milliseconds);
}

/// <inheritdoc />
public sealed class TokenCostCalculator : ITokenCostCalculator
{
    public decimal? CalculateCost(
        int? promptTokens,
        int? completionTokens,
        decimal inputPricePer1K,
        decimal outputPricePer1K)
    {
        // Token 数完全未知时无法估算
        if (promptTokens is null && completionTokens is null)
        {
            return null;
        }

        var input = (promptTokens ?? 0) / 1000m * inputPricePer1K;
        var output = (completionTokens ?? 0) / 1000m * outputPricePer1K;

        return input + output;
    }

    public string FormatCost(decimal? cost, string currency = "USD")
    {
        if (cost is null)
        {
            return "--";
        }

        var symbol = string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase) ? "¥" : "$";
        var value = cost.Value;

        // 负值不应出现；真出现了说明上游算错了，按未知处理更诚实
        if (value < 0)
        {
            return "--";
        }

        return value switch
        {
            < 0.0001m => $"< {symbol}0.0001",
            < 1m => symbol + value.ToString("F5", CultureInfo.InvariantCulture),
            _ => symbol + value.ToString("F2", CultureInfo.InvariantCulture),
        };
    }

    public string FormatTokens(long? tokens)
    {
        if (tokens is null)
        {
            return "--";
        }

        var value = tokens.Value;

        return value switch
        {
            < 1_000 => value.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        };
    }

    public string FormatDuration(long? milliseconds)
    {
        if (milliseconds is null)
        {
            return "--";
        }

        var value = milliseconds.Value;

        if (value < 60_000)
        {
            return (value / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        var minutes = value / 60_000;
        var seconds = (value % 60_000) / 1000;
        return seconds == 0
            ? $"{minutes}m"
            : $"{minutes}m{seconds}s";
    }
}
