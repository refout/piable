using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Piable.ViewModels;

namespace Piable.Helpers;

/// <summary>
/// 供应商按钮组的 IDataTemplate。Avalonia 12 已移除 DataTemplateSelector，
/// 改为直接实现 IDataTemplate，在 Build 里按项类型把渲染分派给各枚 DataTemplate
/// （普通供应商 → 图标芯片，「+ 新建」哨兵 → 新建按钮，「删除」哨兵 → 删除按钮）。
/// </summary>
public sealed class ProviderSelectorTemplate : IDataTemplate
{
    /// <summary>普通供应商芯片模板。</summary>
    public IDataTemplate? ProviderTemplate { get; set; }

    /// <summary>「+ 新建」按钮模板。</summary>
    public IDataTemplate? NewTemplate { get; set; }

    /// <summary>「删除」按钮模板（仅自定义供应商可见）。</summary>
    public IDataTemplate? DeleteTemplate { get; set; }

    public Control? Build(object? param)
    {
        var template = param switch
        {
            NewProviderSentinel => NewTemplate,
            DeleteProviderSentinel => DeleteTemplate,
            _ => ProviderTemplate,
        };
        return template?.Build(param);
    }

    public bool Match(object? data) => data is ProviderChoice or NewProviderSentinel or DeleteProviderSentinel;
}
