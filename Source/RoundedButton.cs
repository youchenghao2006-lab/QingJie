using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QingJie
{
    // Keep standard Button keyboard and accessibility behavior with a quieter visual style.
    internal sealed class RoundedButton : Button
    {
        private bool hover, down;
        private float hoverAmount, pressAmount, ripple = 1F;
        private Point rippleOrigin;
        private readonly Timer animation = new Timer { Interval = 16 };
        private UiIcon icon;
        private bool navigation, selected;

        internal bool Navigation
        {
            get { return navigation; }
            set { if (navigation != value) { navigation = value; Invalidate(); } }
        }

        internal bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        internal UiIcon Icon
        {
            get { return icon; }
            set { if (icon != value) { icon = value; Invalidate(); } }
        }

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            animation.Tick += delegate
            {
                hoverAmount += ((hover && Enabled ? 1F : 0F) - hoverAmount) * .23F;
                pressAmount += ((down && Enabled ? 1F : 0F) - pressAmount) * .35F;
                ripple = System.Math.Min(1F, ripple + .055F);
                if (System.Math.Abs(hoverAmount - (hover && Enabled ? 1F : 0F)) < .005F &&
                    System.Math.Abs(pressAmount - (down && Enabled ? 1F : 0F)) < .005F && ripple >= 1F)
                    animation.Stop();
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; animation.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; animation.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                down = true; ripple = 0F; rippleOrigin = e.Location; animation.Start();
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; animation.Start(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { down = true; animation.Start(); }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            down = false;
            if (e.KeyCode == Keys.Space) { rippleOrigin = new Point(Width / 2, Height / 2); ripple = 0F; }
            animation.Start();
            base.OnKeyUp(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { hover = down = false; hoverAmount = pressAmount = 0F; animation.Stop(); }
            Invalidate(); base.OnEnabledChanged(e);
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
            {
                hover = down = false;
                hoverAmount = pressAmount = 0F;
                ripple = 1F;
                animation.Stop();
            }
            base.OnVisibleChanged(e);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Paint the background and surface together in OnPaint. This also
            // covers ButtonBase paths that skip the background paint callback.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // ButtonBase can skip OnPaintBackground; repaint the actual parent here
            // so rounded corners match gradients instead of leaving a solid box.
            PaintParentSurface(e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = g.DpiX / 96F;
            float lift = (hoverAmount * (navigation ? .6F : 1.8F) - pressAmount * 2.2F) * unit;
            RectangleF rect = new RectangleF(3 * unit, 4 * unit - lift, Math.Max(1, Width - 6 * unit),
                Math.Max(1, Height - 10 * unit));
            Color fill = selected ? Color.FromArgb(224, 234, 255) : BackColor;
            if (!Enabled) fill = Mix(fill, Color.White, .60f);
            else
            {
                fill = Mix(fill, Color.FromArgb(95, 140, 247), .09f * hoverAmount);
                fill = Mix(fill, Color.FromArgb(41, 62, 100), .12f * pressAmount);
            }
            bool accent = BackColor.B - BackColor.R > 60 && BackColor.R < 150;
            float depth = Enabled ? (accent ? .65F : .08F) + hoverAmount * .7F : .05F;
            if (navigation && !selected) depth *= .25F;
            for (int i = 3; i >= 1; i--)
            {
                RectangleF shadow = rect;
                shadow.Offset(0, (.5F + i * .5F) * unit);
                shadow.Inflate(i * .20F * unit, i * .15F * unit);
                using (GraphicsPath path = Rounded(shadow, 8 * unit))
                using (Brush brush = new SolidBrush(Color.FromArgb((int)(11 * depth), 56, 89, 160)))
                    g.FillPath(brush, path);
            }
            using (GraphicsPath path = Rounded(rect, 8 * unit))
            using (Brush brush = new LinearGradientBrush(rect,
                Mix(fill, Color.White, accent ? .10F : .05F),
                Mix(fill, Color.FromArgb(111, 139, 195), accent ? .025F : .015F), 90F))
            {
                g.FillPath(brush, path);
                Color borderColor = Focused && ShowFocusCues
                    ? Color.FromArgb(151, 177, 242)
                    : Color.FromArgb(Enabled ? 22 : 8, 99, 131, 189);
                using (Pen border = new Pen(borderColor, unit))
                    g.DrawPath(border, path);
                // A restrained top highlight gives depth without another layer
                // of animated controls or layout changes.
                GraphicsState highlightState = g.Save();
                g.SetClip(path);
                using (Pen highlight = new Pen(Color.FromArgb(accent ? 52 : 160, Color.White), unit))
                    g.DrawLine(highlight, rect.Left + 8 * unit, rect.Top + unit,
                        rect.Right - 8 * unit, rect.Top + unit);
                g.Restore(highlightState);
                if (ripple < 1F && Enabled)
                {
                    GraphicsState state = g.Save();
                    g.SetClip(path);
                    float radius = (float)System.Math.Sqrt(Width * Width + Height * Height) *
                        (1F - (float)System.Math.Pow(1F - ripple, 2));
                    using (Brush wave = new SolidBrush(Color.FromArgb((int)(48 * (1F - ripple)),
                        accent ? Color.White : Color.FromArgb(126, 162, 240))))
                        g.FillEllipse(wave, rippleOrigin.X - radius, rippleOrigin.Y - radius,
                            radius * 2, radius * 2);
                    g.Restore(state);
                }
            }
            Color textColor = Enabled ? ForeColor : Color.FromArgb(151, 161, 178);
            if (selected && Enabled) textColor = UiTheme.Accent;
            Rectangle textBounds = Rectangle.Round(rect);
            if (navigation)
            {
                float iconSize = 16 * unit;
                float left = rect.Left + 15 * unit;
                UiTheme.DrawIcon(g, icon, new RectangleF(left,
                    rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize), textColor);
                textBounds = Rectangle.Round(new RectangleF(left + iconSize + 10 * unit,
                    rect.Top, Math.Max(1, rect.Right - left - iconSize - 18 * unit), rect.Height));
                if (selected)
                    using (Pen mark = new Pen(UiTheme.Accent, 3 * unit))
                    {
                        mark.StartCap = mark.EndCap = LineCap.Round;
                        g.DrawLine(mark, rect.Left + unit, rect.Top + 9 * unit,
                            rect.Left + unit, rect.Bottom - 9 * unit);
                    }
                TextRenderer.DrawText(g, Text, Font, textBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                return;
            }
            if (icon != UiIcon.None)
            {
                Size measured = TextRenderer.MeasureText(g, Text, Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                float iconSize = Math.Min(15 * unit, rect.Height - 5 * unit);
                float gap = 6 * unit;
                float groupWidth = measured.Width + iconSize + gap;
                // Compact two-character tool buttons keep their text when an
                // icon would crowd it; wider action buttons show both.
                if (iconSize > 0 && groupWidth <= rect.Width - 8 * unit)
                {
                    float left = rect.Left + (rect.Width - groupWidth) / 2;
                    UiTheme.DrawIcon(g, icon, new RectangleF(left,
                        rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize), textColor);
                    textBounds = Rectangle.Round(new RectangleF(left + iconSize + gap,
                        rect.Top, measured.Width, rect.Height));
                }
            }
            TextRenderer.DrawText(g, Text, Font, textBounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }

        private static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }

        private void PaintParentSurface(PaintEventArgs e)
        {
            if (Parent == null)
            {
                e.Graphics.Clear(Color.FromArgb(240, 244, 252));
                return;
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                // InvokePaint does not paint child controls. Let transparent panels
                // resolve their own parent, preserving the background's exact offset.
                e.Graphics.TranslateTransform(-Left, -Top);
                Rectangle clip = e.ClipRectangle;
                clip.Offset(Left, Top);
                using (var parentPaint = new PaintEventArgs(e.Graphics, clip))
                {
                    InvokePaintBackground(Parent, parentPaint);
                    InvokePaint(Parent, parentPaint);
                }
            }
            finally { e.Graphics.Restore(state); }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
