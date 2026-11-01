using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpfApp2;

/// <summary>Управляет вводом, запуском расчёта и показом ответа с графиком.</summary>
public partial class MainWindow : Window {
  private readonly ChartServiceClient charts = new();
  private readonly NewtonSearch finder = new();
  private bool isBusy;
  private bool isInitializing = true;
  private SearchResult? searchResult;
  private CancellationTokenSource? operation;

  private (double a, double b, string expression)? sampleKey;
  private double?[]? cachedSamples;

  public MainWindow() {
    InitializeComponent();
    isInitializing = false;
  }

  private void onInputChanged(object sender, TextChangedEventArgs e) {
    if (isInitializing || resultText is null) {
      return;
    }

    sampleKey = null;
    cachedSamples = null;
    searchResult = null;
    resultText.Text = "";
    plotImage.Source = null;
    statusText.Foreground = Brushes.DarkSlateGray;
    statusText.Text = "Данные изменены. Постройте график или выполните расчёт заново.";
  }

  private void onModeChanged(object sender, SelectionChangedEventArgs e) {
    if (isInitializing || solveButton is null) {
      return;
    }

    solveButton.Content = modeBox.SelectedIndex switch {
      1 => "Найти минимум",
      2 => "Найти максимум",
      _ => "Найти корень"
    };
    searchResult = null;
    resultText.Text = "";
    plotImage.Source = null;
    statusText.Foreground = Brushes.DarkSlateGray;
    statusText.Text = "";
  }

  private async void onPlotClick(object sender, RoutedEventArgs e) {
    if (isBusy) {
      return;
    }

    try {
      setBusy(true);
      var input = readInput();
      statusText.Text = "Строю график…";
      var samples = getSamples(input);
      await drawPlotAsync(input, samples);
      statusText.Foreground = Brushes.DarkSlateGray;
      statusText.Text = "";
    } catch (Exception ex) {
      showError(ex);
    } finally {
      setBusy(false);
    }
  }

  /// <summary>Считает ответ локально; даже при ошибке поиска пытается показать график.</summary>
  private async void onSolveClick(object sender, RoutedEventArgs e) {
    if (isBusy) {
      return;
    }

    try {
      setBusy(true);
      searchResult = null;
      resultText.Text = "";
      plotImage.Source = null;
      var input = readInput();
      statusText.Text = "Проверяю функцию на интервале…";
      var samples = getSamples(input);
      string? searchError = null;
      try {
        var token = operation!.Token;
        var result = await Task.Run(() => finder.find(input, samples, token), token);
        searchResult = result;
        string label = input.mode switch {
          SearchMode.MINIMUM => "Минимум",
          SearchMode.MAXIMUM => "Максимум",
          _ => "Корень"
        };
        resultText.Text = $"{label}: x ≈ {formatResult(result.x, input.digits)}";
        if (input.mode != SearchMode.ROOT) {
          resultText.Text += $"\nf(x) ≈ {formatResult(result.y, input.digits)}";
        }

        statusText.Foreground = Brushes.DarkSlateGray;
        statusText.Text = "Строю график…";
      } catch (InputException error) {
        searchError = error.Message;
        statusText.Foreground = Brushes.Firebrick;
        statusText.Text = searchError;
      }

      try {
        await drawPlotAsync(input, samples);
        if (searchError is null) {
          statusText.Text = "";
        }
      } catch (OperationCanceledException) {
        statusText.Text = searchError is null ? "Построение графика отменено." : searchError
            + "\nПостроение графика отменено.";
      } catch (Exception plotError) {
        string plotMessage = "График: " + plotError.Message;
        statusText.Text = searchError is null ? plotMessage : searchError + "\n" + plotMessage;
      }
    } catch (Exception ex) {
      showError(ex);
    } finally {
      setBusy(false);
    }
  }

  /// <summary>Вычисляет 257 точек или берёт их из кеша для той же формулы и границ.</summary>
  private double?[] getSamples(SearchInput input) {
    var key = (input.a, input.b, input.formula.expression);
    if (sampleKey == key && cachedSamples is not null) {
      return cachedSamples;
    }

    var samples = new double?[257];
    for (int i = 0; i < samples.Length; i++) {
      double x = i == samples.Length - 1 ? input.b : input.a + (input.b - input.a) * i / (
          samples.Length - 1);
      double value = input.formula.evaluate(x);
      samples[i] = double.IsFinite(value) ? value : null;
    }

    sampleKey = key;
    cachedSamples = samples;
    return samples;
  }

