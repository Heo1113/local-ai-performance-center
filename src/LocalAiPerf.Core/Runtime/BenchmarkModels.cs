namespace LocalAiPerf.Core.Runtime;

/// <summary>
/// One opt-in inference measurement: a fixed test prompt sent through a runtime's public chat API.
/// The response text is read only to time it and is never stored.
/// </summary>
public sealed record BenchmarkResult
{
    public required DateTimeOffset StartedAt { get; init; }
    public required string Endpoint { get; init; }
    public string? Model { get; init; }
    public required string PromptId { get; init; }
    public int MaxTokens { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public RuntimeErrorCategory? ErrorCategory { get; init; }

    /// <summary>Measured by this app: request sent to first generated token received (includes HTTP, queue and prefill).</summary>
    public double? FirstTokenMs { get; init; }

    /// <summary>Measured by this app: request sent to end of stream.</summary>
    public double? TotalMs { get; init; }

    /// <summary>Reported by the runtime (usage).</summary>
    public long? PromptTokens { get; init; }
    public long? CompletionTokens { get; init; }
    public long? CachedTokens { get; init; }

    /// <summary>Derived: (completion tokens - 1) / time between first and last token as received by this app.</summary>
    public double? ClientDecodeTokPerSec { get; init; }

    /// <summary>Reported by the engine (timings.predicted_per_second / prompt_per_second).</summary>
    public double? EngineDecodeTokPerSec { get; init; }
    public double? EnginePromptTokPerSec { get; init; }
}

/// <summary>Result of one press of "measure": the runs that were sent, or why none could be sent.</summary>
public sealed record BenchmarkBatch(IReadOnlyList<BenchmarkResult> Runs, RuntimeError? Error, bool Stored)
{
    public BenchmarkResult? LastSuccess => Runs.LastOrDefault(r => r.Success);
}
