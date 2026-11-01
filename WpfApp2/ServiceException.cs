namespace WpfApp2;

/// <summary>Ошибка получения изображения графика.</summary>
public sealed class ServiceException(string message) : Exception(message) {
}
