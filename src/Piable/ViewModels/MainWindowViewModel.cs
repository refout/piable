using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

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
    private readonly ISkillService _skills;
    private readonly IToolCatalog _toolCatalog;
    private readonly IAgentResourceService _resources;

    /// <summary>搜索防抖时长。每个字都查一次库在会话多了以后会明显卡顿。</summary>
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _statusClearCts;
    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<Agent> _agents = [];
    private ProviderConfig? _provider;

    /// <summary>
    /// 未过滤的完整列表。搜索只是把 <see cref="Sessions"/> 换成子集，
    /// 清空搜索框后要能原样恢复——包括那些没被搜到、但用户本来就在看的会话。
    /// </summary>
    private readonly List<SessionListItemViewModel> _allSessions = [];

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

    /// <summary>标题栏是否处于重命名编辑态。</summary>
    [ObservableProperty]
    private bool _isRenamingSession;

    /// <summary>重命名输入框里的草稿标题。提交时才写回会话。</summary>
    [ObservableProperty]
    private string _sessionTitleDraft = string.Empty;

    /// <summary>侧边栏搜索框的内容。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    /// <summary>会话列表是否处于搜索结果态。影响列表项第二行的文案。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    private bool _isSearching;

    public bool HasSearchText => SearchText.Length > 0;

    /// <summary>搜索无结果时的提示。有会话时列表本身就说明问题，不需要额外文案。</summary>
    public bool ShowNoSearchResults => IsSearching && Sessions.Count == 0;

    [ObservableProperty]
    private bool _isProviderConfigured;

    public MainWindowViewModel(
        IConfigService config,
        ISessionService sessions,
        IAgentOrchestrator orchestrator,
        ITokenCostCalculator calculator,
        IModelListService modelList,
        ISkillService skills,
        IToolCatalog toolCatalog,
        IAgentResourceService resources,
        IMcpClientService mcp)
    {
        _config = config;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _calculator = calculator;
        _skills = skills;
        _toolCatalog = toolCatalog;
        _resources = resources;

        ProviderConfig = new ProviderConfigViewModel(config, modelList, orchestrator, this);
        AgentConfig = new AgentConfigViewModel(config, skills, this, mcp);
        SkillConfig = new SkillConfigViewModel(skills, this);
        McpConfig = new McpServerConfigViewModel(config, this, mcp);
        Preferences = new PreferencesViewModel(config, this);

        ProviderConfig.ProvidersChanged += async (_, _) => await ReloadProvidersAsync().ConfigureAwait(true);
        Preferences.PreferencesChanged += (_, _) => CurrentSession?.RefreshStatistics();
        Preferences.ThemeChanged += (_, theme) => ThemeChangeRequested?.Invoke(this, theme);
        Preferences.WindowBlurChanged += (_, mode) => WindowBlurChangeRequested?.Invoke(this, mode);

        // 主窗口缓存了智能体清单，增删改后必须重新读取，
        // 否则侧边栏的智能体名与会话默认智能体会停留在旧值。
        AgentConfig.AgentsChanged += async (_, _) => await ReloadAgentsAsync().ConfigureAwait(true);
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    public ProviderConfigViewModel ProviderConfig { get; }

    public AgentConfigViewModel AgentConfig { get; }

    public SkillConfigViewModel SkillConfig { get; }

    public McpServerConfigViewModel McpConfig { get; }

    public PreferencesViewModel Preferences { get; }

    /// <summary>请求应用主题（Light / Dark / System）。</summary>
    public event EventHandler<string>? ThemeChangeRequested;

    /// <summary>请求主窗口刷新原生背景模糊（Off / Mica / AcrylicBlur）。</summary>
    public event EventHandler<string>? WindowBlurChangeRequested;

    /// <summary>状态栏右侧显示"供应商 · 模型"。</summary>
    public string ProviderModelText =>
        _provider is null
            ? Loc.Get("Status.NoProvider")
            : $"{_provider.DisplayName} · "
              + (string.IsNullOrWhiteSpace(_provider.DefaultModel) ? Loc.Get("Status.NoModel") : _provider.DefaultModel);

    /// <summary>启动流程：建库、读偏好、加载列表、打开最近会话。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _config.InitializeAsync(ct).ConfigureAwait(true);

        await Preferences.LoadAsync(ct).ConfigureAwait(true);
        IsLeftPanelCollapsed = Preferences.Snapshot.LeftPanelCollapsed;
        ThemeChangeRequested?.Invoke(this, Preferences.Snapshot.Theme);
        WindowBlurChangeRequested?.Invoke(this, Preferences.Snapshot.WindowBlur);

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

    private async Task ReloadProvidersAsync(CancellationToken ct = default)
    {
        _provider = await _config.GetDefaultProviderAsync(ct).ConfigureAwait(true);

        if (_provider is null && !string.IsNullOrWhiteSpace(Preferences.Snapshot.SelectedProviderId))
        {
            _provider = await _config.GetProviderAsync(Preferences.Snapshot.SelectedProviderId, ct)
                .ConfigureAwait(true);
        }

        IsProviderConfigured = _provider is not null
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
        _allSessions.Clear();

        var summaries = await _sessions.GetRecentAsync(limit: 50, ct).ConfigureAwait(true);
        foreach (var summary in summaries)
        {
            var item = CreateSessionItem(summary);
            _allSessions.Add(item);
            Sessions.Add(item);
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
        _agents.FirstOrDefault(a => a.Id == agentId)?.Name ?? Loc.Get("Session.AgentDeleted");

    // ---------------- 会话搜索 ----------------

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        if (value.Trim().Length == 0)
        {
            // 清空不必防抖：晚一拍恢复列表只会让用户以为会话丢了
            ShowAllSessions();
            return;
        }

        _ = DebouncedSearchAsync(_searchCts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 又有新输入，本次查询作废
            return;
        }

        await ApplySearchAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 按当前搜索词过滤列表。搜索词为空则恢复完整列表。
    ///
    /// 过滤只影响左侧列表，不动右侧正在看的会话——
    /// 正在生成的对话被过滤掉就中断它，是最不应该发生的事。
    /// </summary>
    private async Task ApplySearchAsync()
    {
        var keyword = SearchText.Trim();

        if (keyword.Length == 0)
        {
            ShowAllSessions();
            return;
        }

        // 搜索结果里可能包含不在"最近 50 条"里的老会话，
        // 因此结果集以数据库查询为准，而不是在已有列表上做内存过滤。
        var results = await _sessions.SearchAsync(keyword, limit: 50).ConfigureAwait(true);

        // 查询期间用户可能又改了输入框
        if (!string.Equals(keyword, SearchText.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var keepId = SelectedSession?.Id ?? CurrentSession?.Session.Id;

        IsSearching = true;
        Sessions.Clear();

        foreach (var summary in results)
        {
            var item = GetOrCreateItem(summary);
            item.IsSearching = true;
            item.MatchCount = summary.MatchCount;
            item.RefreshSummary();
            Sessions.Add(item);
        }

        RestoreSelection(keepId, clearWhenMissing: true);
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private void ShowAllSessions()
    {
        var keepId = SelectedSession?.Id ?? CurrentSession?.Session.Id;

        IsSearching = false;
        Sessions.Clear();

        foreach (var item in _allSessions.OrderByDescending(i => i.UpdatedAt))
        {
            item.IsSearching = false;
            item.MatchCount = 0;
            item.RefreshSummary();
            Sessions.Add(item);
        }

        RestoreSelection(keepId, clearWhenMissing: false);
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    /// <summary>
    /// 重新选中指定会话。不在结果里时<b>不清空也不改选</b>——
    /// 退而选中第一条会把右侧已打开的会话悄悄换掉。
    /// </summary>
    /// <param name="clearWhenMissing">
    /// 搜索态下置 true：被过滤掉的会话不再出现在列表里，选中态留着会造成
    /// "界面上没选中任何行、但选中项还是它"的错觉。右侧内容不受影响。
    /// </param>
    private void RestoreSelection(string? keepId, bool clearWhenMissing)
    {
        if (keepId is null)
        {
            return;
        }

        var match = Sessions.FirstOrDefault(s => s.Id == keepId);
        if (match is not null)
        {
            SelectedSession = match;
            return;
        }

        if (clearWhenMissing && SelectedSession is not null)
        {
            // 只清选中态，CurrentSession 保持不动：正在看的对话不该因为搜索而消失
            SelectedSession = null;
        }
    }

    /// <summary>取已有列表项，没有就新建并登记，保证同一会话在界面上始终是同一个对象。</summary>
    private SessionListItemViewModel GetOrCreateItem(ChatSessionSummary summary)
    {
        var existing = _allSessions.FirstOrDefault(i => i.Id == summary.Id);
        if (existing is not null)
        {
            existing.Update(summary, ResolveAgentName(summary.AgentId));
            return existing;
        }

        var item = CreateSessionItem(summary);
        _allSessions.Add(item);
        return item;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchText = string.Empty;
    }

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

        // 搜索态下新建的会话不在过滤结果里，留着搜索词等于让它当场消失
        if (IsSearching)
        {
            ClearSearch();
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
        _allSessions.Insert(0, item);
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

    // ---- 会话重命名（设计文档 1.1） ----

    [RelayCommand]
    private void BeginRenameSession()
    {
        if (CurrentSession is null)
        {
            return;
        }

        SessionTitleDraft = CurrentSession.Title;
        IsRenamingSession = true;
    }

    [RelayCommand]
    private async Task CommitRenameSessionAsync()
    {
        // 取消后失焦也会走到这里，先判状态，否则撤销的标题会被重新写回去
        if (!IsRenamingSession)
        {
            return;
        }

        IsRenamingSession = false;

        if (CurrentSession is not null)
        {
            await CurrentSession.RenameAsync(SessionTitleDraft).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelRenameSession() => IsRenamingSession = false;

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
        await SkillConfig.LoadAsync().ConfigureAwait(true);
        await McpConfig.LoadAsync().ConfigureAwait(true);
        await Preferences.LoadAsync().ConfigureAwait(true);
    }

    partial void OnSelectedSessionChanged(SessionListItemViewModel? value)
    {
        // 换会话时编辑框还开着会指向别的会话，直接收起
        IsRenamingSession = false;

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
        // 已经打开的会话不重复加载：搜索过滤会反复重设选中项，
        // 而重新加载会丢掉正在流式生成的那条消息。
        if (CurrentSession is not null && CurrentSession.Session.Id == item.Id)
        {
            return;
        }

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

            // 切走时把没决定的工具确认放掉，否则那条生成会永远停在等用户点击上
            CurrentSession.AbandonPendingConfirmation();

            CurrentSession.TitleChanged -= OnSessionTitleChanged;
            CurrentSession.TurnCompleted -= OnTurnCompleted;
        }

        var vm = new ChatSessionViewModel(
            session, _agents, _provider, _sessions, _orchestrator, _toolCatalog, _resources,
            _calculator, Preferences.Snapshot, this)
        {
            IsActive = true,
        };

        vm.TitleChanged += OnSessionTitleChanged;
        vm.TurnCompleted += OnTurnCompleted;

        CurrentSession = vm;
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
    }

    private async Task DeleteSessionAsync(SessionListItemViewModel item)
    {
        await _sessions.DeleteAsync(item.Id).ConfigureAwait(true);

        var wasCurrent = ReferenceEquals(item, SelectedSession);
        Sessions.Remove(item);
        _allSessions.Remove(item);
        OnPropertyChanged(nameof(ShowNoSearchResults));

        if (wasCurrent)
        {
            SelectedSession = Sessions.FirstOrDefault();
        }

        if (Sessions.Count == 0)
        {
            // 删完最后一个会话后不能把已删除的那个留在右侧：它已经不在列表里了，
            // 再往里发消息会写到一个不存在的会话上，而且界面上看不出异常。
            // 直接开一个新的，与首次启动的行为保持一致。
            SelectedSession = null;
            CurrentSession = null;
            await NewSessionAsync().ConfigureAwait(true);
        }

        ReportSuccess(Loc.Get("Session.Deleted"));
    }

    // ---------------- IStatusReporter ----------------

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
