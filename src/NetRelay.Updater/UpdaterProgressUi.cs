using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetRelay.Updater;

internal sealed class UpdaterProgressUi : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _uiThread;
    private ProgressForm? _form;
    private bool _closed;

    private UpdaterProgressUi()
    {
        _uiThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NetRelay Updater UI"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public static UpdaterProgressUi Start() => new();

    public void Report(string title, string detail, int percent)
    {
        if (_closed)
        {
            return;
        }

        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        try
        {
            form.BeginInvoke(() => form.UpdateState(title, detail, percent, failed: false));
        }
        catch
        {
            // UI feedback must not interrupt the updater.
        }
    }

    public void Complete(string title, string detail)
    {
        Report(title, detail, 100);
        Thread.Sleep(1800);
        Close();
    }

    public void Fail(string title, string detail)
    {
        if (_closed)
        {
            return;
        }

        var form = _form;
        if (form is not null && !form.IsDisposed)
        {
            try
            {
                form.BeginInvoke(() => form.UpdateState(title, detail, 100, failed: true));
            }
            catch
            {
            }
        }

        Thread.Sleep(4200);
        Close();
    }

    public void Dispose()
    {
        Close();
        _ready.Dispose();
    }

    private void Run()
    {
        ApplicationConfiguration.Initialize();
        _form = new ProgressForm();
        _ready.Set();
        Application.Run(_form);
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        try
        {
            form.BeginInvoke(() => form.Close());
        }
        catch
        {
        }
    }

    private sealed class ProgressForm : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _detailLabel;
        private readonly Label _percentLabel;
        private readonly RoundedSurface _track;
        private readonly RoundedSurface _fill;
        private bool _failed;

        public ProgressForm()
        {
            Text = "NetRelay 更新";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            Size = new Size(470, 232);
            BackColor = Color.FromArgb(238, 245, 252);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            Padding = new Padding(24);

            var iconBox = new RoundedSurface
            {
                Size = new Size(42, 42),
                Location = new Point(26, 26),
                Radius = 14,
                FillColor = Color.FromArgb(26, 83, 110, 242)
            };
            iconBox.Paint += (_, e) =>
            {
                using var iconFont = new Font("Segoe Fluent Icons", 18F, FontStyle.Regular, GraphicsUnit.Point);
                using var brush = new SolidBrush(Color.FromArgb(83, 110, 242));
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                e.Graphics.DrawString("\uE895", iconFont, brush, new PointF(11, 10));
            };
            Controls.Add(iconBox);

            _titleLabel = new Label
            {
                AutoSize = false,
                Location = new Point(82, 28),
                Size = new Size(330, 24),
                Text = "正在更新 NetRelay",
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(24, 34, 54)
            };
            Controls.Add(_titleLabel);

            _detailLabel = new Label
            {
                AutoSize = false,
                Location = new Point(82, 55),
                Size = new Size(348, 38),
                Text = "正在准备更新...",
                ForeColor = Color.FromArgb(101, 114, 137)
            };
            Controls.Add(_detailLabel);

            _track = new RoundedSurface
            {
                Location = new Point(28, 125),
                Size = new Size(414, 12),
                Radius = 6,
                FillColor = Color.FromArgb(216, 225, 238)
            };
            Controls.Add(_track);

            _fill = new RoundedSurface
            {
                Location = new Point(28, 125),
                Size = new Size(1, 12),
                Radius = 6,
                FillColor = Color.FromArgb(83, 110, 242)
            };
            Controls.Add(_fill);
            _fill.BringToFront();

            _percentLabel = new Label
            {
                AutoSize = false,
                Location = new Point(28, 151),
                Size = new Size(414, 20),
                Text = "0%",
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(83, 110, 242),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point)
            };
            Controls.Add(_percentLabel);

            UpdateRegion();
        }

        public void UpdateState(string title, string detail, int percent, bool failed)
        {
            _failed = failed;
            var clamped = Math.Clamp(percent, 0, 100);
            _titleLabel.Text = title;
            _detailLabel.Text = detail;
            _percentLabel.Text = failed ? "即将关闭" : $"{clamped}%";
            _percentLabel.ForeColor = failed ? Color.FromArgb(217, 74, 92) : Color.FromArgb(83, 110, 242);
            _fill.FillColor = failed ? Color.FromArgb(217, 74, 92) : Color.FromArgb(83, 110, 242);
            _fill.Width = Math.Max(1, (int)Math.Round(_track.Width * clamped / 100.0));
            _fill.Invalidate();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var borderPen = new Pen(Color.White, 1F);
            using var shadowPen = new Pen(Color.FromArgb(30, 38, 55, 83), 1F);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, 26);
            e.Graphics.DrawPath(borderPen, path);
            if (_failed)
            {
                using var failPen = new Pen(Color.FromArgb(60, 217, 74, 92), 1F);
                e.Graphics.DrawPath(failPen, path);
            }
            else
            {
                e.Graphics.DrawPath(shadowPen, path);
            }
        }

        private void UpdateRegion()
        {
            using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 26);
            Region = new Region(path);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class RoundedSurface : Control
        {
            private Color _fillColor = Color.White;

            public RoundedSurface()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.UserPaint,
                    true);
            }

            public int Radius { get; set; } = 8;

            public Color FillColor
            {
                get => _fillColor;
                set
                {
                    _fillColor = value;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(FillColor);
                using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
                e.Graphics.FillPath(brush, path);
            }
        }
    }
}
