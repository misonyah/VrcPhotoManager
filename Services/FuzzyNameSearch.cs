using System.Globalization;
using System.Text;

namespace VrcPhotoManager.Services;

/// <summary>
/// VRChat display names commonly use "fancy text generator" Unicode styling that reads as
/// plain Latin letters to a human but has entirely different codepoints from ASCII, so a
/// literal Contains() search never matches it (found via a real report: a friend's actual
/// display name "ᴀʟᴛɪ x." - Latin Small Capital letters, U+1D00 block - never autocompleted
/// under any typed spelling). A survey of this account's own VRCX friends list (886 names, 105
/// with non-ASCII characters) shaped what's handled here vs. left alone:
///
/// - Genuine accented Latin/Greek/Cyrillic letters (é, ñ, ō, ...) decompose via NFKD into a
///   base letter + combining accent, so stripping combining marks after NFKD normalization
///   handles all of these generically - no per-letter table needed.
/// - Latin small capitals (Phonetic/IPA Extensions block) have NO Unicode decomposition
///   mapping, so NFKD doesn't touch them - needs a manual table.
/// - A handful of Cyrillic/Greek letters are visually near-identical to a single Latin letter
///   and get used the same way (found live: a friend named "Вathsalts" uses Cyrillic В, not
///   Latin B) - mapped by visual resemblance, not the Unicode character's actual name (e.g.
///   Cyrillic 'р', named CYRILLIC SMALL LETTER ER, looks like Latin 'p', not 'r').
/// - Genuinely different scripts (Japanese, Thai, CJK, etc. - also common in the same survey)
///   are deliberately left untouched: they're real text, not ASCII stylization, and forcing
///   them through a Latin-lookalike table would produce nonsense matches.
///
/// This is scoped to what's actually been observed, not an attempt at full Unicode confusables
/// coverage (that table has thousands of entries and covers spoofing-detection concerns this
/// app doesn't have). Also does NOT cover Mathematical Alphanumeric Symbols (bold/italic/
/// script/fraktur/double-struck letter styles) - that block lives above U+FFFF and needs
/// surrogate-pair-aware iteration, a bigger change than any case seen so far called for.
/// </summary>
public static class FuzzyNameSearch
{
    public static bool Matches(string candidateName, string query) =>
        Normalize(candidateName).Contains(Normalize(query), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string input)
    {
        string decomposed = input.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            // A combining accent left behind by NFKD decomposition (e.g. é -> 'e' + combining
            // acute) - drop it, the base letter it's attached to already got appended.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(NormalizeChar(c));
        }
        return sb.ToString();
    }

    private static char NormalizeChar(char c)
    {
        if (LookalikeMap.TryGetValue(c, out char mapped)) return mapped;
        if (c is >= 'Ａ' and <= 'Ｚ') return (char)(c - 0xFEE0); // fullwidth A-Z
        if (c is >= 'ａ' and <= 'ｚ') return (char)(c - 0xFEE0); // fullwidth a-z
        if (c is >= 'Ⓐ' and <= 'Ⓩ') return (char)('A' + (c - 'Ⓐ')); // circled A-Z
        if (c is >= 'ⓐ' and <= 'ⓩ') return (char)('a' + (c - 'ⓐ')); // circled a-z
        return c;
    }

    private static readonly Dictionary<char, char> LookalikeMap = new()
    {
        // Latin small capitals (Phonetic/IPA Extensions) - S and X have no distinct small-caps
        // codepoint (generators reuse plain lowercase s/x, already correct unchanged).
        ['ᴀ'] = 'A', ['ʙ'] = 'B', ['ᴄ'] = 'C', ['ᴅ'] = 'D', ['ᴇ'] = 'E', ['ꜰ'] = 'F', ['ɢ'] = 'G',
        ['ʜ'] = 'H', ['ɪ'] = 'I', ['ᴊ'] = 'J', ['ᴋ'] = 'K', ['ʟ'] = 'L', ['ᴍ'] = 'M', ['ɴ'] = 'N',
        ['ᴏ'] = 'O', ['ᴘ'] = 'P', ['ʀ'] = 'R', ['ᴛ'] = 'T', ['ᴜ'] = 'U', ['ᴠ'] = 'V', ['ᴡ'] = 'W',
        ['ʏ'] = 'Y', ['ᴢ'] = 'Z',

        // Distinct letters (not diacritic compositions - NFKD doesn't decompose these) that
        // still read closely enough as a plain Latin letter to match on.
        ['ø'] = 'o', ['Ø'] = 'O',

        // Cyrillic/Greek Latin-lookalikes actually seen in this account's own friends list -
        // mapped by appearance, not Unicode name.
        ['а'] = 'a', ['е'] = 'e', ['к'] = 'k', ['о'] = 'o', ['р'] = 'p', ['В'] = 'B', ['Н'] = 'H',
        ['ο'] = 'o', ['Κ'] = 'K',
    };
}
