namespace WpfApp2;

public sealed record PlotViewport(double left, double right, double bottom, double top) {
  public double?[] sample(CompiledFormula formula) {
    return sample(formula, left, right);
  }

  public static double?[] sample(CompiledFormula formula, double a, double b) {
    var values = new double?[257];
    for (int i = 0; i < values.Length; i++) {
      double x = i == values.Length - 1 ? b : a + (b - a) * (i / (double)(values.Length - 1));
      double y = formula.evaluate(x);
      values[i] = double.IsFinite(y) ? y : null;
    }

    return values;
  }

  public static PlotViewport fit(SearchInput input, IReadOnlyList<NewtonStep> steps) {
    double margin = (input.b - input.a) / 4;
    double left = double.IsFinite(input.a - margin) ? input.a - margin : input.a;
    double right = double.IsFinite(input.b + margin) ? input.b + margin : input.b;
    if (!double.IsFinite(right - left)) {
      left = input.a;
      right = input.b;
    }

    double[] values = sample(input.formula, left, right).Where(v => v.HasValue)
        .Select(v => v!.Value).OrderBy(v => v).ToArray();
    if (values.Length == 0) {
      throw new ServiceException("Функция не определена на графике.");
    }

    double lower = Math.Min(0, values[(int)((values.Length - 1) * 0.05)]);
    double upper = Math.Max(0, values[(int)((values.Length - 1) * 0.95)]);
    foreach (var step in steps) {
      lower = Math.Min(lower, step.y);
      upper = Math.Max(upper, step.y);
    }

    double padding = upper > lower ? (upper - lower) * 0.1
        : Math.Max(Math.Abs(lower) * 0.1, 0.0001);
    if (!double.IsFinite(lower - padding) || !double.IsFinite(upper + padding)
        || !double.IsFinite(upper - lower + 2 * padding)) {
      throw new ServiceException("Слишком большие значения для графика.");
    }

    return new PlotViewport(left, right, lower - padding, upper + padding);
  }

  public PlotViewport zoom(double factor, double horizontal, double vertical) {
    double anchorX = left + (right - left) * Math.Clamp(horizontal, 0, 1);
    double anchorY = top - (top - bottom) * Math.Clamp(vertical, 0, 1);
    var next = new PlotViewport(anchorX + (left - anchorX) * factor,
        anchorX + (right - anchorX) * factor, anchorY + (bottom - anchorY) * factor,
        anchorY + (top - anchorY) * factor);
    return next.usable ? next : this;
  }

  public bool contains(PlotViewport other) => left <= other.left && right >= other.right
      && bottom <= other.bottom && top >= other.top;

  public PlotViewport pan(double horizontal, double vertical) {
    double dx = (right - left) * horizontal;
    double dy = (top - bottom) * vertical;
    var next = new PlotViewport(left + dx, right + dx, bottom + dy, top + dy);
    return next.usable ? next : this;
  }

  public bool usable {
    get {
      var next = this;
      double width = next.right - next.left;
      double height = next.top - next.bottom;
      if (!double.IsFinite(next.left) || !double.IsFinite(next.right)
          || !double.IsFinite(next.bottom) || !double.IsFinite(next.top)
          || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
          || next.left + width / 256 <= next.left || next.right - width / 256 >= next.right
          || next.bottom + height / 256 <= next.bottom || next.top - height / 256 >= next.top) {
        return false;
      }

      return true;
    }
  }
}
