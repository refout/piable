using Piable.Services;

namespace Piable.Tests.Services;

public class SessionTitleGeneratorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void 空消息回落到默认标题(string? input)
    {
        Assert.Equal(SessionTitleGenerator.DefaultTitle, SessionTitleGenerator.Generate(input));
    }

    [Fact]
    public void 短消息原样作为标题()
    {
        Assert.Equal("你好", SessionTitleGenerator.Generate("你好"));
    }

    [Fact]
    public void 超过二十字截断并加省略号()
    {
        var input = new string('字', 30);

        var title = SessionTitleGenerator.Generate(input);

        Assert.Equal(new string('字', 20) + "…", title);
        Assert.Equal(21, title.Length);
    }

    [Fact]
    public void 正好二十字不截断()
    {
        var input = new string('字', 20);

        Assert.Equal(input, SessionTitleGenerator.Generate(input));
    }

    [Fact]
    public void 换行被折叠为空格()
    {
        Assert.Equal("第一行 第二行", SessionTitleGenerator.Generate("第一行\n第二行"));
    }

    [Fact]
    public void 连续空白折叠为单个空格()
    {
        Assert.Equal("a b", SessionTitleGenerator.Generate("a  \n\n\t  b"));
    }

    [Fact]
    public void 全角空格同样被折叠()
    {
        Assert.Equal("a b", SessionTitleGenerator.Generate("a　　b"));
    }

    [Fact]
    public void 标题首尾不留空白()
    {
        Assert.Equal("内容", SessionTitleGenerator.Generate("   \n 内容 \n  "));
    }

    [Fact]
    public void 截断按字符而非字节_中文不被腰斩()
    {
        var title = SessionTitleGenerator.Generate("这是一段比较长的中文提问内容需要被截断处理掉多余部分");

        // 取前 20 个字符（"这是一段比较长的中文提问内容需要被截断处"）后加省略号，
        // 而不是按 UTF-8 字节截断——那会把汉字劈成半个
        Assert.Equal("这是一段比较长的中文提问内容需要被截断处…", title);
        Assert.Equal(21, title.Length);
    }
}
