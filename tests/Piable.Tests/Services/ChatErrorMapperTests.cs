using System.Net;
using System.Text.Json;
using Piable.Services;

namespace Piable.Tests.Services;

public class ChatErrorMapperTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "认证失败")]
    [InlineData(HttpStatusCode.Forbidden, "权限不足")]
    [InlineData(HttpStatusCode.NotFound, "端点或模型不存在")]
    [InlineData(HttpStatusCode.RequestTimeout, "请求超时")]
    [InlineData(HttpStatusCode.TooManyRequests, "配额")]
    [InlineData(HttpStatusCode.InternalServerError, "供应商服务异常")]
    [InlineData(HttpStatusCode.BadGateway, "供应商服务异常")]
    public void HTTP状态码映射为用户可读提示(HttpStatusCode status, string expectedFragment)
    {
        var exception = new HttpRequestException("raw", inner: null, statusCode: status);

        var message = ChatErrorMapper.ToUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains(expectedFragment, message);
    }

    [Fact]
    public void 无状态码的网络异常映射为连接异常()
    {
        var message = ChatErrorMapper.ToUserMessage(new HttpRequestException("connection refused"));

        Assert.Equal("网络连接异常", message);
    }

    [Fact]
    public void 超时映射为超时提示()
    {
        Assert.Contains("超时", ChatErrorMapper.ToUserMessage(new TimeoutException())!);
        Assert.Contains("超时", ChatErrorMapper.ToUserMessage(
            new TaskCanceledException("timeout", new TimeoutException()))!);
    }

    [Fact]
    public void 用户主动取消时不产生提示()
    {
        var exception = new OperationCanceledException();

        Assert.Null(ChatErrorMapper.ToUserMessage(exception, userCancelled: true));
    }

    [Fact]
    public void 非用户发起的取消静默处理()
    {
        // 取消但不是用户点的停止（例如窗口关闭），同样不必弹错误
        Assert.Null(ChatErrorMapper.ToUserMessage(new OperationCanceledException(), userCancelled: false));
    }

    [Fact]
    public void JSON解析错误映射为数据错误提示()
    {
        var message = ChatErrorMapper.ToUserMessage(new JsonException("bad json"));

        Assert.Contains("数据解析错误", message!);
    }

    [Fact]
    public void 未知异常给出兜底提示而不泄露堆栈()
    {
        var message = ChatErrorMapper.ToUserMessage(new InvalidOperationException("internal detail"));

        Assert.Equal("未知错误，请查看日志", message);
        Assert.DoesNotContain("internal detail", message);
    }

    [Fact]
    public void 提示文案为中文且带图标()
    {
        var message = ChatErrorMapper.ToUserMessage(
            new HttpRequestException("x", null, HttpStatusCode.Unauthorized));

        Assert.StartsWith("", message);
    }
}
