using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

class Program
{
    static Dictionary<string, long> rules = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<int, long> trackedPids = new Dictionary<int, long>();
    static string configPath = "affinity.ini";
    static FileSystemWatcher configWatcher;

    static void Main()
    {
        // --- ログの設定 (ここがログローテーションのキモ) ---
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console() // コンソールに出力
            .WriteTo.File("logs/affinity-keeper-.txt",
                rollingInterval: RollingInterval.Day,   // 毎日新しいファイルを作成
                retainedFileCountLimit: 7,              // 7日分だけ保持（ローテーション）
                fileSizeLimitBytes: 10 * 1024 * 1024,   // 10MBを超えたら分割
                rollOnFileSizeLimit: true)              // サイズ上限で新しいファイルを作る
            .CreateLogger();

        try
        {
            LoadConfig();
            Log.Information("Affinity Keeper started. (Full Trace Mode)");

            ApplyToExistingProcesses();
            StartConfigWatcher();
            StartProcessWatcher();

            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush(); // ログの書き出しを完了させて終了
        }
    }

    static void StartConfigWatcher()
    {
        configWatcher = new FileSystemWatcher
        {
            Path = AppDomain.CurrentDomain.BaseDirectory,
            Filter = configPath,
            NotifyFilter = NotifyFilters.LastWrite
        };

        configWatcher.Changed += (s, e) =>
        {
            // メモ帳などの保存時に複数回イベントが発生するのを防ぐための簡易的な待機
            Thread.Sleep(500);
            Log.Information("Configuration change detected. Reloading...");

            lock (rules)
            {
                rules.Clear();
                LoadConfig();
            }
            // 新しいルールに基づいて既存プロセスに再適用
            ApplyToExistingProcesses();
        };

        configWatcher.EnableRaisingEvents = true;
    }

    static void LoadConfig()
    {
        //string path = "affinity.ini";
        if (!File.Exists(configPath))
        {
            // File.WriteAllText(path, "# exe=0-3\n# obs64=0,1,2,3");
            File.WriteAllText(configPath, "# exe=0-3!");
            return;
        }
        /*
        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            var parts = t.Split('=');
            if (parts.Length != 2) continue;

            string exe = parts[0].Trim().Replace(".exe", "");
            long mask = CpuListToMask(parts[1].Trim());
            rules[exe] = mask;
            // Console.WriteLine($"Rule: {exe} -> 0x{mask:X}");
            Log.Information("Rule added: {exe} -> 0x{mask:X}", exe, mask);
        }
        */

        using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                var parts = line.Split('=');
                if (parts.Length != 2) continue;

                string exe = parts[0].Trim().Replace(".exe", "");
                long mask = CpuListToMask(parts[1].Trim());

                lock (rules) { rules[exe] = mask; }
                Log.Information("Rule loaded: {Exe} -> 0x{Mask:X}", exe, mask);
            }
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
        // Console.WriteLine("Scanning existing processes...");
        Log.Information("Scanning existing processes...");
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
            // Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Applied 0x{mask:X} to {p.ProcessName} (PID: {p.Id})");
            Log.Information("Applied 0x{mask:X} to {ProcessName} (PID: {Pid})", mask, p.ProcessName, p.Id);
        }
        catch (Exception ex)
        {
            // 管理者権限でもアクセスできないシステムプロセスはここで弾かれる
            if (!p.ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                // Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed: {p.ProcessName} (PID: {p.Id}) - {ex.Message}");
                Log.Warning("Failed: {ProcessName} (PID: {Pid}) - {Message}", p.ProcessName, p.Id, ex.Message);
        }
    }
}