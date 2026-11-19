namespace WpfApp2;

public sealed class NewtonSearch {
  private const int MAX_ITERATIONS = 200;
  private const int SAMPLE_COUNT = 2048;

  public SearchResult find(SearchInput input, CancellationToken token) {
    if (!double.IsFinite(input.a) || !double.IsFinite(input.b) || input.a >= input.b
        || !double.IsFinite(input.b - input.a) || input.digits is < 0 or > 15
        || !Enum.IsDefined(input.mode)) {
      throw new InputException("Проверьте интервал, точность и режим поиска.");
    }
    double fa = input.formula.evaluate(input.a), fb = input.formula.evaluate(input.b);
    double start = double.IsFinite(fb) && (!double.IsFinite(fa) || fb > fa)
        ? input.b : input.a;
    bool root = input.mode == SearchMode.ROOT;
    var roots = new List<SearchResult>();
    var stationary = new List<SearchResult>();

    var coordinates = Enumerable.Range(0, SAMPLE_COUNT + 1)
        .Select(i => i == SAMPLE_COUNT ? input.b
            : input.a + (input.b - input.a) * (i / (double)SAMPLE_COUNT)).ToList();
    if (input.a <= 0 && input.b >= 0) {
      coordinates.Add(0);
    }
    coordinates = coordinates.Distinct().OrderBy(x => x).ToList();
    bool hasGap = false;
    double previousX = coordinates[0];
    var previous = input.formula.derivatives(previousX);
    // shortcut: finite sampling can miss close roots; use isolation if completeness is required.
    foreach (double x in coordinates) {
      token.ThrowIfCancellationRequested();
      var d = input.formula.derivatives(x);
      if (double.IsFinite(d.value)) {
        if (root && d.value == 0 && isRoot(input.formula, x)) {
          addCandidate(roots, candidate(x, d.value, SearchOutcome.ROOT,
              x == 0 ? "Проверка x = 0" : "Проверка точки интервала"));
        }
        if (d.first == 0) {
          addCandidate(stationary, candidate(x, d.value, SearchOutcome.STATIONARY,
              "Проверка стационарной точки"));
        }
      } else {
        hasGap = true;
      }
      if (x != previousX) {
        bool continuous = input.formula.enclose(previousX, x).valid;
        hasGap |= !continuous;
        if (root && continuous && opposite(previous.value, d.value)) {
          var found = locate(input, previousX, x, false, token);
          if (found is not null) {
            addCandidate(roots, found);
          }
        }
        if (continuous && opposite(previous.first, d.first)) {
          var found = locate(input, previousX, x, true, token);
          if (found is not null) {
            addCandidate(stationary, found);
          }
        }
      }
      previousX = x;
      previous = d;
    }
    var candidates = root ? roots : new List<SearchResult>();
    foreach (var point in stationary) {
      if (root) {
        if (isRoot(input.formula, point.x)) {
          addCandidate(candidates, point with { outcome = SearchOutcome.ROOT });
        }
      } else {
        var kind = classify(input, point.x);
        if (input.mode == SearchMode.MINIMUM && kind == SearchPointKind.MINIMUM
            || input.mode == SearchMode.MAXIMUM && kind == SearchPointKind.MAXIMUM) {
          addCandidate(candidates, point with { pointKind = kind });
        }
      }
    }
    string type = input.mode == SearchMode.MINIMUM ? "минимумов" : "максимумов";
    SearchResult refuse(SearchOutcome outcome, string message) => new(double.NaN, double.NaN) {
      outcome = outcome, message = message
    };
    if (candidates.Count > 1) {
      return refuse(root ? SearchOutcome.MULTIPLE_ROOTS : SearchOutcome.MULTIPLE_EXTREMA,
          root ? "На интервале обнаружено несколько корней; единственный ответ определить нельзя."
              : $"На интервале обнаружено несколько внутренних {type}; "
                  + "единственный ответ определить нельзя.");
    }
    if (!root && hasGap) {
      return refuse(SearchOutcome.STOPPED,
          "На интервале есть разрыв, неопределённость или слишком большие значения. "
              + "Проверить количество внутренних экстремумов не удалось.");
    }
    if (candidates.Count == 0) {
      string message = root
          ? "Предварительная проверка не обнаружила пересечений или касаний оси Ox "
              + "на заданном интервале. Метод Ньютона не запускался."
          : $"На заданном интервале внутренних {type} не обнаружено. "
              + "Границы интервала не считаются ответом. Метод Ньютона не запускался.";
      if (isReciprocal(input.formula.root)) {
        message += root ? " У 1/x нет конечного корня: к оси Ox функция приближается "
                + "только при уходе x в бесконечность. При x = 0 функция не определена."
            : " У 1/x нет внутреннего минимума или максимума: производная не обращается в ноль.";
      }
      return refuse(root ? SearchOutcome.NO_ROOTS : SearchOutcome.NO_EXTREMUM, message);
    }

    var expected = candidates[0];
    if (root && (expected.x == input.a || expected.x == input.b) && expected.y == 0) {
      var history = new List<NewtonStep> {
        new(start, input.formula.evaluate(start), "Начальная точка: большая f на границе")
      };
      if (start != expected.x) {
        history.Add(new(expected.x, 0, "Проверка корня на границе"));
      }
      return expected with { steps = history, message = "Корень проверен на границе интервала." };
    }
    var primary = iterate(input, start, !root, token);
    bool canCheckZero = primary.x == 0 || primary.outcome == SearchOutcome.STOPPED
        && Math.Abs(primary.x) <= Math.Max(1, input.b - input.a) * 1e-12;
    if (expected.x == 0 && primary.steps.Count > 0 && canCheckZero) {
      var history = primary.steps;
      if (primary.x != 0) {
        history = history.Append(new NewtonStep(0, expected.y, "Проверка x = 0")).ToArray();
      }
      primary = expected with { steps = history };
    }
    if (root && primary.outcome == SearchOutcome.ROOT) {
      addCandidate(candidates, primary);
      if (candidates.Count > 1) {
        return refuse(SearchOutcome.MULTIPLE_ROOTS,
            "Обнаружен ещё один корень; единственный ответ определить нельзя.");
      }
      return primary with {
        message = "Найден корень. Других корней предварительная проверка не обнаружила; "
            + "единственность не доказана."
      };
    }
    if (!root && primary.outcome == SearchOutcome.STATIONARY) {
      var kind = classify(input, primary.x);
      if (kind == expected.pointKind) {
        addCandidate(candidates, primary);
        if (candidates.Count > 1) {
          return refuse(SearchOutcome.MULTIPLE_EXTREMA,
              $"Обнаружено несколько внутренних {type}; единственный ответ определить нельзя.");
        }
        return primary with {
          outcome = input.mode == SearchMode.MINIMUM
              ? SearchOutcome.MINIMUM : SearchOutcome.MAXIMUM,
          pointKind = kind,
          message = "Найден внутренний экстремум. Других экстремумов выбранного типа "
              + "предварительная проверка не обнаружила."
        };
      }
      return primary with {
        outcome = SearchOutcome.STOPPED,
        message = "Ньютон пришёл к другой стационарной точке. "
            + "Запрошенный тип экстремума с выбранной границы не найден."
      };
    }
    return primary;
  }

