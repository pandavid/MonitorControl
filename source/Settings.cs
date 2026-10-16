using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ============================================================================
//  全局快捷键（切换输入源）+ 设置窗口
//  用途：显示器切到别的输入源、本机画面看不见时，用快捷键把显示器切回本机。
//  设置存 %APPDATA%\MonitorSwitch\settings.ini
// ============================================================================

internal sealed class HotkeyConfig
{
    public bool Enabled = true;
    /// 每个热键的修饰键位（MOD_ALT=1 MOD_CONTROL=2 MOD_SHIFT=4 MOD_WIN=8）
    /// 默认用 Ctrl+Shift+1/2/3：实测这台机器上 Ctrl+Alt+1、Ctrl+Win+1..3、Alt+Win+1..3 都被占用
    public int[] Mods = new int[] { 6, 6, 6 };
    public int[] Vks = new int[] { 0x31, 0x32, 0x33 };

    private static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitch"); }
    }
    private static string IniPath { get { return Path.Combine(Dir, "settings.ini"); } }

    public static HotkeyConfig Load()
    {
        HotkeyConfig c = new HotkeyConfig();
        try
        {
            if (!File.Exists(IniPath)) return c;
            foreach (string line in File.ReadAllLines(IniPath, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "hotkeys") c.Enabled = (v == "1");
                else if (k == "hk_dp1" || k == "hk_hdmi1" || k == "hk_hdmi2")
                {
                    string[] p = v.Split(',');
                    int mods, vk;
                    if (p.Length == 2 && int.TryParse(p[0], out mods) && int.TryParse(p[1], out vk))
                    {
                        int i = (k == "hk_dp1") ? 0 : (k == "hk_hdmi1" ? 1 : 2);
                        c.Mods[i] = mods;
                        c.Vks[i] = vk;
                    }
                }
            }
        }
        catch { }
        return c;
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("hotkeys=" + (Enabled ? "1" : "0"));
            sb.AppendLine("hk_dp1=" + Mods[0] + "," + Vks[0]);
            sb.AppendLine("hk_hdmi1=" + Mods[1] + "," + Vks[1]);
            sb.AppendLine("hk_hdmi2=" + Mods[2] + "," + Vks[2]);
            File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public void ResetDefaults()
    {
        Enabled = true;
        Mods = new int[] { 6, 6, 6 };
        Vks = new int[] { 0x31, 0x32, 0x33 };
    }

    public static string Describe(int mods, int vk)
    {
        if (vk == 0) return "未设置";
        StringBuilder sb = new StringBuilder();
        if ((mods & 0x2) != 0) sb.Append("Ctrl+");
        if ((mods & 0x1) != 0) sb.Append("Alt+");
        if ((mods & 0x4) != 0) sb.Append("Shift+");
        if ((mods & 0x8) != 0) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    public static string KeyName(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        switch (vk)
        {
            case 0x20: return "空格";
            case 0x0D: return "回车";
            case 0x09: return "Tab";
            case 0x25: return "左方向";
            case 0x26: return "上方向";
            case 0x27: return "右方向";
            case 0x28: return "下方向";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x24: return "Home";
            case 0x23: return "End";
            case 0x2C: return "PrintScreen";
            default: return "0x" + vk.ToString("X2");
        }
    }

    public static void FromKeys(Keys data, out int mods, out int vk)
    {
        mods = 0;
        if ((data & Keys.Control) == Keys.Control) mods |= 0x2;
        if ((data & Keys.Alt) == Keys.Alt) mods |= 0x1;
        if ((data & Keys.Shift) == Keys.Shift) mods |= 0x4;
        vk = (int)(data & Keys.KeyCode);
    }
}

internal static class HotkeyManager
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;

    public static bool Register(IntPtr hWnd, int id, int mods, int vk)
    {
        if (vk == 0) return false;
        try { return RegisterHotKey(hWnd, id, mods | MOD_NOREPEAT, vk); }
        catch { return false; }
    }

    public static void Unregister(IntPtr hWnd, int id)
    {
        try { UnregisterHotKey(hWnd, id); }
        catch { }
    }
}

