using System.Diagnostics;
using System.Runtime.Versioning;
using OllamaMonitor.Metrics;

namespace OllamaMonitor.Monitoring;

/// <summary>
/// Liest CPU, RAM, VRAM und GPU-Auslastung über die Windows-Leistungsindikatoren
/// (dieselben Werte wie im Task-Manager, funktioniert auch mit AMD-Karten).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemStats : IDisposable
{
    private readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total");
    private readonly PerformanceCounter _memAvailable = new("Memory", "Available Bytes");
    private Dictionary<string, CounterSample> _previousGpu = new();

    public SystemStats()
    {
        _cpu.NextValue(); // erster Wert ist immer 0
    }

    public Snapshot Read(Snapshot s)
    {
        var (dedicated, shared) = ReadVram();
        return s with
        {
            CpuPercent = Math.Round(_cpu.NextValue(), 1),
            MemoryAvailableBytes = (long)_memAvailable.NextValue(),
            MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            VramDedicatedBytes = dedicated,
            VramSharedBytes = shared,
            GpuEngines = ReadGpuEngines(),
        };
    }

    private static (long?, long?) ReadVram()
    {
        try
        {
            var data = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            return (Sum(data, "Dedicated Usage"), Sum(data, "Shared Usage"));
        }
        catch
        {
            return (null, null);
        }
    }

    private static long? Sum(InstanceDataCollectionCollection data, string counter)
    {
        var col = data[counter];
        if (col is null) return null;
        long sum = 0;
        foreach (InstanceData d in col.Values) sum += d.RawValue;
        return sum;
    }

    /// <summary>
    /// Instanzen heissen z.B. "pid_1234_luid_0x..._phys_0_eng_3_engtype_Compute".
    /// Pro Engine über alle Prozesse summieren, dann pro Engine-Typ das Maximum nehmen
    /// (entspricht der Anzeige im Task-Manager).
    /// </summary>
    private Dictionary<string, double> ReadGpuEngines()
    {
        var result = new Dictionary<string, double>();
        try
        {
            var col = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            if (col is null) return result;

            var current = new Dictionary<string, CounterSample>();
            var perEngine = new Dictionary<string, (string Type, double Value)>();

            foreach (InstanceData d in col.Values)
            {
                current[d.InstanceName] = d.Sample;
                if (!_previousGpu.TryGetValue(d.InstanceName, out var prev)) continue;

                var name = d.InstanceName;
                var luid = name.IndexOf("_luid_", StringComparison.Ordinal);
                var type = name.IndexOf("_engtype_", StringComparison.Ordinal);
                if (luid < 0 || type < 0) continue;

                var engineKey = name[(luid + 1)..type];
                var engineType = name[(type + "_engtype_".Length)..].Trim().ToLowerInvariant();
                if (engineType.Length == 0) continue;

                var value = CounterSample.Calculate(prev, d.Sample);
                perEngine[engineKey] = perEngine.TryGetValue(engineKey, out var e)
                    ? (engineType, e.Value + value)
                    : (engineType, value);
            }

            _previousGpu = current;

            foreach (var (_, (t, v)) in perEngine)
            {
                var clamped = Math.Min(100, v);
                result[t] = result.TryGetValue(t, out var existing) ? Math.Max(existing, clamped) : clamped;
            }
        }
        catch
        {
            // Kategorie nicht vorhanden (z.B. alte Treiber) -> keine GPU-Werte
        }
        return result;
    }

    public void Dispose()
    {
        _cpu.Dispose();
        _memAvailable.Dispose();
    }
}
