namespace WpfApp2;

/// <summary>Ошибка получения изображения графика.</summary>
public sealed class ServiceException(string message, Exception? inner = null)
    : Exception(message, inner) {
  public TimeSpan? retryAfter {
    get;
    init;
  }
}
