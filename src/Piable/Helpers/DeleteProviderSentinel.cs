namespace Piable.Helpers;

/// <summary>
/// 供应商按钮组里「删除」哨兵项，紧跟在「+ 新建」之后，参与同一换行流。
/// 仅当选中自定义供应商时才可见（由 DeleteProviderTemplate 的 IsVisible 绑定控制），
/// 内置预设不显示——它们是界面的一部分，用户想做的是清空配置而非让预设消失。
/// </summary>
public sealed class DeleteProviderSentinel
{
    public static readonly DeleteProviderSentinel Instance = new();
}
