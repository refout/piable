using System.Text.Json.Serialization;

namespace Piable.Models;

/// <summary>
/// 用户创建的一条供应商配置实例。ApiKey 在此对象中为明文，
/// 落库时由存储层加密（见 EncryptionHelper），读取时解密。
/// </summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>关联的内置供应商预设 ID；自定义供应商使用 <see cref="ProviderPresets.Custom"/>。</summary>
    public string PresetId { get; set; } = string.Empty;

    /// <summary>
    /// 供应商在界面上的名字。自定义供应商由用户填写（如「公司网关」「本地 vLLM」）；
    /// 内置预设留空，显示时用预设自己的名字。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>明文 API Key；Ollama 等本地供应商可为 null。</summary>
    public string? ApiKey { get; set; }

    /// <summary>覆盖预设的默认 Endpoint；为 null 时使用预设值。</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI 专用部署名。</summary>
    public string? DeploymentName { get; set; }

    /// <summary>最终生效的模型列表（预设 + 动态获取 + 手动添加去重后的结果）。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>默认使用的模型。</summary>
    public string DefaultModel { get; set; } = string.Empty;

    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public double? TopP { get; set; }

    public decimal InputPricePer1K { get; set; }
    public decimal OutputPricePer1K { get; set; }

    /// <summary>是否为默认供应商配置。全局至多一条为 true。</summary>
    public bool IsDefault { get; set; }

    /// <summary>模型列表最后一次成功获取的时间。</summary>
    public DateTimeOffset? ModelListUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 界面上显示的名字：自定义供应商用自己的 <see cref="Name"/>，
    /// 内置预设用预设名；两者都没有时退回预设 ID，至少不让界面出现空白。
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name)
            ? Name
            : ProviderPresets.Find(PresetId)?.DisplayName ?? PresetId;

    /// <summary>是否为用户自建的供应商（可改名、可删除）。</summary>
    [JsonIgnore]
    public bool IsCustom => ProviderPresets.IsCustom(PresetId);

    /// <summary>该配置是否已具备发起请求的最低条件。</summary>
    [JsonIgnore]
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(DefaultModel)
        && (!string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Endpoint));
}
