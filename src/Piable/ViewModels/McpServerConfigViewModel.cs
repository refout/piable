using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public string DisplayName => $"{(IsEnabled ? "🟢" : "⚪")} {Name}";

    public string Subtitle => Transport switch
    {
        McpTransport.Stdio => "Stdio（本地子进程）",
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

    public McpServerConfigViewModel(
        IConfigService config, IStatusReporter status, IMcpClientService? mcp = null)
    {
        _config = config;
        _status = status;
        _mcp = mcp;
    }

    public ObservableCollection<McpServerListItemViewModel> Servers { get; } = [];

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

        if (value is null)
        {
            _editing = null;
            return;
        }

        _ = LoadEditorAsync(value.Id);
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

        Name = "新 MCP 服务器";
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
            StatusMessage = "⚠️ 名称不能为空";
            return;
        }

        if (IsStdio && string.IsNullOrWhiteSpace(Command))
        {
            IsStatusError = true;
            StatusMessage = "⚠️ Stdio 模式必须填写启动命令";
            return;
        }

        if (IsHttpTransport && string.IsNullOrWhiteSpace(Url))
        {
            IsStatusError = true;
            StatusMessage = "⚠️ HTTP 模式必须填写 URL";
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

        await _config.SaveMcpServerAsync(server).ConfigureAwait(true);

        IsStatusError = false;
        StatusMessage = "✅ 已保存";
        _status.ReportSuccess($"MCP 服务器「{server.Name}」已保存");

        // 配置变了，旧连接必须作废，否则下次对话仍用着改之前的连接
        if (_mcp is not null)
        {
            await _mcp.InvalidateAsync(server.Id).ConfigureAwait(true);
        }

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
        StatusMessage = "正在连接…";

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
                    ? "✅ 连接成功，但该服务器未提供任何工具"
                    : $"✅ 连接成功，发现 {tools.Count} 个工具：{string.Join("、", tools.Select(t => t.OriginalName))}";
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
            StatusMessage = $"⚠️ {ex.Message}";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? "⚠️ 连接失败";
        }
        finally
        {
            IsBusy = false;
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
        _status.ReportSuccess("MCP 服务器已删除");
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
