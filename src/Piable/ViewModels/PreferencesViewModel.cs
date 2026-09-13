using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>模糊模式的一个可选项。Value 落库，DisplayName 仅用于界面展示。</summary>
public sealed record BlurModeOption(string Value, string DisplayName);

/// <summary>
/// 「配置 → 偏好设置」标签页。
/// 偏好项改动即存，不设"保存"按钮——这类设置没有中间态，多一步确认只会碍事。
/// </summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IStatusReporter _status;
    private readonly ILocalizationService _localization;

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
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private AgentListItemViewModel? _defaultAgent;

    [ObservableProperty]
    private int _requestTimeoutSeconds = 300;

    /// <summary>工具调用循环的最大轮数。下限为 1——0 轮等于完全禁用工具。</summary>
    [ObservableProperty]
    private int _maxToolRounds = 5;

    /// <summary>危险工具是否逐次确认。</summary>
    [ObservableProperty]
    private bool _confirmDangerousTools = true;

    /// <summary>
    /// 思考模式（扩展推理）。开启后让支持的模型先推理再回答，并在消息里展示推理过程。
    /// 仅为界面开关的镜像；真正是否生效取决于模型/端点，由编排器在请求时按模型智能处理。
    /// </summary>
    [ObservableProperty]
    private bool _thinkingEnabled;

    /// <summary>
    /// 主窗口的原生背景模糊模式。取值："Off" / "Mica" / "AcrylicBlur"。
    /// 实际生效层级由操作系统决定，Mica 在不支持的平台会自动退回 AcrylicBlur、再退回 Blur。
    /// </summary>
    [ObservableProperty]
    private string _windowBlur = "Off";

    /// <summary>模糊模式变化，主窗口据此刷新系统背板。</summary>
    public event EventHandler<string>? WindowBlurChanged;

    public PreferencesViewModel(IConfigService config, IStatusReporter status)
        : this(config, status, Loc.Service)
    {
    }

    public PreferencesViewModel(IConfigService config, IStatusReporter status, ILocalizationService localization)
    {
        _config = config;
        _status = status;
        _localization = localization;

        // 展示名随语言变化：切换语言时重建一次选项集合，已选中项的文案才会跟着刷新。
        _localization.LanguageChanged += (_, _) => BuildBlurOptions();
        BuildBlurOptions();
    }

    public ObservableCollection<AgentListItemViewModel> AvailableAgents { get; } = [];

    public IReadOnlyList<string> AvailableCurrencies { get; } = ["USD", "CNY"];

    public IReadOnlyList<string> AvailableThemes { get; } = ["Light", "Dark", "System"];

    public IReadOnlyList<LanguageOption> AvailableLanguages => _localization.AvailableLanguages;

    /// <summary>模糊模式的可选项。DisplayName 用当前语言，故每次切换语言都会重建。</summary>
    public ObservableCollection<BlurModeOption> AvailableBlurModes { get; } = [];

    private void BuildBlurOptions()
    {
        AvailableBlurModes.Clear();
        foreach (var value in BlurModeValues)
        {
            AvailableBlurModes.Add(new BlurModeOption(value, _localization.Get(BlurModeConverter.BlurModeKey(value))));
        }
    }

    /// <summary>模糊模式取值。顺序即下拉列表顺序：关闭 → 云母 → 亚克力。</summary>
    internal static IReadOnlyList<string> BlurModeValues { get; } = ["Off", "Mica", "AcrylicBlur"];

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
            SelectedLanguage = FindLanguage(_preferences.Language);
            RequestTimeoutSeconds = _preferences.RequestTimeoutSeconds;
            MaxToolRounds = _preferences.MaxToolRounds;
            ConfirmDangerousTools = _preferences.ConfirmDangerousTools;
            ThinkingEnabled = _preferences.ThinkingEnabled;
            WindowBlur = _preferences.WindowBlur;

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

    partial void OnMaxToolRoundsChanged(int value) =>
        Persist(p => p.MaxToolRounds = Math.Clamp(value, 1, 20));

    partial void OnConfirmDangerousToolsChanged(bool value) =>
        Persist(p => p.ConfirmDangerousTools = value);

    partial void OnThinkingEnabledChanged(bool value) =>
        Persist(p => p.ThinkingEnabled = value);

    partial void OnWindowBlurChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        Persist(p => p.WindowBlur = value);
        WindowBlurChanged?.Invoke(this, value);
    }

    partial void OnThemeChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        Persist(p => p.Theme = value);
        ThemeChanged?.Invoke(this, value);
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_isLoading || value is null)
        {
            return;
        }

        // 先切服务再落库：切换会触发 LanguageChanged，界面与派生文本据此重算，
        // 顺序反过来也行，但保存失败时至少界面已经按用户所见的语言显示了。
        _localization.SetLanguage(value.Code);
        Persist(p => p.Language = value.Code);
    }

    /// <summary>偏好里存的代码可能是旧版本遗留的、或用户手改过的，认不出来就退回第一项。</summary>
    private LanguageOption? FindLanguage(string? code) =>
        AvailableLanguages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? AvailableLanguages.FirstOrDefault();

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
            _status.ReportError(ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Pref.SaveFailed"));
        }
    }
}
