using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OllamaMonitor;

/// <summary>
/// Schickt die rohen Zeilen aus server.log an Loki (Push-API /loki/api/v1/push).
/// Gesammelt wird pro Tick, gesendet in einem Request. Ist Loki nicht erreichbar,
/// bleiben die Zeilen im Puffer (max. MaxBuffered, älteste fliegen raus).
/// </summary>
public sealed partial class LokiPusher(string baseUrl, string? user, string? token, ILogger log)
{
    private const int MaxBuffered = 20_000;
    private const int MaxPerRequest = 5_000;

    private readonly Uri _pushUri = new(baseUrl.TrimEnd('/') + "/loki/api/v1/push");
    private readonly HttpClient _http = CreateClient(user, token);
    private readonly LinkedList<(long Ns, string Level, string Line)> _buffer = new();
    private readonly string _host = Environment.MachineName;
    private long _lastNs;
    private bool _warned;

    public void Enqueue(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Loki erwartet Nanosekunden; streng steigend, damit gleiche Zeitstempel nicht verworfen werden
        var ns = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        if (ns <= _lastNs) ns = _lastNs + 1;
        _lastNs = ns;

        _buffer.AddLast((ns, DetectLevel(line), line));
        while (_buffer.Count > MaxBuffered) _buffer.RemoveFirst();
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        while (_buffer.Count > 0)
        {
            var batch = _buffer.Take(MaxPerRequest).ToList();
            var payload = new
            {
                streams = batch
                    .GroupBy(e => e.Level)
                    .Select(g => new
                    {
                        stream = new Dictionary<string, string>
                        {
                            ["job"] = "ollama",
                            ["host"] = _host,
                            ["level"] = g.Key,
                        },
                        values = g.Select(e => new[] { e.Ns.ToString(), e.Line }).ToArray(),
                    }),
            };

            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(_pushUri, content, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    // 4xx (ausser 429) = Daten werden nie angenommen -> verwerfen statt endlos wiederholen
                    if ((int)resp.StatusCode is >= 400 and < 500 and not 429)
                    {
                        log.LogWarning("Loki lehnt {Count} Zeilen ab ({Status}): {Body}", batch.Count, (int)resp.StatusCode, body);
                        for (var i = 0; i < batch.Count; i++) _buffer.RemoveFirst();
                        continue;
                    }
                    throw new HttpRequestException($"{(int)resp.StatusCode} {body}");
                }

                for (var i = 0; i < batch.Count; i++) _buffer.RemoveFirst();
                if (_warned) { log.LogInformation("Loki wieder erreichbar"); _warned = false; }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!_warned) log.LogWarning("Loki nicht erreichbar ({Uri}): {Msg}. Zeilen werden gepuffert.", _pushUri, ex.Message);
                _warned = true;
                return; // nächster Tick versucht es erneut
            }
        }
    }

    /// <summary>Ollama (Go slog) schreibt "level=INFO"; [GIN]-Zeilen nach HTTP-Status; Rest = unknown.</summary>
    private static string DetectLevel(string line)
    {
        var m = LevelRx().Match(line);
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();

        var gin = GinStatusRx().Match(line);
        if (gin.Success)
            return gin.Groups[1].Value[0] switch { '5' => "error", '4' => "warn", _ => "info" };

        return "unknown";
    }

    private static HttpClient CreateClient(string? user, string? token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(token))
        {
            // Grafana Cloud Loki: Basic Auth mit User-ID und Token. Lokal: leer lassen.
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
        return client;
    }

    [GeneratedRegex(@"\blevel=(DEBUG|INFO|WARN|ERROR|TRACE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRx();

    [GeneratedRegex(@"^\[GIN\][^|]*\|\s*(\d{3})\s*\|")]
    private static partial Regex GinStatusRx();
}
