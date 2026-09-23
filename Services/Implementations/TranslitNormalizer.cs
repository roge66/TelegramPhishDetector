using System.Text;

namespace TelegramPhishDetector.Services.Implementations;

public class TranslitNormalizer : ITranslitNormalizer
{
    /// <summary>
    /// Пары «латиница → кириллица»: сначала многосимвольные шаблоны, затем одиночные буквы.
    /// </summary>
    private static readonly (string From, string To)[] Transliterations =
    [
        ("shh", "щ"),
        ("shch", "щ"),
        ("sch", "щ"),
        ("zh", "ж"),
        ("kh", "х"),
        ("ts", "ц"),
        ("ch", "ч"),
        ("sh", "ш"),
        ("yo", "ё"),
        ("yu", "ю"),
        ("ya", "я"),
        ("a", "а"),
        ("b", "б"),
        ("v", "в"),
        ("g", "г"),
        ("d", "д"),
        ("e", "е"),
        ("z", "з"),
        ("i", "и"),
        ("j", "й"),
        ("y", "ы"),
        ("k", "к"),
        ("l", "л"),
        ("m", "м"),
        ("n", "н"),
        ("o", "о"),
        ("p", "п"),
        ("r", "р"),
        ("s", "с"),
        ("t", "т"),
        ("u", "у"),
        ("f", "ф"),
        ("h", "х"),
        ("w", "в"),
        ("x", "кс"),
        ("q", "к"),
    ];

    public string Normalize(string text)
    {
        text = text.ToLowerInvariant();
        var sb = new StringBuilder(text);
        foreach (var (from, to) in Transliterations)
            sb.Replace(from, to);
        return sb.ToString();
    }
}
