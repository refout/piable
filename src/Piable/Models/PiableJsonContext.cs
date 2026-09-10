using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piable.Models;

/// <summary>
/// 全应用唯一的 JSON 序列化入口。
/// 项目已设置 <c>JsonSerializerIsReflectionEnabledByDefault=false</c>，
/// 因此任何序列化都必须经由本源生成器上下文，运行时不做反射，AOT 下可正常工作。
/// </summary>
// 枚举统一由各枚举类型上的 [JsonConverter(typeof(JsonStringEnumConverter<T>))] 处理，
// 以便输出稳定的原始成员名（"OpenAICompatible"）而非受命名策略影响的形式，
// 保证导出文件在版本间可读、可手改、可重新导入。
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProviderConfig), TypeInfoPropertyName = "ProviderConfig")]
[JsonSerializable(typeof(List<ProviderConfig>), TypeInfoPropertyName = "ProviderConfigList")]
[JsonSerializable(typeof(Agent), TypeInfoPropertyName = "Agent")]
[JsonSerializable(typeof(List<Agent>), TypeInfoPropertyName = "AgentList")]
[JsonSerializable(typeof(McpServerConfig), TypeInfoPropertyName = "McpServerConfig")]
[JsonSerializable(typeof(List<McpServerConfig>), TypeInfoPropertyName = "McpServerConfigList")]
[JsonSerializable(typeof(SkillDefinition), TypeInfoPropertyName = "SkillDefinition")]
[JsonSerializable(typeof(List<SkillDefinition>), TypeInfoPropertyName = "SkillDefinitionList")]
[JsonSerializable(typeof(ChatSession), TypeInfoPropertyName = "ChatSession")]
[JsonSerializable(typeof(List<ChatSession>), TypeInfoPropertyName = "ChatSessionList")]
[JsonSerializable(typeof(ChatSessionSummary), TypeInfoPropertyName = "ChatSessionSummary")]
[JsonSerializable(typeof(List<ChatSessionSummary>), TypeInfoPropertyName = "ChatSessionSummaryList")]
[JsonSerializable(typeof(ChatMessage), TypeInfoPropertyName = "ChatMessage")]
[JsonSerializable(typeof(List<ChatMessage>), TypeInfoPropertyName = "ChatMessageList")]
[JsonSerializable(typeof(UserPreferences), TypeInfoPropertyName = "UserPreferences")]
[JsonSerializable(typeof(ToolCallPayload), TypeInfoPropertyName = "ToolCallPayload")]
// 以下三个是数据库中的 JSON 列与模型列表响应用到的容器类型，
// 显式命名属性以免依赖生成器的默认命名规则。
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "StringList")]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "StringDictionary")]
[JsonSerializable(typeof(JsonElement), TypeInfoPropertyName = "JsonElement")]
// 模型列表接口的响应体。字段名与各厂商实际返回保持一致，未命中的厂商返回空列表。
[JsonSerializable(typeof(OpenAiModelListResponse))]
[JsonSerializable(typeof(OllamaModelListResponse))]
internal sealed partial class PiableJsonContext : JsonSerializerContext;

/// <summary>OpenAI 风格模型列表响应：<c>{ "data": [ { "id": "..." } ] }</c>。</summary>
internal sealed class OpenAiModelListResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiModelEntry>? Data { get; set; }
}

internal sealed class OpenAiModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>Ollama 原生模型列表响应：<c>{ "models": [ { "name": "..." } ] }</c>。</summary>
internal sealed class OllamaModelListResponse
{
    [JsonPropertyName("models")]
    public List<OllamaModelEntry>? Models { get; set; }
}

internal sealed class OllamaModelEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}
