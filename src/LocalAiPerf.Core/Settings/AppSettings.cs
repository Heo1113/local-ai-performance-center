using System.Text.RegularExpressions;

namespace LocalAiPerf.Core.Settings;

/// <summary>User settings. Contains no secrets: API keys are read from an environment variable named here.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SamplingSettings Sampling { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();
    public StrataSettings Strata { get; set; } = new();
    public BenchmarkSettings Benchmark { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public AnalysisSettings Analysis { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    public AppSettings Clone() => SettingsSerializer.Deserialize(SettingsSerializer.Serialize(this));

    /// <summary>Clamps every value into its valid range and replaces invalid text with defaults. Returns notes about fixes.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Sampling ??= new();
        Retention ??= new();
        Strata ??= new();
        Benchmark ??= new();
        Recording ??= new();
        Analysis ??= new();
        Ui ??= new();

        Sampling.IntervalMs = Clamp(Sampling.IntervalMs, SamplingSettings.MinIntervalMs, SamplingSettings.MaxIntervalMs, "기록 간격", notes);
        Retention.RetentionDays = Clamp(Retention.RetentionDays, 1, 3650, "보존 기간", notes);
        Retention.MaxDatabaseSizeMb = Clamp(Retention.MaxDatabaseSizeMb, 64, 102_400, "저장 공간 상한", notes);
        Strata.TimeoutMs = Clamp(Strata.TimeoutMs, 200, 30_000, "Strata 응답 제한 시간", notes);
        Strata.MetadataRefreshSeconds = Clamp(Strata.MetadataRefreshSeconds, 5, 3600, "Strata 정보 갱신 주기", notes);

        if (!EndpointValidator.TryNormalize(Strata.BaseUrl, out var normalized, out var error))
        {
            notes.Add($"Strata 주소가 올바르지 않아 기본값으로 되돌렸습니다: {error}");
            Strata.BaseUrl = StrataSettings.DefaultBaseUrl;
        }
        else
        {
            Strata.BaseUrl = normalized;
        }

        if (!string.IsNullOrWhiteSpace(Strata.ApiKeyEnvironmentVariable) && !IsValidEnvironmentVariableName(Strata.ApiKeyEnvironmentVariable))
        {
            notes.Add("API 키 환경 변수 이름이 올바르지 않아 비웠습니다.");
            Strata.ApiKeyEnvironmentVariable = null;
        }

        if (!EndpointValidator.TryNormalize(Benchmark.BaseUrl, out var benchUrl, out var benchError))
        {
            notes.Add($"추론 측정 주소가 올바르지 않아 기본값으로 되돌렸습니다: {benchError}");
            Benchmark.BaseUrl = BenchmarkSettings.DefaultBaseUrl;
        }
        else
        {
            Benchmark.BaseUrl = benchUrl;
        }

        if (!string.IsNullOrWhiteSpace(Benchmark.ApiKeyEnvironmentVariable) && !IsValidEnvironmentVariableName(Benchmark.ApiKeyEnvironmentVariable))
        {
            notes.Add("추론 측정 API 키 환경 변수 이름이 올바르지 않아 비웠습니다.");
            Benchmark.ApiKeyEnvironmentVariable = null;
        }

        Benchmark.Model = string.IsNullOrWhiteSpace(Benchmark.Model) ? null : Benchmark.Model.Trim();
        Benchmark.MaxTokens = Clamp(Benchmark.MaxTokens, 16, 2048, "추론 측정 최대 토큰", notes);
        Benchmark.Repeat = Clamp(Benchmark.Repeat, 1, 10, "추론 측정 반복 횟수", notes);
        Benchmark.TimeoutSeconds = Clamp(Benchmark.TimeoutSeconds, 10, 600, "추론 측정 제한 시간", notes);
        Benchmark.AutoIntervalMinutes = Benchmark.AutoIntervalMinutes <= 0 ? 0 : Clamp(Benchmark.AutoIntervalMinutes, 1, 1440, "자동 측정 간격", notes);
        Benchmark.AutoSkipGpuUtilPercent = Clamp(Benchmark.AutoSkipGpuUtilPercent, 1, 100, "자동 측정 건너뛰기 GPU 사용률", notes);
        Analysis.DegradationThresholdPercent = Clamp(Analysis.DegradationThresholdPercent, 5, 90, "성능 저하 임계값", notes);
        Analysis.MinDegradationSeconds = Clamp(Analysis.MinDegradationSeconds, 1, 3600, "성능 저하 최소 지속 시간", notes);
        Analysis.BaselineWindowSeconds = Clamp(Analysis.BaselineWindowSeconds, 10, 3600, "기준 구간 길이", notes);
        Analysis.HighGpuTempC = Clamp(Analysis.HighGpuTempC, 50, 110, "GPU 고온 기준", notes);
        Ui.LiveChartMinutes = Clamp(Ui.LiveChartMinutes, 1, 60, "실시간 그래프 길이", notes);
        Ui.SelectedGpuIndex = Clamp(Ui.SelectedGpuIndex, 0, 15, "표시 GPU", notes);
        SchemaVersion = CurrentSchemaVersion;
        return notes;
    }

    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    public static bool IsValidEnvironmentVariableName(string name)
        => EnvName.IsMatch(name);

    private static int Clamp(int value, int min, int max, string label, List<string> notes)
    {
        if (value < min || value > max)
        {
            var fixedValue = Math.Clamp(value, min, max);
            notes.Add($"{label} 값 {value}을(를) 허용 범위로 조정했습니다: {fixedValue}");
            return fixedValue;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max, string label, List<string> notes)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            var fixedValue = double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
            notes.Add($"{label} 값 {value}을(를) 허용 범위로 조정했습니다: {fixedValue}");
            return fixedValue;
        }

        return value;
    }
}

