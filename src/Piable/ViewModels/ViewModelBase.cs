using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Piable.Helpers;

namespace Piable.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类，只负责一件事：切语言后让界面重取一遍文案。
/// </summary>
/// <remarks>
/// XAML 里的 <c>{DynamicResource Loc.xxx}</c> 会自己跟着资源字典变，
/// 但 ViewModel 上那些"取一条文案再拼点东西"的派生属性不会——
/// 例如"当前跟随默认：gpt-4o"。它们不在资源字典里，只能靠属性变更通知。
///
/// 与其让每个 ViewModel 各自订阅事件、各自记得该通知哪些属性，
/// 不如在基类里统一发一次"全部属性都变了"：Avalonia 收到空属性名的通知
/// 会重新求值该对象上的所有绑定，代价是一次性的全量刷新，
/// 而切换语言本来就是罕见操作。
///
/// 订阅走静态事件，因此用弱引用持有实例：否则每个会话 ViewModel
/// 都会被静态事件一直攥着，聊久了就是一堆释放不掉的会话。
/// </remarks>
public abstract class ViewModelBase : ObservableObject
{
    private static readonly List<WeakReference<ViewModelBase>> Instances = [];

    static ViewModelBase() => Loc.LanguageChanged += (_, _) => RefreshAll();

    [SuppressMessage("Usage", "CA1000", Justification = "静态构造里访问静态字段是预期的")]
    protected ViewModelBase()
    {
        lock (Instances)
        {
            Instances.Add(new WeakReference<ViewModelBase>(this));
        }
    }

    private static void RefreshAll()
    {
        var alive = new List<ViewModelBase>();

        lock (Instances)
        {
            Instances.RemoveAll(reference => !reference.TryGetTarget(out _));

            foreach (var reference in Instances)
            {
                if (reference.TryGetTarget(out var viewModel))
                {
                    alive.Add(viewModel);
                }
            }
        }

        // 空属性名 = "所有属性都变了"
        foreach (var viewModel in alive)
        {
            viewModel.OnPropertyChanged(string.Empty);
        }
    }
}
