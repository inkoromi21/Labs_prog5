using System.Globalization;

namespace WpfApp2;

/// <summary>Ищет корень или экстремум методом Ньютона с численными производными.</summary>
public sealed class NewtonSearch {
  private const int MAX_ITERATIONS = 200;
  private const int MAX_BACKTRACKS = 60;

  public SearchResult find(SearchInput input, double?[] values,
      CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    if (!double.IsFinite(input.a) || !double.IsFinite(input.b) || input.a >= input.b
        || !double.IsFinite(input.b - input.a)) {
      throw new InputException("Нужно задать конечные границы a < b.");
    }

    if (!double.IsFinite(input.epsilon) || input.epsilon <= 0 || input.digits is < 0 or > 15
        || !Enum.IsDefined(input.mode)) {
      throw new InputException("Проверьте задачу и точность.");
    }

    if (values.Length < 3) {
      throw new InputException("Недостаточно точек для поиска.");
    }

    int invalid = Array.FindIndex(values, value => value is null || !double.IsFinite(value.Value));
    if (invalid >= 0) {
      double location = point(input, invalid, values.Length);
      throw new InputException("Разрыв или ошибка области определения: "
          + $"f(x) не определена при x≈{format(location)}.");
    }

    double[] samples = values.Select(value => value!.Value).ToArray();
    if (samples.All(value => value == samples[0])) {
      throw new InputException(input.mode == SearchMode.ROOT && samples[0] == 0
          ? "Функция равна нулю во всех проверенных точках."
          : "Функция постоянна на проверенных точках.");
    }

    double[] objective = samples.Select(value => score(value, input.mode)).ToArray();
    if (countBasins(objective) > 1) {
      throw new InputException("Сузьте интервал.");
    }

    int best = Array.IndexOf(objective, objective.Min());
    double x = point(input, best, samples.Length);
    double y = evaluate(input, x);
    double scale = samples.Max(value => Math.Abs(value));

    for (int iteration = 0; iteration < MAX_ITERATIONS; iteration++) {
      cancellationToken.ThrowIfCancellationRequested();
      checkPrecision(input, x);
      if (input.mode == SearchMode.ROOT && y == 0) {
        return result(x, y, x);
      }

      var (h, first, second, localScale) = derivatives(input, x, y);
      // first ≈ h*f'(x)/localScale, second ≈ h²*f''(x)/localScale.
      // Общий масштаб сокращается в отношении производных.
      if (input.mode != SearchMode.ROOT) {
        double direction = input.mode == SearchMode.MINIMUM ? 1 : -1;
        if ((x == input.a && direction * first / h >= 0)
            || (x == input.b && direction * first / h <= 0)) {
          return result(x, y, x);
        }

        if (direction * second <= 0) {
          throw failure(input.mode);
        }
      }

      double denominator = input.mode == SearchMode.ROOT ? first : second;
      if (denominator == 0 || !double.IsFinite(denominator)) {
        throw failure(input.mode);
      }

      double step = input.mode == SearchMode.ROOT
          ? h * ((y / localScale) / first)
          : h * (first / second);
      if (!double.IsFinite(step)) {
        throw failure(input.mode);
      }

      // Проверяем полный шаг Ньютона, а не уменьшенный при возврате в интервал.
      if (Math.Abs(step) <= input.epsilon) {
        if (input.mode == SearchMode.ROOT ? isRoot(input, x, y, scale)
            : nearExtremum(input, x, y)) {
          double next = x - step;
          if (next >= input.a && next <= input.b) {
            double nextY = evaluate(input, next);
            if (score(nextY, input.mode) <= score(y, input.mode)) {
              return result(next, nextY, x);
            }
          }

          return result(x, y, x);
        }
      }

      bool accepted = false;
      for (int attempt = 0; attempt < MAX_BACKTRACKS; attempt++) {
        cancellationToken.ThrowIfCancellationRequested();
        double next = x - step;
        if (next == x) {
          break;
        }

        if (double.IsFinite(next) && next >= input.a && next <= input.b) {
          double nextY = input.formula.evaluate(next);
          if (double.IsFinite(nextY) && score(nextY, input.mode) < score(y, input.mode)) {
            x = next;
            y = nextY;
            accepted = true;
            break;
          }
        }

        step /= 2;
      }

      if (!accepted) {
        throw failure(input.mode);
      }
    }

    throw failure(input.mode);
  }

