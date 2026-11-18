namespace WpfApp2;

public enum SearchOutcome {
  STOPPED,
  ROOT,
  MINIMUM,
  MAXIMUM,
  STATIONARY
}

public sealed record SearchResult(double x, double y) {
  public SearchOutcome outcome {
    get;
    init;
  }
  public string message {
    get;
    init;
  } = "";
  public IReadOnlyList<NewtonStep> steps {
    get;
    init;
  } = Array.Empty<NewtonStep>();
}
