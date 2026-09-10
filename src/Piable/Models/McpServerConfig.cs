namespace Piable.Models;

/// <summary>
/// MCP 服务器配置。
/// 首版仅定义数据结构与持久化，连接与工具发现由后续阶段的 McpClientService 实现。
/// </summary>
public sealed class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = "新 MCP 服务器";

    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    /// <summary>Stdio 模式下的可执行文件命令。</summary>
    public string? Command { get; set; }

    /// <summary>Stdio 模式下的命令行参数。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>SSE / StreamableHTTP 模式下的地址。</summary>
    public string? Url { get; set; }

    /// <summary>HTTP 请求头，通常用于放置鉴权信息。</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
