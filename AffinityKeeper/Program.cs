using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;

class Program
{
    static Dictionary<string, long> rules =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    static void Main()
    {
        LoadConfig();

        Console.WriteLine("Affinity Keeper started.");

        ApplyToExistingProcesses();

        StartProcessWatcher();

        Thread.Sleep(Timeout.Infinite);
    }

    static void LoadConfig()
    {
        string path = "affinity.ini";

        if (!File.Exists(path))
        {
            File.WriteAllText(path,
@"# exe=cpu list
# examples:
# chrome.exe=0-3
# ffxiv_dx11.exe=1-7
# obs64.exe=0,1
");

            Console.WriteLine("affinity.ini created.");
            return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();

            if (t.Length == 0 || t.StartsWith("#"))
                continue;

            var parts = t.Split('=');

            if (parts.Length != 2)
                continue;

            string exe = parts[0].Trim();
            string cpuList = parts[1].Trim();

            long mask = CpuListToMask(cpuList);

            rules[exe] = mask;

            Console.WriteLine($"{exe} -> mask 0x{mask:X}");
        }
    }

    static long CpuListToMask(string text)
    {
        long mask = 0;

        foreach (var part in text.Split(','))
        {
            string p = part.Trim();

            if (p.Contains("-"))
            {
                var range = p.Split('-');

                int start = int.Parse(range[0]);
                int end = int.Parse(range[1]);

                for (int i = start; i <= end; i++)
                    mask |= 1L << i;
            }
            else
            {
                int cpu = int.Parse(p);
                mask |= 1L << cpu;
            }
        }

        return mask;
    }

    static void ApplyToExistingProcesses()
    {
        foreach (var p in Process.GetProcesses())
        {
            TryApply(p);
        }
    }

    static void StartProcessWatcher()
    {
        var query = new WqlEventQuery(
            "SELECT * FROM Win32_ProcessStartTrace");

        var watcher = new ManagementEventWatcher(query);

        watcher.EventArrived += (sender, e) =>
        {
            try
            {
                string name = e.NewEvent["ProcessName"].ToString();
                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);

                if (!rules.ContainsKey(name))
                    return;

                Thread.Sleep(200);

                var p = Process.GetProcessById(pid);

                TryApply(p);
            }
            catch { }
        };

        watcher.Start();
    }

    static void TryApply(Process p)
    {
        try
        {
            if (!rules.TryGetValue(p.ProcessName + ".exe", out long mask))
                if (!rules.TryGetValue(p.ProcessName, out mask))
                    return;

            p.ProcessorAffinity = (IntPtr)mask;

            Console.WriteLine(
                $"Applied 0x{mask:X} to {p.ProcessName} (PID {p.Id})");
        }
        catch ( Exception ex)
        {
            // 権限不足などで失敗した場合は理由を表示するとデバッグしやすいです
            Console.WriteLine($"Failed to apply to {p.ProcessName}: {ex.Message}");
        }
    }
}