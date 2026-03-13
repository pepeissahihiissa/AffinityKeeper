namespace AffinityKeeper;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

public class ConfigForm : Form
{
    private ListBox lbRules = new ListBox() { Dock = DockStyle.Fill };
    private ListBox lbRunning = new ListBox() { Dock = DockStyle.Fill };
    private TextBox txtCpu = new TextBox() { Text = "0-3", PlaceholderText = "CPU List (ex: 0,1,4-6)" };
    private Button btnUpdate = new Button { Text = "選択中のルールを更新", Width = 150, Enabled = false };
    private ComboBox cbPresets = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private string presetsPath = "presets.ini";

    // CPUチェックボックスを保持するリスト
    private List<CheckBox> cpuCheckBoxes = new List<CheckBox>();
    private FlowLayoutPanel cpuPanel = new FlowLayoutPanel() { Dock = DockStyle.Fill, AutoScroll = true };

    public ConfigForm()
    {
        var btnSavePreset = new Button { Text = "保存", Width = 50 };
        var lblPreset = new Label { Text = "プリセット:", Margin = new Padding(0, 5, 0, 0), Width = 60 };

        this.Text = "Affinity Keeper Settings";
        this.Size = new Size(800, 600); // 少し横幅を広げます

        // --- レイアウト構成の修正 ---
        var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // 行の高さ設定：上段（リスト）は伸びる、中段（CPU）と下段（ボタン）は中身に合わせる
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 余ったスペースはここが使う
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // CPUパネルは中身に合わせる
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 操作パネルも中身に合わせる

        // 左側：登録済みのルール
        var leftPanel = new GroupBox { Text = "登録済みのルール", Dock = DockStyle.Fill };
        leftPanel.Controls.Add(lbRules);
        mainLayout.Controls.Add(leftPanel, 0, 0);

        // 右側：実行中のプロセス
        var rightPanel = new GroupBox { Text = "実行中のプロセス (ダブルクリックで追加)", Dock = DockStyle.Fill };
        rightPanel.Controls.Add(lbRunning);
        mainLayout.Controls.Add(rightPanel, 1, 0);

        // 中段：CPU選択パネル（タスクマネージャー風）
        var cpuGroupBox = new GroupBox { Text = "CPU Affinity 選択", Dock = DockStyle.Fill };
        cpuGroupBox.Controls.Add(cpuPanel);
        mainLayout.Controls.Add(cpuGroupBox, 0, 1);
        mainLayout.SetColumnSpan(cpuGroupBox, 2);

        // --- 下部操作パネルの修正 ---
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,              // 中身に合わせて伸びる
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(5)
        };
        var btnRemove = new Button { Text = "選択したルールを削除", Width = 150 };
        var btnRefresh = new Button { Text = "プロセス一覧更新", Width = 120 };

