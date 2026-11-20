using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace QingJie
{
    internal enum UiIcon
    {
        None, Capture, Screen, Open, History, Settings, Save, Copy, Clipboard,
        Edit, Plus, Delete, Undo, Hand, Fit, Arrow, Pen, Rectangle, Text, Mosaic, Redo, Board,
        ScrollCapture
    }

    internal static class UiTheme
    {
        private static readonly string familyName = FindFamily();
        internal static string FamilyName { get { return familyName; } }
        internal static readonly Color Accent = Color.FromArgb(58, 105, 239);
        internal static readonly Color Ink = Color.FromArgb(36, 53, 83);
        internal static readonly Color Muted = Color.FromArgb(116, 134, 160);
        internal static readonly Color Page = Color.FromArgb(240, 244, 252);
        internal static readonly Color Border = Color.FromArgb(220, 230, 246);

        // The caller owns each returned Font. Resolve installed families once,
        // never while painting or switching between workspaces.
        internal static System.Drawing.Font Font(float size,
            FontStyle style = FontStyle.Regular)
        {
            return new System.Drawing.Font(familyName, size, style, GraphicsUnit.Point);
        }

        private static string FindFamily()
        {
            string[] choices = { "MiSans", "DengXian", "等线", "Microsoft YaHei UI" };
            foreach (string choice in choices)
            {
                try
                {
                    using (FontFamily family = new FontFamily(choice))
                        if (family.IsStyleAvailable(FontStyle.Regular)) return family.Name;
                }
                catch (ArgumentException) { }
            }
            return System.Drawing.FontFamily.GenericSansSerif.Name;
        }

        internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 0) return path;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Small line icons have no font dependency and scale with device DPI.
        internal static void DrawIcon(Graphics graphics, UiIcon icon,
            RectangleF bounds, Color color)
        {
            if (icon == UiIcon.None || bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.TranslateTransform(bounds.Left, bounds.Top);
                graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(color, 1.4f))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    switch (icon)
                    {
                        case UiIcon.Capture:
                            graphics.DrawLines(pen, new PointF[] { new PointF(2, 6), new PointF(2, 2), new PointF(6, 2) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(10, 2), new PointF(14, 2), new PointF(14, 6) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(14, 10), new PointF(14, 14), new PointF(10, 14) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(6, 14), new PointF(2, 14), new PointF(2, 10) });
                            graphics.DrawEllipse(pen, 6, 6, 4, 4);
                            break;
                        case UiIcon.Screen:
                            graphics.DrawRectangle(pen, 1.5f, 2.5f, 13, 9);
                            graphics.DrawLine(pen, 8, 11.5f, 8, 14);
                            graphics.DrawLine(pen, 5, 14, 11, 14);
                            break;
                        case UiIcon.ScrollCapture:
                            graphics.DrawLines(pen, new PointF[] { new PointF(5, 2), new PointF(2, 2),
                                new PointF(2, 14), new PointF(5, 14) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(11, 2), new PointF(14, 2),
                                new PointF(14, 14), new PointF(11, 14) });
                            graphics.DrawLine(pen, 8, 3, 8, 12);
                            graphics.DrawLines(pen, new PointF[] { new PointF(5.5f, 9.5f),
                                new PointF(8, 12), new PointF(10.5f, 9.5f) });
                            break;
                        case UiIcon.Open:
                            graphics.DrawLines(pen, new PointF[] { new PointF(2, 6), new PointF(2, 3),
                                new PointF(6, 3), new PointF(8, 5), new PointF(13, 5), new PointF(13, 6) });
                            graphics.DrawPolygon(pen, new PointF[] { new PointF(1.5f, 7), new PointF(14.5f, 7),
                                new PointF(12.5f, 13), new PointF(3.5f, 13) });
                            break;
                        case UiIcon.History:
                            graphics.DrawArc(pen, 2, 2, 12, 12, 205, 305);
                            graphics.DrawLines(pen, new PointF[] { new PointF(1.5f, 2), new PointF(1.5f, 6), new PointF(5, 6) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(8, 4.5f), new PointF(8, 8), new PointF(10.5f, 9.5f) });
                            break;
                        case UiIcon.Settings:
                            graphics.DrawEllipse(pen, 4, 4, 8, 8);
                            graphics.DrawEllipse(pen, 6.5f, 6.5f, 3, 3);
                            for (int i = 0; i < 8; i++)
                            {
                                double angle = i * Math.PI / 4;
                                graphics.DrawLine(pen, 8 + 4.5f * (float)Math.Cos(angle), 8 + 4.5f * (float)Math.Sin(angle),
                                    8 + 6.5f * (float)Math.Cos(angle), 8 + 6.5f * (float)Math.Sin(angle));
                            }
                            break;
                        case UiIcon.Save:
                            graphics.DrawPolygon(pen, new PointF[] { new PointF(2, 2), new PointF(11, 2),
                                new PointF(14, 5), new PointF(14, 14), new PointF(2, 14) });
                            graphics.DrawRectangle(pen, 5, 2, 5, 4);
                            graphics.DrawRectangle(pen, 5, 9, 6, 5);
                            break;
                        case UiIcon.Copy:
                            graphics.DrawRectangle(pen, 5, 5, 9, 9);
                            graphics.DrawLines(pen, new PointF[] { new PointF(2, 11), new PointF(2, 2), new PointF(11, 2) });
                            break;
                        case UiIcon.Clipboard:
                            graphics.DrawRectangle(pen, 3, 3.5f, 10, 11);
                            graphics.DrawRectangle(pen, 5.5f, 1.5f, 5, 3.5f);
                            graphics.DrawLine(pen, 5.5f, 8, 10.5f, 8);
                            graphics.DrawLine(pen, 5.5f, 11, 9, 11);
                            break;
                        case UiIcon.Edit:
                        case UiIcon.Pen:
                            graphics.DrawPolygon(pen, new PointF[] { new PointF(3, 10), new PointF(10.5f, 2.5f),
                                new PointF(13.5f, 5.5f), new PointF(6, 13), new PointF(2, 14) });
                            graphics.DrawLine(pen, 9, 4, 12, 7);
                            break;
                        case UiIcon.Plus:
                            graphics.DrawLine(pen, 8, 3, 8, 13);
                            graphics.DrawLine(pen, 3, 8, 13, 8);
                            break;
                        case UiIcon.Delete:
                            graphics.DrawLine(pen, 2, 4, 14, 4);
                            graphics.DrawLine(pen, 6, 2, 10, 2);
                            graphics.DrawLines(pen, new PointF[] { new PointF(4, 6), new PointF(5, 14), new PointF(11, 14), new PointF(12, 6) });
                            break;
                        case UiIcon.Undo:
                            graphics.DrawLines(pen, new PointF[] { new PointF(5, 2), new PointF(2, 5), new PointF(5, 8) });
                            graphics.DrawBezier(pen, 2, 5, 15, 2, 16, 14, 7, 13);
                            break;
                        case UiIcon.Redo:
                            graphics.DrawLines(pen, new PointF[] { new PointF(11, 2), new PointF(14, 5), new PointF(11, 8) });
                            graphics.DrawBezier(pen, 14, 5, 1, 2, 0, 14, 9, 13);
                            break;
                        case UiIcon.Mosaic:
                            for (int row = 0; row < 2; row++)
                                for (int column = 0; column < 2; column++)
                                    graphics.DrawRectangle(pen, 2 + column * 7, 2 + row * 7, 5, 5);
                            break;
                        case UiIcon.Board:
                            graphics.DrawRectangle(pen, 1.5f, 2, 8, 7);
                            graphics.DrawRectangle(pen, 6.5f, 7, 8, 7);
                            break;
                        case UiIcon.Hand:
                            graphics.DrawLines(pen, new PointF[] { new PointF(3, 8), new PointF(3, 5),
                                new PointF(5, 5), new PointF(5, 2), new PointF(7, 2), new PointF(7, 6),
                                new PointF(9, 3), new PointF(11, 4), new PointF(11, 6), new PointF(13, 5),
                                new PointF(14, 7), new PointF(12, 13), new PointF(8, 14), new PointF(5, 12), new PointF(2, 9), new PointF(3, 8) });
                            break;
                        case UiIcon.Fit:
                            graphics.DrawLines(pen, new PointF[] { new PointF(2, 6), new PointF(2, 2), new PointF(6, 2) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(10, 2), new PointF(14, 2), new PointF(14, 6) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(14, 10), new PointF(14, 14), new PointF(10, 14) });
                            graphics.DrawLines(pen, new PointF[] { new PointF(6, 14), new PointF(2, 14), new PointF(2, 10) });
                            graphics.DrawRectangle(pen, 5, 5, 6, 6);
                            break;
                        case UiIcon.Arrow:
                            graphics.DrawLine(pen, 3, 13, 13, 3);
                            graphics.DrawLines(pen, new PointF[] { new PointF(6, 3), new PointF(13, 3), new PointF(13, 10) });
                            break;
                        case UiIcon.Rectangle:
                            graphics.DrawRectangle(pen, 2, 3, 12, 10);
                            break;
                        case UiIcon.Text:
                            graphics.DrawLine(pen, 3, 3, 13, 3);
                            graphics.DrawLine(pen, 8, 3, 8, 14);
                            graphics.DrawLine(pen, 5, 14, 11, 14);
                            break;
                    }
                }
            }
            finally { graphics.Restore(state); }
        }
    }
}
