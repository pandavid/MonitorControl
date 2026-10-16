using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ============================================================================
//  MonitorSwitchTray —— HKC G27H7Pro 输入源切换（托盘版）
//  右键/左键点击右下角托盘图标 -> 选择输入源
//  命令行：MonitorSwitchTray.exe --check | --switch DP1|HDMI1|HDMI2
// ============================================================================

internal static class Ddc
{
    public const byte VCP_INPUT_SOURCE = 0x60;
    public const byte VCP_BRIGHTNESS = 0x10;
    public const byte VCP_CONTRAST = 0x12;
    public const byte VCP_COLOR_PRESET = 0x14;

    public static string CodeName(byte code)
    {
        switch (code)
        {
            case 0x10: return "亮度";
            case 0x12: return "对比度";
            case 0x14: return "色温预设";
            case 0x16: return "红增益";
            case 0x18: return "绿增益";
            case 0x1A: return "蓝增益";
            case 0x60: return "输入源";
            case 0x62: return "音量";
            case 0x8D: return "静音";
            case 0xC0: return "点亮时长";
            case 0xD6: return "电源模式";
            default: return "0x" + code.ToString("X2");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    public static void DestroyIconHandle(IntPtr h) { DestroyIcon(h); }

    public static List<IntPtr> Collect(out List<string> descriptions)
    {
        List<IntPtr> handles = new List<IntPtr>();
        List<string> descs = new List<string>();
        MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr data)
        {
            uint n = 0;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) && n > 0)
            {
                PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
                {
                    for (uint i = 0; i < n; i++)
                    {
                        handles.Add(arr[i].hPhysicalMonitor);
                        descs.Add(arr[i].szPhysicalMonitorDescription);
                    }
                }
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        descriptions = descs;
        return handles;
    }

    public static bool ReadVcp(IntPtr h, byte code, out uint value, out uint max)
    {
        uint type, cur, m;
        value = 0;
        max = 0;
        if (!GetVCPFeatureAndVCPFeatureReply(h, code, out type, out cur, out m)) return false;
        value = cur;
        max = m;
        return true;
    }

    public static bool ReadInput(IntPtr h, out uint value)
    {
        uint max;
        return ReadVcp(h, VCP_INPUT_SOURCE, out value, out max);
    }

    /// 找到第一个支持 VCP 0x60 的显示器，返回它的当前输入源。
    public static bool TryGetCurrent(out uint value, out string monitorName)
    {
        List<string> descs;
        List<IntPtr> monitors = Collect(out descs);
        monitorName = "";
        value = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            uint v;
            if (ReadInput(monitors[i], out v))
            {
                value = v;
                monitorName = descs[i];
                return true;
            }
        }
        return false;
    }

    /// 通用 VCP 写入：写入 -> 轮询回读确认 -> 必要时重试
    /// （DDC/CI 写入可能被忽略，而且刚写完的第一次回读可能还是旧值）。
    public static bool SetValue(byte code, uint target, out uint settled, out int attempts, out string error)
    {
        settled = 0;
        attempts = 0;
        error = "";
        bool slow = (code == VCP_INPUT_SOURCE); // 切输入源慢，亮度/对比度响应快
        int interval = slow ? 400 : 180;
        int tries = 6;
        int maxAttempts = slow ? 3 : 2;

        List<string> descs;
        List<IntPtr> monitors = Collect(out descs);
        if (monitors.Count == 0) { error = "找不到物理显示器。"; return false; }

        bool anySupported = false;
        foreach (IntPtr h in monitors)
        {
            uint cur, max;
            if (!ReadVcp(h, code, out cur, out max)) continue; // 该显示器不支持这个 VCP
            anySupported = true;
            settled = cur;
            // 注意：0x60（输入源）不能按 max 夹取——这台 HKC 对 0x60 上报的 max 是 3（垃圾值），
            // 夹取后会把 0x11 写成 3，显示器直接忽略，表现为"切输入源没反应"。
            if (target > max && code != VCP_INPUT_SOURCE) target = max;
            if (cur == target) return true;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                attempts = attempt;
                if (!SetVCPFeature(h, code, target))
                {
                    error = "SetVCPFeature 返回错误 " + Marshal.GetLastWin32Error();
                    break;
                }
                for (int k = 0; k < tries; k++)
                {
                    Thread.Sleep(interval);
                    uint now, m2;
                    if (ReadVcp(h, code, out now, out m2)) settled = now;
                    if (settled == target) return true;
                }
            }
            error = CodeName(code) + "仍为 " + settled;
            return false;
        }

        if (!anySupported)
        {
            error = code == VCP_INPUT_SOURCE
                ? "显示器没有响应 VCP 0x60，请检查 OSD 里的 DDC/CI 开关。"
                : "显示器没有响应 VCP 0x" + code.ToString("X2") + "。";
        }
        return false;
    }

    public static bool Switch(uint target, out uint settled, out int attempts, out string error)
    {
        return SetValue(VCP_INPUT_SOURCE, target, out settled, out attempts, out error);
    }

    /// 读取第一个能响应该 VCP 的显示器的当前值与最大值。
    public static bool TryGetValue(byte code, out uint value, out uint max, out string error)
    {
        value = 0;
        max = 0;
        error = "";
        List<string> descs;
        List<IntPtr> monitors = Collect(out descs);
        foreach (IntPtr h in monitors)
        {
            if (ReadVcp(h, code, out value, out max)) return true;
        }
        error = "显示器没有响应 VCP 0x" + code.ToString("X2") + "。";
        return false;
    }

    public static string NameOf(uint vcp)
    {
        switch (vcp)
        {
            case 0x01: return "VGA-1";
            case 0x02: return "VGA-2";
            case 0x03: return "DVI-1";
            case 0x04: return "DVI-2";
            case 0x0F: return "DisplayPort-1";
            case 0x10: return "DisplayPort-2";
            case 0x11: return "HDMI-1";
            case 0x12: return "HDMI-2";
            default: return "0x" + vcp.ToString("X2");
        }
    }
}

