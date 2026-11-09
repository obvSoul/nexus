using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.App.ViewModels;
using Nexus.Core;
using Nexus.Core.Formatting;
using Nexus.Storage;

namespace Nexus.App.Controls;

/// <summary>
/// Draws a two-level squarified treemap with one DrawingContext pass (no per-rectangle elements, so hundreds of
/// rectangles stay cheap). Click a folder to enter it; hover shows details.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private sealed record Placed(Rect Rect, TreemapNode Node, TreemapNode? Parent, bool Header);

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(TreemapNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d)._placed = null));

    public TreemapNode? Root { get => (TreemapNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }

    /// <summary>Node under the mouse (also valid when the context menu opens).</summary>
    public TreemapNode? HoveredNode { get; private set; }

    public event Action<TreemapNode>? FolderActivated;

    private List<Placed>? _placed;
    private Size _placedFor;
    private Placed? _hover;

    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x09)), null, new Rect(size));
        if (Root is null || size.Width < 10 || size.Height < 10) return;
        if (_placed is null || _placedFor != size) { _placed = Layout(Root, size); _placedFor = size; }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var p in _placed)
        {
            if (p.Header) // folder title strip
            {
                Text(dc, p.Node.Name, p.Rect, dpi, Brushes.White, 11, bold: true);
                continue;
            }
            dc.DrawRectangle(BrushFor(p.Node, p.Parent), BorderPen, p.Rect);
            if (p.Parent is null || !p.Node.IsDir) Text(dc, Label(p.Node), p.Rect, dpi, Brushes.White, 11, bold: false);
        }
        if (_hover is { Header: false } h)
            dc.DrawRectangle(null, new Pen(Brushes.White, 2), Rect.Inflate(h.Rect, -1, -1));
    }

    private static readonly Pen BorderPen = MakePen();
    private static Pen MakePen() { var p = new Pen(new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x09)), 1); p.Freeze(); return p; }

    private static string Label(TreemapNode n) => n.IsOther ? n.Name : $"{n.Name}\n{ByteSize.Format(n.Size)}";

    private static void Text(DrawingContext dc, string text, Rect r, double dpi, Brush brush, double fontSize, bool bold)
    {
        if (r.Width < 44 || r.Height < 16) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? new Typeface(Face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : Face, fontSize, brush, dpi)
        {
            MaxTextWidth = Math.Max(1, r.Width - 8),
            MaxTextHeight = Math.Max(1, r.Height - 4),
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(r.X + 4, r.Y + 2));
    }

    // ---- layout ----

    private static List<Placed> Layout(TreemapNode root, Size size)
    {
        var result = new List<Placed>();
        PlaceChildren(root, null, new RectD(0, 0, size.Width, size.Height), result, depth: 0);
        return result;
    }

    private static void PlaceChildren(TreemapNode node, TreemapNode? parent, RectD bounds, List<Placed> into, int depth)
    {
        var kids = node.Children;
        if (kids.Count == 0) return;
        var rects = TreemapLayout.Squarify(kids.Select(k => (double)k.Size).ToList(), bounds);
        for (int i = 0; i < kids.Count; i++)
        {
            var r = rects[i];
            if (r.W < 1 || r.H < 1) continue;
            var rect = new Rect(r.X, r.Y, r.W, r.H);
            var kid = kids[i];
            into.Add(new Placed(rect, kid, depth == 0 ? null : node, false));
            if (depth == 0 && kid.IsDir && kid.Children.Count > 0 && r.W > 46 && r.H > 40)
            {
                into.Add(new Placed(new Rect(rect.X, rect.Y, rect.Width, 18), kid, null, true));
                var inner = new RectD(r.X + 2, r.Y + 18, r.W - 4, r.H - 20);
                PlaceChildren(kid, node, inner, into, depth + 1);
            }
        }
    }

    // ---- colors ----

    private static readonly Dictionary<string, Brush> Cache = new();

    private static Brush BrushFor(TreemapNode n, TreemapNode? parent)
    {
        string key = n.IsOther ? "other" : n.IsDir ? "dir:" + (parent is null ? n.Name : parent.Name) : "cat:" + FileTypes.Of(n.Extension);
        if (Cache.TryGetValue(key, out var b)) return b;
        Color c;
        if (n.IsOther) c = Color.FromRgb(0x4A, 0x4A, 0x50);
        else if (n.IsDir) c = FromHue(StableHue(parent is null ? n.Name : parent.Name), 0.35, parent is null ? 0.30 : 0.38);
        else c = FileTypes.Of(n.Extension) switch
        {
            FileCategory.Image => Color.FromRgb(0x8E, 0x5B, 0xC4),
            FileCategory.Video => Color.FromRgb(0xC4, 0x5B, 0x5B),
            FileCategory.Audio => Color.FromRgb(0xC4, 0x8A, 0x3B),
            FileCategory.Document => Color.FromRgb(0x3B, 0x7F, 0xC4),
            FileCategory.Archive => Color.FromRgb(0xB5, 0xA8, 0x3B),
            FileCategory.Code => Color.FromRgb(0x4C, 0xA8, 0x6A),
            FileCategory.Executable => Color.FromRgb(0x3B, 0xA8, 0xA8),
            _ => Color.FromRgb(0x6B, 0x6B, 0x75),
        };
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return Cache[key] = brush;
    }

    /// <summary>Same folder name, same colour at every launch (string.GetHashCode is randomized per process).</summary>
    private static double StableHue(string name)
    {
        uint hash = 2166136261;
        foreach (char ch in name.ToUpperInvariant()) hash = (hash ^ ch) * 16777619;
        return hash % 360;
    }

    private static Color FromHue(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double T(double t) { t = (t % 1 + 1) % 1; return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p; }
        return Color.FromRgb((byte)(T(h / 360 + 1.0 / 3) * 255), (byte)(T(h / 360) * 255), (byte)(T(h / 360 - 1.0 / 3) * 255));
    }

    // ---- interaction ----

    private Placed? HitTest(Point pt)
    {
        if (_placed is null) return null;
        for (int i = _placed.Count - 1; i >= 0; i--)
            if (_placed[i].Rect.Contains(pt)) return _placed[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        HoveredNode = hit?.Node;
        if (hit is null) { ToolTip = null; }
        else
        {
            var n = hit.Node;
            long parentSize = hit.Parent?.Size ?? Root?.Size ?? 0;
            string pct = parentSize > 0 ? $" · {100.0 * n.Size / parentSize:0.#} % du dossier parent" : "";
            ToolTip = $"{n.Name}\n{ByteSize.Format(n.Size)}{pct}" + (n.IsDir && !n.IsOther ? "\nClic : ouvrir ce dossier" : "");
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null; HoveredNode = null; ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit?.Node is { IsDir: true, IsOther: false } n) FolderActivated?.Invoke(n);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        _hover = hit; HoveredNode = hit?.Node;
        base.OnMouseRightButtonDown(e);
    }
}
