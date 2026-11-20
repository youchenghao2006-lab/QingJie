using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunMosaicFeatureTests()
    {
        Run("all five mosaic styles produce independent, full-size opaque images", delegate {
            using (Bitmap source = Pattern(79, 53))
            {
                HashSet<long> styles = new HashSet<long>();
                int original = source.GetPixel(0, 0).ToArgb();
                foreach (MosaicStyle style in Enum.GetValues(typeof(MosaicStyle)))
                {
                    using (Bitmap result = MosaicEffects.Apply(source, 12, style, Color.Purple))
                    {
                        Equal(source.Size, result.Size);
                        Equal(PixelFormat.Format32bppArgb, result.PixelFormat);
                        long signature = 17;
                        for (int y = 0; y < result.Height; y++)
                            for (int x = 0; x < result.Width; x++)
                            {
                                Color pixel = result.GetPixel(x, y);
                                Equal(255, (int)pixel.A);
                                signature = unchecked(signature * 31 + pixel.ToArgb());
                            }
                        styles.Add(signature);
                        result.SetPixel(0, 0, Color.Magenta);
                        Equal(original, source.GetPixel(0, 0).ToArgb());
                    }
                }
                Equal(5, styles.Count);
            }
        });
        Run("pixelate keeps complete and partial edge blocks uniform", delegate {
            using (Bitmap source = Pattern(29, 23))
            using (Bitmap result = MosaicEffects.Apply(source, 12, MosaicStyle.Pixelate, Color.Red))
            {
                int first = result.GetPixel(0, 0).ToArgb();
                for (int y = 0; y < 12; y++)
                    for (int x = 0; x < 12; x++)
                        Equal(first, result.GetPixel(x, y).ToArgb());
                int edge = result.GetPixel(24, 12).ToArgb();
                for (int y = 12; y < 23; y++)
                    for (int x = 24; x < 29; x++)
                        Equal(edge, result.GetPixel(x, y).ToArgb());
            }
        });
        Run("blur obscures high-frequency image detail", delegate {
            using (Bitmap source = new Bitmap(48, 48))
            {
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++)
                        source.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Black : Color.White);
                using (Bitmap result = MosaicEffects.Apply(source, 12, MosaicStyle.Blur, Color.Red))
                {
                    Color center = result.GetPixel(24, 24);
                    Assert(center.R > 60 && center.R < 195, "Blur did not remove checkerboard detail.");
                    Assert(Math.Abs(center.R - result.GetPixel(25, 24).R) < 30,
                        "Blur retained the original neighboring-pixel contrast.");
                }
            }
        });
        Run("solid mosaic uses exact selected RGB and opaque coverage", delegate {
            using (Bitmap source = Pattern(31, 19))
            using (Bitmap result = MosaicEffects.Apply(source, 18, MosaicStyle.Solid,
                Color.FromArgb(40, 123, 45, 67)))
            {
                for (int y = 0; y < result.Height; y++)
                    for (int x = 0; x < result.Width; x++)
                        Equal(Color.FromArgb(255, 123, 45, 67).ToArgb(), result.GetPixel(x, y).ToArgb());
            }
        });
        Run("mosaic flattens transparent inputs and handles one-pixel images", delegate {
            using (Bitmap source = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            {
                source.SetPixel(0, 0, Color.FromArgb(0, 20, 50, 90));
                foreach (MosaicStyle style in Enum.GetValues(typeof(MosaicStyle)))
                {
                    using (Bitmap result = MosaicEffects.Apply(source, 24, style, Color.Red))
                    {
                        Equal(new Size(1, 1), result.Size);
                        Equal(255, (int)result.GetPixel(0, 0).A);
                        if (style == MosaicStyle.Pixelate || style == MosaicStyle.Blur)
                            Equal(Color.White.ToArgb(), result.GetPixel(0, 0).ToArgb());
                    }
                }
            }
        });
        Run("mosaic rejects invalid source, size, and style", delegate {
            Throws<ArgumentNullException>(delegate { using (MosaicEffects.Apply(null, 12, MosaicStyle.Pixelate, Color.Red)) {} });
            using (Bitmap source = Pattern(10, 10))
            {
                Throws<ArgumentOutOfRangeException>(delegate { using (MosaicEffects.Apply(source, 0, MosaicStyle.Pixelate, Color.Red)) {} });
                Throws<ArgumentOutOfRangeException>(delegate { using (MosaicEffects.Apply(source, -3, MosaicStyle.Pixelate, Color.Red)) {} });
                Throws<ArgumentOutOfRangeException>(delegate { using (MosaicEffects.Apply(source, 12, (MosaicStyle)999, Color.Red)) {} });
            }
        });
        Run("mosaic strength is independent of annotation stroke width", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                Equal(24, canvas.MosaicSize);
                Equal(MosaicStyle.Pixelate, canvas.MosaicStyle);
                canvas.StrokeWidth = 2;
                canvas.MosaicSize = 48;
                Equal(2f, canvas.StrokeWidth);
                canvas.StrokeWidth = 12;
                Equal(48, canvas.MosaicSize);
                canvas.MosaicSize = 0;
                Equal(8, canvas.MosaicSize);
                canvas.MosaicSize = 1000;
                Equal(96, canvas.MosaicSize);
            }
        });
        Run("each mosaic style affects only selected pixels and undoes exactly", delegate {
            using (Bitmap source = Pattern(160, 100))
            {
                foreach (MosaicStyle style in Enum.GetValues(typeof(MosaicStyle)))
                {
                    using (AnnotationCanvas canvas = new AnnotationCanvas())
                    using (Bitmap expectedEffect = MosaicEffects.Apply(source, 24, style, Color.Purple))
                    {
                        canvas.Size = new Size(224, 164);
                        canvas.SetImage(source);
                        canvas.Tool = DrawingTool.Mosaic;
                        canvas.MosaicStyle = style;
                        canvas.MosaicSize = 24;
                        canvas.StrokeColor = Color.Purple;
                        Drag(canvas, new PointF(25, 20), new PointF(95, 70));
                        using (Bitmap exported = canvas.ExportImage())
                            for (int y = 0; y < source.Height; y++)
                                for (int x = 0; x < source.Width; x++)
                                {
                                    bool inside = x >= 25 && x < 95 && y >= 20 && y < 70;
                                    Equal((inside ? expectedEffect : source).GetPixel(x, y).ToArgb(),
                                        exported.GetPixel(x, y).ToArgb());
                                }
                        canvas.Undo();
                        using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
                    }
                }
            }
        });
        Run("changing mosaic options cancels an uncommitted preview", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(160, 100))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Mosaic;
                Mouse(canvas, "OnMouseDown", new PointF(20, 20));
                Mouse(canvas, "OnMouseMove", new PointF(90, 70));
                canvas.MosaicSize = 48;
                Mouse(canvas, "OnMouseUp", new PointF(90, 70));
                Assert(!canvas.CanUndo, "Size change committed an obsolete preview.");
                Mouse(canvas, "OnMouseDown", new PointF(20, 20));
                Mouse(canvas, "OnMouseMove", new PointF(90, 70));
                canvas.MosaicStyle = MosaicStyle.Stripes;
                Mouse(canvas, "OnMouseUp", new PointF(90, 70));
                Assert(!canvas.CanUndo, "Style change committed an obsolete preview.");
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("committed solid masks retain their own colors across option changes", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(160, 100))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Mosaic;
                canvas.MosaicStyle = MosaicStyle.Solid;
                canvas.StrokeColor = Color.Red;
                Drag(canvas, new PointF(10, 10), new PointF(50, 60));
                canvas.StrokeColor = Color.Blue;
                Drag(canvas, new PointF(80, 10), new PointF(120, 60));
                using (Bitmap exported = canvas.ExportImage())
                {
                    Equal(Color.Red.ToArgb(), exported.GetPixel(20, 20).ToArgb());
                    Equal(Color.Blue.ToArgb(), exported.GetPixel(90, 20).ToArgb());
                }
                canvas.Undo();
                using (Bitmap exported = canvas.ExportImage())
                {
                    Equal(Color.Red.ToArgb(), exported.GetPixel(20, 20).ToArgb());
                    Equal(source.GetPixel(90, 20).ToArgb(), exported.GetPixel(90, 20).ToArgb());
                }
            }
        });
    }
}
