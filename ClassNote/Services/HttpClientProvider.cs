using System.IO;
using System.Net.Http;

namespace ClassNote.Services;

/// <summary>
/// 进程级共享 <see cref="HttpClient"/>：仅用于 LLM API 调用等外部请求。
/// （单机模式：不再有本地服务端，此客户端用于 DeepSeek 等兼容 API。）
/// </summary>
public static class HttpClientProvider
{
    private static readonly HttpClient SharedClient = CreateClient();

    public static HttpClient Shared => SharedClient;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(300),
        };
    }
}
