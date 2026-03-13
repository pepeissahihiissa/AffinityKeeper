namespace AffinityKeeper;
    using System.Diagnostics;

    public class ConfigForm : Form
    {
        private ListBox lbRules = new ListBox() { Dock = DockStyle.Fill };
        private ListBox lbRunning = new ListBox() { Dock = DockStyle.Fill };
        private TextBox txtCpu = new TextBox() { Text = "0-3", PlaceholderText = "CPU List (ex: 0,1,4-6)" };

        public ConfigForm()
        {
            this.Text = "Affinity Keeper Settings";
            this.Size = new Size(600, 400);

            // レイアウト構成
            var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // 左側：現在の設定
            var leftPanel = new GroupBox { Text = "登録済みのルール", Dock = DockStyle.Fill };
            leftPanel.Controls.Add(lbRules);
            mainLayout.Controls.Add(leftPanel, 0, 0);

            // 右側：実行中のプロセス
            var rightPanel = new GroupBox { Text = "実行中のプロセス (ダブルクリックで追加)", Dock = DockStyle.Fill };
            rightPanel.Controls.Add(lbRunning);
            mainLayout.Controls.Add(rightPanel, 1, 0);

            // 下部：操作パネル
            var bottomPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            var btnRemove = new Button { Text = "選択したルールを削除", Width = 150 };
            var btnRefresh = new Button { Text = "プロセス一覧更新", Width = 120 };
            bottomPanel.Controls.Add(new Label { Text = "適用CPU:", Margin = new Padding(0, 5, 0, 0) });
            bottomPanel.Controls.Add(txtCpu);
            bottomPanel.Controls.Add(btnRemove);
            bottomPanel.Controls.Add(btnRefresh);
            mainLayout.Controls.Add(bottomPanel, 0, 1);
            mainLayout.SetColumnSpan(bottomPanel, 2);

            this.Controls.Add(mainLayout);

            // イベント登録
            btnRefresh.Click += (s, e) => RefreshRunningProcesses();
            btnRemove.Click += (s, e) => RemoveRule();
            lbRunning.DoubleClick += (s, e) => AddRuleFromRunning();

            // 初期データ読み込み
            RefreshRules();
            RefreshRunningProcesses();
        }

        private void RefreshRules()
        {
            lbRules.Items.Clear();
            if (File.Exists("affinity.ini"))
            {
                lbRules.Items.AddRange(File.ReadAllLines("affinity.ini").Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l)).ToArray());
            }
        }

        private void RefreshRunningProcesses()
        {
            lbRunning.Items.Clear();
            var processes = Process.GetProcesses()
                .Select(p => p.ProcessName)
                .Distinct()
                .OrderBy(n => n);
            lbRunning.Items.AddRange(processes.ToArray());
        }

        private void AddRuleFromRunning()
        {
            if (lbRunning.SelectedItem == null) return;
            string procName = lbRunning.SelectedItem.ToString();
            string newRule = $"{procName}={txtCpu.Text}";

            // 重複チェックして追加
            var lines = File.Exists("affinity.ini") ? File.ReadAllLines("affinity.ini").ToList() : new List<string>();
            lines.RemoveAll(l => l.StartsWith(procName + "=")); // 既存があれば削除
            lines.Add(newRule);

            File.WriteAllLines("affinity.ini", lines);
            RefreshRules();
            // FileSystemWatcherが検知して自動適用される
        }

        private void RemoveRule()
        {
            if (lbRules.SelectedItem == null) return;
            string selected = lbRules.SelectedItem.ToString();
            var lines = File.ReadAllLines("affinity.ini").ToList();
            lines.Remove(selected);
            File.WriteAllLines("affinity.ini", lines);
            RefreshRules();
        }
    }