using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;
using Piable.Models;

namespace Piable.ViewModels;

/// <summary>
/// 「选择供应商」下拉里的一项：要么是一个内置预设，要么是一条自定义供应商。
/// </summary>
/// <remarks>
/// 为什么需要这么一层：内置预设是代码里的常量，一条预设最多对应一条配置；
/// 自定义供应商却是用户创建的数据，可以有任意多条。两者混在一个下拉里，
/// ComboBox 的选中项就必须是同一种类型——否则"选中了什么"根本没法表达。
/// </remarks>
public sealed class ProviderChoice : ObservableObject
{
    private ProviderConfig? _provider;

    /// <summary>内置预设项。</summary>
    private ProviderChoice(ProviderPreset preset, ProviderConfig? existing)
    {
        Preset = preset;
        _provider = existing;
    }

    /// <summary>自定义供应商项（可以是已保存的，也可以是刚新建尚未落库的）。</summary>
    private ProviderChoice(ProviderConfig provider) => _provider = provider;

    public static ProviderChoice ForPreset(ProviderPreset preset, ProviderConfig? existing) =>
        new(preset, existing);

    public static ProviderChoice ForCustom(ProviderConfig provider) => new(provider);

    /// <summary>内置预设；自定义项为 null。</summary>
    public ProviderPreset? Preset { get; }

    /// <summary>
    /// 已存在的配置。内置预设在用户第一次选中前可能是 null（尚未落库）；
    /// 自定义项必定有值——新建时先在内存里建好对象，保存时才写库。
    /// </summary>
    public ProviderConfig? Provider => _provider;

    public bool IsBuiltIn => Preset is not null;

    public bool IsCustom => Preset is null;

    /// <summary>下拉里显示的名字：内置用预设名，自定义用自己的名字。</summary>
    public string DisplayName =>
        Preset is not null
            ? Preset.DisplayName
            : string.IsNullOrWhiteSpace(_provider?.Name) ? Loc.Get("Provider.Unnamed") : _provider!.Name;

    /// <summary>把落库后的配置挂到这一项上，避免下次选中时重复创建。</summary>
    public void Attach(ProviderConfig provider)
    {
        _provider = provider;
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>改名后要通知界面，否则下拉里显示的名字不会跟着变。</summary>
    public void Rename(string name)
    {
        if (_provider is null)
        {
            return;
        }

        _provider.Name = name;
        OnPropertyChanged(nameof(DisplayName));
    }
}
