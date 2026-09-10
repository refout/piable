namespace Piable.Models;

/// <summary>
/// 内置供应商预设清单。不落库，仅作为用户创建供应商配置时的模板与默认值来源。
/// </summary>
public static class ProviderPresets
{
    public const string OpenAi = "openai";
    public const string DeepSeek = "deepseek";
    public const string Ollama = "ollama";
    public const string AzureOpenAi = "azure-openai";

    /// <summary>全部内置预设，按界面展示顺序排列。</summary>
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new ProviderPreset
        {
            Id = OpenAi,
            DisplayName = "OpenAI",
            ProviderType = ProviderType.OpenAICompatible,
            DefaultEndpoint = "https://api.openai.com/v1",
            RequiresApiKey = true,
            DefaultModels = ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "o3-mini"],
            InputPricePer1K = 0.0025m,
            OutputPricePer1K = 0.010m,
            ModelsEndpoint = "/models",
            ModelListFormat = ModelListFormat.OpenAi,
        },
        new ProviderPreset
        {
            Id = DeepSeek,
            DisplayName = "DeepSeek",
            ProviderType = ProviderType.OpenAICompatible,
            DefaultEndpoint = "https://api.deepseek.com/v1",
            RequiresApiKey = true,
            DefaultModels = ["deepseek-chat", "deepseek-reasoner"],
            InputPricePer1K = 0.00027m,
            OutputPricePer1K = 0.0011m,
            ModelsEndpoint = "/models",
            ModelListFormat = ModelListFormat.OpenAi,
        },
        new ProviderPreset
        {
            Id = Ollama,
            DisplayName = "Ollama（本地）",
            ProviderType = ProviderType.OpenAICompatible,
            // Ollama 的 OpenAI 兼容层挂在 /v1 下，/v1/models 同样返回 OpenAI 格式
            DefaultEndpoint = "http://localhost:11434/v1",
            RequiresApiKey = false,
            // 本地模型因机器而异，不预设，交由"获取模型列表"拉取
            DefaultModels = [],
            InputPricePer1K = 0m,
            OutputPricePer1K = 0m,
            ModelsEndpoint = "/models",
            ModelListFormat = ModelListFormat.OpenAi,
        },
        new ProviderPreset
        {
            Id = AzureOpenAi,
            DisplayName = "Azure OpenAI",
            ProviderType = ProviderType.AzureOpenAI,
            DefaultEndpoint = "https://YOUR_RESOURCE.openai.azure.com/",
            RequiresApiKey = true,
            // Azure 使用部署名而非模型名，且各账户部署不同，只能手动填写
            DefaultModels = [],
            InputPricePer1K = 0m,
            OutputPricePer1K = 0m,
            ModelsEndpoint = null,
            ModelListFormat = ModelListFormat.OpenAi,
        },
    ];

    /// <summary>按 ID 查找预设；找不到返回 null（用户可能删除了预设对应的配置）。</summary>
    public static ProviderPreset? Find(string? presetId) =>
        string.IsNullOrWhiteSpace(presetId)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>获取预设；找不到时抛出，用于调用方已确信预设存在的场景。</summary>
    public static ProviderPreset Get(string presetId) =>
        Find(presetId) ?? throw new InvalidOperationException($"未知的供应商预设：{presetId}");
}

/// <summary>
/// 模型定价参考预设。<b>这些价格仅作填充便利，可能随官方调整而过期，用户可以随意覆盖。</b>
/// 单位：美元 / 1K tokens。
/// </summary>
public static class ModelPricingPresets
{
    public static IReadOnlyList<ModelPricingEntry> All { get; } =
    [
        new("gpt-4o", 0.0025m, 0.010m),
        new("gpt-4o-mini", 0.00015m, 0.0006m),
        new("gpt-4.1", 0.002m, 0.008m),
        new("o3-mini", 0.0011m, 0.0044m),
        new("deepseek-chat", 0.00027m, 0.0011m),
        new("deepseek-reasoner", 0.00055m, 0.00219m),
    ];

    /// <summary>按模型名精确匹配定价；找不到返回 null。</summary>
    public static ModelPricingEntry? Find(string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 一条模型定价参考值。定义为顶层类型而非 <see cref="ModelPricingPresets"/> 的嵌套类型：
/// XAML 的 x:DataType 无法解析嵌套类型名。
/// </summary>
public sealed record ModelPricingEntry(string Model, decimal InputPer1K, decimal OutputPer1K);
