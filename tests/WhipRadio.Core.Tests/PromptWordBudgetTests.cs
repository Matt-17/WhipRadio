using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class PromptWordBudgetTests
{
    [TestMethod]
    public void WordsPerSecond_UsesLanguageDefaultsAndSpeechRate()
    {
        Assert.Equal(2.5, PromptWordBudget.WordsPerSecond("de", 1.0), precision: 10);
        Assert.Equal(2.8, PromptWordBudget.WordsPerSecond("en", 1.0), precision: 10);
        Assert.Equal(3.0, PromptWordBudget.WordsPerSecond("de-DE", 1.2), precision: 10);
    }

    [TestMethod]
    public void EstimateWordBudget_ClampsToAtLeastOneWord()
    {
        Assert.Equal(1, PromptWordBudget.EstimateWordBudget("en", 1.0, 0));
        Assert.Equal(28, PromptWordBudget.EstimateWordBudget("en", 1.0, 10));
    }

    [TestMethod]
    public void CountWords_CountsWhitespaceSeparatedWords()
    {
        Assert.Equal(0, PromptWordBudget.CountWords("  "));
        Assert.Equal(4, PromptWordBudget.CountWords("short line\nwith tabs"));
    }
}
