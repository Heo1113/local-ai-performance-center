using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Runtimes.Strata;

namespace LocalAiPerf.Runtimes.Benchmark;

/// <summary>
/// Opt-in inference measurement through an OpenAI-compatible chat API (for example the Strata desktop app's public API).
/// Sends one fixed test prompt per run, streams the answer to time it and discards the text. Nothing else is sent.
/// The prompt and settings match Strata's own desktop benchmark (greedy, thinking off, up to 200 output tokens).
/// </summary>
public sealed class ChatBenchmarkClient : IDisposable
{
    public const string PromptId = "lapc-rainbow-v1";
    public const string Prompt = "Explain how a rainbow forms in clear, simple language.";
    private const int MaxStreamBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly BenchmarkSettings _settings;
    private readonly string? _key;

    public ChatBenchmarkClient(BenchmarkSettings settings, string? key, HttpMessageHandler? handler = null)
    {
        if (!EndpointValidator.TryNormalize(settings.BaseUrl, out var root, out var error))
        {
            throw new ArgumentException(error, nameof(settings));
        }

        _settings = settings;
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        Endpoint = root;
        var ownsHandler = handler is null;
        var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = !EndpointValidator.IsLoopback(new Uri(root)),
            ConnectCallback = (context, ct) => StrataHttpClient.ConnectAsync(context.DnsEndPoint, TimeSpan.FromSeconds(3), ct),
        };
        _http = new HttpClient(handler, ownsHandler) { BaseAddress = new Uri(root), Timeout = timeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalAiPerformanceCenter", AppInfo.Version));
    }

    public string Endpoint { get; }
    public bool HasKey => _key is not null;

