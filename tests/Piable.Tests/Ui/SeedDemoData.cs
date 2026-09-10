using System.Text.Json;
using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Storage;
using Piable.Services.Tools;

namespace Piable.Tests.Ui;

/// <summary>
/// 临时的演示数据播种器：把一段对话写进真实的应用数据目录，
/// 便于用截图人工确认 Markdown 渲染、消息气泡、统计条与工具调用的实际观感。
/// 只在本机验证时手动运行，不参与常规测试流程。
/// </summary>
public class SeedDemoData
{
    [Fact(Skip = "本地人工验证用：会把演示数据写入真实应用数据目录，需要时手动去掉 Skip")]
    public async Task Seed()
    {
        var paths = AppPaths.CreateDefault();
        var database = new PiableDatabase(paths.DatabasePath);
        await database.InitializeAsync();

        var protector = AesGcmSecretProtector.LoadOrCreate(paths.KeyFilePath);
        var providers = new ProviderRepository(database, protector);
        var agents = new AgentRepository(database);
        var sessions = new SessionRepository(database);
        var prefs = new PreferenceRepository(database);
        var mcpServers = new McpServerRepository(database);
        var skillRepo = new SkillRepository(database);

        var config = new ConfigService(providers, agents, prefs, mcpServers);
        await config.InitializeAsync();
        await new SkillService(skillRepo).InitializeAsync();

        // 让代码助手带上「执行命令」技能并授权，用以展示工具调用
        var coder = await agents.GetByIdAsync(ConfigService.CoderAgentId);
        if (coder is not null)
        {
            coder.SkillIds = [SkillService.ShellSkillId];
            coder.AllowDangerousTools = true;
            await agents.UpsertAsync(coder);
        }

        var session = new ChatSession
        {
            Title = "工具调用演示",
            AgentId = ConfigService.CoderAgentId,
            ModelUsed = "deepseek-chat",
        };
        await sessions.UpsertAsync(session);

        await AppendAsync(sessions, session.Id, MessageRole.User, "看看当前目录有哪些文件。");

        await AppendAsync(sessions, session.Id, MessageRole.Assistant,
            "好的，我来运行一下。", durationMs: 900, promptTokens: 120, completionTokens: 9);

        // 一次成功的工具调用
        await AppendToolAsync(sessions, session.Id, new ToolCallPayload
        {
            ToolName = "run_shell",
            DisplayName = "执行命令",
            SourceLabel = "技能 · 执行命令",
            Risk = ToolRisk.Dangerous,
            Status = ToolInvocationStatusPayload.Succeeded,
            ArgumentsText = "command=ls -la",
            ResultPayload = "退出码：0\n标准输出：\ntotal 24\ndrwxr-xr-x  6 user  staff  192 Sep 11 00:10 .\n"
                            + "drwxr-xr-x  3 user  staff   96 Sep 11 00:09 ..\n"
                            + "-rw-r--r--  1 user  staff  812 Sep 11 00:10 README.md\n"
                            + "drwxr-xr-x  4 user  staff  128 Sep 11 00:10 src",
            DurationMs = 340,
        });

        await AppendAsync(sessions, session.Id, MessageRole.Assistant, """
            当前目录下有这些内容：

            - `README.md`
            - `src/`（项目源码目录）

            需要我进一步查看某个文件吗？
            """, durationMs: 2100, promptTokens: 180, completionTokens: 62, cost: 0.0002m);

        // 一次被拒绝的危险调用，用于确认拒绝态的展示
        await AppendAsync(sessions, session.Id, MessageRole.User, "帮我把当前目录下的临时文件都删掉。");

        await AppendToolAsync(sessions, session.Id, new ToolCallPayload
        {
            ToolName = "run_shell",
            DisplayName = "执行命令",
            SourceLabel = "技能 · 执行命令",
            Risk = ToolRisk.Dangerous,
            Status = ToolInvocationStatusPayload.Denied,
            ArgumentsText = "command=rm -rf ./tmp",
            ResultPayload = "错误：该工具被标记为危险操作，当前智能体未获授权执行。",
            DurationMs = 0,
        });
    }

    private static Task AppendAsync(
        SessionRepository sessions, string sessionId, MessageRole role, string content,
        long? durationMs = null, int? promptTokens = null, int? completionTokens = null,
        decimal? cost = null, bool interrupted = false) =>
        sessions.AppendMessageAsync(sessionId, new ChatMessage
        {
            Role = role,
            Content = content,
            DurationMs = durationMs,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            EstimatedCost = cost,
            ModelUsed = durationMs is null ? null : "deepseek-chat",
            IsInterrupted = interrupted,
        });

    private static Task AppendToolAsync(
        SessionRepository sessions, string sessionId, ToolCallPayload payload) =>
        sessions.AppendMessageAsync(sessionId, new ChatMessage
        {
            Role = MessageRole.Tool,
            Content = JsonSerializer.Serialize(payload, PiableJsonContext.Default.ToolCallPayload),
        });
}
