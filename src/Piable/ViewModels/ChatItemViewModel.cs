namespace Piable.ViewModels;

/// <summary>
/// 对话流中的一个条目。消息与工具调用都排在同一个列表里，
/// 因此需要一个共同基类，让界面用 DataTemplate 按类型分派渲染。
/// </summary>
public abstract class ChatItemViewModel : ViewModelBase
{
    /// <summary>该条目是否是由用户发出的（用于决定对齐方式）。</summary>
    public abstract bool IsUser { get; }
}
