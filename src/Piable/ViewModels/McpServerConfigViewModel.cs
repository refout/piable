using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.ViewModels;

/// <summary>MCP 服务器管理页中的一行。</summary>
public sealed partial class McpServerListItemViewModel : InlineConfirmViewModel
{
    [ObservableProperty]
    private bool _isSelected;

    public McpServerListItemViewModel(McpServerConfig server)
    {
        Id = server.Id;
        _name = server.Name;
        _transport = server.Transport;
        _isEnabled = server.Enabled;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private McpTransport _transport;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>启用状态由界面图标（CheckmarkCircle/Circle）表达，名字单独成串。</summary>
    public string DisplayName => Name;

    /// <summary>禁用态，供界面显示灰色圆点图标。</summary>
    public bool IsDisabled => !IsEnabled;

    public string Subtitle => Transport switch
    {
        McpTransport.Stdio => Loc.Get("Mcp.TransportStdio"),
        McpTransport.Sse => "SSE",
        _ => "Streamable HTTP",
    };

    public void Update(McpServerConfig server)
    {
        Name = server.Name;
        Transport = server.Transport;
        IsEnabled = server.Enabled;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>工具浏览列表中的一项。</summary>
public sealed partial class McpToolItemViewModel : ViewModelBase
{
    public McpToolItemViewModel(ToolDescriptor tool)
    {
        Name = tool.Name;
        DisplayName = string.IsNullOrWhiteSpace(tool.OriginalName) ? tool.Name : tool.OriginalName!;
        ShowBothNames = !string.IsNullOrWhiteSpace(tool.OriginalName)
                        && !string.Equals(tool.OriginalName, tool.Name, StringComparison.Ordinal);
        Description = tool.Description;
        IsDangerous = tool.Risk == ToolRisk.Dangerous;

        var schema = (tool.Tool as AIFunction)?.JsonSchema;
        SchemaText = schema is null ? string.Empty : Prettify(schema.Value);
        ParameterSummary = schema is null ? Loc.Get("Mcp.NoParameterInfo") : DescribeParameters(schema.Value);
    }

    /// <summary>模型实际调用时使用的名字。</summary>
    public string Name { get; }

    /// <summary>给人看的名字（MCP 服务端上的原始名）。</summary>
    public string DisplayName { get; }

    /// <summary>两个名字不一致时（加了来源前缀）才需要同时显示，否则是重复信息。</summary>
    public bool ShowBothNames { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsDangerous { get; }

    public string RiskLabel => IsDangerous ? Loc.Get("Mcp.RiskDangerous") : Loc.Get("Mcp.RiskReadOnly");

    /// <summary>参数概览，如 <c>path*：string、recursive：boolean</c>。</summary>
    public string ParameterSummary { get; }

    /// <summary>完整参数 Schema（缩进后的 JSON）。</summary>
    public string SchemaText { get; }

    public bool HasSchema => SchemaText.Length > 0;

    private static string DescribeParameters(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return Loc.Get("Common.NoParameters");
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredArray)
            && requiredArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        var parts = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            var type = property.Value.ValueKind == JsonValueKind.Object
                       && property.Value.TryGetProperty("type", out var typeElement)
                       && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : "any";

            parts.Add(required.Contains(property.Name)
                ? Loc.Get("Common.ParamEntryRequired", property.Name, type)
                : Loc.Get("Common.ParamEntry", property.Name, type));
        }

        return parts.Count == 0
            ? Loc.Get("Common.NoParameters")
            : string.Join(Loc.Get("Common.ListSeparator"), parts);
    }

    /// <summary>把 Schema 缩进后输出。用 Utf8JsonWriter 而不是序列化器，避免触发反射路径。</summary>
    private static string Prettify(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>资源浏览列表中的一项。</summary>
public sealed partial class McpResourceItemViewModel : ViewModelBase
{
    public McpResourceItemViewModel(McpResourceDescriptor resource)
    {
        Uri = resource.Uri;
        Name = resource.DisplayName;
        Subtitle = string.IsNullOrWhiteSpace(resource.MimeType)
            ? resource.Uri
            : $"{resource.MimeType} · {resource.Uri}";
        Description = resource.Description;
        IsTemplate = resource.IsTemplate;
    }

    public string Uri { get; }

    public string Name { get; }

    public string Subtitle { get; }

    public string? Description { get; }

    /// <summary>模板的 URI 带占位符，读不出来，界面上要标清楚。</summary>
    public bool IsTemplate { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>提示模板列表中的一项。</summary>
public sealed partial class McpPromptItemViewModel : ViewModelBase
{
    public McpPromptItemViewModel(McpPromptDescriptor prompt)
    {
        Name = prompt.Name;
        DisplayName = prompt.DisplayName;
        Description = prompt.Description;
        Arguments = [.. prompt.Arguments.Select(a => new PromptArgumentInput(
            a.Name, a.Description, a.Required))];
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>参数输入框。每个模板各自持有一份，切换模板时不会被串。</summary>
    public List<PromptArgumentInput> Arguments { get; }

    public string ArgumentSummary => Arguments.Count == 0
        ? Loc.Get("Common.NoParameters")
        : string.Join(Loc.Get("Common.ListSeparator"),
            Arguments.Select(a => a.Required ? $"{a.Name}*" : a.Name));
}

/// <summary>提示模板的一个参数输入框。</summary>
public sealed partial class PromptArgumentInput : ViewModelBase
{
    public PromptArgumentInput(string name, string? description, bool required)
    {
        Name = name;
        Description = description;
        Required = required;
    }

    public string Name { get; }

    public string? Description { get; }

    public bool Required { get; }

    public string Label => Required ? $"{Name} *" : Name;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>「配置 → MCP 服务器」标签页。</summary>
public sealed partial class McpServerConfigViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IStatusReporter _status;
    private readonly IMcpClientService? _mcp;

    private McpServerConfig? _editing;

    [ObservableProperty]
    private McpServerListItemViewModel? _selectedServer;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private McpTransport _transport = McpTransport.Stdio;

    [ObservableProperty]
    private string _command = string.Empty;

    /// <summary>命令行参数，每行一个。比让用户写 JSON 数组直观得多。</summary>
    [ObservableProperty]
    private string _argumentsText = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>请求头，每行一个 <c>键=值</c>。</summary>
    [ObservableProperty]
    private string _headersText = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>工具浏览：当前选中的工具。</summary>
    [ObservableProperty]
    private McpToolItemViewModel? _selectedTool;

    /// <summary>资源浏览：当前选中的资源。</summary>
    [ObservableProperty]
    private McpResourceItemViewModel? _selectedResource;

    /// <summary>资源内容。只读展示，用户自行复制。</summary>
    [ObservableProperty]
    private string _resourceContentText = string.Empty;

    /// <summary>提示浏览：当前选中的提示模板。</summary>
    [ObservableProperty]
    private McpPromptItemViewModel? _selectedPrompt;

    [ObservableProperty]
    private string _promptResultText = string.Empty;

    [ObservableProperty]
    private bool _isBrowsing;

    public McpServerConfigViewModel(
        IConfigService config, IStatusReporter status, IMcpClientService? mcp = null)
    {
        _config = config;
        _status = status;
        _mcp = mcp;
    }

    public ObservableCollection<McpServerListItemViewModel> Servers { get; } = [];

    /// <summary>当前服务器提供的工具。点"读取工具"才拉取。</summary>
    public ObservableCollection<McpToolItemViewModel> Tools { get; } = [];

    /// <summary>当前服务器提供的资源。点"读取资源"才拉取。</summary>
    public ObservableCollection<McpResourceItemViewModel> Resources { get; } = [];

    /// <summary>当前服务器提供的提示模板。点"读取提示"才拉取。</summary>
    public ObservableCollection<McpPromptItemViewModel> Prompts { get; } = [];

    public bool HasTools => Tools.Count > 0;

    public bool HasResources => Resources.Count > 0;

    public bool HasPrompts => Prompts.Count > 0;

    /// <summary>提示模板的参数输入区，随选中的模板重建。</summary>
    public ObservableCollection<PromptArgumentInput> PromptArguments { get; } = [];

    public IReadOnlyList<McpTransport> AvailableTransports { get; } =
        [McpTransport.Stdio, McpTransport.Sse, McpTransport.StreamableHttp];

    public bool HasSelection => SelectedServer is not null;

    public bool IsStdio => Transport == McpTransport.Stdio;

    public bool IsHttpTransport => !IsStdio;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var selectedId = SelectedServer?.Id;

        Servers.Clear();
        foreach (var server in await _config.GetMcpServersAsync(ct).ConfigureAwait(true))
        {
            var item = new McpServerListItemViewModel(server);
            item.DeleteConfirmed += (_, vm) => _ = DeleteAsync(((McpServerListItemViewModel)vm).Id);
            Servers.Add(item);
        }

        SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(McpServerListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        // 换了服务器，上一次浏览出来的资源与提示属于别的服务器，必须一并清掉
        ClearBrowsedContent();

        if (value is null)
        {
            _editing = null;
            return;
        }

        _ = LoadEditorAsync(value.Id);
    }

    private void ClearBrowsedContent()
    {
        Tools.Clear();
        Resources.Clear();
        Prompts.Clear();
        PromptArguments.Clear();
        SelectedTool = null;
        SelectedResource = null;
        SelectedPrompt = null;
        ResourceContentText = string.Empty;
        PromptResultText = string.Empty;

        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasPrompts));
    }

    partial void OnSelectedPromptChanged(McpPromptItemViewModel? value)
    {
        PromptArguments.Clear();
        PromptResultText = string.Empty;

        if (value is null)
        {
            return;
        }

        foreach (var argument in value.Arguments)
        {
            PromptArguments.Add(argument);
        }
    }

    partial void OnTransportChanged(McpTransport value)
    {
        OnPropertyChanged(nameof(IsStdio));
        OnPropertyChanged(nameof(IsHttpTransport));
    }

    private async Task LoadEditorAsync(string id)
    {
        var servers = await _config.GetMcpServersAsync().ConfigureAwait(true);
        var server = servers.FirstOrDefault(s => s.Id == id);
        if (server is null)
        {
            return;
        }

        _editing = new McpServerConfig
        {
            Id = server.Id,
            Name = server.Name,
            Transport = server.Transport,
            Command = server.Command,
            Args = [.. server.Args],
            Url = server.Url,
            Headers = new Dictionary<string, string>(server.Headers, StringComparer.Ordinal),
            Enabled = server.Enabled,
            CreatedAt = server.CreatedAt,
        };

        Name = server.Name;
        Transport = server.Transport;
        Command = server.Command ?? string.Empty;
        ArgumentsText = string.Join(Environment.NewLine, server.Args);
        Url = server.Url ?? string.Empty;
        HeadersText = string.Join(
            Environment.NewLine,
            server.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
        IsEnabled = server.Enabled;
        StatusMessage = string.Empty;
        IsStatusError = false;
    }

    [RelayCommand]
    private void NewServer()
    {
        _editing = null;
        SelectedServer = null;

        Name = Loc.Get("Mcp.NewName");
        Transport = McpTransport.Stdio;
        Command = string.Empty;
        ArgumentsText = string.Empty;
        Url = string.Empty;
        HeadersText = string.Empty;
        IsEnabled = true;
        StatusMessage = string.Empty;
        IsStatusError = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Common.NameRequired");
            return;
        }

        if (IsStdio && string.IsNullOrWhiteSpace(Command))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.StdioCommandRequired");
            return;
        }

        if (IsHttpTransport && string.IsNullOrWhiteSpace(Url))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.HttpUrlRequired");
            return;
        }

        var server = _editing ?? new McpServerConfig();
        server.Name = Name.Trim();
        server.Transport = Transport;
        server.Command = IsStdio ? Command.Trim() : null;
        server.Args = IsStdio ? ParseLines(ArgumentsText) : [];
        server.Url = IsHttpTransport ? Url.Trim() : null;
        server.Headers = IsHttpTransport ? ParseHeaders(HeadersText) : [];
        server.Enabled = IsEnabled;

        IsBusy = true;
        try
        {
            await _config.SaveMcpServerAsync(server).ConfigureAwait(true);

            // 配置变了，旧连接必须作废，否则下次对话仍用着改之前的连接
            if (_mcp is not null)
            {
                await _mcp.InvalidateAsync(server.Id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            IsBusy = false;
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.SaveFailed", ChatErrorMapper.ToUserMessage(ex) ?? ex.Message);
            _status.ReportError(StatusMessage);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        IsStatusError = false;
        StatusMessage = Loc.Get("Common.Saved");
        _status.ReportSuccess(Loc.Get("Mcp.Saved", server.Name));

        await LoadAsync().ConfigureAwait(true);
        SelectedServer = Servers.FirstOrDefault(s => s.Id == server.Id);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        IsBusy = true;
        IsStatusError = false;
        StatusMessage = Loc.Get("Mcp.Connecting");

        var probe = _editing ?? new McpServerConfig();
        probe.Name = Name.Trim();
        probe.Transport = Transport;
        probe.Command = IsStdio ? Command.Trim() : null;
        probe.Args = IsStdio ? ParseLines(ArgumentsText) : [];
        probe.Url = IsHttpTransport ? Url.Trim() : null;
        probe.Headers = IsHttpTransport ? ParseHeaders(HeadersText) : [];

        try
        {
            // 测试用一个临时 Id，避免污染正式连接缓存
            var probeId = probe.Id;
            probe.Id = $"probe-{Guid.NewGuid():n}";
            try
            {
                var tools = await _mcp.GetToolsAsync(probe).ConfigureAwait(true);
                IsStatusError = false;
                StatusMessage = tools.Count == 0
                    ? Loc.Get("Mcp.ConnectedNoTools")
                    : Loc.Get("Mcp.ConnectedToolsFound", tools.Count,
                        string.Join(Loc.Get("Common.ListSeparator"), tools.Select(t => t.OriginalName)));
            }
            finally
            {
                await _mcp.InvalidateAsync(probe.Id).ConfigureAwait(true);
                probe.Id = probeId;
            }
        }
        catch (McpConnectionException ex)
        {
            IsStatusError = true;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Mcp.ConnectFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------- 资源与提示浏览 ----------------

    /// <summary>
    /// 从表单构造一份服务器配置用于浏览。
    /// 与"测试连接"不同，这里用真实 Id：浏览就是要连上这台服务器，
    /// 复用已建立的连接既快又不会多起一个子进程。
    /// </summary>
    private McpServerConfig? BuildBrowsingConfig()
    {
        if (_editing is null)
        {
            return null;
        }

        return new McpServerConfig
        {
            Id = _editing.Id,
            Name = Name.Trim(),
            Transport = Transport,
            Command = IsStdio ? Command.Trim() : null,
            Args = IsStdio ? ParseLines(ArgumentsText) : [],
            Url = IsHttpTransport ? Url.Trim() : null,
            Headers = IsHttpTransport ? ParseHeaders(HeadersText) : [],
            Enabled = true,
        };
    }

    /// <summary>
    /// 一次性读取工具、资源与提示模板。
    /// 逐个卡片点三次太琐碎，而这三者的连接是复用的，一次读全反而更省事。
    /// </summary>
    [RelayCommand]
    private async Task LoadAllAsync()
    {
        await LoadToolsAsync().ConfigureAwait(true);
        await LoadResourcesAsync().ConfigureAwait(true);
        await LoadPromptsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadToolsAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        var config = BuildBrowsingConfig();
        if (config is null)
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.SaveBeforeTools");
            return;
        }

        IsBrowsing = true;
        Tools.Clear();
        SelectedTool = null;
        IsStatusError = false;
        StatusMessage = Loc.Get("Mcp.ReadingTools");

        try
        {
            foreach (var tool in await _mcp.GetToolsAsync(config).ConfigureAwait(true))
            {
                Tools.Add(new McpToolItemViewModel(tool));
            }

            IsStatusError = false;
            StatusMessage = Tools.Count == 0
                ? Loc.Get("Mcp.NoTools")
                : Loc.Get("Mcp.ToolsFound", Tools.Count);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Mcp.ReadToolsFailed", ex.Message);
        }
        finally
        {
            IsBrowsing = false;
            OnPropertyChanged(nameof(HasTools));
        }
    }

    [RelayCommand]
    private async Task LoadResourcesAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        var config = BuildBrowsingConfig();
        if (config is null)
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.SaveBeforeResources");
            return;
        }

        IsBrowsing = true;
        Resources.Clear();
        SelectedResource = null;
        ResourceContentText = string.Empty;
        IsStatusError = false;
        StatusMessage = Loc.Get("Agent.ReadingResources");

        try
        {
            foreach (var resource in await _mcp.GetResourcesAsync(config).ConfigureAwait(true))
            {
                Resources.Add(new McpResourceItemViewModel(resource));
            }

            IsStatusError = false;
            StatusMessage = Resources.Count == 0
                ? Loc.Get("Mcp.NoResources")
                : Loc.Get("Agent.ResourcesFound", Resources.Count);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Mcp.ReadResourcesFailed", ex.Message);
        }
        finally
        {
            IsBrowsing = false;
            OnPropertyChanged(nameof(HasResources));
        }
    }

    [RelayCommand]
    private async Task ReadSelectedResourceAsync()
    {
        if (_mcp is null || SelectedResource is null)
        {
            return;
        }

        var config = BuildBrowsingConfig();
        if (config is null)
        {
            return;
        }

        IsBrowsing = true;
        IsStatusError = false;
        ResourceContentText = Loc.Get("Common.Reading");

        try
        {
            var content = await _mcp
                .ReadResourceAsync(config, SelectedResource.Uri)
                .ConfigureAwait(true);

            ResourceContentText = content.Text;
            StatusMessage = Loc.Get("Mcp.ResourceRead", SelectedResource.Uri);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            ResourceContentText = string.Empty;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Common.ReadFailed", ex.Message);
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    [RelayCommand]
    private async Task LoadPromptsAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        var config = BuildBrowsingConfig();
        if (config is null)
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.SaveBeforePrompts");
            return;
        }

        IsBrowsing = true;
        Prompts.Clear();
        SelectedPrompt = null;
        IsStatusError = false;
        StatusMessage = Loc.Get("Mcp.ReadingPrompts");

        try
        {
            foreach (var prompt in await _mcp.GetPromptsAsync(config).ConfigureAwait(true))
            {
                Prompts.Add(new McpPromptItemViewModel(prompt));
            }

            IsStatusError = false;
            StatusMessage = Prompts.Count == 0
                ? Loc.Get("Mcp.NoPrompts")
                : Loc.Get("Mcp.PromptsFound", Prompts.Count);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Mcp.ReadPromptsFailed", ex.Message);
        }
        finally
        {
            IsBrowsing = false;
            OnPropertyChanged(nameof(HasPrompts));
        }
    }

    [RelayCommand]
    private async Task GetSelectedPromptAsync()
    {
        if (_mcp is null || SelectedPrompt is null)
        {
            return;
        }

        var config = BuildBrowsingConfig();
        if (config is null)
        {
            return;
        }

        IsBrowsing = true;
        IsStatusError = false;
        PromptResultText = Loc.Get("Mcp.Expanding");

        try
        {
            var arguments = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var argument in PromptArguments)
            {
                arguments[argument.Name] = argument.Value;
            }

            var messages = await _mcp
                .GetPromptAsync(config, SelectedPrompt.Name, arguments)
                .ConfigureAwait(true);

            PromptResultText = string.Join(
                Environment.NewLine + Environment.NewLine,
                messages.Select(m => $"[{m.Role}] {m.Text}"));

            StatusMessage = Loc.Get("Mcp.PromptExpanded", SelectedPrompt.DisplayName);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            PromptResultText = string.Empty;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Mcp.ExpandFailed", ex.Message);
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    private async Task DeleteAsync(string id)
    {
        await _config.DeleteMcpServerAsync(id).ConfigureAwait(true);

        if (_mcp is not null)
        {
            await _mcp.InvalidateAsync(id).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
        _status.ReportSuccess(Loc.Get("Mcp.Deleted"));
    }

    private static List<string> ParseLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in ParseLines(text))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return headers;
    }
}
