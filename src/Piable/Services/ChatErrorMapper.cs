using System.ClientModel;
using System.Net;
using System.Text.Json;
using Piable.Helpers;

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
                ? Loc.Get("Error.Timeout")
                : null;
        }

        return exception switch
        {
            ClientResultException client => FromStatusCode(client.Status),
            HttpRequestException http => FromHttpException(http),
            TimeoutException => Loc.Get("Error.Timeout"),
            JsonException => Loc.Get("Error.Parse"),
            _ => Loc.Get("Error.Unknown"),
        };
    }

    private static string FromHttpException(HttpRequestException exception) =>
        exception.StatusCode is { } status
            ? FromStatusCode((int)status)
            : Loc.Get("Error.Network");

    private static string FromStatusCode(int status) => status switch
    {
        401 => Loc.Get("Error.Unauthorized"),
        403 => Loc.Get("Error.Forbidden"),
        404 => Loc.Get("Error.NotFound"),
        408 => Loc.Get("Error.Timeout"),
        429 => Loc.Get("Error.RateLimited"),
        >= 500 => Loc.Get("Error.ServerError"),
        _ => Loc.Get("Error.RequestFailed"),
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
