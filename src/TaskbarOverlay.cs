using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsMonitor
{
    /// <summary>
    /// 任务栏实时显示（标准嵌入方案）：
    /// 通过 SetParent 将窗口嵌入任务栏（Shell_TrayWnd），成为任务栏的一部分——
    /// 任务栏在它就在、任务栏隐藏（全屏）它跟着隐藏、天然不闪不跳。
    /// 支持：左键点击弹出详情（按下即响、150ms 内未移动判定为点击）、按住拖动调整位置（记忆）。
    /// 固定宽度槽位布局：数值变化零重排；悬停不变色不重绘。
    /// </summary>
    public class TaskbarOverlay : Form
    {
        public event EventHandler Clicked;

        private readonly Timer _refreshTimer;
        private readonly Timer _clickTimer;
        private bool _rendering;
        private bool _dirty = true;
        private int _lastMeasuredWidth;

        /// <summary>是否启用任务栏显示（由主窗体托盘/按钮控制，关闭后定时器不得重新显示）</summary>
        public bool OverlayOn = true;

        // 实时数据
        private bool _cpuOk, _gpuOk, _cpuBlocked;
        private float? _cpuTemp, _cpuPower, _cpuClock;
        private float? _gpuTemp, _gpuPower, _gpuClock;

        // 固定槽位宽度（字体确定后计算一次，之后恒定）
        private float _wName, _wTemp, _wPower, _wClock, _wSep;
        private Font _fontLabel;
        private Font _fontValue;
        private float _fontLabelSizePx;
        private float _fontValueSizePx;

        // 嵌入状态
        private IntPtr _tray = IntPtr.Zero;
        private int _posX = 64;     // 相对任务栏左上角（物理像素）
        private int _trayW, _trayH;
        private uint _selfPid;
        private int _lastMoveX = int.MinValue;
        private int _lastMoveY = int.MinValue;
        private int _lastMoveW = int.MinValue;
        private int _lastMoveH = int.MinValue;
        // 自有宽高（物理像素）：完全不依赖 WinForms 的 Size/Width/Height，
        // 避免 SetParent 后 WinForms 布局属性用错误基准触发 SetBounds 导致窗口错位
        private int _winW = 420;
        private int _winH = 40;

        // 拖动/点击判定
        private bool _mouseDown;
        private bool _dragMoved;
        private Point _downPoint;
        private int _downPosX;

        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "taskbar_overlay_pos.txt");

        // 配色
        private static readonly Color ColorCpu = Color.FromArgb(0x5C, 0xCC, 0xFC);
        private static readonly Color ColorGpu = Color.FromArgb(0x42, 0xDF, 0xA8);
        private static readonly Color ColorText = Color.FromArgb(0xF4, 0xF7, 0xFC);
        private static readonly Color ColorDim = Color.FromArgb(0xC2, 0xCC, 0xDC);
        private static readonly Color ColorPower = Color.FromArgb(0xFC, 0xC6, 0x54);
        private static readonly Color ColorClock = Color.FromArgb(0x8C, 0xC2, 0xFF);
        private static readonly Color ColorOk = Color.FromArgb(0x42, 0xDF, 0xA8);
        private static readonly Color ColorWarn = Color.FromArgb(0xFC, 0xC6, 0x54);
        private static readonly Color ColorErr = Color.FromArgb(0xFD, 0x80, 0x80);
        private static readonly Color ColorShadow = Color.FromArgb(120, 0, 0, 0);

        private const float SidePad = 14f;

        public TaskbarOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(420, 40);

            _clickTimer = new Timer();
            _clickTimer.Interval = 150;
            _clickTimer.Tick += delegate
            {
                _clickTimer.Stop();
                if (!_dragMoved && Clicked != null) Clicked(this, EventArgs.Empty);
            };

            _refreshTimer = new Timer();
            _refreshTimer.Interval = 300;
            _refreshTimer.Tick += delegate { RefreshPosition(); };
            _refreshTimer.Start();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// 拦截 WinForms 的布局移动：SetParent 嵌入任务栏后，WinForms 仍按"顶级窗口"
        /// 把屏幕坐标缓存为 Location，任何 SetBounds 都会把屏幕坐标当相对坐标传给
        /// SetWindowPos，把窗口推到错误位置（y=任务栏top+任务栏top）。
        /// 嵌入后一律拒绝 WinForms 移动窗口，仅同步宽高；位置完全由 MoveWindow 控制。
        /// </summary>
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (IsHandleCreated && _tray != IntPtr.Zero)
            {
                if (width > 0) _winW = width;
                if (height > 0) _winH = height;
                _dirty = true;
                return; // 不调用 base，绝不触发 SetWindowPos
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000; // TOOLWINDOW + NOACTIVATE + LAYERED
                return cp;
            }
        }

        /// <summary>嵌入任务栏（标准方案：成为 Shell_TrayWnd 的子窗口）。</summary>
        public void AttachToTaskbar()
        {
            try
            {
                GetWindowThreadProcessId(Handle, out _selfPid);
                LoadPos();
                RefreshPosition();
            }
            catch { }
        }

        public void UpdateValues(HardwareSnapshot snap)
        {
            _cpuOk = snap.CpuName.Length > 0;
            _cpuTemp = snap.CpuTemperature;
            _cpuPower = snap.CpuPower;
            _cpuClock = snap.CpuClock;
            _cpuBlocked = snap.CpuBlocked;
            _gpuOk = snap.GpuName.Length > 0;
            _gpuTemp = snap.GpuTemperature;
            _gpuPower = snap.GpuPower;
            _gpuClock = snap.GpuClock;
            _dirty = true;
        }

        /// <summary>覆盖条在屏幕上的矩形（供详情卡定位）。</summary>
        public Rectangle ScreenBounds
        {
            get
            {
                RECT wr;
                GetWindowRect(Handle, out wr);
                return new Rectangle(wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top);
            }
        }

        /// <summary>
        /// 刷新：确保已嵌入任务栏 → 计算并限制位置（含开始菜单面板避让）→ 按需渲染。
        /// 全屏时任务栏被盖/隐藏，子窗口自然随之不可见，无需额外处理。
        /// </summary>
        public void RefreshPosition()
        {
            if (_rendering) return;
            if (!OverlayOn) { HideIfVisible(); return; }
            try
            {
                if (_tray == IntPtr.Zero)
                {
                    IntPtr tray = FindWindow("Shell_TrayWnd", null);
                    if (tray == IntPtr.Zero) { HideIfVisible(); return; }
                    _tray = tray;
                    SetParent(Handle, tray);
                    int style = GetWindowLong(Handle, GWL_STYLE);
                    SetWindowLong(Handle, GWL_STYLE, style | WS_CHILD);
                }

                RECT r;
                GetWindowRect(_tray, out r);
                int th = r.bottom - r.top;
                if (th <= 2) { HideIfVisible(); return; }
                _trayW = r.right - r.left;
                _trayH = th;

                int desiredW = _lastMeasuredWidth > 0 ? _lastMeasuredWidth : 420;
                int maxW = _trayW - 120;
                if (maxW < 200) maxW = 200;
                if (desiredW > maxW) desiredW = maxW;

                int px = _posX;
                int py = 0;

                // 开始菜单/搜索面板避让：枚举可见顶层窗口，若与覆盖条屏幕矩形相交则右移
                int screenX = r.left + px;
                int screenY = r.top + py;
                int avoidRight = 0;
                EnumWindows(delegate(IntPtr w, IntPtr lp)
                {
                    if (w == Handle) return true;
                    if (!IsWindowVisible(w)) return true;
                    uint wpid;
                    GetWindowThreadProcessId(w, out wpid);
                    if (wpid == _selfPid) return true; // 自身进程（主窗体/弹卡）

                    System.Text.StringBuilder cn = new System.Text.StringBuilder(160);
                    GetClassName(w, cn, cn.Capacity);
                    string cls = cn.ToString();
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                        cls == "Shell_SecondaryTrayWnd" || cls == "EdgeUiInputTopWndClass" ||
                        cls == "Thread Event Target" || cls == "CEF-OSC-WIDGET") return true; // 背景层

                    RECT wr;
                    if (!GetWindowRect(w, out wr)) return true;
                    if (wr.right <= wr.left || wr.bottom <= wr.top) return true;
                    // 全屏背景 CoreWindow（Windows 输入体验等）跳过
                    if (cls == "Windows.UI.Core.CoreWindow")
                    {
                        int sw = GetSystemMetrics(0);
                        int sh = GetSystemMetrics(1);
                        if (wr.left <= 0 && wr.right >= sw - 4 && wr.top <= 0 && wr.bottom >= sh - 4)
                            return true;
                    }
                    // 与覆盖条相交？
                    if (wr.right <= screenX || wr.left >= screenX + desiredW) return true;
                    if (wr.bottom <= screenY || wr.top >= screenY + _winH) return true;
                    if (wr.right + 8 > avoidRight) avoidRight = wr.right + 8;
                    return true;
                }, IntPtr.Zero);
                if (avoidRight > screenX && avoidRight + desiredW <= r.right - 8)
                {
                    px = avoidRight - r.left;
                }

                // 位置限制在任务栏内
                if (px < 6) px = 6;
                if (px + desiredW > _trayW - 6) px = _trayW - 6 - desiredW;
                if (py < 0) py = 0;
                if (py + _winH > th) py = Math.Max(0, th - _winH);

                // 尺寸变化时同步自有宽高（SetBoundsCore 也会拦截，这里兜底）
                if (_winW != desiredW || _winH != th)
                {
                    _winW = desiredW;
                    _winH = th;
                    _dirty = true;
                }

                // 仅在尺寸/位置实际变化时才 MoveWindow（bRepaint=false）：
                // 位置不变时绝不调用——对分层窗口，无谓的 MoveWindow 会让 DWM 重新合成表面，
                // 与每秒数据刷新撞在一起就是"数据一变就闪"。窗口位置只由本方法控制
                // （SetBoundsCore 已拦截 WinForms 的一切移动），判重不会造成错位。
                if (_winW != _lastMoveW || _winH != _lastMoveH || px != _lastMoveX || py != _lastMoveY)
                {
                    MoveWindow(Handle, px, py, _winW, _winH, false);
                    _lastMoveX = px;
                    _lastMoveY = py;
                    _lastMoveW = _winW;
                    _lastMoveH = _winH;
                }

                if (!Visible) Show();
                if (_dirty)
                {
                    _dirty = false;
                    Render();
                }
            }
            catch (Exception ex)
            {
                Log("ERR " + ex.Message);
                try { if (!Visible) Show(); } catch { }
            }
        }

        private void HideIfVisible()
        {
            if (Visible) Hide();
        }

        // ---------- 交互：按下即判定、拖动调整位置 ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _mouseDown = true;
            _dragMoved = false;
            _downPoint = e.Location;
            _downPosX = _posX;
            _clickTimer.Stop();
            _clickTimer.Start(); // 150ms 内未移动 → 视为点击
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_mouseDown) return;
            int dx = e.X - _downPoint.X;
            int dy = e.Y - _downPoint.Y;
            if (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)
            {
                _dragMoved = true;
                _clickTimer.Stop();
                _posX = _downPosX + dx;
                SavePos();
                RefreshPosition(); // 立即跟随，内部会限制范围
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool wasDown = _mouseDown;
                bool moved = _dragMoved;
                _mouseDown = false;
                if (wasDown && !moved)
                {
                    _clickTimer.Stop();
                    if (Clicked != null) Clicked(this, EventArgs.Empty); // 快速点击：松开立即触发
                }
            }
        }

        private void LoadPos()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string[] lines = File.ReadAllLines(ConfigPath);
                    foreach (string l in lines)
                    {
                        if (l != null && l.StartsWith("x="))
                        {
                            int v;
                            if (int.TryParse(l.Substring(2), out v)) _posX = v;
                        }
                    }
                }
            }
            catch { }
        }

        private void SavePos()
        {
            try { File.WriteAllText(ConfigPath, "x=" + _posX + "\r\n"); }
            catch { }
        }

        // ---------- 渲染 ----------

        private void Render()
        {
            if (!Visible || _winW <= 0 || _winH <= 0) return;
            if (_rendering) return;
            _rendering = true;
            try
            {
                EnsureFonts();
                using (Bitmap bmp = new Bitmap(_winW, _winH, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                        g.Clear(Color.Transparent);
                        DrawContent(g);
                    }
                    SetBitmap(bmp);
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        private void EnsureFonts()
        {
            float th = _winH;
            float labelPx = Math.Min(Math.Max(th * 0.155f, 8f), 15f);
            float valuePx = Math.Min(Math.Max(th * 0.24f, 10f), 23f);
            if (_fontValue == null || Math.Abs(_fontValueSizePx - valuePx) > 0.3f || Math.Abs(_fontLabelSizePx - labelPx) > 0.3f)
            {
                if (_fontLabel != null) _fontLabel.Dispose();
                if (_fontValue != null) _fontValue.Dispose();
                _fontLabelSizePx = labelPx;
                _fontValueSizePx = valuePx;
                _fontLabel = new Font("Segoe UI Variable Text", labelPx, FontStyle.Bold, GraphicsUnit.Pixel);
                _fontValue = new Font("Segoe UI Variable Display", valuePx, FontStyle.Bold, GraphicsUnit.Pixel);

                using (Bitmap tmp = new Bitmap(4, 4))
                using (Graphics g = Graphics.FromImage(tmp))
                {
                    _wName = g.MeasureString("CPU", _fontLabel).Width;
                    _wTemp = g.MeasureString("88°C", _fontValue).Width;
                    _wPower = g.MeasureString("999W", _fontValue).Width;
                    _wClock = g.MeasureString("9.99G", _fontValue).Width;
                    _wSep = g.MeasureString("|", _fontLabel).Width;
                    _lastMeasuredWidth = (int)Math.Ceiling(SidePad * 2f + BlockWidth() * 2f + 6f + _wSep + 10f);
                }
            }
        }

        private float BlockWidth()
        {
            return 9f + _wName + 8f + _wTemp + 6f + _wPower + 6f + _wClock;
        }

        private void DrawContent(Graphics g)
        {
            float cy = _winH / 2f;
            float x = SidePad;
            x = DrawBlock(g, x, cy, "CPU", ColorCpu, _cpuOk && !_cpuBlocked, _cpuTemp, _cpuPower, _cpuClock);

            x += 6f;
            DrawText(g, "|", _fontLabel, ColorDim, x, cy);
            x += 10f;

            DrawBlock(g, x, cy, "GPU", ColorGpu, _gpuOk, _gpuTemp, _gpuPower, _gpuClock);
        }

        private float DrawBlock(Graphics g, float x, float cy, string name, Color nameColor, bool ok,
            float? temp, float? power, float? clock)
        {
            using (SolidBrush dot = new SolidBrush(nameColor))
                g.FillEllipse(dot, x, cy - 2.2f, 4.4f, 4.4f);
            float xName = x + 9f;
            DrawText(g, name, _fontLabel, ColorText, xName, cy);

            float xTemp = xName + _wName + 8f;
            DrawText(g, ok && temp.HasValue ? temp.Value.ToString("F0") + "°C" : "--", _fontValue, TempColor(temp), xTemp, cy);

            float xPower = xTemp + _wTemp + 6f;
            DrawText(g, ok && power.HasValue ? power.Value.ToString("F0") + "W" : "--", _fontValue, ColorPower, xPower, cy);

            float xClock = xPower + _wPower + 6f;
            DrawText(g, ok && clock.HasValue ? (clock.Value / 1000f).ToString("F1") + "G" : "--", _fontValue, ColorClock, xClock, cy);

            return xClock + _wClock;
        }

        private void DrawText(Graphics g, string text, Font font, Color color, float x, float cy)
        {
            float y = cy - font.Height / 2f;
            using (SolidBrush sh = new SolidBrush(ColorShadow))
                g.DrawString(text, font, sh, x + 1f, y + 1f);
            using (SolidBrush tb = new SolidBrush(color))
                g.DrawString(text, font, tb, x, y);
        }

        private Color TempColor(float? v)
        {
            if (!v.HasValue) return ColorDim;
            if (v.Value < 60f) return ColorOk;
            if (v.Value < 80f) return ColorWarn;
            return ColorErr;
        }

        private void SetBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);

            SIZE size = new SIZE(bmp.Width, bmp.Height);
            POINT src = new POINT(0, 0);
            // 子窗口：UpdateLayeredWindow 的 pptDst 需要"相对父窗口客户区"坐标。
            // 传屏幕坐标会把窗口每次往下推一个父窗口偏移量（累积错位/抖动）——这就是"闪"的根因。
            RECT wr;
            GetWindowRect(Handle, out wr);
            RECT tr;
            GetWindowRect(_tray, out tr);
            POINT dst = new POINT(wr.left - tr.left, wr.top - tr.top);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);

            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay_debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_refreshTimer != null) _refreshTimer.Dispose();
                if (_clickTimer != null) _clickTimer.Dispose();
                if (_fontLabel != null) _fontLabel.Dispose();
                if (_fontValue != null) _fontValue.Dispose();
            }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; public POINT(int a, int b) { x = a; y = b; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; public SIZE(int a, int b) { cx = a; cy = b; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    }
}
