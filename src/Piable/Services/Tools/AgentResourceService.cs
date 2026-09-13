using System.Text;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>一次资源解析的结果。</summary>
/// <param name="Text">拼好的上下文文本；没有挂载资源（或都读失败）时为空。</param>
/// <param name="Warnings">需要提示给用户的问题（例如某个资源读不到）。</param>
public sealed record ResourceResolution(string Text, IReadOnlyList<string> Warnings)
{
    public static ResourceResolution Empty { get; } = new(string.Empty, []);
}

/// <summary>把智能体挂载的 MCP 资源读成一段可注入的上下文。</summary>
public interface IAgentResourceService
{
    Task<ResourceResolution> ResolveAsync(Agent agent, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AgentResourceService : IAgentResourceService
{
    /// <summary>
    /// 单个资源的字符上限。
    /// 一个几 MB 的资源全文塞进提示词会把上下文直接撑爆——
    /// 截断是必要的，且截断处要明确标出来，免得模型把残缺内容当成完整内容。
    /// </summary>
    private const int MaxCharsPerResource = 8000;

    private readonly IMcpClientService _mcp;
    private readonly IConfigService _config;

    public AgentResourceService(IMcpClientService mcp, IConfigService config)
    {
        _mcp = mcp;
        _config = config;
    }

    public async Task<ResourceResolution> ResolveAsync(Agent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.McpResourceUris.Count == 0)
        {
            return ResourceResolution.Empty;
        }

        var servers = await _config.GetMcpServersAsync(ct).ConfigureAwait(false);
        var candidates = servers.Where(s => s.Enabled && agent.McpServerIds.Contains(s.Id)).ToList();

        var warnings = new List<string>();
        var builder = new StringBuilder();

        foreach (var uri in agent.McpResourceUris)
        {
            ct.ThrowIfCancellationRequested();

            if (candidates.Count == 0)
            {
                warnings.Add(Loc.Get("Resource.NoSource", uri));
                continue;
            }

            // 挂载时只记了 URI，没记来自哪台服务器，因此逐个问一遍关联的服务器。
            // 只有读不到的才会多问几家：命中即停，正常情况一个请求就够。
            McpResourceContent? content = null;
            string? lastError = null;

            foreach (var server in candidates)
            {
                try
                {
                    content = await _mcp.ReadResourceAsync(server, uri, ct).ConfigureAwait(false);
                    break;
                }
                catch (McpConnectionException ex)
                {
                    lastError = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            if (content is null)
            {
                warnings.Add(Loc.Get("Resource.ReadFailed", uri, lastError ?? Loc.Get("Common.UnknownReason")));
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append("## ").AppendLine(uri);
            builder.AppendLine();
            builder.AppendLine(Truncate(content.Text));
        }

        if (builder.Length == 0)
        {
            return new ResourceResolution(string.Empty, warnings);
        }

        var header = new StringBuilder()
            .AppendLine(Loc.Get("Resource.SectionTitle"))
            .AppendLine()
            .AppendLine(Loc.Get("Resource.SectionHint"))
            .AppendLine();

        return new ResourceResolution(header.Append(builder).ToString().TrimEnd(), warnings);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxCharsPerResource
            ? text
            : text[..MaxCharsPerResource] + Loc.Get("Resource.Truncated", text.Length);
}