    public static ChatBenchmarkClient FromSettings(BenchmarkSettings settings)
        => new(settings, string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable) ? null : Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable));

    /// <summary>The configured model, or the first model the API lists.</summary>
    public async Task<(string? Model, RuntimeError? Error)> ResolveModelAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.Model))
        {
            return (_settings.Model, null);
        }

        try
        {
            using var request = Authorized(new HttpRequestMessage(HttpMethod.Get, "/v1/models"));
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (Fail((int)response.StatusCode) is { } failure)
            {
                return (null, failure);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (JsonRead.Arr(doc.RootElement, "data") is JsonElement data)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (JsonRead.Str(item, "id") is string id)
                    {
                        return (id, null);
                    }
                }
            }

            return (null, new RuntimeError(RuntimeErrorCategory.Compatibility, "/v1/models에 모델이 없습니다. 앱에서 모델을 로드했는지 확인하거나 설정에 모델 ID를 넣으세요."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, Classify(ex, ct));
        }
    }

    public async Task<BenchmarkResult> RunOnceAsync(string model, DateTimeOffset startedAt, CancellationToken ct)
    {
        var result = new BenchmarkResult { StartedAt = startedAt, Endpoint = Endpoint, Model = model, PromptId = PromptId, MaxTokens = _settings.MaxTokens };
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = Prompt } },
            ["temperature"] = 0,
            ["reasoning_effort"] = "none",
            ["max_tokens"] = _settings.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, bool> { ["include_usage"] = true },
        });
        var clock = Stopwatch.StartNew();
        double? first = null, last = null;
        long? promptTokens = null, completionTokens = null, cached = null;
        double? engineDecode = null, enginePrompt = null;
        try
        {
            using var request = Authorized(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (Fail((int)response.StatusCode) is { } failure)
            {
                return result with { Error = failure.Message, ErrorCategory = failure.Category, TotalMs = clock.Elapsed.TotalMilliseconds };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            long read = 0;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
            {
                read += line.Length;
                if (read > MaxStreamBytes)
                {
                    return result with { Error = "응답이 4 MiB 제한을 넘었습니다.", ErrorCategory = RuntimeErrorCategory.Compatibility };
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload == "[DONE]")
                {
                    break;
                }

                using var chunk = JsonDocument.Parse(payload);
                var root = chunk.RootElement;
                if (JsonRead.Obj(root, "error") is JsonElement error)
                {
                    return result with { Error = JsonRead.Str(error, "message") ?? "서버가 오류를 보냈습니다.", ErrorCategory = RuntimeErrorCategory.Server };
                }

                if (JsonRead.Arr(root, "choices") is JsonElement choices && choices.GetArrayLength() > 0
                    && JsonRead.Obj(choices[0], "delta") is JsonElement delta
                    && (!string.IsNullOrEmpty(JsonRead.Str(delta, "content", 1)) || !string.IsNullOrEmpty(JsonRead.Str(delta, "reasoning_content", 1))))
                {
                    var now = clock.Elapsed.TotalMilliseconds;
                    first ??= now;
                    last = now;
                }

                if (JsonRead.Obj(root, "usage") is JsonElement usage)
                {
                    promptTokens = JsonRead.Long(usage, "prompt_tokens") ?? promptTokens;
                    completionTokens = JsonRead.Long(usage, "completion_tokens") ?? completionTokens;
                    cached = JsonRead.Obj(usage, "prompt_tokens_details") is JsonElement details ? JsonRead.Long(details, "cached_tokens") : cached;
                }

                if (JsonRead.Obj(root, "timings") is JsonElement timings)
                {
                    engineDecode = JsonRead.Num(timings, "predicted_per_second") ?? engineDecode;
                    enginePrompt = JsonRead.Num(timings, "prompt_per_second") ?? enginePrompt;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            var failure = Classify(ex, ct);
            return result with { Error = failure.Message, ErrorCategory = failure.Category, TotalMs = clock.Elapsed.TotalMilliseconds };
        }

        var total = clock.Elapsed.TotalMilliseconds;
        double? clientDecode = completionTokens is long n && n >= 2 && first is double f && last is double l && l > f ? (n - 1) / ((l - f) / 1000.0) : null;
        if (first is null)
        {
            return result with { Error = "생성된 토큰이 없습니다.", ErrorCategory = RuntimeErrorCategory.Compatibility, TotalMs = total };
        }

        return result with
        {
            Success = true,
            FirstTokenMs = first,
            TotalMs = total,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CachedTokens = cached,
            ClientDecodeTokPerSec = clientDecode,
            EngineDecodeTokPerSec = engineDecode,
            EnginePromptTokPerSec = enginePrompt,
        };
    }

    public void Dispose() => _http.Dispose();

    private HttpRequestMessage Authorized(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        }

        return request;
    }

    private RuntimeError? Fail(int status) => status switch
    {
        >= 200 and < 300 => null,
        401 or 403 => new RuntimeError(RuntimeErrorCategory.Authentication,
            _key is null ? "API 키가 필요합니다." : "API 키가 거부되었습니다.",
            $"Strata 앱의 Serving API 키를 환경 변수 {_settings.ApiKeyEnvironmentVariable ?? BenchmarkSettings.DefaultKeyVariable}에 넣고 앱을 다시 시작하세요.", null, status),
        404 => new RuntimeError(RuntimeErrorCategory.Compatibility, "Chat Completions API를 찾을 수 없습니다 (HTTP 404). 주소를 확인하세요.", null, null, status),
        503 => new RuntimeError(RuntimeErrorCategory.Server, "서버가 지금 요청을 받지 않습니다 (HTTP 503). 모델이 로드되어 있는지 확인하세요.", null, null, status),
        >= 500 => new RuntimeError(RuntimeErrorCategory.Server, $"서버 오류 (HTTP {status}).", null, null, status),
        _ => new RuntimeError(RuntimeErrorCategory.Compatibility, $"요청이 거부되었습니다 (HTTP {status}). 모델 ID를 확인하세요.", null, null, status),
    };

    private static RuntimeError Classify(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new RuntimeError(RuntimeErrorCategory.Timeout, "제한 시간 안에 측정이 끝나지 않았습니다.");
        }

        if (ex is JsonException)
        {
            return new RuntimeError(RuntimeErrorCategory.Compatibility, "스트리밍 응답 형식을 읽을 수 없습니다.");
        }

        var socket = ex;
        while (socket is not null and not SocketException)
        {
            socket = socket.InnerException;
        }

        return new RuntimeError(RuntimeErrorCategory.Connection,
            socket is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.TimedOut }
                ? "앱에 연결할 수 없습니다 (실행 중이 아니거나 주소가 다름)."
                : $"앱과 통신하지 못했습니다 ({ex.GetType().Name}).",
            "Strata 앱이 실행 중이고 '앱에 연결' 화면의 주소(기본 http://127.0.0.1:47831)와 같은지 확인하세요.");
    }
}
