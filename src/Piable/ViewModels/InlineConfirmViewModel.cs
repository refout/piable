using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Piable.ViewModels;

/// <summary>
/// 列表项的内联二次确认（设计文档 4.4）：首次点击进入确认态，
/// 再次点击才真正执行，超时或点击别处自动取消。
/// 会话列表与智能体列表共用这套交互。
/// </summary>
public abstract partial class InlineConfirmViewModel : ViewModelBase
{
    /// <summary>确认状态的有效时长。</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _confirmCts;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>用户完成二次确认，由外层执行真正的删除。</summary>
    public event EventHandler<InlineConfirmViewModel>? DeleteConfirmed;

    /// <summary>
    /// 点击删除按钮。首次进入确认态，确认态下再次点击则删除。
    ///
    /// 注意这里刻意用同步命令：若把 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// 直接 await 在命令里，命令会一直处于"执行中"，而 AsyncRelayCommand 在执行期间
    /// 会把按钮置为不可用——用户第二次点击会被直接吞掉，确认流程永远走不通。
    /// 因此计时放到后台，点击处理必须立即返回。
    /// </summary>
    [RelayCommand]
    private void RequestDelete()
    {
        if (IsConfirmingDelete)
        {
            ConfirmDelete();
            return;
        }

        CancelConfirm();
        IsConfirmingDelete = true;

        var cts = new CancellationTokenSource();
        _confirmCts = cts;
        _ = ExpireConfirmAsync(cts);
    }

    private async Task ExpireConfirmAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(ConfirmTimeout, cts.Token).ConfigureAwait(true);
            IsConfirmingDelete = false;
        }
        catch (OperationCanceledException)
        {
            // 已被取消，确认态由取消方重置
        }
        finally
        {
            // 由本任务负责释放：取消方只 Cancel 不 Dispose，
            // 避免在延迟仍持有 token 时释放造成竞态。
            cts.Dispose();
        }
    }

    /// <summary>退出确认态。点击别处、切换选中项时调用。</summary>
    public void CancelConfirm()
    {
        var cts = _confirmCts;
        _confirmCts = null;

        // 只取消不释放，释放交给 ExpireConfirmAsync 的 finally
        cts?.Cancel();
        IsConfirmingDelete = false;
    }

    private void ConfirmDelete()
    {
        CancelConfirm();
        DeleteConfirmed?.Invoke(this, this);
    }
}
