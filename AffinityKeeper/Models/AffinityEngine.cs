namespace AffinityKeeper.Models;

using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

public static class AffinityEngine
{
    /// <summary>
    /// 文字列(0,1-3)をビットマスク(long)に変換
    /// </summary>
    public static long CpuListToMask(string text)
    {
        long mask = 0;
        if (string.IsNullOrWhiteSpace(text)) return mask;
        try
        {
            foreach (var part in text.Split(','))
            {
                var p = part.Trim();
                if (p.Contains("-"))
                {
                    var range = p.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0], out int s) && int.TryParse(range[1], out int e))
                        for (int i = s; i <= e; i++) mask |= 1L << i;
                }
                else if (int.TryParse(p, out int v))
                {
                    mask |= 1L << v;
                }
            }
        }
        catch { }
        return mask;
    }

    /// <summary>
    /// ビットマスクを文字列(0,1,2)に変換
    /// </summary>
    public static string MaskToCpuList(long mask)
    {
        var cores = new List<int>();
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            if ((mask & (1L << i)) != 0) cores.Add(i);
        }
        return string.Join(",", cores);
    }

    /// <summary>
    /// アフィニティを適用（リトライ機能付き）
    /// </summary>
    public static bool ApplyAffinity(Process p, long mask)
    {
        try
        {
            p.ProcessorAffinity = (IntPtr)mask;
            Log.Information("Applied 0x{Mask:X} to {ProcessName} (PID: {Pid})", mask, p.ProcessName, p.Id);
            return true;
        }
        catch (Exception ex)
        {
            if (!p.ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                Log.Warning("Failed to apply affinity to {ProcessName}: {Message}", p.ProcessName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 実行中の全プロセスとその親PIDの対応表を高速に取得します。
    /// </summary>
    public static Dictionary<int, int> GetParentProcessMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            // 全プロセスのIDと親IDを一括取得
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                int pid = Convert.ToInt32(obj["ProcessId"]);
                int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                map[pid] = ppid;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build parent process map");
        }
        return map;
    }

    /// <summary>
    /// フルコア開放用のマスクを取得
    /// </summary>
    public static long GetFullMask() => (1L << Environment.ProcessorCount) - 1;
}