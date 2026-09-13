using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;
using Piable.Models;
using Piable.ViewModels;

namespace Piable.Helpers;

/// <summary>
/// 把「选择供应商」里的一项 <see cref="ProviderChoice"/> 映射到对应的 Fluent 图标。
/// FluentIcons 不含各厂家的品牌 Logo，这里用语义相近的通用图标代表：
/// OpenAI → 火花（通用 AI 意象）、DeepSeek → 大脑（推理）、Ollama → 服务器（本地服务）、
/// Azure OpenAI → 云、自定义供应商 → 拼图块。
/// </summary>
public sealed class ProviderIconConverter : IValueConverter
{
    public object? Convert(
        object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProviderChoice choice)
        {
            return null;
        }

        // 自定义供应商（含尚未命名/未保存的）一律用拼图块
        if (choice.IsCustom)
        {
            return Icon.PuzzlePiece;
        }

        return choice.Preset?.Id switch
        {
            ProviderPresets.OpenAi => Icon.Sparkle,
            ProviderPresets.DeepSeek => Icon.Brain,
            ProviderPresets.Ollama => Icon.Server,
            ProviderPresets.AzureOpenAi => Icon.Cloud,
            _ => Icon.PuzzlePiece,
        };
    }

    public object? ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
