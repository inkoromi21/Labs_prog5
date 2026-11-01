namespace WpfApp2;

/// <summary>Ошибка ввода или расчёта с понятным сообщением для пользователя.</summary>
public sealed class InputException(string message) : Exception(message) {
}
