using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// =====================================================================
//  护眼叠加 / 自定义夜间模式
//
//  设计要点（针对"截图颜色与实际不一致"的修复）：
//  1) 叠加层窗体一律加 WDA_EXCLUDEFROMCAPTURE，屏幕上看得到暖色，
//     但任何截图 / 录屏 API 抓到的都是未着色的原始画面 —— 与 Win11 夜灯一致。
//  2) 同时提供"色彩曲线"模式（SetDeviceGammaRamp），原理与系统夜灯相同，
//     本身就天然不进截图。但开启"自动管理颜色(ACM)"或 HDR 时该接口会被
//     驱动忽略，所以自动模式下会先检测，ACM/HDR 打开时自动改用叠加层。
//  3) 基线 gamma 曲线落盘保存，程序异常退出留下残色时，下次启动会自动还原。
// =====================================================================

public static class AppState
{
    public static double Strength = 0.13;   // 0..0.6
    public static int Warmth = 40;          // 0..100
    public static bool Enabled = true;
    public static int Mode = 0;             // 0=自动 1=色彩曲线 2=叠加层

    static string dir, path;
    public static event Action Changed;

    static AppState()
    {
        try { dir = Path.GetDirectoryName(Application.ExecutablePath); } catch { dir = null; }
        if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        path = Path.Combine(dir, "config.ini");
    }

    public static string DataDir { get { return dir; } }

    public static Color TintColor()
    {
        double t = Math.Max(0, Math.Min(100, Warmth)) / 100.0;
        int r = 255;
        int g = (int)(210 - 90 * t);
        int b = (int)(150 - 130 * t);
        return Color.FromArgb(Clamp(r), Clamp(g), Clamp(b));
    }
    static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

    public static void Raise() { Save(); if (Changed != null) Changed(); }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(path, new string[] {
                "Strength=" + Strength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Warmth=" + Warmth,
                "Enabled=" + (Enabled ? "1" : "0"),
                "Mode=" + Mode
            });
        }
        catch { }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var ln in File.ReadAllLines(path))
            {
                var p = ln.Split('=');
                if (p.Length != 2) continue;
                var k = p[0].Trim(); var v = p[1].Trim();
                if (k == "Strength") { double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) Strength = Math.Max(0, Math.Min(0.6, d)); }
                else if (k == "Warmth") { int w; if (int.TryParse(v, out w)) Warmth = Math.Max(0, Math.Min(100, w)); }
                else if (k == "Enabled") { Enabled = v == "1"; }
                else if (k == "Mode") { int m; if (int.TryParse(v, out m)) Mode = Math.Max(0, Math.Min(2, m)); }
            }
        }
        catch { }
    }
}

