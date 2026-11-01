namespace WpfApp2;

/// <summary>Проверенная формула и её вычисление при заданном x.</summary>
public sealed record CompiledFormula(string expression, Func<double, double> evaluate);
