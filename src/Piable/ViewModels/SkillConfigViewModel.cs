using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Tools;

namespace Piable.ViewModels;

/// <summary>「配置 → 技能」标签页。</summary>
public sealed partial class SkillConfigViewModel : ViewModelBase
{
    private readonly ISkillService _skills;
    private readonly IStatusReporter _status;

    /// <summary>正在编辑的技能副本，点保存才落库。</summary>
    private SkillDefinition? _editing;

    [ObservableProperty]
    private SkillListItemViewModel? _selectedSkill;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _toolSpec = string.Empty;

    [ObservableProperty]
    private ISkillHandler? _selectedHandler;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>正在保存技能。写库并重新加载列表期间禁用保存按钮并显示转圈。</summary>
    [ObservableProperty]
    private bool _isBusy;

    public SkillConfigViewModel(ISkillService skills, IStatusReporter status)
    {
        _skills = skills;
        _status = status;
    }

    public ObservableCollection<SkillListItemViewModel> Skills { get; } = [];

    /// <summary>可选的本地实现。技能的动作只能来自这份清单，不能由用户任意指定。</summary>
    public IReadOnlyList<ISkillHandler> AvailableHandlers => SkillHandlers.All;

    public bool HasSelection => SelectedSkill is not null;

    /// <summary>当前所选处理器是否危险，用于在编辑区给出提示。</summary>
    public bool IsHandlerDangerous => SelectedHandler?.Risk == ToolRisk.Dangerous;

    public string HandlerRiskHint => IsHandlerDangerous
        ? Loc.Get("Skill.RiskDangerousHint")
        : Loc.Get("Skill.RiskSafeHint");

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var selectedId = SelectedSkill?.Id;

        Skills.Clear();
        foreach (var skill in await _skills.GetAllAsync(ct).ConfigureAwait(true))
        {
            var item = new SkillListItemViewModel(skill, SkillHandlers.Find(skill.Handler));
            item.DeleteConfirmed += (_, vm) => _ = DeleteAsync(((SkillListItemViewModel)vm).Id);
            Skills.Add(item);
        }

        SelectedSkill = Skills.FirstOrDefault(s => s.Id == selectedId) ?? Skills.FirstOrDefault();
    }

    partial void OnSelectedSkillChanged(SkillListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        if (value is null)
        {
            _editing = null;
            return;
        }

        _ = LoadEditorAsync(value.Id);
    }

    private async Task LoadEditorAsync(string id)
    {
        var skill = await _skills.GetAsync(id).ConfigureAwait(true);
        if (skill is null)
        {
            return;
        }

        _editing = new SkillDefinition
        {
            Id = skill.Id,
            Name = skill.Name,
            ToolName = skill.ToolName,
            Description = skill.Description,
            ToolSpec = skill.ToolSpec,
            Handler = skill.Handler,
            Enabled = skill.Enabled,
            CreatedAt = skill.CreatedAt,
        };

        Name = skill.Name;
        ToolName = skill.ToolName;
        Description = skill.Description;
        ToolSpec = skill.ToolSpec;
        SelectedHandler = AvailableHandlers.FirstOrDefault(
            h => string.Equals(h.Key, skill.Handler, StringComparison.OrdinalIgnoreCase));
        IsEnabled = skill.Enabled;
        StatusMessage = string.Empty;
        IsStatusError = false;
    }

    /// <summary>选中处理器时把建议的名称、描述、参数结构填进表单，用户可再改。</summary>
    partial void OnSelectedHandlerChanged(ISkillHandler? value)
    {
        OnPropertyChanged(nameof(IsHandlerDangerous));
        OnPropertyChanged(nameof(HandlerRiskHint));

        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = value.DisplayName;
        }

        ToolName = ToolNaming.Suggest(value.Key, value.Key);
        Description = value.SuggestedDescription;
        ToolSpec = value.SuggestedToolSpec;
    }

    [RelayCommand]
    private void NewSkill()
    {
        _editing = null;
        SelectedSkill = null;

        Name = string.Empty;
        ToolName = string.Empty;
        Description = string.Empty;
        ToolSpec = string.Empty;
        SelectedHandler = AvailableHandlers.FirstOrDefault();
        IsEnabled = true;
        StatusMessage = Loc.Get("Skill.FillToSave");
        IsStatusError = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedHandler is null)
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Skill.HandlerRequired");
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            IsStatusError = true;
            StatusMessage = Loc.Get("Common.NameRequired");
            return;
        }

        var skill = _editing ?? new SkillDefinition();
        skill.Name = Name.Trim();
        skill.ToolName = ToolName.Trim();
        skill.Description = Description.Trim();
        skill.ToolSpec = ToolSpec;
        skill.Handler = SelectedHandler.Key;
        skill.Enabled = IsEnabled;

        IsBusy = true;
        try
        {
            await _skills.SaveAsync(skill).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            IsBusy = false;
            IsStatusError = true;
            StatusMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            // 写库失败等意外情况同样要反馈，不能被 AsyncRelayCommand 静默吞掉
            IsBusy = false;
            IsStatusError = true;
            StatusMessage = Loc.Get("Mcp.SaveFailed", ChatErrorMapper.ToUserMessage(ex) ?? ex.Message);
            _status.ReportError(StatusMessage);
            return;
        }

        IsBusy = false;
        IsStatusError = false;
        StatusMessage = Loc.Get("Common.Saved");
        _status.ReportSuccess(Loc.Get("Skill.Saved", skill.Name));

        await LoadAsync().ConfigureAwait(true);
        SelectedSkill = Skills.FirstOrDefault(s => s.Id == skill.Id);
    }

    private async Task DeleteAsync(string id)
    {
        await _skills.DeleteAsync(id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        _status.ReportSuccess(Loc.Get("Skill.Deleted"));
    }
}
