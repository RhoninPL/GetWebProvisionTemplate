using System;

namespace WebProvisioningTemplate.Console
{
    public class ConsoleReader
    {
        #region Constants

        private static readonly LineEditor _lineEditor = new LineEditor("WebProvisionTemplateDownloader");

        #endregion

        #region Public Methods

        public static string GetInput(string label = null)
        {
            System.Console.WriteLine(label);
            string value = _lineEditor.Edit(string.Empty, string.Empty);
            System.Console.WriteLine();

            return value;
        }

        public static string GetPassword(string label = null)
        {
            if (!string.IsNullOrWhiteSpace(label))
                System.Console.WriteLine(label);

            string value = string.Empty;

            for (ConsoleKeyInfo keyInfo = System.Console.ReadKey(true); keyInfo.Key != ConsoleKey.Enter; keyInfo = System.Console.ReadKey(true))
            {
                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (value.Length <= 0)
                        continue;

                    value = value.Remove(value.Length - 1);
                    System.Console.SetCursorPosition(System.Console.CursorLeft - 1, System.Console.CursorTop);
                    System.Console.Write(" ");
                    System.Console.SetCursorPosition(System.Console.CursorLeft - 1, System.Console.CursorTop);
                }
                else if (keyInfo.Key != ConsoleKey.Enter)
                {
                    System.Console.Write("*");
                    value += keyInfo.KeyChar;
                }
            }

            System.Console.WriteLine();
            System.Console.WriteLine();

            return value;
        }

        #endregion
    }
}