using System.Text;

namespace TelegramPhishDetector.Services.Implementations;

public class HomoglyphNormalizer : IHomoglyphNormalizer
{
    private static readonly Dictionary<char, char> _homoglyphMap = new()
    {
        /* Кириллица -> латиница (обман)
        {'а', 'a'}, {'е', 'e'}, {'р', 'p'}, {'о', 'o'}, {'с', 'c'}, {'х', 'x'},
        {'А', 'A'}, {'Е', 'E'}, {'Р', 'P'}, {'О', 'O'}, {'С', 'C'}, {'Х', 'X'},*/
        // Латиница -> латиница (замена цифрами)
        {'0', 'o'}, {'1', 'l'}, {'3', 'e'}, {'4', 'a'}, {'5', 's'}, {'6', 'b'},
        {'7', 't'}, {'8', 'b'}, {'9', 'g'},
        // Спецсимволы
        {'І', 'I'}, {'і', 'i'}, {'|', 'l'}, {'!', 'i'}, {'@', 'a'},
    };

    public string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(_homoglyphMap.ContainsKey(c) ? _homoglyphMap[c] : c);
        }
        return sb.ToString();
    }
}