  private async Task drawPlotAsync(SearchInput input, double?[] samples) {
    byte[] png = await charts.renderAsync(input.a, input.b, samples, searchResult,
        operation!.Token);
    var bitmap = new BitmapImage();
    using (var stream = new MemoryStream(png)) {
      bitmap.BeginInit();
      bitmap.CacheOption = BitmapCacheOption.OnLoad;
      bitmap.StreamSource = stream;
      bitmap.EndInit();
    }

    bitmap.Freeze();
    plotImage.Source = bitmap;
  }

  private void onClearClick(object sender, RoutedEventArgs e) {
    if (isBusy) {
      return;
    }

    aText.Clear();
    bText.Clear();
    digitsText.Clear();
    formulaText.Clear();
    searchResult = null;
    resultText.Text = "";
    plotImage.Source = null;
    statusText.Foreground = Brushes.DarkSlateGray;
    statusText.Text = "Поля очищены.";
  }

  private void onCancelClick(object sender, RoutedEventArgs e) {
    operation?.Cancel();
  }

  /// <summary>Читает поля и преобразует число знаков в epsilon = 10^(-digits).</summary>
  private SearchInput readInput() {
    double a = parseNumber(aText.Text, "a");
    double b = parseNumber(bText.Text, "b");
    if (a >= b) {
      throw new InputException("Нужно a < b.");
    }

    if (!double.IsFinite(b - a)) {
      throw new InputException("Слишком большой интервал.");
    }

    double sampleStep = (b - a) / 128;
    if (sampleStep == 0 || a + sampleStep <= a || b - sampleStep >= b) {
      throw new InputException("Слишком узкий интервал.");
    }

    if (!int.TryParse(digitsText.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
        out int digits) || digits is < 0 or > 15) {
      throw new InputException("Знаков после запятой: целое число от 0 до 15.");
    }

    double epsilon = Math.Pow(10, -digits);
    CompiledFormula formula = FormulaInput.compile(formulaText.Text);
    if (modeBox.SelectedIndex is < 0 or > 2) {
      throw new InputException("Выберите, что найти.");
    }

    return new SearchInput(a, b, epsilon, digits, formula, (SearchMode)modeBox.SelectedIndex);
  }

  /// <summary>Оформляет ответ, сохраняя правила вывода целых чисел и нуля.</summary>
  private static string formatResult(double value, int digits) {
    // Проверяем до округления: для 3,000000001 сохраняем заданные знаки.
    if (value == Math.Truncate(value)) {
      return (value == 0 ? 0d : value).ToString("F0", CultureInfo.CurrentCulture);
    }

    double rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
    if (rounded == 0) {
      return "0";
    }

    return rounded.ToString("F" + digits.ToString(CultureInfo.InvariantCulture),
        CultureInfo.CurrentCulture);
  }

  private static double parseNumber(string value, string name) {
    string normalized = value.Trim().Replace(',', '.');
    if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
        out double number) || !double.IsFinite(number)) {
      throw new InputException($"Поле {name}: введите число.");
    }

    return number;
  }

  private void setBusy(bool busy) {
    isBusy = busy;
    if (busy) {
      operation = new CancellationTokenSource();
    } else {
      operation?.Dispose();
      operation = null;
    }

    plotButton.IsEnabled = solveButton.IsEnabled = clearButton.IsEnabled = !busy;
    cancelButton.IsEnabled = busy;
    aText.IsEnabled = bText.IsEnabled = digitsText.IsEnabled = formulaText.IsEnabled = !busy;
    modeBox.IsEnabled = !busy;
    Mouse.OverrideCursor = busy ? Cursors.Wait : null;
  }

  private void showError(Exception error) {
    resultText.Text = "";
    statusText.Foreground = Brushes.Firebrick;
    statusText.Text
        = error is OperationCanceledException
            ? "Расчёт отменён."
            : error is InputException or ServiceException
                ? error.Message
                : "Ошибка расчёта.";
  }
}