  /// <summary>Вычисляет производные по пяти точкам, оставаясь внутри [a, b].</summary>
  private static (double h, double first, double second, double scale) derivatives(
      SearchInput input, double x, double y) {
    double h = Math.Min((input.b - input.a) / 1024, Math.Max(1, Math.Abs(x)) * 1e-4);
    bool central = x - 2 * h >= input.a && x + 2 * h <= input.b;
    if (!central) {
      double room = Math.Max(x - input.a, input.b - x);
      h = Math.Min(h, room / 4);
      if (x - input.a > input.b - x) {
        h = -h;
      }
    }

    var points = new double[5];
    for (int i = 0; i < points.Length; i++) {
      int offset = central ? i - 2 : i;
      double sampleX = x + offset * h;
      if (offset != 0 && sampleX == x) {
        throw new InputException("Недостаточно точности чисел для вычисления производных.");
      }

      points[i] = evaluate(input, sampleX);
    }

    double scale = Math.Max(Math.Abs(y), points.Max(value => Math.Abs(value)));
    if (scale == 0) {
      throw failure(input.mode);
    }

    // Вычитаем f(x), чтобы постоянная часть формулы не влияла на разности.
    double[] d = points.Select(value => value / scale - y / scale).ToArray();
    double first = central
        ? (d[0] - d[4]) / 12 + (d[3] - d[1]) * 2 / 3
        : 4 * d[1] - 3 * d[2] + 4 * d[3] / 3 - d[4] / 4;
    double second = central
        ? (d[1] + d[3]) * 4 / 3 - (d[0] + d[4]) / 12
        : -26 * d[1] / 3 + 19 * d[2] / 2 - 14 * d[3] / 3 + 11 * d[4] / 12;
    return (h, first, second, scale);
  }

  /// <summary>Предварительная проверка по точкам, а не доказательство единственности.</summary>
  private static int countBasins(double[] values) {
    int count = 0;
    for (int first = 0; first < values.Length;) {
      int last = first;
      while (last + 1 < values.Length && values[last + 1] == values[first]) {
        last++;
      }

      if ((first == 0 || values[first] < values[first - 1])
          && (last == values.Length - 1 || values[last] < values[last + 1])) {
        count++;
      }

      first = last + 1;
    }

    return count;
  }

  private static void checkPrecision(SearchInput input, double x) {
    double spacing = Math.Max(Math.Abs(Math.BitIncrement(x) - x),
        Math.Abs(x - Math.BitDecrement(x)));
    if (spacing > input.epsilon) {
      throw new InputException($"Недостаточно точности чисел для {input.digits} знаков.");
    }
  }

  private static bool isRoot(SearchInput input, double x, double y, double scale) {
    if (y == 0 || Math.Abs(y) / scale <= 1e-12) {
      return true;
    }

    // Например, Math.Sin(Math.PI) не равен нулю из-за округления double.
    double left = Math.BitDecrement(x);
    double right = Math.BitIncrement(x);
    if (left < input.a || right > input.b) {
      return false;
    }

    double leftY = input.formula.evaluate(left);
    double rightY = input.formula.evaluate(right);
    return double.IsFinite(leftY) && double.IsFinite(rightY)
        && Math.Max(Math.Abs(leftY), Math.Abs(rightY)) <= scale
        && Math.Sign(leftY) != Math.Sign(rightY);
  }

  private static bool nearExtremum(SearchInput input, double x, double y) {
    foreach (double neighbor in new[] {
      Math.Max(input.a, x - input.epsilon),
      Math.Min(input.b, x + input.epsilon)
    }) {
      double value = input.formula.evaluate(neighbor);
      if (!double.IsFinite(value) || score(value, input.mode) < score(y, input.mode)) {
        return false;
      }
    }

    return true;
  }

  private static double evaluate(SearchInput input, double x) {
    double y = input.formula.evaluate(x);
    if (!double.IsFinite(y)) {
      throw new InputException($"f(x) не определена при x≈{format(x)}.");
    }

    return y;
  }

  private static double score(double y, SearchMode mode) => mode switch {
    SearchMode.ROOT => Math.Abs(y),
    SearchMode.MAXIMUM => -y,
    _ => y
  };

  private static SearchResult result(double x, double y, double previous) =>
      new(x, y, Math.Min(x, previous), Math.Max(x, previous));

  private static InputException failure(SearchMode mode) => new(mode == SearchMode.ROOT
      ? "Метод Ньютона не сошёлся к корню. Возможны нулевая производная или разрыв. "
          + "Сузьте интервал."
      : "Метод Ньютона не сошёлся к нужному экстремуму. "
          + "Возможны нулевая вторая производная, излом или разрыв. Сузьте интервал.");

  private static double point(SearchInput input, int index, int count) => index == count - 1
      ? input.b
      : input.a + (input.b - input.a) * index / (count - 1);

  private static string format(double value) => value.ToString("G6", CultureInfo.CurrentCulture);
}