        // ボタン類を配置
        bottomPanel.Controls.Add(new Label { Text = "プリセット:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        bottomPanel.Controls.Add(cbPresets);
        bottomPanel.Controls.Add(btnSavePreset);
        bottomPanel.Controls.Add(new Label { Text = " | ", AutoSize = true, Margin = new Padding(5, 8, 5, 0) });
        bottomPanel.Controls.Add(new Label { Text = "適用CPU文字列:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        bottomPanel.Controls.Add(txtCpu);
        bottomPanel.Controls.Add(btnUpdate);
        bottomPanel.Controls.Add(btnRemove);
        bottomPanel.Controls.Add(btnRefresh);

        mainLayout.Controls.Add(bottomPanel, 0, 2);
        mainLayout.SetColumnSpan(bottomPanel, 2);

        this.Controls.Add(mainLayout);

        // CPUチェックボックスの生成
        InitializeCpuCheckBoxes();

        // イベント登録
        btnRefresh.Click += (s, e) => RefreshRunningProcesses();
        btnRemove.Click += (s, e) => RemoveRule();
        lbRunning.DoubleClick += (s, e) => AddRuleFromRunning();
        lbRules.SelectedIndexChanged += (s, e) => OnRuleSelected();

        // txtCpuを手動で書き換えた時もチェックボックスに反映させる
        txtCpu.TextChanged += (s, e) => UpdateChecksFromText();

        // イベント登録
        btnUpdate.Click += (s, e) => UpdateSelectedRule();
        lbRules.SelectedIndexChanged += (s, e) => {
            OnRuleSelected();
            btnUpdate.Enabled = (lbRules.SelectedItem != null); // 選択中のみ有効化
        };

        // イベント登録
        btnSavePreset.Click += (s, e) => SaveCurrentAsPreset();
        cbPresets.SelectedIndexChanged += (s, e) => LoadSelectedPreset();

        RefreshPresets();
        RefreshRules();
        RefreshRunningProcesses();
    }

    private void InitializeCpuCheckBoxes()
    {
        int cpuCount = Environment.ProcessorCount;
        for (int i = 0; i < cpuCount; i++)
        {
            var cb = new CheckBox { Text = $"CPU {i}", Width = 70, Tag = i };
            cb.CheckedChanged += (s, e) => UpdateTextFromChecks();
            cpuCheckBoxes.Add(cb);
            cpuPanel.Controls.Add(cb);
        }
    }

    // チェックボックスの状態から "0,1,2-4" 形式の文字列を生成
    private bool isUpdating = false;
    private void UpdateTextFromChecks()
    {
        if (isUpdating) return;
        isUpdating = true;

        var selectedCores = cpuCheckBoxes.Where(cb => cb.Checked).Select(cb => (int)cb.Tag).ToList();
        txtCpu.Text = ConvertListToRangeString(selectedCores);

        isUpdating = false;
    }

    // 文字列からチェックボックスの状態を復元
    private void UpdateChecksFromText()
    {
        if (isUpdating) return;
        isUpdating = true;

        var cores = ParseRangeString(txtCpu.Text);
        foreach (var cb in cpuCheckBoxes)
        {
            cb.Checked = cores.Contains((int)cb.Tag);
        }

        isUpdating = false;
    }

    // 左側のリストでルールを選択した時
    private void OnRuleSelected()
    {
        if (lbRules.SelectedItem == null) return;
        string line = lbRules.SelectedItem.ToString();
        var parts = line.Split('=');
        if (parts.Length == 2)
        {
            txtCpu.Text = parts[1]; // 文字列を入れると UpdateChecksFromText が走る
        }
    }

    // ヘルパー：リストを "0,1,3-5" 形式に変換
    private string ConvertListToRangeString(List<int> cores)
    {
        if (!cores.Any()) return "";
        cores.Sort();
        var ranges = new List<string>();
        for (int i = 0, j = 0; i < cores.Count; i = j)
        {
            for (j = i + 1; j < cores.Count && cores[j] == cores[j - 1] + 1; j++) ;
            ranges.Add(j - i > 1 ? $"{cores[i]}-{cores[j - 1]}" : cores[i].ToString());
        }
        return string.Join(",", ranges);
    }

    // ヘルパー： "0,1,3-5" 形式をリストに変換
    private HashSet<int> ParseRangeString(string text)
    {
        var result = new HashSet<int>();
        foreach (var part in text.Split(','))
        {
            if (part.Contains("-"))
            {
                var range = part.Split('-');
                if (int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                    for (int i = start; i <= end; i++) result.Add(i);
            }
            else if (int.TryParse(part, out int val)) result.Add(val);
        }
        return result;
    }

    private void UpdateSelectedRule()
    {
        if (lbRules.SelectedItem == null) return;

        // 現在選択されている行 (例: "opera=0-3") からプロセス名を取り出す
        string selectedLine = lbRules.SelectedItem.ToString();
        string procName = selectedLine.Split('=')[0].Trim();

        if (string.IsNullOrEmpty(procName)) return;

        // ファイルを読み込んで該当行を置換
        var lines = File.Exists("affinity.ini") ? File.ReadAllLines("affinity.ini").ToList() : new List<string>();

        // 既存のルールを検索して置換（あるいは削除して追加）
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(procName + "="))
            {
                lines[i] = $"{procName}={txtCpu.Text}";
                found = true;
                break;
            }
        }

        if (!found) lines.Add($"{procName}={txtCpu.Text}");

        File.WriteAllLines("affinity.ini", lines);

        // 選択状態を維持しつつリストを更新
        int currentIndex = lbRules.SelectedIndex;
        RefreshRules();
        if (lbRules.Items.Count > currentIndex) lbRules.SelectedIndex = currentIndex;

        Log.Information("Rule updated: {Exe} -> {Mask}", procName, txtCpu.Text);
    }

    private void RefreshPresets()
    {
        cbPresets.Items.Clear();
        if (!File.Exists(presetsPath)) return;

        var lines = File.ReadAllLines(presetsPath);
        foreach (var line in lines)
        {
            var parts = line.Split('=');
            if (parts.Length == 2) cbPresets.Items.Add(parts[0].Trim());
        }
    }

    private void SaveCurrentAsPreset()
    {
        if (string.IsNullOrWhiteSpace(txtCpu.Text)) return;

        // 簡易的な名前入力ダイアログを表示
        string presetName = Microsoft.VisualBasic.Interaction.InputBox(
            "プリセット名を入力してください", "プリセット保存", "New Preset");

        if (string.IsNullOrWhiteSpace(presetName)) return;

        var lines = File.Exists(presetsPath) ? File.ReadAllLines(presetsPath).ToList() : new List<string>();
        lines.RemoveAll(l => l.StartsWith(presetName + "="));
        lines.Add($"{presetName}={txtCpu.Text}");

        File.WriteAllLines(presetsPath, lines);
        RefreshPresets();
        cbPresets.SelectedItem = presetName;

        Log.Information("Preset saved: {Name} = {Mask}", presetName, txtCpu.Text);
    }

    private void LoadSelectedPreset()
    {
        if (cbPresets.SelectedItem == null) return;
        string selectedName = cbPresets.SelectedItem.ToString();

        var lines = File.ReadAllLines(presetsPath);
        foreach (var line in lines)
        {
            var parts = line.Split('=');
            if (parts.Length == 2 && parts[0].Trim() == selectedName)
            {
                txtCpu.Text = parts[1].Trim(); // これでチェックボックスも自動連動します
                break;
            }
        }
    }

    // --- 以降、RefreshRules, RefreshRunningProcesses, AddRuleFromRunning, RemoveRule は既存と同じ ---
    private void RefreshRules() { /* ...省略... */ lbRules.Items.Clear(); if (File.Exists("affinity.ini")) lbRules.Items.AddRange(File.ReadAllLines("affinity.ini").Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l)).ToArray()); }
    private void RefreshRunningProcesses() { lbRunning.Items.Clear(); var processes = Process.GetProcesses().Select(p => p.ProcessName).Distinct().OrderBy(n => n); lbRunning.Items.AddRange(processes.ToArray()); }
    private void AddRuleFromRunning() { if (lbRunning.SelectedItem == null) return; string procName = lbRunning.SelectedItem.ToString(); string newRule = $"{procName}={txtCpu.Text}"; var lines = File.Exists("affinity.ini") ? File.ReadAllLines("affinity.ini").ToList() : new List<string>(); lines.RemoveAll(l => l.StartsWith(procName + "=")); lines.Add(newRule); File.WriteAllLines("affinity.ini", lines); RefreshRules(); }
    private void RemoveRule() { if (lbRules.SelectedItem == null) return; string selected = lbRules.SelectedItem.ToString(); var lines = File.ReadAllLines("affinity.ini").ToList(); lines.Remove(selected); File.WriteAllLines("affinity.ini", lines); RefreshRules(); }
}