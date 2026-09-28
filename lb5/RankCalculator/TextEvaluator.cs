using System.Text;
namespace RankCalculator;

public static class TextEvaluator
{
    public static double CalculateRank(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        int total = 0, nonAlphabetic = 0;
        foreach (Rune character in text.EnumerateRunes())
        {
            total++;
            if (!(character.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё'))
                nonAlphabetic++;
        }
        return (double)nonAlphabetic / total;
    }
}
