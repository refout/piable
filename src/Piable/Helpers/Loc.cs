using Piable.Services;

namespace Piable.Helpers;

/// <summary>
/// 文案查找的静态入口：<c>Loc.Get("Chat.Send")</c>。
/// </summary>
/// <remarks>
/// 为什么不把 <see cref="ILocalizationService"/> 注入到每个 ViewModel：
/// 取文案是横切关注点，和日志一样——为它在十余个构造函数里各加一个参数，
/// 只会让依赖列表变长而带不来任何可测试性的提升。这里持有一个服务实例
/// （默认是内置默认语言的实现），测试与启动流程可以整体替换。
/// </remarks>
public static class Loc
{
    private static ILocalizationService _service = new LocalizationService();

    static Loc() => _service.LanguageChanged += OnServiceLanguageChanged;

    /// <summary>当前使用的服务。替换后事件会重新转发。</summary>
    public static ILocalizationService Service => _service;

    /// <summary>语言已切换；需要在语言变化后重算派生文本的界面监听它。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// 换掉门面背后的服务。<paramref name="service"/> 与当前相同则什么也不做——
    /// 否则同一个服务会被反复挂上转发器，切一次语言就通知好几遍。
    /// </summary>
    public static void Use(ILocalizationService service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        _service.LanguageChanged -= OnServiceLanguageChanged;
        _service = service;
        _service.LanguageChanged += OnServiceLanguageChanged;
    }

    private static void OnServiceLanguageChanged(object? sender, EventArgs e) =>
        LanguageChanged?.Invoke(null, EventArgs.Empty);

    public static string Get(string key) => _service.Get(key);

    public static string Get(string key, params object?[] args) => _service.Get(key, args);
}
