namespace Hymnal.Core.Audio;

/// <summary>The kinds of keystroke that make a typing sound. Navigation never makes one.</summary>
public enum TypingSoundKind
{
    /// <summary>A printable character: the type slug striking the platen.</summary>
    Key,

    /// <summary>Space or tab: the space bar's duller thunk and the escapement.</summary>
    Space,

    /// <summary>A new line: the carriage return.</summary>
    Return,

    /// <summary>Backspace, Delete, or a word delete: the carriage stepping back.</summary>
    Backspace,
}

/// <summary>Decides which typing sound, if any, a piece of typed text makes.</summary>
public static class TypingSoundClassifier
{
    /// <summary>
    /// The sound for text the user typed (a text-input event). Null for empty text or control characters
    /// other than tab and new lines, which make no sound.
    /// </summary>
    public static TypingSoundKind? ForTypedText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Contains('\n') || text.Contains('\r'))
            return TypingSoundKind.Return;

        var allWhitespace = true;
        foreach (var c in text)
        {
            if (char.IsControl(c) && c != '\t')
                return null;
            if (!char.IsWhiteSpace(c))
                allWhitespace = false;
        }

        return allWhitespace ? TypingSoundKind.Space : TypingSoundKind.Key;
    }
}
