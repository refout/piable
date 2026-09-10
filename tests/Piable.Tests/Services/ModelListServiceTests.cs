using System.Net;
using System.Text;
using Piable.Models;
using Piable.Services;

namespace Piable.Tests.Services;

public class ModelListServiceTests
{
    /// <summary>记录请求并返回预置响应的假 HttpMessageHandler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ModelListService Service, StubHandler Handler) Create(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(body, status);
        return (new ModelListService(new HttpClient(handler)), handler);
    }

    private static ProviderConfig Provider(
        string presetId = ProviderPresets.OpenAi, string? apiKey = "sk-test") => new()
    {
        Id = "p1",
        PresetId = presetId,
        ApiKey = apiKey,
        Endpoint = "https://api.example.com/v1",
    };

    [Fact]
    public async Task 解析OpenAI格式的模型列表()
    {
        var (service, _) = Create("""
            {"object":"list","data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"o3-mini"}]}
            """);

        var models = await service.FetchModelsAsync(Provider());

        Assert.Equal(["gpt-4o", "gpt-4o-mini", "o3-mini"], models);
    }

    [Fact]
    public void 解析Ollama原生端点的模型列表()
    {
        // 内置 Ollama 预设走的是 /v1/models（OpenAI 格式），但用户也可能把
        // Endpoint 指向原生 /api/tags，因此 Ollama 格式的解析分支必须可用。
        var models = ModelListService.Parse(
            """{"models":[{"name":"llama3:latest"},{"name":"qwen2.5:7b"}]}""",
            ModelListFormat.Ollama);

        Assert.Equal(["llama3:latest", "qwen2.5:7b"], models);
    }

    [Fact]
    public void Ollama新版本用model字段时同样可解析()
    {
        var models = ModelListService.Parse(
            """{"models":[{"model":"llama3:latest"}]}""", ModelListFormat.Ollama);

        Assert.Equal(["llama3:latest"], models);
    }

    [Fact]
    public void 解析结果去重并排序()
    {
        var models = ModelListService.Parse(
            """{"data":[{"id":"b"},{"id":"a"},{"id":"B"},{"id":""},{"id":null}]}""",
            ModelListFormat.OpenAi);

        Assert.Equal(["a", "b"], models);
    }

    [Fact]
    public void 空响应与畸形结构返回空列表而不抛异常()
    {
        Assert.Empty(ModelListService.Parse("{}", ModelListFormat.OpenAi));
        Assert.Empty(ModelListService.Parse("""{"data":null}""", ModelListFormat.OpenAi));
        Assert.Empty(ModelListService.Parse("{}", ModelListFormat.Ollama));
    }

    [Fact]
    public async Task 请求路径由Endpoint与预设路径拼接而成()
    {
        var (service, handler) = Create("""{"data":[]}""");

        await service.FetchModelsAsync(Provider());

        Assert.Equal("https://api.example.com/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task 用户覆盖的Endpoint优先()
    {
        var (service, handler) = Create("""{"data":[]}""");
        var provider = Provider();
        provider.Endpoint = "https://custom.example.com/openai/v1/";

        await service.FetchModelsAsync(provider);

        // 结尾多余的斜杠不应产生双斜杠
        Assert.Equal("https://custom.example.com/openai/v1/models",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task 使用Bearer鉴权()
    {
        var (service, handler) = Create("""{"data":[]}""");

        await service.FetchModelsAsync(Provider());

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public void Azure使用api_key头而非Bearer()
    {
        // Azure 预设本身不支持获取模型列表，无法经由 FetchModelsAsync 走到鉴权分支，
        // 因此直接验证鉴权头的构造逻辑。
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/models");

        ModelListService.ApplyAuthentication(
            request, Provider(ProviderPresets.AzureOpenAi), ProviderPresets.Get(ProviderPresets.AzureOpenAi));

        Assert.Null(request.Headers.Authorization);
        Assert.True(request.Headers.TryGetValues("api-key", out var values));
        Assert.Equal("sk-test", values.Single());
    }

    [Fact]
    public async Task 本地无Key供应商不发送鉴权头()
    {
        var (service, handler) = Create("""{"data":[]}""");

        await service.FetchModelsAsync(Provider(ProviderPresets.Ollama, apiKey: null));

        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.False(handler.LastRequest.Headers.Contains("api-key"));
    }

    [Fact]
    public async Task 不支持动态获取的预设直接给出提示()
    {
        var (service, _) = Create("""{"data":[]}""");

        // Azure 预设未配置 ModelsEndpoint
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FetchModelsAsync(Provider(ProviderPresets.AzureOpenAi)));

        Assert.Contains("手动添加", ex.Message);
    }

    [Fact]
    public async Task 服务端返回错误状态时抛出异常()
    {
        var (service, _) = Create("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchModelsAsync(Provider()));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("认证失败", ChatErrorMapper.ToUserMessage(ex));
    }
}