/// 显示器/系统的显示状态：HDR 是否开启。
/// 这台 HKC 在 HDR 模式下会把亮度/对比度/色温的控制权交给 HDR 通路：
/// VCP 0x10/0x12/0x14 的值会被固件收下，但完全不作用到面板（实测亮度 70->25 画面无变化）。
internal static class DisplayState
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYUP = 0x2;

    /// 读 HKLM\...\GraphicsDrivers\MonitorDataStore\<显示器>\HDREnabled
    public static bool IsHdrEnabled()
    {
        try
        {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore"))
            {
                if (root == null) return false;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (RegistryKey k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        object v = k.GetValue("HDREnabled");
                        if (v != null && Convert.ToInt32(v) != 0) return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    public static string HdrHint()
    {
        return "HDR 已开启：亮度/对比度/色温不会生效，先按 Win+Alt+B 关掉 HDR";
    }

    /// 发送 Win+Alt+B（Windows 11 的 HDR 开关热键）
    public static void ToggleHdr()
    {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);              // Win
        Thread.Sleep(70);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);              // Alt
        Thread.Sleep(70);
        keybd_event(0x42, 0, 0, UIntPtr.Zero);              // B
        Thread.Sleep(90);
        keybd_event(0x42, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(0x5B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}

/// 隐藏窗口：仅用于 (a) 把后台线程的调用封送到 UI 线程 (b) 让弹出菜单获得前台焦点
/// (c) 监听 TaskbarCreated —— explorer 重启后自动重新注册托盘图标。
internal sealed class HiddenForm : Form
{
    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    public event EventHandler TaskbarRestarted;
    public event Action<int> HotkeyPressed;

    public HiddenForm()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, -4000);
        Size = new Size(1, 1);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_TASKBARCREATED && TaskbarRestarted != null) TaskbarRestarted(this, EventArgs.Empty);
        if (m.Msg == HotkeyManager.WM_HOTKEY && HotkeyPressed != null) HotkeyPressed(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private static readonly uint[] Targets = new uint[] { 0x0F, 0x11, 0x12 };
    private static readonly string[] Names = new string[] { "DisplayPort-1", "HDMI-1", "HDMI-2" };

    // MCCS 0x14 色温预设。实测这台固件只接受 0x06(7500K) / 0x08(9300K) / 0x0B(用户1)：
    // 写 0x05(6500K) 会被拒并落到 7500K，写 0x01-0x04/0x07/0x0A 会让它变成"未设置"。
    private static readonly uint[] ColorValues = new uint[] { 0x06, 0x08, 0x0B };
    private static readonly string[] ColorNames = new string[] { "7500K", "9300K", "用户 1" };
    private static readonly int[] LevelPresets = new int[] { 25, 50, 75, 100 };

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MonitorSwitchTray";

    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem header;
    private readonly ToolStripMenuItem[] targetItems;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly ToolStripMenuItem[] colorItems;
    private readonly ToolStripMenuItem brightnessHeader;
    private readonly ToolStripMenuItem contrastHeader;
    private readonly ToolStripMenuItem colorHeader;
    private readonly ToolStripMenuItem hdrItem;
    private readonly Form hidden;   // 仅用于 Invoke 到 UI 线程 / 菜单前台化，永不显示
    private MonitorPanel panel;     // 弹出面板（懒创建）
    private HotkeyConfig hotCfg;
    private SettingsDialog settingsDlg;
    private System.Windows.Forms.Timer usageTimer;
    private bool busy;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayApp()
    {
        hidden = new HiddenForm();
        IntPtr unused = hidden.Handle; // 强制创建窗口句柄

        menu = new ContextMenuStrip();
        header = new ToolStripMenuItem("正在读取…");
        header.Enabled = false;
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        targetItems = new ToolStripMenuItem[Targets.Length];
        for (int i = 0; i < Targets.Length; i++)
        {
            uint target = Targets[i];
            string name = Names[i];
            ToolStripMenuItem item = new ToolStripMenuItem("切换到 " + name);
            item.Click += delegate { BeginSwitch(target, name); };
            targetItems[i] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        // ---------------- 亮度 ----------------
        ToolStripMenuItem brightnessMenu = new ToolStripMenuItem("亮度");
        brightnessHeader = new ToolStripMenuItem("正在读取…");
        brightnessHeader.Enabled = false;
        brightnessMenu.DropDownItems.Add(brightnessHeader);
        brightnessMenu.DropDownItems.Add(new ToolStripSeparator());
        brightnessMenu.DropDownItems.Add(MakeAdjustItem("亮度 +5", Ddc.VCP_BRIGHTNESS, 5));
        brightnessMenu.DropDownItems.Add(MakeAdjustItem("亮度 -5", Ddc.VCP_BRIGHTNESS, -5));
        brightnessMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int lvl in LevelPresets)
        {
            brightnessMenu.DropDownItems.Add(MakeSetItem(lvl + "%", Ddc.VCP_BRIGHTNESS, (uint)lvl, false));
        }
        brightnessMenu.DropDownOpening += delegate { RefreshLevelHeaders(); };
        menu.Items.Add(brightnessMenu);

        // ---------------- 对比度 ----------------
        ToolStripMenuItem contrastMenu = new ToolStripMenuItem("对比度");
        contrastHeader = new ToolStripMenuItem("正在读取…");
        contrastHeader.Enabled = false;
        contrastMenu.DropDownItems.Add(contrastHeader);
        contrastMenu.DropDownItems.Add(new ToolStripSeparator());
        contrastMenu.DropDownItems.Add(MakeAdjustItem("对比度 +5", Ddc.VCP_CONTRAST, 5));
        contrastMenu.DropDownItems.Add(MakeAdjustItem("对比度 -5", Ddc.VCP_CONTRAST, -5));
        contrastMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int lvl in LevelPresets)
        {
            contrastMenu.DropDownItems.Add(MakeSetItem(lvl + "%", Ddc.VCP_CONTRAST, (uint)lvl, false));
        }
        contrastMenu.DropDownOpening += delegate { RefreshLevelHeaders(); };
        menu.Items.Add(contrastMenu);

        // ---------------- 色温预设 ----------------
        ToolStripMenuItem colorMenu = new ToolStripMenuItem("色温预设");
        colorHeader = new ToolStripMenuItem("正在读取…");
        colorHeader.Enabled = false;
        colorMenu.DropDownItems.Add(colorHeader);
        colorMenu.DropDownItems.Add(new ToolStripSeparator());
        colorItems = new ToolStripMenuItem[ColorValues.Length];
        for (int i = 0; i < ColorValues.Length; i++)
        {
            colorItems[i] = MakeSetItem(ColorNames[i], Ddc.VCP_COLOR_PRESET, ColorValues[i], true);
            colorMenu.DropDownItems.Add(colorItems[i]);
        }
        ToolStripMenuItem colorNote = new ToolStripMenuItem("注：本机固件色温读数会滞后，以实际观感为准");
        colorNote.Enabled = false;
        colorMenu.DropDownItems.Add(colorNote);
        colorMenu.DropDownOpening += delegate { RefreshLevelHeaders(); };
        menu.Items.Add(colorMenu);

        hdrItem = new ToolStripMenuItem("切换 HDR（Win+Alt+B）");
        hdrItem.Click += delegate { BeginToggleHdr(); };
        menu.Items.Add(hdrItem);

        ToolStripMenuItem openPanel = new ToolStripMenuItem("打开面板…");
        openPanel.Click += delegate { ShowPanel(); };
        menu.Items.Add(openPanel);
        ToolStripMenuItem openSet = new ToolStripMenuItem("设置…（快捷键）");
        openSet.Click += delegate { OpenSettings(); };
        menu.Items.Add(openSet);
        ToolStripMenuItem sleepItem = new ToolStripMenuItem("关闭屏幕（待机）");
        sleepItem.Click += delegate { ScreenOffOnce(); };
        menu.Items.Add(sleepItem);

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem refresh = new ToolStripMenuItem("刷新状态");
        refresh.Click += delegate { RefreshState(); };
        menu.Items.Add(refresh);

        autoStartItem = new ToolStripMenuItem("开机自动启动");
        autoStartItem.CheckOnClick = false;
        autoStartItem.Click += delegate { ToggleAutoStart(); };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem quit = new ToolStripMenuItem("退出");
        quit.Click += delegate { ExitApp(); };
        menu.Items.Add(quit);

        menu.Opening += delegate { RefreshState(); };

        tray = new NotifyIcon();
        tray.Icon = BuildIcon();
        tray.Text = "显示器输入源切换";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowPanel();
        };

        HiddenForm hf = hidden as HiddenForm;
        if (hf != null)
        {
            hf.TaskbarRestarted += delegate
            {
                // explorer 重启后托盘区被清空，重新注册图标
                tray.Visible = true;
                RefreshState();
            };
            hf.HotkeyPressed += OnHotkey;
        }
        hotCfg = HotkeyConfig.Load();
        ApplyHotkeys();

        StartUsageSampler();
        WarmUpPanel();
        RefreshState();
        tray.ShowBalloonTip(3000, "显示器输入源切换已启动",
            "左键点托盘图标打开面板，右键打开菜单。", ToolTipIcon.Info);
    }

    private void ShowMenuAtCursor()
    {
        RefreshState();
        try { SetForegroundWindow(hidden.Handle); }
        catch { }
        menu.Show(Cursor.Position);
    }

    private void EnsurePanel()
    {
        if (panel == null)
        {
            panel = new MonitorPanel(BeginToggleHdr, ExitApp, IsAutoStartEnabled, SetAutoStart, OpenSettings);
        }
    }

    /// 启动 1 秒后先把面板建好（不显示），这样第一次点击托盘图标是秒开，不会先白一下
    private void WarmUpPanel()
    {
        System.Windows.Forms.Timer warm = new System.Windows.Forms.Timer();
        warm.Interval = 1000;
        warm.Tick += delegate
        {
            warm.Stop();
            warm.Dispose();
            try { EnsurePanel(); }
            catch { }
        };
        warm.Start();
    }

    private void ShowPanel()
    {
        EnsurePanel();
        // 面板开着时再点一次就是关闭。但点击会让面板先失焦自隐藏，
        // 所以还要看"刚刚是否隐藏过"，否则会立刻又被打开。
        bool justHidden = (DateTime.UtcNow - panel.LastHiddenUtc).TotalMilliseconds < 350;
        if (panel.Visible || justHidden)
        {
            panel.HidePanel();
            return;
        }
        panel.ShowNear(Cursor.Position);
    }

    // ---------------- 全局快捷键 ----------------
    private static readonly uint[] HotkeyTargets = new uint[] { 0x0F, 0x11, 0x12 };

    private void ApplyHotkeys()
    {
        if (hotCfg == null) return;
        IntPtr h = hidden.Handle;
        for (int i = 0; i < 3; i++) HotkeyManager.Unregister(h, i + 1);
        if (!hotCfg.Enabled) return;
        for (int i = 0; i < 3; i++) HotkeyManager.Register(h, i + 1, hotCfg.Mods[i], hotCfg.Vks[i]);
    }

    /// 重新注册并返回一句结果，给设置窗口显示
    private string ApplyHotkeysReport()
    {
        if (hotCfg == null) hotCfg = HotkeyConfig.Load();
        IntPtr h = hidden.Handle;
        for (int i = 0; i < 3; i++) HotkeyManager.Unregister(h, i + 1);
        if (!hotCfg.Enabled) return "全局快捷键已关闭";
        string bad = "";
        for (int i = 0; i < 3; i++)
        {
            if (!HotkeyManager.Register(h, i + 1, hotCfg.Mods[i], hotCfg.Vks[i]))
            {
                if (bad.Length > 0) bad += "、";
                bad += HotkeyConfig.Describe(hotCfg.Mods[i], hotCfg.Vks[i]);
            }
        }
        if (bad.Length > 0) return "这些组合被别的程序占用了：" + bad;
        return "已生效：" + HotkeyConfig.Describe(hotCfg.Mods[0], hotCfg.Vks[0])
            + " / " + HotkeyConfig.Describe(hotCfg.Mods[1], hotCfg.Vks[1])
            + " / " + HotkeyConfig.Describe(hotCfg.Mods[2], hotCfg.Vks[2]);
    }

    private void OnHotkey(int id)
    {
        if (id < 1 || id > 3) return;
        uint target = HotkeyTargets[id - 1];
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint settled;
            int attempts;
            string err;
            bool ok = Ddc.Switch(target, out settled, out attempts, out err);
            try
            {
                hidden.BeginInvoke((MethodInvoker)delegate
                {
                    if (ok)
                    {
                        tray.ShowBalloonTip(2500, "快捷键切换成功", "显示器已切到 " + Ddc.NameOf(target), ToolTipIcon.Info);
                    }
                    else
                    {
                        tray.ShowBalloonTip(4000, "快捷键切换失败", Ddc.NameOf(target) + "：" + err, ToolTipIcon.Warning);
                    }
                    RefreshState();
                });
            }
            catch { }
        });
    }

    private void OpenSettings()
    {
        EnsurePanel();
        // 让设置窗口浮在面板上：面板没开就先开出来，然后面板在设置开着期间不自动隐藏
        if (!panel.Visible) panel.ShowNear(Cursor.Position);
        if (settingsDlg == null || settingsDlg.IsDisposed)
        {
            if (hotCfg == null) hotCfg = HotkeyConfig.Load();
            settingsDlg = new SettingsDialog(hotCfg, ApplyHotkeysReport);
            settingsDlg.FormClosed += delegate
            {
                if (panel != null) panel.SuppressDeactivate = false;
            };
        }
        panel.SuppressDeactivate = true;
        // 正好压在面板正中
        int x = panel.Left;
        int y = panel.Top + Math.Max(0, (panel.Height - settingsDlg.Height) / 2);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        if (y + settingsDlg.Height > wa.Bottom - 8) y = wa.Bottom - 8 - settingsDlg.Height;
        if (y < wa.Top + 8) y = wa.Top + 8;
        settingsDlg.ShowAt(new Point(x, y));
    }

    /// --settings 自检：打开设置窗口，seconds 秒后退出
    public void SettingsDemo(int seconds)
    {
        if (settingsDlg == null || settingsDlg.IsDisposed)
        {
            if (hotCfg == null) hotCfg = HotkeyConfig.Load();
            settingsDlg = new SettingsDialog(hotCfg, ApplyHotkeysReport);
        }
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        settingsDlg.ShowAt(new Point(wa.Right - settingsDlg.Width - 60, wa.Bottom - settingsDlg.Height - 60));
        if (seconds > 0)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate { t.Stop(); t.Dispose(); ExitApp(); };
            t.Start();
        }
    }

    /// 每 60 秒采样一次：亮屏 = 程序在运行，使用中 = 最近 2 分钟内有键鼠输入
    private void StartUsageSampler()
    {
        usageTimer = new System.Windows.Forms.Timer();
        usageTimer.Interval = 60000;
        usageTimer.Tick += delegate
        {
            Usage.Add(60, Usage.IdleSeconds() < 120 ? 60 : 0);
        };
        usageTimer.Start();
    }

    private void SetAutoStart(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) return;
                if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;

    /// 版本号（发版时改这里；--version 会打印）
    public const string AppVersion = "0.2.0";

    /// 建一个屏幕外的隐藏窗口（用来承载 SC_MONITORPOWER 消息，不需要托盘实例）
    private static Form MakeHiddenSender()
    {
        Form f = new Form();
        f.ShowInTaskbar = false;
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Size = new Size(1, 1);
        f.Show();
        return f;
    }

    /// 立即关屏（显示器进入待机；动一下鼠标或按键盘唤醒）
    public static void ScreenOffOnce()
    {
        Form f = MakeHiddenSender();
        try { SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2); }
        catch { }
        Application.DoEvents();
        f.Close();
        f.Dispose();
    }

    /// 命令行用：N 分钟后关屏（本进程驻留到关屏后自己退出）
    public static void ScreenOffLater(int minutes)
    {
        Form f = MakeHiddenSender();
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = minutes * 60 * 1000;
        t.Tick += delegate
        {
            t.Stop();
            t.Dispose();
            try { SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2); }
            catch { }
            f.Close();
        };
        t.Start();
        Application.Run(f);
    }

    /// --panel-demo：面板摆在固定位置（不依赖鼠标），seconds 秒后自动退出，用于截图核对
    public void PanelDemo(int seconds, int tab)
    {
        EnsurePanel();
        panel.SetDemoMode(seconds > 0);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        panel.ShowAt(new Point(wa.Right - panel.Width - 40, wa.Bottom - panel.Height - 40));
        panel.SelectTab(tab);
        if (seconds > 0)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate { t.Stop(); t.Dispose(); ExitApp(); };
            t.Start();
        }
    }

    private void RefreshState()
    {
        uint cur;
        string mon;
        if (!Ddc.TryGetCurrent(out cur, out mon))
        {
            header.Text = "未找到可切换的显示器";
            tray.Text = "显示器输入源切换 — 显示器无响应";
            foreach (ToolStripMenuItem it in targetItems) it.Checked = false;
        }
        else
        {
            header.Text = "当前输入源：" + Ddc.NameOf(cur)
                + (DisplayState.IsHdrEnabled() ? "   ｜ HDR 已开启，亮度/对比度/色温无效" : "");
            string t = "显示器输入源切换 — 当前 " + Ddc.NameOf(cur);
            tray.Text = t.Length > 62 ? t.Substring(0, 62) : t;
            for (int i = 0; i < Targets.Length; i++) targetItems[i].Checked = (Targets[i] == cur);
        }
        autoStartItem.Checked = IsAutoStartEnabled();
    }

    private void BeginSwitch(uint target, string name)
    {
        if (busy)
        {
            tray.ShowBalloonTip(2000, "请稍候", "上一次切换还在进行中。", ToolTipIcon.Info);
            return;
        }
        busy = true;
        foreach (ToolStripMenuItem it in targetItems) it.Enabled = false;
        tray.ShowBalloonTip(2000, "正在切换", "正在把显示器切换到 " + name + " …", ToolTipIcon.Info);

        ThreadPool.QueueUserWorkItem(delegate
        {
            uint settled;
            int attempts;
            string error;
            bool ok = Ddc.Switch(target, out settled, out attempts, out error);
            try
            {
                hidden.BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    foreach (ToolStripMenuItem it in targetItems) it.Enabled = true;
                    if (ok)
                    {
                        tray.ShowBalloonTip(3000, "切换成功", "已切换到 " + name + "。", ToolTipIcon.Info);
                    }
                    else
                    {
                        tray.ShowBalloonTip(5000, "切换失败",
                            "没有切到 " + name + "：" + error + "（当前 " + Ddc.NameOf(settled) + "）", ToolTipIcon.Warning);
                    }
                    RefreshState();
                });
            }
            catch { }
        });
    }

    private ToolStripMenuItem MakeAdjustItem(string text, byte code, int delta)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Click += delegate { BeginAdjust(code, delta); };
        return item;
    }

    private ToolStripMenuItem MakeSetItem(string text, byte code, uint value, bool notifySuccess)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Click += delegate { BeginSet(code, value, notifySuccess); };
        return item;
    }

    private static string ColorNameOf(uint v)
    {
        if (v == 0x00) return "未设置（请在 OSD 里选一次色温）";
        if (v == 0x05) return "6500K（固件拒绝 DDC 写入，只能用 OSD）";
        for (int i = 0; i < ColorValues.Length; i++)
        {
            if (ColorValues[i] == v) return ColorNames[i];
        }
        return "0x" + v.ToString("X2");
    }

    /// 子菜单弹出时才去读显示器，避免每次打开主菜单都发一串 DDC 请求。
    private void RefreshLevelHeaders()
    {
        uint v, max;
        string err;
        string hdr = DisplayState.IsHdrEnabled() ? "（HDR 开，改动无效）" : "";
        if (Ddc.TryGetValue(Ddc.VCP_BRIGHTNESS, out v, out max, out err)) brightnessHeader.Text = "当前亮度：" + v + " / " + max + hdr;
        else brightnessHeader.Text = "亮度：读取失败" + hdr;
        if (Ddc.TryGetValue(Ddc.VCP_CONTRAST, out v, out max, out err)) contrastHeader.Text = "当前对比度：" + v + " / " + max + hdr;
        else contrastHeader.Text = "对比度：读取失败" + hdr;
        if (Ddc.TryGetValue(Ddc.VCP_COLOR_PRESET, out v, out max, out err))
        {
            colorHeader.Text = "当前色温：" + ColorNameOf(v) + hdr;
            for (int i = 0; i < ColorValues.Length; i++) colorItems[i].Checked = (ColorValues[i] == v);
        }
        else
        {
            colorHeader.Text = "色温：读取失败" + hdr;
        }
    }

    private void BeginAdjust(byte code, int delta)
    {
        if (busy)
        {
            tray.ShowBalloonTip(1500, "请稍候", "上一个操作还在进行。", ToolTipIcon.Info);
            return;
        }
        if (DisplayState.IsHdrEnabled())
        {
            tray.ShowBalloonTip(6000, "HDR 已开启，此项不会生效", DisplayState.HdrHint(), ToolTipIcon.Warning);
            return;
        }
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint cur, max;
            string err;
            if (!Ddc.TryGetValue(code, out cur, out max, out err)) { FinishOp(code, false, 0, err, false); return; }
            long t = (long)cur + delta;
            if (t < 0) t = 0;
            if (t > max) t = max;
            uint settled;
            int attempts;
            bool ok = Ddc.SetValue(code, (uint)t, out settled, out attempts, out err);
            FinishOp(code, ok, settled, err, false);
        });
    }

    private void BeginSet(byte code, uint value, bool notifySuccess)
    {
        if (busy)
        {
            tray.ShowBalloonTip(1500, "请稍候", "上一个操作还在进行。", ToolTipIcon.Info);
            return;
        }
        if (DisplayState.IsHdrEnabled())
        {
            tray.ShowBalloonTip(6000, "HDR 已开启，此项不会生效", DisplayState.HdrHint(), ToolTipIcon.Warning);
            return;
        }
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            uint settled;
            int attempts;
            string err;
            bool ok = Ddc.SetValue(code, value, out settled, out attempts, out err);
            FinishOp(code, ok, settled, err, notifySuccess);
        });
    }

    private void FinishOp(byte code, bool ok, uint settled, string error, bool notifySuccess)
    {
        try
        {
            hidden.BeginInvoke((MethodInvoker)delegate
            {
                busy = false;
                if (ok)
                {
                    if (notifySuccess)
                    {
                        string text = (code == Ddc.VCP_COLOR_PRESET) ? ColorNameOf(settled) : settled.ToString();
                        tray.ShowBalloonTip(2500, Ddc.CodeName(code) + " 已改变", "现在：" + text, ToolTipIcon.Info);
                    }
                }
                else
                {
                    tray.ShowBalloonTip(5000, "操作失败", Ddc.CodeName(code) + "：" + error, ToolTipIcon.Warning);
                }
                RefreshState();
                RefreshLevelHeaders();
            });
        }
        catch { }
    }

    private void BeginToggleHdr()
    {
        if (busy)
        {
            tray.ShowBalloonTip(1500, "请稍候", "上一个操作还在进行。", ToolTipIcon.Info);
            return;
        }
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            DisplayState.ToggleHdr();   // 发送 Win+Alt+B
            Thread.Sleep(2200);
            bool on = DisplayState.IsHdrEnabled();
            try
            {
                hidden.BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    tray.ShowBalloonTip(4000, on ? "HDR 已开启" : "HDR 已关闭",
                        on ? "HDR 下亮度/对比度/色温不会生效。" : "现在可以调亮度/对比度/色温了。",
                        ToolTipIcon.Info);
                    RefreshState();
                    RefreshLevelHeaders();
                    if (panel != null) panel.RefreshAll();
                });
            }
            catch { busy = false; }
        });
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return k != null && k.GetValue(RunValueName) != null;
            }
        }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        bool enable = !IsAutoStartEnabled();
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) return;
                if (enable) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
            tray.ShowBalloonTip(3000, enable ? "已设置开机自启" : "已取消开机自启",
                enable ? "下次登录 Windows 后会自动驻留托盘。" : "已从启动项移除。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "设置失败", ex.Message, ToolTipIcon.Warning);
        }
        RefreshState();
    }

    private void ExitApp()
    {
        try { Gamma.Reset(); }   // 退出时还原显卡 Gamma 曲线，别把屏幕留在调暗/偏色的状态
        catch { }
        tray.Visible = false;
        tray.Dispose();
        hidden.Dispose();
        ExitThread();
    }

    /// 供 --demo-menu 自检使用
    public ContextMenuStrip MenuStrip { get { return menu; } }

    public void Shutdown() { ExitApp(); }

    private static Icon BuildIcon()
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (SolidBrush frame = new SolidBrush(Color.FromArgb(245, 245, 245)))
            using (SolidBrush screen = new SolidBrush(Color.FromArgb(40, 52, 68)))
            using (SolidBrush accent = new SolidBrush(Color.FromArgb(70, 200, 125)))
            {
                g.FillRectangle(frame, 2, 4, 28, 20);
                g.FillRectangle(screen, 4, 6, 24, 16);
                g.FillPolygon(accent, new Point[] { new Point(8, 9), new Point(8, 16), new Point(14, 12) });
                g.FillPolygon(accent, new Point[] { new Point(24, 11), new Point(24, 18), new Point(18, 14) });
                g.FillRectangle(frame, 13, 24, 6, 4);
                g.FillRectangle(frame, 8, 28, 16, 2);
            }
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone();
        }
        finally
        {
            Ddc.DestroyIconHandle(h);
            bmp.Dispose();
        }
    }
}

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static Mutex single;

    [STAThread]
    private static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        // ---------- 命令行模式（不启动托盘，方便脚本 / 验证） ----------
        if (args.Length > 0)
        {
            // winexe 没有自己的控制台，直接设 Console.OutputEncoding 会抛 IOException；
            // 改用显式 UTF-8 的 stdout/stderr 写入器（脚本调用时可正常重定向）。
            try
            {
                StreamWriter so = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                so.AutoFlush = true;
                Console.SetOut(so);
                StreamWriter se = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false));
                se.AutoFlush = true;
                Console.SetError(se);
            }
            catch { }
            string a0 = args[0].ToLowerInvariant();
            if (a0 == "--version" || a0 == "-v")
            {
                Console.WriteLine("MonitorControl v" + TrayApp.AppVersion);
                return 0;
            }
            if (a0 == "--screen-off")
            {
                TrayApp.ScreenOffOnce();
                return 0;
            }
            if (a0 == "--screen-off-in")
            {
                int mins;
                if (args.Length < 2 || !int.TryParse(args[1], out mins) || mins <= 0)
                {
                    Console.Error.WriteLine("用法: --screen-off-in 30");
                    return 1;
                }
                Console.WriteLine("已设置 " + mins + " 分钟后关闭屏幕（本进程会驻留到关屏后自动退出）");
                TrayApp.ScreenOffLater(mins);
                return 0;
            }
            if (a0 == "--set-mode" && args.Length >= 4)
            {
                int mw, mh, mhz;
                if (!int.TryParse(args[1], out mw) || !int.TryParse(args[2], out mh) || !int.TryParse(args[3], out mhz))
                {
                    Console.Error.WriteLine("用法: --set-mode 2560 1440 160");
                    return 1;
                }
                string em;
                if (DisplayModes.Apply(mw, mh, mhz, out em))
                {
                    Console.WriteLine("已切到 " + mw + " × " + mh + " @ " + mhz + " Hz");
                    return 0;
                }
                Console.Error.WriteLine("切换失败：" + em);
                return 1;
            }
            if (a0 == "--check" || a0 == "-c" || a0 == "check")
            {
                uint cur; string mon;
                if (!Ddc.TryGetCurrent(out cur, out mon))
                {
                    Console.Error.WriteLine("未找到可切换的显示器（显示器无响应 VCP 0x60）。");
                    return 1;
                }
                Console.WriteLine("当前输入源：" + Ddc.NameOf(cur) + " (VCP 0x60 = 0x" + cur.ToString("X2") + ")  [" + mon + "]");
                return 0;
            }
            if (a0 == "--switch" || a0 == "-s" || a0 == "switch")
            {
                if (args.Length < 2) { Console.Error.WriteLine("用法: --switch DP1|HDMI1|HDMI2"); return 1; }
                string want = args[1].ToUpperInvariant();
                uint target = 0xFFFFFFFF;
                if (want == "DP1" || want == "DP") target = 0x0F;
                else if (want == "HDMI1") target = 0x11;
                else if (want == "HDMI2") target = 0x12;
                if (target == 0xFFFFFFFF) { Console.Error.WriteLine("未知的输入源: " + args[1]); return 1; }
                uint settled; int attempts; string error;
                bool ok = Ddc.Switch(target, out settled, out attempts, out error);
                if (ok) Console.WriteLine("已切换到 " + Ddc.NameOf(target) + "（第 " + attempts + " 次写入生效）");
                else Console.Error.WriteLine("切换失败：" + error);
                return ok ? 0 : 1;
            }

            // ---- HDR 状态 / 切换 ----
            if (a0 == "--hdr")
            {
                bool on = DisplayState.IsHdrEnabled();
                Console.WriteLine("HDR = " + (on
                    ? "开启（此时亮度/对比度/色温写入不会作用到面板，按 Win+Alt+B 可关闭）"
                    : "关闭（亮度/对比度/色温可用）"));
                return 0;
            }
            if (a0 == "--toggle-hdr")
            {
                Console.WriteLine("发送 Win+Alt+B …");
                DisplayState.ToggleHdr();
                Thread.Sleep(2500);
                Console.WriteLine("现在 HDR = " + (DisplayState.IsHdrEnabled() ? "开启" : "关闭"));
                return 0;
            }

            // ---- 读取 / 设置任意 VCP ----
            if (a0 == "--get" || a0 == "--set")
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("用法: --get <brightness|contrast|color|redgain|greengain|bluegain|volume|mute|usagetime|power|input>");
                    Console.Error.WriteLine("      --set <name>=<value>    例如 --set brightness=65 / --set color=7500");
                    return 1;
                }
                string body = args[1];
                string name = body;
                string valueText = null;
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    name = body.Substring(0, eq);
                    valueText = body.Substring(eq + 1);
                }

                byte code;
                if (!ResolveVcp(name, out code))
                {
                    Console.Error.WriteLine("未知的项: " + name);
                    return 1;
                }

                if (a0 == "--get")
                {
                    uint v, max;
                    string err;
                    if (!Ddc.TryGetValue(code, out v, out max, out err)) { Console.Error.WriteLine(err); return 1; }
                    string shown = (code == Ddc.VCP_COLOR_PRESET) ? ColorLabel(v) : v.ToString();
                    Console.WriteLine(name + " = " + shown + "   (VCP 0x" + code.ToString("X2") + ", max " + max + ")");
                    return 0;
                }

                if (valueText == null)
                {
                    Console.Error.WriteLine("--set 要写成 name=value，例如 --set brightness=65");
                    return 1;
                }
                uint target;
                if (code == Ddc.VCP_COLOR_PRESET)
                {
                    if (!ColorValueOf(valueText, out target))
                    {
                        Console.Error.WriteLine("色温只支持 6500 | 7500 | 9300 | user1");
                        return 1;
                    }
                }
                else
                {
                    if (!uint.TryParse(valueText, out target))
                    {
                        Console.Error.WriteLine("值必须是数字: " + valueText);
                        return 1;
                    }
                }

                uint settled;
                int attempts;
                string error;
                bool okSet = Ddc.SetValue(code, target, out settled, out attempts, out error);
                string settledText = (code == Ddc.VCP_COLOR_PRESET) ? ColorLabel(settled) : settled.ToString();
                if (okSet) Console.WriteLine(name + " 已设为 " + settledText + "（第 " + attempts + " 次写入生效）");
                else Console.Error.WriteLine("设置失败：" + error + "（当前 " + settledText + "）");
                return okSet ? 0 : 1;
            }
        }

        // ---------- 面板 / 模式列表 / 使用时长 ----------
        if (args.Length > 0)
        {
            string ap = args[0].ToLowerInvariant();
            try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true }); }
            catch { }

            if (ap == "--modes")
            {
                DisplayModes.Mode cur = DisplayModes.Current();
                Console.WriteLine("当前: " + cur.Width + "x" + cur.Height + " @ " + cur.Hertz + "Hz " + cur.Bits + "bit");
                Console.WriteLine("可用模式:");
                foreach (DisplayModes.Mode m in DisplayModes.List()) Console.WriteLine("  " + m.Label);
                return 0;
            }
            if (ap == "--usage")
            {
                Dictionary<string, long[]> map = Usage.Load();
                if (map.Count == 0) { Console.WriteLine("还没有使用记录（程序运行 1 分钟后开始统计）"); return 0; }
                List<string> keys = new List<string>(map.Keys);
                keys.Sort();
                foreach (string k in keys)
                {
                    long[] v = map[k];
                    Console.WriteLine(k + "  亮屏 " + Usage.Format(v[0]) + "  使用中 " + Usage.Format(v[1]));
                }
                return 0;
            }
            if (ap == "--gamma-reset")
            {
                Console.WriteLine(Gamma.Reset() ? "Gamma 已恢复标准曲线" : "Gamma 恢复失败");
                return 0;
            }
            if (ap == "--gamma-test")
            {
                bool ok; string err; int dd; double rb;
                int[,] cases = new int[,] {
                    {100, 6500}, {80, 6500}, {70, 6500}, {60, 6500}, {50, 6500},
                    {100, 5000}, {100, 3500}, {90, 3500}, {80, 3500}, {60, 3500},
                    {50, 3500}, {65, 4500}, {80, 4000}, {100, 3000}
                };
                Console.WriteLine("请求(调光/色温)   系统是否接受   实际调光   回读");
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    int dim = cases[i, 0];
                    int kel = cases[i, 1];
                    ushort[] ramp = Gamma.BuildRamp(dim, kel);
                    ok = Gamma.ApplyRaw(ramp, false, out err);
                    System.Threading.Thread.Sleep(120);
                    Gamma.TryReadBack(out dd, out rb);
                    Console.WriteLine("  " + dim + "% / " + kel + "K".PadRight(1)
                        + "        " + (ok ? "接受" : "拒绝").PadRight(6)
                        + "  " + Gamma.LastAchievedDim + "%"
                        + "      " + dd + "% 红/绿 " + rb.ToString("0.00"));
                    if (!ok) Console.WriteLine("      原因: " + err);
                    Gamma.Reset();
                    System.Threading.Thread.Sleep(120);
                }
                return 0;
            }
            if (ap == "--settings")
            {
                int secs2 = 20;
                if (args.Length > 1)
                {
                    int parsed2;
                    if (int.TryParse(args[1], out parsed2)) secs2 = parsed2;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TrayApp settingsApp = new TrayApp();
                settingsApp.SettingsDemo(secs2);
                Application.Run(settingsApp);
                return 0;
            }
            if (ap == "--panel" || ap == "--panel-demo")
            {
                int secs = 0;
                int tab = 0;
                if (ap == "--panel-demo")
                {
                    secs = 14;
                    if (args.Length > 1)
                    {
                        int parsed;
                        if (int.TryParse(args[1], out parsed)) secs = parsed;
                    }
                    if (args.Length > 2)
                    {
                        int t2;
                        if (int.TryParse(args[2], out t2)) tab = t2;
                    }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TrayApp app = new TrayApp();
                app.PanelDemo(secs, tab);
                Application.Run(app);
                return 0;
            }
        }

        // ---------- 自检：把菜单树弹出来（不碰鼠标），供截图核对 ----------
        if (args.Length > 0 && args[0].ToLowerInvariant() == "--demo-menu")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            TrayApp demo = new TrayApp();
            System.Windows.Forms.Timer show = new System.Windows.Forms.Timer();
            show.Interval = 900;
            show.Tick += delegate
            {
                show.Stop();
                ContextMenuStrip m = demo.MenuStrip;
                m.Show(new Point(Screen.PrimaryScreen.Bounds.Width - 780, Screen.PrimaryScreen.Bounds.Height - 560));
                foreach (ToolStripItem it in m.Items)
                {
                    if (it.Text == "亮度")
                    {
                        ((ToolStripMenuItem)it).ShowDropDown();
                        break;
                    }
                }
                System.Windows.Forms.Timer quit = new System.Windows.Forms.Timer();
                quit.Interval = 15000;
                quit.Tick += delegate { quit.Stop(); demo.Shutdown(); };
                quit.Start();
            };
            show.Start();
            Application.Run(demo);
            return 0;
        }

        // ---------- 托盘模式 ----------
        bool createdNew;
        single = new Mutex(true, "HKC-MonitorSwitchTray-SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("显示器输入源切换工具已经在托盘中运行了。", "显示器输入源切换",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
        GC.KeepAlive(single);
        return 0;
    }

    private static bool ResolveVcp(string name, out byte code)
    {
        switch (name.ToLowerInvariant())
        {
            case "brightness": case "bright": code = Ddc.VCP_BRIGHTNESS; return true;
            case "contrast": code = Ddc.VCP_CONTRAST; return true;
            case "color": case "colorpreset": case "colortemp": code = Ddc.VCP_COLOR_PRESET; return true;
            case "redgain": code = 0x16; return true;
            case "greengain": code = 0x18; return true;
            case "bluegain": code = 0x1A; return true;
            case "volume": code = 0x62; return true;
            case "mute": code = 0x8D; return true;
            case "usagetime": case "hours": code = 0xC0; return true;
            case "power": code = 0xD6; return true;
            case "input": code = Ddc.VCP_INPUT_SOURCE; return true;
            default: code = 0; return false;
        }
    }

    private static string ColorLabel(uint v)
    {
        switch (v)
        {
            case 0x05: return "6500K";
            case 0x06: return "7500K";
            case 0x08: return "9300K";
            case 0x0B: return "用户1";
            default: return "0x" + v.ToString("X2");
        }
    }

    private static bool ColorValueOf(string text, out uint v)
    {
        v = 0;
        string t = text.Trim().ToLowerInvariant();
        switch (t)
        {
            case "6500": case "6500k": v = 0x05; return true;
            case "7500": case "7500k": v = 0x06; return true;
            case "9300": case "9300k": v = 0x08; return true;
            case "user1": case "user": case "0b": v = 0x0B; return true;
        }
        // 也接受原始数值（0-255），方便排查显示器固件实际接受的取值
        int raw;
        if (int.TryParse(t, out raw) && raw >= 0 && raw <= 255)
        {
            v = (uint)raw;
            return true;
        }
        return false;
    }
}
