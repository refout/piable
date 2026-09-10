using System.Text.Json.Serialization;

namespace Piable.Models;

/// <summary>供应商协议类型。仅支持这两种，其余厂商均以 OpenAI 兼容协议接入。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderType>))]
public enum ProviderType
{
    /// <summary>OpenAI 兼容的 /chat/completions 协议（OpenAI、DeepSeek、Ollama、各类中转均属此类）。</summary>
    OpenAICompatible,

    /// <summary>Azure OpenAI，使用 deployment 名而非模型名，鉴权走 api-key 头。</summary>
    AzureOpenAI,
}

/// <summary>MCP 服务器传输方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpTransport>))]
public enum McpTransport
{
    /// <summary>本地子进程，通过 stdin/stdout 通信。</summary>
    Stdio,

    /// <summary>Server-Sent Events。</summary>
    Sse,

    /// <summary>可流式 HTTP。</summary>
    StreamableHttp,
}

/// <summary>消息角色。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>供应商返回模型列表时的响应格式。</summary>
public enum ModelListFormat
{
    /// <summary>OpenAI 风格：<c>{ "data": [ { "id": "gpt-4o" } ] }</c>。</summary>
    OpenAi,

    /// <summary>Ollama 风格：<c>{ "models": [ { "name": "llama3" } ] }</c>。</summary>
    Ollama,
}
