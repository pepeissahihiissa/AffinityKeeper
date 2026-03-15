namespace AffinityKeeper;

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Serilog;

// 簡易的な起動画面（スプラッシュスクリーン）の定義
public class SplashForm : Form
{
    private Label lblStatus;
    private Panel pnlBackground;

    public SplashForm()
    {
        // フォームの設定
        this.Size = new Size(450, 300);
        this.FormBorderStyle = FormBorderStyle.None; // 枠なし
        this.StartPosition = FormStartPosition.CenterScreen; // 画面中央
        this.TopMost = true; // 最前面に表示

        // 背景パネル（画像を表示するため）
        pnlBackground = new Panel { Dock = DockStyle.Fill };

        // 画像読み込み（ファイルがなければ背景色のみ）
        if (File.Exists("splash.png"))
        {
            try { pnlBackground.BackgroundImage = Image.FromFile("splash.png"); } catch { }
            pnlBackground.BackgroundImageLayout = ImageLayout.Stretch;
        }
        else
        {
            pnlBackground.BackColor = Color.FromArgb(45, 45, 48); // ダークグレー
        }

        // 状態表示ラベル
        lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 0, 0, 0), // 半透明の黒
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Text = "Initializing..."
        };

        pnlBackground.Controls.Add(lblStatus);
        this.Controls.Add(pnlBackground);
    }

    // 状態テキストを更新するメソッド（スレッドセーフ）
    public void UpdateStatus(string text)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action(() => UpdateStatus(text)));
            return;
        }
        lblStatus.Text = text;
        lblStatus.Refresh(); // 描画を強制更新
    }
}

static class Program
{
    private static NotifyIcon? trayIcon;
    private static SplashForm? splashForm;

    [STAThread]
    static void Main()
    {
        // アプリ固有の名前でMutexを作成
        using (Mutex mutex = new Mutex(false, "AffinityKeeper_SingleInstance_Mutex"))
        {
            // 他のインスタンスが既に動いているかチェック
            if (!mutex.WaitOne(0, false))
            {
                MessageBox.Show("Affinity Keeperは既に起動しています。", "二重起動チェック", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            // 1. アプリケーションの初期化
            ApplicationConfiguration.Initialize();

            // 2. ログの設定
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/affinity-keeper-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                // 3. 起動画面（スプラッシュスクリーン）を表示
                splashForm = new SplashForm();
                splashForm.Show();
                Application.DoEvents(); // 描画を強制

                // 4. 初期化処理を非同期で実行
                InitializeApplicationAsync().ContinueWith(t =>
                {
                    // 初期化完了後の処理（メインスレッドで行う必要がある）
                    if (splashForm != null && !splashForm.IsDisposed)
                    {
                        splashForm.Invoke(new Action(() =>
                        {
                            splashForm.Close(); // 起動画面を閉じる
                            InitializeTrayIcon(); // トレイアイコンを表示
                            Log.Information("Initialization complete. Sitting in tray.");
                        }));
                    }
                });

                // 5. メインループ開始
                Application.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }

    // 非同期で初期化処理を行うメソッド
    private static async Task InitializeApplicationAsync()
    {
        if (splashForm == null) return;

        // --- 1. 設定ファイルの読み込み ---
        splashForm.UpdateStatus("設定ファイルを読み込み中...");
        Log.Information("Loading configuration...");
        // (ここに LoadRules() などの実際の処理を入れる)
        await Task.Delay(1000); // 処理をシミュレート

        // --- 2. 既存プロセスのスキャン (ここが時間がかかる部分) ---
        splashForm.UpdateStatus("実行中のプロセスをスキャン・適用中...");
        Log.Information("Scanning existing processes...");
        // (ここに ScanAndApplyAffinity() などの実際の処理を入れる)
        // ※実際には10秒かかる処理がここに入ります。
        await Task.Delay(5000); // 5秒の処理をシミュレート

        // --- 3. プロセス監視の開始 ---
        splashForm.UpdateStatus("プロセス監視を開始中...");
        Log.Information("Starting process watcher...");
        // (ここに StartProcessWatcher() などの実際の処理を入れる)
        await Task.Delay(1000); // 処理をシミュレート

        splashForm.UpdateStatus("準備完了。トレイに常駐します。");
        await Task.Delay(500); // 少し待ってから閉じる
    }

    private static void InitializeTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("設定画面を開く", null, (s, e) => ShowConfigForm());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("終了", null, (s, e) => Application.Exit());

        // アイコンファイル（app.ico）があれば読み込む
        Icon? icon = null;
        if (File.Exists("app.ico"))
        {
            try { icon = new Icon("app.ico"); } catch { }
        }

        trayIcon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application, // ファイルがなければ標準アイコン
            ContextMenuStrip = contextMenu,
            Text = "Affinity Keeper",
            Visible = true
        };

        trayIcon.DoubleClick += (s, e) => ShowConfigForm();
    }

    private static Form? configForm;
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