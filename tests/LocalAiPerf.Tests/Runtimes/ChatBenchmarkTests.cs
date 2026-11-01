using System.Text.Json;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.MockStrata;
using LocalAiPerf.Recording;
using LocalAiPerf.Runtimes.Benchmark;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Runtimes;

public class ChatBenchmarkTests
{
    private static BenchmarkSettings Settings(string url, string? model = null) => new() { BaseUrl = url, Model = model, MaxTokens = 64, TimeoutSeconds = 10 };

    private static BenchmarkResult Run(DateTimeOffset at, bool ok = true, string model = "m", double decode = 100, int maxTokens = 200) => new()
    {
        StartedAt = at,
        Endpoint = "http://127.0.0.1:47831",
        Model = model,
        PromptId = ChatBenchmarkClient.PromptId,
        MaxTokens = maxTokens,
        Success = ok,
        Error = ok ? null : "failed",
        ErrorCategory = ok ? null : RuntimeErrorCategory.Connection,
        FirstTokenMs = ok ? 80 : null,
        TotalMs = 2000,
        PromptTokens = ok ? 21 : null,
        CompletionTokens = ok ? 200 : null,
        ClientDecodeTokPerSec = ok ? decode : null,
        EngineDecodeTokPerSec = ok ? decode + 2 : null,
        EnginePromptTokPerSec = ok ? 1500 : null,
    };

