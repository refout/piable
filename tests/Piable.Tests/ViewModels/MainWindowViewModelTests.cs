using Piable.Models;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

/// <summary>
/// 侧边栏会话搜索。
/// 只验证列表过滤的行为，不牵扯界面布局——布局另有 MainWindowLayoutTests 覆盖。
/// </summary>
public class MainWindowViewModelTests
{
    private sealed record Harness(TestWorkspace Workspace, TestServices Services, MainWindowViewModel ViewModel);

    private static async Task<Harness> CreateAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var services = TestServices.Create(workspace);
        await services.InitializeSeedDataAsync();

        // 这里不能先 InitializeAsync：列表为空时它会自动开一个空会话，
        // 之后再播种就多出一条，断言条数全都要跟着偏。
        var vm = services.CreateMainWindowViewModel();
        return new Harness(workspace, services, vm);
    }

    private static async Task SeedSessionAsync(
        TestWorkspace workspace, string id, string title, params string[] messages)
    {
        await workspace.Sessions.UpsertAsync(new ChatSession { Id = id, Title = title });

        foreach (var text in messages)
        {
            await workspace.Sessions.AppendMessageAsync(
                id, new ChatMessage { Role = MessageRole.User, Content = text });
        }
    }

    /// <summary>轮询等待条件成立。搜索有 300ms 防抖，没有同步的完成信号。</summary>
    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }

        Assert.True(condition(), "等待条件超时");
    }

    [Fact]
    public async Task 按标题搜索只留下命中的会话()
    {
        var h = await CreateAsync();
        await using var _ = h.Workspace;

        await SeedSessionAsync(h.Workspace, "s1", "Rust 所有权", "借用检查");
        await SeedSessionAsync(h.Workspace, "s2", "周末去哪玩", "爬山");

        // 重新初始化以载入刚写入的会话
        await h.ViewModel.InitializeAsync();

        h.ViewModel.SearchText = "Rust";
        await WaitAsync(() => h.ViewModel.Sessions.Count == 1);

        Assert.True(h.ViewModel.IsSearching);
        Assert.Equal("s1", h.ViewModel.Sessions.Single().Id);
    }

    [Fact]
    public async Task 按正文搜索命中并给出命中条数()
    {
        var h = await CreateAsync();
        await using var _ = h.Workspace;

        await SeedSessionAsync(h.Workspace, "s1", "闲聊", "今天天气不错", "聊聊 Rust 的所有权");
        await SeedSessionAsync(h.Workspace, "s2", "购物清单", "买牛奶和面包");

        await h.ViewModel.InitializeAsync();

        h.ViewModel.SearchText = "Rust";
        await WaitAsync(() => h.ViewModel.Sessions.Count == 1);

        var item = h.ViewModel.Sessions.Single();
        Assert.Equal(1, item.MatchCount);
        Assert.Contains("命中 1 条", item.SummaryText);
    }

    [Fact]
    public async Task 清空搜索恢复完整列表()
    {
        var h = await CreateAsync();
        await using var _ = h.Workspace;

        await SeedSessionAsync(h.Workspace, "s1", "Rust 所有权", "借用检查");
        await SeedSessionAsync(h.Workspace, "s2", "周末去哪玩", "爬山");

        await h.ViewModel.InitializeAsync();
        Assert.Equal(2, h.ViewModel.Sessions.Count);

        h.ViewModel.SearchText = "Rust";
        await WaitAsync(() => h.ViewModel.Sessions.Count == 1);

        h.ViewModel.ClearSearchCommand.Execute(null);

        Assert.False(h.ViewModel.IsSearching);
        Assert.Equal(2, h.ViewModel.Sessions.Count);
        Assert.Equal(string.Empty, h.ViewModel.SearchText);
    }

    [Fact]
    public async Task 搜索把当前会话过滤掉时右侧不被关掉()
    {
        var h = await CreateAsync();
        await using var _ = h.Workspace;

        await SeedSessionAsync(h.Workspace, "s1", "Rust 所有权", "借用检查");
        await SeedSessionAsync(h.Workspace, "s2", "周末去哪玩", "爬山");

        await h.ViewModel.InitializeAsync();

        var weekend = h.ViewModel.Sessions.Single(s => s.Id == "s2");
        h.ViewModel.SelectedSession = weekend;
        await WaitAsync(() => h.ViewModel.CurrentSession?.Session.Id == "s2");

        h.ViewModel.SearchText = "Rust";
        await WaitAsync(() => h.ViewModel.Sessions.Count == 1);

        // 列表里没有它了，但右侧正在看的内容不能跟着消失
        Assert.Null(h.ViewModel.SelectedSession);
        Assert.Equal("s2", h.ViewModel.CurrentSession?.Session.Id);
    }

    [Fact]
    public async Task 搜索态下新建会话会先清掉搜索词()
    {
        var h = await CreateAsync();
        await using var _ = h.Workspace;

        await SeedSessionAsync(h.Workspace, "s1", "Rust 所有权", "借用检查");
        await h.ViewModel.InitializeAsync();

        h.ViewModel.SearchText = "Rust";
        // 这里只有一条会话，等 Count==1 会在防抖结束前就成立，必须等搜索真的生效
        await WaitAsync(() => h.ViewModel.IsSearching);

        await h.ViewModel.NewSessionCommand.ExecuteAsync(null);

        // 否则新建的会话不在过滤结果里，一建好就"消失"了
        Assert.Equal(string.Empty, h.ViewModel.SearchText);
        Assert.False(h.ViewModel.IsSearching);
        Assert.Contains(h.ViewModel.Sessions, s => s.Id == h.ViewModel.SelectedSession?.Id);
    }
}
