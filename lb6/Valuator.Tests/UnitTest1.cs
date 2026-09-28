using Valuator.Shared;
using RankCalculator;
using StackExchange.Redis;

namespace Valuator.Tests;

public class TextEvaluatorTests
{
    [Theory]
    [InlineData("abcXYZабвЯЁё", 0)]
    [InlineData("123 !\n", 1)]
    [InlineData("Hello, мир!", 0.2727272727272727)]
    [InlineData(" ", 1)]
    [InlineData("é中", 1)]
    [InlineData("a😀", 0.5)]
    public void CountsOnlyRussianAndLatinLetters(string text, double expected)
        => Assert.Equal(expected, TextEvaluator.CalculateRank(text), 12);

    [Fact]
    public void RejectsEmptyText()
        => Assert.Throws<ArgumentException>(() => TextEvaluator.CalculateRank(""));

    [Fact]
    public void RejectsNullText()
        => Assert.Throws<ArgumentNullException>(() => TextEvaluator.CalculateRank(null!));
}