    [Fact]
    public async Task Measures_a_streamed_answer_and_sends_only_the_fixed_prompt()
    {
        await using var server = new MockStrataServer { RequiredKey = "app-key" };
        server.Start();
        using var client = new ChatBenchmarkClient(Settings(server.BaseUrl), "app-key");

        var (model, error) = await client.ResolveModelAsync(TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Equal(server.ModelId, model); // first entry of /v1/models

        var run = await client.RunOnceAsync(model!, T0, TestContext.Current.CancellationToken);

        Assert.True(run.Success, run.Error);
        Assert.Equal(server.ModelId, run.Model);
        Assert.Equal(server.ChatTokens, run.CompletionTokens);
        Assert.Equal(21, run.PromptTokens);
        Assert.Equal(120.0, run.EngineDecodeTokPerSec);
        Assert.Equal(1680.0, run.EnginePromptTokPerSec);
        Assert.True(run.FirstTokenMs > 0);
        Assert.True(run.TotalMs >= run.FirstTokenMs);
        Assert.NotNull(run.ClientDecodeTokPerSec);
        Assert.True(run.ClientDecodeTokPerSec > 0);

        using var body = JsonDocument.Parse(server.LastChatBody!);
        var root = body.RootElement;
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal(ChatBenchmarkClient.Prompt, messages[0].GetProperty("content").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(64, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());

        // Only the model list and the chat endpoint are used; nothing else is touched.
        Assert.All(server.Requests, r => Assert.Contains(r.Path, new[] { "/v1/models", "/v1/chat/completions" }));
    }

    [Fact]
    public async Task Missing_and_wrong_keys_are_authentication_errors_with_a_hint()
    {
        await using var server = new MockStrataServer { RequiredKey = "app-key" };
        server.Start();

        using var noKey = new ChatBenchmarkClient(Settings(server.BaseUrl, "m"), null);
        var run = await noKey.RunOnceAsync("m", T0, TestContext.Current.CancellationToken);
        Assert.False(run.Success);
        Assert.Equal(RuntimeErrorCategory.Authentication, run.ErrorCategory);

        using var wrongKey = new ChatBenchmarkClient(Settings(server.BaseUrl), "other");
        var (model, error) = await wrongKey.ResolveModelAsync(TestContext.Current.CancellationToken);
        Assert.Null(model);
        Assert.Equal(RuntimeErrorCategory.Authentication, error!.Category);
        Assert.Contains(BenchmarkSettings.DefaultKeyVariable, error.Hint);
        Assert.DoesNotContain("other", error.Message + error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopped_app_is_a_connection_error_and_no_model_is_a_server_error()
    {
        using var stopped = new ChatBenchmarkClient(Settings("http://127.0.0.1:9", "m"), null);
        var run = await stopped.RunOnceAsync("m", T0, TestContext.Current.CancellationToken);
        Assert.False(run.Success);
        Assert.Equal(RuntimeErrorCategory.Connection, run.ErrorCategory);

        await using var server = new MockStrataServer(mode: MockMode.ServerError);
        server.Start();
        using var client = new ChatBenchmarkClient(Settings(server.BaseUrl, "m"), null);
        var failed = await client.RunOnceAsync("m", T0, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeErrorCategory.Server, failed.ErrorCategory);
    }

    [Fact]
    public void Settings_keep_automatic_measurement_off_unless_asked()
    {
        var defaults = new AppSettings();
        defaults.Normalize();
        Assert.Equal(0, defaults.Benchmark.AutoIntervalMinutes);
        Assert.Equal("http://127.0.0.1:47831", defaults.Benchmark.BaseUrl);

        var s = new AppSettings();
        s.Benchmark.AutoIntervalMinutes = -5;
        s.Benchmark.MaxTokens = 1;
        s.Benchmark.BaseUrl = "http://192.168.0.10:47831"; // plain http to another machine is refused
        s.Normalize();
        Assert.Equal(0, s.Benchmark.AutoIntervalMinutes);
        Assert.Equal(16, s.Benchmark.MaxTokens);
        Assert.Equal(BenchmarkSettings.DefaultBaseUrl, s.Benchmark.BaseUrl);

        s.Benchmark.AutoIntervalMinutes = 100_000;
        s.Normalize();
        Assert.Equal(1440, s.Benchmark.AutoIntervalMinutes);
    }

    [Fact]
    public void Runs_are_stored_with_the_session_and_an_old_database_is_migrated()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        long sessionId;
        using (var store = TelemetryStore.Open(path))
        {
            sessionId = store.StartSession(Start(T0)).Id;
        }

        // Make it look like a version-1 database (before measurement runs existed).
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE benchmark_runs; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        using (var store = TelemetryStore.Open(path))
        {
            Assert.NotNull(store.GetSession(sessionId)); // existing data kept
            store.WriteBenchmark(sessionId, Run(T0.AddMinutes(1)));
            store.WriteBenchmark(sessionId, Run(T0.AddMinutes(2), ok: false));
            store.WriteBenchmark(null, Run(T0.AddMinutes(3), decode: double.NaN));

            var data = store.LoadSessionData(sessionId)!;
            Assert.Equal(2, data.BenchmarkRuns.Count);
            Assert.True(data.BenchmarkRuns[0].Success);
            Assert.Equal(100, data.BenchmarkRuns[0].ClientDecodeTokPerSec);
            Assert.False(data.BenchmarkRuns[1].Success);
            Assert.Equal(RuntimeErrorCategory.Connection, data.BenchmarkRuns[1].ErrorCategory);

            var all = store.ListBenchmarks(null);
            Assert.Equal(3, all.Count);
            Assert.Equal(T0.AddMinutes(3), all[0].StartedAt); // newest first
            Assert.Null(all[0].ClientDecodeTokPerSec); // NaN is not stored as a number
        }

        Assert.Equal(2, TelemetryStore.ValidateBackup(path).SchemaVersion);
    }

    [Fact]
    public void Only_successful_runs_become_metric_points_and_different_setups_are_flagged()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var a = store.StartSession(Start(T0, "before")).Id;
        store.WriteBenchmark(a, Run(T0.AddMinutes(1), decode: 100));
        store.WriteBenchmark(a, Run(T0.AddMinutes(2), ok: false));
        store.WriteBenchmark(a, Run(T0.AddMinutes(3), decode: 110));
        var b = store.StartSession(Start(T0.AddHours(1), "after")).Id;
        store.WriteBenchmark(b, Run(T0.AddHours(1).AddMinutes(1), decode: 90, maxTokens: 400));

        var series = SeriesExtractor.Extract(store.LoadSessionData(a)!);
        Assert.Equal([100.0, 110.0], series[MetricKeys.BenchDecodeTokS].Points.Select(p => p.Value));
        Assert.Equal(2, series[MetricKeys.BenchFirstTokenMs].Points.Count);

        var reportA = SessionAnalyzer.Analyze(store.LoadSessionData(a)!, DetectionOptions.Default);
        var reportB = SessionAnalyzer.Analyze(store.LoadSessionData(b)!, DetectionOptions.Default);
        Assert.Equal(105, reportA.Get(MetricKeys.BenchDecodeTokS)!.Stats.Mean);
        var comparison = SessionComparer.Compare([reportA, reportB]);
        Assert.Contains(comparison.Rows, r => r.Metric.Key == MetricKeys.BenchDecodeTokS);
        Assert.Contains(comparison.Warnings, w => w.Kind == "benchmark-different");
    }

    [Fact]
    public async Task Service_runs_on_request_stores_results_in_the_active_session_and_never_runs_twice_at_once()
    {
        using var dir = new TempDir();
        await using var server = new MockStrataServer { ChatTokenDelay = TimeSpan.FromMilliseconds(30) };
        server.Start();
        var settings = new AppSettings();
        settings.Sampling.IntervalMs = 500;
        settings.Strata.Enabled = false;
        settings.Benchmark.BaseUrl = server.BaseUrl;
        settings.Benchmark.ApiKeyEnvironmentVariable = null;
        settings.Benchmark.Repeat = 2;
        settings.Normalize();

        await using var service = new MonitoringService(new AppPaths(dir.Path, false), settings, new MonitoringOptions { UseMockSensors = true });
        await service.StartAsync();
        var sessionId = service.Recorder!.ActiveSession!.Id;
        Assert.Null(service.LastBenchmark);

        var first = service.RunBenchmarkAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var second = await service.RunBenchmarkAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(second.Runs);
        Assert.NotNull(second.Error);

        var batch = await first;
        Assert.Null(batch.Error);
        Assert.Equal(2, batch.Runs.Count);
        Assert.All(batch.Runs, r => Assert.True(r.Success, r.Error));
        Assert.True(batch.Stored);
        Assert.NotNull(service.LastBenchmark);
        Assert.Equal(2, service.Store!.LoadSessionData(sessionId)!.BenchmarkRuns.Count);
        Assert.Contains(service.Store.ListEvents(sessionId), e => e.Category == EventCategories.Benchmark);
    }
}
