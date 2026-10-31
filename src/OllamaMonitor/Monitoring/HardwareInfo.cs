using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OllamaMonitor.Monitoring;

public sealed record GpuInfo(string Name, string Driver, long? VramBytes);

/// <summary>Statische Hardware-Infos, einmal beim Start aus der Registry gelesen (kein WMI nötig).</summary>
public sealed record HardwareInfo(string Cpu, int LogicalCores, long RamBytes, string Os, IReadOnlyList<GpuInfo> Gpus)
{
    public static HardwareInfo Read()
    {
        var cpu = "unknown";
        var gpus = new List<GpuInfo>();

        if (OperatingSystem.IsWindows())
        {
            cpu = ReadCpu() ?? cpu;
            gpus = ReadGpus();
        }

        return new HardwareInfo(
            cpu,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.OSDescription.Trim(),
            gpus);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    /// <summary>Grafikkarten aus der Geräteklasse "Display" (Name, Treiber, VRAM gesamt).</summary>
    [SupportedOSPlatform("windows")]
    private static List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();
        using var cls = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return list;

        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name) continue;
                if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;

                var driver = key.GetValue("DriverVersion") as string ?? "";
                var vram = ToLong(key.GetValue("HardwareInformation.qwMemorySize"))
                           ?? ToLong(key.GetValue("HardwareInformation.MemorySize"));
                list.Add(new GpuInfo(name.Trim(), driver, vram));
            }
            catch
            {
                // einzelne Unterschlüssel sind ohne Admin-Rechte nicht lesbar -> überspringen
            }
        }
        return list;
    }

    private static long? ToLong(object? v) => v switch
    {
        long l => l,
        int i => (uint)i,
        byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        _ => null,
    };
}
