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

/// <summary>
/// 工具的风险等级。决定模型请求调用它时是否需要智能体显式授权。
///
/// 分级的依据只有一条：<b>如果模型被不可信内容诱导着调用了它，会不会造成用户不想要的后果。</b>
/// 读时间是无论如何都无害的；执行 shell 命令则可能删文件、发网络请求。
/// </summary>
public enum ToolRisk
{
    /// <summary>无副作用或副作用可为用户接受，默认放行。</summary>
    Safe,

    /// <summary>可能修改系统状态或读取敏感数据，需要智能体显式开启「允许执行危险工具」。</summary>
    Dangerous,
}

/// <summary>工具的来源，用于界面上区分同名工具的出处。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolSource>))]
public enum ToolSource
{
    /// <summary>来自本机技能。</summary>
    Skill,

    /// <summary>来自 MCP 服务器。</summary>
    Mcp,
}
