using System.Globalization;
using System.Text;

namespace SaveFileSync.Metadata;

/// <summary>Compares game names written differently ("The Witcher 3: Wild Hunt" / "Witcher 3 - Wild Hunt").</summary>
public static class NameMatcher
{
    /// <summary>Lower-case letters and digits only, accents removed, "™/®" and a leading "the" dropped.</summary>
    public static string Normalize(string name)
    {
        // Trademark signs would decompose into the letters "TM"; drop them first.
        var decomposed = name.Replace("™", "").Replace("®", "").Replace("©", "").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c) || c is '-' or ':' or '_' or '/' or '.')
            {
                sb.Append(' ');
            }
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Concat(words);
    }

    /// <summary>0 (nothing in common) … 1 (same name once normalized).</summary>
    public static double Similarity(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        if (x == y)
        {
            return 1;
        }

        if (x.Length < 2 || y.Length < 2)
        {
            return 0;
        }

        // Dice coefficient on character bigrams.
        var bigramsX = Bigrams(x);
        var bigramsY = Bigrams(y);
        var common = 0;
        foreach (var (bigram, count) in bigramsX)
        {
            if (bigramsY.TryGetValue(bigram, out var other))
            {
                common += Math.Min(count, other);
            }
        }

        var score = 2.0 * common / ((x.Length - 1) + (y.Length - 1));

        // "Resident Evil 2" and "Resident Evil 3", or "Hades" and "Hades II", look almost the same letter by letter
        // but are different games: names with different numbers can never reach the automatic-match threshold.
        return NumberTokens(a).SequenceEqual(NumberTokens(b)) ? score : Math.Min(score, 0.5);
    }

    /// <summary>The sequel numbers in a name ("2", "III" → 3), in order.</summary>
    internal static List<int> NumberTokens(string name)
    {
        var numbers = new List<int>();
        foreach (var raw in name.Split([' ', '-', ':', '_', '/', '.', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                numbers.Add(n);
            }
            else if (Roman(raw) is { } roman)
            {
                numbers.Add(roman);
            }
        }

        return numbers;
    }

    private static int? Roman(string token) => token switch
    {
        "II" or "ii" => 2,
        "III" or "iii" => 3,
        "IV" or "iv" => 4,
        "V" => 5,
        "VI" or "vi" => 6,
        "VII" or "vii" => 7,
        "VIII" or "viii" => 8,
        "IX" or "ix" => 9,
        "X" => 10,
        _ => null,
    };

    /// <summary>The best candidate whose similarity reaches <paramref name="threshold"/>; ties keep the earlier one.</summary>
    public static T? Best<T>(string wanted, IEnumerable<T> candidates, Func<T, IEnumerable<string>> names, double threshold)
        where T : class
    {
        T? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var score = names(candidate).Select(n => Similarity(wanted, n)).DefaultIfEmpty(0).Max();
            if (score >= threshold && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static Dictionary<string, int> Bigrams(string s)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < s.Length - 1; i++)
        {
            var key = s.Substring(i, 2);
            result[key] = result.GetValueOrDefault(key) + 1;
        }

        return result;
    }
}
