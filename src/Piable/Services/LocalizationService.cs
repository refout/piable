using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Piable.Models;

namespace Piable.Services;

/// <summary>界面上可选的一种语言。显示名用该语言自己的写法（"简体中文" / "English"）。</summary>
/// <param name="Code">语言代码，如 zh-CN。</param>
/// <param name="NativeName">该语言自己的名字，不随界面语言变化。</param>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>界面文案查找。</summary>
public interface ILocalizationService
{
    /// <summary>当前语言代码。</summary>
    string LanguageCode { get; }

    /// <summary>可选语言。</summary>
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>当前语言包的全部条目。界面层用它把文案灌进资源字典。</summary>
    IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>取一条文案；找不到时原样返回键名，界面上会直接露出"Loc.xxx"而不是空白。</summary>
    string Get(string key);

    /// <summary>带占位符的文案，如 <c>Get("Chat.Copied", name)</c>。</summary>
    string Get(string key, params object?[] args);

    /// <summary>切换语言。会触发 <see cref="LanguageChanged"/>。</summary>
    void SetLanguage(string languageCode);

    /// <summary>语言已切换，界面需要重新取一次文案。</summary>
    event EventHandler? LanguageChanged;
}

/// <inheritdoc />
public sealed class LocalizationService : ILocalizationService
{
    /// <summary>语言包以嵌入资源打包：单文件发布时不会有散落的文件，也不依赖工作目录。</summary>
    private const string ResourcePrefix = "Piable.Resources.Strings.";

    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    private string _languageCode;

    public LocalizationService(string? languageCode = null)
    {
        AvailableLanguages =
        [
            new LanguageOption(DefaultLanguage, "简体中文"),
            new LanguageOption(English, "English"),
        ];

        _languageCode = Normalize(languageCode);
        foreach (var pair in LoadPack(_languageCode))
        {
            _strings[pair.Key] = pair.Value;
        }
    }

    /// <summary>内置默认语言。应用首次启动时没有偏好记录，就用它。</summary>
    public const string DefaultLanguage = "zh-CN";

    public const string English = "en-US";

    public string LanguageCode => _languageCode;

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    public IReadOnlyDictionary<string, string> Strings => _strings;

    public event EventHandler? LanguageChanged;

    public string Get(string key) =>
        _strings.TryGetValue(key, out var value) ? value : key;

    public string Get(string key, params object?[] args)
    {
        var format = Get(key);
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    public void SetLanguage(string languageCode)
    {
        var normalized = Normalize(languageCode);
        if (string.Equals(normalized, _languageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pack = LoadPack(normalized);

        _strings.Clear();
        foreach (var pair in pack)
        {
            _strings[pair.Key] = pair.Value;
        }

        _languageCode = normalized;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(normalized);

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>找不到的语言代码一律退回默认语言，别让界面变成一片键名。</summary>
    private string Normalize(string? languageCode) =>
        AvailableLanguages.Any(l => string.Equals(l.Code, languageCode, StringComparison.OrdinalIgnoreCase))
            ? languageCode!
            : DefaultLanguage;

    private static Dictionary<string, string> LoadPack(string languageCode)
    {
        var name = ResourcePrefix + languageCode + ".json";

        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"缺少语言包：{name}");

        // 走源生成器上下文而非反射：项目已禁用 STJ 的反射回退
        return JsonSerializer.Deserialize(stream, PiableJsonContext.Default.StringDictionary)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
