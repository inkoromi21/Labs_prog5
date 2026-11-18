using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfApp2;

public partial class MainWindow : Window {
  private readonly ChartServiceClient charts = new();
  private readonly NewtonSearch finder = new();
  private readonly DispatcherTimer zoomTimer = new();
  private readonly LinkedList<ChartFrame> frames = new();
  private bool isInitializing = true;
  private bool isBusy;
  private bool isClosed;
  private bool rateRetryUsed;
  private int viewVersion;
  private IReadOnlyList<NewtonStep> steps = Array.Empty<NewtonStep>();
  private int selectedStep = -1;
  private string calculationError = "";
  private string graphError = "";
  private CancellationTokenSource? operation;
  private CancellationTokenSource? graphOperation;
  private DateTimeOffset nextRequestAt;

  public MainWindow() {
    zoomTimer.Interval = TimeSpan.FromMilliseconds(750);
    zoomTimer.Tick += async (_, _) => {
      zoomTimer.Stop();
      await refreshGraphAsync();
    };
    InitializeComponent();
    plotSurface.viewChanged += onPlotViewChanged;
    var area = SystemParameters.WorkArea;
    MinHeight = Math.Min(MinHeight, area.Height);
    MinWidth = Math.Min(MinWidth, area.Width);
    Height = Math.Min(Height, area.Height);
    Width = Math.Min(Width, area.Width);
    isInitializing = false;
  }

  private void onInputChanged(object sender, TextChangedEventArgs e) {
    if (isInitializing) {
      return;
    }
    clearState();
    statusText.Text = "Данные изменены. Нажмите «Рассчитать».";
  }

  private void onModeChanged(object sender, SelectionChangedEventArgs e) {
    if (!isInitializing) {
      clearState();
    }
  }

  private async void onSolveClick(object sender, RoutedEventArgs e) {
    if (isBusy) {
      return;
    }
    clearState();
    var current = new CancellationTokenSource();
    operation = current;
    setBusy(true);
    try {
      var input = readInput();
      statusText.Text = "Выполняется расчёт…";
      var result = await Task.Run(() => finder.find(input, current.Token), current.Token);
      if (isClosed || !ReferenceEquals(operation, current)) {
        return;
      }
      steps = result.steps;
      statusText.Text = result.message;
      calculationError = "";
      updateErrors();
      resultText.Text = result.outcome switch {
        SearchOutcome.ROOT => $"Корень: x ≈ {formatResult(result.x, input.digits)}",
        SearchOutcome.MINIMUM => $"Локальный минимум: ≈ {formatResult(result.y, input.digits)}",
        SearchOutcome.MAXIMUM => $"Локальный максимум: ≈ {formatResult(result.y, input.digits)}",
        SearchOutcome.STATIONARY =>
            $"Стационарная точка: x ≈ {formatResult(result.x, input.digits)}",
        _ => "Метод остановлен"
      };
      resultText.ToolTip = steps.Count > 0
          ? $"Последнее приближение: x ≈ {result.x:G17}; f(x) ≈ {result.y:G17}"
          : null;
      plotSurface.setSearch(input, steps);
      emptyPlotText.Visibility = Visibility.Collapsed;
      showStep(steps.Count > 0 ? 0 : -1);
    } catch (Exception error) {
      if (!isClosed && ReferenceEquals(operation, current)) {
        if (resultText.Text.Length == 0) {
          statusText.Text = "";
        }
        calculationError = errorMessage(error);
        updateErrors();
      }
    } finally {
      if (ReferenceEquals(operation, current)) {
        operation = null;
        setBusy(false);
      }
      current.Dispose();
    }
    if (!isClosed && plotSurface.input is not null) {
      await refreshGraphAsync();
    }
  }

  private void onPlotViewChanged(object? sender, EventArgs e) {
    if (isInitializing || plotSurface.view is null) {
      return;
    }
    viewVersion++;
    rateRetryUsed = false;
    if (useCachedFrame(plotSurface.view)) {
      zoomTimer.Stop();
      return;
    }
    scheduleGraph(TimeSpan.FromMilliseconds(750));
  }

  private void scheduleGraph(TimeSpan delay) {
    if (isClosed) {
      return;
    }
    zoomTimer.Stop();
    zoomTimer.Interval = delay < TimeSpan.FromMilliseconds(50)
        ? TimeSpan.FromMilliseconds(50) : delay;
    zoomTimer.Start();
  }