  private static SearchResult? locate(SearchInput input, double left, double right,
      bool derivative, CancellationToken token) {
    var first = input.formula.derivatives(left);
    double lowTarget = derivative ? first.first : first.value;
    for (int i = 0; i < MAX_ITERATIONS; i++) {
      token.ThrowIfCancellationRequested();
      double x = left + (right - left) / 2;
      var d = input.formula.derivatives(x);
      double target = derivative ? d.first : d.value;
      double denominator = derivative ? d.second : d.first;
      if (!double.IsFinite(d.value) || !double.IsFinite(target)) {
        return null;
      }
      bool found = derivative
          ? target == 0 || double.IsFinite(denominator) && denominator != 0
              && Math.Abs(target / denominator) <= tolerance(x)
          : isRoot(input.formula, x);
      if (found) {
        return candidate(x, d.value, derivative ? SearchOutcome.STATIONARY : SearchOutcome.ROOT,
            "Предварительная проверка интервала");
      }
      if (x == left || x == right) {
        return null;
      }
      if (opposite(lowTarget, target)) {
        right = x;
      } else {
        left = x;
        lowTarget = target;
      }
    }
    return null;
  }

  private static SearchResult iterate(SearchInput input, double start, bool derivative,
      CancellationToken token) {
    double x = start;
    var history = new List<NewtonStep>();
    var visited = new HashSet<double>();
    string note = "Начальная точка: большая f на границе";
    SearchResult finish(SearchOutcome outcome, string message) => new(
        history.Count == 0 ? double.NaN : history[^1].x,
        history.Count == 0 ? double.NaN : history[^1].y) {
          outcome = outcome, message = message, steps = history.ToArray()
        };
    for (int i = 0; i <= MAX_ITERATIONS; i++) {
      token.ThrowIfCancellationRequested();
      var d = input.formula.derivatives(x);
      if (!double.IsFinite(d.value)) {
        return finish(SearchOutcome.STOPPED, "Функция не определена или переполнение.");
      }
      history.Add(new(x, d.value, note));
      double target = derivative ? d.first : d.value;
      double denominator = derivative ? d.second : d.first;
      bool converged = derivative
          ? target == 0 || double.IsFinite(target) && double.IsFinite(denominator)
              && denominator != 0 && Math.Abs(target / denominator) <= tolerance(x)
          : isRoot(input.formula, x);
      if (converged) {
        return finish(derivative ? SearchOutcome.STATIONARY : SearchOutcome.ROOT,
            "Выполнен внутренний критерий сходимости.");
      }
      if (i == MAX_ITERATIONS) {
        return finish(SearchOutcome.STOPPED, "Нет сходимости за 200 итераций.");
      }
      if (!double.IsFinite(target)) {
        return finish(SearchOutcome.STOPPED, "Необходимая производная не определена.");
      }
      if (!double.IsFinite(denominator) || denominator == 0) {
        return finish(SearchOutcome.STOPPED, derivative
            ? "f″(x) = 0 или не определена: шаг Ньютона невозможен."
            : "f′(x) = 0 или не определена: шаг Ньютона невозможен.");
      }
      double next = x - target / denominator;
      note = derivative ? "Шаг Ньютона для f′" : "Шаг Ньютона для f";
      if (!double.IsFinite(next)) {
        return finish(SearchOutcome.STOPPED, "Шаг Ньютона привёл к переполнению.");
      }
      if (next < input.a || next > input.b) {
        return finish(SearchOutcome.STOPPED,
            $"Следующая точка {next:G10} вне [{input.a:G10}; {input.b:G10}]. Метод остановлен.");
      }
      if (next == x) {
        return finish(SearchOutcome.STOPPED,
            "Машинная остановка: точка не изменяется, критерий сходимости не выполнен.");
      }
      if (!visited.Add(x) || visited.Contains(next)) {
        return finish(SearchOutcome.STOPPED, "Обнаружен цикл Ньютона. Метод остановлен.");
      }
      x = next;
    }
    throw new InvalidOperationException();
  }

