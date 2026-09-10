using System.ClientModel;
using System.Net;
using System.Text.Json;

namespace Piable.Services;

/// <summary>
/// 把底层异常翻译成设计文档 10.1 约定的用户可读提示。
/// 界面上不该出现堆栈或英文异常消息，这里也是唯一做该翻译的地方。
/// </summary>
public static class ChatErrorMapper
{
    /// <summary>
    /// 转换为用户可读信息。返回 null 表示应当静默处理（用户主动取消）。
    /// </summary>
    /// <param name="exception">捕获到的异常。</param>
    /// <param name="userCancelled">用户是否主动点了"停止"。</param>
    public static string? ToUserMessage(Exception exception, bool userCancelled = false)
    {
        // 用户主动取消属于正常操作，不提示
        if (userCancelled && exception is OperationCanceledException)
        {
            return null;
        }

        // 超时也表现为 TaskCanceledException，需要与主动取消区分开
        if (exception is OperationCanceledException or TaskCanceledException)
        {
            return IsTimeout(exception)
                ? "⏰ 请求超时，请检查网络"
                : null;
        }

        return exception switch
        {
            ClientResultException client => FromStatusCode(client.Status),
            HttpRequestException http => FromHttpException(http),
            TimeoutException => "⏰ 请求超时，请检查网络",
            JsonException => "⚠️ 数据解析错误，请检查 API 响应",
            _ => "⚠️ 未知错误，请查看日志",
        };
    }

    private static string FromHttpException(HttpRequestException exception) =>
        exception.StatusCode is { } status
            ? FromStatusCode((int)status)
            : "⚠️ 网络连接异常";

    private static string FromStatusCode(int status) => status switch
    {
        401 => "❌ 认证失败，请检查 API Key",
        403 => "❌ 权限不足",
        404 => "❌ 端点或模型不存在",
        408 => "⏰ 请求超时，请检查网络",
        429 => "⚠️ API 配额已用尽，请稍后重试",
        >= 500 => "⚠️ 供应商服务异常，请稍后重试",
        _ => "⚠️ 请求失败，请检查配置",
    };

    /// <summary>
    /// 判断取消是否源于超时。HttpClient 超时抛出的是带内部 TimeoutException 的
    /// TaskCanceledException，据此与用户主动取消区分。
    /// </summary>
    private static bool IsTimeout(Exception exception) =>
        exception is TimeoutException
        || exception.InnerException is TimeoutException
        || (exception is TaskCanceledException { CancellationToken.IsCancellationRequested: false });

    /// <summary>把 HTTP 状态码映射为提示，供"测试连接"等直接拿到状态码的场景使用。</summary>
    public static string FromStatusCode(HttpStatusCode status) => FromStatusCode((int)status);
}
