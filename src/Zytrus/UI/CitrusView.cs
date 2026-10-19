using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Zytrus.Core;

namespace Zytrus.UI;

/// <summary>Colors of one fruit variant.</summary>
internal sealed record CitrusPalette(
    Color RimLight, Color Rim, Color RimEdge, Color Pore, Color Albedo, Color GlassTint,
    Color FrostInner, Color FrostOuter, Color Stem, Color Leaf, Color LeafDark, Color CoreText, Color CoreSubText)
{
    public static CitrusPalette For(CitrusVariety variety) => variety switch
    {
        CitrusVariety.Limette => new(
            C("#EAF7CF"), C("#BFE37F"), C("#8CC63F"), C("#30406010"), C("#FAFFF0"), C("#E9F7C9"),
            C("#C8F7FFEA"), C("#A0D6F0A8"), C("#6D7A2E"), C("#4CAF50"), C("#2E7D32"), C("#22400A"), C("#557040")),
        CitrusVariety.Orange => new(
            C("#FFDDA8"), C("#FFB54D"), C("#F08C1A"), C("#30A84700"), C("#FFF6E8"), C("#FFE2B8"),
            C("#C8FFF4E4"), C("#A0FFD08A"), C("#7A5A2E"), C("#5DBB4A"), C("#2F7D32"), C("#4A2A06"), C("#8A6236")),
        CitrusVariety.Grapefruit => new(
            C("#FFDCD5"), C("#FFAFA2"), C("#F07F6E"), C("#30A03020"), C("#FFF4F1"), C("#FFD9D2"),
            C("#C8FFF1EE"), C("#A0FFB8AC"), C("#7A5A2E"), C("#5DBB4A"), C("#2F7D32"), C("#4A1A12"), C("#8A5248")),
        _ => new( // Zitrone: light yellow rim
            C("#FFFBD1"), C("#FFF07A"), C("#F2D23C"), C("#30B89400"), C("#FFFDF0"), C("#FFF6B0"),
            C("#C8FFFBE0"), C("#A0FFF09A"), C("#7A7A2E"), C("#5DBB4A"), C("#2F7D32"), C("#3D3400"), C("#6E6230"))
    };

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>
/// Draws the citrus slice: light rim with stem, glass segments arranged in a circle and a glass core that
/// explains the hovered segment. With a <see cref="LiquidGlass.Result"/> the segments show the refracted,
/// frosted screen behind them; without one (e.g. while it is still being computed) they are plain frosted.
/// </summary>
internal sealed class CitrusView : FrameworkElement
{
    public sealed record Segment(ProcessAction Action, string Glyph, string Label, string Title, string Description, bool Enabled);

    public const int None = -1;
    public const int CenterHit = -2;

    // Proportions relative to the rim radius R (shared with the glass renderer)
    const double BoxFactor = 1.14, RimInner = 0.915, AlbedoInner = 0.875, CoreFactor = 0.30, SegmentInnerFactor = 0.306;

    static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface SubFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    List<Segment> _segments = new();
    CitrusPalette _palette = CitrusPalette.For(CitrusVariety.Zitrone);
    CitrusVariety _variety = CitrusVariety.Zitrone;
    bool _showLabels = true;
    string _idleTitle = "Auf ein Stück ziehen";
    string _idleSubtitle = "Mitte = abbrechen";
    int _hover = None;
    double[] _pop = Array.Empty<double>();
    bool _animating;
    LiquidGlass.Result? _glass;
    double _glassOpacity = 1;

    Drawing? _staticLayer;
    Size _staticSize;
    CitrusVariety _staticVariety;

    public CitrusView()
    {
        Unloaded += (_, _) => StopAnimating();
    }

    public CitrusPalette Palette => _palette;

    public int SegmentCount => _segments.Count;

    public void Configure(IEnumerable<Segment> segments, CitrusVariety variety, bool showLabels, string idleTitle, string idleSubtitle)
    {
        _segments = segments.ToList();
        if (_pop.Length != _segments.Count) _pop = new double[_segments.Count];
        _variety = variety;
        _palette = CitrusPalette.For(variety);
        _showLabels = showLabels;
        _idleTitle = idleTitle;
        _idleSubtitle = idleSubtitle;
        if (_hover >= _segments.Count) _hover = None;
        InvalidateVisual();
    }

    /// <summary>Greys out segments whose function doesn't fit the dragged files.</summary>
    public void SetAvailability(Func<ProcessAction, bool> isAvailable)
    {
        _segments = _segments.Select(s => s with { Enabled = isAvailable(s.Action) }).ToList();
        InvalidateVisual();
    }

    /// <summary>Sets (or clears) the rendered glass; fades it in.</summary>
    public void SetGlass(LiquidGlass.Result? glass)
    {
        bool fadeIn = glass != null && _glass == null;
        _glass = glass;
        _glassOpacity = fadeIn ? 0 : 1;
        StartAnimating();
        InvalidateVisual();
    }

    public bool IsSegmentEnabled(int index) => index >= 0 && index < _segments.Count && _segments[index].Enabled;

    public Segment? SegmentAt(int index) => index >= 0 && index < _segments.Count ? _segments[index] : null;

    public int Hover
    {
        get => _hover;
        set
        {
            if (_hover == value) return;
            _hover = value;
            StartAnimating();
            InvalidateVisual();
        }
    }

    // ---- geometry ------------------------------------------------------------------------------

    Point CenterPoint => new(ActualWidth / 2, ActualHeight / 2);

    double Radius => Math.Min(ActualWidth, ActualHeight) / 2 / BoxFactor;

    double Step => _segments.Count == 0 ? 360 : 360.0 / _segments.Count;

    /// <summary>The first segment sits centered at 12 o'clock, the rest follow clockwise.</summary>
    double StartAngle => -90 - Step / 2;

    /// <summary>Glass geometry for a backdrop of the given pixel size (same proportions as the drawing).</summary>
    public LiquidGlass.Shape ShapeFor(double pixelWidth, double pixelHeight)
    {
        double r = Math.Min(pixelWidth, pixelHeight) / 2 / BoxFactor;
        return new LiquidGlass.Shape(pixelWidth / 2, pixelHeight / 2, r * CoreFactor, r * SegmentInnerFactor, r * AlbedoInner,
            Math.Max(1, _segments.Count), StartAngle, r * 0.006);
    }

    public static LiquidGlass.Parameters GlassParameters(AppSettings s) =>
        new(s.GlassBlur, s.GlassRefraction, s.GlassTint, CitrusPalette.For(s.Variety).GlassTint);

    static Point Polar(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    /// <summary>Segment index under the point, <see cref="CenterHit"/> for the core, <see cref="None"/> outside.</summary>
    public int HitTest(Point p)
    {
        double r = Radius;
        if (r <= 0 || _segments.Count == 0) return None;
        var v = p - CenterPoint;
        double distance = v.Length;
        if (distance <= r * CoreFactor) return CenterHit;
        if (distance > r * 1.02) return None;

        double angle = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        double relative = ((angle - StartAngle) % 360 + 360) % 360;
        return Math.Min(_segments.Count - 1, (int)(relative / Step));
    }

    // ---- animation -----------------------------------------------------------------------------

    void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        bool moving = false;
        for (int i = 0; i < _pop.Length; i++)
        {
            double target = i == _hover && IsSegmentEnabled(i) ? 1 : 0;
            double next = _pop[i] + (target - _pop[i]) * 0.3;
            if (Math.Abs(next - target) < 0.01) next = target;
            if (next != _pop[i]) moving = true;
            _pop[i] = next;
        }
        if (_glassOpacity < 1)
        {
            _glassOpacity = Math.Min(1, _glassOpacity + 0.12);
            moving = true;
        }
        if (moving) InvalidateVisual();
        else StopAnimating();
    }

    // ---- drawing -------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        var c = CenterPoint;
        double r = Radius;
        double segmentOuter = r * AlbedoInner;
        double segmentInner = r * SegmentInnerFactor;
        double coreR = r * CoreFactor;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var p = _palette;
        var glass = _glass;
        var fullRect = new Rect(0, 0, w, h);

        if (_staticLayer == null || _staticSize != RenderSize || _staticVariety != _variety)
        {
            _staticLayer = BuildStaticLayer(c, r);
            _staticSize = RenderSize;
            _staticVariety = _variety;
        }
        dc.DrawDrawing(_staticLayer);

        var membrane = new Pen(new SolidColorBrush(p.Albedo), r * 0.022) { LineJoin = PenLineJoin.Round };
        membrane.Freeze();

        double step = Step;
        var order = Enumerable.Range(0, _segments.Count).OrderBy(i => i < _pop.Length ? _pop[i] : 0).ToList();

        foreach (int i in order)
        {
            var seg = _segments[i];
            double pop = i < _pop.Length ? _pop[i] : 0;
            double a0 = StartAngle + i * step, a1 = a0 + step, mid = (a0 + a1) / 2;
            var center = Polar(c, pop * r * 0.05, mid);
            double outer = segmentOuter + pop * r * 0.012;
            var geometry = Sector(center, segmentInner, outer, a0, a1);

            // Frosted base (visible alone until the glass is ready, and under it while it fades in)
            if (glass == null || _glassOpacity < 1) dc.DrawGeometry(FrostBrush(center, outer, coreR), null, geometry);

            if (glass != null)
            {
                dc.PushClip(geometry);
                dc.PushTransform(new TranslateTransform(center.X - c.X, center.Y - c.Y));
                dc.PushOpacity(_glassOpacity);
                dc.DrawImage(glass.Image, fullRect);
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }

            if (!seg.Enabled) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 110, 110, 110)), null, geometry);
            if (pop > 0.01) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(55 * pop), 255, 255, 255)), null, geometry);

            // Specular rim light (top left), like the bright edge of liquid glass
            var specular = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            specular.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(170 + 60 * pop), 255, 255, 255), 0));
            specular.GradientStops.Add(new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.55));
            specular.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(90 + 40 * pop), 255, 255, 255), 1));
            var rimPen = new Pen(specular, r * (0.009 + 0.005 * pop)) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, rimPen, Sector(center, segmentInner + r * 0.016, outer - r * 0.016, a0 + 1.6, a1 - 1.6));

            dc.DrawGeometry(null, membrane, geometry);

            double luma = glass != null && i < glass.SegmentLuma.Length ? glass.SegmentLuma[i] * _glassOpacity + 0.85 * (1 - _glassOpacity) : 0.85;
            DrawLabel(dc, seg, Polar(center, segmentInner + (outer - segmentInner) * 0.56, mid), r, dpi, luma + (pop > 0.5 ? 0.1 : 0));
        }

        DrawCore(dc, c, coreR, r, dpi, glass, fullRect);
    }

    Brush FrostBrush(Point center, double outer, double coreR)
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = center,
            GradientOrigin = center,
            RadiusX = outer,
            RadiusY = outer
        };
        brush.GradientStops.Add(new GradientStop(_palette.FrostInner, coreR / outer));
        brush.GradientStops.Add(new GradientStop(_palette.FrostOuter, 1));
        brush.Freeze();
        return brush;
    }

    void DrawLabel(DrawingContext dc, Segment seg, Point at, double r, double dpi, double backgroundLuma)
    {
        bool darkText = backgroundLuma > 0.62;
        double opacity = seg.Enabled ? 1 : 0.45;
        var brush = new SolidColorBrush(darkText ? Color.FromRgb(0x2B, 0x27, 0x05) : Colors.White) { Opacity = opacity };
        var shadow = new SolidColorBrush(darkText ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0)) { Opacity = opacity };
        brush.Freeze();
        shadow.Freeze();
        double shadowOffset = Math.Max(0.6, r * 0.006);

        var icon = new FormattedText(seg.Glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, IconFace,
            _showLabels ? r * 0.115 : r * 0.15, brush, dpi);
        FormattedText? label = null;
        if (_showLabels)
        {
            label = new FormattedText(seg.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, r * 0.062, brush, dpi)
            {
                MaxTextWidth = r * 0.6,
                TextAlignment = TextAlignment.Center,
                LineHeight = r * 0.072
            };
        }

        double gap = r * 0.015;
        double total = icon.Height + (label != null ? gap + label.Height : 0);
        double y = at.Y - total / 2;

        var iconOrigin = new Point(at.X - icon.Width / 2, y);
        icon.SetForegroundBrush(shadow);
        dc.DrawText(icon, new Point(iconOrigin.X, iconOrigin.Y + shadowOffset));
        icon.SetForegroundBrush(brush);
        dc.DrawText(icon, iconOrigin);

        if (label != null)
        {
            var labelOrigin = new Point(at.X - label.MaxTextWidth / 2, y + icon.Height + gap);
            label.SetForegroundBrush(shadow);
            dc.DrawText(label, new Point(labelOrigin.X, labelOrigin.Y + shadowOffset));
            label.SetForegroundBrush(brush);
            dc.DrawText(label, labelOrigin);
        }
    }

    void DrawCore(DrawingContext dc, Point c, double coreR, double r, double dpi, LiquidGlass.Result? glass, Rect fullRect)
    {
        var p = _palette;
        var core = new EllipseGeometry(c, coreR, coreR);

        if (glass == null || _glassOpacity < 1) dc.DrawGeometry(FrostBrush(c, coreR, coreR * 0.2), null, core);
        if (glass != null)
        {
            dc.PushClip(core);
            dc.PushOpacity(_glassOpacity);
            dc.DrawImage(glass.Image, fullRect);
            dc.Pop();
            dc.Pop();
        }
        // Extra frosting in the middle keeps the explanation readable on any background.
        var frost = new RadialGradientBrush(Color.FromArgb(175, 255, 255, 255), Color.FromArgb(120, p.GlassTint.R, p.GlassTint.G, p.GlassTint.B));
        frost.Freeze();
        var ring = new Pen(new SolidColorBrush(p.Albedo), Math.Max(1, r * 0.014));
        ring.Freeze();
        dc.DrawGeometry(frost, ring, core);

        string title, subtitle;
        var hovered = SegmentAt(_hover);
        if (hovered != null)
        {
            title = hovered.Title;
            subtitle = hovered.Enabled ? hovered.Description : "passt nicht zu diesen Dateien";
        }
        else
        {
            title = _idleTitle;
            subtitle = _idleSubtitle;
        }

        double width = coreR * 1.7;
        var titleText = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, r * 0.07,
            new SolidColorBrush(p.CoreText), dpi)
        {
            MaxTextWidth = width,
            MaxLineCount = 2,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var subText = new FormattedText(subtitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, SubFace, r * 0.055,
            new SolidColorBrush(p.CoreSubText), dpi)
        {
            MaxTextWidth = width,
            MaxLineCount = 3,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };

        double gap = r * 0.015;
        double total = titleText.Height + gap + subText.Height;
        double y = c.Y - total / 2;
        dc.DrawText(titleText, new Point(c.X - width / 2, y));
        dc.DrawText(subText, new Point(c.X - width / 2, y + titleText.Height + gap));
    }

    /// <summary>Rim, pith ring, pores, stem and leaf – everything that doesn't change while hovering.</summary>
    Drawing BuildStaticLayer(Point c, double r)
    {
        var p = _palette;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Soft shadow ring around the fruit (the inside is covered by glass)
            var shadow = new RadialGradientBrush();
            shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.80));
            shadow.GradientStops.Add(new GradientStop(Color.FromArgb(55, 0, 0, 0), 0.88));
            shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
            dc.DrawEllipse(shadow, null, new Point(c.X, c.Y + r * 0.04), r * 1.1, r * 1.1);

            // Light rim (peel) as a thick ring, lit from the top left
            var rim = new LinearGradientBrush { StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1) };
            rim.GradientStops.Add(new GradientStop(p.RimLight, 0));
            rim.GradientStops.Add(new GradientStop(p.Rim, 0.5));
            rim.GradientStops.Add(new GradientStop(p.RimEdge, 1));
            double rimWidth = r * (1 - RimInner);
            dc.DrawEllipse(null, new Pen(rim, rimWidth), c, r - rimWidth / 2, r - rimWidth / 2);

            // Pores in the rim
            var poreBrush = new SolidColorBrush(p.Pore);
            var rng = new Random(11);
            for (int i = 0; i < 150; i++)
            {
                double angle = rng.NextDouble() * 360;
                double distance = r * (RimInner + 0.012 + rng.NextDouble() * 0.06);
                double size = r * (0.004 + rng.NextDouble() * 0.005);
                dc.DrawEllipse(poreBrush, null, Polar(c, distance, angle), size, size);
            }

            // Glossy highlight on the rim
            var gloss = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), r * 0.012) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(Polar(c, r * 0.965, 200), false, false);
                ctx.ArcTo(Polar(c, r * 0.965, 250), new Size(r * 0.965, r * 0.965), 0, false, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, gloss, arc);

            // White pith ring between rim and segments
            double albedoWidth = r * (RimInner - AlbedoInner);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(p.Albedo), albedoWidth), c, r * AlbedoInner + albedoWidth / 2, r * AlbedoInner + albedoWidth / 2);

            DrawStemAndLeaf(dc, c, r);
        }
        group.Freeze();
        return group;
    }

    void DrawStemAndLeaf(DrawingContext dc, Point c, double r)
    {
        var p = _palette;
        var top = new Point(c.X, c.Y - r * 0.985);

        // Stem: a short, slightly bent stalk with a darker tip
        var stem = new StreamGeometry();
        using (var ctx = stem.Open())
        {
            ctx.BeginFigure(new Point(top.X - r * 0.025, top.Y + r * 0.03), true, true);
            ctx.BezierTo(new Point(top.X - r * 0.03, top.Y - r * 0.05), new Point(top.X - r * 0.01, top.Y - r * 0.10), new Point(top.X + r * 0.02, top.Y - r * 0.12), true, true);
            ctx.LineTo(new Point(top.X + r * 0.045, top.Y - r * 0.10), true, true);
            ctx.BezierTo(new Point(top.X + r * 0.02, top.Y - r * 0.08), new Point(top.X + r * 0.02, top.Y - r * 0.03), new Point(top.X + r * 0.025, top.Y + r * 0.03), true, true);
        }
        stem.Freeze();
        var stemFill = new LinearGradientBrush(Lighten(p.Stem, 0.25), p.Stem, new Point(0, 0), new Point(1, 0));
        dc.DrawGeometry(stemFill, new Pen(new SolidColorBrush(Darken(p.Stem, 0.3)), r * 0.006), stem);

        // Leaf growing to the right of the stem
        var stemBase = new Point(top.X + r * 0.03, top.Y - r * 0.06);
        var tip = new Point(stemBase.X + r * 0.36, stemBase.Y - r * 0.06);
        var leaf = new StreamGeometry();
        using (var ctx = leaf.Open())
        {
            ctx.BeginFigure(stemBase, true, true);
            ctx.BezierTo(new Point(stemBase.X + r * 0.08, stemBase.Y - r * 0.10),
                         new Point(tip.X - r * 0.12, tip.Y - r * 0.04), tip, true, true);
            ctx.BezierTo(new Point(tip.X - r * 0.07, tip.Y + r * 0.08),
                         new Point(stemBase.X + r * 0.13, stemBase.Y + r * 0.05), stemBase, true, true);
        }
        leaf.Freeze();
        var leafFill = new LinearGradientBrush(p.Leaf, p.LeafDark, new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(leafFill, new Pen(new SolidColorBrush(p.LeafDark), r * 0.005), leaf);
        var vein = new Pen(new SolidColorBrush(Color.FromArgb(150, p.LeafDark.R, p.LeafDark.G, p.LeafDark.B)), r * 0.007) { EndLineCap = PenLineCap.Round };
        dc.DrawLine(vein, stemBase, new Point(stemBase.X + (tip.X - stemBase.X) * 0.85, stemBase.Y + (tip.Y - stemBase.Y) * 0.85));
    }

    static Geometry Sector(Point center, double inner, double outer, double a0, double a1)
    {
        bool large = a1 - a0 > 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(center, inner, a0), true, true);
            ctx.LineTo(Polar(center, outer, a0), true, true);
            ctx.ArcTo(Polar(center, outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Polar(center, inner, a1), true, true);
            ctx.ArcTo(Polar(center, inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    static Color Lighten(Color c, double t) =>
        Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    static Color Darken(Color c, double t) =>
        Color.FromArgb(c.A, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
