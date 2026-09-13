namespace Piable.Services.Tools;

/// <summary>
/// MCP 服务器上的一个资源或资源模板。
///
/// 这两个概念在协议里是分开的（<c>resources/list</c> 与 <c>resources/templates/list</c>），
/// 这里合并表示，用 <see cref="IsTemplate"/> 区分——对使用者来说差别只在于
/// 能不能直接读，模板要先按 URI 模板填出具体 URI。
/// </summary>
public sealed record McpResourceDescriptor
{
    public required string ServerId { get; init; }

    public required string ServerName { get; init; }

    /// <summary>资源 URI；模板则为 URI 模板。</summary>
    public required string Uri { get; init; }

    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }

    public bool IsTemplate { get; init; }

    /// <summary>展示名：优先标题，其次名称，最后退回 URI。</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? (string.IsNullOrWhiteSpace(Name) ? Uri : Name) : Title;
}

/// <summary>一次资源读取的结果。</summary>
public sealed record McpResourceContent
{
    public required string Uri { get; init; }

    public string? MimeType { get; init; }

    /// <summary>
    /// 文本内容。二进制资源会给出一段说明而不是 base64——
    /// 把几 MB 的 base64 摊在界面上既看不了也没意义。
    /// </summary>
    public required string Text { get; init; }

    public bool IsBinary { get; init; }
}

/// <summary>MCP 服务器上的一个提示模板。</summary>
public sealed record McpPromptDescriptor
{
    public required string ServerId { get; init; }

    public required string ServerName { get; init; }

    /// <summary>提示名，获取结果时用它。</summary>
    public required string Name { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }

    public IReadOnlyList<McpPromptArgumentDescriptor> Arguments { get; init; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? Name : Title;
}

/// <summary>提示模板的一个参数。</summary>
public sealed record McpPromptArgumentDescriptor
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}

/// <summary>提示模板展开后的一条消息。</summary>
public sealed record McpPromptMessage
{
    /// <summary>"user"、"assistant" 等。</summary>
    public required string Role { get; init; }

    public required string Text { get; init; }
}
