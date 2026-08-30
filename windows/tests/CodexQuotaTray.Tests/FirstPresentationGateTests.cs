using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class FirstPresentationGateTests
{
    [TestMethod]
    public async Task FirstPresentationRunsGateOnceAndSubsequentShowBypassesIt()
    {
        var gate = new FirstPresentationGate();
        var events = new List<string>();

        var first = await PresentAsync(gate, events);
        var second = await PresentAsync(gate, events);

        Assert.AreEqual(FirstPresentationOutcome.First, first);
        Assert.AreEqual(FirstPresentationOutcome.Bypassed, second);
        CollectionAssert.AreEqual(
            new[] { "cloak", "present", "ready", "uncloak", "revealed", "present", "revealed" },
            events);
    }

    [TestMethod]
    public async Task ConcurrentShowWhileFirstPresentationRunsIsCoalesced()
    {
        var gate = new FirstPresentationGate();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentCount = 0;
        var first = gate.PresentAsync(
            _ => true,
            () => presentCount++,
            _ => ready.Task,
            () => true,
            () => Assert.Fail("The visible first presentation should not hide."),
            () => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var concurrent = await gate.PresentAsync(
            _ => true,
            () => presentCount++,
            _ => Task.CompletedTask,
            () => true,
            () => { },
            () => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        ready.SetResult();

        Assert.AreEqual(FirstPresentationOutcome.Coalesced, concurrent);
        Assert.AreEqual(FirstPresentationOutcome.First, await first);
        Assert.AreEqual(1, presentCount);
    }

    [TestMethod]
    public async Task ReadinessFailureAlwaysUncloaks()
    {
        var gate = new FirstPresentationGate();
        var cloaked = true;
        var revealed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.PresentAsync(
            value => { cloaked = value; return true; },
            () => { },
            _ => throw new InvalidOperationException("rendering failed"),
            () => true,
            () => { },
            () => revealed = true,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        Assert.IsFalse(cloaked);
        Assert.IsTrue(revealed);
    }

    [TestMethod]
    public async Task ReadinessTimeoutAlwaysUncloaks()
    {
        var gate = new FirstPresentationGate();
        var cloaked = true;

        var outcome = await gate.PresentAsync(
            value => { cloaked = value; return true; },
            () => { },
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            () => true,
            () => { },
            () => { },
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.AreEqual(FirstPresentationOutcome.First, outcome);
        Assert.IsFalse(cloaked);
    }

    [TestMethod]
    public async Task ExitCancellationKeepsWindowHiddenBeforeUncloak()
    {
        var gate = new FirstPresentationGate();
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await gate.PresentAsync(
            value => { events.Add(value ? "cloak" : "uncloak"); return true; },
            () => events.Add("present"),
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => false,
            () => events.Add("hide"),
            () => events.Add("revealed"),
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.AreEqual(FirstPresentationOutcome.First, outcome);
        CollectionAssert.AreEqual(new[] { "cloak", "present", "hide", "uncloak" }, events);
    }

    [TestMethod]
    public async Task HideFailureStillUncloaksAndCompletesTheGate()
    {
        var gate = new FirstPresentationGate();
        var cloaked = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.PresentAsync(
            value => { cloaked = value; return true; },
            () => { },
            _ => Task.CompletedTask,
            () => false,
            () => throw new InvalidOperationException("hide failed"),
            () => Assert.Fail("An exiting presentation must not be revealed."),
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        Assert.IsFalse(cloaked);
        var followUp = await gate.PresentAsync(
            _ => true,
            () => Assert.Fail("A completed hidden gate must not present."),
            _ => Task.CompletedTask,
            () => false,
            () => { },
            () => Assert.Fail("A completed hidden gate must not reveal."),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.AreEqual(FirstPresentationOutcome.Bypassed, followUp);
    }

    private static Task<FirstPresentationOutcome> PresentAsync(
        FirstPresentationGate gate,
        ICollection<string> events) =>
        gate.PresentAsync(
            value => { events.Add(value ? "cloak" : "uncloak"); return true; },
            () => events.Add("present"),
            _ => { events.Add("ready"); return Task.CompletedTask; },
            () => true,
            () => events.Add("hide"),
            () => events.Add("revealed"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
}
