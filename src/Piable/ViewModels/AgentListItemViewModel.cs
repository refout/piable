using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;
using Piable.Models;

namespace Piable.ViewModels;

/// <summary>智能体管理页中的一行。</summary>
public sealed partial class AgentListItemViewModel : InlineConfirmViewModel
{
    [ObservableProperty]
    private bool _isSelected;

    public AgentListItemViewModel(Agent agent)
    {
        Id = agent.Id;
        IsBuiltIn = agent.IsBuiltIn;
        Update(agent);
    }

    public string Id { get; }

    public bool IsBuiltIn { get; }

    /// <summary>内置智能体不可删除，界面据此隐藏删除按钮。</summary>
    public bool CanDelete => !IsBuiltIn;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private bool _isDefault;

    /// <summary>行标题。默认智能体在界面上用星标图标（Star）标记，内置智能体带后缀。</summary>
    public string DisplayName => Name + (IsBuiltIn ? Loc.Get("Agent.BuiltInSuffix") : string.Empty);

    public void Update(Agent agent)
    {
        Name = agent.Name;
        Description = agent.Description;
        IsDefault = agent.IsDefault;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CanDelete));
    }
}