  private bool suitable(ChartFrame frame, PlotViewport view) {
    var input = plotSurface.input;
    // SVG strokes need no pixel-density test; the 2048 segments still need refinement.
    return input is not null && frame.expression == input.formula.expression
        && frame.includesDerivative == (input.mode != SearchMode.ROOT)
        && frame.view.contains(view)
        && (frame.view.right - frame.view.left) / 2048
            <= (view.right - view.left) / Math.Max(1, plotSurface.plotArea.Width) * 2;
  }

  private bool useCachedFrame(PlotViewport view) {
    for (var node = frames.First; node is not null; node = node.Next) {
      if (!suitable(node.Value, view)) {
        continue;
      }
      var frame = node.Value;
      frames.Remove(node);
      frames.AddFirst(frame);
      plotSurface.setFrame(frame);
      plotProgressText.Text = "";
      graphError = "";
      updateErrors();
      return true;
    }
    return false;
  }

  private async Task refreshGraphAsync() {
    var input = plotSurface.input;
    var view = plotSurface.view;
    if (isClosed || input is null || view is null || isBusy) {
      return;
    }
    if (plotSurface.isDragging) {
      scheduleGraph(TimeSpan.FromMilliseconds(750));
      return;
    }
    if (useCachedFrame(view)) {
      return;
    }
    if (graphOperation is not null) {
      return;
    }
    if (nextRequestAt > DateTimeOffset.UtcNow) {
      scheduleGraph(nextRequestAt - DateTimeOffset.UtcNow);
      return;
    }

    var current = new CancellationTokenSource();
    graphOperation = current;
    int version = viewVersion;
    var bounds = view.zoom(1.5, 0.5, 0.5);
    double ratio = plotSurface.plotArea.Width / plotSurface.plotArea.Height;
    int width = ratio >= 1 ? 3000 : (int)Math.Clamp(3000 * ratio, 300, 3000);
    int height = ratio <= 1 ? 3000 : (int)Math.Clamp(3000 / ratio, 300, 3000);
    plotProgressText.Text = "Обновляется график…";
    nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(2);
    bool retry = false;
    try {
      byte[] svg = await charts.renderAsync(input, bounds, width, height, current.Token);
      var frame = await Task.Run(() => ChartFrame.parse(svg, bounds) with {
        expression = input.formula.expression,
        includesDerivative = input.mode != SearchMode.ROOT
      }, current.Token);
      current.Token.ThrowIfCancellationRequested();
      if (isClosed || !ReferenceEquals(graphOperation, current)
          || !ReferenceEquals(plotSurface.input, input)) {
        return;
      }
      frames.AddFirst(frame);
      while (frames.Sum(f => f.bytes) > 64L * 1024 * 1024) {
        frames.RemoveLast();
      }
      graphError = "";
      updateErrors();
      if (viewVersion == version) {
        plotSurface.setFrame(frame);
      }
    } catch (OperationCanceledException) when (current.IsCancellationRequested) {
      // A new calculation or closing the window invalidates this image.
    } catch (Exception error) {
      if (!isClosed && ReferenceEquals(graphOperation, current)) {
        graphError = errorMessage(error);
        updateErrors();
        if (error is ServiceException { retryAfter: TimeSpan wait }) {
          nextRequestAt = DateTimeOffset.UtcNow + wait;
          retry = !rateRetryUsed;
          rateRetryUsed = true;
        }
      }
    } finally {
      if (ReferenceEquals(graphOperation, current)) {
        graphOperation = null;
        plotProgressText.Text = "";
        if (retry || viewVersion != version) {
          scheduleGraph(nextRequestAt > DateTimeOffset.UtcNow
        ? nextRequestAt - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(750));
        }
      }
      current.Dispose();
    }
  }

  private void showStep(int target) {
    selectedStep = steps.Count > 0 ? Math.Clamp(target, 0, steps.Count - 1) : -1;
    plotSurface.showStep(selectedStep);
    stepText.Text = selectedStep >= 0 ? steps.Count == 1 ? "Начальная / конечная точка"
        : $"Шаг {selectedStep} из {steps.Count - 1}" : "Шаг —";
    stepText.ToolTip = null;
    if (selectedStep >= 0 && plotSurface.input is SearchInput input) {
      var p = steps[selectedStep];
      var d = input.formula.derivatives(p.x);
      stepText.ToolTip =
          $"x = {p.x:G17}\nf(x) = {p.y:G17}\nf′(x) = {d.first:G17}\nf″(x) = {d.second:G17}";
    }
    updateNavigation();
  }

  private void onPreviousClick(object sender, RoutedEventArgs e) => showStep(selectedStep - 1);

  private void onNextClick(object sender, RoutedEventArgs e) => showStep(selectedStep + 1);

