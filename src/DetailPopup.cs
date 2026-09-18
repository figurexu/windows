using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsMonitor
{
    /// <summary>
    /// 详细数据卡：点击任务栏信息条后弹出。现代卡片风格（圆角 + 投影 + 双列布局），
    /// 淡入淡出动画，展示 CPU / 显卡 当前/峰值/最低 温度功耗频率、GPU 降频原因与散热判断。
    /// 使用 UpdateLayeredWindow 渲染，支持柔和投影与平滑动画。
    /// </summary>
    public class DetailPopup : Form
    {
        private const int DesignW = 560;
        private const int DesignH = 352;

        private readonly Timer _autoHide;
        private readonly Timer _anim;
        private Rectangle _closeRect;
        private float _scale = 1f;
        private byte _alpha;
        private bool _closing;
        private bool _rendering;
        private bool _disposed;

        // 数据
        private bool _cpuOk, _gpuOk, _cpuBlocked, _cpuFromAida64;
        private float? _cpuTemp, _cpuPower, _cpuClock;
        private float? _gpuTemp, _gpuPower, _gpuClock;
        private string _gpuPerfCap = "";
        private MainForm.MetricData _cpuM0, _cpuM1, _cpuM2, _gpuM0, _gpuM1, _gpuM2;

        // 像素字号（96DPI 基准，随 ScaleTransform 等比放大）
        private readonly Font _fontTitle = new Font("Segoe UI Variable Text", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _fontHead = new Font("Segoe UI Variable Text", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _fontLabel = new Font("Segoe UI Variable Text", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font _fontValue = new Font("Segoe UI Variable Display", 24f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _fontSmall = new Font("Segoe UI Variable Text", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font _fontNote = new Font("Segoe UI Variable Text", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        private static readonly Color ColorCard = Color.FromArgb(0x21, 0x25, 0x2F);
        private static readonly Color ColorCol = Color.FromArgb(0x17, 0x1B, 0x23);
        private static readonly Color ColorBorder = Color.FromArgb(0x3A, 0x42, 0x52);
        private static readonly Color ColorText = Color.FromArgb(0xF2, 0xF5, 0xFA);
        private static readonly Color ColorDim = Color.FromArgb(0x98, 0xA2, 0xB4);
        private static readonly Color ColorFaint = Color.FromArgb(0x6B, 0x76, 0x89);
        private static readonly Color ColorCpu = Color.FromArgb(0x5C, 0xCC, 0xFC);
        private static readonly Color ColorGpu = Color.FromArgb(0x42, 0xDF, 0xA8);
        private static readonly Color ColorPower = Color.FromArgb(0xFC, 0xC6, 0x54);
        private static readonly Color ColorClock = Color.FromArgb(0x8C, 0xC2, 0xFF);
        private static readonly Color ColorOk = Color.FromArgb(0x42, 0xDF, 0xA8);
        private static readonly Color ColorWarn = Color.FromArgb(0xFC, 0xC6, 0x54);
        private static readonly Color ColorErr = Color.FromArgb(0xFD, 0x80, 0x80);

        public DetailPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            UpdateCloseRect();

            _autoHide = new Timer();
            _autoHide.Interval = 20000;
            _autoHide.Tick += delegate { BeginClose(); };

            _anim = new Timer();
            _anim.Interval = 14;
            _anim.Tick += OnAnimTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _scale = DeviceDpi / 96f;
            if (_scale < 1.4f) _scale = 1.4f;
            if (_scale > 2.2f) _scale = 2.2f;
            Size = new Size((int)Math.Round(DesignW * _scale), (int)Math.Round(DesignH * _scale));
            UpdateCloseRect();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000; // 工具窗口 + 不抢焦点 + 分层
                return cp;
            }
        }

        private void UpdateCloseRect()
        {
            _closeRect = new Rectangle(
                (int)Math.Round((DesignW - 38) * _scale),
                (int)Math.Round(12 * _scale),
                (int)Math.Round(26 * _scale),
                (int)Math.Round(26 * _scale));
        }

        public void UpdateData(HardwareSnapshot snap, MainForm.PanelData cpu, MainForm.PanelData gpu)
        {
            _cpuOk = snap.CpuName.Length > 0;
            _cpuBlocked = snap.CpuBlocked;
            _cpuFromAida64 = snap.CpuFromAida64;
            _cpuTemp = snap.CpuTemperature;
            _cpuPower = snap.CpuPower;
            _cpuClock = snap.CpuClock;
            _gpuOk = snap.GpuName.Length > 0;
            _gpuTemp = snap.GpuTemperature;
            _gpuPower = snap.GpuPower;
            _gpuClock = snap.GpuClock;
            _gpuPerfCap = snap.GpuPerfCapReason;
            _cpuM0 = cpu.Metrics[0]; _cpuM1 = cpu.Metrics[1]; _cpuM2 = cpu.Metrics[2];
            _gpuM0 = gpu.Metrics[0]; _gpuM1 = gpu.Metrics[1]; _gpuM2 = gpu.Metrics[2];
            if (Visible && !_rendering) Render();
        }

        public void ShowAbove(Rectangle anchor)
        {
            Screen s = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
            Rectangle wa = s.WorkingArea;
            int x = anchor.Left;
            if (x + Width > wa.Right - 8) x = wa.Right - 8 - Width;
            if (x < wa.Left + 8) x = wa.Left + 8;

            bool taskbarBottom = anchor.Top > wa.Bottom - 200;
            int y = taskbarBottom ? anchor.Top - Height - 8 : anchor.Bottom + 8;
            if (y < wa.Top + 8) y = wa.Top + 8;
            if (y + Height > wa.Bottom - 8) y = wa.Bottom - 8 - Height;

            SetBounds(x, y, Width, Height);

            if (Visible)
            {
                // 已在显示中：直接回到完全不透明
                _closing = false;
                _alpha = 255;
                _anim.Stop();
                Render();
                return;
            }

            // 先以透明渲染，再显示，避免闪白
            _alpha = 0;
            _closing = false;
            Render();
            Show();
            _anim.Start();
            _autoHide.Stop();
            _autoHide.Start();
        }

        public void HidePopup()
        {
            if (!Visible) return;
            BeginClose();
        }

        private void BeginClose()
        {
            _autoHide.Stop();
            if (!Visible) return;
            _closing = true;
            _anim.Start();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            if (_closing)
            {
                if (_alpha <= 20)
                {
                    _alpha = 0;
                    _anim.Stop();
                    _closing = false;
                    Hide();
                    return;
                }
                _alpha = (byte)Math.Max(0, _alpha - 34);
                Render();
            }
            else
            {
                if (_alpha >= 245)
                {
                    _alpha = 255;
                    _anim.Stop();
                    Render();
                    return;
                }
                _alpha = (byte)Math.Min(255, _alpha + 34);
                Render();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) BeginClose();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateCloseRect();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 分层窗口由 UpdateLayeredWindow 渲染，不在此绘制
        }

        private void Render()
        {
            if (!Visible || Width <= 0 || Height <= 0) return;
            if (_rendering) return;
            _rendering = true;
            try
            {
                using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                        g.Clear(Color.Transparent);
                        g.ScaleTransform(_scale, _scale);
                        DrawCard(g);
                    }
                    SetBitmap(bmp, _alpha);
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        private void DrawCard(Graphics g)
        {
            // 投影（多层半透明圆角叠加，模拟柔和阴影）
            for (int i = 3; i >= 1; i--)
            {
                using (GraphicsPath sp = Rounded(3f, 3f + i * 1.2f, DesignW - 6f, DesignH - 6f, 16f))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(26 - i * 5, 0, 0, 0)))
                    g.FillPath(sb, sp);
            }

            // 主卡片
            using (GraphicsPath card = Rounded(0f, 0f, DesignW - 1f, DesignH - 1f, 16f))
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new RectangleF(0f, 0f, DesignW, DesignH),
                    Color.FromArgb(0x26, 0x2A, 0x35),
                    Color.FromArgb(0x1C, 0x20, 0x29),
                    LinearGradientMode.Vertical))
                    g.FillPath(bg, card);
                using (Pen border = new Pen(Color.FromArgb(140, 0x4A, 0x53, 0x66), 1f))
                    g.DrawPath(border, card);
            }

            // 头部
            using (SolidBrush t = new SolidBrush(ColorText))
                g.DrawString("硬件监控", _fontTitle, t, 22f, 14f);
            using (SolidBrush st = new SolidBrush(ColorDim))
                g.DrawString("实时传感器详情", _fontNote, st, 22f, 40f);

            // 实时状态胶囊
            string live = "● 实时";
            SizeF liveSize = g.MeasureString(live, _fontSmall);
            float pillW = liveSize.Width + 22f;
            using (GraphicsPath pp = Rounded(DesignW - pillW - 48f, 16f, pillW, 24f, 12f))
            using (SolidBrush pb = new SolidBrush(Color.FromArgb(40, 0x42, 0xDF, 0xA8)))
                g.FillPath(pb, pp);
            using (SolidBrush lv = new SolidBrush(ColorOk))
                g.DrawString(live, _fontSmall, lv, DesignW - pillW - 38f, 20.5f);
            using (SolidBrush cx = new SolidBrush(ColorDim))
                g.DrawString("×", _fontTitle, cx, DesignW - 34f, 13f);

            // 双列
            float colTop = 66f;
            float colH = 216f;
            float gap = 12f;
            float colW = (DesignW - 22f * 2f - gap) / 2f;
            DrawColumn(g, 22f, colTop, colW, colH, ColorCpu, "CPU",
                _cpuFromAida64 ? "AIDA64" : (_cpuBlocked ? "受限" : "原生"),
                _cpuOk && !_cpuBlocked, _cpuTemp, _cpuPower, _cpuClock, _cpuM0, _cpuM1, _cpuM2);
            DrawColumn(g, 22f + colW + gap, colTop, colW, colH, ColorGpu, "GPU",
                "NVAPI", _gpuOk, _gpuTemp, _gpuPower, _gpuClock, _gpuM0, _gpuM1, _gpuM2);

            // 底部：降频原因 + 散热判断
            DrawDivider(g, 296f);

            string reason = MapPerfCap(_gpuPerfCap);
            using (SolidBrush rb = new SolidBrush(reason.StartsWith("⚠") ? ColorWarn : ColorDim))
                g.DrawString(reason, _fontNote, rb, 22f, 306f);

            Color cvColor;
            string cpuVerdict = CpuVerdict(out cvColor);
            Color gvColor;
            string gpuVerdict = GpuVerdict(out gvColor);
            using (SolidBrush v1 = new SolidBrush(cvColor))
                g.DrawString(cpuVerdict, _fontNote, v1, 22f, 328f);
            using (SolidBrush v2 = new SolidBrush(gvColor))
                g.DrawString(gpuVerdict, _fontNote, v2, 310f, 328f);
        }

        private void DrawColumn(Graphics g, float x, float y, float w, float h, Color accent, string name,
            string source, bool ok, float? temp, float? power, float? clock,
            MainForm.MetricData m0, MainForm.MetricData m1, MainForm.MetricData m2)
        {
            using (GraphicsPath col = Rounded(x, y, w, h, 12f))
            {
                using (SolidBrush bg = new SolidBrush(ColorCol))
                    g.FillPath(bg, col);
                using (Pen border = new Pen(Color.FromArgb(120, 0x2E, 0x35, 0x42), 1f))
                    g.DrawPath(border, col);
            }

            // 列头
            using (SolidBrush dot = new SolidBrush(accent))
                g.FillEllipse(dot, x + 14f, y + 14f, 9f, 9f);
            using (SolidBrush hb = new SolidBrush(ColorText))
                g.DrawString(name, _fontHead, hb, x + 30f, y + 9f);
            SizeF srcSize = g.MeasureString(source, _fontSmall);
            using (SolidBrush sb = new SolidBrush(ColorFaint))
                g.DrawString(source, _fontSmall, sb, x + w - 14f - srcSize.Width, y + 11f);

            using (Pen line = new Pen(Color.FromArgb(50, 0xFF, 0xFF, 0xFF), 1f))
                g.DrawLine(line, x + 14f, y + 36f, x + w - 14f, y + 36f);

            // 三个指标
            float ty = y + 44f;
            DrawTile(g, x, ty, "温度", ok && temp.HasValue ? temp.Value.ToString("F0") + "°C" : "--", TempColor(temp),
                ok && m0.MaxValue.HasValue ? "峰值 " + m0.MaxValue.Value.ToString("F0") + "°C" : "--", null, w);
            DrawTile(g, x, ty + 56f, "功耗", ok && power.HasValue ? power.Value.ToString("F0") + "W" : "--", ColorPower,
                ok && m1.MaxValue.HasValue ? "峰值 " + m1.MaxValue.Value.ToString("F0") + "W" : "--", null, w);
            DrawTile(g, x, ty + 112f, "频率",
                ok && clock.HasValue ? (clock.Value / 1000f).ToString("F2") + "G" : "--", ColorClock,
                ok && m2.MaxValue.HasValue ? "峰值 " + (m2.MaxValue.Value / 1000f).ToString("F2") + "G" : "--",
                ok && m2.MinValue.HasValue ? "最低 " + (m2.MinValue.Value / 1000f).ToString("F2") + "G" : "--", w);
        }

        private void DrawTile(Graphics g, float x, float y, string label, string value, Color valueColor,
            string peak, string min, float colW)
        {
            using (SolidBrush lb = new SolidBrush(ColorDim))
                g.DrawString(label, _fontLabel, lb, x + 14f, y + 2f);
            using (SolidBrush vb = new SolidBrush(valueColor))
                g.DrawString(value, _fontValue, vb, x + 14f, y + 20f);

            float rightX = x + colW - 14f;
            using (SolidBrush pb = new SolidBrush(ColorText))
            {
                if (!string.IsNullOrEmpty(peak))
                {
                    SizeF ps = g.MeasureString(peak, _fontSmall);
                    g.DrawString(peak, _fontSmall, pb, rightX - ps.Width, y + 4f);
                }
            }
            if (!string.IsNullOrEmpty(min))
            {
                using (SolidBrush nb = new SolidBrush(ColorDim))
                {
                    SizeF ns = g.MeasureString(min, _fontSmall);
                    g.DrawString(min, _fontSmall, nb, rightX - ns.Width, y + 22f);
                }
            }
        }

        private void DrawDivider(Graphics g, float y)
        {
            using (Pen pen = new Pen(Color.FromArgb(60, 0xFF, 0xFF, 0xFF), 1f))
                g.DrawLine(pen, 22f, y, DesignW - 22f, y);
        }

        private string MapPerfCap(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "GPU 降频原因：无法获取（需 AIDA64 运行）";
            if (raw.IndexOf("Thermal", StringComparison.OrdinalIgnoreCase) >= 0)
                return "⚠ GPU 受散热限制（Thermal），已降频保护";
            if (raw.IndexOf("Pwr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0)
                return "⚠ GPU 受功耗限制（撞功耗墙）";
            if (raw.IndexOf("Reliability", StringComparison.OrdinalIgnoreCase) >= 0)
                return "GPU 受可靠性电压限制（Reliability Voltage）";
            if (raw.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0)
                return "GPU 空闲（Idle），未满载";
            if (raw.IndexOf("Util", StringComparison.OrdinalIgnoreCase) >= 0)
                return "GPU 负载较低（Utilization），未达到上限";
            return "GPU 降频原因：" + raw;
        }

        private string CpuVerdict(out Color color)
        {
            if (!_cpuOk)
            {
                color = ColorDim;
                return _cpuBlocked ? "CPU 数据不可用" : "CPU 数据不足";
            }
            // 无 AIDA64/HWiNFO 时温度/功耗不可用（仅原生频率），明确提示来源
            if (!_cpuTemp.HasValue && !_cpuPower.HasValue)
            {
                color = ColorDim;
                return "CPU 温度/功耗需 AIDA64 或 HWiNFO";
            }
            float? maxT = _cpuM0 != null ? _cpuM0.MaxValue : null;
            float? maxC = _cpuM2 != null ? _cpuM2.MaxValue : null;
            if (maxT.HasValue && maxT.Value >= 90f)
            {
                color = ColorWarn;
                return "CPU ⚠ 峰值 " + maxT.Value.ToString("F0") + "°C 接近上限";
            }
            if (maxC.HasValue && _cpuClock.HasValue && maxC.Value > 0f &&
                _cpuClock.Value / maxC.Value < 0.7f && _cpuTemp.HasValue && _cpuTemp.Value >= 85f)
            {
                color = ColorErr;
                return "CPU ⚠ 疑似散热降频";
            }
            color = ColorOk;
            return "CPU 散热正常";
        }

        private string GpuVerdict(out Color color)
        {
            float? maxT = _gpuM0 != null ? _gpuM0.MaxValue : null;
            if (maxT.HasValue && maxT.Value >= 85f)
            {
                color = ColorWarn;
                return "GPU ⚠ 峰值 " + maxT.Value.ToString("F0") + "°C 偏高";
            }
            color = ColorOk;
            return "GPU 散热正常";
        }

        private Color TempColor(float? v)
        {
            if (!v.HasValue) return ColorDim;
            if (v.Value < 60f) return ColorOk;
            if (v.Value < 80f) return ColorWarn;
            return ColorErr;
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(x, y, d, d, 180f, 90f);
            path.AddArc(x + w - d, y, d, d, 270f, 90f);
            path.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            path.AddArc(x, y + h - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // ---------- UpdateLayeredWindow 渲染 ----------

        private void SetBitmap(Bitmap bmp, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);

            SIZE size = new SIZE(bmp.Width, bmp.Height);
            POINT src = new POINT(0, 0);
            POINT dst = new POINT(Left, Top);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = alpha;
            blend.AlphaFormat = 1;

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);

            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_autoHide != null) _autoHide.Dispose();
                if (_anim != null) _anim.Dispose();
                _fontTitle.Dispose(); _fontHead.Dispose(); _fontLabel.Dispose();
                _fontValue.Dispose(); _fontSmall.Dispose(); _fontNote.Dispose();
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
