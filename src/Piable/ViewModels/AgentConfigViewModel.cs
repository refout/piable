using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.ViewModels;

/// <summary>智能体编辑页里一个可挂载的 MCP 资源。</summary>
public sealed partial class SelectableResourceViewModel : ViewModelBase
{
    public SelectableResourceViewModel(McpResourceDescriptor resource)
    {
        Uri = resource.Uri;
        Name = resource.DisplayName;
        Detail = string.IsNullOrWhiteSpace(resource.MimeType)
            ? resource.ServerName
            : $"{resource.ServerName} · {resource.MimeType}";
        IsTemplate = resource.IsTemplate;
        Description = resource.Description;
    }

    public string Uri { get; }

    public string Name { get; }

    public string Detail { get; }

    public string? Description { get; }

    /// <summary>
    /// 资源模板（URI 带占位符）不能直接读，因此不允许挂载——
    /// 让它能勾上只会在生成时得到一条读失败的警告。
    /// </summary>
    public bool IsTemplate { get; }

    public bool CanMount => !IsTemplate;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>「配置 → 智能体管理」标签页。</summary>
public sealed partial class AgentConfigViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly ISkillService _skills;
    private readonly IStatusReporter _status;
    private readonly IMcpClientService? _mcp;

    /// <summary>正在编辑的智能体副本。改动先落在这里，点保存才写库。</summary>
    private Agent? _editing;

    /// <summary>
    /// 已挂载的资源 URI。独立于 <see cref="AvailableResources"/> 维护：
    /// 资源列表要连上 MCP 服务器才拿得到，而保存不该依赖于"用户点过刷新"——
    /// 否则没点刷新就保存，会把之前挂载的资源无声地清空。
    /// </summary>
    private readonly List<string> _mountedResourceUris = [];

    [ObservableProperty]
    private AgentListItemViewModel? _selectedAgent;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    /// <summary>"跟随供应商默认"：勾选时模型与采样参数都留空，由供应商决定。</summary>
    [ObservableProperty]
    private bool _followProviderDefaults = true;

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private double _temperature = 0.7;

    [ObservableProperty]
    private int _maxTokens = 2048;

    [ObservableProperty]
    private double _topP = 1.0;

    /// <summary>是否有可导出的内容（已选中的智能体，或正在编辑的草稿）。</summary>
    public bool CanExport => _editing is not null;

    /// <summary>
    /// 是否允许执行被标记为危险的工具。
    /// 默认关闭，需要用户按智能体显式打开（模型可能被提示注入诱导去调用危险工具）。
    /// </summary>
    [ObservableProperty]
    private bool _allowDangerousTools;

    /// <summary>勾选了危险技能但尚未授权时给出提示，避免用户以为它已经在工作。</summary>
    public bool HasUngrantedDangerousSkill =>
        !AllowDangerousTools
        && AvailableSkills.Any(s => s.IsSelected && s.IsDangerous);

    /// <summary>列表为空时界面给出引导文案。用显式布尔属性而非对 Count 取反。</summary>
    public bool HasAvailableSkills => AvailableSkills.Count > 0;

    public bool HasAvailableMcpServers => AvailableMcpServers.Count > 0;

    public bool HasAvailableResources => AvailableResources.Count > 0;

    /// <summary>正在拉取资源列表。连接 MCP 服务器可能要等子进程启动，得给个反馈。</summary>
    [ObservableProperty]
    private bool _isLoadingResources;

    /// <summary>正在保存智能体。保存会写库并重新加载列表，期间禁用保存按钮并显示转圈。</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    partial void OnAllowDangerousToolsChanged(bool value) =>
        OnPropertyChanged(nameof(HasUngrantedDangerousSkill));

    public AgentConfigViewModel(
        IConfigService config,
        ISkillService skills,
        IStatusReporter status,
        IMcpClientService? mcp = null)
    {
        _config = config;
        _skills = skills;
        _status = status;
        _mcp = mcp;
    }

    public ObservableCollection<AgentListItemViewModel> Agents { get; } = [];

