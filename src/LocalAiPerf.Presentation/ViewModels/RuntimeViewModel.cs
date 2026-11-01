using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Presentation.Services;
using LocalAiPerf.Runtimes.Benchmark;
using LocalAiPerf.Recording;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>AI runtime (Strata) connection, identity, settings, live inference numbers, requests and errors.</summary>
public sealed partial class RuntimeViewModel(MonitoringService service, IUiDispatcher ui) : PageViewModel
{
    private const string Reported = "엔진 보고";
    private const string Derived = "계산값";

    public override string Title => "AI 런타임";
    public override string Glyph => "";

    [ObservableProperty] public partial string Endpoint { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string StateText { get; set; } = "-";
    [ObservableProperty] public partial StatusLevel StateLevel { get; set; }
    [ObservableProperty] public partial string ErrorCategory { get; set; } = "";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial string ErrorHint { get; set; } = "";
    [ObservableProperty] public partial string LastSuccess { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string NextRetry { get; set; } = "";
    [ObservableProperty] public partial string KeyStatus { get; set; } = "";
    [ObservableProperty] public partial ChartModel? ThroughputChart { get; set; }
    [ObservableProperty] public partial string BenchmarkTarget { get; set; } = "";
    [ObservableProperty] public partial string BenchmarkKeyStatus { get; set; } = "";
    [ObservableProperty] public partial string BenchmarkStatus { get; set; } = "";
    [ObservableProperty] public partial string BenchmarkHint { get; set; } = "";
    [ObservableProperty] public partial string BenchmarkAuto { get; set; } = "";
    [ObservableProperty] public partial ChartModel? BenchmarkChart { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarkCommand))]
    public partial bool IsBenchmarking { get; set; }

    public ObservableCollection<BenchmarkRow> Benchmarks { get; } = [];

    public ObservableCollection<ValueRow> Server { get; } = [];
    public ObservableCollection<ValueRow> Model { get; } = [];
    public ObservableCollection<ValueRow> Settings { get; } = [];
    public ObservableCollection<ValueRow> Live { get; } = [];
    public ObservableCollection<SlotRow> Slots { get; } = [];
    public ObservableCollection<RequestDisplayRow> Recent { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];

    public override Task OnActivatedAsync()
    {
        if (!_subscribed)
        {
            // Automatic runs finish on a background thread; the table reloads on the next frame.
            service.BenchmarkCompleted += _ => Interlocked.Increment(ref _benchmarkVersion);
            _subscribed = true;
        }

        Update();
        UpdateBenchmarkInfo();
        LoadBenchmarks();
        return Task.CompletedTask;
    }

    private int _benchmarkVersion;
    private int _shownBenchmarkVersion = -1;
    private bool _subscribed;

    private bool CanRunBenchmark() => !IsBenchmarking;

    /// <summary>Sends the fixed test prompt (only when the user presses the button) and shows the timings.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBenchmark))]
    private async Task RunBenchmarkAsync()
    {
        IsBenchmarking = true;
        BenchmarkHint = "";
        BenchmarkStatus = "측정 준비 중";
        try
        {
            var progress = new UiProgress(text => ui.Post(() => BenchmarkStatus = text));
            var batch = await service.RunBenchmarkAsync(null, progress).ConfigureAwait(false);
            ui.Post(() => ShowBatch(batch));
        }
        finally
        {
            ui.Post(() => IsBenchmarking = false);
        }
    }

    private void ShowBatch(BenchmarkBatch batch)
    {
        if (batch.Error is { } error)
        {
            BenchmarkStatus = $"측정하지 못했습니다 ({RuntimeStateText.ToKorean(error.Category)}): {error.Message}";
            BenchmarkHint = error.Hint ?? "";
        }
        else
        {
            var ok = batch.Runs.Count(r => r.Success);
            var failed = batch.Runs.FirstOrDefault(r => !r.Success);
            BenchmarkStatus = $"측정 {batch.Runs.Count}회 완료 (성공 {ok}회)" + (batch.Stored ? "" : " · 저장되지 않음 (데이터베이스 사용 불가)");
            BenchmarkHint = failed?.Error ?? "";
            if (failed?.ErrorCategory is RuntimeErrorCategory.Authentication)
            {
                BenchmarkHint += $" Strata 앱 'Strata를 앱에 연결' 화면의 API 키를 환경 변수 {KeyVariable()}에 넣고 이 프로그램을 다시 시작하세요.";
            }
        }

        UpdateBenchmarkInfo();
        LoadBenchmarks();
    }