  private void onLastClick(object sender, RoutedEventArgs e) => showStep(steps.Count - 1);

  private void onPlotKeyDown(object sender, KeyEventArgs e) {
    if (isBusy || plotSurface.view is null) {
      return;
    }
    if (e.Key is Key.Add or Key.OemPlus) {
      plotSurface.zoom(0.8);
    } else if (e.Key is Key.Subtract or Key.OemMinus) {
      plotSurface.zoom(1.25);
    } else if (selectedStep >= 0) {
      int target = e.Key switch {
        Key.Left => selectedStep - 1,
        Key.Right => selectedStep + 1,
        Key.Home => 0,
        Key.End => steps.Count - 1,
        _ => selectedStep
      };
      if (target == selectedStep) {
        return;
      }
      showStep(target);
    } else {
      return;
    }
    e.Handled = true;
  }

  private void onClearClick(object sender, RoutedEventArgs e) {
    if (isBusy) {
      return;
    }
    clearState();
    isInitializing = true;
    aText.Clear();
    bText.Clear();
    digitsText.Clear();
    formulaText.Clear();
    isInitializing = false;
    statusText.Text = "Поля очищены.";
    aText.Focus();
  }

  private void onCancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();

  private void clearState() {
    zoomTimer.Stop();
    operation?.Cancel();
    operation = null;
    graphOperation?.Cancel();
    graphOperation = null;
    viewVersion++;
    plotSurface.clear();
    steps = Array.Empty<NewtonStep>();
    selectedStep = -1;
    calculationError = graphError = "";
    resultText.Text = statusText.Text = plotProgressText.Text = "";
    resultText.ToolTip = null;
    updateErrors();
    emptyPlotText.Visibility = Visibility.Visible;
    stepText.Text = "Шаг —";
    setBusy(false);
  }

  private void setBusy(bool busy) {
    isBusy = busy;
    solveButton.IsEnabled = clearButton.IsEnabled = !busy;
    aText.IsEnabled = bText.IsEnabled = digitsText.IsEnabled = formulaText.IsEnabled = !busy;
    modeBox.IsEnabled = !busy;
    cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    updateNavigation();
  }

  private void updateNavigation() {
    bool available = !isBusy && selectedStep >= 0 && steps.Count > 0;
    previousButton.IsEnabled = available && selectedStep > 0;
    nextButton.IsEnabled = lastButton.IsEnabled = available && selectedStep + 1 < steps.Count;
  }

  private SearchInput readInput() {
    double a = parseNumber(aText.Text, "a");
    double b = parseNumber(bText.Text, "b");
    if (a >= b) {
      throw new InputException("Нужно a < b.");
    }
    if (!double.IsFinite(b - a)) {
      throw new InputException("Слишком большой интервал.");
    }
    double sampleStep = (b - a) / 256;
    if (sampleStep == 0 || a + sampleStep <= a || b - sampleStep >= b) {
      throw new InputException("Слишком узкий интервал.");
    }
    if (!int.TryParse(digitsText.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
            out int digits) || digits is < 0 or > 15) {
      throw new InputException("Знаков после запятой: целое число от 0 до 15.");
    }
    var formula = FormulaInput.compile(formulaText.Text);
    if (modeBox.SelectedIndex is < 0 or > 2) {
      throw new InputException("Выберите, что найти.");
    }
    return new SearchInput(a, b, Math.Pow(10, -digits), digits, formula,
            (SearchMode)modeBox.SelectedIndex);
  }

  private static double parseNumber(string value, string name) {
    string normalized = value.Trim().Replace(',', '.');
    if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
        out double number) || !double.IsFinite(number)) {
      throw new InputException($"Поле {name}: введите число.");
    }
    return number;
  }

  private static string formatResult(double value, int digits) {
    if (value == Math.Truncate(value)) {
      return (value == 0 ? 0d : value).ToString("F0", CultureInfo.CurrentCulture);
    }
    double rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
    return rounded == 0 ? "0" : rounded.ToString("F" + digits, CultureInfo.CurrentCulture);
  }

  private void updateErrors() => errorText.Text = string.Join("\n",
      new[] { calculationError, graphError }.Where(s => s.Length > 0));

  private static string errorMessage(Exception error) => error is OperationCanceledException
      ? "Операция отменена." : error is InputException or ServiceException
          ? error.Message : "Ошибка операции.";

  private void onWindowClosed(object? sender, EventArgs e) {
    isClosed = true;
    zoomTimer.Stop();
    operation?.Cancel();
    graphOperation?.Cancel();
    operation = graphOperation = null;
    frames.Clear();
  }
}