internal sealed class SettingsDialog : Form
{
    private static readonly Color CBg = Color.FromArgb(26, 28, 32);
    private static readonly Color CCard = Color.FromArgb(38, 41, 47);
    private static readonly Color CCardAlt = Color.FromArgb(52, 56, 64);
    private static readonly Color CText = Color.FromArgb(236, 239, 244);
    private static readonly Color CDim = Color.FromArgb(146, 154, 165);
    private static readonly Color CAccent = Color.FromArgb(86, 190, 120);
    private static readonly Color CWarn = Color.FromArgb(242, 184, 66);

    private static readonly string[] Names = new string[] { "回到 DisplayPort-1（本机）", "切换到 HDMI-1", "切换到 HDMI-2" };

    private readonly HotkeyConfig cfg;
    private readonly Func<string> apply;
    private readonly float S;
    private readonly Button[] hkButtons = new Button[3];
    private readonly Button enableBtn;
    private readonly Label status;
    private int recording = -1;

    public SettingsDialog(HotkeyConfig config, Func<string> applyCallback)
    {
        cfg = config;
        apply = applyCallback;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { S = g.DpiX / 96f; }
        if (S < 1f) S = 1f;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = CBg;
        KeyPreview = true;
        DoubleBuffered = true;
        Size = new Size(Px(360), Px(440));

        Label title = MkLabel("设置", 14, 8, 150, 11, CText);
        title.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        Controls.Add(title);
        Controls.Add(MkLabel("快捷键 · 切输入源", 14, 38, 260, 8, CDim));

        Button close = MkButton("✕", 360 - 44, 8, 30, 28, delegate { Close(); });
        close.BackColor = CBg;
        close.ForeColor = CDim;
        Controls.Add(close);

        int top = 70;
        Panel card = MkCard("全局快捷键", top, 190);
        enableBtn = MkButton("", 12, 30, 312, 30, delegate
        {
            cfg.Enabled = !cfg.Enabled;
            cfg.Save();
            ShowStatus(apply());
            RefreshUi();
        });
        card.Controls.Add(enableBtn);

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            card.Controls.Add(MkLabel(Names[i], 12, 68 + i * 38, 170, 8.5, CText));
            hkButtons[i] = MkButton("", 190, 66 + i * 38, 134, 28, delegate { StartRecording(idx); });
            card.Controls.Add(hkButtons[i]);
        }
        Controls.Add(card);

        Panel note = MkCard("说明", 268, 96);
        note.Controls.Add(MkLabelLine("· 显示器切到别的输入源、看不到本机画面时，也能用这些快捷键切回来", 12, 30, 312, 8));
        note.Controls.Add(MkLabelLine("· 组合键至少含 Ctrl / Alt / Shift / Win 之一；点按钮后按新组合，Esc 取消", 12, 50, 312, 8));
        note.Controls.Add(MkLabelLine("· 存于 %APPDATA%\\MonitorSwitch\\settings.ini", 12, 70, 312, 8));
        Controls.Add(note);

        status = MkLabel("", 14, 372, 330, 8.5, CAccent);
        Controls.Add(status);

        Button reset = MkButton("恢复默认", 160, 400, 90, 28, delegate
        {
            cfg.ResetDefaults();
            cfg.Save();
            ShowStatus(apply());
            RefreshUi();
        });
        reset.BackColor = CCard;
        Controls.Add(reset);

        Button closeBtn = MkButton("关闭", 256, 400, 90, 28, delegate { Hide(); });
        closeBtn.BackColor = CCard;
        Controls.Add(closeBtn);

