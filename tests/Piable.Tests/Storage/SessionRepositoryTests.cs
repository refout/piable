using Piable.Models;

namespace Piable.Tests.Storage;

public class SessionRepositoryTests
{
    /// <summary>会话带有指向供应商与智能体的外键，测试前需要先建好被引用的行。</summary>
    private static async Task SeedReferencesAsync(TestWorkspace workspace)
    {
        await workspace.Providers.UpsertAsync(new ProviderConfig
        {
            Id = "p1",
            PresetId = ProviderPresets.OpenAi,
            DefaultModel = "gpt-4o-mini",
        });
        await workspace.Agents.UpsertAsync(new Agent { Id = "a1", Name = "通用助手" });
    }

    private static async Task<ChatSession> NewSessionAsync(TestWorkspace workspace, string id = "s1")
    {
        var session = new ChatSession
        {
            Id = id,
            Title = "新对话",
            ProviderId = "p1",
            AgentId = "a1",
            ModelUsed = "gpt-4o-mini",
        };
        await workspace.Sessions.UpsertAsync(session);
        return session;
    }

    [Fact]
    public async Task 消息按追加顺序读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        foreach (var text in new[] { "第一条", "第二条", "第三条" })
        {
            await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
            {
                Role = MessageRole.User,
                Content = text,
            });
        }

        var session = await workspace.Sessions.GetByIdAsync("s1");

        Assert.NotNull(session);
        Assert.Equal(["第一条", "第二条", "第三条"], session.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task 时间戳完全相同的消息_顺序依然稳定()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        // 流式生成时用户消息与助手消息可能落在同一毫秒；
        // 仅靠 Timestamp 排序会得到不确定的顺序，SortOrder 就是为此存在的。
        var sameInstant = DateTimeOffset.Now;
        for (var i = 0; i < 5; i++)
        {
            await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
            {
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = $"第{i}条",
                Timestamp = sameInstant,
            });
        }

        var session = await workspace.Sessions.GetByIdAsync("s1");

        Assert.Equal(
            ["第0条", "第1条", "第2条", "第3条", "第4条"],
            session!.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task 统计字段可完整往返()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        var start = DateTimeOffset.Now;
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "回答",
            StartTime = start,
            EndTime = start.AddSeconds(2.3),
            DurationMs = 2300,
            PromptTokens = 45,
            CompletionTokens = 156,
            TotalTokens = 201,
            EstimatedCost = 0.0003m,
            ModelUsed = "gpt-4o-mini",
            IsInterrupted = true,
        });

        var message = (await workspace.Sessions.GetByIdAsync("s1"))!.Messages.Single();

        Assert.Equal(MessageRole.Assistant, message.Role);
        Assert.Equal(2300, message.DurationMs);
        Assert.Equal(45, message.PromptTokens);
        Assert.Equal(156, message.CompletionTokens);
        Assert.Equal(201, message.TotalTokens);
        Assert.Equal(0.0003m, message.EstimatedCost);
        Assert.Equal("gpt-4o-mini", message.ModelUsed);
        Assert.True(message.IsInterrupted);
        // 时间以 UTC 存储，读回后应表示同一时刻
        Assert.Equal(start.ToUniversalTime(), message.StartTime!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task 思考内容可随消息持久化并读回()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        // 回归点：思考模式开启时模型会回传推理过程，这段内容必须入库，
        // 否则重新打开会话就只剩最终回答、看不到思考过程。
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "最终回答",
            ThinkingContent = "让我先拆解一下这个需求……",
            ModelUsed = "o3-mini",
        });

        var message = (await workspace.Sessions.GetByIdAsync("s1"))!.Messages.Single();

        Assert.Equal("最终回答", message.Content);
        Assert.Equal("让我先拆解一下这个需求……", message.ThinkingContent);
    }


    [Fact]
    public async Task 删除会话级联删除其消息()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.User,
            Content = "你好",
        });

        await workspace.Sessions.DeleteAsync("s1");

        Assert.Null(await workspace.Sessions.GetByIdAsync("s1"));

        // 消息经 ON DELETE CASCADE 一并清除，且连接必须开启 foreign_keys 才会生效
        await using var connection = await workspace.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Messages WHERE SessionId = 's1';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task 删除供应商后会话保留_仅失去供应商指向()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.User,
            Content = "历史消息",
        });

        await workspace.Providers.DeleteAsync("p1");

        var session = await workspace.Sessions.GetByIdAsync("s1");
        Assert.NotNull(session);
        Assert.Null(session.ProviderId);
        // 历史内容必须原样保留
        Assert.Equal("历史消息", session.Messages.Single().Content);
        Assert.Equal("gpt-4o-mini", session.ModelUsed);
    }

    [Fact]
    public async Task 删除智能体后会话保留_仅失去智能体指向()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        await workspace.Agents.DeleteAsync("a1");

        var session = await workspace.Sessions.GetByIdAsync("s1");
        Assert.NotNull(session);
        Assert.Null(session.AgentId);
    }

    [Fact]
    public async Task 摘要聚合消息数与总token()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage { Role = MessageRole.User });
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.Assistant,
            TotalTokens = 201,
        });
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage
        {
            Role = MessageRole.Assistant,
            TotalTokens = 99,
        });

        var summary = (await workspace.Sessions.GetSummariesAsync()).Single();

        Assert.Equal(3, summary.MessageCount);
        Assert.Equal(300, summary.TotalTokens);
    }

    [Fact]
    public async Task 无消息的会话摘要为空统计而不是无记录()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        var summary = (await workspace.Sessions.GetSummariesAsync()).Single();

        Assert.Equal(0, summary.MessageCount);
        Assert.Equal(0, summary.TotalTokens);
    }

    [Fact]
    public async Task 摘要按最近更新倒序且受数量限制()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);

        for (var i = 0; i < 5; i++)
        {
            await workspace.Sessions.UpsertAsync(new ChatSession
            {
                Id = $"s{i}",
                Title = $"对话{i}",
                ProviderId = "p1",
                AgentId = "a1",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(i),
            });
        }

        var all = await workspace.Sessions.GetSummariesAsync(limit: 100);
        Assert.Equal(5, all.Count);
        Assert.Equal("s4", all[0].Id);

        var limited = await workspace.Sessions.GetSummariesAsync(limit: 2);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task 追加消息会刷新会话的更新时间()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);

        var session = await NewSessionAsync(workspace);
        session.UpdatedAt = DateTimeOffset.Now.AddHours(-1);
        await workspace.Sessions.UpsertAsync(session);

        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage { Role = MessageRole.User });

        var reloaded = await workspace.Sessions.GetMetadataAsync("s1");
        Assert.True(reloaded!.UpdatedAt > session.UpdatedAt);
    }

    [Fact]
    public async Task 首次追加消息的排序序号从零开始()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        await NewSessionAsync(workspace);

        // 会话此前没有任何消息，COALESCE(MAX(SortOrder), -1) + 1 必须得到 0 而非空集
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage { Content = "首条" });

        var session = await workspace.Sessions.GetByIdAsync("s1");
        Assert.Single(session!.Messages);
        Assert.Equal("首条", session.Messages[0].Content);
    }

    // ---------------- 搜索 ----------------

    /// <summary>建两个会话，各自带若干条消息，用于验证搜索的命中与计数。</summary>
    private static async Task SeedForSearchAsync(TestWorkspace workspace)
    {
        await SeedReferencesAsync(workspace);

        await NewSessionAsync(workspace, "s1");
        await workspace.Sessions.UpsertAsync(
            new ChatSession { Id = "s1", Title = "讨论 Rust 所有权", ProviderId = "p1", AgentId = "a1" });
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage { Content = "所有权规则有三条" });
        await workspace.Sessions.AppendMessageAsync("s1", new ChatMessage { Content = "借用与生命周期" });

        await NewSessionAsync(workspace, "s2");
        await workspace.Sessions.UpsertAsync(
            new ChatSession { Id = "s2", Title = "周末去哪玩", ProviderId = "p1", AgentId = "a1" });
        await workspace.Sessions.AppendMessageAsync("s2", new ChatMessage { Content = "聊聊 Rust 的学习曲线" });
    }

    [Fact]
    public async Task 按标题搜索命中会话()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedForSearchAsync(workspace);

        var results = await workspace.Sessions.SearchAsync("周末");

        Assert.Equal(["s2"], results.Select(r => r.Id));
    }

    [Fact]
    public async Task 按消息正文搜索命中会话并给出命中条数()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedForSearchAsync(workspace);

        var results = await workspace.Sessions.SearchAsync("Rust");

        Assert.Equal(2, results.Count);

        // s1 只因标题含 "Rust" 命中，正文里没有，命中数为 0——
        // 这正是界面要区分"标题像"与"确实聊过"的依据
        Assert.Equal(0, results.Single(r => r.Id == "s1").MatchCount);
        Assert.Equal(2, results.Single(r => r.Id == "s1").MessageCount);
        Assert.Equal(1, results.Single(r => r.Id == "s2").MatchCount);
    }

    [Fact]
    public async Task 搜索区分不出结果时返回空列表()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedForSearchAsync(workspace);

        Assert.Empty(await workspace.Sessions.SearchAsync("肯定搜不到的词"));
    }

    [Fact]
    public async Task 搜索关键词里的通配符被转义()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedForSearchAsync(workspace);
        await workspace.Sessions.AppendMessageAsync(
            "s1", new ChatMessage { Content = "进度 100% 完成" });

        // '%' 若不被转义会退化成"任意后缀"，把只含 "100" 之外的会话也捞进来
        var withWildcard = await workspace.Sessions.SearchAsync("100%");
        Assert.Equal(["s1"], withWildcard.Select(r => r.Id));

        // '_' 同理：单个字符通配符不应匹配任意字符
        var underscore = await workspace.Sessions.SearchAsync("s_");
        Assert.Empty(underscore);
    }

    [Fact]
    public async Task 更新会话标题与智能体()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await SeedReferencesAsync(workspace);
        var session = await NewSessionAsync(workspace);

        session.Title = "关于AI的讨论";
        session.AgentSnapshot = "{\"name\":\"通用助手\"}";
        await workspace.Sessions.UpsertAsync(session);

        var reloaded = await workspace.Sessions.GetMetadataAsync("s1");

        Assert.Equal("关于AI的讨论", reloaded!.Title);
        Assert.Equal("{\"name\":\"通用助手\"}", reloaded.AgentSnapshot);
    }
}
