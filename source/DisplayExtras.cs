using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ============================================================================
//  DisplayExtras —— MonitorBar 那套"DDC 之外"的能力：
//    * Gamma 软件调光 + Gamma 色温（任何接口、任何显示器都能用，但 HDR 下无效）
//    * 分辨率 / 刷新率枚举与切换
//    * 空闲检测 + 每日使用时长统计（存 %APPDATA%\MonitorSwitch\usage.csv）
// ============================================================================

internal static class Gamma
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    public const int DimMin = 50;      // Windows 只接受离标准曲线不远的 Gamma，太暗会被拒
    public const int DimMax = 100;
    public const int KelvinMin = 3000;
    public const int KelvinMax = 6500;

    private static IntPtr DisplayDc()
    {
        return CreateDC("DISPLAY", null, null, IntPtr.Zero);
    }

    /// 实测：Windows/驱动要求每个通道的最亮点 >= 32768（即 50%），且曲线必须单调不减、
    /// 相邻差 < 32768。所以调光和色温会互相挤占——色温压暗某通道后，能调到的"最暗"就变浅了。
    /// 这里在触到下限时按同一比例抬高三个通道：色温比例保持不变，代价是实际调光没到目标值。
    public const double ChannelFloor = 0.5;

    public static int LastAchievedDim = 100;
    public static int LastLimitedKelvin = 6500;

    public static ushort[] BuildRamp(int dimPercent, int kelvin)
    {
        return BuildRampEx(dimPercent, kelvin, null);
    }


    private static ushort[] BuildRampEx(int dimPercent, int kelvin, int[] achievedOut)
    {
        if (dimPercent < DimMin) dimPercent = DimMin;
        if (dimPercent > DimMax) dimPercent = DimMax;
        if (kelvin < KelvinMin) kelvin = KelvinMin;
        if (kelvin > KelvinMax) kelvin = KelvinMax;

        double kr = 1.0, kg = 1.0, kb = 1.0;
        TemperatureToRgb(kelvin, out kr, out kg, out kb);
        double dim = dimPercent / 100.0;

        double fr = dim * kr, fg = dim * kg, fb = dim * kb;
        double fmin = Math.Min(fr, Math.Min(fg, fb));
        double fmax = Math.Max(fr, Math.Max(fg, fb));
        if (fmin < ChannelFloor && fmin > 0.0001)
        {
            double scale = ChannelFloor / fmin;
            fr *= scale; fg *= scale; fb *= scale;
            dim *= scale;
            if (dim > 1.0) dim = 1.0;
        }
        if (fmax > 1.0 && fmax > 0.0001)
        {
            double scale2 = 1.0 / fmax;
            fr *= scale2; fg *= scale2; fb *= scale2;
        }

        ushort[] ramp = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            double v = i * 257.0;
            ramp[i] = Clamp(v * fr);
            ramp[256 + i] = Clamp(v * fg);
            ramp[512 + i] = Clamp(v * fb);
        }
        LastAchievedDim = (int)Math.Round(dim * 100);
        if (achievedOut != null) achievedOut[0] = LastAchievedDim;
        return ramp;
    }

    public static ushort[] BuildIdentityRamp()
    {
        return BuildRamp(100, 6500);
    }

    /// useDesktopDc = true 时用 GetDC(NULL) 而不是 CreateDC("DISPLAY")，两种句柄在个别驱动上表现不同
    public static bool ApplyRaw(ushort[] ramp, bool useDesktopDc, out string error)
    {
        error = "";
        IntPtr hdc = useDesktopDc ? GetDC(IntPtr.Zero) : DisplayDc();
        if (hdc == IntPtr.Zero) { error = "取不到屏幕 DC"; return false; }
        try
        {
            if (!SetDeviceGammaRamp(hdc, ramp)) { error = "系统拒绝了这条 Gamma 曲线"; return false; }
            return true;
        }
        finally
        {
            if (useDesktopDc) ReleaseDC(IntPtr.Zero, hdc);
            else DeleteDC(hdc);
        }
    }

    /// dimPercent 50-100，kelvin 3000-6500（6500 = 不偏色）
    public static bool Apply(int dimPercent, int kelvin, out string error)
    {
        return ApplyRaw(BuildRamp(dimPercent, kelvin), false, out error);
    }

    public static bool Reset()
    {
        IntPtr hdc = DisplayDc();
        if (hdc == IntPtr.Zero) return false;
        try
        {
            ushort[] ramp = new ushort[3 * 256];
            for (int i = 0; i < 256; i++)
            {
                ramp[i] = Clamp(i * 257.0);
                ramp[256 + i] = Clamp(i * 257.0);
                ramp[512 + i] = Clamp(i * 257.0);
            }
            return SetDeviceGammaRamp(hdc, ramp);
        }
        finally { DeleteDC(hdc); }
    }

    /// 读回当前 Gamma 曲线，用来判断有没有被改过（验证用）
    public static bool TryReadBack(out int dimPercentApprox, out double redBias)
    {
        dimPercentApprox = 100;
        redBias = 1.0;
        IntPtr hdc = DisplayDc();
        if (hdc == IntPtr.Zero) return false;
        try
        {
            ushort[] ramp = new ushort[3 * 256];
            if (!GetDeviceGammaRamp(hdc, ramp)) return false;
            // 用第 255 个采样点估算：标准是 255*257 = 65535
            double r = ramp[255] / 65535.0;
            double g = ramp[511] / 65535.0;
            double b = ramp[767] / 65535.0;
            dimPercentApprox = (int)Math.Round(g * 100);
            redBias = (g > 0.001) ? (r / g) : 1.0;
            return true;
        }
        finally { DeleteDC(hdc); }
    }

    private static ushort Clamp(double v)
    {
        if (v < 0) return 0;
        if (v > 65535) return 65535;
        return (ushort)Math.Round(v);
    }

    /// Tanner Helland 的色温近似，返回归一化后的 RGB 系数（最亮通道 = 1）
    private static void TemperatureToRgb(int kelvin, out double r, out double g, out double b)
    {
        double t = kelvin / 100.0;
        double rr, gg, bb;
        if (t <= 66)
        {
            rr = 255;
            gg = 99.4708025861 * Math.Log(t) - 161.1195681661;
            bb = (t <= 19) ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            rr = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            gg = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            bb = 255;
        }
        rr = Clamp255(rr); gg = Clamp255(gg); bb = Clamp255(bb);
        double max = Math.Max(rr, Math.Max(gg, bb));
        if (max < 1) max = 1;
        r = rr / max; g = gg / max; b = bb / max;
    }

    private static double Clamp255(double v)
    {
        if (v < 0) return 0;
        if (v > 255) return 255;
        return v;
    }
}