        RefreshUi();
    }

    private int Px(double v) { return (int)Math.Round(v * S); }
    private Font F(double pt) { return new Font("Microsoft YaHei UI", (float)pt, FontStyle.Regular); }

    private Label MkLabel(string text, int x, int y, int w, double pt, Color color)
    {
        Label l = new Label();
        l.Text = text;
        l.AutoSize = false;
        l.BackColor = Color.Transparent;
        l.ForeColor = color;
        l.Font = F(pt);
        l.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(pt * 2.9));
        return l;
    }

    private Label MkLabelLine(string text, int x, int y, int w, double pt)
    {
        Label l = MkLabel(text, x, y, w, pt, CDim);
        l.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(pt * 2.6));
        return l;
    }

    private Button MkButton(string text, int x, int y, int w, int h, EventHandler onClick)
    {
        Button b = new Button();
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = CCardAlt;
        b.ForeColor = CText;
        b.Font = F(9);
        b.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(h));
        b.Cursor = Cursors.Hand;
        if (onClick != null) b.Click += onClick;
        return b;
    }

    private Panel MkCard(string title, int top, int height)
    {
        Panel p = new Panel();
        p.BackColor = CCard;
        p.Bounds = new Rectangle(0, Px(top), Px(336), Px(Math.Max(height, 26)));
        if (title.Length > 0) p.Controls.Add(MkLabel(title, 12, 4, 300, 8.5, CDim));
        return p;
    }

    private void ShowStatus(string text)
    {
        status.Text = text;
        status.ForeColor = (text != null && text.IndexOf("占用") >= 0) ? CWarn : CAccent;
    }

    private void RefreshUi()
    {
        enableBtn.Text = cfg.Enabled ? "全局快捷键：开" : "全局快捷键：关";
        enableBtn.BackColor = cfg.Enabled ? CAccent : CCardAlt;
        enableBtn.ForeColor = cfg.Enabled ? Color.FromArgb(14, 26, 18) : CDim;
        for (int i = 0; i < 3; i++)
        {
            if (recording == i)
            {
                hkButtons[i].Text = "按下新组合…";
                hkButtons[i].BackColor = CWarn;
                hkButtons[i].ForeColor = Color.FromArgb(30, 22, 4);
            }
            else
            {
                hkButtons[i].Text = HotkeyConfig.Describe(cfg.Mods[i], cfg.Vks[i]);
                hkButtons[i].BackColor = cfg.Enabled ? CCardAlt : Color.FromArgb(44, 47, 53);
                hkButtons[i].ForeColor = cfg.Enabled ? CText : CDim;
            }
        }
    }

    private void StartRecording(int idx)
    {
        recording = idx;
        ShowStatus("请按下新的组合键（Esc 取消）");
        RefreshUi();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (recording >= 0)
        {
            if (e.KeyCode == Keys.Escape)
            {
                recording = -1;
                ShowStatus("已取消");
                RefreshUi();
                return;
            }
            int mods, vk;
            HotkeyConfig.FromKeys(e.KeyData, out mods, out vk);
            bool isFunctionKey = (vk >= 0x70 && vk <= 0x87);
            if (mods == 0 && !isFunctionKey)
            {
                ShowStatus("至少要带 Ctrl / Alt / Shift / Win 之一");
                return;
            }
            if (vk == 0) return;
            cfg.Mods[recording] = mods;
            cfg.Vks[recording] = vk;
            cfg.Save();
            string msg = apply();
            recording = -1;
            ShowStatus(msg);
            RefreshUi();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        if (recording >= 0)
        {
            recording = -1;
            RefreshUi();
        }
        base.OnDeactivate(e);
    }

    public void ShowNear(Point anchor)
    {
        int x = anchor.X - Width + Px(20);
        int y = anchor.Y - Height - Px(10);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        if (x < wa.Left + Px(8)) x = wa.Left + Px(8);
        if (y < wa.Top + Px(8)) y = wa.Top + Px(8);
        ShowAt(new Point(x, y));
    }

    public void ShowAt(Point p)
    {
        Location = p;
        Show();
        BringToFront();
        Activate();
        RefreshUi();
        ShowStatus(apply());
    }
}
