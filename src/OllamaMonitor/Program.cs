using System.Net.Http.Headers;
using System.Text;
using OllamaMonitor.Hosting;
using OllamaMonitor.Metrics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = Host.CreateApplicationBuilder(args);

// Läuft als Konsolen-App oder als Windows-Dienst (siehe README)
builder.Services.AddWindowsService(o => o.ServiceName = "OllamaMonitor");
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddHostedService<Worker>();

// OpenTelemetry Konfiguration über appsettings.json
bool useOtel = builder.Configuration.GetValue("UseOtel", false);
var exportSeconds = builder.Configuration.GetValue("Monitor:ExportIntervalSeconds", 15);

if (useOtel)
{
    var otlp = builder.Configuration.GetSection("GrafanaCloud");
    var endpoint = otlp["OtlpEndpoint"];
    var instanceId = otlp["InstanceId"];
    var token = otlp["Token"];
    var localEndpoint = builder.Configuration["LocalPrometheus:OtlpEndpoint"];

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("ollama-monitor", serviceInstanceId: Environment.MachineName))
        .WithMetrics(m =>
        {
            m.AddMeter(Telemetry.MeterName)
             .AddView("ollama_request_duration_seconds",
                 new ExplicitBucketHistogramConfiguration { Boundaries = [0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300] })
            .AddView("ollama_tokens_per_second",
                new ExplicitBucketHistogramConfiguration { Boundaries = [5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000] })
            .AddView("ollama_prompt_size_tokens",
                new ExplicitBucketHistogramConfiguration { Boundaries = [1024, 4096, 8192, 16384, 32768, 49152, 65536, 131072] });

            var cloudEnabled = !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(token);
            var localEnabled = !string.IsNullOrWhiteSpace(localEndpoint);

            if (localEnabled)
            {
                m.AddOtlpExporter("local", (o, reader) =>
                {
                    o.Endpoint = new Uri(localEndpoint!.TrimEnd('/') + "/v1/metrics");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportSeconds * 1000;
                });
            }

            if (cloudEnabled)
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{instanceId}:{token}"));
                m.AddOtlpExporter("grafana-cloud", (o, reader) =>
                {
                    o.Endpoint = new Uri(endpoint!.TrimEnd('/') + "/v1/metrics");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    o.HttpClientFactory = () =>
                    {
                        var client = new HttpClient();
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
                        return client;
                    };
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportSeconds * 1000;
                });
            }

            if (!cloudEnabled && !localEnabled)
            {
                m.AddConsoleExporter((_, reader) =>
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 30_000);
            }
        });
}

builder.Build().Run();
