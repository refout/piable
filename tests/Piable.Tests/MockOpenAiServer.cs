using System.Net;
using System.Text;
using System.Text.Json;

namespace Piable.Tests;

/// <summary>
/// 一个最小的 OpenAI 兼容服务端，用于端到端验证流式链路。
///
/// 之所以不直接 mock <c>IChatClient</c>：真正容易出问题的是 HTTP 与 SSE 这一层
/// （请求路径、鉴权头、流式分帧、usage 字段名）。只替换接口会把这些全部绕开，
/// 测试通过也说明不了实际的对话流程可用。
/// </summary>
/// <remarks>
/// 全进程只启动一个 <see cref="HttpListener"/>，每个实例用一个随机虚拟路径区分：
/// <c>http://127.0.0.1:{port}/{route}/chat/completions</c>。
///
/// 不让每个实例各起一个监听器的原因：同一进程内多个 HttpListener 会互相串台——
/// 发给 A 端口的请求可能被 B 接走（http.sys 的 URL 组按其内部映射表分发，
/// 而 .NET 的 HttpListener 对同主机不同端口的登记并不隔离）。表现是测试收到
/// 别的测试构造的回复，随机失败且极难定位。共用一个监听器后，路由由我们自己按路径做，
/// 投递目标唯一确定。
/// </remarks>
internal sealed class MockOpenAiServer : IAsyncDisposable
{
    private static readonly Lock SharedGate = new();
    private static readonly Dictionary<string, MockOpenAiServer> Routes = new(StringComparer.Ordinal);
    private static HttpListener? s_listener;
    private static string? s_baseUrl;

    private readonly string _route;
    private readonly List<string> _requestBodies = [];
    private readonly Lock _gate = new();

    private MockOpenAiServer(string route) => _route = route;

    /// <summary>作为 ProviderConfig.Endpoint 使用的地址，形如 http://127.0.0.1:{port}/{route}/。</summary>
    public string BaseUrl => $"{s_baseUrl}/{_route}/";

    /// <summary>服务端会返回的 SSE 响应体。测试可替换为任意内容。</summary>
    public string SseBody { get; set; } = string.Empty;

    /// <summary>按顺序返回的响应队列。队列耗尽后回落到 <see cref="SseBody"/>。</summary>
    private readonly Queue<string> _queuedResponses = new();

    /// <summary>入队一个响应。用于需要多轮往返的场景（例如先请求工具、再给出最终回答）。</summary>
    public void EnqueueResponse(string sseBody)
    {
        lock (_gate)
        {
            _queuedResponses.Enqueue(sseBody);
        }
    }

    /// <summary>
    /// 非流式响应体。设为非 null 时按 application/json 返回。
    /// 流式调用与"测试连接"这类一次性调用期望的响应格式不同，需要分别构造。
    /// </summary>
    public string? JsonBody { get; set; }

    /// <summary>服务端返回的 HTTP 状态码。</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>最近一次请求的 Authorization 头。</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>最近一次请求的路径（含查询串，已去掉本实例的虚拟路径前缀）。</summary>
    public string? LastPath { get; private set; }

