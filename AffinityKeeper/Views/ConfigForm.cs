namespace AffinityKeeper.Views;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Serilog;
using AffinityKeeper.Models;

/// <summary>
/// 設定画面のUI
/// </summary>
public class ConfigForm : Form
{
    // UIコンポーネント
    private ListBox lbRules = new ListBox() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9) };
    private ListBox lbRunning = new ListBox() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9) };
    private TextBox txtCpu = new TextBox() { Width = 200, PlaceholderText = "例: 0,1,2,4-6" };
    private ComboBox cbPresets = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private Label lblStatus = new Label { Text = "● 状態確認中...", ForeColor = Color.Gray, Width = 300, AutoSize = true };
    private Button btnUpdateRule = new Button { Text = "選択中のルールを更新", Width = 150, Enabled = false };
    private Button  btnSyncFromActual = new Button { Text = "実態をメモリに反映", Visible = false };

    private List<CheckBox> cpuCheckBoxes = new List<CheckBox>();
    private FlowLayoutPanel cpuPanel = new FlowLayoutPanel() { Dock = DockStyle.Fill, AutoScroll = true };

    private readonly string configPath = "affinity.ini";
    private readonly string presetsDir = "presets";
    private bool isUpdating = false;

    private System.Windows.Forms.Timer diffCheckTimer;
    private bool hasAffinityMismatch = false;

    public ConfigForm()
    {
        Text = "Affinity Keeper Settings (Profile Mode)";
        Size = new Size(1000, 750);
        MinimumSize = new Size(800, 600);

        if (!Directory.Exists(presetsDir)) Directory.CreateDirectory(presetsDir);

        InitializeMainLayout();
        InitializeCpuCheckBoxes();

        // イベント紐付け
        txtCpu.TextChanged += (s, e) => {
            UpdateChecksFromText();
            // テキストボックスの直接編集はプリセット一致判定に影響しない（保存/更新ボタン押下時に判定）
        };

        cbPresets.SelectedIndexChanged += (s, e) => LoadSelectedPreset();

        lbRules.SelectedIndexChanged += (s, e) => {
            OnRuleSelected();
            btnUpdateRule.Enabled = lbRules.SelectedItem != null;
        };

        // Affinityチェックタイマー
        diffCheckTimer = new System.Windows.Forms.Timer { Interval = 5000 }; // 1分間隔
        diffCheckTimer.Tick += (s, e) => CheckAffinityMismatch();
        diffCheckTimer.Start();

        // 初期ロード
        RefreshPresets();
        RefreshRules();
        RefreshRunningProcesses();
    }

    /// <summary>
    /// UI初期配置
    /// </summary>
    private void InitializeMainLayout()
    {
        var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240)); // 少し広げました

        // 1. プリセットエリア
        var presetPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 12, 0, 0) };
        var btnSavePreset = new Button { Text = "構成を保存", Width = 100 };
        var btnDeletePreset = new Button { Text = "削除", Width = 60 };
        btnSavePreset.Click += (s, e) => SaveCurrentAsPreset();
        btnDeletePreset.Click += (s, e) => DeletePreset();
        presetPanel.Controls.AddRange(new Control[] { new Label { Text = "プロファイル:", AutoSize = true }, cbPresets, btnSavePreset, btnDeletePreset, lblStatus });

        // 2. リストエリア
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(5) };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var btnRemoveRule = new Button { Text = "ルールを削除してAffinityリセット", Dock = DockStyle.Bottom, Height = 35, BackColor = Color.MistyRose };
        btnRemoveRule.Click += (s, e) => RemoveRuleAndResetAffinity(); // 変更点

        var ruleGroup = new GroupBox { Text = "登録済みルール", Dock = DockStyle.Fill };
        ruleGroup.Controls.Add(lbRules);
        ruleGroup.Controls.Add(btnRemoveRule);

        var btnAdd = new Button { Text = "◀ 追加", Dock = DockStyle.Fill, Margin = new Padding(0, 100, 0, 100), BackColor = Color.LightSkyBlue };
        btnAdd.Click += (s, e) => AddRuleFromRunning();

        var runningGroup = new GroupBox { Text = "実行中のプロセス", Dock = DockStyle.Fill };
        runningGroup.Controls.Add(lbRunning);
        lbRunning.DoubleClick += (s, e) => AddRuleFromRunning();

        listLayout.Controls.Add(ruleGroup, 0, 0);
        listLayout.Controls.Add(btnAdd, 1, 0);
        listLayout.Controls.Add(runningGroup, 2, 0);

        // 3. Affinity詳細設定エリア (全選択・全解除ボタン追加)
        var affinityGroup = new GroupBox { Text = "Affinity詳細設定", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var affinityInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        affinityInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); // ボタン行
        affinityInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // チェックボックス行
        affinityInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));  // テキスト・更新ボタン行

        var quickSelectPanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var btnAll = new Button { Text = "全選択", Width = 80 };
        var btnNone = new Button { Text = "全解除 (CPU 0のみ)", Width = 130 };
        btnAll.Click += (s, e) => SetAllCores(true);
        btnNone.Click += (s, e) => SetAllCores(false);

        // 実態を反映、をクリック
        btnSyncFromActual.Click += (s, e) => {
            if (lbRules.SelectedItem == null) return;

            string name = lbRules.SelectedItem.ToString()!.Split('=')[0].Trim();
            var processes = Process.GetProcessesByName(name);
            if (processes.Length == 0) return;

            // 1. 実態の値を読み取る
            long actualMask = (long)processes[0].ProcessorAffinity;
            string cpuList = AffinityEngine.MaskToCpuList(actualMask);

            // 2. UI（テキストボックス）に反映
            txtCpu.Text = cpuList;

            // 3. メモリとストレージを更新（既存の更新ロジックを再利用）
            UpdateSelectedRule();

            MessageBox.Show($"実態の設定({cpuList})をルールに上書きしました。");
            btnSyncFromActual.Visible = false;
        };
        quickSelectPanel.Controls.AddRange(new Control[] { btnAll, btnNone, btnSyncFromActual });

        var bottomAction = new FlowLayoutPanel { Dock = DockStyle.Fill };
        bottomAction.Controls.AddRange(new Control[] { new Label { Text = "適用CPU:", AutoSize = true }, txtCpu, btnUpdateRule });

        btnUpdateRule.Click += (s, e) => UpdateSelectedRule();

        affinityInner.Controls.Add(quickSelectPanel, 0, 0);
        affinityInner.Controls.Add(cpuPanel, 0, 1);
        affinityInner.Controls.Add(bottomAction, 0, 2);
        affinityGroup.Controls.Add(affinityInner);

        mainLayout.Controls.Add(presetPanel, 0, 0);
        mainLayout.Controls.Add(listLayout, 0, 1);
        mainLayout.Controls.Add(affinityGroup, 0, 2);

        Controls.Add(mainLayout);
    }

    // --- 新規ロジック：全選択・全解除 ---
    /// <summary>
    /// 全選択・全解除
    /// </summary>
    /// <param name="all"></param>
    private void SetAllCores(bool all)
    {
        isUpdating = true;
        if (all)
        {
            foreach (var cb in cpuCheckBoxes) cb.Checked = true;
        }
        else
        {
            foreach (var cb in cpuCheckBoxes) cb.Checked = false;
            if (cpuCheckBoxes.Count > 0) cpuCheckBoxes[0].Checked = true; // CPU 0のみ残す
        }
        isUpdating = false;
        UpdateTextFromChecks();
    }

    private void InitializeCpuCheckBoxes()
    {
        cpuPanel.Controls.Clear();
        cpuCheckBoxes.Clear();
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            var cb = new CheckBox { Text = $"CPU {i}", Width = 70, Tag = i };
            cb.CheckedChanged += (s, e) => UpdateTextFromChecks();
            cpuCheckBoxes.Add(cb);
            cpuPanel.Controls.Add(cb);
        }
    }

    // --- 新規ロジック：削除時の初期化 ---
    /// <summary>
    /// 削除時に初期化する
    /// </summary>
    private void RemoveRuleAndResetAffinity()
    {
        if (lbRules.SelectedItem == null) return;
        string line = lbRules.SelectedItem.ToString()!;
        string name = line.Split('=')[0].Trim();

        // 1. Affinityを全コア開放 (OSデフォルト) に戻す
        long allMask = AffinityEngine.GetFullMask();// (1L << Environment.ProcessorCount) - 1;
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
        {
            try { 
                //p.ProcessorAffinity = (IntPtr)allMask;
                AffinityEngine.ApplyAffinity(p, allMask);
            } catch { }

        }

        // 2. ファイルから削除
        var lines = File.ReadAllLines(configPath).Where(l => l != line).ToList();
        File.WriteAllLines(configPath, lines);

        // 【重要】メモリ上の辞書を更新（これをしないと監視対象に残ったままになる）
        Program.LoadRules();

        RefreshRules();
        MessageBox.Show($"{name} のルールを削除し、Affinityを全コア開放に戻しました。");
    }

    // --- ロジック：正規化と比較 ---
    /// <summary>
    /// アフィニティの表記修正
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private string NormalizeAffinity(string input)
    {
        var cores = new HashSet<int>();
        foreach (var part in input.Split(','))
        {
            string p = part.Trim();
            if (p.Contains("-"))
            {
                var r = p.Split('-');
                if (r.Length == 2 && int.TryParse(r[0], out int s) && int.TryParse(r[1], out int e))
                    for (int i = s; i <= e; i++) cores.Add(i);
            }
            else if (int.TryParse(p, out int v)) cores.Add(v);
        }
        return string.Join(",", cores.OrderBy(n => n));
    }
    /// <summary>
    /// 現状とプリセットの一致判定
    /// </summary>
    private void CheckPresetMatch()
    {
        if (isUpdating) return;

        // 現在のリストボックス（affinity.iniの状態）をシリアライズ
        var currentRules = lbRules.Items.Cast<string>()
            .Select(line => {
                var p = line.Split('=');
                return p.Length == 2 ? $"{p[0].Trim().ToLower()}={NormalizeAffinity(p[1])}" : "";
            })
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s)
            .ToList();

        string currentContent = string.Join("\n", currentRules);
        string? foundName = null;

        foreach (var file in Directory.GetFiles(presetsDir, "*.ini"))
        {
            var presetRules = File.ReadAllLines(file)
                .Select(line => {
                    var p = line.Split('=');
                    return p.Length == 2 ? $"{p[0].Trim().ToLower()}={NormalizeAffinity(p[1])}" : "";
                })
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s)
                .ToList();

            if (currentContent == string.Join("\n", presetRules))
            {
                foundName = Path.GetFileNameWithoutExtension(file);
                break;
            }
        }

        isUpdating = true;
        if (foundName != null)
        {
            lblStatus.Text = $"● 構成一致: {foundName}";
            lblStatus.ForeColor = Color.Green;
            cbPresets.SelectedItem = foundName;
        }
        else
        {
            lblStatus.Text = "● 未保存の構成 (カスタム)";
            lblStatus.ForeColor = Color.Orange;
            cbPresets.SelectedIndex = -1;
        }
        isUpdating = false;
    }

    // --- 各種操作 ---

    private void SaveCurrentAsPreset()
    {
        string name = Microsoft.VisualBasic.Interaction.InputBox("プロファイル名を入力してください", "プリセット保存", "MyProfile").Trim();
        if (string.IsNullOrEmpty(name)) return;

        string path = Path.Combine(presetsDir, name + ".ini");
        File.WriteAllLines(path, lbRules.Items.Cast<string>());

        RefreshPresets();
        CheckPresetMatch();
    }

    private void LoadSelectedPreset()
    {
        if (isUpdating || cbPresets.SelectedItem == null) return;
        string path = Path.Combine(presetsDir, cbPresets.SelectedItem.ToString() + ".ini");
        if (File.Exists(path))
        {
            File.Copy(path, configPath, true);
            RefreshRules(); // これにより CheckPresetMatch も呼ばれる
        }
    }

    private void DeletePreset()
    {
        if (cbPresets.SelectedItem == null) return;
        if (MessageBox.Show("このプリセットを削除しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            File.Delete(Path.Combine(presetsDir, cbPresets.SelectedItem.ToString() + ".ini"));
            RefreshPresets();
            CheckPresetMatch();
        }
    }

    private void UpdateTextFromChecks()
    {
        if (isUpdating) return;
        isUpdating = true;
        var selected = cpuCheckBoxes.Where(c => c.Checked).Select(c => (int)c.Tag).OrderBy(n => n);
        txtCpu.Text = string.Join(",", selected);
        isUpdating = false;
    }

    private void UpdateChecksFromText()
    {
        if (isUpdating) return;
        isUpdating = true;
        var cores = NormalizeAffinity(txtCpu.Text).Split(',')
                        .Select(s => int.TryParse(s, out int v) ? v : -1).ToHashSet();
        foreach (var cb in cpuCheckBoxes) cb.Checked = cores.Contains((int)cb.Tag);
        isUpdating = false;
    }

    private void RefreshPresets()
    {
        isUpdating = true;
        cbPresets.Items.Clear();
        foreach (var f in Directory.GetFiles(presetsDir, "*.ini"))
            cbPresets.Items.Add(Path.GetFileNameWithoutExtension(f));
        isUpdating = false;
    }

    private void RefreshRules()
    {
        lbRules.Items.Clear();
        if (File.Exists(configPath))
            lbRules.Items.AddRange(File.ReadAllLines(configPath).Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l)).ToArray());
        CheckPresetMatch();
    }

    private void RefreshRunningProcesses()
    {
        lbRunning.Items.Clear();
        lbRunning.Items.AddRange(Process.GetProcesses().Select(p => p.ProcessName).Distinct().OrderBy(n => n).ToArray());
    }

    private void OnRuleSelected()
    {
        if (lbRules.SelectedItem == null) return;
        var parts = lbRules.SelectedItem.ToString()!.Split('=');
        if (parts.Length == 2)
        {//txtCpu.Text = NormalizeAffinity(parts[1]);
            long mask = AffinityEngine.CpuListToMask(parts[1]);
            txtCpu.Text = AffinityEngine.MaskToCpuList(mask);
        }
    }

    private void AddRuleFromRunning()
    {
        if (lbRunning.SelectedItem == null) return;
        string name = lbRunning.SelectedItem.ToString()!;
        long newMask = AffinityEngine.CpuListToMask(txtCpu.Text);

        // ファイル保存
        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
        lines.RemoveAll(l => l.StartsWith(name + "="));
        lines.Add($"{name}={txtCpu.Text}");
        File.WriteAllLines(configPath, lines);

        // メモリ更新
        Program.LoadRules();

        // 【即時適用】
        foreach (var p in Process.GetProcessesByName(name))
        {
            AffinityEngine.ApplyAffinity(p, newMask);
        }

        RefreshRules();
    }

    private void UpdateSelectedRule()
    {
        if (lbRules.SelectedItem == null) return;

        // 1. 名前と新しいマスクを取得
        string name = lbRules.SelectedItem.ToString()!.Split('=')[0].Trim();
        long newMask = AffinityEngine.CpuListToMask(txtCpu.Text);

        // 2. affinity.ini ファイルを更新
        var lines = File.ReadAllLines(configPath).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(name + "="))
                lines[i] = $"{name}={txtCpu.Text}";
        }
        File.WriteAllLines(configPath, lines);

        // 3. メモリ上の監視用辞書を更新 (重要)
        Program.LoadRules();

        // 4. 【即時適用】現在実行中の同名プロセスすべてに新しいマスクを適用
        var targets = Process.GetProcessesByName(name);
        foreach (var p in targets)
        {
            try
            {
                AffinityEngine.ApplyAffinity(p, newMask);
            }
            catch (Exception ex)
            {
                Log.Warning("即時適用失敗: {ProcessName} - {Msg}", name, ex.Message);
            }
        }

        RefreshRules();
        lblStatus.Text = $"● {name} に新設定を即時適用しました";
        lblStatus.ForeColor = Color.Blue;
    }

    private void RemoveRule()
    {
        if (lbRules.SelectedItem == null) return;
        var lines = File.ReadAllLines(configPath).Where(l => l != lbRules.SelectedItem.ToString()).ToList();
        File.WriteAllLines(configPath, lines);
        RefreshRules();
    }

    /// <summary>
    /// メモリ上のルールと、現在のプロセスの実態に相違がないかチェックする
    /// </summary>
    private void CheckAffinityMismatch()
    {
        if (lbRules.SelectedItem == null) return;

        // 現在選択中のルール名を取得
        string name = lbRules.SelectedItem.ToString()!.Split('=')[0].Trim();

        // 1. メモリ上の期待値を取得
        if (!Program.GetRules().TryGetValue(name, out long expectedMask)) return;

        // 2. 実態（実行中のプロセス）の状態を確認
        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 0) return;

        // 最初の1つのプロセスで比較
        long actualMask = (long)processes[0].ProcessorAffinity;

        if (expectedMask != actualMask)
        {
            // 相違あり
            OnMismatchDetected(name, expectedMask, actualMask);
        }
        else
        {
            // 相違なし（解消された場合）
            OnMismatchResolved();
        }
    }

    private void OnMismatchDetected(string name, long expected, long actual)
    {
        hasAffinityMismatch = true;
        lblStatus.Text = $"⚠️ 外部変更検知: {name} (期待:0x{expected:X} / 実態:0x{actual:X})";
        lblStatus.ForeColor = Color.Red;

        // ここでボタンを出現させる！
        btnSyncFromActual.Visible = true;
        btnSyncFromActual.Text = $"実態(0x{actual:X})を反映";
        
        // ここでトレイアイコンへの通知指示を出す
        Program.NotifyMismatch(name);
    }

    private void OnMismatchResolved()
    {
        hasAffinityMismatch = false;
        lblStatus.Text = "● 正常に同期中";
        lblStatus.ForeColor = Color.Green;

        // 相違がなくなったらボタンを隠す
        btnSyncFromActual.Visible = false;
    }

    private void UpdateRuleFromActual()
    {
        // 実態を取得して txtCpu.Text を更新し、そのまま UpdateSelectedRule() を呼ぶフロー
    }
}