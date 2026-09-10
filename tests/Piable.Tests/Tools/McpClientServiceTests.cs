using Microsoft.Extensions.AI;
using Piable.Models;
using Piable.Services.Tools;

namespace Piable.Tests.Tools;

/// <summary>
/// MCP 接入的端到端验证：真实启动一个子进程、走完 JSON-RPC 握手、
/// 发现工具并实际调用。用 mock 掉服务接口的方式测恰好会绕开这一整层。
/// </summary>
public class McpClientServiceTests
{
    /// <summary>测试用的假服务端可执行文件。它由 ProjectReference 复制到测试输出目录。</summary>
    private static string FakeServerPath =>
        Path.Combine(AppContext.BaseDirectory, "FakeMcpServer.exe");

    private static McpServerConfig ServerConfig(string name = "测试服务器") => new()
    {
        Name = name,
        Transport = McpTransport.Stdio,
        Command = FakeServerPath,
        Enabled = true,
    };

    private static async Task<(McpClientService Service, IReadOnlyList<ToolDescriptor> Tools)> ConnectAsync()
    {
        var service = new McpClientService();
        var tools = await service.GetToolsAsync(ServerConfig());
        return (service, tools);
    }

    [Fact]
    public void 假服务端已随测试程序集一起输出()
    {
        // 若这条失败，说明 ProjectReference 没把可执行文件带过来，后续测试都会失败
        Assert.True(File.Exists(FakeServerPath), $"未找到 {FakeServerPath}");
    }

    [Fact]
    public async Task 通过stdio连接并发现工具()
    {
        var (service, tools) = await ConnectAsync();
        await using var _ = service;

        Assert.Equal(2, tools.Count);
        Assert.All(tools, t => Assert.Equal(ToolSource.Mcp, t.Source));
        Assert.All(tools, t => Assert.Equal("MCP · 测试服务器", t.SourceLabel));
    }

    [Fact]
    public async Task 工具名带服务器前缀以避免跨服务器重名()
    {
        var (service, tools) = await ConnectAsync();
        await using var _ = service;

        Assert.Contains(tools, t => t.Name == "测试服务器_echo" || t.Name.EndsWith("_echo", StringComparison.Ordinal));
        Assert.All(tools, t => Assert.True(ToolNaming.IsValid(t.Name), $"工具名 {t.Name} 必须是合法函数名"));
    }

    [Fact]
    public async Task 展示名保留服务端上的原始工具名()
    {
        var (service, tools) = await ConnectAsync();
        await using var _ = service;

        Assert.Contains(tools, t => t.OriginalName == "echo");
        Assert.Contains(tools, t => t.OriginalName == "write_note");
    }

    [Fact]
    public async Task 声明只读的工具判为安全_未声明的一律判为危险()
    {
        var (service, tools) = await ConnectAsync();
        await using var _ = service;

        var echo = tools.Single(t => t.OriginalName == "echo");
        var write = tools.Single(t => t.OriginalName == "write_note");

        // echo 声明了 readOnlyHint
        Assert.Equal(ToolRisk.Safe, echo.Risk);
        // write_note 未声明注解：第三方工具能做什么客户端无从验证，只能按危险处理
        Assert.Equal(ToolRisk.Dangerous, write.Risk);
    }

    [Fact]
    public async Task 发现的工具可实际调用()
    {
        var (service, tools) = await ConnectAsync();
        await using var _ = service;

        var echo = tools.Single(t => t.OriginalName == "echo");
        var function = Assert.IsAssignableFrom<AIFunction>(echo.Tool);

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "你好" }),
            CancellationToken.None);

        Assert.Equal("echo: 你好", result?.ToString());
    }

    [Fact]
    public async Task 重复获取工具复用同一连接()
    {
        await using var service = new McpClientService();
        var config = ServerConfig();

        var first = await service.GetToolsAsync(config);
        Assert.True(service.IsConnected(config.Id));
        var second = await service.GetToolsAsync(config);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task 作废后重新连接()
    {
        await using var service = new McpClientService();
        var config = ServerConfig();

        await service.GetToolsAsync(config);
        Assert.True(service.IsConnected(config.Id));

        await service.InvalidateAsync(config.Id);
        Assert.False(service.IsConnected(config.Id));

        var tools = await service.GetToolsAsync(config);
        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task 未连接时IsConnected为假()
    {
        await using var service = new McpClientService();

        Assert.False(service.IsConnected("任意Id"));
    }

    [Fact]
    public async Task 命令不存在时抛出可读的连接异常()
    {
        await using var service = new McpClientService();
        var config = new McpServerConfig
        {
            Name = "不存在的服务器",
            Transport = McpTransport.Stdio,
            Command = "definitely-not-a-real-command-xyz",
        };

        var ex = await Assert.ThrowsAsync<McpConnectionException>(
            () => service.GetToolsAsync(config));

        Assert.Contains("不存在的服务器", ex.Message);
    }

    [Fact]
    public async Task 缺少启动命令时抛出可读异常()
    {
        await using var service = new McpClientService();
        var config = new McpServerConfig
        {
            Name = "未配置命令",
            Transport = McpTransport.Stdio,
            Command = null,
        };

        var ex = await Assert.ThrowsAsync<McpConnectionException>(
            () => service.GetToolsAsync(config));

        Assert.Contains("启动命令", ex.Message);
    }

    [Fact]
    public async Task URL无效时抛出可读异常()
    {
        await using var service = new McpClientService();
        var config = new McpServerConfig
        {
            Name = "地址错误",
            Transport = McpTransport.StreamableHttp,
            Url = "这不是一个 URL",
        };

        var ex = await Assert.ThrowsAsync<McpConnectionException>(
            () => service.GetToolsAsync(config));

        Assert.Contains("URL", ex.Message);
    }

    [Fact]
    public async Task 连接失败不会被缓存_下次调用会重试()
    {
        await using var service = new McpClientService();
        var config = new McpServerConfig
        {
            Name = "第一次失败",
            Transport = McpTransport.Stdio,
            Command = "definitely-not-a-real-command-xyz",
        };

        await Assert.ThrowsAsync<McpConnectionException>(() => service.GetToolsAsync(config));
        Assert.False(service.IsConnected(config.Id));

        // 改成可用的命令后应当能连上，而不是一直吃上次失败的缓存
        config.Command = FakeServerPath;
        var tools = await service.GetToolsAsync(config);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task 同一服务器并发请求只建立一次连接()
    {
        await using var service = new McpClientService();
        var config = ServerConfig();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => service.GetToolsAsync(config)));

        // 同一个连接对象被复用
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task 释放后拒绝新的调用()
    {
        var service = new McpClientService();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetToolsAsync(ServerConfig()));
    }

    [Fact]
    public async Task 释放服务会一并关闭子进程()
    {
        var service = new McpClientService();
        var tools = await service.GetToolsAsync(ServerConfig());
        Assert.Equal(2, tools.Count);

        // 不断言进程句柄，只验证释放不抛异常——
        // 真正的验证在于释放后机器上不残留 FakeMcpServer 进程
        await service.DisposeAsync();

        Assert.False(service.IsConnected("任意Id"));
    }
}
