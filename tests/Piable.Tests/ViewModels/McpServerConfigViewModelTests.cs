using System.Text.Json;
using Microsoft.Extensions.AI;
using Piable.Models;
using Piable.Services.Tools;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

/// <summary>
/// MCP 服务器页上的"查看服务器内容"：工具、资源、提示模板。
/// 用假的 MCP 客户端，重点是列表内容、详情展示与切换服务器时的清理。
/// </summary>
public class McpServerConfigViewModelTests
{
    /// <summary>带参数 Schema 的假工具，用于验证参数概览与 Schema 展示。</summary>
    private sealed class StubFunction : AIFunction
    {
        public StubFunction(string name, string? description, string schemaJson)
        {
            Name = name;
            Description = description ?? string.Empty;
            using var document = JsonDocument.Parse(schemaJson);
            Schema = document.RootElement.Clone();
        }

        public override string Name { get; }

        public override string Description { get; }

        public override JsonElement JsonSchema => Schema;

        private JsonElement Schema { get; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new((object?)"ok");
    }

    /// <summary>按配置返回固定内容的假客户端。</summary>
    private sealed class FakeMcp : IMcpClientService
    {
        public readonly List<string> Calls = [];

        public IReadOnlyList<ToolDescriptor> Tools { get; set; } = [];

        public IReadOnlyList<McpResourceDescriptor> Resources { get; set; } = [];

        public IReadOnlyList<McpPromptDescriptor> Prompts { get; set; } = [];

        public Task<IReadOnlyList<ToolDescriptor>> GetToolsAsync(
            McpServerConfig config, CancellationToken ct = default)
        {
            Calls.Add("tools");
            return Task.FromResult(Tools);
        }

        public Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(
            McpServerConfig config, CancellationToken ct = default)
        {
            Calls.Add("resources");
            return Task.FromResult(Resources);
        }

        public Task<McpResourceContent> ReadResourceAsync(
            McpServerConfig config, string uri, CancellationToken ct = default) =>
            Task.FromResult(new McpResourceContent { Uri = uri, MimeType = "text/plain", Text = "内容" });

        public Task<IReadOnlyList<McpPromptDescriptor>> GetPromptsAsync(
            McpServerConfig config, CancellationToken ct = default)
        {
            Calls.Add("prompts");
            return Task.FromResult(Prompts);
        }

        public Task<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
            McpServerConfig config, string promptName,
            IReadOnlyDictionary<string, string?>? arguments = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPromptMessage>>([]);

        public Task InvalidateAsync(string serverId) => Task.CompletedTask;

        public bool IsConnected(string serverId) => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Harness(TestWorkspace Workspace, TestServices Services,
        McpServerConfigViewModel ViewModel, FakeMcp Mcp);

    private static async Task<Harness> CreateAsync(FakeMcp mcp, params string[] serverNames)
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        foreach (var name in serverNames)
        {
            await services.Config.SaveMcpServerAsync(new McpServerConfig
            {
                Name = name,
                Transport = McpTransport.Stdio,
                Command = "fake-mcp",
                Enabled = true,
            });
        }

        var vm = new McpServerConfigViewModel(services.Config, new StubStatusReporter(), mcp);
        await vm.LoadAsync();

        return new Harness(workspace, services, vm, mcp);
    }

    /// <summary>编辑器是异步加载的（选中服务器后发一个后台任务），没有同步的完成信号。</summary>
    private static async Task WaitForEditorAsync(McpServerConfigViewModel vm, string expectedName)
    {
        var waited = 0;
        while (vm.Name != expectedName && waited < 3000)
        {
            await Task.Delay(20);
            waited += 20;
        }

        Assert.Equal(expectedName, vm.Name);
    }

    private static ToolDescriptor Tool(
        string name, string? originalName, ToolRisk risk, string schemaJson) =>
        new()
        {
            Tool = new StubFunction(name, "读取文件内容", schemaJson),
            Name = name,
            OriginalName = originalName,
            Source = ToolSource.Mcp,
            Risk = risk,
            SourceLabel = "MCP · 本地工具服务",
            Description = "读取文件内容",
        };

    private const string FileSchema = """
        {"type":"object","properties":{"path":{"type":"string"},"recursive":{"type":"boolean"}},"required":["path"]}
        """;

