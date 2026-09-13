using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Piable.Helpers;

/// <summary>
/// 供应商芯片的选中高亮：把「芯片自身的 ProviderChoice」与「VM 的 SelectedChoice」做引用比较，
/// 选中时返回强调色画刷，否则返回默认表面/边框色。
/// 用 MultiBinding + 此转换器代替 DataTrigger（Avalonia 12 核心已移除 DataTrigger，
/// 它属于独立的 Behaviors 包，本项目未引用），从而无需新增依赖。
/// ConverterParameter 区分要算背景还是边框："border" 算边框色，其余算背景色。
/// 画刷从 Application.Resources 现取，随当前主题取值。
/// </summary>
public sealed class ProviderSelectedConverter : IMultiValueConverter
{
    public object? Convert(
        IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSelected = values.Count == 2 && ReferenceEquals(values[0], values[1]);

        var key = parameter as string == "border"
            ? (isSelected ? "AccentBrush" : "BorderSubtle")
            : (isSelected ? "AccentSubtle" : "SurfaceBackground");

        if (Application.Current?.TryGetResource(key, out var res) == true && res is IBrush brush)
        {
            return brush;
        }

        return null;
    }
}
