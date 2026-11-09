using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Native
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}

sealed class Shape
{
    public string Kind;
    public Color Color;
    public Point Start;
    public Point End;
    public string Text;
    public int Weight = 2;
    public float FontSize = 18f;
    public bool Circle;
    public List<Point> Points = new List<Point>();
}

sealed class ToolButton
{
    public Rectangle Bounds;
    public string Label;
    public string Action;
    public string Icon;
    public string Variant;
    public bool DividerBefore;
    public int Width;
    public Color IconColor = Color.Empty;
    public Color Swatch = Color.Empty;
    public bool Active;
}

sealed class ShotForm : Form
{
    static Color BarFill = Color.FromArgb(242, 250, 250, 252);
    static Color BarBorder = Color.FromArgb(28, 0, 0, 0);
    static Color BarShadow = Color.FromArgb(46, 0, 0, 0);
    static Color TextColor = Color.FromArgb(32, 33, 36);
    static Color ActiveFill = Color.FromArgb(255, 219, 234, 255);
    static Color ActiveInk = Color.FromArgb(255, 10, 132, 255);
    static Color ActiveRing = Color.FromArgb(255, 10, 132, 255);
    static Color Divider = Color.FromArgb(22, 0, 0, 0);

    static readonly Color CloseRed = Color.FromArgb(226, 58, 52);
    static readonly Color CheckGreen = Color.FromArgb(46, 184, 92);

    public static void UseDarkTheme()
    {
        BarFill = Color.FromArgb(243, 32, 32, 38);
        BarBorder = Color.FromArgb(46, 255, 255, 255);
        BarShadow = Color.FromArgb(70, 0, 0, 0);
        TextColor = Color.FromArgb(238, 238, 243);
        Divider = Color.FromArgb(32, 255, 255, 255);
    }

