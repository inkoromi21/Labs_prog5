namespace WpfApp2;

public sealed record CompiledFormula(string expression, FormulaNode root) {
  public double evaluate(double x) => root.at(x).value;

  public Differential derivatives(double x) => root.at(x);
  // Used only to leave gaps at discontinuities in the plotted curve.

  public Interval enclose(double left, double right) => root.over(new(left, right));
}

public readonly record struct Differential(double value, double first, double second) {
  public static Differential operator +(Differential a, Differential b) =>
      new(a.value + b.value, a.first + b.first, a.second + b.second);

  public static Differential operator -(Differential a, Differential b) =>
      new(a.value - b.value, a.first - b.first, a.second - b.second);

  public static Differential operator *(Differential a, Differential b) =>
      new(a.value * b.value, a.first * b.value + a.value * b.first,
          a.second * b.value + 2 * a.first * b.first + a.value * b.second);

  public Differential compose(double y, double d, double dd) =>
      new(y, d * first, dd * first * first + d * second);
}

public sealed record FormulaNode(string operation, FormulaNode? left = null,
    FormulaNode? right = null, double number = 0) {
  private double? constant => operation switch {
    "number" => number,
    "pi" => Math.PI,
    "e" => Math.E,
    "neg" when left!.constant.HasValue => -left.constant.Value,
    _ => null
  };

  public Differential at(double x) {
    if (operation == "x") {
      return new(x, 1, 0);
    }
    if (constant is double c) {
      return new(c, 0, 0);
    }
    var a = left!.at(x);
    if (operation == "neg") {
      return new(-a.value, -a.first, -a.second);
    }
    if (right is not null) {
      var b = right.at(x);
      switch (operation) {
        case "+":
          return a + b;
        case "-":
          return a - b;
        case "*":
          return a * b;
        case "/":
          double quotient = a.value / b.value;
          double first = (a.first - quotient * b.first) / b.value;
          return new(quotient, first,
              (a.second - quotient * b.second - 2 * first * b.first) / b.value);
        case "^":
          if (right.constant is double n) {
            if (n == 0) {
              return new(1, 0, 0);
            }
            if (n == 1) {
              return a;
            }
            return a.compose(Math.Pow(a.value, n), n * Math.Pow(a.value, n - 1),
                n == 2 ? 2 : n * (n - 1) * Math.Pow(a.value, n - 2));
          }
          var logarithm = a.compose(Math.Log(a.value), 1 / a.value,
              -1 / (a.value * a.value));
          var exponent = b * logarithm;
          double exp = Math.Exp(exponent.value);
          var result = exponent.compose(exp, exp, exp);
          return result with {
            value = Math.Pow(a.value, b.value)
          };
      }
    }
    double v = a.value;
    if (double.IsNaN(v)) {
      return new(double.NaN, double.NaN, double.NaN);
    }
    double t = Math.Tanh(v);
    return operation switch {
      "sign" => a.compose(Math.Sign(v), v == 0 ? double.NaN : 0, v == 0 ? double.NaN : 0),
      "atan" => a.compose(Math.Atan(v), 1 / (1 + v * v), -2 * v / Math.Pow(1 + v * v, 2)),
      "tanh" => a.compose(t, 1 - t * t, -2 * t * (1 - t * t)),
      "sin" => a.compose(Math.Sin(v), Math.Cos(v), -Math.Sin(v)),
      "cos" => a.compose(Math.Cos(v), -Math.Sin(v), -Math.Cos(v)),
      "tan" => a.compose(Math.Tan(v), 1 / Math.Pow(Math.Cos(v), 2),
          2 * Math.Tan(v) / Math.Pow(Math.Cos(v), 2)),
      "sqrt" => a.compose(Math.Sqrt(v), 0.5 / Math.Sqrt(v), -0.25 / (v * Math.Sqrt(v))),
      "ln" => a.compose(Math.Log(v), 1 / v, -1 / (v * v)),
      "log" => a.compose(Math.Log10(v), 1 / (v * Math.Log(10)),
          -1 / (v * v * Math.Log(10))),
      "exp" => a.compose(Math.Exp(v), Math.Exp(v), Math.Exp(v)),
      "abs" => a.compose(Math.Abs(v), v == 0 ? double.NaN : Math.Sign(v),
          v == 0 ? double.NaN : 0),
      _ => new(double.NaN, double.NaN, double.NaN)
    };
  }

  public Interval over(Interval x) {
    if (operation == "x") {
      return x;
    }
    if (operation == "number") {
      return Interval.literal(number);
    }
    if (operation == "pi") {
      return Interval.pi;
    }
    if (operation == "e") {
      return new(Math.BitDecrement(Math.E), Math.BitIncrement(Math.E));
    }
    var a = left!.over(x);
    if (operation == "neg") {
      return -a;
    }
    if (right is not null) {
      var b = right.over(x);
      return operation switch {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => a / b,
        "^" when right.constant is double n && n == Math.Truncate(n) && Math.Abs(n) <= 1024
            => a.power((int)n),
        "^" => (a.logarithm() * b).exponential(),
        _ => Interval.invalid
      };
    }
    return operation switch {
      "sign" => !a.valid ? Interval.invalid : a.lower > 0 ? Interval.point(1)
          : a.upper < 0 ? Interval.point(-1) : a == Interval.point(0) ? a : Interval.invalid,
      "atan" => a.arctangent(),
      "tanh" => a.hyperbolicTangent(),
      "sin" => a.sine(),
      "cos" => a.cosine(),
      "tan" => a.sine() / a.cosine(),
      "sqrt" => a.squareRoot(),
      "ln" => a.logarithm(),
      "log" => a.logarithm() / Interval.ln10,
      "exp" => a.exponential(),
      "abs" => a.absolute(),
      _ => Interval.invalid
    };
  }
}
