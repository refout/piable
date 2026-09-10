using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 「配置 → 偏好设置」标签页。
/// 偏好项改动即存，不设"保存"按钮——这类设置没有中间态，多一步确认只会碍事。
/// </summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IStatusReporter _status;

    /// <summary>加载期间抑制自动保存，避免回填表单时把默认值写回去。</summary>
    private bool _isLoading;

    private UserPreferences _preferences = new();

    [ObservableProperty]
    private bool _showStatistics = true;

    [ObservableProperty]
    private bool _showCost = true;

    [ObservableProperty]
    private bool _showDetailedTokens;

    [ObservableProperty]
    private bool _expandStatisticsByDefault;

    [ObservableProperty]
    private string _currency = "USD";

    [ObservableProperty]
    private string _theme = "System";

    [ObservableProperty]
    private AgentListItemViewModel? _defaultAgent;

    [ObservableProperty]
    private int _requestTimeoutSeconds = 300;

    public PreferencesViewModel(IConfigService config, IStatusReporter status)
    {
        _config = config;
        _status = status;
    }

    public ObservableCollection<AgentListItemViewModel> AvailableAgents { get; } = [];

    public IReadOnlyList<string> AvailableCurrencies { get; } = ["USD", "CNY"];

    public IReadOnlyList<string> AvailableThemes { get; } = ["Light", "Dark", "System"];

    /// <summary>偏好变化，主窗口据此刷新统计显示。</summary>
    public event EventHandler? PreferencesChanged;

    /// <summary>主题变化，主窗口据此切换应用主题。</summary>
    public event EventHandler<string>? ThemeChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _isLoading = true;
        try
        {
            _preferences = await _config.GetPreferencesAsync(ct).ConfigureAwait(true);

            ShowStatistics = _preferences.ShowStatistics;
            ShowCost = _preferences.ShowCost;
            ShowDetailedTokens = _preferences.ShowDetailedTokens;
            ExpandStatisticsByDefault = _preferences.ExpandStatisticsByDefault;
            Currency = _preferences.Currency;
            Theme = _preferences.Theme;
            RequestTimeoutSeconds = _preferences.RequestTimeoutSeconds;

            AvailableAgents.Clear();
            foreach (var agent in await _config.GetAgentsAsync(ct).ConfigureAwait(true))
            {
                AvailableAgents.Add(new AgentListItemViewModel(agent));
            }

            DefaultAgent = AvailableAgents.FirstOrDefault(a => a.Id == _preferences.DefaultAgentId)
                           ?? AvailableAgents.FirstOrDefault(a => a.IsDefault);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>当前偏好实例，供消息渲染等处读取。</summary>
    public UserPreferences Snapshot => _preferences;

    // ---- 以下每个 OnXxxChanged 都执行"改内存 → 落库 → 通知" ----

    partial void OnShowStatisticsChanged(bool value) => Persist(p => p.ShowStatistics = value);

    partial void OnShowCostChanged(bool value) => Persist(p => p.ShowCost = value);

    partial void OnShowDetailedTokensChanged(bool value) => Persist(p => p.ShowDetailedTokens = value);

    partial void OnExpandStatisticsByDefaultChanged(bool value) =>
        Persist(p => p.ExpandStatisticsByDefault = value);

    partial void OnCurrencyChanged(string value) => Persist(p => p.Currency = value);

    partial void OnRequestTimeoutSecondsChanged(int value) =>
        Persist(p => p.RequestTimeoutSeconds = Math.Max(1, value));

    partial void OnThemeChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        Persist(p => p.Theme = value);
        ThemeChanged?.Invoke(this, value);
    }

    partial void OnDefaultAgentChanged(AgentListItemViewModel? value)
    {
        if (_isLoading || value is null)
        {
            return;
        }

        Persist(p => p.DefaultAgentId = value.Id);
    }

    private void Persist(Action<UserPreferences> mutate)
    {
        if (_isLoading)
        {
            return;
        }

        mutate(_preferences);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _config.SavePreferencesAsync(_preferences).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _status.ReportError(ChatErrorMapper.ToUserMessage(ex) ?? "⚠️ 偏好保存失败");
        }
    }
}
