using System.Text;

namespace HanYeong.Core;

/// <summary>
/// Incremental dubeolsik automaton that mimics the Windows Korean IME: feed it the keys
/// that were typed and it produces the text the IME would have produced.
/// Backspace inside the composing syllable removes one jamo step, like the IME does.
/// </summary>
public sealed class HangulComposer
{
    // The syllable being composed. Cho may hold a cluster (ㄳ) only while there is no vowel.
    record struct Block(char Cho, char Jung, char Jong)
    {
        public bool IsEmpty => Cho == '\0' && Jung == '\0';

        public override string ToString() =>
            Cho != '\0' && Jung != '\0' ? Jamo.Compose(Cho, Jung, Jong).ToString()
            : Cho != '\0' ? Cho.ToString()
            : Jung != '\0' ? Jung.ToString()
            : "";
    }

    readonly StringBuilder _committed = new();
    readonly Stack<Block> _history = new();
    Block _block;

    public string Text => _committed + _block.ToString();
    public string Composing => _block.ToString();
    public bool IsComposing => !_block.IsEmpty;

    public static string Compose(string keys)
    {
        var composer = new HangulComposer();
        foreach (char key in keys) composer.Type(key);
        return composer.Text;
    }

    public void Type(char key)
    {
        if (Dubeolsik.ToJamo(key) is not { } jamo)
        {
            Commit();
            _committed.Append(key);
            return;
        }
        _history.Push(_block);
        if (Jamo.IsVowel(jamo)) AddVowel(jamo);
        else AddConsonant(jamo);
    }

    public void Backspace()
    {
        if (!_block.IsEmpty)
        {
            _block = _history.Count > 0 ? _history.Pop() : default;
            return;
        }
        if (_committed.Length > 0) _committed.Length--;
    }

    /// <summary>Appends finished text that did not come from keys (e.g. text we inserted ourselves).</summary>
    public void AppendCommitted(string text)
    {
        Commit();
        _committed.Append(text);
    }

    public void Commit()
    {
        _committed.Append(_block.ToString());
        _block = default;
        _history.Clear();
    }

    void AddConsonant(char c)
    {
        var b = _block;
        if (b.IsEmpty)
        {
            _block = new Block(c, '\0', '\0');
        }
        else if (b.Jung == '\0')
        {
            // Consonants with no vowel yet: the IME joins valid clusters (r+t → ㄳ).
            if (Jamo.CombineConsonants(b.Cho, c) is { } cluster) _block = b with { Cho = cluster };
            else StartNew(new Block(c, '\0', '\0'));
        }
        else if (b.Cho == '\0')
        {
            StartNew(new Block(c, '\0', '\0'));
        }
        else if (b.Jong == '\0')
        {
            if (Jamo.CanBeFinal(c)) _block = b with { Jong = c };
            else StartNew(new Block(c, '\0', '\0'));
        }
        else if (Jamo.CombineConsonants(b.Jong, c) is { } finalCluster)
        {
            _block = b with { Jong = finalCluster };
        }
        else
        {
            StartNew(new Block(c, '\0', '\0'));
        }
    }

    void AddVowel(char v)
    {
        var b = _block;
        if (b.IsEmpty)
        {
            _block = new Block('\0', v, '\0');
        }
        else if (b.Jung == '\0')
        {
            // A lone cluster gives its second half to the new syllable: ㄳ + ㅏ → ㄱ사.
            if (Jamo.Split(b.Cho) is var (first, second))
            {
                _block = new Block(first, '\0', '\0');
                StartNew(new Block(second, v, '\0'), carried: new Block(second, '\0', '\0'));
            }
            else
            {
                _block = b with { Jung = v };
            }
        }
        else if (b.Jong == '\0')
        {
            if (Jamo.CombineVowels(b.Jung, v) is { } compound) _block = b with { Jung = compound };
            else StartNew(new Block('\0', v, '\0'));
        }
        else
        {
            // The final consonant moves to the new syllable: 각 + ㅏ → 가가, 닭 + ㅏ → 달가.
            var (keep, move) = Jamo.Split(b.Jong) ?? ('\0', b.Jong);
            _block = b with { Jong = keep };
            StartNew(new Block(move, v, '\0'), carried: new Block(move, '\0', '\0'));
        }
    }

    // `carried` is what Backspace returns to when a consonant moved over into the new syllable.
    void StartNew(Block next, Block? carried = null)
    {
        _committed.Append(_block.ToString());
        _history.Clear();
        if (carried is { } c) _history.Push(c);
        _block = next;
    }
}
