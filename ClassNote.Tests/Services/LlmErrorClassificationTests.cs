using System;
using System.Net.Http;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// LLM 失败分类（v0.6.0 审查 🟡-4）的回归测试。
/// 这个分类同时决定三件事，混在一起会踩两个方向的坑：
/// · 把配置错误当瞬时错误 → 长录音为每一段白打两次注定失败的请求，
///   最后产出一篇满是"（第 N 段整理失败：尚未配置 API Key）"却标记为"完成"的笔记；
/// · 把普通 4xx 当整体失败 → 某一段素材超长被拒，就让整节课已经整理好的部分一起陪葬。
/// </summary>
public class LlmErrorClassificationTests
{
    [Fact]
    public void ConfigurationError_IsNotTransient_AndFailsWholeNote()
    {
        var ex = new LlmCallException("尚未配置 LLM API Key，请在「设置」中配置。",
            isTransient: false, isConfigurationError: true);

        Assert.False(LlmService.IsTransientError(ex));
        Assert.True(LlmService.IsConfigurationError(ex));
    }

    [Fact]
    public void TransientFailure_IsRetriedButNotFatalForWholeNote()
    {
        // JSON 模式返回空内容 / 429 / 5xx：值得重试一次，但不代表整篇笔记没救
        var ex = new LlmCallException("LLM 返回空内容（JSON 模式偶发，可重新生成）", isTransient: true);

        Assert.True(LlmService.IsTransientError(ex));
        Assert.False(LlmService.IsConfigurationError(ex));
    }

    [Fact]
    public void ClientError_IsNeitherRetriedNorFatal()
    {
        // 4xx（例如某一段素材被服务端拒绝）：重试没有意义，但只该算该段失败
        var ex = new LlmCallException("LLM API 调用失败 (400): ...", isTransient: false);

        Assert.False(LlmService.IsTransientError(ex));
        Assert.False(LlmService.IsConfigurationError(ex));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    public void NetworkAndTimeoutFailures_AreTransient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(LlmService.IsTransientError(ex));
        Assert.False(LlmService.IsConfigurationError(ex));
    }

    [Fact]
    public void UnknownExceptions_AreNotTreatedAsTransient()
    {
        // 兜底策略：不认识的异常不重试——宁可少试一次，
        // 也不要为一个必然失败的原因反复打 API
        var ex = new InvalidOperationException("随便什么");

        Assert.False(LlmService.IsTransientError(ex));
        Assert.False(LlmService.IsConfigurationError(ex));
    }

    [Fact]
    public void LlmCallException_PreservesMessageAndInnerException()
    {
        var inner = new HttpRequestException("socket");

        var ex = new LlmCallException("包装后的消息", isTransient: true, inner: inner);

        Assert.Equal("包装后的消息", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
