namespace WpfApp2;

/// <summary>Данные одного поиска: границы, точность, формула и выбранная задача.</summary>
public sealed record SearchInput(double a, double b, double epsilon, int digits,
    CompiledFormula formula, SearchMode mode = SearchMode.ROOT);