    private string KeyVariable() => service.Settings.Benchmark.ApiKeyEnvironmentVariable ?? Core.Settings.BenchmarkSettings.DefaultKeyVariable;

    private void UpdateBenchmarkInfo()
    {
        var b = service.Settings.Benchmark;
        BenchmarkTarget = $"{b.BaseUrl}/v1/chat/completions · 모델 {(string.IsNullOrWhiteSpace(b.Model) ? "자동 (/v1/models 첫 번째)" : b.Model)} · 최대 {b.MaxTokens} 토큰 · {b.Repeat}회";
        var variable = b.ApiKeyEnvironmentVariable;
        BenchmarkAuto = b.AutoIntervalMinutes <= 0
            ? "자동 측정: 꺼짐 (설정에서 켤 수 있음)"
            : $"자동 측정: {b.AutoIntervalMinutes}분마다 1회, GPU 사용률 {b.AutoSkipGpuUtilPercent}% 이상이면 미룸"
              + (service.NextAutoBenchmarkAt is DateTimeOffset next ? $" · 다음 예정 {Fmt.Time(next)}" : "")
              + (service.AutoBenchmarkDeferral is string deferral ? $" · {deferral}" : "");
        BenchmarkKeyStatus = string.IsNullOrWhiteSpace(variable)
            ? "API 키 환경 변수: 사용 안 함"
            : $"API 키 환경 변수 {variable}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)) ? "값 없음" : "값 있음 (내용은 표시하지 않음)")}";
    }

    private void LoadBenchmarks()
    {
        _shownBenchmarkVersion = Volatile.Read(ref _benchmarkVersion);
        var runs = service.RecentBenchmarks(200);
        Benchmarks.Clear();
        foreach (var r in runs.Take(30))
        {
            Benchmarks.Add(ToRow(r));
        }

        BenchmarkChart = ChartBuilder.BenchmarkTrend(runs);
    }

    public static BenchmarkRow ToRow(BenchmarkResult r) => new(
        Fmt.DateTime(r.StartedAt),
        r.Model ?? Fmt.NA,
        r.Success ? "성공" : $"실패: {(r.ErrorCategory is { } c ? RuntimeStateText.ToKorean(c) : "")} {r.Error}".TrimEnd(),
        Fmt.Num(r.FirstTokenMs, "0", "ms"),
        Fmt.Num(r.ClientDecodeTokPerSec, "0.0", "tok/s"),
        Fmt.Num(r.EngineDecodeTokPerSec, "0.0", "tok/s"),
        Fmt.Num(r.EnginePromptTokPerSec, "0.0", "tok/s"),
        $"{Fmt.Int(r.PromptTokens)} / {Fmt.Int(r.CompletionTokens)}" + (r.CachedTokens is long cached && cached > 0 ? $" (캐시 {cached})" : ""),
        Fmt.Num(r.TotalMs is double ms ? ms / 1000 : null, "0.00", "s"),
        !r.Success);

    private sealed class UiProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    public override void OnFrame(TelemetryFrame frame)
    {
        Update();
        UpdateBenchmarkInfo();
        if (_shownBenchmarkVersion != Volatile.Read(ref _benchmarkVersion))
        {
            LoadBenchmarks();
        }

        var frames = DashboardViewModel.Window(service.Live.Snapshot(), frame.Timestamp, TimeSpan.FromMinutes(Math.Clamp(service.Settings.Ui.LiveChartMinutes, 1, 60)));
        ThroughputChart = ChartBuilder.Throughput(frames, service.Interval);
    }

