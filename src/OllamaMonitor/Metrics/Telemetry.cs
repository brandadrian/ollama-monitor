using System.Diagnostics.Metrics;
using OllamaMonitor.Monitoring;

namespace OllamaMonitor.Metrics;

public sealed record LoadedModel(string Name, long SizeBytes, long VramBytes, long? ContextLength);

/// <summary>Letzter Stand der periodisch gesammelten Werte (wird von den Observable Gauges gelesen).</summary>
public sealed record Snapshot
{
    public double? CpuPercent { get; init; }
    public long? MemoryAvailableBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public long? VramDedicatedBytes { get; init; }
    public long? VramSharedBytes { get; init; }
    public IReadOnlyDictionary<string, double> GpuEngines { get; init; } = new Dictionary<string, double>();
    public bool OllamaUp { get; init; }
    public IReadOnlyList<LoadedModel> Models { get; init; } = [];
}

/// <summary>
/// Alle Metriken. Namen sind bewusst schon im Prometheus-Stil (snake_case, Einheit im Namen),
/// damit sie in Grafana Cloud vorhersehbar ankommen. Counter bekommen dort automatisch "_total".
/// </summary>
public sealed class Telemetry : IDisposable
{
    public const string MeterName = "OllamaMonitor";

    private readonly Meter _meter = new(MeterName);

    public volatile Snapshot Current = new();

    public Counter<long> Requests { get; }
    public Counter<long> PromptTokens { get; }
    public Counter<long> OutputTokens { get; }
    public Counter<long> TruncatedPrompts { get; }
    public Counter<long> Evictions { get; }
    public Counter<long> LoadFailures { get; }
    public Histogram<double> RequestDuration { get; }
    public Histogram<double> TokensPerSecond { get; }
    public Histogram<long> PromptSize { get; }

    public HardwareInfo Hardware { get; } = HardwareInfo.Read();

    public Telemetry()
    {
        // --- Hardware (statisch, als "info"-Metriken mit Labels) ---
        _meter.CreateObservableGauge("host_info", () => new Measurement<double>(1,
            new KeyValuePair<string, object?>("cpu", Hardware.Cpu),
            new KeyValuePair<string, object?>("logical_cores", Hardware.LogicalCores.ToString()),
            new KeyValuePair<string, object?>("ram_gb", Math.Round(Hardware.RamBytes / 1073741824.0).ToString()),
            new KeyValuePair<string, object?>("os", Hardware.Os)));
        _meter.CreateObservableGauge("host_cpu_logical_cores", () => new Measurement<double>(Hardware.LogicalCores));
        _meter.CreateObservableGauge("gpu_info", () => Hardware.Gpus.Select(g => new Measurement<double>(1,
            new KeyValuePair<string, object?>("gpu", g.Name),
            new KeyValuePair<string, object?>("driver", g.Driver),
            new KeyValuePair<string, object?>("vram_gb", g.VramBytes is long v ? Math.Round(v / 1073741824.0).ToString() : "?"))));
        _meter.CreateObservableGauge("gpu_vram_total_bytes", () => Hardware.Gpus
            .Where(g => g.VramBytes is not null)
            .Select(g => new Measurement<double>(g.VramBytes!.Value, new KeyValuePair<string, object?>("gpu", g.Name))));

        // --- Ereignisse aus server.log ---
        Requests = _meter.CreateCounter<long>("ollama_requests", description: "Inferenz-Anfragen (model, endpoint, status)");
        PromptTokens = _meter.CreateCounter<long>("ollama_prompt_tokens", description: "Verarbeitete Prompt-Tokens");
        OutputTokens = _meter.CreateCounter<long>("ollama_output_tokens", description: "Generierte Tokens");
        TruncatedPrompts = _meter.CreateCounter<long>("ollama_truncated_prompts", description: "Prompts, die wegen zu kleinem Kontext gekürzt wurden");
        Evictions = _meter.CreateCounter<long>("ollama_evictions", description: "Modelle aus dem VRAM verdrängt");
        LoadFailures = _meter.CreateCounter<long>("ollama_load_failures", description: "Fehlgeschlagene Modell-Ladevorgänge");
        RequestDuration = _meter.CreateHistogram<double>("ollama_request_duration_seconds", description: "Dauer pro Anfrage");
        TokensPerSecond = _meter.CreateHistogram<double>("ollama_tokens_per_second", description: "Geschwindigkeit (phase=prompt|output)");
        PromptSize = _meter.CreateHistogram<long>("ollama_prompt_size_tokens", description: "Prompt-Grösse vor dem Kürzen");

        // --- Rechner ---
        _meter.CreateObservableGauge("host_cpu_percent", () => One(Current.CpuPercent));
        _meter.CreateObservableGauge("host_memory_available_bytes", () => One(Current.MemoryAvailableBytes));
        _meter.CreateObservableGauge("host_memory_total_bytes", () => One(Current.MemoryTotalBytes));
        _meter.CreateObservableGauge("gpu_vram_dedicated_bytes", () => One(Current.VramDedicatedBytes));
        _meter.CreateObservableGauge("gpu_vram_shared_bytes", () => One(Current.VramSharedBytes));
        _meter.CreateObservableGauge("gpu_utilization_percent", () =>
            Current.GpuEngines.Select(e => new Measurement<double>(e.Value, new KeyValuePair<string, object?>("engine", e.Key))));

        // --- Ollama ---
        _meter.CreateObservableGauge("ollama_up", () => new Measurement<double>(Current.OllamaUp ? 1 : 0));
        _meter.CreateObservableGauge("ollama_model_loaded", () =>
            Current.Models.Select(m => new Measurement<double>(1, Tag(m))));
        _meter.CreateObservableGauge("ollama_model_vram_bytes", () =>
            Current.Models.Select(m => new Measurement<double>(m.VramBytes, Tag(m))));
        _meter.CreateObservableGauge("ollama_model_size_bytes", () =>
            Current.Models.Select(m => new Measurement<double>(m.SizeBytes, Tag(m))));
        _meter.CreateObservableGauge("ollama_model_context_tokens", () =>
            Current.Models.Where(m => m.ContextLength is not null)
                          .Select(m => new Measurement<double>(m.ContextLength!.Value, Tag(m))));
    }

    private static KeyValuePair<string, object?> Tag(LoadedModel m) => new("model", m.Name);

    private static IEnumerable<Measurement<double>> One(double? v) =>
        v is double d ? [new Measurement<double>(d)] : [];

    private static IEnumerable<Measurement<double>> One(long? v) =>
        v is long l ? [new Measurement<double>(l)] : [];

    public void Dispose() => _meter.Dispose();
}
