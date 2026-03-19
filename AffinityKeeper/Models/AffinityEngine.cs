namespace AffinityKeeper.Models;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>
/// CPUアフィニティの計算とプロセス操作を司るモデルクラス
/// </summary>
public static class AffinityEngine
{
    /// <summary>
    /// 文字列(0,1-3)をビットマスク(long)に変換します
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
                    int start = int.Parse(range[0]);
                    int end = int.Parse(range[1]);
                    for (int i = start; i <= end; i++) mask |= 1L << i;
                }
                else { mask |= 1L << int.Parse(p); }
            }
        }
        catch { /* 不正な形式は無視 */ }
        return mask;
    }

    /// <summary>
    /// ビットマスクをUI表示用の文字列(0,1,2)に変換します
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
    /// 全コア開放用のフルマスクを取得します
    /// </summary>
    public static long GetFullMask() => (1L << Environment.ProcessorCount) - 1;

    /// <summary>
    /// 指定したプロセスにマスクを適用します（リトライ機能付き）
    /// </summary>
    public static bool ApplyAffinity(Process p, long mask)
    {
        try
        {
            p.ProcessorAffinity = (IntPtr)mask;
            return true;
        }
        catch { return false; }
    }
}