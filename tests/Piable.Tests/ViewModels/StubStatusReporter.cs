using Piable.ViewModels;

namespace Piable.Tests.ViewModels;

/// <summary>记录状态栏写入的桩，用于断言错误是否被正确上报。</summary>
internal sealed class StubStatusReporter : IStatusReporter
{
    public List<string> Infos { get; } = [];

    public List<string> Successes { get; } = [];

    public List<string> Errors { get; } = [];

    public string? Busy { get; private set; }

    public void ReportInfo(string message) => Infos.Add(message);

    public void ReportSuccess(string message) => Successes.Add(message);

    public void ReportError(string message) => Errors.Add(message);

    public void SetBusy(string? message) => Busy = message;
}
