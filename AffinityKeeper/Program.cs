using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Linq;

class Program
{
    static Dictionary<string, long> rules = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<int, long> trackedPids = new Dictionary<int, long>();

    static void Main()
    {
        LoadConfig();
        Console.WriteLine("Affinity Keeper started. (Full Trace Mode)");

        // 既存プロセスのスキャン
        ApplyToExistingProcesses();

        // 新規プロセスの監視開始
        StartProcessWatcher();

        Thread.Sleep(Timeout.Infinite);
    }

    static void LoadConfig()
    {
        string path = "affinity.ini";
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# exe=0-3\n# obs64=0,1,2,3");
            return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            var parts = t.Split('=');
            if (parts.Length != 2) continue;

            string exe = parts[0].Trim().Replace(".exe", "");
            long mask = CpuListToMask(parts[1].Trim());
            rules[exe] = mask;
            Console.WriteLine($"Rule: {exe} -> 0x{mask:X}");
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
                for (int i = start; i <= end; i++) mask |= 1L << i;
            }
            else { mask |= 1L << int.Parse(p); }
        }
        return mask;
    }

    static void ApplyToExistingProcesses()
    {
        Console.WriteLine("Scanning existing processes...");
        var allProcesses = Process.GetProcesses().ToList();
        var pidMap = allProcesses.ToDictionary(p => p.Id, p => p.ProcessName);

        // 1. まず直接ルールに合うものを処理
        foreach (var p in allProcesses)
        {
            if (rules.TryGetValue(p.ProcessName, out long mask))
            {
                lock (trackedPids) { trackedPids[p.Id] = mask; }
                ApplyAffinity(p, mask);
            }
        }

        // 2. 親を遡って判定（子プロセス対策）
        foreach (var p in allProcesses)
        {
            if (trackedPids.ContainsKey(p.Id)) continue; // すでに適用済ならスキップ

            try
            {
                // WMIで親PIDを取得
                using (var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {p.Id}"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                        if (trackedPids.TryGetValue(ppid, out long mask))
                        {
                            lock (trackedPids) { trackedPids[p.Id] = mask; }
                            ApplyAffinity(p, mask);
                        }
                    }
                }
            }
            catch { /* アクセス拒否等はスルー */ }
        }
    }

    static void StartProcessWatcher()
    {
        var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
        var watcher = new ManagementEventWatcher(query);

        watcher.EventArrived += (sender, e) =>
        {
            try
            {
                string name = e.NewEvent["ProcessName"].ToString().Replace(".exe", "");
                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                int ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);

                if (rules.TryGetValue(name, out long mask) || trackedPids.TryGetValue(ppid, out mask))
                {
                    lock (trackedPids) { trackedPids[pid] = mask; }
                    Thread.Sleep(250); // 起動直後の初期化待ち
                    var p = Process.GetProcessById(pid);
                    ApplyAffinity(p, mask);
                }
            }
            catch { }
        };
        watcher.Start();
    }

    static void ApplyAffinity(Process p, long mask)
    {
        try
        {
            p.ProcessorAffinity = (IntPtr)mask;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Applied 0x{mask:X} to {p.ProcessName} (PID: {p.Id})");
        }
        catch (Exception ex)
        {
            // 管理者権限でもアクセスできないシステムプロセスはここで弾かれる
            if (!p.ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed: {p.ProcessName} (PID: {p.Id}) - {ex.Message}");
        }
    }
}