    [RelayCommand]
    private void Reconnect()
    {
        service.ReconnectRuntime();
        NextRetry = "다음 조회 때 바로 다시 연결합니다.";
    }

    private void Update()
    {
        var adapter = service.Runtime;
        var keyVariable = service.Settings.Strata.ApiKeyEnvironmentVariable;
        KeyStatus = string.IsNullOrWhiteSpace(keyVariable)
            ? "API 키 환경 변수: 사용 안 함"
            : $"API 키 환경 변수 {keyVariable}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(keyVariable)) ? "값 없음" : "값 있음 (내용은 표시하지 않음)")}";
        if (adapter is null)
        {
            Endpoint = Fmt.NA;
            StateText = "꺼짐 - 설정에서 Strata 연결을 켤 수 있습니다";
            StateLevel = StatusLevel.Neutral;
            return;
        }

        Endpoint = adapter.EndpointDisplay;
        var rt = service.LatestRuntime;
        if (rt is null)
        {
            StateText = "연결 중";
            StateLevel = StatusLevel.Neutral;
            return;
        }

        StateText = RuntimeStateText.ToKorean(rt.State);
        StateLevel = rt.State switch
        {
            RuntimeConnectionState.Connected => StatusLevel.Good,
            RuntimeConnectionState.Limited or RuntimeConnectionState.Timeout or RuntimeConnectionState.AuthRequired => StatusLevel.Warning,
            RuntimeConnectionState.Unreachable => StatusLevel.Neutral,
            _ => StatusLevel.Critical,
        };
        ErrorCategory = rt.Error is { } e ? RuntimeStateText.ToKorean(e.Category) : "";
        ErrorMessage = rt.Error?.Message ?? "";
        ErrorHint = rt.Error?.Hint ?? "";
        LastSuccess = Fmt.DateTime(rt.LastSuccess);
        NextRetry = rt.NextRetryAt is DateTimeOffset next ? $"다음 재시도: {Fmt.Time(next)}" : "";

        Server.Clear();
        Server.Add(new ValueRow("서비스", rt.Server?.Service ?? Fmt.NA, Reported));
        Server.Add(new ValueRow("엔진 버전", rt.Server?.EngineVersion ?? Fmt.NA, Reported, rt.Server?.EngineVersion is null ? rt.Server?.EngineVersionNote ?? "" : ""));
        Server.Add(new ValueRow("텔레메트리 출처", rt.Server?.TelemetrySource switch
        {
            "server-v1" => "/v1/telemetry (schema v1)",
            "legacy-adapter" => "구버전 /metrics + /v1/status",
            "status-only" => "/v1/status만 (실시간 값 없음)",
            null => Fmt.NA,
            var other => other,
        }));
        Server.Add(new ValueRow("서버 인스턴스", rt.Server?.InstanceId is string id ? id[..Math.Min(12, id.Length)] + "…" : Fmt.NA, Reported,
            rt.Server?.InstanceState is string s ? $"상태 {s}" : ""));
        Server.Add(new ValueRow("서버 시작", Fmt.Unix(rt.Server?.BootTimeUnix), Reported));
        Server.Add(new ValueRow("서버 API 키 설정", Fmt.Bool(rt.Server?.ApiKeyConfigured), Reported));

        Model.Clear();
        Model.Add(new ValueRow("모델", rt.Model?.Id ?? Fmt.NA, Reported, rt.IsReachable || rt.Model?.Id is null ? "" : "마지막으로 확인된 값"));
        Model.Add(new ValueRow("로드 상태", Fmt.Bool(rt.Model?.Loaded), Reported));
        Model.Add(new ValueRow("모델 파일", rt.Model?.ModelFileName ?? Fmt.NA, Reported, "파일 이름만 표시 (경로 제외)"));
        Model.Add(new ValueRow("컨텍스트 한도", Fmt.Int(rt.Model?.ContextTokens, "tokens"), Reported));
        Model.Add(new ValueRow("병렬 슬롯", Fmt.Int(rt.Work?.SlotsEffective), Reported,
            rt.Work?.SlotsRequested is int requested ? $"요청값 {requested}" : ""));
        Model.Add(new ValueRow("비전 입력", Fmt.Bool(rt.Model?.Vision), Reported));
        Model.Add(new ValueRow("별칭", rt.Model?.Aliases.Count > 0 ? string.Join(", ", rt.Model.Aliases) : Fmt.NA, Reported));

        Settings.Clear();
        foreach (var setting in rt.Settings)
        {
            Settings.Add(new ValueRow(setting.Key, setting.Value, Reported, "/props 기본 생성 설정"));
        }

        Live.Clear();
        var reachable = rt.IsReachable;
        Live.Add(new ValueRow("답변 생성 속도", reachable ? Fmt.Num(rt.Rates?.AnswerTokPerSec, "0.0", "tok/s") : Fmt.NA, Reported,
            rt.Rates?.AnswerWindowSeconds is double window ? $"최근 {window:0.#}초 구간" : "생성 중일 때만 값이 있음"));
        Live.Add(new ValueRow("프롬프트 처리 속도", reachable ? Fmt.Num(rt.Rates?.PrefillTokPerSec, "0.0", "tok/s") : Fmt.NA, Reported));
        Live.Add(new ValueRow("전체 출력 처리량", reachable ? Fmt.Num(rt.Rates?.AggregateOutputTokPerSec, "0.0", "tok/s") : Fmt.NA, Derived,
            "출력 토큰 카운터 증가분 ÷ 조회 간격"));
        Live.Add(new ValueRow("처리 중 / 대기 / 대기열", reachable && rt.Work is { } w
            ? $"{Fmt.Int(w.Active)} / {Fmt.Int(w.Waiting)} / {Fmt.Int(w.Queued)}" : Fmt.NA, Reported));
        Live.Add(new ValueRow("가장 오래 기다린 요청", reachable ? Fmt.Num(rt.Work?.OldestQueueSeconds, "0.0", "s") : Fmt.NA, Reported));
        Live.Add(new ValueRow("입장 거부 / 대기 시간 초과 / 연결 끊김", reachable && rt.Work is { } c
            ? $"{Fmt.Int(c.AdmissionRejected)} / {Fmt.Int(c.QueueTimeouts)} / {Fmt.Int(c.ClientDisconnectCancels)}" : Fmt.NA, Reported, "서버 시작 이후 누계"));
        Live.Add(new ValueRow("누적 요청 / 출력 토큰", reachable && rt.Totals is { } t
            ? $"{Fmt.Int(t.Requests)} / {Fmt.Int(t.OutputTokens)}" : Fmt.NA, Reported, "서버 시작 이후"));

        Slots.Clear();
        if (reachable)
        {
            foreach (var slot in rt.Slots)
            {
                var fill = slot.HeldTokens is long held && rt.Model?.ContextTokens is long ctx && ctx > 0 ? Fmt.Percent(100.0 * held / ctx) : Fmt.NA;
                Slots.Add(new SlotRow(slot.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), slot.State ?? Fmt.NA,
                    Fmt.Int(slot.HeldTokens), fill, Fmt.Num(slot.TokPerSec, "0.0")));
            }
        }

        Recent.Clear();
        foreach (var r in rt.RecentRequests.Take(20))
        {
            Recent.Add(new RequestDisplayRow(Fmt.Unix(r.StartedUnix), Fmt.Num(r.DurationSeconds, "0.0", "s"), r.Finish ?? Fmt.NA,
                Fmt.Int(r.PromptTokens), Fmt.Int(r.ReusedTokens), Fmt.Int(r.OutputTokens), Fmt.Num(r.PromptMs, "0"), Fmt.Num(r.DecodeMs, "0"),
                Fmt.Num(r.DecodeTokPerSec, "0.0"), Fmt.Num(r.FirstEngineTokenSeconds, "0.00", "s"), r.IsError));
        }

        Activity.Clear();
        foreach (var a in rt.Activity.Reverse().Take(32))
        {
            Activity.Add(new ActivityRow(Fmt.Unix(a.AtUnix), a.RequestId, a.Event, a.Outcome ?? ""));
        }
    }
}
