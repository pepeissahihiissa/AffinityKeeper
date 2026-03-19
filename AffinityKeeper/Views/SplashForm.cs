namespace AffinityKeeper.Views;


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// スプラッシュ画面のクラス
/// </summary>
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

    /// <summary>
    /// ステータス更新
    /// </summary>
    /// <param name="text"></param>
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
