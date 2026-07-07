using System.Text;

namespace BifrostQL.Server;

/// <summary>
/// Console helpers for interactive credential entry.
/// </summary>
public static class ConsolePrompt
{
    /// <summary>
    /// Read a password from the console with masked input.
    /// Falls back to <see cref="Console.ReadLine"/> when stdin is redirected (piped),
    /// where <see cref="Console.ReadKey"/> is not usable.
    /// </summary>
    /// <param name="prompt">Prompt text written before reading.</param>
    /// <returns>The entered password (never null).</returns>
    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }
}