internal static class DisplayModes
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int CDS_TEST = 0x02;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    internal struct Mode
    {
        public int Width, Height, Hertz, Bits;
        public string Label { get { return Width + " × " + Height + "  @ " + Hertz + " Hz"; } }
    }

    public static Mode Current()
    {
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        Mode m = new Mode();
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
        {
            m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight;
            m.Hertz = dm.dmDisplayFrequency; m.Bits = dm.dmBitsPerPel;
        }
        return m;
    }

    private static List<Mode> cache;
    private static DateTime cacheUtc = DateTime.MinValue;

    /// 切页签时会多次调用（每个分辨率行都要问最高刷新率），枚举一次 200 多个模式太浪费 → 缓存 3 秒
    public static void InvalidateCache() { cache = null; }

    /// 去重后的可用模式列表（同分辨率+刷新率只留一条）
    public static List<Mode> List()
    {
        if (cache != null && (DateTime.UtcNow - cacheUtc).TotalSeconds < 3.0) return cache;
        List<Mode> list = new List<Mode>();
        HashSet<string> seen = new HashSet<string>();
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel < 32) continue;
            string key = dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency;
            if (seen.Contains(key)) continue;
            seen.Add(key);
            Mode m = new Mode();
            m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight;
            m.Hertz = dm.dmDisplayFrequency; m.Bits = dm.dmBitsPerPel;
            list.Add(m);
        }
        list.Sort(delegate(Mode a, Mode b)
        {
            int c = (b.Width * b.Height) - (a.Width * a.Height);
            if (c != 0) return c;
            return b.Hertz - a.Hertz;
        });
        cache = list;
        cacheUtc = DateTime.UtcNow;
        return list;
    }

    public static bool Apply(Mode target, out string error)
    {
        error = "";
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        dm.dmPelsWidth = target.Width;
        dm.dmPelsHeight = target.Height;
        dm.dmDisplayFrequency = target.Hertz;
        dm.dmBitsPerPel = target.Bits;
        dm.dmFields = 0x00080000 | 0x00100000 | 0x00400000 | 0x00040000; // WIDTH|HEIGHT|FREQUENCY|BITSPERPEL

        int test = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL)
        {
            error = "系统拒绝了这个模式（错误码 " + test + "）";
            return false;
        }
        int rc = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc != DISP_CHANGE_SUCCESSFUL)
        {
            error = "切换失败（错误码 " + rc + "）";
            return false;
        }
        InvalidateCache();
        return true;
    }

    /// 只列常用档：16:9 那一组（3840×2160 / 2560×1440 / 1920×1080 / 1600×900 / 1280×720）
    private static readonly int[][] CommonRes = new int[][] {
        new int[] { 3840, 2160 }, new int[] { 2560, 1440 }, new int[] { 1920, 1080 },
        new int[] { 1600, 900 }, new int[] { 1280, 720 }
    };
    /// 常用刷新率（从高到低），59/50/30/29 这类非整数/低频档不列
    public static readonly int[] CommonHz = new int[] { 160, 144, 120, 100, 60 };

    public static bool IsCommonHz(int hz)
    {
        for (int i = 0; i < CommonHz.Length; i++) if (CommonHz[i] == hz) return true;
        return false;
    }

    /// 本机实际可用的常用分辨率（按面积从大到小）
    public static List<int[]> CommonResolutions()
    {
        List<int[]> res = new List<int[]>();
        List<Mode> all = List();
        for (int i = 0; i < CommonRes.Length; i++)
        {
            int w = CommonRes[i][0], h = CommonRes[i][1];
            for (int j = 0; j < all.Count; j++)
            {
                if (all[j].Width == w && all[j].Height == h) { res.Add(CommonRes[i]); break; }
            }
        }
        return res;
    }

    /// 某个分辨率下可用的常用刷新率（从高到低）
    public static List<int> CommonRefreshRates(int w, int h)
    {
        List<int> hz = new List<int>();
        List<Mode> all = List();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Width != w || all[i].Height != h) continue;
            if (!IsCommonHz(all[i].Hertz)) continue;
            if (!hz.Contains(all[i].Hertz)) hz.Add(all[i].Hertz);
        }
        hz.Sort(delegate(int a, int b) { return b.CompareTo(a); });
        return hz;
    }

    /// 该分辨率能用的最高常用刷新率（没有则返回最高的偶数值兜底）
    public static int MaxCommonHz(int w, int h)
    {
        List<int> hz = CommonRefreshRates(w, h);
        if (hz.Count > 0) return hz[0];
        return 60;
    }

    public static bool Supports(int w, int h, int hertz, out int bits)
    {
        bits = 32;
        List<Mode> all = List();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Width == w && all[i].Height == h && all[i].Hertz == hertz)
            {
                bits = all[i].Bits;
                return true;
            }
        }
        return false;
    }

    public static bool Apply(int w, int h, int hertz, out string error)
    {
        int bits;
        if (!Supports(w, h, hertz, out bits))
        {
            error = "本机不支持 " + w + " × " + h + " @ " + hertz + " Hz";
            return false;
        }
        Mode m = new Mode();
        m.Width = w; m.Height = h; m.Hertz = hertz; m.Bits = bits;
        return Apply(m, out error);
    }
}