    [Fact]
    public async Task 读取工具清单并给出参数概览()
    {
        var mcp = new FakeMcp
        {
            Tools =
            [
                Tool("mcp_local_read_file", "read_file", ToolRisk.Safe, FileSchema),
                Tool("mcp_local_run", "run", ToolRisk.Dangerous, FileSchema),
            ],
        };

        var h = await CreateAsync(mcp, "本地工具服务");
        await using var _ = h.Workspace;
        await WaitForEditorAsync(h.ViewModel, "本地工具服务");

        await h.ViewModel.LoadToolsCommand.ExecuteAsync(null);

        Assert.Equal(2, h.ViewModel.Tools.Count);
        Assert.True(h.ViewModel.HasTools);

        var readFile = h.ViewModel.Tools[0];
        Assert.Equal("read_file", readFile.DisplayName);
        Assert.True(readFile.ShowBothNames);
        Assert.Equal("只读", readFile.RiskLabel);
        Assert.Contains("path*：string", readFile.ParameterSummary);
        Assert.Contains("recursive：boolean", readFile.ParameterSummary);

        var run = h.ViewModel.Tools[1];
        Assert.Equal("危险", run.RiskLabel);
    }

    [Fact]
    public async Task 选中工具后能拿到完整参数Schema()
    {
        var mcp = new FakeMcp { Tools = [Tool("read_file", null, ToolRisk.Safe, FileSchema)] };

        var h = await CreateAsync(mcp, "本地工具服务");
        await using var _ = h.Workspace;
        await WaitForEditorAsync(h.ViewModel, "本地工具服务");

        await h.ViewModel.LoadToolsCommand.ExecuteAsync(null);

        var tool = h.ViewModel.Tools.Single();
        Assert.True(tool.HasSchema);

        // 缩进后的 JSON，关键字段都在即可，不比对逐字符排版
        Assert.Contains("\"path\"", tool.SchemaText);
        Assert.Contains("\"required\"", tool.SchemaText);
        Assert.False(tool.ShowBothNames);
    }

    [Fact]
    public async Task 全部读取一次拉齐三类内容()
    {
        var mcp = new FakeMcp
        {
            Tools = [Tool("read_file", null, ToolRisk.Safe, FileSchema)],
            Resources =
            [
                new McpResourceDescriptor
                {
                    Uri = "file:///readme.md",
                    Name = "说明",
                    ServerId = "s1",
                    ServerName = "本地工具服务",
                },
            ],
            Prompts =
            [
                new McpPromptDescriptor
                {
                    Name = "summarize",
                    Description = "总结",
                    ServerId = "s1",
                    ServerName = "本地工具服务",
                },
            ],
        };

        var h = await CreateAsync(mcp, "本地工具服务");
        await using var _ = h.Workspace;
        await WaitForEditorAsync(h.ViewModel, "本地工具服务");

        await h.ViewModel.LoadAllCommand.ExecuteAsync(null);

        Assert.Equal(["tools", "resources", "prompts"], mcp.Calls);
        Assert.Single(h.ViewModel.Tools);
        Assert.Single(h.ViewModel.Resources);
        Assert.Single(h.ViewModel.Prompts);
    }

    [Fact]
    public async Task 切换服务器清空上一台的浏览结果()
    {
        var mcp = new FakeMcp { Tools = [Tool("read_file", null, ToolRisk.Safe, FileSchema)] };

        var h = await CreateAsync(mcp, "第一台", "第二台");
        await using var _ = h.Workspace;

        await WaitForEditorAsync(h.ViewModel, "第一台");
        await h.ViewModel.LoadToolsCommand.ExecuteAsync(null);
        Assert.Single(h.ViewModel.Tools);

        h.ViewModel.SelectedServer = h.ViewModel.Servers[1];
        await WaitForEditorAsync(h.ViewModel, "第二台");

        // 不清的话，界面上会同时混着两台服务器的工具与资源
        Assert.Empty(h.ViewModel.Tools);
        Assert.Empty(h.ViewModel.Resources);
        Assert.Empty(h.ViewModel.Prompts);
        Assert.Null(h.ViewModel.SelectedTool);
    }

    [Fact]
    public async Task 未保存的服务器读取时提示先保存()
    {
        var mcp = new FakeMcp { Tools = [Tool("read_file", null, ToolRisk.Safe, FileSchema)] };

        var h = await CreateAsync(mcp, "本地工具服务");
        await using var _ = h.Workspace;
        await WaitForEditorAsync(h.ViewModel, "本地工具服务");

        h.ViewModel.NewServerCommand.Execute(null);
        await h.ViewModel.LoadToolsCommand.ExecuteAsync(null);

        Assert.Contains("请先保存", h.ViewModel.StatusMessage);
        Assert.Empty(h.ViewModel.Tools);
        Assert.Empty(mcp.Calls);
    }
}