// ---------------------------------------------------------------------
//  显示器 gamma 曲线读写
// ---------------------------------------------------------------------
public static class Gamma
{
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDC(string drv, string dev, string port, IntPtr dm);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // ---- DisplayConfig：查询 HDR / 自动颜色管理(ACM) ----
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_TARGET { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; public ulong pad0; public ulong pad1; public ulong pad2; public ulong pad3; }
    [StructLayout(LayoutKind.Sequential)] struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] struct ADVANCED_COLOR_INFO { public DEVICE_INFO_HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topoId);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO info);

    /// <summary>true = 有显示器启用了 HDR 或"自动管理颜色(ACM)"，此时 gamma 曲线会被驱动忽略。</summary>
    public static bool IsWideColorActive()
    {
        try
        {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return false;
            if (np == 0) return false;
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < np; i++)
            {
                var info = new ADVANCED_COLOR_INFO();
                info.header.type = 9; // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
                info.header.size = (uint)Marshal.SizeOf(typeof(ADVANCED_COLOR_INFO));
                info.header.adapterId = paths[i].targetInfo.adapterId;
                info.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref info) != 0) continue;
                // bit0=支持, bit1=已启用(即 HDR/ACM 正在生效), bit2=强制宽色域(Intel 在 SDR 下也可能置位，不能作为判据)
                bool enabled = (info.value & 2) != 0;
                if (enabled) return true;
            }
        }
        catch { }
        return false;
    }

    public static string[] ActiveDisplays()
    {
        var list = new List<string>();
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 0x1) == 0) continue;   // 未附加到桌面
                list.Add(dd.DeviceName);
            }
        }
        catch { }
        if (list.Count == 0) list.Add(@"\\.\DISPLAY1");
        return list.ToArray();
    }

    static IntPtr OpenDc(string dev, out bool owned)
    {
        IntPtr hdc = IntPtr.Zero;
        owned = false;
        try
        {
            if (!string.IsNullOrEmpty(dev))
            {
                hdc = CreateDC("DISPLAY", dev, null, IntPtr.Zero);
                if (hdc != IntPtr.Zero) { owned = true; return hdc; }
            }
            hdc = GetDC(IntPtr.Zero);
            owned = false;
        }
        catch { }
        return hdc;
    }

    public static bool Read(string dev, out ushort[] ramp)
    {
        ramp = null;
        bool owned;
        IntPtr hdc = OpenDc(dev, out owned);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            var buf = new ushort[768];
            if (!GetDeviceGammaRamp(hdc, buf)) return false;
            ramp = buf;
            return true;
        }
        catch { return false; }
        finally { if (owned) DeleteDC(hdc); else ReleaseDC(IntPtr.Zero, hdc); }
    }

    public static bool Write(string dev, ushort[] ramp)
    {
        bool owned;
        IntPtr hdc = OpenDc(dev, out owned);
        if (hdc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(hdc, ramp); }
        catch { return false; }
        finally { if (owned) DeleteDC(hdc); else ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>在基线曲线上叠加暖色：out = (1-s)*base + s*tint（与叠加层 alpha 混合等价）。</summary>
    public static ushort[] BuildTinted(ushort[] baseRamp, double strength, Color tint)
    {
        double s = Math.Max(0, Math.Min(0.6, strength));
        var outp = new ushort[768];
        int[] tc = new int[] { tint.R, tint.G, tint.B };
        for (int c = 0; c < 3; c++)
        {
            double tv = tc[c] * 257.0;
            for (int i = 0; i < 256; i++)
            {
                double v = (1.0 - s) * baseRamp[c * 256 + i] + s * tv;
                if (v < 0) v = 0; if (v > 65535) v = 65535;
                outp[c * 256 + i] = (ushort)Math.Round(v);
            }
        }
        return outp;
    }

    public static bool Equal(ushort[] a, ushort[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static bool Close(ushort[] a, ushort[] b, int tol)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs((int)a[i] - (int)b[i]) > tol) return false;
        return true;
    }

    /// <summary>判断 cur 是否只是 base 的逐通道线性变换（即被叠加过颜色，可能上次异常退出残留）。</summary>
    public static bool LooksLikeRemap(ushort[] cur, ushort[] baseRamp)
    {
        try
        {
            for (int c = 0; c < 3; c++)
            {
                int lo = -1, hi = -1;
                for (int i = 0; i < 256; i++)
                {
                    int v = baseRamp[c * 256 + i];
                    if (lo < 0 && v >= 16384) lo = i;
                    if (v <= 49151) hi = i;
                }
                if (lo < 0 || hi < 0 || hi <= lo) return false;
                double x1 = baseRamp[c * 256 + lo], y1 = cur[c * 256 + lo];
                double x2 = baseRamp[c * 256 + hi], y2 = cur[c * 256 + hi];
                if (Math.Abs(x2 - x1) < 1) return false;
                double a = (y2 - y1) / (x2 - x1);
                double b = y1 - a * x1;
                for (int i = 0; i < 256; i++)
                {
                    double exp = a * baseRamp[c * 256 + i] + b;
                    if (exp < 0) exp = 0; if (exp > 65535) exp = 65535;
                    if (Math.Abs(exp - cur[c * 256 + i]) > 3 * 257) return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    // ---- 基线曲线持久化 ----
    static string BasePath(string dev)
    {
        var sb = new StringBuilder();
        foreach (char ch in (dev == null ? "default" : dev))
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return Path.Combine(AppState.DataDir, "ramp_" + sb.ToString() + ".txt");
    }

    public static ushort[] LoadBaseline(string dev)
    {
        try
        {
            string p = BasePath(dev);
            if (!File.Exists(p)) return null;
            var parts = File.ReadAllText(p).Split(new char[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 768) return null;
            var r = new ushort[768];
            for (int i = 0; i < 768; i++) r[i] = ushort.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            return r;
        }
        catch { return null; }
    }

    public static void SaveBaseline(string dev, ushort[] ramp)
    {
        try
        {
            var sb = new StringBuilder(768 * 6);
            for (int i = 0; i < 768; i++) { sb.Append(ramp[i]); sb.Append(' '); }
            File.WriteAllText(BasePath(dev), sb.ToString());
        }
        catch { }
    }
}

// ---------------------------------------------------------------------
//  着色引擎：决定用 gamma 曲线还是叠加层
// ---------------------------------------------------------------------
public static class TintEngine
{
    static readonly Dictionary<string, ushort[]> baselines = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);
    static bool inited;

    public static bool GammaWritable { get; private set; }
    public static int DisplayCount { get { return baselines.Count; } }

    public static void Init()
    {
        if (inited) return;
        inited = true;
        Sync();
    }

    /// <summary>枚举显示器，首次见到某显示器时读取并落盘基线曲线。</summary>
    public static void Sync()
    {
        foreach (string d in Gamma.ActiveDisplays())
        {
            if (baselines.ContainsKey(d)) continue;
            ushort[] cur;
            if (!Gamma.Read(d, out cur)) continue;
            ushort[] saved = Gamma.LoadBaseline(d);
            if (saved != null && saved.Length == 768)
            {
                if (!Gamma.Equal(cur, saved))
                {
                    if (Gamma.LooksLikeRemap(cur, saved))
                    {
                        // 上次异常退出残留的暖色：先还原成基线
                        Gamma.Write(d, saved);
                    }
                    else
                    {
                        // 用户换过校准/ICC，以当前为准
                        saved = cur;
                        Gamma.SaveBaseline(d, cur);
                    }
                }
            }
            else
            {
                saved = cur;
                Gamma.SaveBaseline(d, cur);
            }
            baselines[d] = saved;
        }
    }

    /// <summary>把当前设置写入显示器曲线。返回 false 表示该机器上 gamma 通道不可用。</summary>
    public static bool Apply()
    {
        Sync();
        if (baselines.Count == 0) { GammaWritable = false; return false; }

        bool anyOk = false, anyTried = false;
        foreach (var kv in baselines)
        {
            ushort[] target = kv.Value;
            if (AppState.Enabled)
                target = Gamma.BuildTinted(kv.Value, AppState.Strength, AppState.TintColor());
            if (!Gamma.Write(kv.Key, target)) continue;
            anyTried = true;
            ushort[] back;
            if (Gamma.Read(kv.Key, out back) && Gamma.Close(back, target, 2 * 257)) anyOk = true;
        }
        GammaWritable = anyTried && anyOk;
        return GammaWritable;
    }

    /// <summary>巡检：仅当曲线被驱动/全屏程序重置时才补写，避免无谓重写造成闪烁。</summary>
    public static void Verify()
    {
        if (!GammaWritable) return;
        foreach (var kv in baselines)
        {
            ushort[] target = kv.Value;
            if (AppState.Enabled)
                target = Gamma.BuildTinted(kv.Value, AppState.Strength, AppState.TintColor());
            ushort[] cur;
            if (!Gamma.Read(kv.Key, out cur)) continue;
            if (Gamma.Close(cur, target, 2 * 257)) continue;   // 仍是我们设的值，跳过
            Gamma.Write(kv.Key, target);
        }
    }

    /// <summary>把显示器曲线还原为基线（关闭护眼 / 退出程序时调用）。</summary>
    public static void Restore()
    {
        foreach (var kv in baselines) Gamma.Write(kv.Key, kv.Value);
    }
}

// ---------------------------------------------------------------------
//  叠加层窗体：对截屏/录屏隐藏
// ---------------------------------------------------------------------
public class OverlayForm : Form
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    const int WM_HOTKEY = 0x0312;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;   // Win10 2004+：截图/录屏中完全隐藏该窗口

    System.Windows.Forms.Timer topTimer;

    public bool ExcludeFromCaptureOk { get; private set; }

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = AppState.TintColor();
        TopMost = true;

        RegisterHotKey(Handle, 1, 0x0002 | 0x0001, 0x26);           // Ctrl+Alt+Up   强度+
        RegisterHotKey(Handle, 2, 0x0002 | 0x0001, 0x28);           // Ctrl+Alt+Down 强度-
        RegisterHotKey(Handle, 3, 0x0002 | 0x0001, 0x30);           // Ctrl+Alt+0    开关
        RegisterHotKey(Handle, 5, 0x0002 | 0x0001, 0x57);           // Ctrl+Alt+W    暖度循环

        topTimer = new System.Windows.Forms.Timer();
        topTimer.Interval = 2000;
        topTimer.Tick += delegate
        {
            if (!Visible) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            EnsureCaptureExcluded();
        };
        topTimer.Start();

        AppState.Changed += OnStateChanged;
    }

    void OnStateChanged()
    {
        try { if (IsHandleCreated) BeginInvoke((Action)Apply); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureCaptureExcluded();
    }

    /// <summary>核心修复：让叠加层不出现在任何截图/录屏结果里。</summary>
    public void EnsureCaptureExcluded()
    {
        try
        {
            if (Handle == IntPtr.Zero) return;
            ExcludeFromCaptureOk = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }
        catch { ExcludeFromCaptureOk = false; }
    }

    public void SetActive(bool active)
    {
        if (!active) { if (Visible) Hide(); return; }
        Apply();
    }

    public void Apply()
    {
        if (!AppState.Enabled) { if (Visible) Hide(); return; }
        var vs = SystemInformation.VirtualScreen;
        SetBounds(vs.X, vs.Y, vs.Width, vs.Height);
        try
        {
            BackColor = AppState.TintColor();
            Opacity = Math.Max(0, Math.Min(0.6, AppState.Strength));
        }
        catch { }
        if (!Visible) Show();
        EnsureCaptureExcluded();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020;  // WS_EX_TRANSPARENT (click-through)
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == 1) { AppState.Strength += 0.02; AppState.Raise(); }
            else if (id == 2) { AppState.Strength -= 0.02; AppState.Raise(); }
            else if (id == 3) { AppState.Enabled = !AppState.Enabled; AppState.Raise(); }
            else if (id == 5) { AppState.Warmth = (AppState.Warmth + 20) % 120; if (AppState.Warmth > 100) AppState.Warmth = 0; AppState.Raise(); }
            return;
        }
        base.WndProc(ref m);
    }
}

// ---------------------------------------------------------------------
//  设置窗口
// ---------------------------------------------------------------------
public class SettingsForm : Form
{
    TrackBar tbS, tbW;
    CheckBox cbEnable, cbAuto;
    ComboBox cbMode;
    Label lblS, lblW, lblState;
    bool loading;

    static string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public SettingsForm()
    {
        Text = "护眼叠加 · 设置";
        Width = 400; Height = 430;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        MaximizeBox = false; MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Microsoft YaHei UI", 9f);

        int y = 15;
        cbEnable = new CheckBox() { Text = "启用护眼", Left = 18, Top = y, Width = 340, Checked = AppState.Enabled };
        cbEnable.CheckedChanged += delegate { if (loading) return; AppState.Enabled = cbEnable.Checked; AppState.Raise(); };
        Controls.Add(cbEnable); y += 38;

        lblS = new Label() { Left = 18, Top = y, Width = 340, Text = "强度" };
        Controls.Add(lblS); y += 22;
        tbS = new TrackBar() { Left = 18, Top = y, Width = 350, Minimum = 0, Maximum = 60, TickFrequency = 10, Value = (int)Math.Round(AppState.Strength * 100) };
        tbS.ValueChanged += delegate { if (loading) return; AppState.Strength = tbS.Value / 100.0; UpdateLabels(); AppState.Raise(); };
        Controls.Add(tbS); y += 58;

        lblW = new Label() { Left = 18, Top = y, Width = 340, Text = "暖度" };
        Controls.Add(lblW); y += 22;
        tbW = new TrackBar() { Left = 18, Top = y, Width = 350, Minimum = 0, Maximum = 100, TickFrequency = 20, Value = AppState.Warmth };
        tbW.ValueChanged += delegate { if (loading) return; AppState.Warmth = tbW.Value; UpdateLabels(); AppState.Raise(); };
        Controls.Add(tbW); y += 60;

        Controls.Add(new Label() { Left = 18, Top = y, Width = 340, Text = "着色方式" }); y += 22;
        cbMode = new ComboBox() { Left = 18, Top = y, Width = 350, DropDownStyle = ComboBoxStyle.DropDownList };
        cbMode.Items.Add("自动（优先色彩曲线，不可用时用叠加层）");
        cbMode.Items.Add("色彩曲线（同系统夜灯，最省资源）");
        cbMode.Items.Add("叠加层（对截图/录屏隐藏）");
        cbMode.SelectedIndex = Math.Max(0, Math.Min(2, AppState.Mode));
        cbMode.SelectedIndexChanged += delegate
        {
            if (loading) return;
            AppState.Mode = cbMode.SelectedIndex;
            AppState.Raise();
        };
        Controls.Add(cbMode); y += 30;

        lblState = new Label() { Left = 18, Top = y, Width = 350, Height = 34, ForeColor = Color.FromArgb(90, 90, 90) };
        Controls.Add(lblState); y += 40;

        cbAuto = new CheckBox() { Text = "开机自启", Left = 18, Top = y, Width = 340, Checked = AutostartEnabled() };
        cbAuto.CheckedChanged += delegate { if (loading) return; SetAutostart(cbAuto.Checked); };
        Controls.Add(cbAuto); y += 40;

        var btnClose = new Button() { Text = "关闭", Left = 265, Top = y, Width = 100, Height = 30 };
        btnClose.Click += delegate { Hide(); };
        Controls.Add(btnClose);

        AppState.Changed += OnState;
        UpdateLabels();
        UpdateState();
    }

    void UpdateLabels()
    {
        lblS.Text = "强度：" + tbS.Value + "%";
        lblW.Text = "暖度：" + tbW.Value;
    }

    void UpdateState()
    {
        string s;
        if (!AppState.Enabled) s = "当前：已关闭";
        else if (TrayApp.UsingOverlay) s = "当前：叠加层（截图 / 录屏不会带上暖色）";
        else if (TrayApp.UsingGamma) s = "当前：色彩曲线（同系统夜灯原理，截图不失真）";
        else s = "当前：未生效，请检查显示设置";
        if (Gamma.IsWideColorActive() && !TrayApp.UsingOverlay)
            s += "\n提示：已开启 HDR / 自动管理颜色，色彩曲线可能被忽略。";
        lblState.Text = s;
    }

    void OnState()
    {
        if (InvokeRequired) { try { BeginInvoke((Action)OnState); } catch { } return; }
        loading = true;
        cbEnable.Checked = AppState.Enabled;
        tbS.Value = (int)Math.Round(AppState.Strength * 100);
        tbW.Value = AppState.Warmth;
        cbMode.SelectedIndex = Math.Max(0, Math.Min(2, AppState.Mode));
        UpdateLabels();
        UpdateState();
        loading = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppState.Changed -= OnState;
        base.OnFormClosed(e);
    }

    static bool AutostartEnabled()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false)) { return k != null && k.GetValue("WarmOverlay") != null; } }
        catch { return false; }
    }
    static void SetAutostart(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) return;
                if (on) k.SetValue("WarmOverlay", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("WarmOverlay", false);
            }
        }
        catch { }
    }
}

