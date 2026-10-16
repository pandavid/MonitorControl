using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

// ============================================================================
//  MonitorPanel —— 参考 MonitorBar（github.com/Binaryify/MonitorBar）的信息架构
//  做成的托盘弹出面板：概览 / 画面 / 分辨率 / 屏幕时间
//  最上面一张 HDR 状态卡，明确显示"HDR 开着时亮度/对比度/色温/软件调光是否可用"。
//
//  布局栅格（逻辑像素，按 DPI 缩放）：面板宽 360，左右边距 12 → 内容宽 336；
//  卡片内 x 从 12 起，右边界不超过 324。
// ============================================================================

internal sealed class MonitorPanel : Form
{
    // ---------- 配色 ----------
    private static readonly Color CBg = Color.FromArgb(26, 28, 32);
    private static readonly Color CCard = Color.FromArgb(38, 41, 47);
    private static readonly Color CCardAlt = Color.FromArgb(52, 56, 64);
    private static readonly Color CText = Color.FromArgb(236, 239, 244);
    private static readonly Color CDim = Color.FromArgb(146, 154, 165);
    private static readonly Color CAccent = Color.FromArgb(86, 190, 120);
    private static readonly Color CWarn = Color.FromArgb(242, 184, 66);
    private static readonly Color CTint = Color.FromArgb(38, 68, 50);      // 选中标签的深绿底
    private static readonly Color CTintText = Color.FromArgb(120, 220, 158);

    private const int PanelW = 360;   // 逻辑宽
    private const int PageW = 336;    // 内容宽
    private const int CardW = 336;

    private readonly float S;
    private readonly Action onToggleHdr;
    private readonly Action onQuit;
    private readonly Func<bool> getAutoStart;
    private readonly Action<bool> setAutoStart;
    private readonly Action onSettings;

    // ---------- 控件 ----------
    private readonly Button[] tabButtons = new Button[4];
    private readonly Panel[] tabPages = new Panel[4];

    private Label hdrChip, hdrNote;
    private Button hdrBtn;

    private Button[] inputButtons;
    private static readonly string[] InputNames = new string[] { "DisplayPort-1", "HDMI-1", "HDMI-2" };
    private static readonly uint[] InputValues = new uint[] { 0x0F, 0x11, 0x12 };

    private MiniSlider brightBar, contrastBar;
    private Label brightVal, contrastVal;

    private Button[] colorButtons;
    private static readonly string[] ColorNames = new string[] { "7500K", "9300K", "用户 1" };
    private static readonly uint[] ColorValues = new uint[] { 0x06, 0x08, 0x0B };

    private MiniSlider dimBar, tempBar;
    private Label dimVal, tempVal, pictureHint;
    private int dimPercent = 100;
    private int tempKelvin = 6500;

    private Label modeStatus;
    private Label hdrSub, dispRes, dispSub, dispNote, hzCur, resCount;
    private Button[] refreshPills;
    private int[] refreshHzVals;
    private Button[] resRows;
    private int[][] resRowVals;
    private Button[] presetRows;
    private int[][] presetVals;
    private static readonly int[] PillHz = new int[] { 160, 144, 120, 60 };
    private const string MonName = "HKC G27H7Pro";

    private Label usageToday;
    private Panel usageChart;

    private Button[] sceneButtons;
    private Label sceneStatus, colorStatus;
    private static readonly string[] SceneNames = new string[] { "日间", "阅读", "观影", "夜间" };
    private int currentScene = -1;          // -1 = 自定义/未套用预设
    private uint lastColor = 0xFFFFFFFF;
    private bool refreshing;
    private readonly System.Windows.Forms.Timer fadeTimer;

    private Label statusLine, colorSoft;
    private DateTime lastReadUtc = DateTime.MinValue;
    private Button autoStartBtn;

    /// 容器全部双缓冲：切换页签时 4 个页面互相切换，不双缓冲会看到明显的重绘闪烁
    private sealed class BuffPanel : Panel
    {
        public BuffPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }
    public DateTime LastHiddenUtc = DateTime.MinValue;
    /// 设置窗口浮在面板上时置 true：此时面板失焦也不隐藏，否则设置窗口会孤零零飘着
    public bool SuppressDeactivate = false;

    private readonly System.Windows.Forms.Timer debounce;
    private bool busy;
    private uint pendingCode;
    private bool syncingSliders;
    private bool demoMode;

