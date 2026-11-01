using System.Globalization;
using System.Text;

namespace WpfApp2;

/// <summary>Проверяет текст и собирает формулу из разрешённых математических операций.</summary>
public static class FormulaInput {
  private static readonly Dictionary<string, string> functions = new(
      StringComparer.OrdinalIgnoreCase) {
    ["sin"] = "sin",
    ["cos"] = "cos",
    ["tan"] = "tan",
    ["sqrt"] = "sqrt",
    ["ln"] = "ln",
    ["log"] = "log",
    ["exp"] = "exp",
    ["abs"] = "abs"
  };

  /// <summary>Проверяет запись формулы, числа, скобки и названия функций.</summary>
  public static string normalize(string source) {
    if (string.IsNullOrWhiteSpace(source)) {
      throw new InputException("Введите формулу f(x).");
    }

    if (source.Length > 200) {
      throw new InputException("Формула слишком длинная (не более 200 символов).");
    }

    var result = new StringBuilder();
    var brackets = new Stack<char>();
    bool expectValue = true;
    int tokens = 0;
    for (int i = 0; i < source.Length;) {
      char c = source[i];
      if (char.IsWhiteSpace(c)) {
        i++;
        continue;
      }

      if (++tokens > 128) {
        throw new InputException("Формула слишком сложная (не более 128 элементов).");
      }

      if (isDigit(c) || c is '.' or ',') {
        if (!expectValue) {
          throw new InputException("Между значениями нужен знак операции, например *.");
        }

        int start = i;
        bool digitSeen = false;
        while (i < source.Length && isDigit(source[i])) {
          digitSeen = true;
          i++;
        }

        if (i < source.Length && source[i] is '.' or ',') {
          i++;
          while (i < source.Length && isDigit(source[i])) {
            digitSeen = true;
            i++;
          }
        }

        if (!digitSeen) {
          throw new InputException("Неверная запись числа в формуле.");
        }

        if (i < source.Length && source[i] is 'e' or 'E') {
          int exponentStart = i++;
          if (i < source.Length && source[i] is '+' or '-') {
            i++;
          }

          int exponentDigits = i;
          while (i < source.Length && isDigit(source[i])) {
            i++;
          }

          if (i == exponentDigits) {
            i = exponentStart;
          }
        }

        string number = source[start..i].Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double value) || !double.IsFinite(value)) {
          throw new InputException("В формуле есть недопустимое число.");
        }

        result.Append(value.ToString("G17", CultureInfo.InvariantCulture));
        expectValue = false;
        continue;
      }

      if (isLetter(c)) {
        if (!expectValue) {
          throw new InputException("Между значениями нужен знак операции, например *.");
        }

        int start = i;
        while (i < source.Length && isLetter(source[i])) {
          i++;
        }

        string name = source[start..i];
        if (name.Equals("x", StringComparison.OrdinalIgnoreCase)) {
          result.Append('x');
        } else if (name.Equals("pi", StringComparison.OrdinalIgnoreCase)) {
          result.Append("pi");
        } else if (name.Equals("e", StringComparison.OrdinalIgnoreCase)) {
          result.Append('e');
        } else if (functions.TryGetValue(name, out string? function)) {
          while (i < source.Length && char.IsWhiteSpace(source[i])) {
            i++;
          }

          if (i >= source.Length || source[i] != '(') {
            throw new InputException($"После {name} нужны скобки, например {name}(x).");
          }

          if (brackets.Count >= 32) {
            throw new InputException("Слишком много вложенных скобок.");
          }

          result.Append(function).Append('(');
          brackets.Push(')');
          i++;
          expectValue = true;
          continue;
        } else {
          throw new InputException($"«{name}» не поддерживается.");
        }

        expectValue = false;
        continue;
      }

