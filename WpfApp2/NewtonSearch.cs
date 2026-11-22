namespace WpfApp2;

public sealed class NewtonSearch {
  private const int MAX_ITERATIONS = 200;

  public SearchResult find(SearchInput input, CancellationToken token) {
    if (!double.IsFinite(input.a) || !double.IsFinite(input.b) || input.a >= input.b
        || !double.IsFinite(input.b - input.a) || input.digits is < 0 or > 15
        || !Enum.IsDefined(input.mode)) {
      throw new InputException("Проверьте интервал, точность и режим поиска.");
    }
    token.ThrowIfCancellationRequested();
    double fa = input.formula.evaluate(input.a), fb = input.formula.evaluate(input.b);
    bool startOnRight = double.IsFinite(fb) && (!double.IsFinite(fa) || fb > fa);

    SearchResult fromBoundary(double start) {
      var result = iterate(input, start, token);
      if (result.outcome is SearchOutcome.MINIMUM or SearchOutcome.MAXIMUM
          && (better(fa, result.y) || better(fb, result.y))) {
        return result with {
          outcome = SearchOutcome.NO_EXTREMUM,
          pointKind = SearchPointKind.UNKNOWN,
          message = "На границе отрезка значение функции лучше."
        };
      }
      return result;
    }
    bool better(double boundary, double y) => double.IsFinite(boundary)
        && (input.mode == SearchMode.MINIMUM ? boundary < y : boundary > y);

    var first = fromBoundary(startOnRight ? input.b : input.a);
    return input.mode == SearchMode.MAXIMUM && !first.hasSolution
        ? fromBoundary(startOnRight ? input.a : input.b) : first;
  }

  private static SearchResult iterate(SearchInput input, double start, CancellationToken token) {
    bool root = input.mode == SearchMode.ROOT;
    bool minimum = input.mode == SearchMode.MINIMUM;
    double epsilon = 0.5 * Math.Pow(10, -input.digits);
    double x = start;
    var history = new List<NewtonStep>();
    var visited = new HashSet<double>();
    string note = "Начальная точка на границе отрезка";
    SearchResult finish(SearchOutcome outcome, string message,
        SearchPointKind kind = SearchPointKind.UNKNOWN) => new(
        history.Count == 0 ? double.NaN : history[^1].x,
        history.Count == 0 ? double.NaN : history[^1].y) {
          outcome = outcome, message = message, steps = history.ToArray(),
          pointKind = kind, usesTangents = root
        };

    for (int i = 0; i <= MAX_ITERATIONS; i++) {
      token.ThrowIfCancellationRequested();
      var d = input.formula.derivatives(x);
      if (!double.IsFinite(d.value)) {
        return finish(SearchOutcome.STOPPED, "Функция не определена или переполнение.");
      }
      double target = root ? d.value : d.first;
      double derivative = root ? d.first : d.second;
      double correction = double.IsFinite(derivative) && derivative != 0
          ? target / derivative : double.NaN;
      double? angle = !root && double.IsFinite(d.first) ? Math.Atan(d.first) : null;
      history.Add(new(x, d.value, note, angle));

      bool exact = target == 0;
      double error = exact ? 0 : estimatedError(history, correction);
      if (error < epsilon) {
        double left = Math.Max(input.a, x - (exact ? 0 : epsilon));
        double right = Math.Min(input.b, x + (exact ? 0 : epsilon));
        if (root && input.formula.enclose(left, right).containsZero) {
          return finish(SearchOutcome.ROOT,
              "Корень найден методом Ньютона с использованием производной f′(x).");
        }
        if (!root && isExtremum(input, x, error)) {
          return finish(minimum ? SearchOutcome.MINIMUM : SearchOutcome.MAXIMUM,
              "Найден экстремум методом Ньютона по производной.",
              minimum ? SearchPointKind.MINIMUM : SearchPointKind.MAXIMUM);
        }
        if (exact && !root) {
          return finish(SearchOutcome.NO_EXTREMUM, "В этой точке не подтверждён экстремум выбранного типа.");
        }
      }
      if (i == MAX_ITERATIONS) {
        return finish(SearchOutcome.STOPPED, "Нет сходимости за 200 итераций.");
      }
      if (!double.IsFinite(correction)) {
        return finish(SearchOutcome.STOPPED, root
            ? "Не удалось вычислить шаг Ньютона по первой производной."
            : "Не удалось вычислить шаг Ньютона по первой и второй производным.");
      }
      double next = x - correction;
      if (!double.IsFinite(next) || next < input.a || next > input.b) {
        return finish(SearchOutcome.STOPPED, "Следующий шаг Ньютона выходит за пределы интервала.");
      }
      if (next == x) {
        return finish(SearchOutcome.STOPPED, "");
      }
      if (!visited.Add(x) || visited.Contains(next)) {
        return finish(SearchOutcome.STOPPED, "Поиск остановлен: шаг вернулся к уже проверенной точке.");
      }
      if (!input.formula.enclose(Math.Min(x, next), Math.Max(x, next)).valid) {
        return finish(SearchOutcome.STOPPED,
            "Шаг пересекает участок, где непрерывность функции не подтверждена.");
      }
      x = next;
      note = root ? "Шаг Ньютона для f" : "Шаг Ньютона для f′";
    }
    throw new InvalidOperationException();
  }

  private static bool isExtremum(SearchInput input, double x, double error) {
    double nominalRadius = Math.Min((input.b - input.a) * 1e-3, Math.Max(1, Math.Abs(x)) * 1e-7);
    double radius = Math.Min(Math.Max(nominalRadius, 4 * error), Math.Min(x - input.a, input.b - x));
    double left = x - radius, right = x + radius;
    if (left >= x || right <= x || !input.formula.enclose(left, right).valid) {
      return false;
    }
    double before = input.formula.derivatives(left).first;
    double after = input.formula.derivatives(right).first;
    return double.IsFinite(before) && double.IsFinite(after)
        && (input.mode == SearchMode.MINIMUM ? before < 0 && after > 0 : before > 0 && after < 0);
  }

  private static double estimatedError(IReadOnlyList<NewtonStep> history, double correction) {
    if (history.Count < 2 || !double.IsFinite(correction) || correction == 0) {
      return double.PositiveInfinity;
    }
    double previous = history[^1].x - history[^2].x;
    double ratio = Math.Abs(correction / previous);
    return ratio < 1 ? Math.Abs(correction) / (1 - ratio) : double.PositiveInfinity;
  }
}
