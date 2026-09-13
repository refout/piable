using Piable.Models;
using Piable.Services.Tools;

namespace Piable.Tests.Tools;

/// <summary>
/// 智能体挂载资源的解析。用假的 MCP 客户端，把重点放在"拼出来的上下文长什么样"
/// 与"部分失败时如何降级"上，而不是真实连接。
/// </summary>
public class AgentResourceServiceTests
{
    /// <summary>按 URI 给出固定内容的假客户端；未登记的 URI 抛连接异常。</summary>
    private sealed class FakeMcp : IMcpClientService
    {
        private readonly Dictionary<string, string> _contents;
        public readonly List<string> ReadCalls = [];

        public FakeMcp(Dictionary<string, string> contents) => _contents = contents;

        public Task<IReadOnlyList<ToolDescriptor>> GetToolsAsync(
            McpServerConfig config, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);

        public Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(
            McpServerConfig config, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([]);

        public Task<McpResourceContent> ReadResourceAsync(
            McpServerConfig config, string uri, CancellationToken ct = default)
        {
            ReadCalls.Add(uri);

            if (!_contents.TryGetValue(uri, out var text))
            {
                throw new McpConnectionException($"读取资源失败：找不到 {uri}");
            }

            return Task.FromResult(new McpResourceContent
            {
                Uri = uri,
                MimeType = "text/plain",
                Text = text,
            });
        }

        public Task<IReadOnlyList<McpPromptDescriptor>> GetPromptsAsync(
            McpServerConfig config, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([]);

        public Task<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
            McpServerConfig config, string promptName,
            IReadOnlyDictionary<string, string?>? arguments = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPromptMessage>>([]);

        public Task InvalidateAsync(string serverId) => Task.CompletedTask;

        public bool IsConnected(string serverId) => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Harness(TestWorkspace Workspace, AgentResourceService Service, FakeMcp Mcp);

    private static async Task<Harness> CreateAsync(Dictionary<string, string> contents)
    {
        var workspace = await TestWorkspace.CreateAsync();
        var mcp = new FakeMcp(contents);

        await workspace.McpServers.UpsertAsync(new McpServerConfig
        {
            Id = "m1",
            Name = "资料服务器",
            Transport = McpTransport.Stdio,
            Command = "whatever",
            Enabled = true,
        });

        var services = TestServices.Create(workspace);
        var service = new AgentResourceService(mcp, services.Config);

        return new Harness(workspace, service, mcp);
    }

    private static Agent AgentWith(params string[] uris) => new()
    {
        Id = "a1",
        Name = "资料助手",
        McpServerIds = ["m1"],
        McpResourceUris = [.. uris],
    };

    [Fact]
    public async Task 未挂载资源时不产生上下文()
    {
        var h = await CreateAsync([]);
        await using var _ = h.Workspace;

        var resolution = await h.Service.ResolveAsync(new Agent { Id = "a1" });

        Assert.Equal(string.Empty, resolution.Text);
        Assert.Empty(h.Mcp.ReadCalls);
    }

    [Fact]
    public async Task 挂载的资源被读成带标题的上下文()
    {
        var h = await CreateAsync(new Dictionary<string, string>
        {
            ["file:///a.md"] = "这是 A 的内容",
            ["file:///b.md"] = "这是 B 的内容",
        });
        await using var _ = h.Workspace;

        var resolution = await h.Service.ResolveAsync(AgentWith("file:///a.md", "file:///b.md"));

        Assert.Contains("# 已挂载的 MCP 资源", resolution.Text);
        Assert.Contains("## file:///a.md", resolution.Text);
        Assert.Contains("这是 A 的内容", resolution.Text);
        Assert.Contains("这是 B 的内容", resolution.Text);
        Assert.Empty(resolution.Warnings);
    }

    [Fact]
    public async Task 单个资源读失败只产生警告_其余资源照常带上()
    {
        var h = await CreateAsync(new Dictionary<string, string> { ["file:///a.md"] = "A 的内容" });
        await using var _ = h.Workspace;

        var resolution = await h.Service.ResolveAsync(AgentWith("file:///missing.md", "file:///a.md"));

        Assert.Contains("A 的内容", resolution.Text);
        var warning = Assert.Single(resolution.Warnings);
        Assert.Contains("file:///missing.md", warning);
    }

    [Fact]
    public async Task 超长资源被截断并注明原文长度()
    {
        var h = await CreateAsync(new Dictionary<string, string>
        {
            ["file:///big.md"] = new string('x', 20000),
        });
        await using var _ = h.Workspace;

        var resolution = await h.Service.ResolveAsync(AgentWith("file:///big.md"));

        Assert.Contains("已截断", resolution.Text);
        Assert.Contains("20000", resolution.Text);
        Assert.True(resolution.Text.Length < 10000);
    }

    [Fact]
    public async Task 没有关联服务器时不会去连任何东西()
    {
        var h = await CreateAsync(new Dictionary<string, string> { ["file:///a.md"] = "A" });
        await using var _ = h.Workspace;

        // 智能体没勾选这台服务器，资源无从读取
        var agent = new Agent { Id = "a1", McpResourceUris = ["file:///a.md"] };
        var resolution = await h.Service.ResolveAsync(agent);

        Assert.Equal(string.Empty, resolution.Text);
        Assert.Empty(h.Mcp.ReadCalls);
        Assert.Contains("没有可用的来源服务器", Assert.Single(resolution.Warnings));
    }
}
