using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;
using Piable.Models;
using Piable.Services.Tools;

namespace Piable.ViewModels;

/// <summary>技能管理页中的一行。</summary>
public sealed partial class SkillListItemViewModel : InlineConfirmViewModel
{
    [ObservableProperty]
    private bool _isSelected;

    public SkillListItemViewModel(SkillDefinition skill, ISkillHandler? handler)
    {
        Id = skill.Id;
        _toolName = skill.ToolName;
        _name = skill.Name;
        _isEnabled = skill.Enabled;
        Risk = handler?.Risk ?? ToolRisk.Dangerous;
        HandlerKey = handler?.Key ?? skill.Handler;
        HandlerDisplayName = handler?.DisplayName ?? Loc.Get("Skill.UnknownHandler", skill.Handler);
    }

    public string Id { get; }

    public ToolRisk Risk { get; }

    public string HandlerKey { get; }

    public string HandlerDisplayName { get; }

    /// <summary>危险技能在关联到智能体后仍需智能体另行授权才会真正执行。</summary>
    public bool IsDangerous => Risk == ToolRisk.Dangerous;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _toolName;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>行标题：<c>执行命令（run_shell）</c>。危险性由界面图标（Warning/Checkmark）表达。</summary>
    public string DisplayName => $"{Name}（{ToolName}）";

    public string Subtitle => IsEnabled ? HandlerDisplayName : Loc.Get("Skill.DisabledSubtitle", HandlerDisplayName);

    public void Update(SkillDefinition skill)
    {
        Name = skill.Name;
        ToolName = skill.ToolName;
        IsEnabled = skill.Enabled;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
    }
}
