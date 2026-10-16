using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

internal static class MonitorInputSwitch
{
    private const byte VCP_INPUT_SOURCE = 0x60;

#if DP1
    private const uint TARGET = 0x0F;
    private const string TARGET_NAME = "DisplayPort-1";
#elif HDMI1
    private const uint TARGET = 0x11;
    private const string TARGET_NAME = "HDMI-1";
#elif HDMI2
    private const uint TARGET = 0x12;
    private const string TARGET_NAME = "HDMI-2";
#else
#error Compile with -define:DP1 or -define:HDMI1 or -define:HDMI2
#endif

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
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    private static List<IntPtr> CollectMonitors(out List<string> descriptions)
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

    private static string NameOf(uint vcp)
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

    private static int Fail(string message)
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            MessageBoxW(IntPtr.Zero, message, "切换显示器输入源", 0x00000010u | 0x00040000u);
        }
        return 1;
    }

    private static int Main(string[] args)
    {
        bool checkOnly = false;
        bool debug = false;
        if (Console.IsOutputRedirected)
        {
            try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
            catch { }
        }
        if (args.Length > 0)
        {
            foreach (string raw in args)
            {
                string a0 = raw.ToLowerInvariant();
                if (a0 == "-c" || a0 == "--check" || a0 == "/check" || a0 == "check") checkOnly = true;
                if (a0 == "-d" || a0 == "--debug" || a0 == "debug") debug = true;
            }
        }
        if (debug)
        {
            Console.WriteLine("[debug] TARGET = 0x" + TARGET.ToString("X2") + " (" + TARGET_NAME + ")");
        }

        List<string> descs;
        List<IntPtr> monitors = CollectMonitors(out descs);
        if (monitors.Count == 0)
        {
            return Fail("找不到可用的物理显示器（没有任何显示设备暴露 DDC/CI 通道）。");
        }

        int handled = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            uint type, cur, max;
            if (!GetVCPFeatureAndVCPFeatureReply(monitors[i], VCP_INPUT_SOURCE, out type, out cur, out max))
            {
                continue; // 该显示器不支持 VCP 0x60
            }
            handled++;
            string label = "#" + (i + 1) + " " + descs[i];

            if (checkOnly)
            {
                Console.WriteLine("当前输入源: " + label + " -> " + NameOf(cur) + " (VCP 0x60 = 0x" + cur.ToString("X2") + ")");
                continue;
            }

            if (cur == TARGET)
            {
                Console.WriteLine(label + " 已经在 " + TARGET_NAME + "，无需切换。");
                continue;
            }

            if (debug)
            {
                if (!SetVCPFeature(monitors[i], VCP_INPUT_SOURCE, TARGET))
                {
                    Console.Error.WriteLine(label + " [debug] SetVCPFeature 失败: " + Marshal.GetLastWin32Error());
                    continue;
                }
                int prev = 0;
                int[] delays = new int[] { 0, 300, 900, 2000, 4000 };
                foreach (int d in delays)
                {
                    if (d > prev) Thread.Sleep(d - prev);
                    prev = d;
                    uint ta, ca, ma;
                    if (GetVCPFeatureAndVCPFeatureReply(monitors[i], VCP_INPUT_SOURCE, out ta, out ca, out ma))
                    {
                        Console.WriteLine("  [debug] t=+" + d + "ms -> 0x" + ca.ToString("X2") + " (" + NameOf(ca) + ")");
                    }
                    else
                    {
                        Console.WriteLine("  [debug] t=+" + d + "ms -> no reply");
                    }
                }
                continue;
            }

            // DDC/CI 写入可能被显示器忽略，且刚写完的回读可能是旧值：写入后轮询确认，必要时重试。
            int attempts = 0;
            uint settled = cur;
            bool switched = false;
            while (attempts < 3 && !switched)
            {
                attempts++;
                if (!SetVCPFeature(monitors[i], VCP_INPUT_SOURCE, TARGET))
                {
                    Console.Error.WriteLine(label + " 切换失败：SetVCPFeature 返回错误 " + Marshal.GetLastWin32Error());
                    break;
                }
                for (int k = 0; k < 6; k++)
                {
                    Thread.Sleep(400);
                    uint tt, cc, mm;
                    if (GetVCPFeatureAndVCPFeatureReply(monitors[i], VCP_INPUT_SOURCE, out tt, out cc, out mm))
                    {
                        settled = cc;
                    }
                    if (settled == TARGET)
                    {
                        break;
                    }
                }
                switched = (settled == TARGET);
                if (!switched && attempts < 3)
                {
                    Console.WriteLine(label + " 第 " + attempts + " 次写入后仍为 " + NameOf(settled) + "，重试…");
                }
            }

            if (switched)
            {
                Console.WriteLine(label + " 已切换到 " + TARGET_NAME + "（原 " + NameOf(cur) + "，第 " + attempts + " 次写入生效）");
            }
            else
            {
                Console.Error.WriteLine(label + " 切换未生效：多次写入后输入源仍为 " + NameOf(settled) + "。");
            }
        }

        if (handled == 0)
        {
            return Fail("显示器没有响应 VCP 0x60（输入源）。请在显示器 OSD 菜单里打开 DDC/CI。");
        }
        return 0;
    }
}
