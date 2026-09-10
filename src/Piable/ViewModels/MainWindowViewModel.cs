using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 主窗口：会话列表、视图切换、状态栏，以及三个配置标签页的承载。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IStatusReporter
{
    /// <summary>状态栏普通提示的停留时长。</summary>
    public static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(5);

    private readonly IConfigService _config;
    private readonly ISessionService _sessions;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ITokenCostCalculator _calculator;

    private CancellationTokenSource? _statusClearCts;
    private IReadOnlyList<Agent> _agents = [];
    private ProviderConfig? _provider;

    [ObservableProperty]
    private bool _isConfigView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftPanelWidth))]
    private bool _isLeftPanelCollapsed;

    /// <summary>左侧面板宽度：展开 260、折叠 48（设计文档 3.2）。</summary>
    public double LeftPanelWidth => IsLeftPanelCollapsed ? 48 : 260;

    [ObservableProperty]
    private ChatSessionViewModel? _currentSession;

    [ObservableProperty]
    private SessionListItemViewModel? _selectedSession;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private string? _busyMessage;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isInitializing = true;

    public MainWindowViewModel(
        IConfigService config,
        ISessionService sessions,
        IAgentOrchestrator orchestrator,
        ITokenCostCalculator calculator,
        IModelListService modelList)
    {
        _config = config;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _calculator = calculator;

        ProviderConfig = new ProviderConfigViewModel(config, modelList, orchestrator, this);
        AgentConfig = new AgentConfigViewModel(config, this);
        Preferences = new PreferencesViewModel(config, this);

        ProviderConfig.ProvidersChanged += async (_, _) => await ReloadProvidersAsync().ConfigureAwait(true);
        Preferences.PreferencesChanged += (_, _) => CurrentSession?.RefreshStatistics();
        Preferences.ThemeChanged += (_, theme) => ThemeChangeRequested?.Invoke(this, theme);

        // 主窗口缓存了智能体清单，增删改后必须重新读取，
        // 否则侧边栏的智能体名与会话默认智能体会停留在旧值。
        AgentConfig.AgentsChanged += async (_, _) => await ReloadAgentsAsync().ConfigureAwait(true);
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    public ProviderConfigViewModel ProviderConfig { get; }

    public AgentConfigViewModel AgentConfig { get; }

    public PreferencesViewModel Preferences { get; }

    /// <summary>请求应用主题（Light / Dark / System）。</summary>
    public event EventHandler<string>? ThemeChangeRequested;

    /// <summary>状态栏右侧显示"供应商 · 模型"。</summary>
    public string ProviderModelText =>
        _provider is null
            ? "未配置供应商"
            : $"{ProviderPresets.Find(_provider.PresetId)?.DisplayName ?? _provider.PresetId} · "
              + (string.IsNullOrWhiteSpace(_provider.DefaultModel) ? "未选模型" : _provider.DefaultModel);

    public string AgentNameText => CurrentSession?.SelectedAgent?.Name ?? "—";

    /// <summary>启动流程：建库、读偏好、加载列表、打开最近会话。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await _config.InitializeAsync(ct).ConfigureAwait(true);

            await Preferences.LoadAsync(ct).ConfigureAwait(true);
            IsLeftPanelCollapsed = Preferences.Snapshot.LeftPanelCollapsed;
            ThemeChangeRequested?.Invoke(this, Preferences.Snapshot.Theme);

            _agents = await _config.GetAgentsAsync(ct).ConfigureAwait(true);
            await ReloadProvidersAsync(ct).ConfigureAwait(true);

            await LoadSessionListAsync(ct).ConfigureAwait(true);

            var first = Sessions.FirstOrDefault();
            if (first is null)
            {
                // 首次启动：直接开一个空会话，否则右侧是一片没有上下文的空白
                await NewSessionAsync().ConfigureAwait(true);
            }
            else
            {
                SelectedSession = first;
            }
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private async Task ReloadProvidersAsync(CancellationToken ct = default)
    {
        _provider = await _config.GetDefaultProviderAsync(ct).ConfigureAwait(true);

        if (_provider is null && !string.IsNullOrWhiteSpace(Preferences.Snapshot.SelectedProviderId))
        {
            _provider = await _config.GetProviderAsync(Preferences.Snapshot.SelectedProviderId, ct)
                .ConfigureAwait(true);
        }

        IsConnected = _provider is not null
                      && !string.IsNullOrWhiteSpace(_provider.DefaultModel)
                      && (!string.IsNullOrWhiteSpace(_provider.ApiKey)
                          || ProviderPresets.Find(_provider.PresetId)?.RequiresApiKey == false);

        OnPropertyChanged(nameof(ProviderModelText));
        CurrentSession?.NotifyHeaderChanged();
        ApplyProviderToCurrentSession();
    }

    private async Task ReloadAgentsAsync(CancellationToken ct = default)
    {
        _agents = await _config.GetAgentsAsync(ct).ConfigureAwait(true);
    }

    private void ApplyProviderToCurrentSession()
    {
        if (CurrentSession is not null)
        {
            CurrentSession.Provider = _provider;
        }
    }

    private async Task LoadSessionListAsync(CancellationToken ct = default)
    {
        Sessions.Clear();

        var summaries = await _sessions.GetRecentAsync(limit: 50, ct).ConfigureAwait(true);
        foreach (var summary in summaries)
        {
            Sessions.Add(CreateSessionItem(summary));
        }
    }

    /// <summary>创建列表项并接上二次确认删除的处理。</summary>
    private SessionListItemViewModel CreateSessionItem(ChatSessionSummary summary)
    {
        var item = new SessionListItemViewModel(summary, ResolveAgentName(summary.AgentId));
        item.DeleteConfirmed += (_, vm) => _ = DeleteSessionAsync((SessionListItemViewModel)vm);
        return item;
    }

    private string ResolveAgentName(string? agentId) =>
        _agents.FirstOrDefault(a => a.Id == agentId)?.Name ?? "已删除";

    // ---------------- 命令 ----------------

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        // 当前会话还是空的就直接复用，避免连点产生一堆空会话
        if (CurrentSession is not null && CurrentSession.Session.IsEmpty)
        {
            IsConfigView = false;
            return;
        }

        // 首次启动时 CurrentSession 还是 null，若直接取它的 SelectedAgent 会得到 null，
        // 会话就会落库成"没有智能体"，侧边栏随即显示成"已删除"。此处回退到默认智能体。
        var agentId = CurrentSession?.SelectedAgent?.Id
                      ?? (await _config.GetDefaultAgentAsync().ConfigureAwait(true))?.Id;

        var session = await _sessions.CreateAsync(_provider?.Id, agentId).ConfigureAwait(true);

        var item = CreateSessionItem(new ChatSessionSummary
        {
            Id = session.Id,
            Title = session.Title,
            AgentId = session.AgentId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
        });

        Sessions.Insert(0, item);
        SelectedSession = item;
        IsConfigView = false;
    }

    [RelayCommand]
    private void ToggleConfigView() => IsConfigView = !IsConfigView;

    [RelayCommand]
    private async Task ToggleLeftPanelAsync()
    {
        IsLeftPanelCollapsed = !IsLeftPanelCollapsed;

        var prefs = Preferences.Snapshot;
        prefs.LeftPanelCollapsed = IsLeftPanelCollapsed;
        await _config.SavePreferencesAsync(prefs).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseConfigView() => IsConfigView = false;

    partial void OnIsConfigViewChanged(bool value)
    {
        if (value)
        {
            _ = LoadConfigTabsAsync();
        }
    }

    private async Task LoadConfigTabsAsync()
    {
        await ProviderConfig.LoadAsync().ConfigureAwait(true);
        await AgentConfig.LoadAsync().ConfigureAwait(true);
        await Preferences.LoadAsync().ConfigureAwait(true);
    }

    partial void OnSelectedSessionChanged(SessionListItemViewModel? value)
    {
        // 切换选中项时取消其他行上的删除确认态
        foreach (var item in Sessions)
        {
            if (!ReferenceEquals(item, value))
            {
                item.CancelConfirm();
            }
        }

        if (value is null)
        {
            return;
        }

        IsConfigView = false;
        _ = OpenSessionAsync(value);
    }

    private async Task OpenSessionAsync(SessionListItemViewModel item)
    {
        var session = await _sessions.LoadAsync(item.Id).ConfigureAwait(true);
        if (session is null)
        {
            // 会话已被删除（例如在别处删掉），从列表里摘掉
            Sessions.Remove(item);
            return;
        }

        if (CurrentSession is not null)
        {
            CurrentSession.IsActive = false;
            CurrentSession.TitleChanged -= OnSessionTitleChanged;
            CurrentSession.TurnCompleted -= OnTurnCompleted;
        }

        var vm = new ChatSessionViewModel(
            session, _agents, _provider, _sessions, _orchestrator, _calculator, Preferences.Snapshot, this)
        {
            IsActive = true,
        };

        vm.TitleChanged += OnSessionTitleChanged;
        vm.TurnCompleted += OnTurnCompleted;

        CurrentSession = vm;
        item.IsSelected = true;
        OnPropertyChanged(nameof(AgentNameText));
    }

    private void OnSessionTitleChanged(object? sender, EventArgs e)
    {
        if (CurrentSession is null || SelectedSession is null)
        {
            return;
        }

        SelectedSession.Title = CurrentSession.Session.Title;
        SelectedSession.RefreshSummary();
    }

    private void OnTurnCompleted(object? sender, EventArgs e)
    {
        if (SelectedSession is null || CurrentSession is null)
        {
            return;
        }

        // 生成结束后同步侧边栏的条数与 token
        var summary = new ChatSessionSummary
        {
            Id = CurrentSession.Session.Id,
            Title = CurrentSession.Session.Title,
            AgentId = CurrentSession.Session.AgentId,
            ModelUsed = CurrentSession.Session.ModelUsed,
            MessageCount = CurrentSession.Session.Messages.Count,
            TotalTokens = CurrentSession.Session.TotalTokens,
            CreatedAt = CurrentSession.Session.CreatedAt,
            UpdatedAt = CurrentSession.Session.UpdatedAt,
        };

        SelectedSession.Update(summary, ResolveAgentName(summary.AgentId));
        OnPropertyChanged(nameof(AgentNameText));
    }

    private async Task DeleteSessionAsync(SessionListItemViewModel item)
    {
        await _sessions.DeleteAsync(item.Id).ConfigureAwait(true);

        var wasCurrent = ReferenceEquals(item, SelectedSession);
        Sessions.Remove(item);

        if (wasCurrent)
        {
            SelectedSession = Sessions.FirstOrDefault();
        }

        ReportSuccess("会话已删除");
    }

    // ---------------- IStatusReporter ----------------

    public void ReportInfo(string message) => SetStatus(message, isError: false);

    public void ReportSuccess(string message) => SetStatus(message, isError: false);

    public void ReportError(string message) => SetStatus(message, isError: true);

    public void SetBusy(string? message) => BusyMessage = message;

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;

        _statusClearCts?.Cancel();
        _statusClearCts?.Dispose();
        _statusClearCts = new CancellationTokenSource();

        var token = _statusClearCts.Token;
        _ = ClearStatusAfterDelayAsync(token);
    }

    private async Task ClearStatusAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StatusMessageDuration, ct).ConfigureAwait(true);
            StatusMessage = string.Empty;
            IsStatusError = false;
        }
        catch (OperationCanceledException)
        {
            // 被新消息顶掉，保留新的内容
        }
    }
}
