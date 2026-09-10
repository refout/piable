using CommunityToolkit.Mvvm.ComponentModel;

namespace Piable.ViewModels;

/// <summary>智能体编辑页里用于勾选关联项的条目（技能或 MCP 服务器）。</summary>
public sealed partial class SelectableLinkViewModel : ViewModelBase
{
    public SelectableLinkViewModel(string id, string name, string detail, bool isDangerous = false)
    {
        Id = id;
        Name = name;
        Detail = detail;
        IsDangerous = isDangerous;
    }

    public string Id { get; }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>危险项在勾选列表上就要标出来，不能等执行时才让用户知道。</summary>
    public bool IsDangerous { get; }

    [ObservableProperty]
    private bool _isSelected;
}
