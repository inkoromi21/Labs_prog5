namespace WpfApp2;

public readonly record struct Interval(double lower, double upper) {
  public static Interval invalid => new(double.NaN, double.NaN);
  public static Interval pi => new(Math.BitDecrement(Math.PI), Math.BitIncrement(Math.PI));
  public static Interval ln10 => new(2.302585092994045, 2.302585092994046);
  private static Interval ln2 => new(0.6931471805599452, 0.6931471805599454);
  public bool valid => double.IsFinite(lower) && double.IsFinite(upper) && lower <= upper;
  public bool containsZero => valid && lower <= 0 && upper >= 0;
  public double width => up(upper - lower);

  public static Interval point(double x) => new(x, x);

  public static Interval literal(double x) =>
      x == Math.Truncate(x) && Math.Abs(x) <= 9007199254740992d
      ? point(x) : new(down(x), up(x));

  private static double down(double x) => Math.BitDecrement(x);

  private static double up(double x) => Math.BitIncrement(x);

  public static Interval operator -(Interval a) => a.valid ? new(-a.upper, -a.lower) : invalid;

  public static Interval operator +(Interval a, Interval b) {
    if (!a.valid || !b.valid) {
      return invalid;
    }
    if (a == point(0)) {
      return b;
    }
    if (b == point(0)) {
      return a;
    }
    double sum = a.lower + b.lower;
    if (integerPoint(a) && integerPoint(b) && Math.Abs(sum) < 9007199254740992d) {
      return point(sum);
    }
    return new(sumBound(a.lower, b.lower, false), sumBound(a.upper, b.upper, true));
  }

  private static double sumBound(double a, double b, bool upper) {
    double sum = a + b;
    if (!double.IsFinite(sum)) {
      return sum;
    }
    double shifted = sum - a;
    double error = (a - (sum - shifted)) + (b - shifted);
    return upper ? error > 0 ? up(sum) : sum : error < 0 ? down(sum) : sum;
  }

  public static Interval operator -(Interval a, Interval b) => a.valid && b.valid
      && a.lower == a.upper && a == b ? point(0) : a + -b;

  public static Interval operator *(Interval a, Interval b) {
    if (!a.valid || !b.valid) {
      return invalid;
    }
    if (a == point(0) || b == point(0)) {
      return point(0);
    }
    if (a == point(1)) {
      return b;
    }
    if (b == point(1)) {
      return a;
    }
    double product = a.lower * b.lower;
    if (integerPoint(a) && integerPoint(b) && Math.Abs(product) < 9007199254740992d) {
      return point(product);
    }
    double[] ends = [a.lower * b.lower, a.lower * b.upper, a.upper * b.lower, a.upper * b.upper];
    return new(down(ends.Min()), up(ends.Max()));
  }

  public static Interval operator /(Interval a, Interval b) =>
      !a.valid || !b.valid || b.containsZero
      ? invalid : a.lower == a.upper && a == b ? point(1)
      : a * new Interval(down(1 / b.upper), up(1 / b.lower));

  private static bool integerPoint(Interval a) => a.lower == a.upper
      && a.lower == Math.Truncate(a.lower) && Math.Abs(a.lower) <= 9007199254740992d;

  public Interval power(int n) {
    if (!valid) {
      return invalid;
    }
    if (n < 0) {
      return point(1) / power(-n);
    }
    var result = point(1);
    var factor = this;
    while (n > 0) {
      if ((n & 1) != 0) {
        result *= factor;
      }
      n >>= 1;
      if (n > 0) {
        factor = factor.square();
      }
    }
    return result;
  }

  private Interval square() => !valid ? invalid
      : integerPoint(this) && Math.Abs(lower) <= 67108864 ? point(lower * lower) : new(
      containsZero ? 0 : Math.Max(0, down(Math.Min(lower * lower, upper * upper))),
      up(Math.Max(lower * lower, upper * upper)));

  public Interval absolute() => !valid ? invalid : containsZero
      ? new(0, Math.Max(-lower, upper)) : lower > 0 ? this : -this;

  public Interval squareRoot() {
    if (!valid || lower < 0) {
      return invalid;
    }
    static Interval endpoint(double x) {
      if (x == 0) {
        return point(0);
      }
      double lo = Math.Sqrt(x), hi = lo;
      for (int i = 0; i < 64; i++) {
        bool lowSafe = point(lo).square().upper <= x;
        bool highSafe = point(hi).square().lower >= x;
        if (lowSafe && highSafe) {
          return new(lo, hi);
        }
        if (!lowSafe) {
          lo = down(lo);
        }
        if (!highSafe) {
          hi = up(hi);
        }
      }
      return invalid;
    }
    var a = endpoint(lower);
    var b = endpoint(upper);
    return a.valid && b.valid ? new(a.lower, b.upper) : invalid;
  }

  public Interval exponential() {
    if (!valid || upper > 700) {
      return invalid;
    }
    static Interval endpoint(double x) {
      int scale = 0;
      while (Math.Abs(Math.ScaleB(x, -scale)) > 0.5) {
        scale++;
      }
      var r = point(Math.ScaleB(x, -scale));
      var sum = point(1);
      var term = point(1);
      for (int k = 1; k <= 32; k++) {
        term = term * r / point(k);
        sum += term;
      }
      // exp(r) Taylor remainder <= 2*|r|^33/33!, |r|<=1/2.
      var tail = point(2);
      for (int k = 1; k <= 33; k++) {
        tail = tail * r.absolute() / point(k);
      }
      sum += new Interval(-tail.upper, tail.upper);
      for (int k = 0; k < scale; k++) {
        sum = sum.square();
      }
      return sum;
    }
    return new(lower < -700 ? 0 : endpoint(lower).lower,
        endpoint(Math.Max(-700, upper)).upper);
  }

  public Interval logarithm() {
    if (!valid || lower <= 0) {
      return invalid;
    }
    static Interval endpoint(double x) {
      int exponent = Math.ILogB(x);
      var m = point(Math.ScaleB(x, -exponent));
      var z = (m - point(1)) / (m + point(1));
      var z2 = z.square();
      var term = z;
      var sum = z;
      for (int k = 1; k < 40; k++) {
        term *= z2;
        sum += term / point(2 * k + 1);
      }
      var tail = point(2) * term.absolute() * z2 / (point(81) * (point(1) - z2));
      return point(2) * sum + new Interval(-tail.upper, tail.upper) + point(exponent) * ln2;
    }
    return new(endpoint(lower).lower, endpoint(upper).upper);
  }

  public Interval arctangent() {
    if (!valid) {
      return invalid;
    }
    static Interval endpoint(double x) {
      if (x < 0) {
        return -endpoint(-x);
      }
      var p = point(x);
      if (x > 1) {
        return pi / point(2) - (point(1) / p).arctangent();
      }
      if (x > 0.5) {
        return pi / point(4) + ((p - point(1)) / (p + point(1))).arctangent();
      }
      var square = p.power(2);
      var term = p;
      var sum = p;
      for (int k = 1; k < 40; k++) {
        term = -term * square;
        sum += term / point(2 * k + 1);
      }
      var tail = term.absolute() * square / point(81);
      return sum + new Interval(-tail.upper, tail.upper);
    }
    return new(endpoint(lower).lower, endpoint(upper).upper);
  }

  public Interval hyperbolicTangent() {
    if (!valid) {
      return invalid;
    }
    static Interval endpoint(double x) {
      if (x < 0) {
        return -endpoint(-x);
      }
      if (x >= 20) {
        return new(down(1), 1);
      }
      var exp = point(-2 * x).exponential();
      return (point(1) - exp) / (point(1) + exp);
    }
    var a = endpoint(lower);
    var b = endpoint(upper);
    return new(Math.Max(-1, a.lower), Math.Min(1, b.upper));
  }

  public Interval sine() => trigonometric(false);

  public Interval cosine() => trigonometric(true);

  private Interval trigonometric(bool cosine) {
    if (!valid) {
      return invalid;
    }
    if (width >= 6 || Math.Max(Math.Abs(lower), Math.Abs(upper)) > 1e12) {
      return new(-1, 1);
    }
    double middle = lower + (upper - lower) / 2;
    double turns = Math.Round(middle / (2 * Math.PI));
    var r = this - point(turns) * (point(2) * pi);
    double radius = Math.Max(Math.Abs(r.lower), Math.Abs(r.upper));
    if (radius > 7) {
      return new(-1, 1);
    }
    var r2 = r.square();
    var term = cosine ? point(1) : r;
    var sum = term;
    for (int k = 1; k < 32; k++) {
      int first = cosine ? 2 * k - 1 : 2 * k;
      term = -term * r2 / point(first * (first + 1));
      sum += term;
    }
    // All real derivatives of sin/cos are bounded by 1.
    var tail = point(1);
    int degree = cosine ? 63 : 64;
    for (int k = 1; k <= degree; k++) {
      tail = tail * point(radius) / point(k);
    }
    sum += new Interval(-tail.upper, tail.upper);
    return sum.valid ? new(Math.Max(-1, sum.lower), Math.Min(1, sum.upper)) : invalid;
  }
}
