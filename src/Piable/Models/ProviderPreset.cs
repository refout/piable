namespace Piable.Models;

/// <summary>
/// 内置供应商预设。定义在应用资源中、不落库，用于简化用户的供应商配置过程。
/// </summary>
public sealed class ProviderPreset
{
    /// <summary>唯一标识，如 "openai"、"deepseek"。</summary>
    public required string Id { get; init; }

    /// <summary>界面显示名称。</summary>
    public required string DisplayName { get; init; }

    /// <summary>协议类型。</summary>
    public required ProviderType ProviderType { get; init; }

    /// <summary>默认 API 地址。</summary>
    public required string DefaultEndpoint { get; init; }

    /// <summary>是否必须填写 API Key（本地部署的 Ollama 为 false）。</summary>
    public bool RequiresApiKey { get; init; }

    /// <summary>常见模型列表，作为用户配置的初始值。</summary>
    public IReadOnlyList<string> DefaultModels { get; init; } = [];

    /// <summary>默认输入价格（美元 / 1K tokens）。0 表示免费或待用户填写。</summary>
    public decimal InputPricePer1K { get; init; }

    /// <summary>默认输出价格（美元 / 1K tokens）。</summary>
    public decimal OutputPricePer1K { get; init; }

    /// <summary>获取模型列表的相对路径，如 "/models"；为 null 表示该预设不支持动态获取。</summary>
    public string? ModelsEndpoint { get; init; }

    /// <summary>模型列表的响应格式，决定如何解析。</summary>
    public ModelListFormat ModelListFormat { get; init; } = ModelListFormat.OpenAi;

    /// <summary>是否支持动态获取模型列表。</summary>
    public bool SupportsModelFetch => !string.IsNullOrWhiteSpace(ModelsEndpoint);
}
