using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;

class Program
{
    // ルール：プロセス名 -> CPUマスク
    static Dictionary<string, long> rules = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    // 追跡中のPIDリスト：親がルールに合致していた場合、その子PIDもここに入れる
    // Key: 子のPID, Value: 適用すべきマスク値
    static Dictionary<int, long> trackedPids = new Dictionary<int, long>();

    static void Main()
    {
        LoadConfig();
        Console.WriteLine("Affinity Keeper started. (Monitoring child processes...)");

        ApplyToExistingProcesses();
        StartProcessWatcher();

        Thread.Sleep(Timeout.Infinite);
    }

    static void LoadConfig()
    {
        string path = "affinity.ini";
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# exe=0-3\n# obs64=1,2");
            return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            var parts = t.Split('=');
            if (parts.Length != 2) continue;

            string exe = parts[0].Trim().Replace(".exe", ""); // .exeを除去して統一
            long mask = CpuListToMask(parts[1].Trim());
            rules[exe] = mask;

            Console.WriteLine($"Rule added: {exe} -> 0x{mask:X}");
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
            else
            {
                mask |= 1L << int.Parse(p);
            }
        }
        return mask;
    }

    static void ApplyToExistingProcesses()
    {
        foreach (var p in Process.GetProcesses())
        {
            TryApply(p, 0); // 既存プロセスは親チェックなし（または必要に応じて拡張）
        }
    }

    static void StartProcessWatcher()
    {
        // ParentProcessIDを取得するためにWQLクエリを使用
        var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
        var watcher = new ManagementEventWatcher(query);

        watcher.EventArrived += (sender, e) =>
        {
            try
            {
                string name = e.NewEvent["ProcessName"].ToString().Replace(".exe", "");
                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                int ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);

                // 1. ルールに直接マッチするか？
                // 2. または、親プロセスが既に追跡対象か？
                if (rules.TryGetValue(name, out long mask) || trackedPids.TryGetValue(ppid, out mask))
                {
                    // このPIDを追跡リストに追加（子プロセスがさらに孫を作る場合のため）
                    lock (trackedPids) { trackedPids[pid] = mask; }

                    // プロセス起動直後はハンドルが取れないことがあるため少し待機
                    Thread.Sleep(250);

                    var p = Process.GetProcessById(pid);
                    ApplyAffinity(p, mask);
                }
            }
            catch { /* プロセスがすぐに終了した場合などは無視 */ }
        };

        watcher.Start();
    }

    static void TryApply(Process p, int ppid)
    {
        string name = p.ProcessName; // .exeは含まれない
        if (rules.TryGetValue(name, out long mask))
        {
            lock (trackedPids) { trackedPids[p.Id] = mask; }
            ApplyAffinity(p, mask);
        }
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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to apply to {p.ProcessName}: {ex.Message}");
        }
    }
}