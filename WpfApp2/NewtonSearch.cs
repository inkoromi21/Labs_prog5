namespace WpfApp2;

public sealed class NewtonSearch {
  private const int MAX_ITERATIONS = 200;

  public SearchResult find(SearchInput input, CancellationToken token) {
    if (!double.IsFinite(input.a) || !double.IsFinite(input.b) || input.a >= input.b
        || !double.IsFinite(input.b - input.a) || !double.IsFinite(input.epsilon)
        || input.epsilon <= 0 || input.digits is < 0 or > 15 || !Enum.IsDefined(input.mode)) {
      throw new InputException("Проверьте интервал, точность и режим поиска.");
    }
    bool root = input.mode == SearchMode.ROOT;
    double direction = input.mode == SearchMode.MAXIMUM ? -1 : 1;
    double x = input.a;
    var history = new List<NewtonStep>();
    var visited = new HashSet<double>();
    SearchResult finish(SearchOutcome outcome, string reason) =>
        new(history.Count == 0 ? double.NaN : history[^1].x,
            history.Count == 0 ? double.NaN : history[^1].y) {
          outcome = outcome,
          message = reason,
          steps = history.ToArray()
        };

    SearchResult stationary(Differential d) {
      double left = input.formula.derivatives(x - input.epsilon).first;
      double right = input.formula.derivatives(x + input.epsilon).first;
      var kind = !double.IsFinite(d.second) || d.second == 0
          || !double.IsFinite(left) || !double.IsFinite(right) ? SearchOutcome.STATIONARY
          : d.second > 0 && left < 0 && right > 0 ? SearchOutcome.MINIMUM
          : d.second < 0 && left > 0 && right < 0 ? SearchOutcome.MAXIMUM
          : SearchOutcome.STATIONARY;
      string text = kind == SearchOutcome.STATIONARY
          ? "Получено приближение стационарной точки. Локальный тип не установлен."
          : "Получено приближение локального "
              + (kind == SearchOutcome.MINIMUM ? "минимума." : "максимума.");
      if (kind != SearchOutcome.STATIONARY && direction * d.second < 0) {
        text += " Это не запрошенный тип экстремума; другой старт не подбирался.";
      }
      return finish(kind, text + " Глобальность не установлена. Точность не гарантируется.");
    }

    for (int i = 0; i <= MAX_ITERATIONS; i++) {
      token.ThrowIfCancellationRequested();
      var d = input.formula.derivatives(x);
      if (!double.IsFinite(d.value)) {
        return finish(SearchOutcome.STOPPED,
            "Функция не определена или переполнение. Метод остановлен.");
      }
      history.Add(new(x, d.value));
      double target = root ? d.value : direction * d.first;
      double denominator = root ? d.first : direction * d.second;
      if (!double.IsFinite(target)) {
        return finish(SearchOutcome.STOPPED,
            "Необходимая производная не определена. Метод остановлен.");
      }
      if (!root && target == 0) {
        return stationary(d);
      }
      if (!double.IsFinite(denominator)) {
        return finish(SearchOutcome.STOPPED,
            "Производная в знаменателе не определена. Метод остановлен.");
      }
      if (denominator == 0) {
        return finish(SearchOutcome.STOPPED, root
            ? "f′(x) = 0: шаг Ньютона невозможен. "
                + "Нулевая машинная невязка также не доказывает точность."
            : "f″(x) = 0: шаг Ньютона невозможен.");
      }
      double correction = target / denominator;
      if (!double.IsFinite(correction)) {
        return finish(SearchOutcome.STOPPED, "Шаг Ньютона привёл к переполнению.");
      }
      // shortcut: this stopping rule is numerical, not a bound on the true error.
      if (Math.Abs(correction) <= input.epsilon && Math.Abs(target) <= input.epsilon) {
        return root ? finish(SearchOutcome.ROOT,
            "Получено приближение корня: невязка и поправка Ньютона не больше ε. "
                + "Точность не гарантируется.")
            : stationary(d);
      }
      if (i == MAX_ITERATIONS) {
        return finish(SearchOutcome.STOPPED, "Нет сходимости за 200 итераций. "
            + "Это не доказывает отсутствия решения.");
      }
      double next = x - correction;
      if (!double.IsFinite(next)) {
        return finish(SearchOutcome.STOPPED, "Шаг Ньютона привёл к переполнению.");
      }
      if (next < input.a || next > input.b) {
        return finish(SearchOutcome.STOPPED,
            $"Следующая точка {next:G10} вне [a; b]. "
                + "Метод остановлен; это не доказывает отсутствия решения.");
      }
      if (next == x) {
        return finish(SearchOutcome.STOPPED, "Машинная остановка: точка больше не изменяется, "
            + "критерий сходимости не выполнен.");
      }
      if (!visited.Add(x) || visited.Contains(next)) {
        return finish(SearchOutcome.STOPPED, "Обнаружен цикл Ньютона. Метод остановлен.");
      }
      x = next;
    }
    throw new InvalidOperationException();
  }
}
