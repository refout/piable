using Piable.Models;
using Piable.Services.Storage;

namespace Piable.Services;

/// <summary>供应商、智能体与用户偏好的配置管理。</summary>
public interface IConfigService
{
    /// <summary>建库并写入内置数据。启动时调用一次。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ProviderConfig>> GetProvidersAsync(CancellationToken ct = default);
    Task<ProviderConfig?> GetProviderAsync(string id, CancellationToken ct = default);

    /// <summary>取默认供应商；没有显式默认时退回列表首项。</summary>
    Task<ProviderConfig?> GetDefaultProviderAsync(CancellationToken ct = default);

    /// <summary>取指定预设对应的配置，不存在则按预设默认值新建一条。</summary>
    Task<ProviderConfig> GetOrCreateProviderAsync(string presetId, CancellationToken ct = default);

    Task SaveProviderAsync(ProviderConfig provider, CancellationToken ct = default);
    Task DeleteProviderAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken ct = default);
    Task<Agent?> GetAgentAsync(string id, CancellationToken ct = default);

    /// <summary>取默认智能体；没有显式默认时退回第一个内置智能体。</summary>
    Task<Agent?> GetDefaultAgentAsync(CancellationToken ct = default);

    Task SaveAgentAsync(Agent agent, CancellationToken ct = default);
    Task DeleteAgentAsync(string id, CancellationToken ct = default);

    Task<UserPreferences> GetPreferencesAsync(CancellationToken ct = default);
    Task SavePreferencesAsync(UserPreferences preferences, CancellationToken ct = default);

    // ---- MCP 服务器（设计文档 2.2 将 MCP 的 CRUD 归在配置服务下）----

    Task<IReadOnlyList<McpServerConfig>> GetMcpServersAsync(CancellationToken ct = default);
    Task SaveMcpServerAsync(McpServerConfig server, CancellationToken ct = default);
    Task DeleteMcpServerAsync(string id, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ConfigService : IConfigService
{
    /// <summary>内置智能体的固定 ID，便于升级时识别并保留用户对它们的修改。</summary>
    public const string GeneralAgentId = "builtin-general";
    public const string CoderAgentId = "builtin-coder";

    private readonly ProviderRepository _providers;
    private readonly AgentRepository _agents;
    private readonly PreferenceRepository _preferences;
    private readonly McpServerRepository _mcpServers;

    public ConfigService(
        ProviderRepository providers,
        AgentRepository agents,
        PreferenceRepository preferences,
        McpServerRepository mcpServers)
    {
        _providers = providers;
        _agents = agents;
        _preferences = preferences;
        _mcpServers = mcpServers;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SeedBuiltInAgentsAsync(ct).ConfigureAwait(false);
    }

    // ---------------- 供应商 ----------------

    public async Task<IReadOnlyList<ProviderConfig>> GetProvidersAsync(CancellationToken ct = default) =>
        await _providers.GetAllAsync(ct).ConfigureAwait(false);

    public Task<ProviderConfig?> GetProviderAsync(string id, CancellationToken ct = default) =>
        _providers.GetByIdAsync(id, ct);

    public async Task<ProviderConfig?> GetDefaultProviderAsync(CancellationToken ct = default)
    {
        var all = await _providers.GetAllAsync(ct).ConfigureAwait(false);
        // GetAllAsync 已按 IsDefault 降序返回，首项即默认项
        return all.Count > 0 ? all[0] : null;
    }

    public async Task<ProviderConfig> GetOrCreateProviderAsync(
        string presetId, CancellationToken ct = default)
    {
        var preset = ProviderPresets.Get(presetId);

        var all = await _providers.GetAllAsync(ct).ConfigureAwait(false);
        var existing = all.FirstOrDefault(p =>
            string.Equals(p.PresetId, preset.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        var created = new ProviderConfig
        {
            PresetId = preset.Id,
            Endpoint = preset.DefaultEndpoint,
            Models = [.. preset.DefaultModels],
            DefaultModel = preset.DefaultModels.Count > 0 ? preset.DefaultModels[0] : string.Empty,
            InputPricePer1K = preset.InputPricePer1K,
            OutputPricePer1K = preset.OutputPricePer1K,
            // 第一个创建的供应商自动成为默认
            IsDefault = all.Count == 0,
        };

        await _providers.UpsertAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    public Task SaveProviderAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        provider.UpdatedAt = DateTimeOffset.Now;
        return _providers.UpsertAsync(provider, ct);
    }

    public Task DeleteProviderAsync(string id, CancellationToken ct = default) =>
        _providers.DeleteAsync(id, ct);

    // ---------------- 智能体 ----------------

    public async Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken ct = default) =>
        await _agents.GetAllAsync(ct).ConfigureAwait(false);

    public Task<Agent?> GetAgentAsync(string id, CancellationToken ct = default) =>
        _agents.GetByIdAsync(id, ct);

    public async Task<Agent?> GetDefaultAgentAsync(CancellationToken ct = default)
    {
        var all = await _agents.GetAllAsync(ct).ConfigureAwait(false);

        return all.FirstOrDefault(a => a.IsDefault)
               ?? all.FirstOrDefault(a => a.IsBuiltIn)
               ?? all.FirstOrDefault();
    }

    public Task SaveAgentAsync(Agent agent, CancellationToken ct = default)
    {
        agent.UpdatedAt = DateTimeOffset.Now;
        return _agents.UpsertAsync(agent, ct);
    }

    public Task DeleteAgentAsync(string id, CancellationToken ct = default) =>
        _agents.DeleteAsync(id, ct);

    // ---------------- 偏好 ----------------

    public Task<UserPreferences> GetPreferencesAsync(CancellationToken ct = default) =>
        _preferences.LoadAsync(ct);

    public Task SavePreferencesAsync(UserPreferences preferences, CancellationToken ct = default) =>
        _preferences.SaveAsync(preferences, ct);

    // ---------------- MCP 服务器 ----------------

    public async Task<IReadOnlyList<McpServerConfig>> GetMcpServersAsync(CancellationToken ct = default) =>
        await _mcpServers.GetAllAsync(ct).ConfigureAwait(false);

    public Task SaveMcpServerAsync(McpServerConfig server, CancellationToken ct = default)
    {
        server.UpdatedAt = DateTimeOffset.Now;
        return _mcpServers.UpsertAsync(server, ct);
    }

    public Task DeleteMcpServerAsync(string id, CancellationToken ct = default) =>
        _mcpServers.DeleteAsync(id, ct);

    // ---------------- 内置数据 ----------------

    /// <summary>
    /// 首次运行时写入内置智能体（设计文档 8.5）。
    /// 已存在的内置智能体不做覆盖，用户对它们的修改会保留。
    /// </summary>
    private async Task SeedBuiltInAgentsAsync(CancellationToken ct)
    {
        var existing = await _agents.GetAllAsync(ct).ConfigureAwait(false);
        var existingIds = existing.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        var isFirstAgent = existing.Count == 0;

        if (!existingIds.Contains(GeneralAgentId))
        {
            await _agents.UpsertAsync(new Agent
            {
                Id = GeneralAgentId,
                Name = "通用助手",
                Description = "通用对话助手，适用于日常问答与写作",
                SystemPrompt =
                    "你是一个专业、可靠的 AI 助手。请用简洁清晰的中文回答，"
                    + "在不确定时如实说明，不要编造事实。涉及代码或步骤时使用 Markdown 排版。",
                IsBuiltIn = true,
                IsDefault = isFirstAgent,
            }, ct).ConfigureAwait(false);
        }

        if (!existingIds.Contains(CoderAgentId))
        {
            await _agents.UpsertAsync(new Agent
            {
                Id = CoderAgentId,
                Name = "代码助手",
                Description = "专注于编程问题的助手，回答更偏向可直接使用的代码",
                SystemPrompt =
                    "你是一位经验丰富的软件工程师。回答编程问题时，"
                    + "优先给出可运行的代码，并简要说明关键设计取舍与潜在陷阱。"
                    + "代码块请标注语言。如果用户的前提有误，直接指出。",
                Temperature = 0.2,
                IsBuiltIn = true,
            }, ct).ConfigureAwait(false);
        }
    }
}
