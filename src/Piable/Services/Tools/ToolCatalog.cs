using Piable.Helpers;
using Piable.Models;

namespace Piable.Services.Tools;

/// <summary>一次工具解析的结果。</summary>
/// <param name="Tools">可交给模型的工具。</param>
/// <param name="Warnings">需要提示给用户的问题（例如某台 MCP 服务器连不上）。</param>
public sealed record ToolResolution(
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<string> Warnings)
{
    public static ToolResolution Empty { get; } = new([], []);
}

/// <summary>按智能体的配置，汇总它可用的全部工具。</summary>
public interface IToolCatalog
{
    Task<ToolResolution> ResolveAsync(Agent agent, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ToolCatalog : IToolCatalog
{
    private readonly ISkillService _skills;
    private readonly IMcpClientService _mcp;
    private readonly IConfigService _config;

    public ToolCatalog(ISkillService skills, IMcpClientService mcp, IConfigService config)
    {
        _skills = skills;
        _mcp = mcp;
        _config = config;
    }

    public async Task<ToolResolution> ResolveAsync(Agent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (!agent.HasTools)
        {
            // 未配置任何工具：对话退化为单轮流式生成
            return ToolResolution.Empty;
        }

        var tools = new List<ToolDescriptor>();
        var warnings = new List<string>();

        await AddSkillToolsAsync(agent, tools, ct).ConfigureAwait(false);
        await AddMcpToolsAsync(agent, tools, warnings, ct).ConfigureAwait(false);

        return new ToolResolution(Deduplicate(tools, warnings), warnings);
    }

    private async Task AddSkillToolsAsync(
        Agent agent, List<ToolDescriptor> tools, CancellationToken ct)
    {
        if (agent.SkillIds.Count == 0)
        {
            return;
        }

        var all = await _skills.GetAllAsync(ct).ConfigureAwait(false);
        var wanted = new HashSet<string>(agent.SkillIds, StringComparer.Ordinal);

        tools.AddRange(_skills.ResolveTools(all.Where(s => wanted.Contains(s.Id))));
    }

    /// <summary>
    /// 逐台连接 MCP 服务器。
    /// 单台连不上不影响其余工具：记一条警告后继续，而不是让整个对话失败
    /// （设计文档 8.2 的"连接失败时自动禁用该服务器，并在状态栏提示"）。
    /// </summary>
    private async Task AddMcpToolsAsync(
        Agent agent, List<ToolDescriptor> tools, List<string> warnings, CancellationToken ct)
    {
        if (agent.McpServerIds.Count == 0)
        {
            return;
        }

        var servers = await _config.GetMcpServersAsync(ct).ConfigureAwait(false);
        var byId = servers.ToDictionary(s => s.Id, StringComparer.Ordinal);

        foreach (var serverId in agent.McpServerIds)
        {
            if (!byId.TryGetValue(serverId, out var server))
            {
                warnings.Add(Loc.Get("Tool.CatalogMissingServer"));
                continue;
            }

            if (!server.Enabled)
            {
                continue;
            }

            try
            {
                tools.AddRange(await _mcp.GetToolsAsync(server, ct).ConfigureAwait(false));
            }
            catch (McpConnectionException ex)
            {
                warnings.Add(ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(Loc.Get("Tool.CatalogDiscoveryFailed", server.Name, ex.Message));
            }
        }
    }

    /// <summary>
    /// 工具名去重。同名工具只保留先注册的那个——模型是按名字调用的，
    /// 留两个同名工具会让它的调用落在不确定的目标上。
    /// </summary>
    private static List<ToolDescriptor> Deduplicate(List<ToolDescriptor> tools, List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ToolDescriptor>(tools.Count);

        foreach (var tool in tools)
        {
            if (seen.Add(tool.Name))
            {
                result.Add(tool);
                continue;
            }

            warnings.Add(Loc.Get("Tool.CatalogDuplicate", tool.Name, tool.SourceLabel));
        }

        return result;
    }
}
