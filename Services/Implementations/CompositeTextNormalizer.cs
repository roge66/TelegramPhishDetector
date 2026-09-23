namespace TelegramPhishDetector.Services.Implementations;

public class CompositeTextNormalizer : ITextNormalizer
{
    private readonly IHomoglyphNormalizer _homoglyph;
    private readonly ITranslitNormalizer _translit;

    public CompositeTextNormalizer(IHomoglyphNormalizer homoglyph, ITranslitNormalizer translit)
    {
        _homoglyph = homoglyph;
        _translit = translit;
    }

    public string Normalize(string text)
    {
        var step1 = _homoglyph.Normalize(text);
        var step2 = _translit.Normalize(step1);
        return step2;
    }
}