using Piable.Services.Tools;

namespace Piable.Tests.Tools;

public class ToolNamingTests
{
    [Theory]
    [InlineData("run_shell")]
    [InlineData("current-datetime")]
    [InlineData("a")]
    [InlineData("Tool123")]
    [InlineData("mcp_server_search")]
    public void 合法工具名通过校验(string name)
    {
        Assert.True(ToolNaming.IsValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("执行命令")]          // 中文
    [InlineData("run shell")]        // 空格
    [InlineData("run.shell")]        // 点号
    [InlineData("run/shell")]        // 斜杠
    public void 非法工具名被拒绝(string? name)
    {
        Assert.False(ToolNaming.IsValid(name));
    }

    [Fact]
    public void 超过六十四字符被拒绝()
    {
        Assert.False(ToolNaming.IsValid(new string('a', 65)));
        Assert.True(ToolNaming.IsValid(new string('a', 64)));
    }

    [Theory]
    [InlineData("执行命令", "run_shell", "run_shell")]
    [InlineData("Current DateTime", "fallback", "current_datetime")]
    [InlineData("MCP: 本地服务", "fallback", "mcp_本地服务")] // 中文片段被保留为下划线后清洗
    public void 由名称推导工具名(string source, string fallback, string expectedPrefix)
    {
        var result = ToolNaming.Suggest(source, fallback);

        Assert.True(ToolNaming.IsValid(result), $"推导结果 {result} 应当是合法工具名");
        Assert.StartsWith(expectedPrefix[..Math.Min(3, expectedPrefix.Length)], result);
    }

    [Fact]
    public void 纯中文名称退回处理器标识()
    {
        // 中文清洗后什么都不剩，必须退回一个有意义的标识而不是空串
        var result = ToolNaming.Suggest("执行命令", "run_shell");

        Assert.Equal("run_shell", result);
        Assert.True(ToolNaming.IsValid(result));
    }

    [Fact]
    public void 连续非法字符折叠为单个下划线()
    {
        var result = ToolNaming.Suggest("a   b___c", "fallback");

        Assert.Equal("a_b_c", result);
    }

    [Fact]
    public void 首字符为数字时退回处理器标识()
    {
        // OpenAI 要求函数名以字母或下划线开头
        var result = ToolNaming.Suggest("123 tool", "run_shell");

        Assert.Equal("run_shell", result);
    }

    [Fact]
    public void 过长的推导结果被截断且仍然合法()
    {
        var result = ToolNaming.Suggest(new string('a', 200), "fallback");

        Assert.True(result.Length <= ToolNaming.MaxLength);
        Assert.True(ToolNaming.IsValid(result));
    }

    [Fact]
    public void 推导结果始终合法()
    {
        string[] sources = ["", "  ", "!!!", "中文完全", "a", "A B C", "x" + new string('!', 100)];

        foreach (var source in sources)
        {
            Assert.True(ToolNaming.IsValid(ToolNaming.Suggest(source, "fallback")), $"来源「{source}」");
        }
    }
}
