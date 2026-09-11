using Piable.Models;
using Piable.Services;
using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

public class ToolCallViewModelTests
{
    private static ToolCallPayload Payload(
        ToolInvocationStatusPayload status = ToolInvocationStatusPayload.Succeeded,
        ToolRisk risk = ToolRisk.Dangerous,
        int durationMs = 340,
        string result = "退出码：0") => new()
    {
        ToolName = "run_shell",
        DisplayName = "执行命令",
        SourceLabel = "技能 · 执行命令",
        Risk = risk,
        Status = status,
        ArgumentsText = "command=ls -la",
        ResultPayload = result,
        DurationMs = durationMs,
    };

    [Fact]
    public void 摘要包含状态图标_工具名与参数()
    {
        var vm = new ToolCallViewModel(Payload());

        Assert.Equal("🔧 执行命令（command=ls -la）", vm.Summary);
    }

    [Theory]
    [InlineData(ToolInvocationStatusPayload.Succeeded, "🔧")]
    [InlineData(ToolInvocationStatusPayload.Denied, "⛔")]
    [InlineData(ToolInvocationStatusPayload.Failed, "⚠️")]
    public void 不同结局用不同图标区分(ToolInvocationStatusPayload status, string icon)
    {
        Assert.StartsWith(icon, new ToolCallViewModel(Payload(status: status)).Summary);
    }

    [Fact]
    public void 危险工具被标记出来用于绘制警示色()
    {
        Assert.True(new ToolCallViewModel(Payload(risk: ToolRisk.Dangerous)).IsDangerous);
        Assert.False(new ToolCallViewModel(Payload(risk: ToolRisk.Safe)).IsDangerous);
    }

    [Fact]
    public void 被拒绝的调用可被独立识别()
    {
        var denied = new ToolCallViewModel(Payload(status: ToolInvocationStatusPayload.Denied));

        Assert.True(denied.IsDenied);
    }

    [Fact]
    public void 结果全文始终可用_不做截断()
    {
        // 界面用可滚动区域展示结果，因此这里不能再自行截断——
        // 曾经的做法是"截断 + 展开按钮"，但容器限高会把展开后的内容照样裁掉。
        var longResult = new string('x', 5000);
        var vm = new ToolCallViewModel(Payload(result: longResult));

        Assert.Equal(longResult, vm.ResultText);
        Assert.Equal(5000, vm.ResultText.Length);
    }

    [Fact]
    public void 耗时为0时不显示时长()
    {
        var vm = new ToolCallViewModel(Payload(durationMs: 0));

        Assert.False(vm.HasDuration);
        Assert.Equal(string.Empty, vm.DurationText);
    }

    [Fact]
    public void 有耗时时格式化为秒()
    {
        var vm = new ToolCallViewModel(Payload(durationMs: 1250));

        Assert.True(vm.HasDuration);
        Assert.Equal("1.3s", vm.DurationText);
    }

    [Fact]
    public void 由编排记录转换时保留来源与耗时()
    {
        var record = new ToolInvocationRecord
        {
            ToolName = "current_datetime",
            DisplayName = "当前时间",
            SourceLabel = "技能 · 当前时间",
            Risk = ToolRisk.Safe,
            ArgumentsText = "（无参数）",
            Status = ToolInvocationStatus.Succeeded,
            ResultPayload = "当前时间：2026-09-11 01:00:00",
            Iteration = 1,
            Duration = TimeSpan.FromMilliseconds(120),
        };

        var payload = ToolCallViewModel.ToPayload(record);

        Assert.Equal("current_datetime", payload.ToolName);
        Assert.Equal("当前时间", payload.DisplayName);
        Assert.Equal(ToolRisk.Safe, payload.Risk);
        Assert.Equal(ToolInvocationStatusPayload.Succeeded, payload.Status);
        Assert.Equal(120, payload.DurationMs);
    }

    [Fact]
    public void 编排层的被拒状态映射为持久化的被拒状态()
    {
        var record = new ToolInvocationRecord
        {
            ToolName = "run_shell",
            DisplayName = "执行命令",
            SourceLabel = "技能 · 执行命令",
            Risk = ToolRisk.Dangerous,
            ArgumentsText = "command=rm -rf /",
            Status = ToolInvocationStatus.Denied,
            ResultPayload = "已拒绝",
            Iteration = 1,
        };

        Assert.Equal(
            ToolInvocationStatusPayload.Denied,
            ToolCallViewModel.ToPayload(record).Status);
    }
}
