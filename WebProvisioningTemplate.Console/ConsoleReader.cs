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

        public static object GetEnumValue(string label, Type enumType)
        {
            System.Console.WriteLine(label);
            var names = Enum.GetNames(enumType);

            for (int i = 0; i < names.Length; i++)
                System.Console.WriteLine($"{i + 1}:\t{names[i]}");
            System.Console.Write("> ");

            while (true)
            {
                var keyInfo = System.Console.ReadKey(true);
                int index = keyInfo.KeyChar - '1';

                if (index >= names.Length || index < 0)
                {
                    System.Console.WriteLine("Invalid key. Try again:\r\n> ");
                    continue;
                }

                var result = Enum.Parse(enumType, names[index]);

                System.Console.WriteLine(result);
                System.Console.WriteLine();

                return result;
            }
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