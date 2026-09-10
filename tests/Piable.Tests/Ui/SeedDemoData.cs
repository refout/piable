using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.Services.Storage;

namespace Piable.Tests.Ui;

/// <summary>
/// 临时的演示数据播种器：把一段对话写进真实的应用数据目录，
/// 便于用截图人工确认 Markdown 渲染、消息气泡与统计条的实际观感。
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

        await new ConfigService(providers, agents, prefs).InitializeAsync();

        var session = new ChatSession
        {
            Title = "关于快速排序的讨论",
            ProviderId = null,
            AgentId = ConfigService.CoderAgentId,
            ModelUsed = "deepseek-chat",
        };
        await sessions.UpsertAsync(session);

        await sessions.AppendMessageAsync(session.Id, new ChatMessage
        {
            Role = MessageRole.User,
            Content = "用中文解释一下快速排序的核心思想，并给出一段 Python 实现。",
        });

        await sessions.AppendMessageAsync(session.Id, new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = """
                快速排序的核心是**分治**：选一个基准值，把数组分成"比它小"和"比它大"两部分，
                再对两部分递归处理。

                ## 关键点

                1. **基准选择**影响性能，随机化可以避免最坏情况
                2. **原地分区**让空间复杂度降到 `O(log n)`
                3. 平均时间复杂度 `O(n log n)`，最坏 `O(n²)`

                ## Python 实现

                ```python
                def quicksort(arr: list[int]) -> list[int]:
                    if len(arr) <= 1:
                        return arr
                    pivot = arr[len(arr) // 2]
                    left = [x for x in arr if x < pivot]
                    mid = [x for x in arr if x == pivot]
                    right = [x for x in arr if x > pivot]
                    return quicksort(left) + mid + quicksort(right)
                ```

                > 注意：这段实现为了可读性用了额外空间，不是原地版本。
                """,
            DurationMs = 8340,
            PromptTokens = 42,
            CompletionTokens = 386,
            TotalTokens = 428,
            EstimatedCost = 0.0004m,
            ModelUsed = "deepseek-chat",
        });

        await sessions.AppendMessageAsync(session.Id, new ChatMessage
        {
            Role = MessageRole.User,
            Content = "原地版本怎么写？",
        });

        await sessions.AppendMessageAsync(session.Id, new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "原地版本用双指针交换，不需要额外数组……",
            DurationMs = 12300,
            PromptTokens = 96,
            CompletionTokens = 251,
            TotalTokens = 347,
            EstimatedCost = 0.0003m,
            ModelUsed = "deepseek-chat",
            IsInterrupted = true,
        });
    }
}
