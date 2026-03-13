namespace AffinityKeeper;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Serilog;

public class ConfigForm : Form
{
    // UIコンポーネント
    private ListBox lbRules = new ListBox() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9) };
    private ListBox lbRunning = new ListBox() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9) };
    private TextBox txtCpu = new TextBox() { Width = 200, PlaceholderText = "例: 0,1,2,4-6" };
    private ComboBox cbPresets = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private Label lblStatus = new Label { Text = "● 状態確認中...", ForeColor = Color.Gray, Width = 300, AutoSize = true };
    private Button btnUpdateRule = new Button { Text = "選択中のルールを更新", Width = 150, Enabled = false };

    private List<CheckBox> cpuCheckBoxes = new List<CheckBox>();
    private FlowLayoutPanel cpuPanel = new FlowLayoutPanel() { Dock = DockStyle.Fill, AutoScroll = true };

    private readonly string configPath = "affinity.ini";
    private readonly string presetsDir = "presets";
    private bool isUpdating = false;

    public ConfigForm()
    {
        this.Text = "Affinity Keeper Settings (Profile Mode)";
        this.Size = new Size(1000, 750);
        this.MinimumSize = new Size(800, 600);

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
            btnUpdateRule.Enabled = (lbRules.SelectedItem != null);
        };

        // 初期ロード
        RefreshPresets();
        RefreshRules();
        RefreshRunningProcesses();
    }

    private void InitializeMainLayout()
    {
        var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // プリセット操作
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // メインリスト
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220)); // CPU設定

        // 1. プリセットエリア
        var presetPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 12, 0, 0) };
        var btnSavePreset = new Button { Text = "現在の構成を保存", Width = 120 };
        var btnDeletePreset = new Button { Text = "削除", Width = 60 };

        btnSavePreset.Click += (s, e) => SaveCurrentAsPreset();
        btnDeletePreset.Click += (s, e) => DeletePreset();

        presetPanel.Controls.AddRange(new Control[] {
            new Label { Text = "プロファイル:", AutoSize = true, Margin = new Padding(0, 5, 5, 0) },
            cbPresets, btnSavePreset, btnDeletePreset, lblStatus
        });

        // 2. リストエリア (3カラム)
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(5) };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        // 左: 登録済みルール
        var btnRemoveRule = new Button { Text = "選択したルールを削除", Dock = DockStyle.Bottom, Height = 30 };
        btnRemoveRule.Click += (s, e) => RemoveRule();
        var ruleGroup = new GroupBox { Text = "登録済みルール (affinity.ini)", Dock = DockStyle.Fill };
        ruleGroup.Controls.Add(lbRules);
        ruleGroup.Controls.Add(btnRemoveRule);

        // 中央: 追加ボタン
        var btnAdd = new Button { Text = "◀ 追加", Dock = DockStyle.Fill, Margin = new Padding(0, 100, 0, 100), BackColor = Color.LightSkyBlue };
        btnAdd.Click += (s, e) => AddRuleFromRunning();

        // 右: 実行中のプロセス
        var btnRefresh = new Button { Text = "プロセス一覧更新", Dock = DockStyle.Bottom, Height = 30 };
        btnRefresh.Click += (s, e) => RefreshRunningProcesses();
        var runningGroup = new GroupBox { Text = "実行中のプロセス (ダブルクリックで追加)", Dock = DockStyle.Fill };
        runningGroup.Controls.Add(lbRunning);
        runningGroup.Controls.Add(btnRefresh);
        lbRunning.DoubleClick += (s, e) => AddRuleFromRunning();

        listLayout.Controls.Add(ruleGroup, 0, 0);
        listLayout.Controls.Add(btnAdd, 1, 0);
        listLayout.Controls.Add(runningGroup, 2, 0);

        // 3. Affinity設定エリア
        var affinityGroup = new GroupBox { Text = "Affinity詳細設定", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var affinityInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        affinityInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        affinityInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

        var bottomAction = new FlowLayoutPanel { Dock = DockStyle.Fill };
        btnUpdateRule.Click += (s, e) => UpdateSelectedRule();
        bottomAction.Controls.AddRange(new Control[] {
            new Label { Text = "適用CPU文字列:", AutoSize = true, Margin = new Padding(0, 8, 5, 0) },
            txtCpu, btnUpdateRule
        });

        affinityInner.Controls.Add(cpuPanel, 0, 0);
        affinityInner.Controls.Add(bottomAction, 0, 1);
        affinityGroup.Controls.Add(affinityInner);

        mainLayout.Controls.Add(presetPanel, 0, 0);
        mainLayout.Controls.Add(listLayout, 0, 1);
        mainLayout.Controls.Add(affinityGroup, 0, 2);

        this.Controls.Add(mainLayout);
    }

    // --- ロジック：正規化と比較 ---

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

    private void InitializeCpuCheckBoxes()
    {
        cpuPanel.Controls.Clear();
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            var cb = new CheckBox { Text = $"CPU {i}", Width = 70, Tag = i };
            cb.CheckedChanged += (s, e) => UpdateTextFromChecks();
            cpuCheckBoxes.Add(cb);
            cpuPanel.Controls.Add(cb);
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
        var cores = NormalizeAffinity(txtCpu.Text).Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToHashSet();
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
        if (parts.Length == 2) txtCpu.Text = NormalizeAffinity(parts[1]);
    }

    private void AddRuleFromRunning()
    {
        if (lbRunning.SelectedItem == null) return;
        string name = lbRunning.SelectedItem.ToString()!;
        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
        lines.RemoveAll(l => l.StartsWith(name + "="));
        lines.Add($"{name}={NormalizeAffinity(txtCpu.Text)}");
        File.WriteAllLines(configPath, lines);
        RefreshRules();
    }

    private void UpdateSelectedRule()
    {
        if (lbRules.SelectedItem == null) return;
        string name = lbRules.SelectedItem.ToString()!.Split('=')[0].Trim();
        var lines = File.ReadAllLines(configPath).ToList();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith(name + "=")) lines[i] = $"{name}={NormalizeAffinity(txtCpu.Text)}";
        File.WriteAllLines(configPath, lines);
        RefreshRules();
    }

    private void RemoveRule()
    {
        if (lbRules.SelectedItem == null) return;
        var lines = File.ReadAllLines(configPath).Where(l => l != lbRules.SelectedItem.ToString()).ToList();
        File.WriteAllLines(configPath, lines);
        RefreshRules();
    }
}