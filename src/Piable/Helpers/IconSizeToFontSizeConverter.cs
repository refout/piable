using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Piable.Helpers;

/// <summary>
/// 把 FluentIcons 的 <c>IconSize</c>（如 Size12 / Size16）换算成对应的像素字号。
/// 这是必要的修复：FluentIcons 的 <c>FluentIcon</c> 只用 IconSize 选字体文件，
/// 并不会把渲染字号（FontSize）同步成 IconSize，FontSize 会停在默认值 20。
/// 20px 的字形在按钮这种内容高度受限、又带圆角裁剪的容器里会被裁掉，
/// 表现为「按钮只剩轮廓、图标不显示」。让 FontSize 跟随 IconSize 即可正常显示。
/// </summary>
public sealed class IconSizeToFontSizeConverter : IValueConverter
{
    public static readonly IconSizeToFontSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Enum size)
        {
            // 成员名形如 Size12 / Size16 / Size20 / Size24 / Size28 / Size32 / Size48 / Resizable
            var digits = new string(size.ToString().Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && double.TryParse(digits, NumberStyles.Integer, culture, out var px))
                return px;
        }

        // 兜底：本项目只用 Size12 / Size16；Resizable 等非常规情况给一个安全值。
        return 16.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
