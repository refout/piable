using Avalonia;
using Avalonia.Controls;
using Piable.Services;

namespace Piable.Helpers;

/// <summary>
/// 把语言包灌进 Avalonia 的资源字典，让 XAML 里的 <c>{DynamicResource Loc.xxx}</c> 生效。
/// </summary>
/// <remarks>
/// 选 DynamicResource 而不是自定义 MarkupExtension：资源字典一改，所有引用处会自动重新求值，
/// "切语言立即生效"是 DynamicResource 的固有能力，不必自己维护订阅与刷新。
/// 代价是键名在 XAML 里只是字符串——拼错了不会编译报错，所以另有一个测试
/// 逐条比对 XAML 里用到的键与语言包里的条目。
/// </remarks>
public static class LocalizedResources
{
    /// <summary>
    /// XAML 里写作 <c>{DynamicResource Loc.Chat.Send}</c>，而语言包里的键是 <c>Chat.Send</c>。
    /// 加前缀是为了在 XAML 中一眼分辨哪些是文案、哪些是画刷与样式，
    /// 这个前缀就是两边对得上的桥梁。
    /// </summary>
    public const string KeyPrefix = "Loc.";

    private static readonly HashSet<string> AppliedKeys = new(StringComparer.Ordinal);

    /// <param name="service">取哪一门语言的文案。</param>
    /// <param name="resources">
    /// 写到哪个资源字典。默认是应用的资源字典；启动早期（<c>App.Initialize</c>）
    /// <c>Application.Current</c> 还没赋值，那时必须把 <c>App</c> 自己的
    /// <c>Resources</c> 传进来，否则第一帧的界面是一片空白。
    /// </param>
    public static void Apply(ILocalizationService service, IResourceDictionary? resources = null)
    {
        resources ??= Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // 先写入新语言的全部条目：同名键直接覆盖，界面不会先闪回空值
        foreach (var pair in service.Strings)
        {
            var key = KeyPrefix + pair.Key;
            resources[key] = pair.Value;
            AppliedKeys.Add(key);
        }

        // 再摘掉新语言里没有的旧键。理论上两套语言包条目一致，
        // 这一步是为了让"某条文案漏翻译"在界面上表现为露出键名，而不是残留上一门语言。
        var stale = AppliedKeys
            .Where(key => !service.Strings.ContainsKey(key[KeyPrefix.Length..]))
            .ToList();
        foreach (var key in stale)
        {
            resources.Remove(key);
            AppliedKeys.Remove(key);
        }
    }
}
