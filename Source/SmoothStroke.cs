using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace QingJie
{
    /// <summary>
    /// Streaming midpoint curves. Each input fixes one short quadratic; only the
    /// straight tail to the cursor is provisional. No look-ahead, timer or history
    /// rescan is needed while drawing. Curves stay inside their control-point hull.
    /// All state is local, so committed strokes can also render on a worker thread.
    /// </summary>
    internal static class SmoothStroke
    {
        internal static PointF Midpoint(PointF a, PointF b)
        {
            return new PointF(a.X + (b.X - a.X) * 0.5f,
                a.Y + (b.Y - a.Y) * 0.5f);
        }

        internal static void Draw(Graphics graphics, Pen pen, IList<PointF> points)
        {
            if (points == null || points.Count == 0) return;
            bool hasLength = false;
            for (int i = 1; i < points.Count; i++)
                if (points[i] != points[0]) { hasLength = true; break; }
            if (!hasLength)
            {
                float radius = pen.Width * 0.5f;
                using (Brush brush = new SolidBrush(pen.Color))
                    graphics.FillEllipse(brush, points[0].X - radius,
                        points[0].Y - radius, pen.Width, pen.Width);
                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                PointF start = points[0];
                for (int i = 1; i < points.Count; i++)
                {
                    PointF control = points[i - 1];
                    PointF end = Midpoint(control, points[i]);
                    path.AddBezier(start, FirstControl(start, control),
                        SecondControl(control, end), end);
                    start = end;
                }
                path.AddLine(start, points[points.Count - 1]);
                graphics.DrawPath(pen, path);
            }
        }

        // Called after appending a point. This is constant work, independent of
        // the stroke length; it never converts the growing point list to an array.
        internal static void DrawLatestSegment(Graphics graphics, Pen pen,
            IList<PointF> points)
        {
            if (points == null || points.Count < 2) return;
            int last = points.Count - 1;
            PointF control = points[last - 1];
            PointF start = last == 1 ? control :
                Midpoint(points[last - 2], control);
            PointF end = Midpoint(control, points[last]);
            graphics.DrawBezier(pen, start, FirstControl(start, control),
                SecondControl(control, end), end);
        }

        // Draw over the stable cache, never into it: the next input replaces this
        // tail with a rounded segment, so its previous pixels must be restored.
        internal static void DrawTail(Graphics graphics, Pen pen,
            IList<PointF> points)
        {
            if (points == null || points.Count < 2) return;
            int last = points.Count - 1;
            graphics.DrawLine(pen, Midpoint(points[last - 1], points[last]), points[last]);
        }

        // Includes the old tail, the new stable curve and the new tail. Callers
        // inflate by the transformed pen radius and an antialiasing margin.
        internal static RectangleF GetRecentBounds(IList<PointF> points)
        {
            if (points == null || points.Count == 0) return RectangleF.Empty;
            int first = Math.Max(0, points.Count - 3);
            float left = points[first].X, right = left;
            float top = points[first].Y, bottom = top;
            for (int i = first + 1; i < points.Count; i++)
            {
                left = Math.Min(left, points[i].X);
                right = Math.Max(right, points[i].X);
                top = Math.Min(top, points[i].Y);
                bottom = Math.Max(bottom, points[i].Y);
            }
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        private static PointF FirstControl(PointF start, PointF control)
        {
            return new PointF(start.X + (control.X - start.X) * (2f / 3f),
                start.Y + (control.Y - start.Y) * (2f / 3f));
        }

        private static PointF SecondControl(PointF control, PointF end)
        {
            return new PointF(end.X + (control.X - end.X) * (2f / 3f),
                end.Y + (control.Y - end.Y) * (2f / 3f));
        }
    }
}
