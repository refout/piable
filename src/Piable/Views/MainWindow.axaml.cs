using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Piable.ViewModels;

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
        }
    }

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

    /// <summary>焦点是否在输入框内。只有在这里按 Enter 才发送，避免影响配置页的输入。</summary>
    private bool IsInputFocused() =>
        GetTopLevel(this)?.FocusManager.GetFocusedElement() is { } focused
        && ReferenceEquals(focused, InputBox);
}