// ---------------------------------------------------------------------
//  托盘主体
// ---------------------------------------------------------------------
public class TrayApp : ApplicationContext
{
    OverlayForm overlay;
    NotifyIcon tray;
    SettingsForm sf;
    Icon icon;
    ToolStripMenuItem miEnable;
    System.Windows.Forms.Timer keepTimer;

    public static bool UsingOverlay;
    public static bool UsingGamma;

    public TrayApp()
    {
        AppState.Load();
        icon = MakeIcon();

        overlay = new OverlayForm();
        overlay.EnsureCaptureExcluded();

        tray = new NotifyIcon();
        tray.Icon = icon;
        tray.Visible = true;
        tray.Text = "护眼叠加（双击打开设置）";

        var menu = new ContextMenuStrip();
        var miSet = new ToolStripMenuItem("打开设置");
        miSet.Click += delegate { ShowSettings(); };
        miEnable = new ToolStripMenuItem("启用护眼");
        miEnable.Checked = AppState.Enabled;
        miEnable.Click += delegate { AppState.Enabled = !AppState.Enabled; AppState.Raise(); };
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += delegate { tray.Visible = false; Application.Exit(); };
        menu.Items.Add(miSet);
        menu.Items.Add(miEnable);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowSettings(); };

        AppState.Changed += delegate { RefreshAll(); };