    static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48), Color.FromArgb(10, 132, 255),
        Color.FromArgb(52, 199, 89), Color.FromArgb(255, 204, 0),
        Color.Black, Color.White,
    };

    readonly Bitmap screen;
    readonly List<Shape> shapes = new List<Shape>();
    readonly List<ToolButton> buttons = new List<ToolButton>();
    readonly List<ToolButton> subButtons = new List<ToolButton>();
    readonly List<Rectangle> dividers = new List<Rectangle>();

    Bitmap canvas;
    Rectangle selection = Rectangle.Empty;
    Rectangle toolbar = Rectangle.Empty;
    Rectangle subBar = Rectangle.Empty;
    Rectangle cropFrame;

    bool dragging;
    bool drawing;
    bool editing;
    bool moving;
    bool resizing;
    Shape activeShape;
    Point moveOrigin;
    Point moveStart;
    Point resizeOrigin;
    float resizeStartFont;
    float resizeStartDistance;
    Point anchor;
    Shape current;
    Point pointer = new Point(-1, -1);
    bool layoutDirty = true;
    Bitmap previewBlur;
    Rectangle previewArea = Rectangle.Empty;
    Shape suppressed;              // dragged text/number, drawn live instead of in the layer
    Graphics measureGraphics;      // cached GDI handle for text metrics

    string tool = "";
    string shapeTool = "";
    string flyout = "";
    int penWeight = 2;
    int textSize = 2;
    bool eraserOn;
    bool numberMode;
    int nextNumber = 1;

    Color color = Palette[0];
    TextBox editor;
    Shape editingShape;
    bool disposed;

    public bool Completed;
    public bool SavedOnly;
    public Rectangle Chosen;

    public ShotForm(Bitmap bitmap, Rectangle bounds)
    {
        screen = bitmap;
        cropFrame = new Rectangle(0, 0, bounds.Width, bounds.Height);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
    }

    public bool HasSelection { get { return selection.Width > 4 && selection.Height > 4; } }

    public void Freeze(Rectangle rect)
    {
        selection = rect;
        editing = true;
        flyout = "shapes";
        shapeTool = "rect";
    }

    public void PreviewFlyout(string name)
    {
        flyout = name;
        if (name == "pen") tool = "pen";
        else if (name == "text") tool = "text";
        else tool = "annotate";
    }

    public void AddDemoShapes()
    {
        shapes.Add(new Shape { Kind = "rect", Color = Palette[0], Weight = 2, Start = new Point(120, 115), End = new Point(215, 175) });
        shapes.Add(new Shape { Kind = "pen", Color = Palette[3], Weight = 1, Points = new List<Point> { new Point(240, 120), new Point(270, 128), new Point(300, 118) } });
        shapes.Add(new Shape { Kind = "pen", Color = Palette[3], Weight = 3, Points = new List<Point> { new Point(240, 150), new Point(270, 158), new Point(300, 148) } });
        shapes.Add(new Shape { Kind = "eraser", Weight = 2, Points = new List<Point> { new Point(276, 112), new Point(276, 176) } });
        shapes.Add(new Shape { Kind = "text", Color = Palette[0], Text = "小字", FontSize = 13f, Start = new Point(120, 245) });
        shapes.Add(new Shape { Kind = "text", Color = Palette[5], Text = "黑色字", FontSize = 19f, Start = new Point(120, 275) });
        shapes.Add(new Shape { Kind = "number", Color = Palette[0], Text = "1", FontSize = 19f, Circle = true, Start = new Point(330, 130) });
        shapes.Add(new Shape { Kind = "number", Color = Palette[0], Text = "2", FontSize = 19f, Circle = true, Start = new Point(330, 200) });
        shapes.Add(new Shape { Kind = "mosaic", Start = new Point(330, 245), End = new Point(420, 290) });

        // Regression check for the shared measurement Graphics: repeated calls
        // must not throw (a disposed handle threw ArgumentException here).
        for (int i = 0; i < 4; i++) LabelBounds(shapes[3]);

        RebuildCanvas();
    }

    public Bitmap RenderResult()
    {
        Rectangle area = HasSelection ? selection : cropFrame;
        Bitmap result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        {
            Bitmap source = canvas ?? screen;
            g.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
            if (suppressed != null && (suppressed.Kind == "text" || suppressed.Kind == "number"))
            {
                GraphicsState live = g.Save();
                g.TranslateTransform(-area.X, -area.Y);
                DrawLabel(g, suppressed);
                g.Restore(live);
            }
        }
        return result;
    }

    void EnsureCanvas()
    {
        if (canvas != null) return;
        canvas = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas)) g.DrawImage(screen, 0, 0);
    }

    /// <summary>
    /// Opaque composite: the screenshot is the base and every mark is painted
    /// straight onto it. Text no longer blends against transparent black (the
    /// dark halo), and the eraser restores the screenshot instead of punching a
    /// transparent hole.
    /// </summary>
    void Replay(IEnumerable<Shape> items)
    {
        foreach (Shape shape in items)
        {
            if (shape == suppressed || shape.Kind != "mosaic") continue;
            Stamp(shape);
        }
        foreach (Shape shape in items)
        {
            if (shape == suppressed || shape.Kind == "mosaic") continue;
            Stamp(shape);
        }
    }

    void RebuildCanvas()
    {
        EnsureCanvas();
        using (Graphics g = Graphics.FromImage(canvas)) g.DrawImage(screen, 0, 0);
        Replay(shapes);
    }

    void RestoreFromScreen(Graphics g, Point from, Point to, float diameter)
    {
        using (GraphicsPath stroke = new GraphicsPath())
        {
            if (from == to)
            {
                stroke.AddEllipse(from.X - diameter / 2f, from.Y - diameter / 2f, diameter, diameter);
            }
            else
            {
                stroke.AddLine(from, to);
                using (Pen nib = new Pen(Color.Black, diameter))
                {
                    // round caps: the brush that drew the stroke has them too, so a
                    // flat cap here leaves a crescent at each end of the erase.
                    nib.StartCap = LineCap.Round;
                    nib.EndCap = LineCap.Round;
                    nib.LineJoin = LineJoin.Round;
                    stroke.Widen(nib);
                }
            }
            g.FillPath(ScreenBrush, stroke);
        }
    }

    static float StrokeFor(int weight)
    {
        return weight <= 1 ? 2f : (weight == 2 ? 4f : 7f);
    }

    static float EraserDiameter(int weight)
    {
        return weight <= 1 ? 10f : (weight == 2 ? 18f : 28f);
    }

    static float NumberFor(int size)
    {
        return size <= 1 ? 9f : (size == 2 ? 13f : 18f);
    }

    static float FontFor(int size)
    {
        return size <= 1 ? 13f : (size == 2 ? 19f : 26f);
    }

    /// <summary>
    /// Remove just this shape from the canvas: restore its box from the
    /// screenshot, then re-stamp the shapes that overlapped that box. Rebuilding
    /// everything made the first move of a drag stutter.
    /// </summary>
    void LiftShape(Shape shape)
    {
        Rectangle area = ShapeBounds(shape);
        area.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
        if (area.Width <= 0 || area.Height <= 0) return;

        using (Graphics g = Graphics.FromImage(canvas))
        {
            GraphicsState state = g.Save();
            g.SetClip(area);
            g.DrawImage(screen, area, area, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        List<Shape> overlapping = new List<Shape>();
        bool touchesBlur = false;
        foreach (Shape other in shapes)
        {
            if (other == shape) continue;
            if (!ShapeBounds(other).IntersectsWith(area)) continue;
            overlapping.Add(other);
            if (other.Kind == "mosaic") touchesBlur = true;
        }

        if (touchesBlur)
        {
            // a blur repaints its whole area, so partial restoration is wrong here
            RebuildCanvas();
            return;
        }
        Replay(overlapping);
    }

    void Stamp(Shape shape)
    {
        EnsureCanvas();
        if (shape.Kind == "mosaic")
        {
            Rectangle mosaicArea = ClampToScreen(Normalize(shape.Start, shape.End));
            if (mosaicArea.Width < 2 || mosaicArea.Height < 2) return;
            using (Bitmap soft = BlurRegion(mosaicArea))
            {
                if (soft == null) return;
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    GraphicsState clip = g.Save();
                    g.SetClip(selection);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(soft, mosaicArea.Location);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.Restore(clip);
                }
            }
            return;
        }

        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            GraphicsState clip = g.Save();
            g.SetClip(selection);

            if (shape.Kind == "pen")
            {
                if (shape.Points.Count > 1)
                {
                    using (Pen pen = PenFor(shape)) g.DrawLines(pen, shape.Points.ToArray());
                }
            }
            else if (shape.Kind == "eraser")
            {
                float diameter = EraserDiameter(shape.Weight);
                for (int i = 1; i < shape.Points.Count; i++)
                {
                    RestoreFromScreen(g, shape.Points[i - 1], shape.Points[i], diameter);
                }
                if (shape.Points.Count == 1)
                {
                    RestoreFromScreen(g, shape.Points[0], shape.Points[0], diameter);
                }
            }
            else if (shape.Kind == "text" || shape.Kind == "number")
            {
                DrawLabel(g, shape);
            }
            else
            {
                using (Pen pen = PenFor(shape))
                {
                    if (shape.Kind == "rect") g.DrawRectangle(pen, Normalize(shape.Start, shape.End));
                    else if (shape.Kind == "ellipse") g.DrawEllipse(pen, Normalize(shape.Start, shape.End));
                    else if (shape.Kind == "line") g.DrawLine(pen, shape.Start, shape.End);
                    else if (shape.Kind == "arrow") DrawArrow(g, pen, shape);
                }
            }
            g.Restore(clip);
        }
    }

    static void DrawLabel(Graphics g, Shape shape)
    {
        if (string.IsNullOrEmpty(shape.Text)) return;
        using (Font font = new Font("Microsoft YaHei UI", shape.FontSize, FontStyle.Regular))
        using (SolidBrush brush = new SolidBrush(shape.Color))
        using (Pen ring = new Pen(shape.Color, Math.Max(1.6f, shape.FontSize / 9f)))
        {
            SizeF size = g.MeasureString(shape.Text, font);
            if (shape.Circle)
            {
                float radius = Math.Max(size.Width, size.Height) * 0.78f;
                g.DrawEllipse(ring, shape.Start.X - radius, shape.Start.Y - radius, radius * 2, radius * 2);
                g.DrawString(shape.Text, font, brush, shape.Start.X - size.Width / 2, shape.Start.Y - size.Height / 2);
            }
            else
            {
                g.DrawString(shape.Text, font, brush, shape.Start);
            }
        }
    }

    static Pen PenFor(Shape shape)
    {
        Pen pen = new Pen(shape.Color, StrokeFor(shape.Weight));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    static void DrawArrow(Graphics g, Pen pen, Shape shape)
    {
        g.DrawLine(pen, shape.Start, shape.End);
        double angle = Math.Atan2(shape.End.Y - shape.Start.Y, shape.End.X - shape.Start.X);
        float head = Math.Max(10f, pen.Width * 3.4f);
        PointF a = new PointF(
            shape.End.X - head * (float)Math.Cos(angle - Math.PI / 7),
            shape.End.Y - head * (float)Math.Sin(angle - Math.PI / 7));
        PointF b = new PointF(
            shape.End.X - head * (float)Math.Cos(angle + Math.PI / 7),
            shape.End.Y - head * (float)Math.Sin(angle + Math.PI / 7));
        using (SolidBrush brush = new SolidBrush(pen.Color))
        {
            g.FillPolygon(brush, new[] { shape.End, Point.Round(a), Point.Round(b) });
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            PaintFrame(e.Graphics, e.ClipRectangle);
        }
        catch
        {
            try { e.Graphics.DrawImage(screen, 0, 0); }
            catch { /* nothing left to draw with */ }
        }
    }

    void PaintFrame(Graphics g, Rectangle clip)
    {
        Rectangle patch = Rectangle.Intersect(clip, new Rectangle(0, 0, screen.Width, screen.Height));
        if (patch.Width <= 0 || patch.Height <= 0) return;

        // Composite only the dirty patch instead of pushing the whole desktop
        // through GDI+ on every mouse move.
        if (canvas != null) g.DrawImage(canvas, patch, patch, GraphicsUnit.Pixel);
        else g.DrawImage(screen, patch, patch, GraphicsUnit.Pixel);

        if (suppressed != null && (suppressed.Kind == "text" || suppressed.Kind == "number"))
        {
            GraphicsState live = g.Save();
            g.SetClip(selection);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawLabel(g, suppressed);
            g.Restore(live);
        }

        if (editing && drawing && current != null && current.Kind != "pen" && current.Kind != "eraser")
        {
            GraphicsState state = g.Save();
            g.SetClip(selection);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawPreview(g, current);
            g.Restore(state);
        }

        bool insideSelection = HasSelection && selection.Contains(patch);
        if (!insideSelection)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddRectangle(cropFrame);
                if (HasSelection) path.AddRectangle(selection);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(96, 0, 0, 0)))
                {
                    g.FillPath(shade, path);
                }
            }
        }

        if (!HasSelection)
        {
            if (clip.IntersectsWith(new Rectangle(0, 0, Width, 120))) DrawHint(g);
            return;
        }

        EnsureLayout();

        Rectangle frame = selection;
        frame.Inflate(3, 3);
        if (clip.IntersectsWith(frame))
        {
            using (Pen pen = new Pen(Color.FromArgb(0, 174, 255), 1f))
            {
                g.DrawRectangle(pen, selection);
            }
            DrawSizeLabel(g, selection);
        }

        if (!editing) return;

        if (clip.IntersectsWith(ActiveShapeArea())) DrawActiveShape(g);
        if (clip.IntersectsWith(EraserArea())) DrawEraserCursor(g);
        if (clip.IntersectsWith(toolbar))
        {
            DrawToolbar(g);
            if (flyout.Length > 0 && !subBar.IsEmpty && clip.IntersectsWith(subBar)) DrawSubBar(g);
        }
        else if (flyout.Length > 0 && !subBar.IsEmpty && clip.IntersectsWith(subBar))
        {
            DrawSubBar(g);
        }
    }

    Rectangle ActiveShapeArea()
    {
        if (activeShape == null) return Rectangle.Empty;
        Rectangle area = ShapeBounds(activeShape);
        area.Inflate(6, 6);
        return area;
    }

    Rectangle EraserArea()
    {
        if (!eraserOn || tool != "pen" || pointer.X < 0) return Rectangle.Empty;
        int radius = (int)(EraserDiameter(penWeight) / 2f) + 6;
        return new Rectangle(pointer.X - radius, pointer.Y - radius, radius * 2, radius * 2);
    }

    void DrawActiveShape(Graphics g)
    {
        if (activeShape == null) return;
        RectangleF box = LabelBounds(activeShape);
        box.Inflate(4, 4);
        using (Pen pen = new Pen(Color.FromArgb(255, 138, 138, 138), 1.2f))
        {
            pen.DashStyle = DashStyle.Dash;
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }
        foreach (Point handle in HandlePoints(box)) DrawHandle(g, handle);
    }

    void DrawHandle(Graphics g, Point at)
    {
        Rectangle box = new Rectangle(at.X - 4, at.Y - 4, 8, 8);
        using (SolidBrush fill = new SolidBrush(Color.White)) g.FillRectangle(fill, box);
        using (Pen pen = new Pen(Color.FromArgb(255, 138, 138, 138), 1.4f)) g.DrawRectangle(pen, box);
    }

    static Point[] HandlePoints(RectangleF box)
    {
        return new[]
        {
            new Point((int)box.Left, (int)box.Top),
            new Point((int)box.Right, (int)box.Top),
            new Point((int)box.Right, (int)box.Bottom),
            new Point((int)box.Left, (int)box.Bottom),
        };
    }

    void DrawEraserCursor(Graphics g)
    {
        if (!eraserOn || tool != "pen" || pointer.X < 0) return;
        if (!selection.Contains(pointer)) return;
        float diameter = EraserDiameter(penWeight);
        using (Pen light = new Pen(Color.FromArgb(240, 255, 255, 255), 1.4f))
        using (Pen dark = new Pen(Color.FromArgb(150, 0, 0, 0), 2.8f))
        {
            RectangleF ring = new RectangleF(pointer.X - diameter / 2, pointer.Y - diameter / 2, diameter, diameter);
            g.DrawEllipse(dark, ring);
            g.DrawEllipse(light, ring);
        }
    }

    void DrawPreview(Graphics g, Shape shape)
    {
        if (shape.Kind == "mosaic")
        {
            Rectangle area = ClampToScreen(Normalize(shape.Start, shape.End));
            if (area.Width < 2 || area.Height < 2) return;
            // Re-blurring on every mouse move is what made the drag stutter:
            // keep the last result and only refresh it when the box meaningfully
            // changes size.
            if (previewBlur == null
                || Math.Abs(area.Width - previewArea.Width) > 6
                || Math.Abs(area.Height - previewArea.Height) > 6)
            {
                if (previewBlur != null) previewBlur.Dispose();
                previewBlur = BlurRegion(area);
                previewArea = area;
            }
            if (previewBlur != null) g.DrawImageUnscaled(previewBlur, area.Location);
            return;
        }
        using (Pen pen = PenFor(shape))
        {
            if (shape.Kind == "rect") g.DrawRectangle(pen, Normalize(shape.Start, shape.End));
            else if (shape.Kind == "ellipse") g.DrawEllipse(pen, Normalize(shape.Start, shape.End));
            else if (shape.Kind == "line") g.DrawLine(pen, shape.Start, shape.End);
            else if (shape.Kind == "arrow") DrawArrow(g, pen, shape);
        }
    }

    Rectangle ClampToScreen(Rectangle area)
    {
        return Rectangle.Intersect(area, new Rectangle(0, 0, screen.Width, screen.Height));
    }

    Bitmap BlurRegion(Rectangle area)
    {
        area = ClampToScreen(area);
        if (area.Width < 2 || area.Height < 2) return null;
        int factor = Math.Max(6, Math.Min(area.Width, area.Height) / 6);
        int smallWidth = Math.Max(1, area.Width / factor);
        int smallHeight = Math.Max(1, area.Height / factor);

        Bitmap small = new Bitmap(smallWidth, smallHeight, PixelFormat.Format32bppRgb);
        using (Graphics sg = Graphics.FromImage(small))
        {
            sg.CompositingMode = CompositingMode.SourceCopy;
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // the blur covers only the screenshot; annotations are painted above
            // it in a second pass, so it must never sample them
            Bitmap source = screen;
            sg.DrawImage(source, new Rectangle(0, 0, smallWidth, smallHeight), area, GraphicsUnit.Pixel);
        }

        Bitmap result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        using (Graphics rg = Graphics.FromImage(result))
        using (ImageAttributes clamp = new ImageAttributes())
        {
            // TileFlipXY makes the upscale clamp at the borders instead of
            // sampling outside, and bilinear avoids the ringing a cubic filter
            // produces on the rim (a dark frame around the blurred patch).
            clamp.SetWrapMode(WrapMode.TileFlipXY);
            rg.CompositingMode = CompositingMode.SourceCopy;
            rg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            rg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            rg.DrawImage(
                small,
                new Rectangle(0, 0, area.Width, area.Height),
                0, 0, small.Width, small.Height,
                GraphicsUnit.Pixel,
                clamp);
        }
        small.Dispose();
        return result;
    }

    void DrawSizeLabel(Graphics g, Rectangle rect)
    {
        using (Font font = new Font("Segoe UI", 8.5f, FontStyle.Bold))
        {
            string label = rect.Width + " x " + rect.Height;
            SizeF size = g.MeasureString(label, font);
            float y = rect.Y - size.Height - 6;
            if (y < 0) y = rect.Y + 4;
            using (GraphicsPath pill = RoundRect(new Rectangle(rect.X, (int)y, (int)size.Width + 12, (int)size.Height + 4), 4))
            using (SolidBrush back = new SolidBrush(Color.FromArgb(200, 20, 20, 24)))
            {
                g.FillPath(back, pill);
            }
            g.DrawString(label, font, Brushes.White, rect.X + 6, y + 2);
        }
    }

    void DrawHint(Graphics g)
    {
        using (Font font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
        {
            string hint = "拖动鼠标选择截图区域      右键 / Esc 取消";
            SizeF size = g.MeasureString(hint, font);
            float x = (Width - size.Width) / 2f;
            Rectangle box = new Rectangle((int)x - 16, 24, (int)size.Width + 32, (int)size.Height + 16);
            using (GraphicsPath path = RoundRect(box, 10))
            using (SolidBrush back = new SolidBrush(BarFill)) g.FillPath(back, path);
            using (SolidBrush text = new SolidBrush(TextColor))
            {
                g.DrawString(hint, font, text, x, 30);
            }
        }
    }

    void EnsureLayout()
    {
        if (!layoutDirty) return;
        layoutDirty = false;
        BuildButtons(MeasureGraphics);
    }

    void BuildButtons(Graphics g)
    {
        buttons.Clear();
        dividers.Clear();

        using (Font font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
        {
            var tools = new List<string[]> { new[] { "标注", "annotate" }, new[] { "画笔", "pen" },
                new[] { "文字", "text" }, new[] { "马赛克", "mosaic" } };
            string[] actions = { "undo", "reset", "save", "cancel", "done" };
            string[] labels = { "撤销", "重选", "保存", "", "" };
            string[] icons = { "", "", "", "close", "check" };

            const int height = 32;
            const int gap = 4;
            const int padX = 10;

            int content = 0;
            var toolWidths = new List<int>();
            for (int i = 0; i < tools.Count; i++)
            {
                int w = (int)g.MeasureString(tools[i][0], font).Width + 22;
                toolWidths.Add(w);
                content += w + gap;
            }
            content += 12;
            content += Palette.Length * (18 + gap);
            content += 12;

            var actionWidths = new List<int>();
            for (int i = 0; i < actions.Length; i++)
            {
                int w = icons[i].Length > 0 ? 36 : (int)g.MeasureString(labels[i], font).Width + 22;
                actionWidths.Add(w);
                content += w + gap;
            }
            content += 12;

            int left = selection.X + (selection.Width - content) / 2;
            int top = selection.Bottom + 10;
            if (top + height + 12 > Height - 4) top = selection.Top - height - 14;
            if (top < 4) top = 4;
            if (left < 4) left = 4;
            if (left + content > Width - 4) left = Width - content - 4;

            toolbar = new Rectangle(left - padX, top - 6, content + padX * 2, height + 12);

            int cursor = left;
            for (int i = 0; i < tools.Count; i++)
            {
                buttons.Add(new ToolButton
                {
                    Bounds = new Rectangle(cursor, top, toolWidths[i], height),
                    Label = tools[i][0],
                    Action = "tool:" + tools[i][1],
                    Active = tool == tools[i][1],
                });
                cursor += toolWidths[i] + gap;
            }

            dividers.Add(new Rectangle(cursor + 5, top + 6, 1, height - 12));
            cursor += 12;

            for (int i = 0; i < Palette.Length; i++)
            {
                buttons.Add(new ToolButton
                {
                    Bounds = new Rectangle(cursor, top + 7, 18, 18),
                    Action = "color:" + i,
                    Swatch = Palette[i],
                    Active = color.ToArgb() == Palette[i].ToArgb(),
                });
                cursor += 18 + gap;
            }

            dividers.Add(new Rectangle(cursor + 5, top + 6, 1, height - 12));
            cursor += 12;

            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] == "cancel")
                {
                    dividers.Add(new Rectangle(cursor + 3, top + 6, 1, height - 12));
                    cursor += 12;
                }
                ToolButton button = new ToolButton
                {
                    Bounds = new Rectangle(cursor, top, actionWidths[i], height),
                    Label = labels[i],
                    Action = actions[i],
                    Icon = icons[i],
                };
                if (actions[i] == "cancel") button.IconColor = CloseRed;
                if (actions[i] == "done") button.IconColor = CheckGreen;
                buttons.Add(button);
                cursor += actionWidths[i] + gap;
            }

            BuildSubButtons();
        }
    }

    void BuildSubButtons()
    {
        subButtons.Clear();
        subBar = Rectangle.Empty;
        if (flyout.Length == 0) return;

        const int size = 30;
        const int gap = 4;
        const int pad = 6;
        var specs = new List<ToolButton>();

        if (flyout == "shapes")
        {
            foreach (string kind in new[] { "rect", "ellipse", "line", "arrow" })
            {
                specs.Add(new ToolButton { Action = "shape:" + kind, Icon = "shape", Variant = kind, Active = shapeTool == kind });
            }
        }
        else if (flyout == "pen")
        {
            for (int i = 1; i <= 3; i++)
            {
                specs.Add(new ToolButton { Action = "weight:" + i, Icon = "weight", Variant = i.ToString(), Active = penWeight == i });
            }
            specs.Add(new ToolButton { Action = "eraser", Icon = "eraser", Active = eraserOn, DividerBefore = true });
        }
        else if (flyout == "text")
        {
            for (int i = 1; i <= 3; i++)
            {
                specs.Add(new ToolButton { Action = "size:" + i, Icon = "size", Variant = i.ToString(), Active = textSize == i });
            }
            specs.Add(new ToolButton { Action = "number", Label = "编号", DividerBefore = true, Active = numberMode, Width = 58 });
        }

        foreach (ToolButton spec in specs) if (spec.Width <= 0) spec.Width = size;

        int width = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            width += specs[i].Width + (i > 0 ? gap : 0) + (specs[i].DividerBefore ? 10 : 0);
        }

        int left = toolbar.X + pad;
        if (left + width + pad * 2 > Width - 4) left = Width - width - pad * 2 - 4;
        if (left < 4) left = 4;

        int top = toolbar.Bottom + 6;
        if (top + size + pad * 2 > Height - 4) top = toolbar.Top - (size + pad * 2) - 6;
        if (top < 4) top = 4;

        subBar = new Rectangle(left, top, width + pad * 2, size + pad * 2);
        int cursor = left + pad;
        foreach (ToolButton spec in specs)
        {
            if (spec.DividerBefore)
            {
                dividers.Add(new Rectangle(cursor - 5, top + pad + 4, 1, size - 8));
                cursor += 10;
            }
            spec.Bounds = new Rectangle(cursor, top + pad, spec.Width, size);
            subButtons.Add(spec);
            cursor += spec.Width + gap;
        }
    }

    void DrawToolbar(Graphics g)
    {
        using (Font font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
        {
            Rectangle shadow = new Rectangle(toolbar.X, toolbar.Y + 3, toolbar.Width, toolbar.Height);
            using (GraphicsPath path = RoundRect(shadow, 12))
            using (SolidBrush back = new SolidBrush(BarShadow)) g.FillPath(back, path);

            using (GraphicsPath path = RoundRect(toolbar, 12))
            {
                using (SolidBrush back = new SolidBrush(BarFill)) g.FillPath(back, path);
                using (Pen border = new Pen(BarBorder)) g.DrawPath(border, path);
            }

            foreach (Rectangle divider in dividers)
            {
                if (divider.Top >= toolbar.Top && divider.Bottom <= toolbar.Bottom + 2)
                {
                    using (SolidBrush fill = new SolidBrush(Divider)) g.FillRectangle(fill, divider);
                }
            }

            for (int i = 0; i < buttons.Count; i++)
            {
                ToolButton button = buttons[i];
                if (button.Swatch != Color.Empty)
                {
                    using (SolidBrush fill = new SolidBrush(button.Swatch)) g.FillEllipse(fill, button.Bounds);
                    using (Pen outline = new Pen(Color.FromArgb(46, 0, 0, 0), 1f)) g.DrawEllipse(outline, button.Bounds);
                    if (button.Active)
                    {
                        using (Pen ring = new Pen(ActiveRing, 2f))
                        {
                            Rectangle ringRect = button.Bounds;
                            ringRect.Inflate(2, 2);
                            g.DrawEllipse(ring, ringRect);
                        }
                    }
                    continue;
                }

                Color foreground = button.Active ? ActiveInk : (button.IconColor.IsEmpty ? TextColor : button.IconColor);
                if (!string.IsNullOrEmpty(button.Icon))
                {
                    DrawActionIcon(g, button, foreground);
                }
                else
                {
                    using (SolidBrush text = new SolidBrush(foreground))
                    {
                        SizeF size = g.MeasureString(button.Label, font);
                        g.DrawString(button.Label, font, text,
                            button.Bounds.X + (button.Bounds.Width - size.Width) / 2,
                            button.Bounds.Y + (button.Bounds.Height - size.Height) / 2);
                    }
                }
            }
        }
    }

    void DrawSubBar(Graphics g)
    {
        if (subBar.IsEmpty) return;

        Rectangle shadow = new Rectangle(subBar.X, subBar.Y + 3, subBar.Width, subBar.Height);
        using (GraphicsPath path = RoundRect(shadow, 10))
        using (SolidBrush back = new SolidBrush(BarShadow)) g.FillPath(back, path);

        using (GraphicsPath path = RoundRect(subBar, 10))
        {
            using (SolidBrush back = new SolidBrush(BarFill)) g.FillPath(back, path);
            using (Pen border = new Pen(BarBorder)) g.DrawPath(border, path);
        }

        foreach (Rectangle divider in dividers)
        {
            if (divider.Top >= subBar.Top && divider.Bottom <= subBar.Bottom + 2)
            {
                using (SolidBrush fill = new SolidBrush(Divider)) g.FillRectangle(fill, divider);
            }
        }

        foreach (ToolButton button in subButtons)
        {
            if (!string.IsNullOrEmpty(button.Label))
            {
                using (Font font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(button.Active ? ActiveInk : TextColor))
                {
                    SizeF size = g.MeasureString(button.Label, font);
                    g.DrawString(button.Label, font, brush,
                        button.Bounds.X + (button.Bounds.Width - size.Width) / 2,
                        button.Bounds.Y + (button.Bounds.Height - size.Height) / 2);
                }
                continue;
            }
            DrawSubIcon(g, button, button.Active ? ActiveInk : TextColor);
        }
    }

    void DrawSubIcon(Graphics g, ToolButton button, Color stroke)
    {
        Rectangle box = button.Bounds;

        if (button.Icon == "shape")
        {
            using (Pen pen = new Pen(stroke, 1.6f))
            {
                Rectangle inner = box;
                inner.Inflate(-9, -9);
                if (button.Variant == "rect") g.DrawRectangle(pen, inner);
                else if (button.Variant == "ellipse") g.DrawEllipse(pen, inner);
                else if (button.Variant == "line") g.DrawLine(pen, inner.Left, inner.Bottom, inner.Right, inner.Top);
                else if (button.Variant == "arrow")
                {
                    g.DrawLine(pen, inner.Left, inner.Bottom, inner.Right, inner.Top);
                    using (SolidBrush head = new SolidBrush(stroke))
                    {
                        g.FillPolygon(head, new[]
                        {
                            new Point(inner.Right, inner.Top),
                            new Point(inner.Right - 10, inner.Top),
                            new Point(inner.Right, inner.Top + 10),
                        });
                    }
                }
            }
            return;
        }

        if (button.Icon == "weight")
        {
            int weight = int.Parse(button.Variant);
            float thickness = weight == 1 ? 2.2f : (weight == 2 ? 4.6f : 7.4f);
            using (Pen pen = new Pen(stroke, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cy = box.Y + box.Height / 2;
                g.DrawLine(pen, box.X + 4, cy, box.Right - 4, cy);
            }
            return;
        }

        if (button.Icon == "eraser")
        {
            using (Pen pen = new Pen(stroke, 1.7f))
            {
                pen.LineJoin = LineJoin.Round;
                Point[] body =
                {
                    new Point(box.X + 13, box.Bottom - 8),
                    new Point(box.X + 8, box.Bottom - 13),
                    new Point(box.Right - 12, box.Y + 9),
                    new Point(box.Right - 7, box.Y + 14),
                };
                g.DrawPolygon(pen, body);
                g.DrawLine(pen, box.X + 12, box.Bottom - 10, box.Right - 9, box.Y + 12);
                g.DrawLine(pen, box.X + 8, box.Bottom - 6, box.Right - 6, box.Bottom - 6);
            }
            return;
        }

        if (button.Icon == "size")
        {
            int size = int.Parse(button.Variant);
            float fontSize = size == 1 ? 9.5f : (size == 2 ? 13.5f : 17f);
            using (Font font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(stroke))
            {
                SizeF measured = g.MeasureString("A", font);
                g.DrawString("A", font, brush,
                    box.X + (box.Width - measured.Width) / 2,
                    box.Y + (box.Height - measured.Height) / 2);
            }
            return;
        }

    }

    void DrawActionIcon(Graphics g, ToolButton button, Color color)
    {
        Rectangle box = button.Bounds;
        int cx = box.X + box.Width / 2;
        int cy = box.Y + box.Height / 2;
        const int r = 9;
        using (Pen pen = new Pen(color, 2.4f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            if (button.Icon == "close")
            {
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
            }
            else if (button.Icon == "check")
            {
                g.DrawLines(pen, new[]
                {
                    new Point(cx - r - 1, cy),
                    new Point(cx - 2, cy + r - 1),
                    new Point(cx + r + 1, cy - r),
                });
            }
        }
    }

    static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    Rectangle ShapeBounds(Shape shape)
    {
        if (shape == null) return Rectangle.Empty;
        if (shape.Kind == "text" || shape.Kind == "number")
        {
            RectangleF box = LabelBounds(shape);
            box.Inflate(8, 8);
            return Rectangle.Round(box);
        }
        Rectangle area = Normalize(shape.Start, shape.End);
        area.Inflate(10, 10);
        return area;
    }

    static Rectangle Normalize(Point a, Point b)
    {
        return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    Graphics MeasureGraphics
    {
        get
        {
            if (measureGraphics == null)
            {
                try
                {
                    measureGraphics = CreateGraphics();
                }
                catch
                {
                    // No window handle yet (headless self test): measure on a scratch surface.
                    ensureScratch();
                    measureGraphics = Graphics.FromImage(scratch);
                }
            }
            return measureGraphics;
        }
    }

    Bitmap scratch;
    Graphics hotGraphics;
    TextureBrush screenBrush;

    /// <summary>Long-lived Graphics on the canvas; callers must Save/Restore.</summary>
    Graphics HotGraphics
    {
        get
        {
            EnsureCanvas();
            if (hotGraphics == null) hotGraphics = Graphics.FromImage(canvas);
            return hotGraphics;
        }
    }

    TextureBrush ScreenBrush
    {
        get
        {
            if (screenBrush == null)
            {
                screenBrush = new TextureBrush(screen);
                screenBrush.WrapMode = WrapMode.Clamp;
            }
            return screenBrush;
        }
    }

    void ensureScratch()
    {
        if (scratch == null) scratch = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
    }

    RectangleF LabelBounds(Shape shape)
    {
        using (Font font = new Font("Microsoft YaHei UI", shape.FontSize, FontStyle.Regular))
        {
            // Shared instance: callers must never dispose it.
            Graphics g = MeasureGraphics;
            SizeF size = g.MeasureString(shape.Text ?? "", font);
            if (shape.Circle)
            {
                float radius = Math.Max(size.Width, size.Height) * 0.78f;
                return new RectangleF(shape.Start.X - radius, shape.Start.Y - radius, radius * 2, radius * 2);
            }
            return new RectangleF(shape.Start.X, shape.Start.Y, size.Width, size.Height);
        }
    }

    Shape HitLabel(Point at)
    {
        for (int i = shapes.Count - 1; i >= 0; i--)
        {
            Shape shape = shapes[i];
            if (shape.Kind != "text" && shape.Kind != "number") continue;
            RectangleF box = LabelBounds(shape);
            box.Inflate(8, 8);
            if (box.Contains(at)) return shape;
        }
        return null;
    }

    int HitHandle(Point at)
    {
        if (activeShape == null) return -1;
        RectangleF box = LabelBounds(activeShape);
        box.Inflate(4, 4);
        Point[] handles = HandlePoints(box);
        for (int i = 0; i < handles.Length; i++)
        {
            if (Math.Abs(at.X - handles[i].X) <= 7 && Math.Abs(at.Y - handles[i].Y) <= 7) return i;
        }
        return -1;
    }

    void CommitEditor()
    {
        if (editor == null) return;
        TextBox box = editor;
        editor = null;
        Shape target = editingShape;
        editingShape = null;
        string value = box.Text;
        Controls.Remove(box);
        box.Dispose();

        if (!string.IsNullOrEmpty(value) && HasSelection)
        {
            if (target != null)
            {
                target.Text = value;
                RebuildCanvas();
            }
            else
            {
                Shape shape = new Shape
                {
                    Kind = "text",
                    Text = value,
                    Color = color,
                    Start = box.Location,
                    FontSize = FontFor(textSize),
                };
                shapes.Add(shape);
                Stamp(shape);
                activeShape = shape;
            }
        }
        Invalidate();
    }

    void OpenEditor(Shape existing, Point at)
    {
        CommitEditor();
        editingShape = existing;
        float fontSize = existing != null ? existing.FontSize : FontFor(textSize);
        editor = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = existing != null ? existing.Color : color,
            Font = new Font("Microsoft YaHei UI", Math.Max(8f, fontSize)),
            Location = existing != null ? new Point((int)existing.Start.X, (int)existing.Start.Y) : at,
            Width = 220,
            Text = existing != null ? existing.Text : "",
        };
        {
            Graphics measure = MeasureGraphics;
            string sample = string.IsNullOrEmpty(editor.Text) ? "输入文字" : editor.Text;
            SizeF measured = measure.MeasureString(sample, editor.Font);
            int wanted = (int)measured.Width + 26;
            int ceiling = Math.Max(180, selection.Width - 30);
            editor.Width = Math.Max(120, Math.Min(wanted, ceiling));
        }
        editor.KeyDown += (s, ev) =>
        {
            if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; CommitEditor(); }
            else if (ev.KeyCode == Keys.Escape)
            {
                ev.SuppressKeyPress = true;
                if (editor != null)
                {
                    TextBox pending = editor;
                    editor = null;
                    editingShape = null;
                    Controls.Remove(pending);
                    pending.Dispose();
                    Invalidate();
                }
            }
        };
        editor.LostFocus += (s, ev) => CommitEditor();
        Controls.Add(editor);
        editor.BringToFront();
        editor.Focus();
        if (existing != null) editor.SelectAll();
    }

    void SaveToFile()
    {
        CommitEditor();
        using (SaveFileDialog dialog = new SaveFileDialog())
        {
            dialog.Filter = "PNG 图片|*.png";
            dialog.FileName = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            bool wasTopMost = TopMost;
            TopMost = false;
            DialogResult result = dialog.ShowDialog(this);
            TopMost = wasTopMost;
            Activate();
            if (result != DialogResult.OK) return;
            try
            {
                using (Bitmap image = RenderResult()) image.Save(dialog.FileName, ImageFormat.Png);
            }
            catch (Exception error)
            {
                MessageBox.Show("保存失败：" + error.Message, "截图标注",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Saving is a pure export: the file is written and the session ends
            // without handing the image to the chat box. Cancelling or failing
            // keeps the editor open.
            SavedOnly = true;
            Completed = false;
            Close();
        }
    }

    void RunAction(string action)
    {
        if (action == "undo")
        {
            if (shapes.Count > 0)
            {
                shapes.RemoveAt(shapes.Count - 1);
                if (activeShape != null && !shapes.Contains(activeShape)) activeShape = null;
                RebuildCanvas();
            }
        }
        else if (action == "reset")
        {
            layoutDirty = true;
            CommitEditor();
            shapes.Clear();
            RebuildCanvas();
            activeShape = null;
            selection = Rectangle.Empty;
            editing = false;
            flyout = "";
            toolbar = Rectangle.Empty;
            subBar = Rectangle.Empty;
            nextNumber = 1;
        }
        else if (action == "save") SaveToFile();
        else if (action == "cancel") { Completed = false; Close(); return; }
        else if (action == "done")
        {
            CommitEditor();
            Completed = true;
            Chosen = selection;
            Close();
            return;
        }
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 右键 = 直接取消整张截图（微信习惯）；Esc 的逐级退出保留不动。
        if (e.Button == MouseButtons.Right)
        {
            Completed = false;
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (editor != null) CommitEditor();

        EnsureLayout();

        if (editing && flyout.Length > 0 && subBar.Contains(e.Location))
        {
            foreach (ToolButton button in subButtons)
            {
                if (!button.Bounds.Contains(e.Location)) continue;
                HandleSubAction(button.Action);
                Invalidate();
                return;
            }
            return;
        }

        if (editing && toolbar.Contains(e.Location))
        {
            foreach (ToolButton button in buttons)
            {
                if (!button.Bounds.Contains(e.Location)) continue;
                if (button.Action.StartsWith("tool:"))
                {
                    string next = button.Action.Substring(5);
                    tool = tool == next ? "" : next;
                    flyout = tool == "annotate" ? "shapes" : (tool == "pen" ? "pen" : (tool == "text" ? "text" : ""));
                    if (flyout == "shapes" && string.IsNullOrEmpty(shapeTool)) shapeTool = "rect";
                    activeShape = null;
                    layoutDirty = true;
                }
                else if (button.Action.StartsWith("color:"))
                {
                    color = Palette[int.Parse(button.Action.Substring(6))];
                    layoutDirty = true;
                }
                else
                {
                    RunAction(button.Action);
                }
                Invalidate();
                return;
            }
            return;
        }

        if (!HasSelection)
        {
            dragging = true;
            anchor = e.Location;
            return;
        }

        if (!editing || !selection.Contains(e.Location)) return;

        int handle = HitHandle(e.Location);
        if (handle >= 0)
        {
            resizing = true;
            suppressed = activeShape;
            LiftShape(activeShape);
            resizeOrigin = e.Location;
            resizeStartFont = activeShape.FontSize;
            RectangleF box = LabelBounds(activeShape);
            float dx = e.Location.X - (box.Left + box.Width / 2);
            float dy = e.Location.Y - (box.Top + box.Height / 2);
            resizeStartDistance = Math.Max(6f, (float)Math.Sqrt(dx * dx + dy * dy));
            return;
        }

        Shape hit = HitLabel(e.Location);
        if (hit != null)
        {
            if (activeShape == hit && e.Clicks >= 2)
            {
                OpenEditor(hit, e.Location);
                return;
            }
            activeShape = hit;
            suppressed = hit;
            LiftShape(hit);
            moving = true;
            moveStart = hit.Start;
            moveOrigin = e.Location;
            Invalidate();
            return;
        }

        activeShape = null;

        if (tool == "text" && !numberMode)
        {
            OpenEditor(null, e.Location);
            return;
        }

        string kind = tool == "annotate" ? shapeTool
            : (tool == "pen" ? (eraserOn ? "eraser" : "pen")
            : (tool == "text" && numberMode ? "number" : tool));
        if (string.IsNullOrEmpty(kind) || kind == "text") return;

        if (kind == "number")
        {
            Shape badge = new Shape
            {
                Kind = "number",
                Text = nextNumber.ToString(),
                Color = color,
                Start = e.Location,
                FontSize = NumberFor(textSize),
                Circle = true,
            };
            nextNumber += 1;
            shapes.Add(badge);
            Stamp(badge);
            activeShape = badge;
            Invalidate();
            return;
        }

        drawing = true;
        current = new Shape
        {
            Kind = kind,
            Color = color,
            Start = e.Location,
            End = e.Location,
            Weight = penWeight,
            FontSize = FontFor(textSize),
        };
        current.Points.Add(e.Location);

        if (kind == "pen" || kind == "eraser") shapes.Add(current);
        Invalidate();
    }

    void HandleSubAction(string action)
    {
        layoutDirty = true;
        if (action.StartsWith("shape:")) { shapeTool = action.Substring(6); tool = "annotate"; }
        else if (action.StartsWith("weight:")) penWeight = int.Parse(action.Substring(7));
        else if (action == "eraser") eraserOn = !eraserOn;
        else if (action.StartsWith("size:")) textSize = int.Parse(action.Substring(5));
        else if (action == "number") numberMode = !numberMode;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point previousPointer = pointer;
        pointer = e.Location;

        if (resizing && activeShape != null)
        {
            Rectangle before = ShapeBounds(activeShape);
            RectangleF box = LabelBounds(activeShape);
            float dx = e.Location.X - (box.Left + box.Width / 2);
            float dy = e.Location.Y - (box.Top + box.Height / 2);
            float distance = Math.Max(6f, (float)Math.Sqrt(dx * dx + dy * dy));
            activeShape.FontSize = Math.Max(8f, Math.Min(120f, resizeStartFont * (distance / resizeStartDistance)));
            Rectangle dirty = Rectangle.Union(before, ShapeBounds(activeShape));
            dirty.Inflate(6, 6);
            Invalidate(dirty);
            return;
        }

        if (moving && activeShape != null)
        {
            Rectangle before = ShapeBounds(activeShape);
            int dx = e.X - moveOrigin.X;
            int dy = e.Y - moveOrigin.Y;
            activeShape.Start = new Point(activeShape.Start.X + dx, activeShape.Start.Y + dy);

            // keep the label inside the crop, otherwise SetClip(selection) erases
            // it and it can never be grabbed back
            RectangleF box = LabelBounds(activeShape);
            int shiftX = 0;
            int shiftY = 0;
            if (box.Left < selection.Left) shiftX = (int)Math.Ceiling(selection.Left - box.Left);
            if (box.Right > selection.Right) shiftX = (int)Math.Floor(selection.Right - box.Right);
            if (box.Top < selection.Top) shiftY = (int)Math.Ceiling(selection.Top - box.Top);
            if (box.Bottom > selection.Bottom) shiftY = (int)Math.Floor(selection.Bottom - box.Bottom);
            if (shiftX != 0 || shiftY != 0)
            {
                activeShape.Start = new Point(activeShape.Start.X + shiftX, activeShape.Start.Y + shiftY);
            }
            moveOrigin = e.Location;
            Rectangle dirty = Rectangle.Union(before, ShapeBounds(activeShape));
            dirty.Inflate(6, 6);
            Invalidate(dirty);
            return;
        }

        if (dragging)
        {
            selection = Normalize(anchor, e.Location);
            Invalidate();
            return;
        }

        if (drawing && current != null)
        {
            if (current.Kind == "pen" || current.Kind == "eraser")
            {
                Point last = current.Points[current.Points.Count - 1];
                current.Points.Add(e.Location);
                Graphics g = HotGraphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                GraphicsState clip = g.Save();
                g.SetClip(selection);
                if (current.Kind == "pen")
                {
                    using (Pen pen = PenFor(current)) g.DrawLine(pen, last, e.Location);
                }
                else
                {
                    RestoreFromScreen(g, last, e.Location, EraserDiameter(current.Weight));
                }
                g.Restore(clip);
                float brushSize = current.Kind == "eraser"
                    ? EraserDiameter(current.Weight)
                    : StrokeFor(current.Weight);
                int margin = (int)(brushSize / 2f) + 8;
                Rectangle dirty = new Rectangle(
                    Math.Min(last.X, e.Location.X) - margin,
                    Math.Min(last.Y, e.Location.Y) - margin,
                    Math.Abs(e.Location.X - last.X) + margin * 2,
                    Math.Abs(e.Location.Y - last.Y) + margin * 2);
                Invalidate(dirty);
                return;
            }

            Rectangle previous = Normalize(current.Start, current.End);
            current.End = e.Location;
            Rectangle previewDirty = Rectangle.Union(previous, Normalize(current.Start, current.End));
            // the stroke itself and the arrow head reach past the bounding box
            int pad = (int)StrokeFor(current.Weight) + 30;
            previewDirty.Inflate(pad, pad);
            Invalidate(previewDirty);
            return;
        }

        EnsureLayout();
        int hover = -1;
        if (editing && toolbar.Contains(e.Location))
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].Bounds.Contains(e.Location)) { hover = i; break; }
            }
        }
        Cursor = !editing ? Cursors.Cross
            : (HitHandle(e.Location) >= 0 ? Cursors.SizeNWSE
            : (hover >= 0 ? Cursors.Hand : Cursors.Default));

        if (eraserOn && tool == "pen")
        {
            int radius = (int)(EraserDiameter(penWeight) / 2f) + 4;
            Invalidate(new Rectangle(previousPointer.X - radius, previousPointer.Y - radius, radius * 2, radius * 2));
            Invalidate(new Rectangle(pointer.X - radius, pointer.Y - radius, radius * 2, radius * 2));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (resizing)
        {
            resizing = false;
            Shape landed = suppressed;
            suppressed = null;
            if (landed == null)
            {
                Invalidate();
                return;
            }
            if (Math.Abs(landed.FontSize - resizeStartFont) < 0.5f)
            {
                RebuildCanvas();
            }
            else
            {
                Stamp(landed);
            }
            Invalidate();
            return;
        }

        if (moving)
        {
            moving = false;
            Shape landed = suppressed;
            suppressed = null;
            if (landed == null)
            {
                Invalidate();
                return;
            }
            if (landed.Start == moveStart)
            {
                // no movement: restore the exact previous stacking order instead
                // of re-stamping the label on top of a blur that covered it
                RebuildCanvas();
            }
            else
            {
                Stamp(landed);
            }
            Invalidate();
            return;
        }

        if (dragging)
        {
            dragging = false;
            if (selection.Width <= 4 || selection.Height <= 4)
            {
                selection = Rectangle.Empty;
                editing = false;
                Invalidate();
            }
            else
            {
                editing = true;
                layoutDirty = true;
                Cursor = Cursors.Default;
                Invalidate();
            }
            return;
        }

        if (drawing)
        {
            drawing = false;
            if (previewBlur != null)
            {
                previewBlur.Dispose();
                previewBlur = null;
                previewArea = Rectangle.Empty;
            }
            if (current != null)
            {
                bool tiny = Math.Abs(current.End.X - current.Start.X) < 3
                    && Math.Abs(current.End.Y - current.Start.Y) < 3;
                if (current.Kind == "pen" || current.Kind == "eraser")
                {
                    if (current.Points.Count < 2) shapes.Remove(current);
                }
                else if (tiny) shapes.Remove(current);
                else
                {
                    shapes.Add(current);
                    if (current.Kind == "mosaic") RebuildCanvas();
                    else Stamp(current);
                }
                current = null;
            }
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (editor != null) { base.OnKeyDown(e); return; }
        if (e.KeyCode == Keys.Escape)
        {
            if (activeShape != null)
            {
                activeShape = null;
                Invalidate();
            }
            else if (HasSelection)
            {
                shapes.Clear();
                RebuildCanvas();
                selection = Rectangle.Empty;
                editing = false;
                flyout = "";
                Invalidate();
            }
            else
            {
                Completed = false;
                Close();
            }
        }
        else if (e.KeyCode == Keys.Delete && activeShape != null)
        {
            shapes.Remove(activeShape);
            activeShape = null;
            RebuildCanvas();
            Invalidate();
        }
        else if (e.KeyCode == Keys.Enter && HasSelection)
        {
            CommitEditor();
            Completed = true;
            Chosen = selection;
            Close();
        }
        base.OnKeyDown(e);
    }

    /* ------------------------------------------------------------ self test */

    int DiffCount(Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
        int diff = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (canvas.GetPixel(x, y).ToArgb() != screen.GetPixel(x, y).ToArgb()) diff++;
            }
        }
        return diff;
    }

    int CountNear(Rectangle area, Color target, int tolerance)
    {
        area.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
        int hits = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                Color c = canvas.GetPixel(x, y);
                if (Math.Abs(c.R - target.R) <= tolerance
                    && Math.Abs(c.G - target.G) <= tolerance
                    && Math.Abs(c.B - target.B) <= tolerance) hits++;
            }
        }
        return hits;
    }

    bool AllOpaque(Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (canvas.GetPixel(x, y).A != 255) return false;
            }
        }
        return true;
    }

    int MaxChannelError(Rectangle area, out Point at, out string detail)
    {
        area.Intersect(new Rectangle(0, 0, screen.Width, screen.Height));
        int worst = 0;
        at = Point.Empty;
        detail = "";
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                Color a = canvas.GetPixel(x, y);
                Color b = screen.GetPixel(x, y);
                int e = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                e = Math.Max(e, Math.Abs(a.A - b.A));
                if (e <= worst) continue;
                worst = e;
                at = new Point(x, y);
                detail = a + " vs " + b;
            }
        }
        return worst;
    }

    /// <summary>
    /// Behavioural checks over the annotation model. Runs headless: no window,
    /// no mouse. Every regression I shipped so far would have been caught here.
    /// </summary>
    public List<string> RunSelfTest()
    {
        List<string> log = new List<string>();
        int fails = 0;
        Action<string, bool> check = (name, ok) =>
        {
            log.Add((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) fails++;
        };

        Rectangle everything = new Rectangle(0, 0, screen.Width, screen.Height);

        RebuildCanvas();
        check("画布初始与截图完全一致", DiffCount(everything) == 0);

        Rectangle textBox = new Rectangle(46, 44, 80, 50);
        Shape text = new Shape { Kind = "text", Text = "AB", Color = Color.Red, FontSize = 24f, Start = new Point(50, 50) };
        shapes.Add(text);
        Stamp(text);
        check("文字落笔后有像素变化", DiffCount(textBox) > 20);

        LiftShape(text);
        check("抬升后原位恢复成截图", DiffCount(textBox) == 0);

        text.Start = new Point(210, 50);
        Stamp(text);
        check("移动后原位没有残留", DiffCount(textBox) == 0);
        check("移动后新位置出现文字", DiffCount(new Rectangle(206, 44, 80, 50)) > 20);

        Rectangle penBox = new Rectangle(100, 112, 195, 16);
        Shape pen = new Shape
        {
            Kind = "pen", Color = Color.Blue, Weight = 3,
            Points = new List<Point> { new Point(100, 120), new Point(300, 120) },
        };
        shapes.Add(pen);
        Stamp(pen);
        check("画笔落笔后有像素变化", DiffCount(penBox) > 20);

        Shape eraser = new Shape
        {
            Kind = "eraser", Weight = 2,
            Points = new List<Point> { new Point(100, 120), new Point(300, 120) },
        };
        shapes.Add(eraser);
        Stamp(eraser);
        check("橡皮擦一次擦干净（完全恢复截图）", DiffCount(penBox) == 0);

        Rectangle mosaicBox = new Rectangle(40, 220, 60, 30);
        Shape flatMosaic = new Shape { Kind = "mosaic", Start = new Point(20, 200), End = new Point(120, 260) };
        shapes.Add(flatMosaic);
        Stamp(flatMosaic);
        // Blurring a uniform area must stay that colour: a few counts of
        // rounding are fine, a translucent or colour-shifted rim (alpha 77,
        // channel error 39 before the fix) is not.
        Point worstAt;
        string worstDetail;
        int flatError = MaxChannelError(mosaicBox, out worstAt, out worstDetail);
        log.Add("      均匀区域最大通道误差 = " + flatError + " @" + worstAt);
        check("马赛克的每个像素都不透明", AllOpaque(mosaicBox));
        check("均匀底色上马赛克颜色不漂移", flatError <= 12);
        shapes.Remove(flatMosaic);
        RebuildCanvas();

        Shape ink = new Shape
        {
            Kind = "pen", Color = Color.White, Weight = 3,
            Points = new List<Point> { new Point(30, 232), new Point(110, 232) },
        };
        shapes.Add(ink);
        Stamp(ink);
        check("马赛克区域内先有内容", DiffCount(new Rectangle(35, 228, 70, 12)) > 20);

        Shape mosaic = new Shape { Kind = "mosaic", Start = new Point(20, 200), End = new Point(120, 260) };
        shapes.Add(mosaic);
        RebuildCanvas();
        check("马赛克不会抹掉其上的笔迹",
            CountNear(new Rectangle(35, 228, 70, 12), Color.White, 70) > 10);

        Shape underlay = new Shape { Kind = "mosaic", Start = new Point(140, 140), End = new Point(330, 270) };
        shapes.Add(underlay);
        Stamp(underlay);
        Shape[] notes =
        {
            new Shape { Kind = "text", Text = "甲", Color = Color.Red, FontSize = 20f, Start = new Point(160, 160) },
            new Shape { Kind = "text", Text = "乙", Color = Color.Red, FontSize = 20f, Start = new Point(200, 200) },
            new Shape { Kind = "text", Text = "丙", Color = Color.Red, FontSize = 20f, Start = new Point(240, 240) },
        };
        foreach (Shape note in notes)
        {
            shapes.Add(note);
            Stamp(note);
        }
        check("马赛克上写的第一行字可见", CountNear(new Rectangle(155, 155, 40, 34), Color.Red, 70) > 5);
        check("马赛克上写的第三行字可见", CountNear(new Rectangle(235, 235, 40, 34), Color.Red, 70) > 5);

        // editing an existing label rebuilds the whole canvas
        RebuildCanvas();
        check("重建后第一行字仍在（不被马赛克盖掉）", CountNear(new Rectangle(155, 155, 40, 34), Color.Red, 70) > 5);
        check("重建后第三行字仍在（不被马赛克盖掉）", CountNear(new Rectangle(235, 235, 40, 34), Color.Red, 70) > 5);

        // clicking one label must not eat its neighbours on the same mosaic
        Shape probe = new Shape { Kind = "text", Text = "丁", Color = Color.Red, FontSize = 20f, Start = new Point(280, 200) };
        shapes.Add(probe);
        Stamp(probe);
        LiftShape(probe);
        check("抬升一个文字不会擦掉同一马赛克上的其他文字",
            CountNear(new Rectangle(235, 235, 40, 34), Color.Red, 70) > 5
            && CountNear(new Rectangle(155, 155, 40, 34), Color.Red, 70) > 5);
        Stamp(probe);

        Shape buried = new Shape { Kind = "text", Text = "戊", Color = Color.Red, FontSize = 20f, Start = new Point(300, 40) };
        shapes.Add(buried);
        Stamp(buried);
        Shape lid = new Shape { Kind = "mosaic", Start = new Point(290, 30), End = new Point(345, 85) };
        shapes.Add(lid);
        RebuildCanvas();
        check("文字不会被后加的马赛克盖住", CountNear(new Rectangle(296, 36, 44, 36), Color.Red, 70) > 5);

        LiftShape(buried);
        RebuildCanvas();
        check("点选之后文字依然可见", CountNear(new Rectangle(296, 36, 44, 36), Color.Red, 70) > 5);

        shapes.Remove(lid);
        shapes.Remove(buried);
        RebuildCanvas();

        Shape badge = new Shape
        {
            Kind = "number", Text = "1", Color = Color.Red, FontSize = 18f,
            Circle = true, Start = new Point(200, 200),
        };
        shapes.Add(badge);
        Stamp(badge);
        check("圈号落笔后有像素变化", DiffCount(new Rectangle(180, 180, 42, 42)) > 20);

        RebuildCanvas();
        check("重建画布后文字仍在", DiffCount(new Rectangle(206, 44, 80, 50)) > 20);
        check("重建画布后橡皮仍然生效", DiffCount(penBox) == 0);

        using (Bitmap export = RenderResult())
        {
            check("导出尺寸等于选区",
                export.Width == selection.Width && export.Height == selection.Height);

            bool matches = true;
            for (int i = 0; i < 60 && matches; i++)
            {
                int x = selection.Left + (i * 5) % (selection.Width - 2);
                int y = selection.Top + (i * 7) % (selection.Height - 2);
                if (export.GetPixel(x - selection.Left, y - selection.Top).ToArgb()
                    != canvas.GetPixel(x, y).ToArgb()) matches = false;
            }
            check("导出像素与画布逐点一致", matches);
        }

        log.Add(fails == 0 ? "RESULT: ALL PASS" : "RESULT: " + fails + " FAILED");
        return log;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (editor != null)
            {
                Controls.Remove(editor);
                editor.Dispose();
                editor = null;
            }
            if (hotGraphics != null)
            {
                hotGraphics.Dispose();
                hotGraphics = null;
            }
            if (screenBrush != null)
            {
                screenBrush.Dispose();
                screenBrush = null;
            }
            if (canvas != null)
            {
                canvas.Dispose();
                canvas = null;
            }
            if (previewBlur != null)
            {
                previewBlur.Dispose();
                previewBlur = null;
            }
            if (measureGraphics != null)
            {
                measureGraphics.Dispose();
                measureGraphics = null;
            }
        }
        base.Dispose(disposing);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();

        int selfIndex = Array.IndexOf(args, "--selftest");
        if (selfIndex >= 0)
        {
            string report = selfIndex + 1 < args.Length && !args[selfIndex + 1].StartsWith("--")
                ? args[selfIndex + 1]
                : null;
            Bitmap board = new Bitmap(400, 300, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(board)) g.Clear(Color.FromArgb(255, 30, 110, 90));
            List<string> log;
            using (ShotForm probe = new ShotForm(board, new Rectangle(0, 0, 400, 300)))
            {
                probe.Freeze(new Rectangle(20, 20, 360, 260));
                log = probe.RunSelfTest();
            }
            board.Dispose();
            bool failed = log.Exists(line => line.StartsWith("FAIL"));
            if (report != null) File.WriteAllLines(report, log.ToArray());
            return failed ? 1 : 0;
        }

        int themeIndex = Array.IndexOf(args, "--theme");
        if (themeIndex >= 0 && args.Length > themeIndex + 1 && args[themeIndex + 1] == "dark")
        {
            ShotForm.UseDarkTheme();
        }

        string output = args.Length > 0 ? args[0] : null;
        bool auto = args.Length > 3 && args[1] == "--auto";
        int autoWidth = 400;
        int autoHeight = 300;
        if (auto)
        {
            if (args.Length > 2) int.TryParse(args[2], out autoWidth);
            if (args.Length > 3) int.TryParse(args[3], out autoHeight);
            if (autoWidth <= 0) autoWidth = 400;
            if (autoHeight <= 0) autoHeight = 300;
        }
        bool demo = Array.IndexOf(args, "--demo") >= 0;

        Rectangle bounds = SystemInformation.VirtualScreen;
        Bitmap screen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(screen))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        ShotForm form = new ShotForm(screen, bounds);

        string uiShot = null;
        int uiIndex = Array.IndexOf(args, "--ui");
        if (uiIndex >= 0 && args.Length > uiIndex + 1) uiShot = args[uiIndex + 1];
        if (uiShot != null)
        {
            form.Freeze(new Rectangle(60, 60, 980, 470));
            int flyIndex = Array.IndexOf(args, "--flyout");
            if (flyIndex >= 0 && args.Length > flyIndex + 1) form.PreviewFlyout(args[flyIndex + 1]);
            form.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(200);
            Application.DoEvents();
            using (Bitmap frame = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(frame, new Rectangle(0, 0, form.Width, form.Height));
                Rectangle cropRect = Rectangle.Intersect(
                    new Rectangle(30, 30, 1040, 640),
                    new Rectangle(0, 0, frame.Width, frame.Height));
                if (cropRect.Width > 0 && cropRect.Height > 0)
                {
                    using (Bitmap crop = frame.Clone(cropRect, PixelFormat.Format32bppArgb))
                    {
                        crop.Save(uiShot, ImageFormat.Png);
                    }
                }
            }
            form.Dispose();
            screen.Dispose();
            return 0;
        }

        Bitmap result;
        if (auto)
        {
            form.Freeze(new Rectangle(100, 100, autoWidth, autoHeight));
            if (demo) form.AddDemoShapes();
            result = form.RenderResult();
            form.Dispose();
            screen.Dispose();
        }
        else
        {
            form.ShowDialog();
            if (form.SavedOnly)
            {
                form.Dispose();
                screen.Dispose();
                return 4;
            }
            if (!form.Completed || !form.HasSelection)
            {
                form.Dispose();
                screen.Dispose();
                return 2;
            }
            result = form.RenderResult();
            form.Dispose();
            screen.Dispose();
        }

        using (result)
        {
            if (output == null)
            {
                using (MemoryStream stdout = new MemoryStream())
                {
                    result.Save(stdout, ImageFormat.Png);
                    Console.Out.Write(Convert.ToBase64String(stdout.ToArray()));
                }
                return 0;
            }
            try
            {
                result.Save(output, ImageFormat.Png);
            }
            catch (Exception error)
            {
                Console.Error.Write(error.Message);
                return 3;
            }
        }
        return 0;
    }
}