public sealed class SamplingSettings
{
    public const int MinIntervalMs = 500;
    public const int MaxIntervalMs = 60_000;

    /// <summary>How often hardware and runtime values are sampled and recorded.</summary>
    public int IntervalMs { get; set; } = 1000;
    public bool GpuEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; } = true;
}

public sealed class RetentionSettings
{
    /// <summary>Samples older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Upper bound for the database size. Oldest data is removed first when it is exceeded.</summary>
    public int MaxDatabaseSizeMb { get; set; } = 1024;
}

public sealed class StrataSettings
{
    public const string DefaultBaseUrl = "http://127.0.0.1:8080";
    public const string DefaultKeyVariable = "STRATA_TELEMETRY_KEY";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Name of the environment variable that holds the read-only telemetry key. The key itself is never stored.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; } = DefaultKeyVariable;
    public int TimeoutMs { get; set; } = 2000;
    public int MetadataRefreshSeconds { get; set; } = 30;
}

/// <summary>
/// Opt-in inference measurement through a public OpenAI-compatible chat API (for example the Strata desktop app's
/// http://127.0.0.1:47831/v1). It sends a fixed test prompt only when the user asks for it, or on the automatic
/// schedule the user turned on.
/// </summary>
public sealed class BenchmarkSettings
{
    public const string DefaultBaseUrl = "http://127.0.0.1:47831";
    public const string DefaultKeyVariable = "STRATA_APP_API_KEY";

    /// <summary>Server root; requests go to {root}/v1/chat/completions.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Model id; empty = the first model listed by /v1/models.</summary>
    public string? Model { get; set; }

    /// <summary>Environment variable holding the app's API key. The key itself is never stored.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; } = DefaultKeyVariable;
    public int MaxTokens { get; set; } = 200;
    public int Repeat { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Automatic measurement every N minutes (one run each). 0 = off (default): the app sends nothing on its own.</summary>
    public int AutoIntervalMinutes { get; set; }

    /// <summary>An automatic run is postponed while GPU utilization was at or above this value in the last seconds (someone is using the GPU).</summary>
    public int AutoSkipGpuUtilPercent { get; set; } = 30;
}

public sealed class RecordingSettings
{
    /// <summary>Start an automatic session when the app starts.</summary>
    public bool AutoRecordOnStartup { get; set; } = true;

    /// <summary>End the automatic session and start a new one when the engine version or model changes.</summary>
    public bool AutoSplitOnRuntimeChange { get; set; } = true;
}

public sealed class AnalysisSettings
{
    /// <summary>A drop of at least this percentage below the rolling baseline marks a degradation candidate.</summary>
    public double DegradationThresholdPercent { get; set; } = 20;

    /// <summary>The drop must last at least this long.</summary>
    public int MinDegradationSeconds { get; set; } = 10;

    /// <summary>Length of the rolling baseline window.</summary>
    public int BaselineWindowSeconds { get; set; } = 120;

    /// <summary>GPU temperature that is flagged as high in the analysis.</summary>
    public double HighGpuTempC { get; set; } = 83;
}

public sealed class UiSettings
{
    public int LiveChartMinutes { get; set; } = 5;
    public int SelectedGpuIndex { get; set; }
}
