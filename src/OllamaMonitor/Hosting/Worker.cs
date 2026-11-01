using System.Text.Json;
using OllamaMonitor.Logs;
using OllamaMonitor.Metrics;
using OllamaMonitor.Monitoring;

namespace OllamaMonitor.Hosting;

/// <summary>Sammelt alle paar Sekunden: Rechner-Werte, geladene Modelle (/api/ps) und neue Log-Zeilen.</summary>
public sealed class Worker(Telemetry tel, IConfiguration cfg, ILogger<Worker> log) : BackgroundService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(cfg.GetValue("Monitor:IntervalSeconds", 5));
        var ollamaUrl = cfg.GetValue("Monitor:OllamaUrl", "http://127.0.0.1:11434")!.TrimEnd('/');
        var logPath = cfg.GetValue<string>("Monitor:LogPath");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "server.log");

        SystemStats? sys = OperatingSystem.IsWindows() ? new SystemStats() : null;
        var tailer = new LogTailer(logPath);
        var parser = new LogParser(tel);

        // Optional: rohe Log-Zeilen zusätzlich an Loki schicken
        var lokiUrl = cfg["Loki:Url"];
        var loki = string.IsNullOrWhiteSpace(lokiUrl) ? null : new LokiPusher(lokiUrl, cfg["Loki:User"], cfg["Loki:Token"], log);
        log.LogInformation("Loki: {Loki}", loki is null ? "aus" : lokiUrl);

        log.LogInformation("Ollama: {Url} | Log: {Log} | Intervall: {Interval}s", ollamaUrl, logPath, interval.TotalSeconds);
        var hw = tel.Hardware;
        log.LogInformation("Hardware: {Cpu} ({Cores} Threads) | RAM {Ram:F0} GB | {Os}",
            hw.Cpu, hw.LogicalCores, hw.RamBytes / 1073741824.0, hw.Os);
        foreach (var g in hw.Gpus)
            log.LogInformation("GPU: {Gpu} | Treiber {Driver} | VRAM {Vram:F0} GB", g.Name, g.Driver, (g.VramBytes ?? 0) / 1073741824.0);

        using var timer = new PeriodicTimer(interval);
        do
        {
            var snap = tel.Current;

            if (sys is not null)
            {
                try { snap = sys.Read(snap); }
                catch (Exception ex) { log.LogWarning(ex, "Systemwerte konnten nicht gelesen werden"); }
            }

            snap = await ReadOllamaPs(ollamaUrl, snap, ct);
            tel.Current = snap;

            try
            {
                foreach (var line in tailer.ReadNewLines())
                {
                    parser.Parse(line);
                    loki?.Enqueue(line);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "server.log konnte nicht gelesen werden"); }

            if (loki is not null) await loki.FlushAsync(ct);
        }
        while (await timer.WaitForNextTickAsync(ct));

        sys?.Dispose();
    }

    private async Task<Snapshot> ReadOllamaPs(string baseUrl, Snapshot snap, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{baseUrl}/api/ps", ct));
            var models = new List<LoadedModel>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    models.Add(new LoadedModel(
                        m.GetProperty("name").GetString() ?? "?",
                        m.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                        m.TryGetProperty("size_vram", out var vram) ? vram.GetInt64() : 0,
                        m.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetInt64() : null));
                }
            }
            return snap with { OllamaUp = true, Models = models };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return snap with { OllamaUp = false, Models = [] };
        }
    }
}
