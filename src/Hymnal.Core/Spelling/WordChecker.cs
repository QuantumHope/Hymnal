using WeCantSpell.Hunspell;

namespace Hymnal.Core.Spelling;

/// <summary>A dictionary that can say whether a word is spelled right and suggest fixes. Thread-safe.</summary>
public interface IWordChecker
{
    bool Check(string word);

    IEnumerable<string> Suggest(string word);
}

/// <summary>An <see cref="IWordChecker"/> over a Hunspell <c>.dic</c>/<c>.aff</c> pair (WeCantSpell.Hunspell).</summary>
public sealed class HunspellWordChecker : IWordChecker
{
    private readonly WordList _words;

    private HunspellWordChecker(WordList words)
    {
        _words = words;
    }

    /// <summary>Reads a Hunspell dictionary from its two streams. Throws if they can't be read.</summary>
    public static HunspellWordChecker FromStreams(Stream dictionary, Stream affix) =>
        new(WordList.CreateFromStreams(dictionary, affix));

    /// <summary>A checker that knows exactly <paramref name="words"/> (tests and fallbacks).</summary>
    public static HunspellWordChecker FromWords(IEnumerable<string> words) =>
        new(WordList.CreateFromWords(words));

    public bool Check(string word) => _words.Check(word);

    public IEnumerable<string> Suggest(string word) => _words.Suggest(word);
}
