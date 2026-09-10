using Piable.Models;
using Piable.Services.Tools;
using Piable.Services.Tools.Handlers;

namespace Piable.Tests.Tools;

public class SkillHandlerTests
{
    private static readonly Dictionary<string, object?> NoArgs = [];

    [Fact]
    public async Task 当前时间返回可读的时间信息()
    {
        var handler = new CurrentDateTimeHandler();

        var result = await handler.ExecuteAsync(NoArgs, CancellationToken.None);

        Assert.Contains("当前时间：", result);
        Assert.Contains("星期：", result);
        Assert.Contains("UTC 时间：", result);
        Assert.Contains(DateTimeOffset.Now.ToString("yyyy-MM-dd"), result);
    }

    [Fact]
    public void 当前时间被标记为安全()
    {
        // 读时间没有任何副作用，不应要求用户授权
        Assert.Equal(ToolRisk.Safe, new CurrentDateTimeHandler().Risk);
    }

    [Fact]
    public void 执行命令被标记为危险()
    {
        // 这是权限最高的操作，必须默认需要授权
        Assert.Equal(ToolRisk.Dangerous, new RunShellHandler().Risk);
    }

    [Fact]
    public async Task 执行命令返回退出码与输出()
    {
        var handler = new RunShellHandler();
        var command = OperatingSystem.IsWindows() ? "echo hello" : "echo hello";

        var result = await handler.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = command }, CancellationToken.None);

        Assert.Contains("退出码：0", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task 非零退出码如实返回()
    {
        var handler = new RunShellHandler();
        var command = OperatingSystem.IsWindows() ? "exit 3" : "exit 3";

        var result = await handler.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = command }, CancellationToken.None);

        Assert.Contains("退出码：3", result);
    }

    [Fact]
    public async Task 缺少命令参数时抛出可读错误()
    {
        var handler = new RunShellHandler();

        var ex = await Assert.ThrowsAsync<SkillExecutionException>(
            () => handler.ExecuteAsync(NoArgs, CancellationToken.None));

        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public async Task 命令超时被终止并报错()
    {
        var handler = new RunShellHandler();
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";

        var ex = await Assert.ThrowsAsync<SkillExecutionException>(
            () => handler.ExecuteAsync(
                new Dictionary<string, object?> { ["command"] = command, ["timeout_seconds"] = 1 },
                CancellationToken.None));

        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task 用户取消时抛取消异常而非技能错误()
    {
        var handler = new RunShellHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 取消是控制流，不该被包装成"技能执行失败"回填给模型
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.ExecuteAsync(
                new Dictionary<string, object?> { ["command"] = "echo x" }, cts.Token));
    }

    [Fact]
    public async Task 参数中的数字以字符串形式给出时仍可解析()
    {
        // 模型偶尔会把数字写成字符串，不该因此让一次调用失败
        var handler = new RunShellHandler();

        var result = await handler.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["command"] = "echo ok",
                ["timeout_seconds"] = "5",
            },
            CancellationToken.None);

        Assert.Contains("ok", result);
    }

    [Fact]
    public async Task 超长输出被截断()
    {
        var handler = new RunShellHandler();
        // 生成远超上限的输出
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,5000) do @echo 0123456789012345678901234567890123456789"
            : "seq 1 5000";

        var result = await handler.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = command }, CancellationToken.None);

        Assert.Contains("已截断", result);
        Assert.True(result.Length < 12000, $"结果长度 {result.Length} 应当被限制");
    }

    [Fact]
    public void 每个处理器都给出了建议的描述与参数结构()
    {
        foreach (var handler in SkillHandlers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(handler.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(handler.SuggestedDescription));
            Assert.False(string.IsNullOrWhiteSpace(handler.SuggestedToolSpec));
            Assert.True(ToolNaming.IsValid(handler.Key), $"处理器标识 {handler.Key} 应当是合法工具名");
        }
    }

    [Fact]
    public void 处理器标识不重复()
    {
        var keys = SkillHandlers.All.Select(h => h.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 按标识查找处理器()
    {
        Assert.NotNull(SkillHandlers.Find("run_shell"));
        Assert.NotNull(SkillHandlers.Find("RUN_SHELL"));
        Assert.Null(SkillHandlers.Find("nonexistent"));
        Assert.Null(SkillHandlers.Find(null));
    }
}
