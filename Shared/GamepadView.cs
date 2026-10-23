using System.Drawing.Drawing2D;
using System.Drawing.Text;

// Live drawing of an Xbox-style controller: pressed buttons light up, sticks and triggers move,
// and two bars at the bottom show the rumble motors.
sealed class GamepadView : Control
{
    // Everything is drawn on a fixed canvas and scaled to fit the control.
    const float CanvasWidth = 400, CanvasHeight = 265;
    const float StickTravel = 17;

    static readonly Color Accent = Color.FromArgb(0, 120, 215);
    static readonly Color ColorA = Color.FromArgb(59, 177, 67);
    static readonly Color ColorB = Color.FromArgb(224, 57, 62);
    static readonly Color ColorX = Color.FromArgb(47, 127, 224);
    static readonly Color ColorY = Color.FromArgb(232, 180, 30);

    GamepadState state;
    byte rumbleLarge, rumbleSmall;
    bool active;
    string caption = "";

    public GamepadView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    // When not active the controller is greyed out and the caption is shown over it.
    public void ShowState(GamepadState state, byte rumbleLarge, byte rumbleSmall, bool active, string caption = "")
    {
        if (state == this.state && rumbleLarge == this.rumbleLarge && rumbleSmall == this.rumbleSmall
            && active == this.active && caption == this.caption) return;
        this.state = state;
        this.rumbleLarge = rumbleLarge;
        this.rumbleSmall = rumbleSmall;
        this.active = active;
        this.caption = caption;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        float scale = Math.Min(Width / CanvasWidth, Height / CanvasHeight);
        if (scale <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.TranslateTransform((Width - CanvasWidth * scale) / 2, (Height - CanvasHeight * scale) / 2);
        g.ScaleTransform(scale, scale);

        using var painter = new Painter(g, active);
        var s = active ? state : default;

        using (var body = new GraphicsPath(FillMode.Winding))
        {
            body.AddEllipse(40, 50, 150, 120);
            body.AddEllipse(210, 50, 150, 120);
            body.AddRectangle(new RectangleF(110, 55, 180, 105));
            body.AddEllipse(50, 110, 105, 125);
            body.AddEllipse(245, 110, 105, 125);
            using var fill = new SolidBrush(painter.Body);
            g.FillPath(fill, body);
        }

        painter.Bar(new RectangleF(55, 6, 100, 14), s.LeftTrigger, "LT");
        painter.Bar(new RectangleF(245, 6, 100, 14), s.RightTrigger, "RT");
        painter.Pill(new RectangleF(55, 26, 100, 16), (s.Buttons & 0x0100) != 0, "LB");
        painter.Pill(new RectangleF(245, 26, 100, 16), (s.Buttons & 0x0200) != 0, "RB");

        painter.Stick(new PointF(110, 100), s.LeftX, s.LeftY, (s.Buttons & 0x0040) != 0);
        painter.Stick(new PointF(250, 168), s.RightX, s.RightY, (s.Buttons & 0x0080) != 0);
        painter.DPad(new PointF(150, 168), s.Buttons);

        painter.Face(new PointF(290, 124), ColorA, "A", (s.Buttons & 0x1000) != 0);
        painter.Face(new PointF(313, 100), ColorB, "B", (s.Buttons & 0x2000) != 0);
        painter.Face(new PointF(267, 100), ColorX, "X", (s.Buttons & 0x4000) != 0);
        painter.Face(new PointF(290, 76), ColorY, "Y", (s.Buttons & 0x8000) != 0);

        painter.Round(new PointF(172, 100), 8, (s.Buttons & 0x0020) != 0, "View");
        painter.Round(new PointF(200, 76), 12, (s.Buttons & 0x0400) != 0, "Guide");
        painter.Round(new PointF(228, 100), 8, (s.Buttons & 0x0010) != 0, "Menu");

        painter.Label("Strong motor", 40, 246);
        painter.Bar(new RectangleF(110, 245, 80, 10), active ? rumbleLarge : (byte)0, "");
        painter.Label("Weak motor", 215, 246);
        painter.Bar(new RectangleF(280, 245, 80, 10), active ? rumbleSmall : (byte)0, "");

        if (caption.Length > 0) painter.Caption(caption, new PointF(CanvasWidth / 2, 140));
    }

    sealed class Painter : IDisposable
    {
        readonly Graphics g;
        readonly bool active;
        readonly Pen outline;
        readonly SolidBrush idle, text;
        readonly Font font = new("Segoe UI", 9f, GraphicsUnit.Pixel);
        readonly Font letterFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        public readonly Color Body;

        public Painter(Graphics g, bool active)
        {
            this.g = g;
            this.active = active;
            Body = active ? Color.FromArgb(228, 228, 228) : Color.FromArgb(238, 238, 238);
            outline = new Pen(active ? Color.FromArgb(110, 110, 110) : Color.FromArgb(185, 185, 185), 1.5f);
            idle = new SolidBrush(Color.White);
            text = new SolidBrush(active ? Color.FromArgb(70, 70, 70) : Color.FromArgb(170, 170, 170));
        }

        Color Lit(Color color) => active ? color : Color.FromArgb(200, 200, 200);

        public void Bar(RectangleF rect, byte value, string label)
        {
            g.FillRectangle(idle, rect);
            if (value > 0)
            {
                using var fill = new SolidBrush(Lit(Accent));
                g.FillRectangle(fill, rect.X, rect.Y, rect.Width * value / 255f, rect.Height);
            }
            g.DrawRectangle(outline, rect.X, rect.Y, rect.Width, rect.Height);
            if (label.Length > 0) g.DrawString(label, font, text, rect, centered);
        }

        public void Pill(RectangleF rect, bool pressed, string label)
        {
            using var path = RoundedRect(rect, rect.Height / 2);
            using var fill = new SolidBrush(pressed ? Lit(Accent) : Color.White);
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
            using var labelBrush = new SolidBrush(pressed ? Color.White : text.Color);
            g.DrawString(label, font, labelBrush, rect, centered);
        }

        public void Stick(PointF center, short x, short y, bool pressed)
        {
            g.FillEllipse(idle, center.X - 28, center.Y - 28, 56, 56);
            g.DrawEllipse(outline, center.X - 28, center.Y - 28, 56, 56);
            var knob = new PointF(center.X + x / 32768f * StickTravel, center.Y - y / 32768f * StickTravel);
            using var fill = new SolidBrush(pressed ? Lit(Accent) : Color.FromArgb(205, 205, 205));
            g.FillEllipse(fill, knob.X - 13, knob.Y - 13, 26, 26);
            g.DrawEllipse(outline, knob.X - 13, knob.Y - 13, 26, 26);
        }

        public void DPad(PointF c, ushort buttons)
        {
            const float size = 16;
            Arm(c.X - size / 2, c.Y - size * 1.5f, (buttons & 0x0001) != 0);
            Arm(c.X - size / 2, c.Y + size / 2, (buttons & 0x0002) != 0);
            Arm(c.X - size * 1.5f, c.Y - size / 2, (buttons & 0x0004) != 0);
            Arm(c.X + size / 2, c.Y - size / 2, (buttons & 0x0008) != 0);
            g.FillRectangle(idle, c.X - size / 2, c.Y - size / 2, size, size);

            void Arm(float x, float y, bool pressed)
            {
                using var fill = new SolidBrush(pressed ? Lit(Accent) : Color.White);
                g.FillRectangle(fill, x, y, size, size);
                g.DrawRectangle(outline, x, y, size, size);
            }
        }

        public void Face(PointF c, Color color, string letter, bool pressed)
        {
            const float r = 11;
            var shown = Lit(color);
            using var fill = new SolidBrush(pressed ? shown : Color.White);
            using var ring = new Pen(shown, 1.5f);
            using var letterBrush = new SolidBrush(pressed ? Color.White : shown);
            g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawEllipse(ring, c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawString(letter, letterFont, letterBrush, new RectangleF(c.X - r, c.Y - r, r * 2, r * 2), centered);
        }

        public void Round(PointF c, float r, bool pressed, string label)
        {
            using var fill = new SolidBrush(pressed ? Lit(Accent) : Color.White);
            g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawEllipse(outline, c.X - r, c.Y - r, r * 2, r * 2);
            g.DrawString(label, font, text, new RectangleF(c.X - 20, c.Y + r, 40, 12), centered);
        }

        public void Label(string label, float x, float y) => g.DrawString(label, font, text, x, y);

        public void Caption(string caption, PointF center)
        {
            using var captionFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(caption, captionFont);
            var box = new RectangleF(center.X - size.Width / 2 - 10, center.Y - size.Height / 2 - 5, size.Width + 20, size.Height + 10);
            using var background = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            using var captionBrush = new SolidBrush(Color.FromArgb(90, 90, 90));
            using var path = RoundedRect(box, 6);
            g.FillPath(background, path);
            g.DrawString(caption, captionFont, captionBrush, box, centered);
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            outline.Dispose();
            idle.Dispose();
            text.Dispose();
            font.Dispose();
            letterFont.Dispose();
            centered.Dispose();
        }
    }
}