      i++;
      if (c == '(') {
        if (!expectValue) {
          throw new InputException("Перед скобкой нужен знак операции, например *.");
        }

        if (brackets.Count >= 32) {
          throw new InputException("Слишком много вложенных скобок.");
        }

        result.Append('(');
        brackets.Push(')');
      } else if (c == ')') {
        if (brackets.Count == 0) {
          throw new InputException($"Лишняя закрывающая скобка в позиции {i}.");
        }

        if (expectValue) {
          throw new InputException($"Перед закрывающей скобкой в позиции {i} нет значения.");
        }

        result.Append(brackets.Pop());
        expectValue = false;
      } else if (c is '+' or '-') {
        result.Append(c);
        expectValue = true;
      } else if (c is '*' or '/' or '^') {
        if (expectValue) {
          throw new InputException($"Перед «{c}» должно быть значение.");
        }

        result.Append(c);
        expectValue = true;
      } else {
        throw new InputException($"Символ «{c}» в формуле не поддерживается.");
      }
    }

    if (brackets.Count != 0) {
      throw new InputException("В формуле не закрыта скобка.");
    }

    if (expectValue) {
      throw new InputException("Формула обрывается после знака операции.");
    }

    return result.ToString();
  }

  private static bool isDigit(char c) {
    return c is >= '0' and <= '9';
  }

  private static bool isLetter(char c) {
    return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
  }

  /// <summary>Разбирает проверенную строку в функцию evaluate(x).</summary>
  public static CompiledFormula compile(string source) {
    string expression = normalize(source);
    return new CompiledFormula(expression, new Parser(expression).parse());
  }

  /// <summary>Рекурсивный разбор с приоритетами операций.</summary>
  private sealed class Parser(string source) {
    private int position;

    public Func<double, double> parse() {
      var formula = addSubtract();
      skipSpaces();
      if (position != source.Length) {
        throw new InputException("Проверьте знаки операций в формуле.");
      }

      return formula;
    }

    private Func<double, double> addSubtract() {
      var left = multiplyDivide();
      while (true) {
        if (take('+')) {
          var previous = left;
          var right = multiplyDivide();
          left = x => previous(x) + right(x);
        } else if (take('-')) {
          var previous = left;
          var right = multiplyDivide();
          left = x => previous(x) - right(x);
        } else {
          return left;
        }
      }
    }

    private Func<double, double> multiplyDivide() {
      var left = unary();
      while (true) {
        if (take('*')) {
          var previous = left;
          var right = unary();
          left = x => previous(x) * right(x);
        } else if (take('/')) {
          var previous = left;
          var right = unary();
          left = x => previous(x) / right(x);
        } else {
          return left;
        }
      }
    }

    private Func<double, double> unary() {
      if (take('+')) {
        return unary();
      }

      if (take('-')) {
        var operand = unary();
        return x => -operand(x);
      }

      return power();
    }

    private Func<double, double> power() {
      var left = primary();
      if (!take('^')) {
        return left;
      }

      var right = unary();
      return x => Math.Pow(left(x), right(x));
    }

    private Func<double, double> primary() {
      skipSpaces();
      if (take('(')) {
        var inside = addSubtract();
        expect(')');
        return inside;
      }

      if (position < source.Length && (isDigit(source[position]) || source[position] == '.')) {
        int start = position;
        while (position < source.Length && isDigit(source[position])) {
          position++;
        }

        if (position < source.Length && source[position] == '.') {
          position++;
          while (position < source.Length && isDigit(source[position])) {
            position++;
          }
        }

        if (position < source.Length && source[position] is 'e' or 'E') {
          position++;
          if (position < source.Length && source[position] is '+' or '-') {
            position++;
          }

          while (position < source.Length && isDigit(source[position])) {
            position++;
          }
        }

        if (!double.TryParse(source[start..position], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number)) {
          throw new InputException("Неверная запись числа в формуле.");
        }

        return _ => number;
      }

      if (position < source.Length && isLetter(source[position])) {
        int start = position;
        while (position < source.Length && isLetter(source[position])) {
          position++;
        }

        string name = source[start..position];
        if (name == "x") {
          return x => x;
        }

        if (name == "pi") {
          return _ => Math.PI;
        }

        if (name == "e") {
          return _ => Math.E;
        }

        expect('(');
        var argument = addSubtract();
        expect(')');
        return name switch {
          "sin" => x => Math.Sin(argument(x)),
          "cos" => x => Math.Cos(argument(x)),
          "tan" => x => Math.Tan(argument(x)),
          "sqrt" => x => Math.Sqrt(argument(x)),
          "ln" => x => Math.Log(argument(x)),
          "log" => x => Math.Log10(argument(x)),
          "exp" => x => Math.Exp(argument(x)),
          "abs" => x => Math.Abs(argument(x)),
          _ => throw new InputException($"Неизвестная функция «{name}».")
        };
      }

      throw new InputException("Проверьте формулу и скобки.");
    }

    private bool take(char value) {
      skipSpaces();
      if (position >= source.Length || source[position] != value) {
        return false;
      }

      position++;
      return true;
    }

    private void expect(char value) {
      if (!take(value)) {
        throw new InputException("Проверьте формулу и скобки.");
      }
    }

    private void skipSpaces() {
      while (position < source.Length && char.IsWhiteSpace(source[position])) {
        position++;
      }
    }
  }
}
