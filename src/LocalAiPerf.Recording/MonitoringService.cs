using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Runtimes;
using LocalAiPerf.Runtimes.Benchmark;
using LocalAiPerf.Storage;
using LocalAiPerf.Telemetry;

namespace LocalAiPerf.Recording;

public sealed record MonitoringOptions
{
    /// <summary>Use synthetic sensors (tests, demos). The UI must show that the data is not real.</summary>
    public bool UseMockSensors { get; init; }

    /// <summary>Start without opening the database (live view only).</summary>
    public bool DisableStorage { get; init; }

    /// <summary>Overrides the auto-record setting for this run (null = use the setting).</summary>
    public bool? AutoRecord { get; init; }
}

/// <summary>
/// Wires sensors, the runtime adapter, the store and the recorder together. Used by the desktop app and the CLI.
/// Hardware monitoring keeps running when the runtime is not reachable or the database cannot be opened.
/// </summary>
public sealed class MonitoringService : IAsyncDisposable
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

    private readonly AppPaths _paths;
    private readonly MonitoringOptions _options;
    private readonly IAppLogger _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _benchmarkGate = new(1, 1);
    private readonly List<string> _notices = [];
    private readonly LiveBuffer _live;
    private HardwareMonitor? _hardware;
    private IAiRuntimeAdapter? _runtime;
    private TelemetryCollector? _collector;
    private SessionRecorder? _recorder;
    private TelemetryStore? _store;
    private CancellationTokenSource? _maintenanceCts;
    private Task? _maintenance;
    private Task? _autoBenchmark;
    private bool _started;

    public MonitoringService(AppPaths paths, AppSettings settings, MonitoringOptions? options = null, IAppLogger? log = null, TimeProvider? time = null)
    {
        _paths = paths;
        Settings = settings;
        _options = options ?? new MonitoringOptions();
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _live = new LiveBuffer(TimeSpan.FromMinutes(Math.Max(60, settings.Ui.LiveChartMinutes)));
    }

    public AppSettings Settings { get; private set; }
    public AppPaths Paths => _paths;
    public bool IsMock => _options.UseMockSensors;
    public HardwareMonitor? Hardware => _hardware;
    public IAiRuntimeAdapter? Runtime => _runtime;
    public TelemetryStore? Store => _store;
    public SessionRecorder? Recorder => _recorder;
    public LiveBuffer Live => _live;
    public string? StorageProblem { get; private set; }
    public RuntimeSnapshot? LatestRuntime => _collector?.LatestRuntime;
    public TimeSpan Interval => TimeSpan.FromMilliseconds(Settings.Sampling.IntervalMs);

    public IReadOnlyList<string> Notices
    {
        get
        {
            lock (_notices)
            {
                return [.. _notices];
            }
        }
    }

    /// <summary>Raised on a background thread for every collected frame.</summary>
    public event Action<TelemetryFrame>? FrameCollected;

    /// <summary>Raised when recording, storage or runtime configuration changes.</summary>
    public event Action? StateChanged;

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            _paths.EnsureDirectories();
            OpenStore();
            BuildPipeline();
            _started = true;
            _maintenanceCts = new CancellationTokenSource();
            _maintenance = Task.Run(() => MaintenanceLoopAsync(_maintenanceCts.Token));
            _autoBenchmark = Task.Run(() => AutoBenchmarkLoopAsync(_maintenanceCts.Token));
            if (_store is not null && (_options.AutoRecord ?? Settings.Recording.AutoRecordOnStartup))
            {
                await _recorder!.StartAsync(null, SessionKind.Auto).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
    }

    public async Task<SessionInfo?> StartRecordingAsync(string? name, string? notes = null, SessionKind kind = SessionKind.Manual)
    {
        if (_recorder is null)
        {
            return null;
        }

        var session = await _recorder.StartAsync(name, kind, notes).ConfigureAwait(false);
        RaiseStateChanged();
        return session;
    }

    public async Task StopRecordingAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        await _recorder.StopAsync(SessionEndReasons.UserStopped).ConfigureAwait(false);
        RaiseStateChanged();
    }

    public void ReconnectRuntime() => _runtime?.RequestReconnect();

    /// <summary>Applies new settings: the sampling pipeline is rebuilt; an active session continues if the interval is unchanged.</summary>
    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        newSettings.Normalize();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var intervalChanged = newSettings.Sampling.IntervalMs != Settings.Sampling.IntervalMs;
            var wasRecording = _recorder?.ActiveSession;
            if (wasRecording is not null && intervalChanged)
            {
                await _recorder!.StopAsync(SessionEndReasons.SettingsChanged).ConfigureAwait(false);
            }

            await TearDownPipelineAsync(keepRecorder: true).ConfigureAwait(false);
            Settings = newSettings;
            _live.Window = TimeSpan.FromMinutes(Math.Max(60, newSettings.Ui.LiveChartMinutes));
            BuildPipeline();
            if (wasRecording is not null && intervalChanged)
            {
                await _recorder!.StartAsync(wasRecording.Kind == SessionKind.Manual ? wasRecording.Name + " (설정 변경 후)" : null,
                    wasRecording.Kind).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
    }

    public bool IsBenchmarkRunning => _benchmarkGate.CurrentCount == 0;

    private BenchmarkResult? _lastBenchmark;
    private bool _lastBenchmarkLoaded;

    /// <summary>The newest measurement run (from this run of the app, or the newest stored one).</summary>
    public BenchmarkResult? LastBenchmark
    {
        get
        {
            if (!_lastBenchmarkLoaded && _store is not null)
            {
                _lastBenchmarkLoaded = true;
                _lastBenchmark ??= RecentBenchmarks(1).FirstOrDefault();
            }

            return _lastBenchmark;
        }
    }

    /// <summary>Raised after each measurement run (success or failure), on a background thread.</summary>
    public event Action<BenchmarkResult>? BenchmarkCompleted;

    /// <summary>
    /// Opt-in measurement: sends the fixed test prompt <paramref name="repeat"/> times through the configured chat API and
    /// stores the numbers (attached to the session being recorded, if any). Only runs when the user asks for it.
    /// </summary>
    public async Task<BenchmarkBatch> RunBenchmarkAsync(int? repeat = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _benchmarkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BenchmarkBatch([], new RuntimeError(RuntimeErrorCategory.Configuration, "이미 측정 중입니다."), false);
        }

        RaiseStateChanged();
        try
        {
            var settings = Settings.Benchmark;
            var count = Math.Clamp(repeat ?? settings.Repeat, 1, 10);
            using var client = ChatBenchmarkClient.FromSettings(settings);
            progress?.Report("모델 확인 중");
            var (model, error) = await client.ResolveModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                _log.Warn("benchmark", $"측정 요청을 보내지 못했습니다: {error?.Message}");
                return new BenchmarkBatch([], error, false);
            }

            var runs = new List<BenchmarkResult>();
            var stored = _store is not null;
            for (var i = 0; i < count; i++)
            {
                progress?.Report($"측정 {i + 1}/{count} 실행 중 ({model})");
                var run = await client.RunOnceAsync(model, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                runs.Add(run);
                stored &= StoreBenchmark(run);
                _lastBenchmark = run;
                BenchmarkCompleted?.Invoke(run);
                if (!run.Success && run.ErrorCategory is RuntimeErrorCategory.Connection or RuntimeErrorCategory.Authentication or RuntimeErrorCategory.Compatibility)
                {
                    break; // the next run would fail the same way
                }
            }

            var ok = runs.Count(r => r.Success);
            _log.Info("benchmark", $"측정 요청 {runs.Count}회 (성공 {ok}회), 모델 {model}");
            TryWriteEvent(new EventRecord(_recorder?.ActiveSession?.Id, _time.GetUtcNow(), ok == runs.Count ? EventSeverity.Info : EventSeverity.Warning,
                EventCategories.Benchmark, $"측정 요청 {runs.Count}회 실행 (성공 {ok}회), 모델 {model}, 최대 {settings.MaxTokens} 토큰"));
            return new BenchmarkBatch(runs, null, stored);
        }
        catch (ArgumentException ex)
        {
            return new BenchmarkBatch([], new RuntimeError(RuntimeErrorCategory.Configuration, ex.Message), false);
        }
        finally
        {
            _benchmarkGate.Release();
            RaiseStateChanged();
        }
    }

    /// <summary>Stored measurement runs: one session's, or the newest across all sessions.</summary>
    public IReadOnlyList<BenchmarkResult> RecentBenchmarks(int limit = 50)
    {
        try
        {
            return _store?.ListBenchmarks(null, limit) ?? [];
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _log.Warn("benchmark", $"측정 기록을 읽지 못했습니다: {ex.Message}");
            return [];
        }
    }

    private bool StoreBenchmark(BenchmarkResult run)
    {
        try
        {
            _store?.WriteBenchmark(_recorder?.ActiveSession?.Id, run);
            return _store is not null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or IOException)
        {
            _log.Error("benchmark", $"측정 결과를 저장하지 못했습니다: {ex.Message}");
            return false;
        }
    }

    private void TryWriteEvent(EventRecord record)
    {
        try
        {
            _store?.WriteEvent(record);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or IOException)
        {
            _log.Warn("benchmark", $"이벤트를 저장하지 못했습니다: {ex.Message}");
        }
    }

    public async Task<RetentionResult?> ApplyRetentionNowAsync()
    {
        var store = _store;
        if (store is null)
        {
            return null;
        }

        var activeId = _recorder?.ActiveSession?.Id;
        var result = await Task.Run(() => store.ApplyRetention(Settings.Retention.RetentionDays,
            (long)Settings.Retention.MaxDatabaseSizeMb * 1024 * 1024, activeId)).ConfigureAwait(false);
        RaiseStateChanged();
        return result;
    }

    public Task BackupAsync(string destination, bool overwrite)
    {
        var store = _store ?? throw new InvalidOperationException(StorageProblem ?? "데이터베이스가 열려 있지 않습니다.");
        return Task.Run(() => store.BackupTo(destination, overwrite));
    }

    /// <summary>
    /// Restores a validated backup. Recording stops, the database is replaced (the old file is kept aside) and the
    /// pipeline restarts. Returns the path of the preserved previous database.
    /// </summary>
    public async Task<string?> RestoreAsync(string backupPath)
    {
        var validation = TelemetryStore.ValidateBackup(backupPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message);
        }

        string? aside;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownPipelineAsync(keepRecorder: false).ConfigureAwait(false);
            _store?.Dispose();
            _store = null;
            aside = await Task.Run(() => TelemetryStore.RestoreFrom(backupPath, _paths.DatabasePath, _time)).ConfigureAwait(false);
            OpenStore();
            _store?.RecoverUnfinishedSessions(SessionEndReasons.RecoveredAfterCrash);
            BuildPipeline();
            if (_recorder is not null && (_options.AutoRecord ?? Settings.Recording.AutoRecordOnStartup))
            {
                await _recorder.StartAsync(null, SessionKind.Auto).ConfigureAwait(false);
            }

            AddNotice($"백업에서 복원했습니다 ({Path.GetFileName(backupPath)}). 이전 데이터베이스는 {Path.GetFileName(aside ?? "-")}(으)로 보관했습니다.");
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
        return aside;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            if (_maintenanceCts is not null)
            {
                await _maintenanceCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await (_maintenance ?? Task.CompletedTask).ConfigureAwait(false);
                    await (_autoBenchmark ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                _maintenanceCts.Dispose();
                _maintenanceCts = null;
            }

            await TearDownPipelineAsync(keepRecorder: false).ConfigureAwait(false);
            _store?.Dispose();
            _store = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
        _benchmarkGate.Dispose();
    }

    private void OpenStore()
    {
        if (_options.DisableStorage || _store is not null)
        {
            return;
        }

        try
        {
            _store = TelemetryStore.Open(_paths.DatabasePath, _time, _log);
            StorageProblem = null;
            if (_store.OpenNotice is string notice)
            {
                AddNotice(notice);
            }

            var recovered = _store.RecoverUnfinishedSessions();
            if (recovered > 0)
            {
                AddNotice($"이전 실행이 정상 종료되지 않았습니다. 열려 있던 세션 {recovered}개를 마지막 샘플 시각으로 닫았습니다 (기록된 데이터는 보존).");
            }
        }
        catch (Exception ex)
        {
            _store = null;
            StorageProblem = $"데이터베이스를 열 수 없어 기록하지 않습니다 (실시간 모니터링은 계속됩니다): {ex.Message}";
            AddNotice(StorageProblem);
            _log.Error("storage", StorageProblem, ex);
        }
    }

    private void BuildPipeline()
    {
        _hardware = HardwareMonitor.Create(Settings.Sampling, _options.UseMockSensors, _time, _log);
        _runtime = RuntimeAdapterFactory.Create(Settings, _time, _log);
        var recorderOptions = new RecorderOptions(Settings.Sampling.IntervalMs, Settings.Recording.AutoSplitOnRuntimeChange,
            _runtime?.Kind, _runtime?.EndpointDisplay, _options.UseMockSensors);
        if (_store is not null)
        {
            if (_recorder is null)
            {
                var hardware = _hardware;
                _recorder = new SessionRecorder(_store, recorderOptions, () => (_hardware ?? hardware).Info, _time, _log);
                _recorder.StatusChanged += RaiseStateChanged;
            }
            else
            {
                _recorder.UpdateOptions(recorderOptions);
            }
        }

        _collector = new TelemetryCollector(_hardware, _runtime, Interval, _time, _log);
        _collector.FrameCollected += OnFrame;
        _collector.Start();
        _log.Info("monitor", $"수집 시작: 간격 {Settings.Sampling.IntervalMs} ms, GPU 제공자 {_hardware.Gpu.Name}, 런타임 {_runtime?.EndpointDisplay ?? "꺼짐"}");
    }

    private async Task TearDownPipelineAsync(bool keepRecorder)
    {
        if (_collector is not null)
        {
            _collector.FrameCollected -= OnFrame;
            await _collector.StopAsync().ConfigureAwait(false);
            _collector = null;
        }

        if (!keepRecorder && _recorder is not null)
        {
            _recorder.StatusChanged -= RaiseStateChanged;
            await _recorder.DisposeAsync().ConfigureAwait(false);
            _recorder = null;
        }
        else if (_recorder is not null)
        {
            await _recorder.FlushAsync().ConfigureAwait(false);
        }

        _runtime?.Dispose();
        _runtime = null;
        _hardware?.Dispose();
        _hardware = null;
    }

    private void OnFrame(TelemetryFrame frame)
    {
        // Charts need rates, work counts and model facts only; request lists stay in the recorder's copy.
        _live.Add(frame.Runtime is null ? frame : frame with
        {
            Runtime = frame.Runtime with { RecentRequests = [], Activity = [], Settings = [] },
        });
        _recorder?.Enqueue(frame);
        try
        {
            FrameCollected?.Invoke(frame);
        }
        catch (Exception ex)
        {
            _log.Error("monitor", "프레임 구독자 예외", ex);
        }
    }

    private static readonly TimeSpan AutoBenchmarkTick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoBenchmarkFirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GpuBusyWindow = TimeSpan.FromSeconds(15);

    /// <summary>When the next automatic run is due (null while automatic measurement is off).</summary>
    public DateTimeOffset? NextAutoBenchmarkAt { get; private set; }

    /// <summary>Why the last due automatic run was postponed, if it was.</summary>
    public string? AutoBenchmarkDeferral { get; private set; }

    /// <summary>
    /// Automatic measurement (off by default). Sends one run every AutoIntervalMinutes; a due run waits while the GPU
    /// is busy so it does not slow down the user's own work.
    /// </summary>
    private async Task AutoBenchmarkLoopAsync(CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        DateTimeOffset? lastRun = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AutoBenchmarkTick, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var minutes = Settings.Benchmark.AutoIntervalMinutes;
            if (minutes <= 0)
            {
                NextAutoBenchmarkAt = null;
                AutoBenchmarkDeferral = null;
                continue;
            }

            var now = _time.GetUtcNow();
            var due = lastRun is DateTimeOffset last ? last + TimeSpan.FromMinutes(minutes) : startedAt + AutoBenchmarkFirstDelay;
            NextAutoBenchmarkAt = due;
            if (now < due || IsBenchmarkRunning)
            {
                continue;
            }

            if (GpuBusy(now) is double util)
            {
                AutoBenchmarkDeferral = $"GPU 사용 중({util:0}% ≥ {Settings.Benchmark.AutoSkipGpuUtilPercent}%)이라 자동 측정을 미뤘습니다.";
                RaiseStateChanged();
                continue;
            }

            AutoBenchmarkDeferral = null;
            lastRun = now;
            try
            {
                var batch = await RunBenchmarkAsync(1, null, ct).ConfigureAwait(false);
                if (batch.Error is { } error)
                {
                    _log.Warn("benchmark", $"자동 측정 실패: {error.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("benchmark", "자동 측정 중 오류", ex);
            }

            NextAutoBenchmarkAt = lastRun + TimeSpan.FromMinutes(Settings.Benchmark.AutoIntervalMinutes);
            RaiseStateChanged();
        }
    }

    /// <summary>The highest GPU utilization of the last seconds when it reaches the threshold, otherwise null.</summary>
    internal double? GpuBusy(DateTimeOffset now)
    {
        var threshold = Settings.Benchmark.AutoSkipGpuUtilPercent;
        var peak = _live.Snapshot()
            .Where(f => now - f.Timestamp <= GpuBusyWindow)
            .SelectMany(f => f.Hardware.Gpus)
            .Select(g => g.UtilizationPercent)
            .Where(u => u is not null)
            .DefaultIfEmpty(null)
            .Max();
        return peak is double p && p >= threshold ? p : null;
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ApplyRetentionNowAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error("storage", "보존 정책 적용 실패", ex);
            }

            try
            {
                await Task.Delay(RetentionInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void AddNotice(string notice)
    {
        lock (_notices)
        {
            _notices.Add(notice);
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("monitor", "상태 구독자 예외", ex);
        }
    }

    /// <summary>Static description of the machine for the current pipeline.</summary>
    public HardwareInfo? HardwareInfo => _hardware?.Info;
}
