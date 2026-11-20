using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace QingJie
{
    internal sealed class BufferedWorkspacePanel : Panel
    {
        public BufferedWorkspacePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    // Rounded buttons ask their ancestors to paint the surface behind their
    // corners. Cache the static artwork so a page switch does not regenerate
    // the same gradient and shadows for every child control.
    internal abstract class CachedSurfaceLayoutPanel : TableLayoutPanel
    {
        private Bitmap surface;
        private float surfaceDpiX, surfaceDpiY;

        protected CachedSurfaceLayoutPanel() { DoubleBuffered = true; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            if (surface == null || surface.Size != ClientSize ||
                Math.Abs(surfaceDpiX - e.Graphics.DpiX) > .1f ||
                Math.Abs(surfaceDpiY - e.Graphics.DpiY) > .1f)
            {
                Bitmap next = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                try
                {
                    next.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                    using (Graphics graphics = Graphics.FromImage(next))
                        RenderSurface(graphics);
                }
                catch { next.Dispose(); throw; }
                ReleaseSurface();
                surface = next;
                surfaceDpiX = e.Graphics.DpiX;
                surfaceDpiY = e.Graphics.DpiY;
            }
            Rectangle clip = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
            if (clip.Width > 0 && clip.Height > 0)
                e.Graphics.DrawImage(surface, clip, clip, GraphicsUnit.Pixel);
        }

        protected abstract void RenderSurface(Graphics graphics);

        protected override void OnSizeChanged(EventArgs e)
        {
            ReleaseSurface();
            base.OnSizeChanged(e);
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            ReleaseSurface();
            base.OnBackColorChanged(e);
        }

        private void ReleaseSurface()
        {
            if (surface != null) { surface.Dispose(); surface = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseSurface();
            base.Dispose(disposing);
        }
    }

    internal sealed class BackdropLayoutPanel : CachedSurfaceLayoutPanel
    {
        public BackdropLayoutPanel()
        {
            DoubleBuffered = true;
            BackColor = UiTheme.Page;
        }

        protected override void RenderSurface(Graphics graphics)
        {
            if (Width <= 0 || Height <= 0) return;
            using (var background = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(246, 249, 255), Color.FromArgb(235, 241, 252), 55F))
                graphics.FillRectangle(background, ClientRectangle);
            // Static blue/lilac lighting ties the source rail and canvas together.
            using (var ellipse = new GraphicsPath())
            {
                ellipse.AddEllipse(Width * .43F, -Height * .48F, Width * .9F, Height * 1.05F);
                using (var glow = new PathGradientBrush(ellipse))
                {
                    glow.CenterColor = Color.FromArgb(40, 162, 179, 246);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, 161, 187, 245) };
                    graphics.FillPath(glow, ellipse);
                }
            }
            using (var ellipse = new GraphicsPath())
            {
                ellipse.AddEllipse(-Width * .2F, Height * .4F, Width * .75F, Height * .95F);
                using (var glow = new PathGradientBrush(ellipse))
                {
                    glow.CenterColor = Color.FromArgb(24, 135, 206, 226);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, 135, 206, 226) };
                    graphics.FillPath(glow, ellipse);
                }
            }
        }
    }

    internal sealed class CardLayoutPanel : CachedSurfaceLayoutPanel
    {
        public CardLayoutPanel() { DoubleBuffered = true; }

        protected override void RenderSurface(Graphics g)
        {
            g.Clear(UiTheme.Page);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = g.DpiX / 96F;
            float d = 22 * unit;
            float x = 2 * unit, y = unit, w = Width - 4 * unit, h = Height - 6 * unit;
            if (w < d || h < d) return;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
                for (int i = 4; i >= 1; i--)
                {
                    using (var shadow = (GraphicsPath)path.Clone())
                    using (var transform = new Matrix())
                    {
                        transform.Translate(0, i * unit);
                        shadow.Transform(transform);
                        using (var shade = new SolidBrush(Color.FromArgb(5, 70, 102, 163)))
                            g.FillPath(shade, shadow);
                    }
                }
                using (var fill = new LinearGradientBrush(new RectangleF(x, y, w, h),
                    Color.White, BackColor, 90F)) g.FillPath(fill, path);
                GraphicsState lighting = g.Save();
                g.SetClip(path);
                using (var bloom = new GraphicsPath())
                {
                    float bloomWidth = Math.Max(150 * unit, w * .82f);
                    bloom.AddEllipse(x + w * .45f, y - 80 * unit, bloomWidth, 160 * unit);
                    using (var glow = new PathGradientBrush(bloom))
                    {
                        glow.CenterColor = Color.FromArgb(32, 176, 201, 255);
                        glow.SurroundColors = new Color[] { Color.FromArgb(0, 176, 201, 255) };
                        g.FillPath(glow, bloom);
                    }
                }
                using (var highlight = new Pen(Color.FromArgb(210, Color.White), unit))
                    g.DrawLine(highlight, x + 15 * unit, y + unit, x + w - 15 * unit, y + unit);
                g.Restore(lighting);
                using (var border = new Pen(UiTheme.Border, unit)) g.DrawPath(border, path);
            }
        }
    }
}
