using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Models;
using Piable.Services;

namespace Piable.ViewModels;

/// <summary>「配置 → 智能体管理」标签页。</summary>
public sealed partial class AgentConfigViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IStatusReporter _status;

    /// <summary>正在编辑的智能体副本。改动先落在这里，点保存才写库。</summary>
    private Agent? _editing;

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

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public AgentConfigViewModel(IConfigService config, IStatusReporter status)
    {
        _config = config;
        _status = status;
    }

    public ObservableCollection<AgentListItemViewModel> Agents { get; } = [];

    /// <summary>智能体列表发生变化（增删改或改了默认项），主窗口需要重新读取。</summary>
    public event EventHandler? AgentsChanged;

    /// <summary>智能体是否有改动待保存。</summary>
    public bool HasSelection => SelectedAgent is not null;

    /// <summary>内置智能体不允许删除。</summary>
    public bool CanDeleteSelected => SelectedAgent?.CanDelete ?? false;

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
        OnPropertyChanged(nameof(CanDeleteSelected));

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
        StatusMessage = string.Empty;
        IsStatusError = false;
    }

    [RelayCommand]
    private async Task NewAgentAsync()
    {
        var agent = new Agent
        {
            Name = "新智能体",
            SystemPrompt = "你是一个乐于助人的 AI 助手。",
        };

        await _config.SaveAgentAsync(agent).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);

        SelectedAgent = Agents.FirstOrDefault(a => a.Id == agent.Id);
        _status.ReportSuccess("已新建智能体");
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
            StatusMessage = "⚠️ 名称不能为空";
            return;
        }

        _editing.Name = Name.Trim();
        _editing.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        _editing.SystemPrompt = SystemPrompt;

        if (FollowProviderDefaults)
        {
            _editing.Model = null;
            _editing.Temperature = null;
            _editing.MaxTokens = null;
            _editing.TopP = null;
        }
        else
        {
            _editing.Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim();
            _editing.Temperature = Temperature;
            _editing.MaxTokens = MaxTokens;
            _editing.TopP = TopP;
        }

        await _config.SaveAgentAsync(_editing).ConfigureAwait(true);

        IsStatusError = false;
        StatusMessage = "✅ 已保存";
        _status.ReportSuccess($"智能体「{_editing.Name}」已保存");

        var id = _editing.Id;
        await ReloadAsync().ConfigureAwait(true);
        SelectedAgent = Agents.FirstOrDefault(a => a.Id == id);
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        if (_editing is null)
        {
            return;
        }

        _editing.IsDefault = true;
        await _config.SaveAgentAsync(_editing).ConfigureAwait(true);

        var id = _editing.Id;
        await ReloadAsync().ConfigureAwait(true);
        SelectedAgent = Agents.FirstOrDefault(a => a.Id == id);

        _status.ReportSuccess($"已将「{Name}」设为默认智能体");
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
        StatusMessage = "已恢复默认提示词，点击保存后生效";
    }

    /// <summary>删除智能体。由列表项二次确认后调用。</summary>
    public async Task DeleteAsync(string agentId)
    {
        await _config.DeleteAgentAsync(agentId).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        _status.ReportSuccess("智能体已删除");
    }
}
