namespace HanYeong.Core;

/// <summary>Standard 2-set (dubeolsik) keyboard layout: Latin key ↔ jamo.</summary>
public static class Dubeolsik
{
    const string Keys = "qwertyuiopasdfghjklzxcvbnm";
    const string Jamos = "ㅂㅈㄷㄱㅅㅛㅕㅑㅐㅔㅁㄴㅇㄹㅎㅗㅓㅏㅣㅋㅌㅊㅍㅠㅜㅡ";
    const string ShiftKeys = "QWERTOP";
    const string ShiftJamos = "ㅃㅉㄸㄲㅆㅒㅖ";

    static readonly Dictionary<char, char> KeyToJamo = new();
    static readonly Dictionary<char, char> JamoToKey = new();

    static Dubeolsik()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            KeyToJamo[Keys[i]] = Jamos[i];
            // The Korean IME ignores Shift on keys without a shifted jamo (Shift+A is still ㅁ).
            KeyToJamo[char.ToUpperInvariant(Keys[i])] = Jamos[i];
            JamoToKey[Jamos[i]] = Keys[i];
        }
        for (int i = 0; i < ShiftKeys.Length; i++)
        {
            KeyToJamo[ShiftKeys[i]] = ShiftJamos[i];
            JamoToKey[ShiftJamos[i]] = ShiftKeys[i];
        }
    }

    public static char? ToJamo(char key) => KeyToJamo.TryGetValue(key, out var j) ? j : null;

    /// <summary>Key for a simple jamo; null for compound vowels/clusters (use <see cref="Jamo.Split"/> first).</summary>
    public static char? ToKey(char jamo) => JamoToKey.TryGetValue(jamo, out var k) ? k : null;
}
