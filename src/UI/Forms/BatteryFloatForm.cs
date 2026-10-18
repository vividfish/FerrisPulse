// ============================================================================
// 模块名称: BatteryFloatForm.cs
// 所属区域: UI/Forms (业务对话框层)
// 核心职责:
//   常驻电量悬浮小窗。拖到屏幕边缘会贴边并收成细电量条；闲置时只轮流展示在线设备电量；鼠标移入才列出全部设备。不抢前台焦点。
// 依赖关系: Controls.RoundedCard, Models.MouseBatteryInfo
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改本文件逻辑、属性或绘图逻辑时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法（❌ 严禁 $"..." 插值，必须使用 string.Format 或 + 拼接）。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RazerBatteryTray
{
    public class BatteryFloatForm : Form
    {
        public delegate void PositionCommittedHandler(Point location, int edge);
        public event PositionCommittedHandler PositionCommitted;

        private const int EdgeNone = 0;
        private const int EdgeLeft = 1;
        private const int EdgeRight = 2;
        private const int EdgeTop = 3;
        private const int EdgeBottom = 4;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, ref RECT rc);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;
        private const uint SWP_NOSENDCHANGING = 0x0400;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private sealed class FloatDevice
        {
            public string Name;
            public string Status;
            public int Percent;
            public bool Connected;
            public Color Accent;
        }

        private readonly float dpiScale;
        private int anchorX = -9999;
        private int anchorY = -9999;
        private bool placed = false;
        private bool dragging = false;
        private bool hovering = false;
        private int grabX;
        private int grabY;
        private int dockEdge = EdgeNone;
        private int dockX;
        private int dockY;
        private int restX;
        private int restY;

        private FloatDevice[] devices = new FloatDevice[0];
        private int cycleIndex = 0;
        private System.Windows.Forms.Timer cycleTimer;
        private System.Windows.Forms.Timer leaveTimer;

        public BatteryFloatForm(float scale)
        {
            dpiScale = scale < 1.0f ? 1.0f : scale;
            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.FromArgb(17, 19, 25);
            this.DoubleBuffered = true;
            this.Text = "FerrisPulse · 电量";
            this.ClientSize = new Size(CardWidth(), CardHeight());
            IntPtr forceHandle = this.Handle;
            if (forceHandle == IntPtr.Zero)
            {
                CreateHandle();
            }

            cycleTimer = new System.Windows.Forms.Timer();
            cycleTimer.Interval = 2800;
            cycleTimer.Tick += OnCycleTick;
            cycleTimer.Start();

            leaveTimer = new System.Windows.Forms.Timer();
            leaveTimer.Interval = 180;
            leaveTimer.Tick += OnLeaveTick;
        }

        public void SetAnchor(int x, int y, int edge)
        {
            anchorX = x;
            anchorY = y;
            dockEdge = edge;
            if (edge == EdgeLeft || edge == EdgeRight) dockY = y;
            else if (edge == EdgeTop || edge == EdgeBottom) dockX = x;
            placed = false;
        }

        public void ApplyDevices(IList<MouseBatteryInfo> source)
        {
            int n = source == null ? 0 : source.Count;
            List<FloatDevice> list = new List<FloatDevice>();
            for (int i = 0; i < n; i++)
            {
                if (source[i] == null) continue;
                list.Add(Snapshot(source[i]));
            }
            if (list.Count == 0)
            {
                list.Add(DisconnectedDevice());
            }
            devices = list.ToArray();
            if (cycleIndex >= ConnectedCount()) cycleIndex = 0;
            if (hovering && !dragging)
            {
                Present();
            }
            else
            {
                this.Invalidate();
            }
        }

        public void ShowFloat()
        {
            if (!this.IsHandleCreated) return;
            if (!placed)
            {
                PlaceInitial();
                placed = true;
                return;
            }
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSENDCHANGING);
        }

        public void HideFloat()
        {
            if (!this.IsHandleCreated) return;
            if (leaveTimer != null) leaveTimer.Stop();
            hovering = false;
            SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_NOSENDCHANGING);
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
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (dragging) return;
            if (leaveTimer != null) leaveTimer.Stop();
            if (!hovering)
            {
                hovering = true;
                Present();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dragging) return;
            if (leaveTimer != null) leaveTimer.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (leaveTimer != null) leaveTimer.Stop();
            hovering = false;
            dragging = true;
            RECT rc = new RECT();
            GetWindowRect(this.Handle, ref rc);
            Point cursor = Cursor.Position;
            grabX = cursor.X - rc.Left;
            grabY = cursor.Y - rc.Top;
            int cardW = CardWidth();
            int cardH = CardHeight();
            if (grabX < 0) grabX = 0;
            if (grabY < 0) grabY = 0;
            if (grabX > cardW - 1) grabX = cardW / 2;
            if (grabY > cardH - 1) grabY = cardH / 2;
            this.Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point cursor = Cursor.Position;
            int x = cursor.X - grabX;
            int y = cursor.Y - grabY;
            ApplyWindow(x, y, CardWidth(), CardHeight());
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            this.Capture = false;
            RECT rc = new RECT();
            GetWindowRect(this.Handle, ref rc);
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            Point cursor = Cursor.Position;
            Rectangle wa = WorkAreaOf(cursor);
            int edge = DetectEdge(wa, rc.Left, rc.Top, w, h, cursor);
            dockEdge = edge;
            if (edge == EdgeLeft || edge == EdgeRight) dockY = rc.Top;
            else if (edge == EdgeTop || edge == EdgeBottom) dockX = rc.Left;
            else
            {
                restX = rc.Left;
                restY = rc.Top;
            }
            Present();
            CommitRest();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (hovering && !dragging)
            {
                PaintList(e.Graphics);
            }
            else if (dockEdge != EdgeNone && !dragging)
            {
                PaintThinBar(e.Graphics);
            }
            else
            {
                PaintCard(e.Graphics, CurrentDevice());
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (cycleTimer != null)
            {
                cycleTimer.Stop();
                cycleTimer.Dispose();
                cycleTimer = null;
            }
            if (leaveTimer != null)
            {
                leaveTimer.Stop();
                leaveTimer.Dispose();
                leaveTimer = null;
            }
            base.OnFormClosed(e);
        }

        private void OnCycleTick(object sender, EventArgs e)
        {
            if (hovering || dragging) return;
            int online = ConnectedCount();
            if (online < 2) return;
            cycleIndex++;
            if (cycleIndex >= online) cycleIndex = 0;
            this.Invalidate();
        }

        private void OnLeaveTick(object sender, EventArgs e)
        {
            leaveTimer.Stop();
            if (dragging) return;
            RECT rc = new RECT();
            GetWindowRect(this.Handle, ref rc);
            Point cursor = Cursor.Position;
            if (cursor.X >= rc.Left && cursor.X < rc.Right && cursor.Y >= rc.Top && cursor.Y < rc.Bottom)
            {
                return;
            }
            if (!hovering) return;
            hovering = false;
            Present();
        }

        private void PlaceInitial()
        {
            Rectangle wa;
            int x;
            int y;
            if (anchorX > -9000 && anchorY > -9000)
            {
                x = anchorX;
                y = anchorY;
                wa = WorkAreaOf(new Point(x, y));
            }
            else
            {
                wa = Screen.PrimaryScreen.WorkingArea;
                int margin = (int)(18 * dpiScale);
                x = wa.Right - CardWidth() - margin;
                y = wa.Bottom - CardHeight() - margin;
            }
            restX = x;
            restY = y;
            if (dockEdge == EdgeNone)
            {
                dockEdge = DetectEdge(wa, x, y, CardWidth(), CardHeight(), new Point(x + CardWidth() / 2, y + CardHeight() / 2));
            }
            if (dockEdge == EdgeLeft || dockEdge == EdgeRight) dockY = y;
            else if (dockEdge == EdgeTop || dockEdge == EdgeBottom) dockX = x;
            Present();
            if (dockEdge != EdgeNone)
            {
                CommitRest();
            }
        }

        private void Present()
        {
            if (dragging) return;
            Point probe = dockEdge != EdgeNone ? new Point(dockX, dockY) : new Point(restX, restY);
            if (probe.X < -1000) probe = new Point(0, 0);
            Rectangle wa = WorkAreaOf(probe);

            if (hovering)
            {
                int lw = ListWidth();
                int lh = ListHeight();
                int x;
                int y;
                if (dockEdge == EdgeLeft)
                {
                    x = wa.Left;
                    y = FitAlong(dockY, BarLength(), lh, wa.Top, wa.Bottom);
                }
                else if (dockEdge == EdgeRight)
                {
                    x = wa.Right - lw;
                    y = FitAlong(dockY, BarLength(), lh, wa.Top, wa.Bottom);
                }
                else if (dockEdge == EdgeTop)
                {
                    y = wa.Top;
                    x = FitAlong(dockX, BarLength(), lw, wa.Left, wa.Right);
                }
                else if (dockEdge == EdgeBottom)
                {
                    y = wa.Bottom - lh;
                    x = FitAlong(dockX, BarLength(), lw, wa.Left, wa.Right);
                }
                else
                {
                    x = restX;
                    y = restY;
                    if (y + lh > wa.Bottom) y = restY + CardHeight() - lh;
                    if (y < wa.Top) y = wa.Top;
                    if (x + lw > wa.Right) x = wa.Right - lw;
                    if (x < wa.Left) x = wa.Left;
                }
                ApplyWindow(x, y, lw, lh);
                return;
            }

            if (dockEdge != EdgeNone)
            {
                int thick = BarThick();
                int len = BarLength();
                int along = (dockEdge == EdgeLeft || dockEdge == EdgeRight) ? dockY : dockX;
                Point p = DockOrigin(wa, dockEdge, along, thick, len);
                dockX = p.X;
                dockY = p.Y;
                int w = (dockEdge == EdgeLeft || dockEdge == EdgeRight) ? thick : len;
                int h = (dockEdge == EdgeLeft || dockEdge == EdgeRight) ? len : thick;
                ApplyWindow(p.X, p.Y, w, h);
                return;
            }

            int cx = restX;
            int cy = restY;
            if (cx + CardWidth() > wa.Right) cx = wa.Right - CardWidth() - 8;
            if (cy + CardHeight() > wa.Bottom) cy = wa.Bottom - CardHeight() - 8;
            if (cx < wa.Left) cx = wa.Left + 8;
            if (cy < wa.Top) cy = wa.Top + 8;
            restX = cx;
            restY = cy;
            ApplyWindow(cx, cy, CardWidth(), CardHeight());
        }

        private void CommitRest()
        {
            int x = dockEdge != EdgeNone ? dockX : restX;
            int y = dockEdge != EdgeNone ? dockY : restY;
            if (PositionCommitted != null)
            {
                PositionCommitted(new Point(x, y), dockEdge);
            }
        }

        private void ApplyWindow(int x, int y, int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            SetWindowPos(this.Handle, HWND_TOPMOST, x, y, w, h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSENDCHANGING);
            int radius = 8;
            if (dockEdge != EdgeNone && !hovering && !dragging)
            {
                radius = Math.Min(w, h) / 2;
            }
            else
            {
                radius = (int)(8 * dpiScale);
            }
            UpdateRegion(w, h, radius);
            this.Invalidate();
        }

        private void PaintThinBar(Graphics g)
        {
            FloatDevice dev = CurrentDevice();
            g.Clear(Color.FromArgb(32, 36, 46));
            int fillMax = (dockEdge == EdgeLeft || dockEdge == EdgeRight) ? this.Height : this.Width;
            int fill = fillMax;
            if (dev.Connected && dev.Percent >= 0)
            {
                fill = (int)(fillMax * (dev.Percent / 100.0));
                if (fill < 2 && dev.Percent > 0) fill = 2;
                if (fill > fillMax) fill = fillMax;
            }
            else if (!dev.Connected || dev.Percent < 0)
            {
                fill = 0;
            }
            using (SolidBrush brush = new SolidBrush(dev.Accent))
            {
                if (dockEdge == EdgeLeft || dockEdge == EdgeRight)
                {
                    g.FillRectangle(brush, 0, this.Height - fill, this.Width, fill);
                }
                else
                {
                    g.FillRectangle(brush, 0, 0, fill, this.Height);
                }
            }
        }

        private void PaintCard(Graphics g, FloatDevice dev)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(17, 19, 25));
            RectangleF card = new RectangleF(0.5f, 0.5f, this.Width - 1f, this.Height - 1f);
            using (GraphicsPath path = RoundedCard.GetRoundRectF(card, 8f * dpiScale))
            using (Pen pen = new Pen(Color.FromArgb(58, 66, 84), 1f))
            {
                g.DrawPath(pen, path);
            }

            string pct = (dev.Connected && dev.Percent >= 0) ? (dev.Percent.ToString() + "%") : "--%";
            using (Font pctFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush pctBrush = new SolidBrush(dev.Accent))
            {
                g.DrawString(pct, pctFont, pctBrush, 10f * dpiScale, 8f * dpiScale);
            }

            float nameX = 72f * dpiScale;
            RectangleF nameRect = new RectangleF(nameX, 6f * dpiScale, this.Width - nameX - 8f * dpiScale, 18f * dpiScale);
            using (Font nameFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush nameBrush = new SolidBrush(Color.FromArgb(230, 236, 245)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(dev.Name ?? "", nameFont, nameBrush, nameRect, sf);
            }

            using (Font stFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush stBrush = new SolidBrush(Color.FromArgb(150, 160, 178)))
            {
                g.DrawString(dev.Status ?? "", stFont, stBrush, nameX, 26f * dpiScale);
            }
        }

        private void PaintList(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(17, 19, 25));
            RectangleF card = new RectangleF(0.5f, 0.5f, this.Width - 1f, this.Height - 1f);
            using (GraphicsPath path = RoundedCard.GetRoundRectF(card, 8f * dpiScale))
            using (Pen pen = new Pen(Color.FromArgb(58, 66, 84), 1f))
            {
                g.DrawPath(pen, path);
            }

            int pad = (int)(8 * dpiScale);
            int rowH = RowHeight();
            int count = devices == null ? 0 : devices.Length;
            FloatDevice current = CurrentDevice();
            using (Font nameFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (Font pctFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font stFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat nameSf = new StringFormat())
            using (StringFormat pctSf = new StringFormat())
            {
                nameSf.Trimming = StringTrimming.EllipsisCharacter;
                nameSf.FormatFlags = StringFormatFlags.NoWrap;
                pctSf.Alignment = StringAlignment.Far;
                pctSf.FormatFlags = StringFormatFlags.NoWrap;
                for (int i = 0; i < count; i++)
                {
                    FloatDevice dev = devices[i];
                    int y = pad + i * rowH;
                    if (object.ReferenceEquals(dev, current))
                    {
                        using (SolidBrush hi = new SolidBrush(Color.FromArgb(32, 38, 50)))
                        {
                            g.FillRectangle(hi, (int)(4 * dpiScale), y, this.Width - (int)(8 * dpiScale), rowH - (int)(4 * dpiScale));
                        }
                    }
                    using (SolidBrush stripe = new SolidBrush(dev.Accent))
                    {
                        g.FillRectangle(stripe, (int)(8 * dpiScale), y + (int)(6 * dpiScale), (int)(3 * dpiScale), rowH - (int)(16 * dpiScale));
                    }
                    float textX = 16f * dpiScale;
                    float pctW = 52f * dpiScale;
                    RectangleF nameRect = new RectangleF(textX, y + 2f * dpiScale, this.Width - textX - pctW - 8f * dpiScale, 16f * dpiScale);
                    RectangleF pctRect = new RectangleF(this.Width - pctW - 8f * dpiScale, y + 1f * dpiScale, pctW, 18f * dpiScale);
                    string pct = (dev.Connected && dev.Percent >= 0) ? (dev.Percent.ToString() + "%") : "--%";
                    using (SolidBrush nameBrush = new SolidBrush(Color.FromArgb(230, 236, 245)))
                    using (SolidBrush pctBrush = new SolidBrush(dev.Accent))
                    using (SolidBrush stBrush = new SolidBrush(Color.FromArgb(150, 160, 178)))
                    {
                        g.DrawString(dev.Name ?? "", nameFont, nameBrush, nameRect, nameSf);
                        g.DrawString(pct, pctFont, pctBrush, pctRect, pctSf);
                        g.DrawString(dev.Status ?? "", stFont, stBrush, textX, y + 18f * dpiScale);
                    }
                }
            }
        }

        private int ConnectedCount()
        {
            if (devices == null) return 0;
            int n = 0;
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i] != null && devices[i].Connected) n++;
            }
            return n;
        }

        private FloatDevice ConnectedAt(int index)
        {
            if (devices == null || index < 0) return null;
            int seen = 0;
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i] == null || !devices[i].Connected) continue;
                if (seen == index) return devices[i];
                seen++;
            }
            return null;
        }

        private FloatDevice CurrentDevice()
        {
            int online = ConnectedCount();
            if (online <= 0) return DisconnectedDevice();
            if (cycleIndex < 0 || cycleIndex >= online) cycleIndex = 0;
            FloatDevice dev = ConnectedAt(cycleIndex);
            if (dev == null) return DisconnectedDevice();
            return dev;
        }

        private static FloatDevice Snapshot(MouseBatteryInfo info)
        {
            if (info == null) return DisconnectedDevice();
            FloatDevice d = new FloatDevice();
            d.Name = CompactName(info.DisplayName);
            if (!info.IsConnected)
            {
                d.Connected = false;
                d.Percent = -1;
                d.Status = "未连接";
                d.Accent = Color.FromArgb(140, 145, 155);
                return d;
            }
            d.Connected = true;
            d.Percent = info.BatteryPercent;
            if (info.IsSleeping)
            {
                d.Status = "待机";
                d.Accent = Color.FromArgb(255, 183, 77);
            }
            else if (info.IsCharging)
            {
                d.Status = "充电中";
                d.Accent = info.BrandColor;
            }
            else if (d.Percent >= 0 && d.Percent <= 10)
            {
                d.Status = "电量低";
                d.Accent = Color.FromArgb(255, 45, 85);
            }
            else if (d.Percent > 10 && d.Percent <= 20)
            {
                d.Status = "在线";
                d.Accent = Color.FromArgb(255, 214, 0);
            }
            else
            {
                d.Status = "在线";
                d.Accent = info.BrandColor;
            }
            return d;
        }

        private static FloatDevice DisconnectedDevice()
        {
            FloatDevice d = new FloatDevice();
            d.Name = "未连接";
            d.Status = "未连接";
            d.Percent = -1;
            d.Connected = false;
            d.Accent = Color.FromArgb(140, 145, 155);
            return d;
        }

        private int CardWidth()
        {
            return (int)(176 * dpiScale);
        }

        private int CardHeight()
        {
            return (int)(52 * dpiScale);
        }

        private int BarThick()
        {
            return Math.Max(4, (int)(6 * dpiScale));
        }

        private int BarLength()
        {
            return (int)(108 * dpiScale);
        }

        private int ListWidth()
        {
            return (int)(220 * dpiScale);
        }

        private int RowHeight()
        {
            return (int)(40 * dpiScale);
        }

        private int ListHeight()
        {
            int count = devices == null ? 1 : devices.Length;
            if (count < 1) count = 1;
            return (int)(8 * dpiScale) * 2 + count * RowHeight();
        }

        private int DetectEdge(Rectangle wa, int x, int y, int w, int h, Point cursor)
        {
            int threshold = (int)(40 * dpiScale);
            int chosen = EdgeNone;
            int best = threshold + 1;
            ConsiderEdge(x - wa.Left, threshold, EdgeLeft, ref chosen, ref best);
            ConsiderEdge(wa.Right - (x + w), threshold, EdgeRight, ref chosen, ref best);
            ConsiderEdge(y - wa.Top, threshold, EdgeTop, ref chosen, ref best);
            ConsiderEdge(wa.Bottom - (y + h), threshold, EdgeBottom, ref chosen, ref best);
            ConsiderEdge(cursor.X - wa.Left, threshold, EdgeLeft, ref chosen, ref best);
            ConsiderEdge(wa.Right - cursor.X, threshold, EdgeRight, ref chosen, ref best);
            ConsiderEdge(cursor.Y - wa.Top, threshold, EdgeTop, ref chosen, ref best);
            ConsiderEdge(wa.Bottom - cursor.Y, threshold, EdgeBottom, ref chosen, ref best);
            return chosen;
        }

        private static void ConsiderEdge(int gap, int threshold, int edge, ref int chosen, ref int best)
        {
            if (gap > threshold) return;
            if (gap < best)
            {
                best = gap;
                chosen = edge;
            }
        }

        private Point DockOrigin(Rectangle wa, int edge, int along, int thick, int length)
        {
            if (edge == EdgeLeft || edge == EdgeRight)
            {
                int y = along;
                if (y < wa.Top) y = wa.Top;
                if (y + length > wa.Bottom) y = wa.Bottom - length;
                int x = edge == EdgeLeft ? wa.Left : wa.Right - thick;
                return new Point(x, y);
            }
            int xAlong = along;
            if (xAlong < wa.Left) xAlong = wa.Left;
            if (xAlong + length > wa.Right) xAlong = wa.Right - length;
            int yEdge = edge == EdgeTop ? wa.Top : wa.Bottom - thick;
            return new Point(xAlong, yEdge);
        }

        private static int FitAlong(int barStart, int barLen, int listLen, int areaStart, int areaEnd)
        {
            int pos = barStart;
            if (pos + listLen > areaEnd) pos = areaEnd - listLen;
            if (pos < areaStart) pos = areaStart;
            if (barLen > 0 && pos > barStart) pos = barStart;
            if (pos < areaStart) pos = areaStart;
            return pos;
        }

        private Rectangle WorkAreaOf(Point pt)
        {
            Screen screen = Screen.FromPoint(pt);
            if (screen == null) screen = Screen.PrimaryScreen;
            return screen.WorkingArea;
        }

        private void UpdateRegion(int w, int h, int radius)
        {
            if (radius < 1) radius = 1;
            if (radius * 2 > w) radius = w / 2;
            if (radius * 2 > h) radius = h / 2;
            using (GraphicsPath path = RoundedCard.GetRoundRectF(new RectangleF(0, 0, w, h), radius))
            {
                Region oldRgn = this.Region;
                this.Region = new Region(path);
                if (oldRgn != null) oldRgn.Dispose();
            }
        }

        private static string CompactName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "外设";
            if (name.IndexOf("旗舰无线", StringComparison.OrdinalIgnoreCase) >= 0) return "GPW";
            if (name.IndexOf("PRO WIRELESS", StringComparison.OrdinalIgnoreCase) >= 0) return "GPW";
            if (name.IndexOf("PRO Wireless", StringComparison.OrdinalIgnoreCase) >= 0) return "GPW";
            if (name.IndexOf("SUPERLIGHT 2", StringComparison.OrdinalIgnoreCase) >= 0) return "GPX 2";
            if (name.IndexOf("SUPERLIGHT", StringComparison.OrdinalIgnoreCase) >= 0) return "GPX";
            return name;
        }
    }
}
