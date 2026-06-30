namespace redb.Tsak.Web.Services;

public sealed class ToastService
{
    public event Action<string, string, int>? OnShow;

    public void Show(string message, string level = "info", int durationMs = 4000)
        => OnShow?.Invoke(message, level, durationMs);

    public void Success(string message) => Show(message, "success");
    public void Error(string message) => Show(message, "error", 8000);
    public void Warning(string message) => Show(message, "warning", 6000);
}