        // 驱动/全屏切换会重置 gamma 曲线，定时补写
        keepTimer = new System.Windows.Forms.Timer();
        keepTimer.Interval = 5000;
        keepTimer.Tick += delegate
        {
            if (AppState.Enabled && UsingGamma && AppState.Mode != 2) TintEngine.Verify();
        };
        keepTimer.Start();

        RefreshAll();

        var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\WarmOverlayShowSettings");
        var th = new Thread(delegate ()
        {
            while (true)
            {
                showEv.WaitOne();
                try { if (overlay.IsHandleCreated) overlay.BeginInvoke((Action)ShowSettings); }
                catch { }
            }
        });
        th.IsBackground = true;
        th.Start();
    }

    void RefreshAll()
    {
        if (overlay != null && overlay.IsHandleCreated && overlay.InvokeRequired)
        {
            try { overlay.BeginInvoke((Action)RefreshAll); } catch { }
            return;
        }
        TintEngine.Init();

        UsingGamma = false;
        UsingOverlay = false;

        if (!AppState.Enabled)
        {
            TintEngine.Restore();
            overlay.SetActive(false);
        }
        else if (AppState.Mode == 2)
        {
            TintEngine.Restore();
            UsingOverlay = true;
            overlay.SetActive(true);
        }
        else if (AppState.Mode == 1)
        {
            UsingGamma = TintEngine.Apply();
            overlay.SetActive(false);
        }
        else // 自动
        {
            // 开了 HDR / 自动管理颜色(ACM) 时 gamma 会被驱动忽略，直接走叠加层
            if (Gamma.IsWideColorActive())
            {
                TintEngine.Restore();
                UsingOverlay = true;
                overlay.SetActive(true);
            }
            else
            {
                UsingGamma = TintEngine.Apply();
                if (UsingGamma) overlay.SetActive(false);
                else { UsingOverlay = true; overlay.SetActive(true); }
            }
        }

        if (miEnable != null) miEnable.Checked = AppState.Enabled;
        if (tray != null)
        {
            string how = UsingOverlay ? "叠加层" : (UsingGamma ? "色彩曲线" : "未生效");
            tray.Text = "护眼叠加 · " + (AppState.Enabled ? "开 " : "关 ")
                + (int)Math.Round(AppState.Strength * 100) + "% · " + how;
        }
    }

    void ShowSettings()
    {
        if (sf == null || sf.IsDisposed) sf = new SettingsForm();
        sf.Show();
        sf.Activate();
        sf.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (tray != null) tray.Visible = false;
            if (keepTimer != null) keepTimer.Stop();
            TintEngine.Restore();
        }
        base.Dispose(disposing);
    }

    public static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(Color.FromArgb(255, 147, 41))) g.FillEllipse(b, 1, 1, 30, 30);
            using (var p = new Pen(Color.White, 2)) g.DrawEllipse(p, 2, 2, 28, 28);
            using (var f = new Font("Microsoft YaHei", 13, FontStyle.Bold))
            {
                var sf2 = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("暖", f, Brushes.White, new RectangleF(0, 0, 32, 32), sf2);
            }
        }
        IntPtr h = bmp.GetHicon();
        return Icon.FromHandle(h);
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Global\\WarmOverlaySingleInstance", out created))
        {
            if (!created)
            {
                try { using (var ev = EventWaitHandle.OpenExisting("Global\\WarmOverlayShowSettings")) { ev.Set(); } }
                catch { }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new TrayApp());
            }
            finally
            {
                // 无论正常退出还是异常，都把显示器曲线还原
                try { TintEngine.Restore(); } catch { }
            }
        }
    }
}