internal static class Usage
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static int IdleSeconds()
    {
        LASTINPUTINFO li = new LASTINPUTINFO();
        li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref li)) return 0;
        uint idle = (uint)Environment.TickCount - li.dwTime;
        return (int)(idle / 1000);
    }

    private static string DirPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitch"); }
    }
    private static string FilePath { get { return Path.Combine(DirPath, "usage.csv"); } }

    /// date -> [亮屏秒数, 使用中秒数]
    public static Dictionary<string, long[]> Load()
    {
        Dictionary<string, long[]> map = new Dictionary<string, long[]>();
        try
        {
            if (!File.Exists(FilePath)) return map;
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string[] p = line.Split(',');
                if (p.Length < 3) continue;
                long on, act;
                if (!long.TryParse(p[1], out on)) continue;
                if (!long.TryParse(p[2], out act)) continue;
                map[p[0]] = new long[] { on, act };
            }
        }
        catch { }
        return map;
    }

    public static void Add(int onSeconds, int activeSeconds)
    {
        try
        {
            Dictionary<string, long[]> map = Load();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            long[] cur;
            if (!map.TryGetValue(today, out cur)) cur = new long[] { 0, 0 };
            cur[0] += onSeconds;
            cur[1] += activeSeconds;
            map[today] = cur;

            if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
            List<string> keys = new List<string>(map.Keys);
            keys.Sort();
            StringBuilder sb = new StringBuilder();
            foreach (string k in keys) sb.AppendLine(k + "," + map[k][0] + "," + map[k][1]);
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public static string Format(long seconds)
    {
        if (seconds < 60) return seconds + " 秒";
        long m = seconds / 60;
        if (m < 60) return m + " 分钟";
        return (m / 60) + " 小时 " + (m % 60) + " 分";
    }
}