    /// <summary>收到的全部请求体。</summary>
    public IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestBodies];
            }
        }
    }

    public static MockOpenAiServer Start()
    {
        EnsureListener();

        var route = Guid.NewGuid().ToString("n");
        var server = new MockOpenAiServer(route);

        lock (SharedGate)
        {
            Routes[route] = server;
        }

        return server;
    }

    private static void EnsureListener()
    {
        lock (SharedGate)
        {
            if (s_listener is not null)
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var port = ReserveFreePort();
                var prefix = $"http://127.0.0.1:{port}/";

                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);

                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    continue;
                }

                s_listener = listener;
                s_baseUrl = prefix.TrimEnd('/');
                _ = Task.Run(() => AcceptLoopAsync(listener));
                return;
            }
        }

        throw new InvalidOperationException("无法为 mock 服务端找到可用端口");
    }

    private static int ReserveFreePort()
    {
        // 端口 0 让系统分配空闲端口，避免与机器上其他进程抢占
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 进程退出或监听被关闭
                return;
            }

            // 并行处理：串行会让多轮工具调用互相等待，测试直接超时
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchAsync(context).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 单个请求处理失败不应让监听循环退出
                }
            });
        }
    }

    private static async Task DispatchAsync(HttpListenerContext context)
    {
        var pathAndQuery = context.Request.Url?.PathAndQuery ?? "/";
        var segments = pathAndQuery.TrimStart('/').Split('/');
        var route = segments.Length > 0 ? segments[0] : string.Empty;

        MockOpenAiServer? target;
        lock (SharedGate)
        {
            Routes.TryGetValue(route, out target);
        }

        if (target is null)
        {
            // 对应的测试已经结束（服务端已释放），请求来晚了
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        // 去掉虚拟路径段，让断言看到的仍是 "/chat/completions"
        var relative = "/" + string.Join('/', segments.Skip(1));
        await target.HandleAsync(context, relative).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpListenerContext context, string relativePath)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _requestBodies.Add(body);
        }

        LastAuthorization = context.Request.Headers["Authorization"];
        LastPath = relativePath;

        context.Response.StatusCode = StatusCode;

        var isJson = JsonBody is not null;
        context.Response.ContentType = isJson ? "application/json" : "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";

        string responseBody;
        lock (_gate)
        {
            responseBody = _queuedResponses.Count > 0 ? _queuedResponses.Dequeue() : SseBody;
        }

        var payload = Encoding.UTF8.GetBytes(isJson ? JsonBody! : responseBody);
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>构造一段 OpenAI 风格的流式响应：若干文本增量 + 收尾 usage。</summary>
    public static string BuildSse(
        IEnumerable<string> textDeltas,
        string model = "gpt-4o-mini",
        int? promptTokens = null,
        int? completionTokens = null)
    {
        var builder = new StringBuilder();

        foreach (var delta in textDeltas)
        {
            builder.Append("data: ").Append(ChunkJson(model, delta, "null")).Append("\n\n");
        }

        // 收尾 chunk：delta 为空，finish_reason 为 stop，usage 只在这一次给出
        var usage = promptTokens is null && completionTokens is null
            ? "null"
            : BuildUsageJson(promptTokens ?? 0, completionTokens ?? 0);

        builder.Append("data: ").Append(ChunkJson(model, content: null, usage, finishReason: "stop"))
            .Append("\n\n");
        builder.Append("data: [DONE]\n\n");

        return builder.ToString();
    }

    /// <summary>
    /// 用 Utf8JsonWriter 构造 chunk，避免手写 JSON 时的转义与括号错误。
    /// </summary>
    private static string ChunkJson(
        string model, string? content, string usageJson, string? finishReason = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "chatcmpl-1");
            writer.WriteString("object", "chat.completion.chunk");
            writer.WriteNumber("created", 1);
            writer.WriteString("model", model);

            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("delta");
            if (content is not null)
            {
                writer.WriteString("content", content);
            }

            writer.WriteEndObject();

            if (finishReason is null)
            {
                writer.WriteNull("finish_reason");
            }
            else
            {
                writer.WriteString("finish_reason", finishReason);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();

            if (usageJson != "null")
            {
                writer.WritePropertyName("usage");
                writer.WriteRawValue(usageJson);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>构造一段非流式的完整响应，供"测试连接"这类一次性调用使用。</summary>
    public static string BuildCompletionJson(
        string content = "pong",
        string model = "gpt-4o-mini",
        int promptTokens = 1,
        int completionTokens = 1)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "chatcmpl-1");
            writer.WriteString("object", "chat.completion");
            writer.WriteNumber("created", 1);
            writer.WriteString("model", model);

            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            writer.WriteString("content", content);
            writer.WriteEndObject();
            writer.WriteString("finish_reason", "stop");
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("usage");
            writer.WriteRawValue(BuildUsageJson(promptTokens, completionTokens));

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 构造一段请求工具调用的流式响应。
    /// OpenAI 的流式工具调用是增量的：delta.tool_calls[].function.arguments 是分片拼起来的，
    /// 这里一次性给全，便于断言。
    /// </summary>
    public static string BuildToolCallSse(
        string toolName,
        string argumentsJson = "{}",
        string callId = "call_1",
        string model = "gpt-4o-mini")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "chatcmpl-tool");
            writer.WriteString("object", "chat.completion.chunk");
            writer.WriteNumber("created", 1);
            writer.WriteString("model", model);

            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);

            // tool_calls 必须嵌在 delta 里，与 content 同级
            writer.WriteStartObject("delta");
            writer.WriteStartArray("tool_calls");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteString("id", callId);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", toolName);
            writer.WriteString("arguments", argumentsJson);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();   // delta

            writer.WriteNull("finish_reason");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var chunk = Encoding.UTF8.GetString(stream.ToArray());
        return $"data: {chunk}\n\ndata: [DONE]\n\n";
    }

    private static string BuildUsageJson(int promptTokens, int completionTokens)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("prompt_tokens", promptTokens);
            writer.WriteNumber("completion_tokens", completionTokens);
            writer.WriteNumber("total_tokens", promptTokens + completionTokens);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        lock (SharedGate)
        {
            Routes.Remove(_route);
        }

        return ValueTask.CompletedTask;
    }
}
