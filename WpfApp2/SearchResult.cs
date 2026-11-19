namespace WpfApp2;

public enum SearchOutcome {
  STOPPED,
  ROOT,
  MINIMUM,
  MAXIMUM,
  STATIONARY,
  MULTIPLE_ROOTS,
  MULTIPLE_EXTREMA,
  NO_ROOTS,
  NO_EXTREMUM
}

public enum SearchPointKind {
  UNKNOWN,
  BOUNDARY,
  MINIMUM,
  MAXIMUM,
  STATIONARY
}

public sealed record SearchResult(double x, double y) {
  public bool hasSolution => outcome is SearchOutcome.ROOT or SearchOutcome.MINIMUM
      or SearchOutcome.MAXIMUM;
  public SearchPointKind pointKind {
    get;
    init;
  }
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
