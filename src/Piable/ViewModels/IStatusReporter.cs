namespace Piable.ViewModels;

/// <summary>
/// 底部状态栏的写入接口。由 <see cref="MainWindowViewModel"/> 实现，
/// 注入给子 ViewModel，让它们无需知道状态栏的具体形态即可反馈进度与错误。
/// </summary>
public interface IStatusReporter
{
    /// <summary>常规提示，短暂显示后自动消失。</summary>
    void ReportInfo(string message);

    /// <summary>成功提示。</summary>
    void ReportSuccess(string message);

    /// <summary>错误提示。</summary>
    void ReportError(string message);

    /// <summary>进行中的操作，如"生成中…"。传 null 表示清除。</summary>
    void SetBusy(string? message);
}
