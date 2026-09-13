namespace Piable.Helpers;

/// <summary>
/// 供应商选择按钮组末尾那枚「+ 新建」的哨兵项。把它和 <see cref="Piable.ViewModels.ProviderChoice"/>
/// 混在同一个换行流里，这样「新建」能像普通供应商一样参与自动换行——同一行放得下就紧跟在
/// 供应商后面，放不下就整体换到下一行，而不是独占一行或对右对齐。
/// </summary>
public sealed class NewProviderSentinel
{
    public static readonly NewProviderSentinel Instance = new();
}
