using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>
/// 「配置 → API 配置」标签页。
/// 下拉里既有内置预设，也有用户自建的自定义供应商；选中后加载（必要时新建）对应配置。
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

    /// <summary>新建后还没保存的自定义供应商。刷新列表时要留着，否则用户填了一半就消失了。</summary>
    private readonly List<ProviderChoice> _unsavedCustoms = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAzure))]
    [NotifyPropertyChangedFor(nameof(RequiresApiKey))]
    [NotifyPropertyChangedFor(nameof(SupportsModelFetch))]
    [NotifyPropertyChangedFor(nameof(CanFetchModels))]
    [NotifyPropertyChangedFor(nameof(IsCustomProvider))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCustomCommand))]
    private ProviderChoice? _selectedChoice;

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

    /// <summary>自定义供应商的名字。内置预设没有自己的名字，显示时用预设名。</summary>
    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private string _newModelName = string.Empty;

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchModels))]
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

    /// <summary>下拉选项：内置预设在前，自定义供应商在后。</summary>
    public ObservableCollection<ProviderChoice> Choices { get; } = [];

    /// <summary>
    /// 选择按钮组真正绑定的集合：在 <see cref="Choices"/> 末尾追加一枚「+ 新建」哨兵，
    /// 这样新建按钮能像普通供应商一样参与换行——同一行放得下就紧跟其后，放不下整体换到下一行。
    /// </summary>
    public ObservableCollection<object> SelectorItems { get; } = [];

    /// <summary>「+ 新建」哨兵的单例，供 XAML 的模板选择器与 ContainerStyle 识别。</summary>
    public NewProviderSentinel NewProviderSentinel { get; } = NewProviderSentinel.Instance;

    /// <summary>「删除」哨兵的单例，紧跟在「+ 新建」之后；仅当选中自定义供应商时可见（由模板的 IsVisible 控制）。</summary>
    public DeleteProviderSentinel DeleteProviderSentinel { get; } = DeleteProviderSentinel.Instance;

    /// <summary>把 Choices 连同末尾的「+ 新建」「删除」哨兵同步进 <see cref="SelectorItems"/>。</summary>
    private void RebuildSelectorItems()
    {
        SelectorItems.Clear();
        foreach (var choice in Choices)
        {
            SelectorItems.Add(choice);
        }
        SelectorItems.Add(NewProviderSentinel);
        SelectorItems.Add(DeleteProviderSentinel);
    }

    /// <summary>
    /// 当前选中的内置预设；自定义项为 null。
    /// 保留这个投影是为了让"当前是哪家供应商"这类判断不必到处区分两种来源。
    /// </summary>
    public ProviderPreset? SelectedPreset
    {
        get => SelectedChoice?.Preset;
        set
        {
            if (value is null)
            {
                return;
            }

            var choice = Choices.FirstOrDefault(c =>
                c.Preset is not null && string.Equals(c.Preset.Id, value.Id, StringComparison.Ordinal));

            if (choice is not null)
            {
                SelectedChoice = choice;
            }
        }
    }

    /// <summary>当前生效的预设：内置用自己，自定义共用自定义模板。</summary>
    private ProviderPreset CurrentPreset => SelectedChoice?.Preset ?? ProviderPresets.CustomPreset;

    /// <summary>是否为自定义供应商（决定是否显示名称输入框与删除按钮）。</summary>
    public bool IsCustomProvider => SelectedChoice?.IsCustom ?? false;

    /// <summary>
    /// 删除确认里那句「删除「公司网关」？」。
    /// 占位符在句子中间，XAML 的 StringFormat 会把语序写死，所以整句在这里拼。
    /// </summary>
    public string DeleteConfirmText =>
        Loc.Get("Provider.DeleteConfirm", SelectedChoice?.DisplayName ?? string.Empty);

    public ObservableCollection<string> Models { get; } = [];

    public IReadOnlyList<ModelPricingEntry> PricingPresets => ModelPricingPresets.All;

    /// <summary>当前配置的预设是否为 Azure（决定是否显示部署名输入框）。</summary>
    public bool IsAzure => CurrentPreset.ProviderType == ProviderType.AzureOpenAI;

    public bool RequiresApiKey => CurrentPreset.RequiresApiKey;

    public bool SupportsModelFetch => CurrentPreset.SupportsModelFetch;

    /// <summary>
    /// "获取模型列表"是否可点。不支持动态获取的预设（如 Azure 用部署名寻址）
    /// 必须禁用，否则用户点下去只会收到一个必然失败的请求。
    /// </summary>
    public bool CanFetchModels => SupportsModelFetch && !IsBusy;

    /// <summary>加载配置页时调用：重建下拉并选中默认项（或第一个预设）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var providers = await _config.GetProvidersAsync(ct).ConfigureAwait(true);
        RebuildChoices(providers);

        _isLoading = true;
        try
        {
            // 有配置就落在它对应的那一项上，否则展示第一个预设——
            // 只是展示，不落库，等用户真正点保存或获取模型时再创建。
            var target = providers.FirstOrDefault(p => p.IsDefault);
            SelectedChoice = target is null
                ? Choices[0]
                : Choices.FirstOrDefault(c => c.Provider?.Id == target.Id) ?? Choices[0];
        }
        finally
        {
            _isLoading = false;
        }

        if (SelectedChoice?.Provider is not null)
        {
            LoadProvider(SelectedChoice.Provider);
        }
        else if (SelectedChoice?.Preset is not null)
        {
            ResetFormTo(SelectedChoice.Preset);
        }
    }

    /// <summary>
    /// 按当前库存重建下拉。内置预设永远都在（即便还没配置），
    /// 自定义供应商则是一条配置一项；新建后未保存的项原样保留。
    /// </summary>
    private void RebuildChoices(IReadOnlyList<ProviderConfig> providers)
    {
        Choices.Clear();

        foreach (var preset in ProviderPresets.All)
        {
            var existing = providers.FirstOrDefault(p => p.PresetId == preset.Id && !p.IsCustom);
            Choices.Add(ProviderChoice.ForPreset(preset, existing));
        }

        foreach (var provider in providers.Where(p => p.IsCustom))
        {
            Choices.Add(ProviderChoice.ForCustom(provider));
        }

        foreach (var pending in _unsavedCustoms)
        {
            Choices.Add(pending);
        }

        RebuildSelectorItems();
    }

    /// <summary>把一条已有配置回填到表单。</summary>
    private void LoadProvider(ProviderConfig provider)
    {
        _current = provider;

        _isLoading = true;
        try
        {
            ApiKey = provider.ApiKey ?? string.Empty;
            Endpoint = provider.Endpoint ?? CurrentPreset.DefaultEndpoint;
            ProviderName = provider.Name;
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

    partial void OnSelectedChoiceChanged(ProviderChoice? value)
    {
        NotifyPresetDependentProperties();

        if (_isLoading || value is null)
        {
            return;
        }

        IsStatusError = false;
        StatusMessage = string.Empty;

        // 已经有配置（内置预设存过、或自定义项）就直接用它，
        // 否则按预设的默认值铺一张空表单——内置预设第一次被选中时属于后者
        if (value.Provider is not null)
        {
            LoadProvider(value.Provider);
            return;
        }

        if (value.Preset is not null)
        {
            _ = SwitchPresetAsync(value);
        }
    }

    private async Task SwitchPresetAsync(ProviderChoice choice)
    {
        var preset = choice.Preset!;
        var provider = await _config.GetOrCreateProviderAsync(preset.Id).ConfigureAwait(true);

        // 挂回去，避免下次再选中同一个预设时又建一条
        choice.Attach(provider);
        if (SelectedChoice == choice)
        {
            LoadProvider(provider);
        }
    }

    /// <summary>按钮组里的供应商芯片点击后切换选中项，从而填充下方表单。</summary>
    [RelayCommand]
    private void SelectChoice(ProviderChoice? choice)
    {
        if (choice is not null)
        {
            SelectedChoice = choice;
        }
    }

    partial void OnProviderNameChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        SelectedChoice?.Rename(value.Trim());
    }

    private void NotifyPresetDependentProperties()
    {
        OnPropertyChanged(nameof(IsAzure));
        OnPropertyChanged(nameof(RequiresApiKey));
        OnPropertyChanged(nameof(SupportsModelFetch));
        OnPropertyChanged(nameof(CanFetchModels));
        OnPropertyChanged(nameof(IsCustomProvider));
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

    /// <summary>
    /// 新建一条自定义供应商。刻意不立刻落库：
    /// 用户点一下"新建"就往库里塞一条空配置，会让列表里堆满没用的条目。
    /// </summary>
    [RelayCommand]
    private void CreateCustom()
    {
        var provider = new ProviderConfig
        {
            PresetId = ProviderPresets.Custom,
            Name = NextCustomName(),
            Endpoint = string.Empty,
            Models = [],
            DefaultModel = string.Empty,
        };

        var choice = ProviderChoice.ForCustom(provider);
        _unsavedCustoms.Add(choice);
        Choices.Add(choice);
        RebuildSelectorItems();

        SelectedChoice = choice;

        IsStatusError = false;
        StatusMessage = Loc.Get("Provider.FillToSave");
    }

    /// <summary>生成一个不与现有条目重名的默认名字。</summary>
    private string NextCustomName()
    {
        var Base = Loc.Get("Provider.CustomName");
        var taken = Choices
            .Select(c => c.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return !taken.Contains(Base) ? Base : Enumerable.Range(2, 99)
            .Select(i => $"{Base} {i}")
            .First(name => !taken.Contains(name));
    }

    private bool CanDeleteCustom() => SelectedChoice is { IsCustom: true };

    /// <summary>
    /// 删除当前自定义供应商。内置预设不能删——它们是界面的一部分，
    /// 用户真正想做的是"清空配置"而不是"让 OpenAI 从列表里消失"。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCustom))]
    private async Task DeleteCustomAsync()
    {
        var choice = SelectedChoice;
        if (choice?.Provider is null || choice.IsBuiltIn)
        {
            return;
        }

        var name = choice.DisplayName;

        IsBusy = true;
        try
        {
            await _config.DeleteProviderAsync(choice.Provider.Id).ConfigureAwait(true);

            var index = Choices.IndexOf(choice);
            _unsavedCustoms.Remove(choice);
            _current = null;
            Choices.Remove(choice);
            RebuildSelectorItems();

            // 落在相邻的一项上：删掉中间某条时不该把用户弹回列表顶端
            SelectedChoice = Choices[Math.Clamp(index, 0, Choices.Count - 1)];

            IsStatusError = false;
            StatusMessage = Loc.Get("Provider.Deleted", name);
            _status.ReportSuccess(Loc.Get("Provider.DeletedToast"));

            ProvidersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Provider.DeleteFailed");
        }
        finally
        {
            IsBusy = false;
        }
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
    private ModelPricingEntry? _selectedPricingPreset;

    partial void OnSelectedPricingPresetChanged(ModelPricingEntry? value)
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
        if (SelectedChoice is null)
        {
            return;
        }

        IsBusy = true;
        IsStatusError = false;
        StatusMessage = Loc.Get("Provider.FetchingModels");

        try
        {
            var provider = BuildConfig();
            var fetched = await _modelList.FetchModelsAsync(provider).ConfigureAwait(true);

            if (fetched.Count == 0)
            {
                IsStatusError = true;
                StatusMessage = Loc.Get("Provider.NoModelsReturned");
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
            StatusMessage = Loc.Get("Provider.ModelsFetched", fetched.Count);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Provider.FetchModelsFailed");
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
        StatusMessage = Loc.Get("Provider.Testing");

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
        if (SelectedChoice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var provider = BuildConfig();
            await _config.SaveProviderAsync(provider).ConfigureAwait(true);
            _current = provider;

            // 保存后才真正落库：把它挂回下拉项，并从"未保存"里摘掉
            SelectedChoice.Attach(provider);
            _unsavedCustoms.Remove(SelectedChoice);

            IsStatusError = false;
            StatusMessage = Loc.Get("Provider.Saved");
            _status.ReportSuccess(Loc.Get("Provider.SavedToast"));

            // 供应商变化会影响新会话的默认设置，通知主窗口刷新
            ProvidersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = ChatErrorMapper.ToUserMessage(ex) ?? Loc.Get("Provider.SaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>由表单内容构造待保存的配置对象。</summary>
    private ProviderConfig BuildConfig()
    {
        var preset = CurrentPreset;

        var provider = _current ?? new ProviderConfig { PresetId = preset.Id };
        provider.PresetId = preset.Id;
        // 内置预设不需要自己的名字；自定义的没填名字就退回一个默认名，
        // 否则下拉里会冒出"未命名供应商"这种没法辨认的条目
        provider.Name = preset == ProviderPresets.CustomPreset
            ? ProviderName.Trim() is { Length: > 0 } name ? name : Loc.Get("Provider.CustomName")
            : string.Empty;
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