    /// <summary>可关联的技能，勾选状态随所选智能体变化。</summary>
    public ObservableCollection<SelectableLinkViewModel> AvailableSkills { get; } = [];

    /// <summary>可关联的 MCP 服务器。</summary>
    public ObservableCollection<SelectableLinkViewModel> AvailableMcpServers { get; } = [];

    /// <summary>
    /// 已关联 MCP 服务器提供的资源。需要点"刷新"才会拉取——
    /// 打开编辑页就挨个连服务器，会让页面在有多台 Stdio 服务器时卡上好几秒。
    /// </summary>
    public ObservableCollection<SelectableResourceViewModel> AvailableResources { get; } = [];

    /// <summary>智能体列表发生变化（增删改或改了默认项），主窗口需要重新读取。</summary>
    public event EventHandler? AgentsChanged;

    /// <summary>智能体是否有改动待保存。</summary>
    public bool HasSelection => SelectedAgent is not null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Agents.Clear();
        foreach (var agent in await _config.GetAgentsAsync(ct).ConfigureAwait(true))
        {
            var item = new AgentListItemViewModel(agent);

            // 内置智能体的删除按钮在界面上已隐藏，这里再挡一道，
            // 保证二次确认事件不会绕过 IsBuiltIn 的约束。
            if (item.CanDelete)
            {
                item.DeleteConfirmed += (_, vm) => _ = DeleteAsync(((AgentListItemViewModel)vm).Id);
            }

            Agents.Add(item);
        }

