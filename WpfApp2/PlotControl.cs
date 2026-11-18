using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpfApp2;

public sealed class PlotControl : FrameworkElement {
  private static readonly Brush gray = new SolidColorBrush(Color.FromRgb(107, 114, 128));
  private static readonly Brush blue = new SolidColorBrush(Color.FromRgb(37, 99, 235));
  private static readonly Brush purple = new SolidColorBrush(Color.FromRgb(139, 92, 246));
  private static readonly Brush red = new SolidColorBrush(Color.FromRgb(220, 38, 38));
  private static readonly Pen gridPen = pen(new SolidColorBrush(Color.FromRgb(229, 231, 235)), 1);
  private static readonly Pen axisPen = pen(gray, 1);
  private static readonly Pen projectionPen = pen(gray, 1, true);
  private static readonly Pen tangentPen = pen(red, 1.5, true);
  private static readonly Pen functionTangentPen = pen(Brushes.SeaGreen, 1.5, true);
  private static readonly Pen boundaryPen = pen(Brushes.Orange, 1, true);
  private Point? dragStart;
  private PlotViewport? dragView;
  private ChartFrame? frame;
  private IReadOnlyList<NewtonStep> steps = Array.Empty<NewtonStep>();
  private int selected = -1;
  public SearchInput? input {
    get;
    private set;
  }
  public PlotViewport? view {
    get;
    private set;
  }
  public event EventHandler? viewChanged;
  public bool isDragging => dragStart.HasValue;
  public Rect plotArea => new(86, 38, Math.Max(1, ActualWidth - 108),
      Math.Max(1, ActualHeight - 108));

  public PlotControl() {
    Focusable = true;
    ClipToBounds = true;
    Cursor = Cursors.Hand;
    RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    SizeChanged += (_, _) => {
      InvalidateVisual();
      viewChanged?.Invoke(this, EventArgs.Empty);
    };
  }

  public void setSearch(SearchInput search, IReadOnlyList<NewtonStep> history) {
    clear();
    input = search;
    steps = history;
    view = PlotViewport.fit(search, history);
    InvalidateVisual();
  }

  public void showStep(int index) {
    selected = index;
    InvalidateVisual();
  }

  public void setFrame(ChartFrame next) {
    frame = next;
    InvalidateVisual();
  }

  public void clear() {
    ReleaseMouseCapture();
    dragStart = null;
    dragView = null;
    Cursor = Cursors.Hand;
    input = null;
    view = null;
    frame = null;
    steps = Array.Empty<NewtonStep>();
    selected = -1;
    InvalidateVisual();
  }

  public void zoom(double factor, double horizontal = 0.5, double vertical = 0.5) {
    if (view is not null) {
      changeView(view.zoom(factor, horizontal, vertical));
    }
  }

  private void changeView(PlotViewport next) {
    if (next == view) {
      return;
    }
    view = next;
    InvalidateVisual();
    viewChanged?.Invoke(this, EventArgs.Empty);
  }

  protected override void OnMouseWheel(MouseWheelEventArgs e) {
    base.OnMouseWheel(e);
    Point p = e.GetPosition(this);
    Rect area = plotArea;
    if (view is null || !area.Contains(p)) {
      return;
    }
    zoom(Math.Pow(1.25, -e.Delta / 120d), (p.X - area.Left) / area.Width,
            (p.Y - area.Top) / area.Height);
    e.Handled = true;
  }

  protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
    base.OnMouseLeftButtonDown(e);
    Focus();
    Point p = e.GetPosition(this);
    if (view is null || !plotArea.Contains(p)) {
      return;
    }
    dragStart = p;
    dragView = view;
    CaptureMouse();
    Cursor = Cursors.ScrollAll;
    e.Handled = true;
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    if (dragStart is not Point start || dragView is null) {
      return;
    }
    if (e.LeftButton != MouseButtonState.Pressed) {
      ReleaseMouseCapture();
      return;
    }
    Point p = e.GetPosition(this);
    changeView(dragView.pan((start.X - p.X) / plotArea.Width,
        (p.Y - start.Y) / plotArea.Height));
    e.Handled = true;
  }

  protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
    base.OnMouseLeftButtonUp(e);
    if (!isDragging) {
      return;
    }
    ReleaseMouseCapture();
    e.Handled = true;
  }

  protected override void OnLostMouseCapture(MouseEventArgs e) {
    base.OnLostMouseCapture(e);
    dragStart = null;
    dragView = null;
    Cursor = Cursors.Hand;
    viewChanged?.Invoke(this, EventArgs.Empty);
  }

  protected override void OnRender(DrawingContext dc) {
    base.OnRender(dc);
    dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
    if (view is null || input is null) {
      return;
    }
    Rect area = plotArea;
    var labels = new List<Rect>();
    dc.PushClip(new RectangleGeometry(area));
    double a = Math.Max(input.a, view.left), b = Math.Min(input.b, view.right);
    if (a < b) {
      dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 249, 252)),
        null, new Rect(point(a, view.top), point(b, view.bottom)));
    }
    drawGrid(dc, labels, false);
    drawCurves(dc);
    line(dc, axisPen, view.left, 0, view.right, 0);
    line(dc, axisPen, 0, view.bottom, 0, view.top);
    foreach (double boundary in new[] { input.a, input.b }) {
      line(dc, boundaryPen, boundary, view.bottom, boundary, view.top);
    }
    for (int i = 0; i < selected && i < steps.Count; i++) {
      dot(dc, steps[i].x, steps[i].y, gray, 3);
    }
    if (selected >= 0 && selected < steps.Count) {
      int transition = Math.Min(selected, steps.Count - 2);
      if (transition >= 0) {
        var p = steps[transition];
        var next = steps[transition + 1];
        var d = input.formula.derivatives(p.x);
        double y = input.mode == SearchMode.ROOT ? d.value : d.first;
        double slope = input.mode == SearchMode.ROOT ? d.first : d.second;
        if (double.IsFinite(y)) {
          line(dc, projectionPen, p.x, 0, p.x, y);
          if (double.IsFinite(slope)) {
            line(dc, tangentPen, view.left, y + slope * (view.left - p.x),
                view.right, y + slope * (view.right - p.x));
          }
          dot(dc, p.x, y, red, 4);
          dot(dc, next.x, 0, red, 3);
          if (input.mode != SearchMode.ROOT) {
            line(dc, projectionPen, next.x, 0, next.x, next.y);
          }
        }
      }
      var current = steps[selected];
      if (input.mode != SearchMode.ROOT) {
        double slope = input.formula.derivatives(current.x).first;
        if (double.IsFinite(slope)) {
          line(dc, functionTangentPen, view.left, current.y + slope * (view.left - current.x),
              view.right, current.y + slope * (view.right - current.x));
        }
      }
      dot(dc, current.x, current.y, red, 5);
    }
    dc.Pop();
    dc.DrawRectangle(null, gridPen, area);
    drawGrid(dc, labels, true);
    if (selected >= 0 && selected < steps.Count) {
      int transition = Math.Min(selected, steps.Count - 2);
      if (transition >= 0) {
        double x1 = steps[transition].x, x2 = steps[transition + 1].x;
        addLabel(dc, labels, $"x{indexText(transition)}", point(x1, view.bottom).X,
            area.Bottom + 29, red);
        addLabel(dc, labels, $"x{indexText(transition + 1)}", point(x2, view.bottom).X,
            area.Bottom + 29, red);
      }
    }
    drawText(dc, "x", new Point(area.Right + 8, area.Bottom - 8), gray);
    drawText(dc, "y", new Point(area.Left - 16, area.Top - 23), gray);
    drawText(dc, "f(x)", new Point(area.Left + 8, 8), blue);
    if (input.mode != SearchMode.ROOT) {
      drawText(dc, "f′(x)", new Point(area.Left + 60, 8), purple);
    }
    if (steps.Count > 1) {
      drawText(dc, "шаг Ньютона", new Point(area.Left + 120, 8), red);
    }
    if (input.mode != SearchMode.ROOT && selected >= 0) {
      drawText(dc, "касательная к f", new Point(area.Left + 240, 8), Brushes.SeaGreen);
    }
  }

  private void drawCurves(DrawingContext dc) {
    if (frame is null || view is null) {
      return;
    }
    var bounds = frame.view;
    double l = Math.Max(view.left, bounds.left), r = Math.Min(view.right, bounds.right);
    double b = Math.Max(view.bottom, bounds.bottom), t = Math.Min(view.top, bounds.top);
    if (l >= r || b >= t) {
      return;
    }
    Rect area = plotArea;
    double sx = (bounds.right - bounds.left) / (view.right - view.left) * area.Width / frame.width;
    double sy = (bounds.top - bounds.bottom) / (view.top - view.bottom)
        * area.Height / frame.height;
    var matrix = new Matrix(sx, 0, 0, sy,
        area.Left + (bounds.left - view.left) / (view.right - view.left) * area.Width,
        area.Top + (view.top - bounds.top) / (view.top - view.bottom) * area.Height);
    if (!double.IsFinite(sx) || !double.IsFinite(sy)
        || !double.IsFinite(matrix.OffsetX) || !double.IsFinite(matrix.OffsetY)) {
      return;
    }
    dc.PushClip(new RectangleGeometry(new Rect(point(l, t), point(r, b))));
    foreach (var curve in frame.curves) {
      var geometry = curve.geometry.Clone();
      var transform = geometry.Transform.Value;
      transform.Append(matrix);
      geometry.Transform = new MatrixTransform(transform);
      // Transform the path, not the pen: its width stays in screen units at every zoom.
      dc.DrawGeometry(null, pen(curve.derivative ? purple : blue,
          curve.derivative ? 2.5 : 2, curve.derivative), geometry);
    }
    dc.Pop();
  }

  private void drawGrid(DrawingContext dc, List<Rect> labels, bool text) {
    if (view is null) {
      return;
    }
    Rect area = plotArea;
    foreach (double x in ticks(view.left, view.right, area.Width / 110)) {
      Point p = point(x, view.bottom);
      if (text) {
        addLabel(dc, labels, number(x, view.right - view.left), p.X, area.Bottom + 5, gray);
      } else {
        line(dc, gridPen, x, view.bottom, x, view.top);
      }
    }
    foreach (double y in ticks(view.bottom, view.top, area.Height / 48)) {
      Point p = point(view.left, y);
      if (text) {
        var label = formatted(number(y, view.top - view.bottom), gray);
        var box = new Rect(area.Left - label.Width - 9, p.Y - label.Height / 2,
            label.Width, label.Height);
        if (box.Left >= 0 && !labels.Any(r => r.IntersectsWith(box))) {
          dc.DrawText(label, box.TopLeft);
          labels.Add(box);
        }
      } else {
        line(dc, gridPen, view.left, y, view.right, y);
      }
    }
  }

  private void addLabel(DrawingContext dc, List<Rect> labels, string value,
      double x, double y, Brush color) {
    if (!double.IsFinite(x) || x < plotArea.Left - 1 || x > plotArea.Right + 1) {
      return;
    }
    var text = formatted(value, color);
    double left = Math.Clamp(x - text.Width / 2, plotArea.Left, Math.Max(plotArea.Left,
        plotArea.Right - text.Width));
    var rect = new Rect(left - 3, y, text.Width + 6, text.Height + 3);
    if (labels.Any(r => r.IntersectsWith(rect))) {
      return;
    }
    dc.DrawText(text, new Point(left, y));
    labels.Add(rect);
  }

  private Point point(double x, double y) => new(
      plotArea.Left + (x - view!.left) / (view.right - view.left) * plotArea.Width,
      plotArea.Top + (view.top - y) / (view.top - view.bottom) * plotArea.Height);

  private void dot(DrawingContext dc, double x, double y, Brush color, double size) {
    Point p = point(x, y);
    if (double.IsFinite(p.X) && double.IsFinite(p.Y) && plotArea.Contains(p)) {
      dc.DrawEllipse(color, null, p, size, size);
    }
  }

  private void line(DrawingContext dc, Pen style, double x1, double y1,
      double x2, double y2) {
    Point a = point(x1, y1), b = point(x2, y2);
    if (!double.IsFinite(a.X) || !double.IsFinite(a.Y)
        || !double.IsFinite(b.X) || !double.IsFinite(b.Y)) {
      return;
    }
    // Clip before sending coordinates to WPF, including lines far outside the viewport.
    double dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
    if (!double.IsFinite(dx) || !double.IsFinite(dy)) {
      return;
    }
    bool clip(double p, double q) {
      if (p == 0) {
        return q >= 0;
      }
      double t = q / p;
      if (p < 0) {
        lo = Math.Max(lo, t);
      } else {
        hi = Math.Min(hi, t);
      }
      return lo <= hi;
    }
    Rect area = plotArea;
    if (clip(-dx, a.X - area.Left) && clip(dx, area.Right - a.X)
        && clip(-dy, a.Y - area.Top) && clip(dy, area.Bottom - a.Y)) {
      dc.DrawLine(style, new Point(a.X + lo * dx, a.Y + lo * dy),
        new Point(a.X + hi * dx, a.Y + hi * dy));
    }
  }

  private FormattedText formatted(string text, Brush color) => new(text,
      CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
      12, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);

  private void drawText(DrawingContext dc, string text, Point p, Brush color) =>
      dc.DrawText(formatted(text, color), p);

  private static string number(double x, double span) {
    int digits = (int)Math.Clamp(Math.Ceiling(Math.Log10(Math.Max(Math.Abs(x), span))
        - Math.Log10(span)) + 3, 4, 17);
    return (x == 0 ? 0d : x).ToString("G" + digits, CultureInfo.CurrentCulture);
  }

  private static string indexText(int index) => string.Concat(index.ToString(
      CultureInfo.InvariantCulture).Select(c => "₀₁₂₃₄₅₆₇₈₉"[c - '0']));

  private static IEnumerable<double> ticks(double min, double max, double count) {
    double raw = (max - min) / Math.Max(1, count);
    double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
    if (!double.IsFinite(power) || power == 0) {
      yield break;
    }
    double step = new[] { 1d, 2d, 5d, 10d }.Select(n => n * power).FirstOrDefault(n => n >= raw);
    if (!double.IsFinite(step) || step <= 0) {
      yield break;
    }
    double start = Math.Ceiling(min / step) * step;
    for (int i = 0; i < 100; i++) {
      double value = start + i * step;
      if (!double.IsFinite(value) || value > max) {
        yield break;
      }
      if (value >= min) {
        yield return value;
      }
    }
  }

  private static Pen pen(Brush color, double width, bool dash = false) {
    var result = new Pen(color, width);
    if (dash) {
      result.DashStyle = DashStyles.Dash;
    }
    result.Freeze();
    return result;
  }
}