    public MonitorPanel(Action toggleHdr, Action quit, Func<bool> autoStartGet, Action<bool> autoStartSet, Action settings)
    {
        onToggleHdr = toggleHdr;
        onQuit = quit;
        getAutoStart = autoStartGet;
        setAutoStart = autoStartSet;
        onSettings = settings;

        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { S = g.DpiX / 96f; }
        if (S < 1f) S = 1f;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = CBg;
        KeyPreview = true;
        Size = new Size(Px(PanelW), Px(760));
        DoubleBuffered = true;

        BuildHeader();
        BuildTabs();
        BuildOverviewPage();
        BuildPicturePage();
        BuildResolutionPage();
        BuildUsagePage();
        BuildFooter();

        debounce = new System.Windows.Forms.Timer();
        debounce.Interval = 140;
        debounce.Tick += delegate { debounce.Stop(); FlushSliders(); };

        fadeTimer = new System.Windows.Forms.Timer();
        fadeTimer.Interval = 15;
        fadeTimer.Tick += delegate
        {
            double o = Opacity + 0.16;
            if (o >= 1.0) { Opacity = 1.0; fadeTimer.Stop(); }
            else Opacity = o;
        };

        Deactivate += delegate { if (!demoMode && !SuppressDeactivate) HidePanel(); };
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) HidePanel(); };

        ShowTab(0);
    }

    // ==================== 尺寸/控件工厂 ====================
    private int Px(double v) { return (int)Math.Round(v * S); }
    private Font F(double pt) { return new Font("Microsoft YaHei UI", (float)pt, FontStyle.Regular); }
    private Font FB(double pt) { return new Font("Microsoft YaHei UI", (float)pt, FontStyle.Bold); }

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

    private Label MkLabelTall(string text, int x, int y, int w, int h, double pt, Color color)
    {
        Label l = MkLabel(text, x, y, w, pt, color);
        l.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(h));
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
        b.Font = F(9.5);
        b.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(h));
        b.Cursor = Cursors.Hand;
        Round(b, 8);
        if (onClick != null) b.Click += onClick;
        return b;
    }

    private Panel MkCard(string title, int top, int height)
    {
        Panel p = new BuffPanel();
        p.BackColor = CCard;
        p.Bounds = new Rectangle(0, Px(top), Px(CardW), Px(height));
        Round(p, 10);
        if (title != null && title.Length > 0)
        {
            p.Controls.Add(MkLabelTall(title, 12, 5, 300, 24, 9.5, CDim));
        }
        return p;
    }

    private Panel MkPage(int idx)
    {
        Panel inner = new BuffPanel();
        inner.BackColor = CBg;
        inner.Bounds = new Rectangle(0, 0, Px(PageW), Px(100));
        tabPages[idx].Controls.Add(inner);
        return inner;
    }

    /// 把内层容器收缩到刚好包住内容：容器比可视区高会强行出现纵向滚动条，
    /// 滚动条又会挤掉宽度、再引发横向滚动条，最后把 HDR 卡挤出可视区。
    private void FitInner(Panel inner)
    {
        int bottom = 0;
        foreach (Control c in inner.Controls)
        {
            if (c.Bottom > bottom) bottom = c.Bottom;
        }
        inner.Bounds = new Rectangle(0, 0, Px(PageW), bottom + Px(4));
    }

    private static void SetButtonActive(Button b, bool active)
    {
        if (b == null) return;
        b.BackColor = active ? CAccent : CCardAlt;
        b.ForeColor = active ? Color.FromArgb(14, 26, 18) : CText;
    }

    private static void SetEnabledState(Control c, bool enabled)
    {
        if (c == null) return;
        c.Enabled = enabled;
    }

    /// 圆角（WinForms 没有原生圆角：给控件设一个圆角 Region）
    private void Round(Control c, double logicalRadius)
    {
        if (c == null) return;
        int d = Px(logicalRadius) * 2;
        if (d < 4) d = 4;
        if (c.Width <= d + 1 || c.Height <= d + 1) return;
        GraphicsPath gp = new GraphicsPath();
        gp.AddArc(0, 0, d, d, 180, 90);
        gp.AddArc(c.Width - d - 1, 0, d, d, 270, 90);
        gp.AddArc(c.Width - d - 1, c.Height - d - 1, d, d, 0, 90);
        gp.AddArc(0, c.Height - d - 1, d, d, 90, 90);
        gp.CloseFigure();
        Region old = c.Region;
        c.Region = new Region(gp);
        if (old != null) old.Dispose();
    }

    /// 一行可选项：整行按钮（左对齐）+ 右侧灰色小字（点小字同样触发）
    private Button MkRow(Panel parent, string left, string right, int x, int y, int w, int h, EventHandler onClick)
    {
        Button b = MkButton(left, x, y, w, h, onClick);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(Px(12), 0, 0, 0);
        b.Font = F(9.5);
        b.Tag = (right == null) ? "" : right;
        b.Paint += RowPaint;
        parent.Controls.Add(b);
        return b;
    }

    /// 行按钮右侧的小字：跟着按钮当前底色走（选中是绿底深字，未选中是灰字）
    private void RowPaint(object sender, PaintEventArgs e)
    {
        Button b = sender as Button;
        if (b == null) return;
        string txt = b.Tag as string;
        if (txt == null || txt.Length == 0) return;
        bool active = (b.BackColor == CAccent);
        Color fg = active ? Color.FromArgb(14, 26, 18) : CDim;
        using (Font f = F(8.5))
        using (SolidBrush br = new SolidBrush(fg))
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            e.Graphics.DrawString(txt, f, br, new RectangleF(Px(6), 0, b.Width - Px(16), b.Height), sf);
        }
    }

    /// 自绘滑杆：WinForms 原生 TrackBar 是浅色系统样式，跟这套深色面板不搭
    private MiniSlider MkSlider(int x, int y, int w, int h, int min, int max, int value)
    {
        MiniSlider s = new MiniSlider(S);
        s.Minimum = min;
        s.Maximum = max;
        s.BackColor = CCard;
        s.Bounds = new Rectangle(Px(x), Px(y), Px(w), Px(h));
        s.Value = value;
        s.Cursor = Cursors.Hand;
        return s;
    }

    private sealed class MiniSlider : Control
    {
        public int Minimum = 0;
        public int Maximum = 100;
        public int TickFrequency = 10;   // 兼容旧写法，未使用
        private readonly float S;
        private int val;
        private bool drag;
        private bool fromUser;      // 只有用户拖动才触发 Scroll（TrackBar 就是这个语义）
        public event EventHandler Scroll;

        public MiniSlider(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private int Px(double v) { return (int)Math.Round(v * S); }

        public int Value
        {
            get { return val; }
            set
            {
                int v = value;
                if (v < Minimum) v = Minimum;
                if (v > Maximum) v = Maximum;
                if (v == val) return;
                val = v;
                Invalidate();
                if (fromUser && Scroll != null) Scroll(this, EventArgs.Empty);
            }
        }

        private void SetFromX(int x)
        {
            int pad = Px(10);
            int x0 = pad, x1 = Width - pad;
            if (x1 <= x0) return;
            int v = Minimum + (int)Math.Round((double)(x - x0) * (Maximum - Minimum) / (x1 - x0));
            fromUser = true;
            Value = v;
            fromUser = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled) { drag = true; Capture = true; SetFromX(e.X); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            drag = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(2, rad * 2);
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int pad = Px(10);
            int cy = Height / 2;
            int th = Px(8);
            int x0 = pad, x1 = Width - pad;
            if (x1 <= x0) return;
            Color cTrack = Enabled ? Color.FromArgb(72, 78, 90) : Color.FromArgb(52, 56, 64);
            Color cFill = Enabled ? CAccent : Color.FromArgb(74, 112, 90);
            Color cThumb = Enabled ? CText : Color.FromArgb(150, 156, 166);
            using (GraphicsPath p = Round(new Rectangle(x0, cy - th / 2, x1 - x0, th), th / 2))
            using (SolidBrush b = new SolidBrush(cTrack)) g.FillPath(b, p);
            int span = x1 - x0;
            int dx = (Maximum > Minimum) ? (int)((long)(val - Minimum) * span / (Maximum - Minimum)) : 0;
            int fx = x0 + dx;
            if (fx > x0 + 1)
            {
                using (GraphicsPath p = Round(new Rectangle(x0, cy - th / 2, fx - x0, th), th / 2))
                using (SolidBrush b = new SolidBrush(cFill)) g.FillPath(b, p);
            }
            int td = Px(18);
            int tx = fx - td / 2;
            if (tx < 0) tx = 0;
            if (tx > Width - td) tx = Width - td;
            using (SolidBrush b = new SolidBrush(cThumb)) g.FillEllipse(b, tx, cy - td / 2, td, td);
        }
    }

    // ==================== 头部 / 标签页 ====================
    private void BuildHeader()
    {
        Label title = MkLabelTall("显示器控制", 14, 8, 230, 32, 13, CText);
        title.Font = FB(13);
        Controls.Add(title);
        hdrSub = MkLabelTall(MonName + " · DDC/CI + Gamma", 14, 42, 240, 20, 8.5, CDim);
        Controls.Add(hdrSub);

        Button gear = MkButton("⚙ 设置", PanelW - 108, 10, 62, 32, delegate { if (onSettings != null) onSettings(); });
        gear.BackColor = CBg;
        gear.ForeColor = CDim;
        gear.Font = F(8.5);
        Controls.Add(gear);

        Button close = MkButton("✕", PanelW - 42, 10, 32, 32, delegate { HidePanel(); });
        close.BackColor = CBg;
        close.ForeColor = CDim;
        close.Font = F(11);
        Controls.Add(close);
    }

    private void BuildTabs()
    {
        string[] names = new string[] { "概览", "画面", "分辨率", "屏幕时间" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            Button b = MkButton(names[i], 12 + i * 84, 66, 80, 34, delegate { ShowTab(idx); });
            b.BackColor = CBg;
            b.ForeColor = CDim;
            tabButtons[i] = b;
            Controls.Add(b);
        }
        for (int i = 0; i < 4; i++)
        {
            Panel page = new BuffPanel();
            page.BackColor = CBg;
            page.Bounds = new Rectangle(Px(12), Px(108), Px(PageW), Px(590));
            page.AutoScroll = true;
            page.Visible = false;
            tabPages[i] = page;
            Controls.Add(page);
        }
    }

    private void ShowTab(int idx)
    {
        for (int i = 0; i < 4; i++)
        {
            tabPages[i].Visible = (i == idx);
            tabButtons[i].BackColor = (i == idx) ? CTint : CBg;
            tabButtons[i].ForeColor = (i == idx) ? CTintText : CDim;
        }
        try { tabPages[idx].AutoScrollPosition = new Point(0, 0); }
        catch { }
        if (idx == 2) RefreshModes();     // 本地 API + 已缓存，毫秒级
        if (idx == 3) RefreshUsage();
        // 只重画本地状态：DDC 读数沿用上一次结果，避免切页签时还要等一次 DDC 往返（那就是"加载过程"）
        RefreshModeLabels();
        ApplyHdrState(DisplayState.IsHdrEnabled());
        RefreshAutoStart();
        RefreshSelection();
        if ((DateTime.UtcNow - lastReadUtc).TotalSeconds > 2.0) RefreshAll();
    }

    // ==================== 概览页 ====================
    private void BuildOverviewPage()
    {
        Panel inner = MkPage(0);

        // ---- 当前显示：分辨率 × 刷新率（大字居中，参考 MonitorBar 的显示器示意图）----
        Panel dCard = MkCard("当前显示", 0, 104);
        dispNote = MkLabelTall("", 168, 6, 156, 24, 9, CDim);
        dispNote.TextAlign = ContentAlignment.MiddleRight;
        Panel screen = new BuffPanel();
        screen.BackColor = CCardAlt;
        screen.Bounds = new Rectangle(Px(12), Px(32), Px(312), Px(64));
        Round(screen, 8);
        dispRes = MkLabelTall("--", 6, 4, 300, 34, 16, CText);
        dispRes.Font = FB(16);
        dispRes.TextAlign = ContentAlignment.MiddleCenter;
        dispSub = MkLabelTall("", 6, 38, 300, 20, 8.5, CDim);
        dispSub.TextAlign = ContentAlignment.MiddleCenter;
        screen.Controls.Add(dispRes);
        screen.Controls.Add(dispSub);
        dCard.Controls.Add(screen);
        dCard.Controls.Add(dispNote);
        dispNote.BringToFront();
        inner.Controls.Add(dCard);

        // ---- HDR 状态卡 ----
        Panel hdrCard = MkCard("HDR 状态", 112, 116);
        hdrChip = MkLabelTall("读取中…", 12, 28, 240, 30, 13, CWarn);
        hdrChip.Font = FB(13);
        hdrNote = MkLabelTall("", 12, 60, 312, 24, 8, CDim);
        hdrBtn = MkButton("切换 HDR (Win+Alt+B)", 12, 86, 300, 28, delegate { onToggleHdr(); });
        hdrCard.Controls.Add(hdrChip);
        hdrCard.Controls.Add(hdrNote);
        hdrCard.Controls.Add(hdrBtn);
        inner.Controls.Add(hdrCard);

        // ---- 输入源 ----
        Panel inCard = MkCard("输入源", 236, 76);
        inputButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            uint val = InputValues[i];
            Button b = MkButton(InputNames[i], 12 + i * 106, 34, 100, 34, delegate { SetInput(val); });
            inputButtons[i] = b;
            inCard.Controls.Add(b);
        }
        inner.Controls.Add(inCard);

        // ---- 亮度 ----
        Panel bCard = MkCard("亮度", 320, 64);
        brightVal = MkLabelTall("--", 216, 4, 108, 30, 13, CText);
        brightVal.Font = FB(13);
        brightVal.TextAlign = ContentAlignment.MiddleRight;
        brightBar = MkSlider(12, 34, 312, 28, 0, 100, 50);
        brightBar.Scroll += delegate { OnSliderMoved(0x10); };
        bCard.Controls.Add(brightBar);
        bCard.Controls.Add(brightVal);
        brightVal.BringToFront();
        inner.Controls.Add(bCard);

        // ---- 对比度 ----
        Panel cCard = MkCard("对比度", 392, 64);
        contrastVal = MkLabelTall("--", 216, 4, 108, 30, 13, CText);
        contrastVal.Font = FB(13);
        contrastVal.TextAlign = ContentAlignment.MiddleRight;
        contrastBar = MkSlider(12, 34, 312, 28, 0, 100, 50);
        contrastBar.Scroll += delegate { OnSliderMoved(0x12); };
        cCard.Controls.Add(contrastBar);
        cCard.Controls.Add(contrastVal);
        contrastVal.BringToFront();
        inner.Controls.Add(cCard);

        // ---- 色温 ----
        Panel tCard = MkCard("色温预设", 464, 96);
        colorStatus = MkLabelTall("", 186, 4, 138, 30, 9.5, CText);
        colorStatus.TextAlign = ContentAlignment.MiddleRight;
        colorSoft = MkLabelTall("", 12, 28, 312, 20, 8.5, CAccent);
        colorButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            uint val = ColorValues[i];
            Button b = MkButton(ColorNames[i], 12 + i * 106, 48, 100, 30, delegate { RunVcp(0x14, val, "色温已切换"); });
            colorButtons[i] = b;
            tCard.Controls.Add(b);
        }
        tCard.Controls.Add(MkLabelTall("6500K 这一档本机固件拒绝 DDC 写入，需要在显示器 OSD 里选", 12, 78, 312, 18, 7.5, CDim));
        tCard.Controls.Add(colorStatus);
        tCard.Controls.Add(colorSoft);
        colorStatus.BringToFront();
        colorSoft.BringToFront();
        inner.Controls.Add(tCard);
        FitInner(inner);
    }

    // ==================== 画面页 ====================
    private void BuildPicturePage()
    {
        Panel inner = MkPage(1);

        pictureHint = MkLabelTall("", 0, 0, PageW, 32, 8, CWarn);
        inner.Controls.Add(pictureHint);

        Panel dCard = MkCard("软件调光（显卡 Gamma · 任何接口都能用）", 40, 74);
        dimVal = MkLabelTall("100%", 216, 4, 108, 28, 13, CText);
        dimVal.Font = FB(13);
        dimVal.TextAlign = ContentAlignment.MiddleRight;
        dimBar = MkSlider(12, 38, 312, 30, Gamma.DimMin, Gamma.DimMax, 100);
        dimBar.Scroll += delegate
        {
            dimPercent = dimBar.Value;
            dimVal.Text = dimPercent + "%";
            currentScene = -1;
            RefreshSelection();
            ApplyGamma();
        };
        dCard.Controls.Add(dimVal);
        dCard.Controls.Add(dimBar);
        dimVal.BringToFront();
        inner.Controls.Add(dCard);

        Panel wCard = MkCard("色温 / 减蓝光（Gamma 色阶）", 122, 74);
        tempVal = MkLabelTall("6500K", 216, 4, 108, 28, 13, CText);
        tempVal.Font = FB(13);
        tempVal.TextAlign = ContentAlignment.MiddleRight;
        tempBar = MkSlider(12, 38, 312, 30, Gamma.KelvinMin / 100, Gamma.KelvinMax / 100, 65);
        tempBar.Scroll += delegate
        {
            tempKelvin = tempBar.Value * 100;
            tempVal.Text = tempKelvin + "K";
            currentScene = -1;
            RefreshSelection();
            ApplyGamma();
        };
        wCard.Controls.Add(tempVal);
        wCard.Controls.Add(tempBar);
        tempVal.BringToFront();
        inner.Controls.Add(wCard);

        Panel sCard = MkCard("场景（一键套用）", 204, 110);
        sceneStatus = MkLabelTall("", 176, 4, 148, 22, 8.5, CText);
        sceneStatus.TextAlign = ContentAlignment.MiddleRight;
        sCard.Controls.Add(sceneStatus);
        sceneStatus.BringToFront();
        sceneButtons = new Button[4];
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            sceneButtons[i] = MkButton(SceneNames[i], 12 + i * 78, 30, 72, 30, delegate { ApplyScene(idx); });
            sCard.Controls.Add(sceneButtons[i]);
        }
        sCard.Controls.Add(MkButton("恢复默认（亮度 70 / 对比度 50 / 不调光 / 6500K）", 12, 70, 312, 28, delegate { ResetAll(); }));
        inner.Controls.Add(sCard);

        Panel nCard = MkCard("说明", 322, 100);
        nCard.Controls.Add(MkLabelTall("· 软件调光只压暗画面、不动背光；Windows 要求每通道最亮点 ≥ 50%，所以最暗约 51%，色温越暖能压得越浅", 12, 28, 312, 34, 7.5, CDim));
        nCard.Controls.Add(MkLabelTall("· HDR 开启时，硬件亮度和软件 Gamma 都不生效", 12, 68, 312, 26, 7.5, CDim));
        inner.Controls.Add(nCard);
        FitInner(inner);
    }

    // ==================== 分辨率页 ====================
    private void BuildResolutionPage()
    {
        Panel page = tabPages[2];

        page.Controls.Add(MkLabel("只列常用档：分辨率和刷新率分开选，下面还有 3 个常用组合", 0, 0, 330, 8.5, CDim));

        // ---- 刷新率：按当前分辨率实际支持的档位显示 ----
        Panel hzCard = MkCard("刷新率", 26, 88);
        hzCur = MkLabelTall("", 140, 6, 184, 24, 9.5, CDim);
        hzCur.TextAlign = ContentAlignment.MiddleRight;
        hzCard.Controls.Add(hzCur);
        hzCur.BringToFront();
        refreshPills = new Button[PillHz.Length];
        refreshHzVals = new int[PillHz.Length];
        for (int i = 0; i < PillHz.Length; i++)
        {
            int hz = PillHz[i];
            refreshHzVals[i] = hz;
            Button b = MkButton(hz + " Hz", 12 + i * 78, 40, 74, 36, MakeRefreshHandler(hz));
            refreshPills[i] = b;
            hzCard.Controls.Add(b);
        }
        page.Controls.Add(hzCard);

        // ---- 分辨率 ----
        Panel resCard = MkCard("分辨率", 122, 226);
        resCount = MkLabelTall("", 200, 6, 124, 24, 8.5, CDim);
        resCount.TextAlign = ContentAlignment.MiddleRight;
        resCard.Controls.Add(resCount);
        resCount.BringToFront();
        List<int[]> res = DisplayModes.CommonResolutions();
        resRows = new Button[res.Count];
        resRowVals = new int[res.Count][];
        for (int i = 0; i < res.Count; i++)
        {
            int w = res[i][0], h = res[i][1];
            resRowVals[i] = res[i];
            resRows[i] = MkRow(resCard, w + " × " + h, "", 12, 32 + i * 38, 312, 34, MakeResHandler(w, h));
        }
        resCount.Text = "常用 " + res.Count + " 种";
        page.Controls.Add(resCard);

        // ---- 常用组合（分辨率 + 刷新率一起切）----
        Panel preCard = MkCard("常用组合（分辨率 + 刷新率）", 356, 140);
        presetRows = new Button[3];
        presetVals = new int[3][];
        int[][] wants = new int[][] { new int[] { 3840, 2160 }, new int[] { 2560, 1440 }, new int[] { 1920, 1080 } };
        int n = 0;
        for (int i = 0; i < wants.Length; i++)
        {
            int w = wants[i][0], h = wants[i][1];
            bool ok = false;
            for (int j = 0; j < res.Count; j++) if (res[j][0] == w && res[j][1] == h) { ok = true; break; }
            if (!ok) continue;
            int hz = DisplayModes.MaxCommonHz(w, h);
            presetVals[n] = new int[] { w, h, hz };
            presetRows[n] = MkRow(preCard, w + " × " + h, hz + " Hz", 12, 32 + n * 34, 312, 32, MakePresetHandler(w, h, hz));
            n++;
        }
        preCard.Visible = (n > 0);
        if (n == 0) preCard.Bounds = new Rectangle(0, Px(356), Px(CardW), Px(2));
        page.Controls.Add(preCard);

        page.Controls.Add(MkLabel("切换分辨率时画面会黑闪一下、窗口会重排，几秒内恢复，属正常", 0, 506, 330, 8, CDim));
        modeStatus = MkLabelTall("", 0, 528, PageW, 44, 9, CAccent);
        page.Controls.Add(modeStatus);
    }

    private EventHandler MakeRefreshHandler(int hz)
    {
        return delegate
        {
            DisplayModes.Mode cur = DisplayModes.Current();
            ApplyMode(cur.Width, cur.Height, hz);
        };
    }

    private EventHandler MakeResHandler(int w, int h)
    {
        return delegate
        {
            DisplayModes.Mode cur = DisplayModes.Current();
            int bits;
            int hz = DisplayModes.Supports(w, h, cur.Hertz, out bits) ? cur.Hertz : DisplayModes.MaxCommonHz(w, h);
            ApplyMode(w, h, hz);
        };
    }

    private EventHandler MakePresetHandler(int w, int h, int hz)
    {
        return delegate { ApplyMode(w, h, hz); };
    }

    /// 刷新分辨率页的选中状态（刷新率档位、分辨率行、常用组合）
    private void RefreshModes()
    {
        DisplayModes.Mode cur = DisplayModes.Current();
        RefreshModeLabels();

        if (hzCur != null) hzCur.Text = cur.Width + " × " + cur.Height;
        List<int> hzAvail = DisplayModes.CommonRefreshRates(cur.Width, cur.Height);
        if (refreshPills != null)
        {
            for (int i = 0; i < refreshPills.Length; i++)
            {
                Button b = refreshPills[i];
                if (b == null) continue;
                bool has = hzAvail.Contains(refreshHzVals[i]);
                b.Visible = has;
                SetButtonActive(b, has && refreshHzVals[i] == cur.Hertz);
            }
        }
        if (resRows != null)
        {
            for (int i = 0; i < resRows.Length; i++)
            {
                if (resRows[i] == null) continue;
                int[] v = resRowVals[i];
                bool isCur = (v[0] == cur.Width && v[1] == cur.Height);
                SetButtonActive(resRows[i], isCur);
                resRows[i].Tag = isCur ? "当前" : (DisplayModes.MaxCommonHz(v[0], v[1]) + " Hz");
                resRows[i].Invalidate();
            }
        }
        if (presetRows != null)
        {
            for (int i = 0; i < presetRows.Length; i++)
            {
                if (presetRows[i] == null) continue;
                int[] v = presetVals[i];
                bool isCur = (v[0] == cur.Width && v[1] == cur.Height && v[2] == cur.Hertz);
                SetButtonActive(presetRows[i], isCur);
                presetRows[i].Tag = isCur ? "当前" : (v[2] + " Hz");
                presetRows[i].Invalidate();
            }
        }
    }

    private void ApplyMode(int w, int h, int hz)
    {
        DisplayModes.Mode cur = DisplayModes.Current();
        if (cur.Width == w && cur.Height == h && cur.Hertz == hz)
        {
            modeStatus.Text = "当前已经是 " + w + " × " + h + " @ " + hz + " Hz";
            return;
        }
        modeStatus.Text = "正在切换 " + w + " × " + h + " @ " + hz + " Hz …";
        string err;
        if (DisplayModes.Apply(w, h, hz, out err))
        {
            modeStatus.Text = "已切到 " + w + " × " + h + " @ " + hz + " Hz";
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1800;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                RefreshModes();
                RefreshAll();
                ClampToScreen();
            };
            t.Start();
        }
        else
        {
            modeStatus.Text = err;
        }
    }

    /// 概览页大字 + 头部副标题上的分辨率 / 刷新率（本地 API，毫秒级，不碰 DDC）
    private void RefreshModeLabels()
    {
        try
        {
            DisplayModes.Mode cur = DisplayModes.Current();
            if (dispRes != null) dispRes.Text = cur.Width + " × " + cur.Height;
            if (dispSub != null) dispSub.Text = cur.Hertz + " Hz · " + cur.Bits + " 位色" + (cur.Hertz >= 144 ? " · 高刷" : "");
            if (dispNote != null) dispNote.Text = ResClass(cur.Width);
            if (hdrSub != null) hdrSub.Text = MonName + " · " + cur.Width + " × " + cur.Height + " · " + cur.Hertz + " Hz";
        }
        catch { }
    }

    private static string ResClass(int w)
    {
        if (w >= 3840) return "4K UHD";
        if (w >= 2560) return "2K QHD";
        if (w >= 1920) return "FHD";
        return "低分辨率";
    }

    /// 分辨率变了屏幕像素尺寸会变，把面板挪回可见区域
    public void ClampToScreen()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int x = Left, y = Top;
        if (x + Width > wa.Right - Px(8)) x = wa.Right - Px(8) - Width;
        if (y + Height > wa.Bottom - Px(8)) y = wa.Bottom - Px(8) - Height;
        if (x < wa.Left + Px(8)) x = wa.Left + Px(8);
        if (y < wa.Top + Px(8)) y = wa.Top + Px(8);
        if (x != Left || y != Top) Location = new Point(x, y);
    }

    // ==================== 屏幕时间页 ====================
    private void BuildUsagePage()
    {
        Panel page = tabPages[3];

        usageToday = MkLabel("读取中…", 0, 0, 320, 10, CText);
        usageToday.Font = FB(10);
        page.Controls.Add(usageToday);

        page.Controls.Add(MkLabelTall("本程序运行时每 60 秒采样：亮屏 = 程序在运行；使用中 = 最近 2 分钟内有键鼠输入", 0, 28, PageW, 34, 7.5, CDim));
        page.Controls.Add(MkLabel("最近 7 天（绿=使用中，灰=亮屏但空闲）", 0, 68, 320, 8.5, CDim));

        usageChart = new BuffPanel();
        usageChart.BackColor = CCard;
        usageChart.Bounds = new Rectangle(0, Px(96), Px(PageW), Px(280));
        usageChart.Paint += delegate(object s, PaintEventArgs e) { DrawUsageChart(e.Graphics); };
        page.Controls.Add(usageChart);
    }

    private void RefreshUsage()
    {
        Dictionary<string, long[]> map = Usage.Load();
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        long[] t;
        if (map.TryGetValue(today, out t))
        {
            usageToday.Text = "今日使用 " + Usage.Format(t[1]) + "　亮屏 " + Usage.Format(t[0]);
        }
        else
        {
            usageToday.Text = "今日还没有记录（程序运行 1 分钟后开始统计）";
        }
        if (usageChart != null) usageChart.Invalidate();
    }

    private void DrawUsageChart(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(CCard);
        Dictionary<string, long[]> map = Usage.Load();
        DateTime start = DateTime.Now.AddDays(-6);
        long max = 300;
        for (int i = 0; i < 7; i++)
        {
            long[] v;
            if (map.TryGetValue(start.AddDays(i).ToString("yyyy-MM-dd"), out v) && v[0] > max) max = v[0];
        }
        int bw = Px(26);
        int gap = Px(14);
        int baseY = Px(180);
        int maxH = Px(130);
        using (Font f = F(7.5))
        using (SolidBrush brAct = new SolidBrush(CAccent))
        using (SolidBrush brIdle = new SolidBrush(Color.FromArgb(96, 102, 112)))
        using (SolidBrush brTxt = new SolidBrush(CDim))
        {
            for (int i = 0; i < 7; i++)
            {
                DateTime d = start.AddDays(i);
                long[] v;
                if (!map.TryGetValue(d.ToString("yyyy-MM-dd"), out v)) v = new long[] { 0, 0 };
                int x = Px(24) + i * (bw + gap);
                int hOn = (int)((v[0] * (long)maxH) / max);
                int hAct = (int)((v[1] * (long)maxH) / max);
                if (hOn < 2 && v[0] > 0) hOn = 2;
                if (hOn > 0) g.FillRectangle(brIdle, x, baseY - hOn, bw, hOn);
                if (hAct > 0) g.FillRectangle(brAct, x, baseY - hAct, bw, hAct);
                string lbl = d.ToString("MM-dd");
                g.DrawString(lbl.Substring(3), f, brTxt, x - Px(2), baseY + Px(6));
                if (v[1] >= 60)
                {
                    string hh = (v[1] / 3600) + "h" + ((v[1] % 3600) / 60) + "m";
                    g.DrawString(hh, f, brTxt, x - Px(6), baseY - hAct - Px(16));
                }
            }
        }
    }

    // ==================== 底部 ====================
    private void BuildFooter()
    {
        statusLine = MkLabelTall("", 14, 700, 320, 22, 8.5, CDim);
        Controls.Add(statusLine);

        autoStartBtn = MkButton("开机自启动", 12, 724, 152, 30, delegate
        {
            bool want = !(getAutoStart != null && getAutoStart());
            setAutoStart(want);
            Status(want ? "已设置开机自启" : "已取消开机自启");
            RefreshAutoStart();
        });
        Controls.Add(autoStartBtn);

        Button quit = MkButton("退出", PanelW - 84, 724, 72, 30, delegate { onQuit(); });
        quit.BackColor = CCard;
        Controls.Add(quit);
    }

    private void Status(string text)
    {
        statusLine.Text = text;
    }

    private void RefreshAutoStart()
    {
        if (autoStartBtn == null || getAutoStart == null) return;
        bool on = getAutoStart();
        autoStartBtn.Text = on ? "开机自启动 ✓" : "开机自启动";
        autoStartBtn.BackColor = on ? CAccent : CCardAlt;
        autoStartBtn.ForeColor = on ? Color.FromArgb(14, 26, 18) : CDim;
    }

    // ==================== 刷新 / 可用性 ====================
    /// 立即用本地状态（HDR 开关、开机自启、上次的勾选）把界面画好，DDC 的值在后台读了再填，
    /// 这样面板是"一次性出现"，不会先白一下再慢慢出字。
    public void RefreshAll()
    {
        RefreshModeLabels();
        ApplyHdrState(DisplayState.IsHdrEnabled());
        RefreshAutoStart();
        RefreshSelection();

        if (refreshing) return;
        refreshing = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint inVal;
            string mon;
            string err;
            uint v, m;
            uint b = 0, bmax = 0, c = 0, cmax = 0, col = 0xFFFFFFFF;
            if (!Ddc.TryGetCurrent(out inVal, out mon)) inVal = 0xFFFFFFFF;
            if (Ddc.TryGetValue(0x10, out v, out m, out err)) { b = v; bmax = m; }
            if (Ddc.TryGetValue(0x12, out v, out m, out err)) { c = v; cmax = m; }
            if (Ddc.TryGetValue(0x14, out v, out m, out err)) col = v;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    refreshing = false;
                    lastReadUtc = DateTime.UtcNow;
                    ApplyReadings(inVal, b, bmax, c, cmax, col);
                });
            }
            catch { refreshing = false; }
        });
    }

    private void ApplyHdrState(bool hdr)
    {
        if (hdr)
        {
            hdrChip.Text = "● HDR 已开启";
            hdrChip.ForeColor = CWarn;
            hdrNote.Text = "亮度 / 对比度 / 色温 / 软件调光 现在都不可用";
            hdrNote.ForeColor = CWarn;
            hdrBtn.Text = "关闭 HDR（Win+Alt+B）→ 恢复可调";
        }
        else
        {
            hdrChip.Text = "○ HDR 已关闭";
            hdrChip.ForeColor = CAccent;
            hdrNote.Text = "亮度 / 对比度 / 色温 / 软件调光 都可用";
            hdrNote.ForeColor = CDim;
            hdrBtn.Text = "开启 HDR（Win+Alt+B）";
        }
        if (pictureHint != null)
        {
            pictureHint.Text = hdr ? "⚠ HDR 开启中：本页的软件调光与色温不会生效，请先关闭 HDR" : "";
        }
        SetEnabledState(brightBar, !hdr);
        SetEnabledState(contrastBar, !hdr);
        SetEnabledState(dimBar, !hdr);
        SetEnabledState(tempBar, !hdr);
        if (colorButtons != null)
        {
            for (int i = 0; i < colorButtons.Length; i++) SetEnabledState(colorButtons[i], !hdr);
        }
    }

    private void ApplyReadings(uint inVal, uint b, uint bmax, uint c, uint cmax, uint col)
    {
        if (inputButtons != null && inVal != 0xFFFFFFFF)
        {
            for (int i = 0; i < inputButtons.Length; i++) SetButtonActive(inputButtons[i], InputValues[i] == inVal);
        }
        syncingSliders = true;
        if (bmax > 0)
        {
            brightVal.Text = b + " / " + bmax;
            brightBar.Value = (int)Math.Min(100, Math.Max(0, b));
        }
        if (cmax > 0)
        {
            contrastVal.Text = c + " / " + cmax;
            contrastBar.Value = (int)Math.Min(100, Math.Max(0, c));
        }
        syncingSliders = false;
        lastColor = col;
        if (colorStatus != null) colorStatus.Text = "当前：" + ColorShort(col);
        if (colorButtons != null && col != 0xFFFFFFFF)
        {
            for (int i = 0; i < colorButtons.Length; i++) SetButtonActive(colorButtons[i], ColorValues[i] == col);
        }
        RefreshSelection();
    }

    /// 场景/色温的选中态（问题 3）：当前场景高亮，手动改过就显示"未套用预设"
    /// 短文案（卡片标题行右侧用，长了会被裁掉）
    private static string ColorShort(uint v)
    {
        switch (v)
        {
            case 0x00: return "未设置";
            case 0x05: return "6500K";
            case 0x06: return "7500K";
            case 0x08: return "9300K";
            case 0x0B: return "用户 1";
            case 0xFFFFFFFF: return "读取失败";
            default: return "0x" + v.ToString("X2");
        }
    }

    private void RefreshSelection()
    {
        if (sceneButtons != null)
        {
            for (int i = 0; i < sceneButtons.Length; i++) SetButtonActive(sceneButtons[i], i == currentScene);
        }
        if (sceneStatus != null)
        {
            sceneStatus.Text = (currentScene >= 0) ? ("当前：" + SceneNames[currentScene]) : "当前：未套用预设";
        }
        RefreshSoftColor();
    }

    /// 色温卡里那行"软件色温"：场景调的是显卡 Gamma（软件）色温，
    /// 和上面 3 个硬件预设（DDC 0x14，固件只接受 7500K/9300K/用户1）是两层，必须分开显示。
    private void RefreshSoftColor()
    {
        if (colorSoft == null) return;
        string how;
        if (currentScene >= 0) how = SceneNames[currentScene] + " 场景";
        else if (tempKelvin != 6500 || dimPercent != 100) how = "自定义";
        else how = "默认";
        colorSoft.Text = "软件色温（Gamma）" + tempKelvin + "K · " + how;
        colorSoft.ForeColor = (currentScene >= 0) ? CAccent : CDim;
    }

    // ==================== 动作 ====================
    private void OnSliderMoved(byte code)
    {
        if (syncingSliders) return;
        if (code == 0x10) brightVal.Text = brightBar.Value + " / 100";
        if (code == 0x12) contrastVal.Text = contrastBar.Value + " / 100";
        currentScene = -1;
        RefreshSelection();
        pendingCode = code;
        debounce.Stop();
        debounce.Start();
    }

    private void FlushSliders()
    {
        if (pendingCode == 0) return;
        byte code = (byte)pendingCode;
        pendingCode = 0;
        uint val = (uint)(code == 0x10 ? brightBar.Value : contrastBar.Value);
        RunVcp(code, val, (code == 0x10 ? "亮度" : "对比度") + " 已设为 " + val);
    }

    private void RunVcp(byte code, uint value, string okText)
    {
        if (DisplayState.IsHdrEnabled())
        {
            Status("HDR 开启中，这项不会生效；请先关闭 HDR");
            return;
        }
        if (busy) { Status("上一个操作还在进行…"); return; }
        busy = true;
        Status("写入中…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint settled;
            int attempts;
            string err;
            bool ok = Ddc.SetValue(code, value, out settled, out attempts, out err);
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    if (ok) currentScene = -1;
                    Status(ok ? okText : ("失败：" + err));
                    RefreshAll();
                });
            }
            catch { busy = false; }
        });
    }

    private void SetInput(uint value)
    {
        if (busy) { Status("上一个操作还在进行…"); return; }
        busy = true;
        Status("切换输入源…（屏幕可能黑一下）");
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint settled;
            int attempts;
            string err;
            bool ok = Ddc.Switch(value, out settled, out attempts, out err);
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    Status(ok ? ("已切换到 " + Ddc.NameOf(value)) : ("切换失败：" + err));
                    RefreshAll();
                });
            }
            catch { busy = false; }
        });
    }

    private void ApplyGamma()
    {
        if (DisplayState.IsHdrEnabled())
        {
            Status("HDR 开启中，软件调光/色温不会生效");
            return;
        }
        string err;
        if (Gamma.Apply(dimPercent, tempKelvin, out err))
        {
            string note = (Gamma.LastAchievedDim < dimPercent)
                ? ("　（系统下限：实际只能到 " + Gamma.LastAchievedDim + "%，暖色和深调光会互相挤占）")
                : "";
            Status("软件调光 " + dimPercent + "% · 色温 " + tempKelvin + "K" + note);
        }
        else
        {
            Status("Gamma 失败：" + err);
        }
    }

    private void ApplyScene(int idx)
    {
        if (DisplayState.IsHdrEnabled())
        {
            Status("HDR 开启中，场景不会生效；请先关闭 HDR");
            return;
        }
        string[] names = SceneNames;
        int bright = 70, contrast = 50, dim = 100, kelvin = 6500;
        switch (idx)
        {
            case 0: bright = 80; contrast = 50; dim = 100; kelvin = 6500; break;
            case 1: bright = 60; contrast = 50; dim = 85; kelvin = 5000; break;
            case 2: bright = 45; contrast = 55; dim = 75; kelvin = 5600; break;
            case 3: bright = 25; contrast = 50; dim = 60; kelvin = 3600; break;
        }
        busy = true;
        Status("正在应用场景：" + names[idx]);
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint s1; int a1; string e1;
            Ddc.SetValue(0x10, (uint)bright, out s1, out a1, out e1);
            uint s2; int a2; string e2;
            Ddc.SetValue(0x12, (uint)contrast, out s2, out a2, out e2);
            string gerr;
            Gamma.Apply(dim, kelvin, out gerr);
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    currentScene = idx;
                    RefreshSelection();
                    dimPercent = dim;
                    tempKelvin = kelvin;
                    dimBar.Value = dim;
                    tempBar.Value = kelvin / 100;
                    dimVal.Text = dim + "%";
                    tempVal.Text = kelvin + "K";
                    // 概览页的硬件亮度/对比度滑杆也跟着走（场景是硬写 DDC 的）
                    brightBar.Value = bright;
                    brightVal.Text = bright + " / 100";
                    contrastBar.Value = contrast;
                    contrastVal.Text = contrast + " / 100";
                    RefreshSelection();
                    Status("场景已切换到：" + names[idx]);
                    RefreshAll();
                });
            }
            catch { busy = false; }
        });
    }

    private void ResetAll()
    {
        Gamma.Reset();
        dimPercent = 100;
        tempKelvin = 6500;
        dimBar.Value = 100;
        tempBar.Value = 65;
        dimVal.Text = "100%";
        tempVal.Text = "6500K";
        currentScene = -1;
        RefreshSelection();
        Status("已恢复默认（亮度 70 / 对比度 50 / 不调光 / 6500K）");
        RunVcp(0x10, 70, "已恢复默认（亮度 70）");
    }

    // ==================== 显示 / 隐藏 ====================
    public void SetDemoMode(bool on)
    {
        demoMode = on;
    }

    /// 供 --panel-demo 截图核对用：直接切到某个标签页
    public void SelectTab(int idx)
    {
        if (idx >= 0 && idx < tabPages.Length) ShowTab(idx);
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
        ShowTab(0);
        Location = p;
        Show();
        BringToFront();
        Activate();
        StartFadeIn();
        RefreshAll();
    }

    /// 淡入动画（问题 4）：15ms 一帧，约 0.1 秒到完全不透明
    private void StartFadeIn()
    {
        if (fadeTimer == null) return;
        Opacity = 0.05;
        fadeTimer.Stop();
        fadeTimer.Start();
    }

    public void HidePanel()
    {
        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }
}
