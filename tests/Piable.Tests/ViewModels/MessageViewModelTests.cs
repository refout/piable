using Piable.Helpers;
using Piable.Models;
using Piable.Services;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

public class MessageViewModelTests
{
    private static MessageViewModel Create(ChatMessage message, UserPreferences? prefs = null) =>
        new(message, new TokenCostCalculator(), prefs ?? new UserPreferences());

    private static ChatMessage AssistantMessage() => new()
    {
        Role = MessageRole.Assistant,
        Content = "回答",
        DurationMs = 2340,
        PromptTokens = 45,
        CompletionTokens = 156,
        TotalTokens = 201,
        EstimatedCost = 0.0003m,
    };

    [Fact]
    public void 用户消息不显示统计()
    {
        var vm = Create(new ChatMessage { Role = MessageRole.User, Content = "你好" });

        Assert.False(vm.HasStatistics);
        Assert.True(vm.IsUser);
        Assert.False(vm.IsAssistant);
    }

    [Fact]
    public void 助手消息默认显示耗时与总token()
    {
        var vm = Create(AssistantMessage());

        Assert.True(vm.ShouldShowStatistics);
        Assert.Equal("2.3s   │   201 tokens", vm.StatisticsText);
    }

    [Fact]
    public void 展开后显示输入输出与费用明细()
    {
        var vm = Create(AssistantMessage());

        vm.IsStatisticsExpanded = true;

        Assert.Equal("2.3s   │   45   │   156   │   $0.00030", vm.StatisticsText);
    }

    [Fact]
    public void 偏好要求默认展开时不点也显示明细()
    {
        var vm = Create(AssistantMessage(), new UserPreferences { ShowDetailedTokens = true });

        Assert.True(vm.ShowDetailedStatistics);
        Assert.Contains("", vm.StatisticsText);
    }

    [Fact]
    public void 关闭显示统计后整条隐藏()
    {
        var vm = Create(AssistantMessage(), new UserPreferences { ShowStatistics = false });

        Assert.False(vm.ShouldShowStatistics);
    }

    [Fact]
    public void 关闭显示费用时明细里没有金额()
    {
        var vm = Create(AssistantMessage(), new UserPreferences { ShowCost = false });
        vm.IsStatisticsExpanded = true;

        Assert.False(vm.HasCost);
        Assert.DoesNotContain("$", vm.StatisticsText);
        Assert.Contains("156", vm.StatisticsText);
    }

    [Fact]
    public void 人民币偏好下费用符号变化()
    {
        var vm = Create(AssistantMessage(), new UserPreferences { Currency = "CNY" });
        vm.IsStatisticsExpanded = true;

        Assert.Contains("¥", vm.StatisticsText);
    }

    [Fact]
    public void 流式追加文本可累加()
    {
        var vm = Create(new ChatMessage { Role = MessageRole.Assistant, Content = "" });

        vm.AppendText("你好");
        vm.AppendText("，世界");

        Assert.Equal("你好，世界", vm.Content);
        Assert.Equal("你好，世界", vm.Model.Content);
    }

    [Fact]
    public void 应用统计后刷新显示值()
    {
        var vm = Create(new ChatMessage { Role = MessageRole.Assistant, Content = "回答" });
        Assert.False(vm.HasStatistics);

        vm.ApplyStatistics(
            TimeSpan.FromMilliseconds(1500),
            new ChatUsage(10, 20, 30),
            cost: 0.001m,
            modelUsed: "gpt-4o-mini",
            interrupted: false);

        Assert.True(vm.HasStatistics);
        Assert.Equal(30, vm.TotalTokens);
        Assert.Equal("gpt-4o-mini", vm.Model.ModelUsed);
        Assert.Equal("1.5s   │   30 tokens", vm.StatisticsText);
    }

    [Fact]
    public void 供应商未汇报usage时按内容估算()
    {
        var vm = Create(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = new string('字', 100),
        });

        vm.ApplyStatistics(TimeSpan.FromSeconds(1), usage: null, cost: null, "m", interrupted: false);

        Assert.Equal(100, vm.TotalTokens);
        Assert.Equal(100, vm.CompletionTokens);
    }

    [Fact]
    public void 被中断的回答不估算token_但仍显示已耗时()
    {
        // 残缺文本推不出有意义的用量，估算只会误导；
        // 但耗时是确定的，按设计文档 4.2 仍要展示。
        var vm = Create(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = new string('字', 100),
        });

        vm.ApplyStatistics(TimeSpan.FromSeconds(1), usage: null, cost: null, "m", interrupted: true);

        Assert.Null(vm.TotalTokens);
        Assert.True(vm.IsInterrupted);
        Assert.True(vm.HasStatistics);
        Assert.Equal("1.0s   │   -- tokens", vm.StatisticsText);
    }

    [Fact]
    public void 偏好变化后刷新统计文本()
    {
        var prefs = new UserPreferences();
        var vm = Create(AssistantMessage(), prefs);
        vm.IsStatisticsExpanded = true;
        Assert.Contains("$", vm.StatisticsText);

        prefs.ShowCost = false;
        vm.RefreshStatistics();

        Assert.DoesNotContain("$", vm.StatisticsText);
    }

    [Fact]
    public void 历史消息有思考内容时显示已思考()
    {
        var vm = Create(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "回答",
            ThinkingContent = "推理过程",
        });

        Assert.True(vm.HasThinking);
        Assert.False(vm.IsThinking);
        Assert.Equal(Loc.Get("Chat.ThinkingDone"), vm.ThinkingStateLabel);
    }

    [Fact]
    public void 流式推理期间显示思考中_开始回答后切到已思考()
    {
        var vm = Create(new ChatMessage { Role = MessageRole.Assistant, Content = "" });

        vm.AppendThinking("让我想想");

        Assert.True(vm.IsThinking);
        Assert.Equal(Loc.Get("Chat.ThinkingInProgress"), vm.ThinkingStateLabel);

        vm.AppendText("好的");

        Assert.False(vm.IsThinking);
        Assert.Equal(Loc.Get("Chat.ThinkingDone"), vm.ThinkingStateLabel);
        Assert.Equal("好的", vm.Content);
    }

    [Fact]
    public void 纯推理消息落库时由EndThinking切到已思考()
    {
        var vm = Create(new ChatMessage { Role = MessageRole.Assistant, Content = "" });

        vm.AppendThinking("只有推理没有回答");
        Assert.Equal(Loc.Get("Chat.ThinkingInProgress"), vm.ThinkingStateLabel);

        vm.EndThinking();

        Assert.False(vm.IsThinking);
        Assert.Equal(Loc.Get("Chat.ThinkingDone"), vm.ThinkingStateLabel);
    }
}
