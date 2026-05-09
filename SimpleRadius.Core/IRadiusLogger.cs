namespace SimpleRadius.Core;

/// <summary>
/// Minimal logging interface for SimpleRadius.Core.
/// Avoids an external NuGet dependency. The GUI wires up its own implementation.
/// </summary>
public interface IRadiusLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Console implementation used for testing and standalone console hosting.</summary>
public sealed class ConsoleRadiusLogger : IRadiusLogger
{
    public void Info(string message)
        => Console.WriteLine($"[INF] {message}");

    public void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WRN] {message}");
        Console.ResetColor();
    }

    public void Error(string message, Exception? ex = null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERR] {message}{(ex != null ? $" — {ex.Message}" : "")}");
        Console.ResetColor();
    }
}
