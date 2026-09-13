using Piable.Models;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

public class InlineConfirmTests
{
    private static SessionListItemViewModel CreateItem() => new(
        new ChatSessionSummary { Id = "s1", Title = "对话1" },
        agentName: "通用助手");

    [Fact]
    public void 首次点击进入确认态而不删除()
    {
        var item = CreateItem();
        var deleted = 0;
        item.DeleteConfirmed += (_, _) => deleted++;

        item.RequestDeleteCommand.Execute(null);

        Assert.True(item.IsConfirmingDelete);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void 确认态下再次点击才真正删除()
    {
        var item = CreateItem();
        var deleted = 0;
        item.DeleteConfirmed += (_, _) => deleted++;

        item.RequestDeleteCommand.Execute(null);
        item.RequestDeleteCommand.Execute(null);

        Assert.Equal(1, deleted);
        Assert.False(item.IsConfirmingDelete);
    }

    [Fact]
    public void 取消后回到初始态且不会删除()
    {
        var item = CreateItem();
        var deleted = 0;
        item.DeleteConfirmed += (_, _) => deleted++;

        item.RequestDeleteCommand.Execute(null);
        item.CancelConfirm();

        Assert.False(item.IsConfirmingDelete);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void 取消后重新点击会再次进入确认态()
    {
        var item = CreateItem();
        var deleted = 0;
        item.DeleteConfirmed += (_, _) => deleted++;

        item.RequestDeleteCommand.Execute(null);
        item.CancelConfirm();
        item.RequestDeleteCommand.Execute(null);

        Assert.True(item.IsConfirmingDelete);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void 取消后紧接着的点击不会误判为确认()
    {
        // 关键回归点：CancelConfirm 会取消计时器，若内部状态没清干净，
        // 下一次点击就会被当成"第二次点击"而直接删掉会话。
        var item = CreateItem();
        var deleted = 0;
        item.DeleteConfirmed += (_, _) => deleted++;

        item.RequestDeleteCommand.Execute(null);
        item.CancelConfirm();
        item.CancelConfirm();
        item.RequestDeleteCommand.Execute(null);

        Assert.Equal(0, deleted);
        Assert.True(item.IsConfirmingDelete);
    }

    [Fact]
    public void 初始不处于确认态()
    {
        Assert.False(CreateItem().IsConfirmingDelete);
    }

    [Fact]
    public void 会话项展示条数与token缩写()
    {
        var item = new SessionListItemViewModel(
            new ChatSessionSummary
            {
                Id = "s1",
                Title = "关于AI的讨论",
                MessageCount = 12,
                TotalTokens = 2300,
            },
            "通用助手");

        Assert.Equal("12条 · 2.3K tokens · 通用助手", item.SummaryText);
    }

    [Fact]
    public void 内置智能体不可删除()
    {
        var builtIn = new AgentListItemViewModel(new Agent { Id = "a1", Name = "通用助手", IsBuiltIn = true });
        var custom = new AgentListItemViewModel(new Agent { Id = "a2", Name = "我的助手" });

        Assert.False(builtIn.CanDelete);
        Assert.True(custom.CanDelete);
    }

    [Fact]
    public void 默认智能体在界面上用星标图标标记()
    {
        var agent = new AgentListItemViewModel(new Agent { Id = "a1", Name = "通用助手", IsDefault = true });

        Assert.True(agent.IsDefault);
        Assert.Equal("通用助手", agent.DisplayName);
    }
}