        SelectedAgent = Agents.FirstOrDefault(a => a.IsDefault) ?? Agents.FirstOrDefault();
    }

    /// <summary>重新载入列表并通知主窗口——主窗口缓存了智能体清单，增删改后必须同步。</summary>
    private async Task ReloadAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(true);
        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedAgentChanged(AgentListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        if (value is null)
        {
            _editing = null;
            return;
        }

        _ = LoadEditorAsync(value.Id);
    }

    private async Task LoadEditorAsync(string agentId)
    {
        var agent = await _config.GetAgentAsync(agentId).ConfigureAwait(true);
        if (agent is null)
        {
            return;
        }

        // 编辑副本，取消时不影响已保存的数据
        _editing = new Agent
        {
            Id = agent.Id,
            Name = agent.Name,
            Description = agent.Description,
            SystemPrompt = agent.SystemPrompt,
            Model = agent.Model,
            Temperature = agent.Temperature,
            MaxTokens = agent.MaxTokens,
            TopP = agent.TopP,
            McpServerIds = [.. agent.McpServerIds],
            SkillIds = [.. agent.SkillIds],
            IsDefault = agent.IsDefault,
            IsBuiltIn = agent.IsBuiltIn,
            CreatedAt = agent.CreatedAt,
        };

        Name = agent.Name;
        Description = agent.Description;
        SystemPrompt = agent.SystemPrompt;
        Model = agent.Model;
        FollowProviderDefaults = agent.Model is null
                                 && agent.Temperature is null
                                 && agent.MaxTokens is null
                                 && agent.TopP is null;
        Temperature = agent.Temperature ?? 0.7;
        MaxTokens = agent.MaxTokens ?? 2048;
        TopP = agent.TopP ?? 1.0;
        AllowDangerousTools = agent.AllowDangerousTools;
        StatusMessage = string.Empty;
        IsStatusError = false;

        await LoadLinkOptionsAsync(agent).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasUngrantedDangerousSkill));
    }

    /// <summary>载入可关联的技能与 MCP 服务器，并按当前智能体的配置勾选。</summary>
    private async Task LoadLinkOptionsAsync(Agent agent)
    {
        DetachLinkHandlers();

        var skillIds = new HashSet<string>(agent.SkillIds, StringComparer.Ordinal);
        var mcpIds = new HashSet<string>(agent.McpServerIds, StringComparer.Ordinal);

        AvailableSkills.Clear();
        foreach (var skill in await _skills.GetAllAsync().ConfigureAwait(true))
        {
            var handler = SkillHandlers.Find(skill.Handler);
            AvailableSkills.Add(new SelectableLinkViewModel(
                skill.Id,
                skill.Name,
                skill.Enabled ? handler?.DisplayName ?? skill.Handler : Loc.Get("Skill.Disabled"),
                handler?.Risk == ToolRisk.Dangerous)
            {
                IsSelected = skillIds.Contains(skill.Id),
            });
        }

        AvailableMcpServers.Clear();
        foreach (var server in await _config.GetMcpServersAsync().ConfigureAwait(true))
        {
            AvailableMcpServers.Add(new SelectableLinkViewModel(
                server.Id,
                server.Name,
                server.Transport.ToString(),
                // 未声明只读的 MCP 工具一律按危险处理，这里如实标注
                isDangerous: true)
            {
                IsSelected = mcpIds.Contains(server.Id),
            });
        }

        AttachLinkHandlers();

        _mountedResourceUris.Clear();
        _mountedResourceUris.AddRange(agent.McpResourceUris);

        // 资源列表留到用户点刷新再拉，这里只清掉上一份，避免张冠李戴
        DetachResourceHandlers();
        AvailableResources.Clear();
        OnPropertyChanged(nameof(HasAvailableResources));

        OnPropertyChanged(nameof(HasAvailableSkills));
        OnPropertyChanged(nameof(HasAvailableMcpServers));
    }

    /// <summary>
    /// 拉取已关联 MCP 服务器的资源清单。
    /// 单台服务器连不上不影响其他服务器——这点与工具发现保持一致。
    /// </summary>
    [RelayCommand]
    private async Task RefreshResourcesAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        IsLoadingResources = true;
        StatusMessage = Loc.Get("Agent.ReadingResources");
        IsStatusError = false;

        var selected = AvailableMcpServers.Where(s => s.IsSelected)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        var collected = new List<SelectableResourceViewModel>();
        var errors = new List<string>();

        try
        {
            var servers = await _config.GetMcpServersAsync().ConfigureAwait(true);

            foreach (var server in servers.Where(s => s.Enabled && selected.Contains(s.Id)))
            {
                try
                {
                    foreach (var resource in await _mcp.GetResourcesAsync(server).ConfigureAwait(true))
                    {
                        collected.Add(new SelectableResourceViewModel(resource)
                        {
                            IsSelected = _mountedResourceUris.Contains(resource.Uri),
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"「{server.Name}」：{ex.Message}");
                }
            }
        }
        finally
        {
            IsLoadingResources = false;
        }

        DetachResourceHandlers();
        AvailableResources.Clear();

        foreach (var item in collected.OrderBy(r => r.Detail, StringComparer.Ordinal)
                                      .ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            AvailableResources.Add(item);
        }

        AttachResourceHandlers();
        OnPropertyChanged(nameof(HasAvailableResources));

        if (errors.Count > 0)
        {
            IsStatusError = true;
            StatusMessage = string.Join(Environment.NewLine, errors);
            return;
        }

        IsStatusError = false;
        StatusMessage = collected.Count == 0
            ? Loc.Get("Agent.NoResources")
            : Loc.Get("Agent.ResourcesFound", collected.Count);
    }

    private void AttachResourceHandlers()
    {
        foreach (var item in AvailableResources)
        {
            item.PropertyChanged += OnResourceSelectionChanged;
        }
    }

    private void DetachResourceHandlers()
    {
        foreach (var item in AvailableResources)
        {
            item.PropertyChanged -= OnResourceSelectionChanged;
        }
    }

    private void OnResourceSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableResourceViewModel.IsSelected)
            || sender is not SelectableResourceViewModel item)
        {
            return;
        }

        if (item.IsSelected)
        {
            if (!_mountedResourceUris.Contains(item.Uri))
            {
                _mountedResourceUris.Add(item.Uri);
            }
        }
        else
        {
            _mountedResourceUris.Remove(item.Uri);
        }
    }

    private void AttachLinkHandlers()
    {
        foreach (var item in AvailableSkills.Concat(AvailableMcpServers))
        {
            item.PropertyChanged += OnLinkSelectionChanged;
        }
    }

    private void DetachLinkHandlers()
    {
        foreach (var item in AvailableSkills.Concat(AvailableMcpServers))
        {
            item.PropertyChanged -= OnLinkSelectionChanged;
        }
    }

    private void OnLinkSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableLinkViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(HasUngrantedDangerousSkill));
        }
    }

    /// <summary>
    /// 新建一个未保存的草稿，而不是立刻写库。
    /// 与技能页、MCP 页保持一致：点"新建"只是打开一张空表单，点保存才真正创建，
    /// 否则用户每次误点都会永久多出一条"新智能体"。
    /// </summary>
    [RelayCommand]
    private void NewAgent()
    {
        // 先清空选中项（它会一并清掉 _editing），再放上新草稿
        SelectedAgent = null;
        _editing = new Agent
        {
            Name = Loc.Get("Agent.NewName"),
            SystemPrompt = "你是一个乐于助人的 AI 助手。",
        };

        Name = _editing.Name;
        Description = null;
        SystemPrompt = _editing.SystemPrompt;
        FollowProviderDefaults = true;
        Model = null;
        Temperature = 0.7;
        MaxTokens = 2048;
        TopP = 1.0;
        AllowDangerousTools = false;

        foreach (var link in AvailableSkills.Concat(AvailableMcpServers))
        {
            link.IsSelected = false;
        }

        _mountedResourceUris.Clear();
        DetachResourceHandlers();
        AvailableResources.Clear();
        OnPropertyChanged(nameof(HasAvailableResources));

        StatusMessage = Loc.Get("Agent.FillToCreate");
        IsStatusError = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_editing is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Common.NameRequired");
            return;
        }

        var agent = BuildFromForm();
        _editing = agent;

        IsBusy = true;
        try
        {
            await _config.SaveAgentAsync(agent).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFailure(Loc.Get("Common.SaveFailed"), ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        IsStatusError = false;
        StatusMessage = Loc.Get("Common.Saved");
        _status.ReportSuccess(Loc.Get("Agent.Saved", agent.Name));

        var id = agent.Id;
        await ReloadAsync().ConfigureAwait(true);
        SelectedAgent = Agents.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// 按当前表单内容构造一个智能体对象。
    /// 保存与导出共用这一份构造逻辑——分头写就会出现"导出去的和存下来的不一样"。
    /// 不改动 <see cref="_editing"/>，导出草稿时不会把未保存的值写进编辑副本。
    /// </summary>
    private Agent BuildFromForm()
    {
        var agent = new Agent
        {
            // Id 与创建时间沿用编辑副本；全新草稿在这里拿到新 Id
            Id = _editing?.Id ?? Guid.NewGuid().ToString("n"),
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            SystemPrompt = SystemPrompt,
            AllowDangerousTools = AllowDangerousTools,
            SkillIds = [.. AvailableSkills.Where(s => s.IsSelected).Select(s => s.Id)],
            McpServerIds = [.. AvailableMcpServers.Where(s => s.IsSelected).Select(s => s.Id)],
            McpResourceUris = [.. _mountedResourceUris],
            IsDefault = _editing?.IsDefault ?? false,
            IsBuiltIn = _editing?.IsBuiltIn ?? false,
            CreatedAt = _editing?.CreatedAt ?? DateTimeOffset.Now,
        };

        if (!FollowProviderDefaults)
        {
            agent.Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim();
            agent.Temperature = Temperature;
            agent.MaxTokens = MaxTokens;
            agent.TopP = TopP;
        }

        return agent;
    }

    /// <summary>
    /// 把当前表单内容序列化为 JSON 文本，供导出到文件。
    ///
    /// 绕开默认的转义编码器：System.Text.Json 默认会把非 ASCII 字符写成
    /// <c>\uXXXX</c>，中文提示词会变成一长串看不懂的转义，
    /// 而导出文件的用途之一就是让人打开来改。这里仍是同一个源生成器上下文，
    /// 只是换掉了写出时的编码器。
    /// </summary>
    public string ExportToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            JsonSerializer.Serialize(writer, BuildFromForm(), PiableJsonContext.Default.Agent);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 从 JSON 文本导入一个智能体并落库。
    /// 文件对话框由视图发起，这里只负责解析与保存，便于脱离界面测试。
    /// </summary>
    public async Task ImportFromJsonAsync(string json)
    {
        Agent? agent;
        try
        {
            agent = JsonSerializer.Deserialize(json, PiableJsonContext.Default.Agent);
        }
        catch (JsonException)
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Agent.ImportBadJson");
            return;
        }

        if (agent is null || string.IsNullOrWhiteSpace(agent.Name))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Agent.ImportNoName");
            return;
        }

        // 外部文件的这几个标记一律不采信：
        // - Id 重新生成，导入不会覆盖本机已有的智能体（重名也会另存一条）
        // - IsBuiltIn 若被带入，导入的智能体就变成"不可删除"了
        // - IsDefault 若被带入，一次导入会悄悄改掉全局默认智能体
        agent.Id = Guid.NewGuid().ToString("n");
        agent.IsBuiltIn = false;
        agent.IsDefault = false;
        agent.CreatedAt = DateTimeOffset.Now;

        try
        {
            await _config.SaveAgentAsync(agent).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFailure(Loc.Get("Agent.ImportFailed"), ex);
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
        SelectedAgent = Agents.FirstOrDefault(a => a.Id == agent.Id);

        IsStatusError = false;
        StatusMessage = Loc.Get("Agent.Imported", agent.Name);
        _status.ReportSuccess(StatusMessage);
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        if (_editing is null)
        {
            return;
        }

        _editing.IsDefault = true;

        try
        {
            await _config.SaveAgentAsync(_editing).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFailure(Loc.Get("Agent.SetDefaultFailed"), ex);
            return;
        }

        var id = _editing.Id;
        await ReloadAsync().ConfigureAwait(true);
        SelectedAgent = Agents.FirstOrDefault(a => a.Id == id);

        _status.ReportSuccess(Loc.Get("Agent.SetDefault", Name));
    }

    /// <summary>
    /// 把写库失败反馈到界面。
    /// 不处理的话异常会被 AsyncRelayCommand 静默吞掉，用户看到的是一切正常，
    /// 但数据其实没存进去——这比报错更糟。
    /// </summary>
    private void ReportFailure(string action, Exception ex)
    {
        var reason = ChatErrorMapper.ToUserMessage(ex) ?? ex.Message;
        IsStatusError = true;
        StatusMessage = Loc.Get("Common.FailureFormat", action, reason);
        _status.ReportError(Loc.Get("Common.FailureFormat", action, reason));
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (_editing is null)
        {
            return;
        }

        // 恢复内置提示词：直接改写当前编辑区，仍需用户点保存才落库
        SystemPrompt = _editing.Id == ConfigService.CoderAgentId
            ? "你是一位经验丰富的软件工程师。回答编程问题时，"
              + "优先给出可运行的代码，并简要说明关键设计取舍与潜在陷阱。"
              + "代码块请标注语言。如果用户的前提有误，直接指出。"
            : "你是一个专业、可靠的 AI 助手。请用简洁清晰的中文回答，"
              + "在不确定时如实说明，不要编造事实。涉及代码或步骤时使用 Markdown 排版。";

        FollowProviderDefaults = true;
        Model = null;
        Temperature = 0.7;
        MaxTokens = 2048;
        TopP = 1.0;

        IsStatusError = false;
        StatusMessage = Loc.Get("Agent.RestoredDefaults");
    }

    /// <summary>删除智能体。由列表项二次确认后调用。</summary>
    public async Task DeleteAsync(string agentId)
    {
        try
        {
            await _config.DeleteAgentAsync(agentId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFailure(Loc.Get("Common.DeleteFailed"), ex);
            return;
        }

        await ReloadAsync().ConfigureAwait(true);

        // 存储层对内置智能体是静默拒绝的，这里必须复核结果——
        // 否则界面会报"已删除"而列表里那条还在。
        if (Agents.Any(a => a.Id == agentId))
        {
            _status.ReportError(Loc.Get("Agent.BuiltInUndeletable"));
            return;
        }

        _status.ReportSuccess(Loc.Get("Agent.Deleted"));
    }
}
