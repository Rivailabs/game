using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraKingdoms.Meta.Server
{
    /// <summary>A minimal HTTP request (the verifiers need GET and POST with small bodies).</summary>
    public sealed class HttpCall
    {
        public string Method { get; }
        public string Url { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public string Body { get; }
        public string ContentType { get; }

        public HttpCall(string method, string url, IReadOnlyDictionary<string, string> headers = null, string body = null, string contentType = null)
        {
            Method = method;
            Url = url;
            Headers = headers ?? new Dictionary<string, string>();
            Body = body;
            ContentType = contentType;
        }
    }

    public sealed class HttpReply
    {
        public int Status { get; }
        public string Body { get; }

        public HttpReply(int status, string body)
        {
            Status = status;
            Body = body ?? string.Empty;
        }

        public bool IsSuccess => Status >= 200 && Status < 300;
        public bool IsTransient => Status == 0 || Status == 408 || Status == 429 || Status >= 500;
    }

    /// <summary>
    /// The network boundary for server-side verification. Tests use a scripted fake; production uses
    /// <see cref="SystemNetHttpTransport"/>. A connection failure is reported as status 0.
    /// </summary>
    public interface IHttpTransport
    {
        Task<HttpReply> SendAsync(HttpCall call, CancellationToken ct = default);
    }

    /// <summary>Production transport over one shared <see cref="HttpClient"/>.</summary>
    public sealed class SystemNetHttpTransport : IHttpTransport
    {
        private readonly HttpClient _client;

        public SystemNetHttpTransport(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        public async Task<HttpReply> SendAsync(HttpCall call, CancellationToken ct = default)
        {
            using (var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url))
            {
                foreach (KeyValuePair<string, string> h in call.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (call.Body != null) request.Content = new StringContent(call.Body, Encoding.UTF8, call.ContentType ?? "application/json");
                try
                {
                    using (HttpResponseMessage response = await _client.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        string body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return new HttpReply((int)response.StatusCode, body);
                    }
                }
                catch (HttpRequestException ex)
                {
                    return new HttpReply(0, ex.Message);
                }
            }
        }
    }
}
