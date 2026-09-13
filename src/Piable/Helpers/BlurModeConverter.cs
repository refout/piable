using System.Globalization;
using Avalonia.Data.Converters;
using Piable.Helpers;
using Piable.ViewModels;

namespace Piable.Helpers;

/// <summary>
/// 在偏好里存取的模糊模式字符串 ("Off" / "Mica" / "AcrylicBlur") 与界面选项
/// <see cref="BlurModeOption"/> 之间互转。
///
/// 展示名随语言变化，所以不直接缓存，转换时现用 <see cref="Loc"/> 取——这样切换语言后
/// 即使选项集合没重建，已选中项的文字也会跟着变。
/// </summary>
public sealed class BlurModeConverter : IValueConverter
{
    public object? Convert(
        object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value as string;
        if (string.IsNullOrEmpty(mode))
        {
            return null;
        }

        return new BlurModeOption(mode, Loc.Get(BlurModeKey(mode)));
    }

    public object? ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BlurModeOption option ? option.Value : null;

    /// <summary>模糊模式值对应的文案键，如 "Mica" → "Pref.WindowBlur.Mica"。</summary>
    internal static string BlurModeKey(string mode) => $"Pref.WindowBlur.{mode}";
}
