using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 「配置 → API 配置」标签页。
/// 下拉框选择的是内置预设；选中后加载（必要时新建）该预设对应的用户配置。
/// </summary>
public sealed partial class ProviderConfigViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IModelListService _modelList;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IStatusReporter _status;

    /// <summary>抑制表单回填过程中触发的重复加载。</summary>
    private bool _isLoading;

    private ProviderConfig? _current;

    [ObservableProperty]
    private ProviderPreset? _selectedPreset;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>API Key 默认打码显示。</summary>
    [ObservableProperty]
    private bool _isApiKeyVisible;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string? _deploymentName;

    [ObservableProperty]
    private string _defaultModel = string.Empty;

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private int _maxTokens = 2048;

    [ObservableProperty]
    private double _topP = 1.0;

    [ObservableProperty]
    private decimal _inputPricePer1K;

    [ObservableProperty]
    private decimal _outputPricePer1K;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private string _newModelName = string.Empty;

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public ProviderConfigViewModel(
        IConfigService config,
        IModelListService modelList,
        IAgentOrchestrator orchestrator,
        IStatusReporter status)
    {
        _config = config;
        _modelList = modelList;
        _orchestrator = orchestrator;
        _status = status;
    }

    public IReadOnlyList<ProviderPreset> AvailablePresets => ProviderPresets.All;

    public ObservableCollection<string> Models { get; } = [];

    public IReadOnlyList<ModelPricingPresets.PricingEntry> PricingPresets => ModelPricingPresets.All;

    /// <summary>当前配置的预设是否为 Azure（决定是否显示部署名输入框）。</summary>
    public bool IsAzure => SelectedPreset?.ProviderType == ProviderType.AzureOpenAI;

    public bool RequiresApiKey => SelectedPreset?.RequiresApiKey ?? true;

    public bool SupportsModelFetch => SelectedPreset?.SupportsModelFetch ?? false;

    /// <summary>加载配置页时调用：拉取已有供应商并选中第一个（或默认项）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var providers = await _config.GetProvidersAsync(ct).ConfigureAwait(true);

        if (providers.Count == 0)
        {
            // 首次进入配置页：默认展示 OpenAI 预设，但先不落库，
            // 等用户真正点保存或获取模型时再创建，避免留下一条空配置。
            _isLoading = true;
            SelectedPreset = ProviderPresets.All[0];
            ResetFormTo(ProviderPresets.All[0]);
            _isLoading = false;
            return;
        }

        var target = providers.FirstOrDefault(p => p.IsDefault) ?? providers[0];
        LoadProvider(target);
    }

    /// <summary>把一条已有配置回填到表单。</summary>
    private void LoadProvider(ProviderConfig provider)
    {
        _current = provider;

        _isLoading = true;
        try
        {
            SelectedPreset = ProviderPresets.Find(provider.PresetId) ?? ProviderPresets.All[0];
            ApiKey = provider.ApiKey ?? string.Empty;
            Endpoint = provider.Endpoint ?? SelectedPreset.DefaultEndpoint;
            DeploymentName = provider.DeploymentName;
            DefaultModel = provider.DefaultModel;
            Temperature = provider.Temperature ?? 0.7;
            MaxTokens = provider.MaxTokens ?? 2048;
            TopP = provider.TopP ?? 1.0;
            InputPricePer1K = provider.InputPricePer1K;
            OutputPricePer1K = provider.OutputPricePer1K;
            IsDefault = provider.IsDefault;

            Models.Clear();
            foreach (var model in provider.Models)
            {
                Models.Add(model);
            }
        }
        finally
        {
            _isLoading = false;
        }

        NotifyPresetDependentProperties();
    }

    partial void OnSelectedPresetChanged(ProviderPreset? value)
    {
        NotifyPresetDependentProperties();

        if (_isLoading || value is null)
        {
            return;
        }

        // 切换到另一个预设：加载已有配置或按预设默认值新建
        _ = SwitchPresetAsync(value);
    }

    private async Task SwitchPresetAsync(ProviderPreset preset)
    {
        IsStatusError = false;
        StatusMessage = string.Empty;

        var provider = await _config.GetOrCreateProviderAsync(preset.Id).ConfigureAwait(true);
        LoadProvider(provider);
    }

    private void NotifyPresetDependentProperties()
    {
        OnPropertyChanged(nameof(IsAzure));
        OnPropertyChanged(nameof(RequiresApiKey));
        OnPropertyChanged(nameof(SupportsModelFetch));
    }

    /// <summary>把表单重置为某个预设的默认值（不落库）。</summary>
    private void ResetFormTo(ProviderPreset preset)
    {
        ApiKey = string.Empty;
        Endpoint = preset.DefaultEndpoint;
        DeploymentName = null;
        Models.Clear();
        foreach (var model in preset.DefaultModels)
        {
            Models.Add(model);
        }

        DefaultModel = preset.DefaultModels.Count > 0 ? preset.DefaultModels[0] : string.Empty;
        Temperature = 0.7;
        MaxTokens = 2048;
        TopP = 1.0;
        InputPricePer1K = preset.InputPricePer1K;
        OutputPricePer1K = preset.OutputPricePer1K;
        IsDefault = false;
    }

    [RelayCommand]
    private void AddModel()
    {
        var name = NewModelName.Trim();
        if (name.Length == 0 || Models.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Models.Add(name);
        NewModelName = string.Empty;

        if (string.IsNullOrWhiteSpace(DefaultModel))
        {
            DefaultModel = name;
        }
    }

    [RelayCommand]
    private void RemoveModel()
    {
        if (SelectedModel is null)
        {
            return;
        }

        var removed = SelectedModel;
        Models.Remove(removed);

        if (string.Equals(DefaultModel, removed, StringComparison.OrdinalIgnoreCase))
        {
            DefaultModel = Models.FirstOrDefault() ?? string.Empty;
        }
    }

    /// <summary>
    /// 从内置定价表填充价格。用可观察属性而非带参命令，
    /// 以便直接绑定到 ComboBox 的 SelectedItem。
    /// </summary>
    [ObservableProperty]
    private ModelPricingPresets.PricingEntry? _selectedPricingPreset;

    partial void OnSelectedPricingPresetChanged(ModelPricingPresets.PricingEntry? value)
    {
        if (value is null)
        {
            return;
        }

        InputPricePer1K = value.InputPer1K;
        OutputPricePer1K = value.OutputPer1K;
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        IsBusy = true;
        IsStatusError = false;
        StatusMessage = "正在获取模型列表…";

        try
        {
            var provider = BuildConfig();
            var fetched = await _modelList.FetchModelsAsync(provider).ConfigureAwait(true);

            if (fetched.Count == 0)
            {
                IsStatusError = true;
                StatusMessage = "⚠️ 供应商未返回任何模型，原列表已保留";
                return;
            }

            // 合并而非替换：用户手动添加的模型不该被一次拉取冲掉
            var merged = Models
                .Concat(fetched)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Models.Clear();
            foreach (var model in merged)
            {
                Models.Add(model);
            }

            if (string.IsNullOrWhiteSpace(DefaultModel))
            {
                DefaultModel = Models.FirstOrDefault() ?? string.Empty;
            }

            IsStatusError = false;
            StatusMessage = $"✅ 已获取 {fetched.Count} 个模型";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? "⚠️ 获取模型列表失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        IsStatusError = false;
        StatusMessage = "正在测试连接…";

        try
        {
            var provider = BuildConfig();
            var result = await _orchestrator
                .TestConnectionAsync(provider, DefaultModel)
                .ConfigureAwait(true);

            IsStatusError = !result.Success;
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var provider = BuildConfig();
            await _config.SaveProviderAsync(provider).ConfigureAwait(true);
            _current = provider;

            IsStatusError = false;
            StatusMessage = "✅ 配置已保存";
            _status.ReportSuccess("配置已保存");

            // 供应商变化会影响新会话的默认设置，通知主窗口刷新
            ProvidersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? "⚠️ 保存失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>由表单内容构造待保存的配置对象。</summary>
    private ProviderConfig BuildConfig()
    {
        var preset = SelectedPreset!;

        var provider = _current ?? new ProviderConfig { PresetId = preset.Id };
        provider.PresetId = preset.Id;
        provider.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        provider.Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? preset.DefaultEndpoint : Endpoint.Trim();
        provider.DeploymentName = string.IsNullOrWhiteSpace(DeploymentName) ? null : DeploymentName.Trim();
        provider.Models = [.. Models];
        provider.DefaultModel = DefaultModel.Trim();
        provider.Temperature = Temperature;
        provider.MaxTokens = MaxTokens;
        provider.TopP = TopP;
        provider.InputPricePer1K = InputPricePer1K;
        provider.OutputPricePer1K = OutputPricePer1K;
        provider.IsDefault = IsDefault;

        return provider;
    }

    /// <summary>供应商有增删改，主窗口需要重新加载列表。</summary>
    public event EventHandler? ProvidersChanged;
}
