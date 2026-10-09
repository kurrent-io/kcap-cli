namespace Capacitor.Cli.Core.Tests.Unit;

public class SpawnTypesTests {
    [Test]
    [Arguments("daemon", SpawnTypeReading.Positive)]
    [Arguments("interactive", SpawnTypeReading.Positive)]
    [Arguments("adaptive", SpawnTypeReading.BackgroundBand)]
    [Arguments("background", SpawnTypeReading.BackgroundBand)]
    [Arguments("app", SpawnTypeReading.Unknown)]
    [Arguments("", SpawnTypeReading.Unknown)]
    [Arguments(null, SpawnTypeReading.Unknown)]
    public async Task Classify_maps_each_word(string? word, SpawnTypeReading expected) {
        await Assert.That(SpawnTypes.Classify(word)).IsEqualTo(expected);
    }

    [Test]
    public async Task Only_positive_words_are_positive() {
        await Assert.That(SpawnTypes.IsPositive("daemon")).IsTrue();
        await Assert.That(SpawnTypes.IsPositive("adaptive")).IsFalse();
        await Assert.That(SpawnTypes.IsPositive(null)).IsFalse();
        await Assert.That(SpawnTypes.IsBackgroundBand("background")).IsTrue();
        await Assert.That(SpawnTypes.IsBackgroundBand("daemon")).IsFalse();
    }
}
