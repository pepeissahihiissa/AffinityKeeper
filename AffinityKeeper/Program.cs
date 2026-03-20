namespace AffinityKeeper;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Serilog;
using AffinityKeeper.Models;
using AffinityKeeper.Views;

static class Program
{
    private static NotifyIcon? trayIcon;
    private static SplashForm? splashForm;
    private static Form? configForm;

    // ロジック用変数
    private static Dictionary<string, long> rules = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<int, long> trackedPids = new Dictionary<int, long>();
    private static ManagementEventWatcher? processWatcher;

    // 外部から rules を参照するためのゲッター
    public static Dictionary<string, long> GetRules() => rules;

    [STAThread]
    static void Main()
    {
        using (Mutex mutex = new Mutex(false, "AffinityKeeper_SingleInstance_Mutex"))
        {
            if (!mutex.WaitOne(0, false))
            {
                MessageBox.Show("Affinity Keeperは既に起動しています。", "二重起動チェック", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            ApplicationConfiguration.Initialize();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/affinity-keeper-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                splashForm = new SplashForm();
                splashForm.Show();
                Application.DoEvents();

                // 初期化を非同期で開始
                InitializeApplicationAsync().ContinueWith(t =>
                {
                    if (splashForm != null && !splashForm.IsDisposed)
                    {
                        splashForm.Invoke(new Action(() =>
                        {
                            splashForm.Close();
                            InitializeTrayIcon();
                            Log.Information("Initialization complete. Sitting in tray.");
                        }));
                    }
                });

                Application.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            finally
            {
                processWatcher?.Stop();
                Log.CloseAndFlush();
            }
        }
    }

    private static async Task InitializeApplicationAsync()
    {
        if (splashForm == null) return;

            // 1. 設定ロード
            // splashForm
        splashForm.UpdateStatus("設定ファイルを読み込み中...");
        await Task.Run(() => LoadRules());

        // 2. 既存プロセスへの適用
        splashForm.UpdateStatus("実行中のプロセスをスキャン中...");
        await Task.Run(() => ApplyToExistingProcesses());

        // 3. 監視開始
        splashForm.UpdateStatus("プロセス監視を開始中...");
        await Task.Run(() => StartProcessWatcher());

        splashForm.UpdateStatus("準備完了。トレイに常駐します。");
        await Task.Delay(500);
    }

    // --- ロジック部分の実装 ---

    public static void LoadRules()
    {
        string path = "affinity.ini";
        rules.Clear();
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            var parts = t.Split('=');
            if (parts.Length != 2) continue;

            string exe = parts[0].Trim().Replace(".exe", "");
            long mask = AffinityEngine.CpuListToMask(parts[1].Trim());
            rules[exe] = mask;
        }
    }

    /*
    private static void ApplyToExistingProcesses()
    {
        var allProcesses = Process.GetProcesses().ToList();

        // 直接マッチ
        foreach (var p in allProcesses)
        {
            if (rules.TryGetValue(p.ProcessName, out long mask))
            {
                lock (trackedPids) { trackedPids[p.Id] = mask; }
                ApplyAffinity(p, mask);
            }
        }

        // 親から継承（子プロセス対策）
        foreach (var p in allProcesses)
        {
            if (trackedPids.ContainsKey(p.Id)) continue;
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {p.Id}"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                        if (trackedPids.TryGetValue(ppid, out long mask))
                        {
                            lock (trackedPids) { trackedPids[p.Id] = mask; }
                            AffinityEngine.ApplyAffinity(p, mask);
                        }
                    }
                }
            }
            catch { }
        }
    }
    */

    private static void ApplyToExistingProcesses()
    {
        var allProcesses = Process.GetProcesses().ToList();

        // 1. 親子関係マップを一括作成 (ここが高速化の鍵)
        var parentMap = AffinityEngine.GetParentProcessMap();

        // 2. 直接マッチするものを先に処理
        foreach (var p in allProcesses)
        {
            if (rules.TryGetValue(p.ProcessName, out long mask))
            {
                lock (trackedPids) { trackedPids[p.Id] = mask; }
                AffinityEngine.ApplyAffinity(p, mask);
            }
        }

        // 3. 親から継承するものを処理 (メモリ上のマップを参照するだけなので爆速)
        foreach (var p in allProcesses)
        {
            if (trackedPids.ContainsKey(p.Id)) continue;

            if (parentMap.TryGetValue(p.Id, out int ppid))
            {
                if (trackedPids.TryGetValue(ppid, out long mask))
                {
                    lock (trackedPids) { trackedPids[p.Id] = mask; }
                    AffinityEngine.ApplyAffinity(p, mask);
                }
            }
        }
    }

    private static void StartProcessWatcher()
    {
        var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
        processWatcher = new ManagementEventWatcher(query);
        processWatcher.EventArrived += (sender, e) =>
        {
            try
            {
                string rawName = e.NewEvent["ProcessName"].ToString() ?? "";
                // .exeを消して比較用に正規化
                string name = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                              ? rawName.Substring(0, rawName.Length - 4)
                              : rawName;

                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                int ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);

                long mask = 0;
                bool shouldApply = false;

                // 1. 直接ルールにマッチするか？
                if (rules.TryGetValue(name, out mask))
                {
                    shouldApply = true;
                }
                // 2. 親が管理対象か？
                else if (trackedPids.TryGetValue(ppid, out mask))
                {
                    shouldApply = true;
                }

                if (shouldApply)
                {
                    lock (trackedPids) { trackedPids[pid] = mask; }

                    // 起動直後はプロセスハンドルが取れないことがあるためリトライ
                    Task.Run(() => {
                        Thread.Sleep(500);
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            AffinityEngine.ApplyAffinity(p, mask);
                        }
                        catch { /* プロセスがすぐ終了した場合など */ }
                    });
                }
            }
            catch (Exception ex) { Log.Error(ex, "Watcher Error"); }
        };
        processWatcher.Start();
    }

    // --- UI制御 ---

    private static void InitializeTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("設定画面を開く", null, (s, e) => ShowConfigForm());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("終了", null, (s, e) => Application.Exit());

        // 自身のexeアイコンを抽出
        Icon? appIcon = null;
        try
        {
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            appIcon = SystemIcons.Application;
        }

        trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            ContextMenuStrip = contextMenu,
            Text = "Affinity Keeper",
            Visible = true
        };
        trayIcon.DoubleClick += (s, e) => ShowConfigForm();
    }

    private static void ShowConfigForm()
    {
        if (configForm == null || configForm.IsDisposed)
        {
            configForm = new ConfigForm();
        }
        configForm.Show();
        configForm.Activate();
    }

    /// <summary>
    /// 相違検知時のトレイアイコン変更と通知
    /// </summary>
    public static void NotifyMismatch(string processName)
    {
        if (trayIcon == null) return;

        // アイコンを警告用に変更（リソースに警告アイコンがあれば）
        // trayIcon.Icon = SystemIcons.Warning; 

        trayIcon.BalloonTipTitle = "Affinity相違検知";
        trayIcon.BalloonTipText = $"{processName} の設定が外部で変更された可能性があります。";
        trayIcon.ShowBalloonTip(3000);
    }
}