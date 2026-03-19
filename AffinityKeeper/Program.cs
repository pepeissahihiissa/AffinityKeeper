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

// スプラッシュ画面の定義
public class SplashForm : Form
{
    private Label lblStatus;
    private Panel pnlBackground;

    public SplashForm()
    {
        this.Size = new Size(450, 300);
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.CenterScreen;
        // this.TopMost = true;

        pnlBackground = new Panel { Dock = DockStyle.Fill };
        if (File.Exists("splash.png"))
        {
            try { pnlBackground.BackgroundImage = Image.FromFile("splash.png"); } catch { }
            pnlBackground.BackgroundImageLayout = ImageLayout.Stretch;
        }
        else
        {
            pnlBackground.BackColor = Color.FromArgb(45, 45, 48);
        }

        lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Text = "Initializing..."
        };

        pnlBackground.Controls.Add(lblStatus);
        this.Controls.Add(pnlBackground);
    }

    public void UpdateStatus(string text)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action(() => UpdateStatus(text)));
            return;
        }
        lblStatus.Text = text;
        lblStatus.Refresh();
    }
}

static class Program
{
    private static NotifyIcon? trayIcon;
    private static SplashForm? splashForm;
    private static Form? configForm;

    // ロジック用変数
    private static Dictionary<string, long> rules = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<int, long> trackedPids = new Dictionary<int, long>();
    private static ManagementEventWatcher? processWatcher;

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

    private static void LoadRules()
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
            long mask = CpuListToMask(parts[1].Trim());
            rules[exe] = mask;
        }
    }

    private static long CpuListToMask(string text)
    {
        long mask = 0;
        try
        {
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
        }
        catch { }
        return mask;
    }

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
                            ApplyAffinity(p, mask);
                        }
                    }
                }
            }
            catch { }
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
                string name = e.NewEvent["ProcessName"].ToString().Replace(".exe", "");
                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                int ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);

                if (rules.TryGetValue(name, out long mask) || trackedPids.TryGetValue(ppid, out mask))
                {
                    lock (trackedPids) { trackedPids[pid] = mask; }
                    Thread.Sleep(300); // 起動直後の安定待ち
                    var p = Process.GetProcessById(pid);
                    ApplyAffinity(p, mask);
                }
            }
            catch { }
        };
        processWatcher.Start();
    }

    private static void ApplyAffinity(Process p, long mask)
    {
        try
        {
            p.ProcessorAffinity = (IntPtr)mask;
            Log.Information("Applied 0x{Mask:X} to {ProcessName} (PID: {Pid})", mask, p.ProcessName, p.Id);
        }
        catch (Exception ex)
        {
            if (!p.ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                Log.Warning("Failed to apply affinity to {ProcessName}: {Message}", p.ProcessName, ex.Message);
        }
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
}