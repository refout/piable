using System.Net.Http.Headers;
using System.Text.Json;
using Piable.Helpers;
using Piable.Models;

namespace Piable.Services;

/// <summary>从供应商 API 动态获取模型列表（设计文档 7.3）。</summary>
public interface IModelListService
{
    /// <summary>拉取模型列表。失败时抛出异常，由调用方映射为用户提示。</summary>
    Task<IReadOnlyList<string>> FetchModelsAsync(
        ProviderConfig provider, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ModelListService : IModelListService
{
    private readonly HttpClient _httpClient;

    public ModelListService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        var preset = ProviderPresets.Find(provider.PresetId)
            ?? throw new InvalidOperationException(Loc.Get("Provider.UnknownPreset", provider.PresetId));

        if (!preset.SupportsModelFetch)
        {
            throw new InvalidOperationException(
                Loc.Get("Provider.NoModelFetch", preset.DisplayName));
        }

        var url = BuildUrl(provider, preset);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuthentication(request, provider, preset);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 复用统一的错误映射，保证"获取模型列表"失败时的提示与对话失败时一致
            throw new HttpRequestException(
                ChatErrorMapper.FromStatusCode(response.StatusCode),
                inner: null,
                statusCode: response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json, preset.ModelListFormat);
    }

    /// <summary>把预设里的相对路径拼到 Endpoint 之后。</summary>
    internal static string BuildUrl(ProviderConfig provider, ProviderPreset preset)
    {
        var endpoint = !string.IsNullOrWhiteSpace(provider.Endpoint)
            ? provider.Endpoint
            : preset.DefaultEndpoint;

        return endpoint.TrimEnd('/') + "/" + preset.ModelsEndpoint!.TrimStart('/');
    }

    /// <summary>
    /// Azure 用 api-key 头，其余供应商用标准的 Bearer 鉴权；
    /// 无需 Key 的本地服务（Ollama）不加任何鉴权头。
    /// </summary>
    internal static void ApplyAuthentication(
        HttpRequestMessage request, ProviderConfig provider, ProviderPreset preset)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return;
        }

        if (preset.ProviderType == ProviderType.AzureOpenAI)
        {
            request.Headers.TryAddWithoutValidation("api-key", provider.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }
    }

    /// <summary>按预设声明的格式解析响应，并做去重与排序，便于界面直接绑定。</summary>
    internal static IReadOnlyList<string> Parse(string json, ModelListFormat format)
    {
        var names = format switch
        {
            ModelListFormat.Ollama => ParseOllama(json),
            _ => ParseOpenAi(json),
        };

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseOpenAi(string json)
    {
        var payload = JsonSerializer.Deserialize(json, PiableJsonContext.Default.OpenAiModelListResponse);
        return payload?.Data?.Select(entry => entry.Id ?? string.Empty).ToList() ?? [];
    }

    private static List<string> ParseOllama(string json)
    {
        var payload = JsonSerializer.Deserialize(json, PiableJsonContext.Default.OllamaModelListResponse);
        // Ollama 早期版本用 name，较新版本两者都给，取到哪个用哪个
        return payload?.Models?
            .Select(entry => entry.Name ?? entry.Model ?? string.Empty)
            .ToList() ?? [];
    }
}
