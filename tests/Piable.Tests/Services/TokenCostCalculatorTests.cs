using Piable.Services;

namespace Piable.Tests.Services;

public class TokenCostCalculatorTests
{
    private readonly TokenCostCalculator _calculator = new();

    [Theory]
    // 设计文档 9.1 的公式：(输入/1000)×输入单价 + (输出/1000)×输出单价
    [InlineData(1000, 1000, 0.005, 0.015, 0.020)]
    [InlineData(45, 156, 0.0025, 0.01, 0.0016725)]
    [InlineData(0, 0, 0.005, 0.015, 0.0)]
    public void 按公式计算费用(
        int prompt, int completion, double inputPrice, double outputPrice, double expected)
    {
        var cost = _calculator.CalculateCost(prompt, completion, (decimal)inputPrice, (decimal)outputPrice);

        Assert.NotNull(cost);
        Assert.Equal((decimal)expected, cost.Value, precision: 7);
    }

    [Fact]
    public void 本地免费模型费用为零而不是未知()
    {
        var cost = _calculator.CalculateCost(1000, 1000, 0m, 0m);

        Assert.Equal(0m, cost);
    }

    [Fact]
    public void Token数完全未知时返回null()
    {
        // "未知"与"免费"必须区分开：前者显示 --，后者显示 $0.00000
        Assert.Null(_calculator.CalculateCost(null, null, 0.005m, 0.015m));
    }

    [Fact]
    public void 只知输出Token时按已知部分计算()
    {
        var cost = _calculator.CalculateCost(null, 1000, 0.005m, 0.015m);

        Assert.Equal(0.015m, cost);
    }

    [Theory]
    [InlineData(null, "USD", "--")]
    [InlineData(0.00005, "USD", "< $0.0001")]
    [InlineData(0.0001, "USD", "$0.00010")]
    [InlineData(0.0003, "USD", "$0.00030")]
    [InlineData(0.5, "USD", "$0.50000")]
    [InlineData(1.0, "USD", "$1.00")]
    [InlineData(12.345, "USD", "$12.35")]
    [InlineData(0.0003, "CNY", "¥0.00030")]
    [InlineData(0.00005, "CNY", "< ¥0.0001")]
    public void 费用按设计文档的区间格式化(double? cost, string currency, string expected)
    {
        var formatted = _calculator.FormatCost(cost is null ? null : (decimal)cost.Value, currency);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void 负费用视为未知()
    {
        Assert.Equal("--", _calculator.FormatCost(-1m));
    }

    // xUnit 会把 InlineData 里的整数字面量装箱为 int，无法赋给 long? 形参，
    // 因此这里必须显式写 L 后缀。
    [Theory]
    [InlineData(null, "--")]
    [InlineData(0L, "0")]
    [InlineData(201L, "201")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1K")]
    [InlineData(2300L, "2.3K")]
    [InlineData(2300000L, "2.3M")]
    public void Token数量按千位缩写(long? tokens, string expected)
    {
        Assert.Equal(expected, _calculator.FormatTokens(tokens));
    }

    [Theory]
    [InlineData(null, "--")]
    [InlineData(0L, "0.0s")]
    [InlineData(8700L, "8.7s")]
    [InlineData(59000L, "59.0s")]
    [InlineData(60000L, "1m")]
    [InlineData(65000L, "1m5s")]
    [InlineData(125000L, "2m5s")]
    public void 耗时格式化(long? milliseconds, string expected)
    {
        Assert.Equal(expected, _calculator.FormatDuration(milliseconds));
    }
}
