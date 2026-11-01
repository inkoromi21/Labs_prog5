namespace WpfApp2;

/// <summary>Найденная точка, значение функции и границы последнего шага Ньютона.</summary>
public sealed record SearchResult(double x, double y, double left, double right);