  private static SearchPointKind classify(SearchInput input, double x) {
    if (x <= input.a || x >= input.b) {
      return SearchPointKind.BOUNDARY;
    }
    double h = Math.Min((input.b - input.a) / SAMPLE_COUNT,
        Math.Max(1, Math.Abs(x)) * 1e-7);
    h = Math.Min(h, Math.Min(x - input.a, input.b - x) / 2);
    double left = input.formula.derivatives(x - h).first;
    double right = input.formula.derivatives(x + h).first;
    return left < 0 && right > 0 ? SearchPointKind.MINIMUM
        : left > 0 && right < 0 ? SearchPointKind.MAXIMUM : SearchPointKind.STATIONARY;
  }

  private static bool opposite(double a, double b) => double.IsFinite(a) && double.IsFinite(b)
      && (a < 0 && b > 0 || a > 0 && b < 0);

  private static double tolerance(double x) =>
      4 * Math.Abs(x - (x >= 0 ? Math.BitDecrement(x) : Math.BitIncrement(x)));

  private static bool isRoot(CompiledFormula formula, double x) {
    var d = formula.derivatives(x);
    if (!double.IsFinite(d.value)) {
      return false;
    }
    if (d.value == 0) {
      var atPoint = formula.enclose(x, x);
      return atPoint == Interval.point(0)
          || double.IsFinite(d.first) && d.first != 0
          || double.IsFinite(d.second) && d.second != 0;
    }
    if (double.IsFinite(d.first) && d.first != 0
        && Math.Abs(d.value / d.first) <= tolerance(x)) {
      return true;
    }
    double radius = 8 * tolerance(x);
    return double.IsFinite(d.second) && d.second != 0
        && Math.Abs(d.first / d.second) <= radius
        && Math.Abs(d.value / d.second) <= radius * radius
        && formula.enclose(x - radius, x + radius).containsZero;
  }

  private static void addCandidate(List<SearchResult> points, SearchResult point) {
    int index = points.FindIndex(p => Math.Abs(p.x - point.x)
        <= 8 * Math.Max(tolerance(p.x), tolerance(point.x)));
    if (index < 0) {
      points.Add(point);
    } else if (Math.Abs(point.y) < Math.Abs(points[index].y)) {
      points[index] = point;
    }
  }

  private static SearchResult candidate(double x, double y, SearchOutcome outcome, string note) =>
      new(x, y) { outcome = outcome, steps = new[] { new NewtonStep(x, y, note) } };

  private static bool isReciprocal(FormulaNode node) => node is {
    operation: "/", left: { operation: "number", number: 1 }, right: { operation: "x" }
  };
}
