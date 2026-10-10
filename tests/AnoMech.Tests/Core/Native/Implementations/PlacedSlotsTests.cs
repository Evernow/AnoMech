using AnoMech.Core.Native.Implementations;

namespace AnoMech.Tests;

public class PlacedSlotsTests
{
    [Test]
    public void SlotNeverWrittenIsNotReleased()
        => Assert.That(new PlacedSlots<int>(8).Release(3, 0), Is.False);

    [Test]
    public void SlotStillHoldingWhatWasWrittenIsReleasedOnce()
    {
        var slots = new PlacedSlots<int>(8);
        slots.Note(3, 42);
        Assert.Multiple(() =>
        {
            Assert.That(slots.Release(3, 42), Is.True);
            Assert.That(slots.Release(3, 42), Is.False);
        });
    }

    [Test]
    public void SlotTheGameRewroteIsNotReleased()
    {
        var slots = new PlacedSlots<int>(8);
        slots.Note(3, 42);
        Assert.Multiple(() =>
        {
            Assert.That(slots.Release(3, 7), Is.False);
            Assert.That(slots.Release(3, 42), Is.False);
        });
    }
}
