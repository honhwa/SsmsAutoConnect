using System;
using System.Text;

namespace SsmsAutoConnect.Protect
{
    /// <summary>
    /// Encrypts a SQL-login password (DPAPI, CurrentUser) for the &lt;Password&gt; element of
    /// %AppData%\SsmsAutoConnect\connections.xml. Run it as the same Windows user that runs SSMS.
    ///   SsmsAutoConnect.Protect.exe            (prompts, input hidden)
    ///   SsmsAutoConnect.Protect.exe --verify &lt;blob&gt;
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--verify")
                {
                    PasswordProtector.Unprotect(args[1]);
                    Console.WriteLine("OK: blob decrypts for this Windows user.");
                    return 0;
                }
                if (args.Length > 0)
                {
                    Console.Error.WriteLine("Usage: SsmsAutoConnect.Protect.exe [--verify <blob>]");
                    return 2;
                }

                string password = ReadHidden("Password: ");
                if (password != ReadHidden("Repeat:   "))
                {
                    Console.Error.WriteLine("Passwords don't match.");
                    return 1;
                }
                Console.WriteLine();
                Console.WriteLine("Paste into <Password>...</Password>:");
                Console.WriteLine(PasswordProtector.Protect(password));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static string ReadHidden(string prompt)
        {
            Console.Write(prompt);
            if (Console.IsInputRedirected)
                return Console.ReadLine() ?? string.Empty;

            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                    sb.Append(key.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }
    }
}
