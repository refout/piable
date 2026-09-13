using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Piable.ViewModels;
using Piable.Helpers;

namespace Piable.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// 距底部多少像素以内仍视为"跟随中"。超过这个距离说明用户主动上滚查看历史，
    /// 此时不应再把他拽回底部（设计文档 3.3.2）。
    /// </summary>
    private const double AutoScrollThreshold = 80;

    private MainWindowViewModel? _viewModel;
    private ChatSessionViewModel? _session;

    /// <summary>合并同一帧内的多次滚动到底部请求，避免流式输出时每个 delta 都排一个回调。</summary>
    private bool _scrollToEndPending;

    public MainWindow()
    {
        InitializeComponent();

        // 用隧道阶段拦截回车：正常冒泡阶段 TextBox 会先把 Enter 当成换行吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            AttachSession(_viewModel.CurrentSession);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentSession))
        {
            AttachSession(_viewModel?.CurrentSession);
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsRenamingSession)
            && _viewModel?.IsRenamingSession == true)
        {
            FocusTitleEditor();
        }
    }

    /// <summary>
    /// 进入重命名时把焦点交给输入框并全选，用户可以直接覆写原标题。
    /// 必须推迟一帧：此刻输入框刚由 IsVisible 变为可见，还没有完成布局，
    /// 立即 Focus 会拿不到焦点。
    /// </summary>
    private void FocusTitleEditor() => Dispatcher.UIThread.Post(() =>
    {
        SessionTitleEditor.Focus();
        SessionTitleEditor.SelectAll();
    });

    private void AttachSession(ChatSessionViewModel? session)
    {
        if (_session is not null)
        {
            _session.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _session = session;

        if (_session is not null)
        {
            _session.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        if (MessageScrollViewer is null || _scrollToEndPending)
        {
            return;
        }

        // 延后到布局完成后再判断几何：Items.Add 之后 ScrollViewer 的 Extent 还没重算，
        // 同步读取会拿到旧高度，ScrollToEnd 也就落不到真正底部。Background 优先级在
        // 测量/排列/渲染之后执行，保证读到的是包含新消息后的最新高度。
        // 用 _scrollToEndPending 合并同一帧内多次请求（流式每个 delta 都会触发），避免堆积。
        _scrollToEndPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollToEndPending = false;
            ScrollToBottomIfFollowing();
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottomIfFollowing()
    {
        if (MessageScrollViewer is null)
        {
            return;
        }

        var distanceFromBottom = MessageScrollViewer.Extent.Height
                                 - MessageScrollViewer.Viewport.Height
                                 - MessageScrollViewer.Offset.Y;

        if (distanceFromBottom <= AutoScrollThreshold)
        {
            MessageScrollViewer.ScrollToEnd();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            // Enter 发送，Shift+Enter 换行（设计文档附录 A）
            case Key.Enter when !shift && IsInputFocused():
                if (_viewModel.CurrentSession?.SendCommand.CanExecute(null) == true)
                {
                    _viewModel.CurrentSession.SendCommand.Execute(null);
                }

                e.Handled = true;
                return;

            case Key.N when ctrl:
                _viewModel.NewSessionCommand.Execute(null);
                e.Handled = true;
                return;

            // Ctrl+, 切换配置面板
            case Key.OemComma when ctrl:
                _viewModel.ToggleConfigViewCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Escape when _viewModel.IsConfigView:
                _viewModel.CloseConfigViewCommand.Execute(null);
                e.Handled = true;
                return;
        }
    }

    /// <summary>焦点是否在消息输入框内。只有在这里按 Enter 才发送，避免影响配置页的输入。</summary>
    private bool IsInputFocused() =>
        GetTopLevel(this)?.FocusManager.GetFocusedElement() is { } focused
        && ReferenceEquals(focused, InputBox);

    // ---- 标题内联重命名 ----

    private void OnSessionTitleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                // 先回写：TextBox.Text 默认在失焦时才推回绑定源，
                // 直接用 SessionTitleDraft 会拿到进入编辑时的旧值。
                _viewModel.SessionTitleDraft = SessionTitleEditor.Text ?? string.Empty;
                _viewModel.CommitRenameSessionCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CancelRenameSessionCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>失焦即保存，与常见的重命名交互一致。</summary>
    private void OnSessionTitleEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SessionTitleDraft = SessionTitleEditor.Text ?? string.Empty;
        _viewModel.CommitRenameSessionCommand.Execute(null);
    }

    // ---- 导出当前会话 ----

    /// <summary>
    /// 文件对话框只能从窗口这一侧发起，ViewModel 拿不到 TopLevel。
    /// 因此内容由 ViewModel 产出，选路径与写盘留在这里。
    /// </summary>
    private async void OnExportSessionClick(object? sender, RoutedEventArgs e)
    {
        var session = _viewModel?.CurrentSession;
        if (session is null)
        {
            return;
        }

        var path = await PickMarkdownSavePathAsync(session.Session.Title).ConfigureAwait(true);
        if (path is null)
        {
            return; // 用户取消了
        }

        try
        {
            await File.WriteAllTextAsync(path, session.ExportAsMarkdown()).ConfigureAwait(true);
            _viewModel!.ReportSuccess(Loc.Get("Export.ChatDone"));
        }
        catch (Exception ex)
        {
            _viewModel!.ReportError(Loc.Get("Common.ExportFailed", ex.Message));
        }
    }

    private async Task<string?> PickMarkdownSavePathAsync(string title) =>
        await PickSavePathAsync($"{SanitizeFileName(title)}.md", "Markdown", "md").ConfigureAwait(true);

    private async Task<string?> PickSavePathAsync(string fileName, string label, string extension)
    {
        // 无头环境下没有可用的存储提供者，CanSave 为 false，直接当作用户取消
        if (!StorageProvider.CanSave)
        {
            return null;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = fileName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(label) { Patterns = [$"*.{extension}"] },
            ],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenPathAsync(string label, string extension)
    {
        if (!StorageProvider.CanOpen)
        {
            return null;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(label) { Patterns = [$"*.{extension}"] },
            ],
        }).ConfigureAwait(true);

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    // ---- 智能体导入导出 ----

    private async void OnExportAgentClick(object? sender, RoutedEventArgs e)
    {
        var config = _viewModel?.AgentConfig;
        if (config is null || !config.CanExport)
        {
            return;
        }

        var path = await PickSavePathAsync($"{SanitizeFileName(config.Name)}.json", Loc.Get("FileType.AgentConfig"), "json")
            .ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, config.ExportToJson()).ConfigureAwait(true);
            _viewModel!.ReportSuccess(Loc.Get("Export.AgentDone"));
        }
        catch (Exception ex)
        {
            _viewModel!.ReportError(Loc.Get("Common.ExportFailed", ex.Message));
        }
    }

    private async void OnImportAgentClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var path = await PickOpenPathAsync(Loc.Get("FileType.AgentConfig"), "json").ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await _viewModel.AgentConfig
                .ImportFromJsonAsync(await File.ReadAllTextAsync(path).ConfigureAwait(true))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.ReportError(Loc.Get("Common.ReadFileFailed", ex.Message));
        }
    }

    /// <summary>去掉文件名里不合法的字符；全剩下非法字符时退回一个固定名。</summary>
    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Where(c => !invalid.Contains(c)).ToArray()).Trim();

        return cleaned.Length == 0 ? Loc.Get("Export.DefaultFileName") : cleaned;
    }
}
