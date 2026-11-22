namespace WpfApp2;

public sealed record NewtonStep(double x, double y, string note = "Шаг Ньютона",
    double? angle = null);
