using System.Drawing;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TitleBarOverlayRevealGateTests
{
    private static readonly Rectangle Host = new(-1600, 80, 1200, 900);

    [TestMethod]
    public void FinalLogicalBoundsDoNotRevealBeforeRestoreBufferElapses()
    {
        var gate = new TitleBarOverlayRevealGate();
        Assert.IsFalse(gate.CanReveal(Host, 1000));
        Assert.AreEqual(1350L, gate.NextCheckAtMilliseconds);
        Assert.IsFalse(gate.CanReveal(Host, 1349));
        Assert.IsTrue(gate.CanReveal(Host, 1350));
        Assert.IsNull(gate.NextCheckAtMilliseconds);
    }

    [TestMethod]
    public void LateGeometryChangeWaitsForAQuietInterval()
    {
        var gate = new TitleBarOverlayRevealGate();
        Assert.IsFalse(gate.CanReveal(Host, 0));
        var finalBounds = Host with { X = -1500 };
        Assert.IsFalse(gate.CanReveal(finalBounds, 340));
        Assert.AreEqual(420L, gate.NextCheckAtMilliseconds);
        Assert.IsFalse(gate.CanReveal(finalBounds, 419));
        Assert.IsTrue(gate.CanReveal(finalBounds, 420));
    }

    [TestMethod]
    public void LocationEventsCanPostponeRevealEvenWhenReportedBoundsAreAlreadyFinal()
    {
        var gate = new TitleBarOverlayRevealGate();
        Assert.IsFalse(gate.CanReveal(Host, 0));
        gate.GeometryChanged(340);
        Assert.IsFalse(gate.CanReveal(Host, 350));
        Assert.AreEqual(420L, gate.NextCheckAtMilliseconds);
        Assert.IsTrue(gate.CanReveal(Host, 420));
    }

    [TestMethod]
    public void DuplicateRestoreNotificationsDoNotRestartTheAnimationDeadline()
    {
        var gate = new TitleBarOverlayRevealGate();
        gate.BeginRestore(1000);
        Assert.IsFalse(gate.CanReveal(Host, 1000));
        gate.BeginRestore(1100);
        gate.BeginRestore(1200);
        Assert.AreEqual(1350L, gate.NextCheckAtMilliseconds);
        Assert.IsTrue(gate.CanReveal(Host, 1350));
    }

    [TestMethod]
    public void ContinuousGeometryEventsCannotKeepTheOverlayHiddenIndefinitely()
    {
        var gate = new TitleBarOverlayRevealGate();
        for (var time = 0; time < 1000; time += 50)
        {
            gate.GeometryChanged(time);
            Assert.IsFalse(gate.CanReveal(Host with { X = Host.X + time }, time));
        }
        Assert.IsTrue(gate.CanReveal(Host with { X = -500 }, 1000));
        Assert.IsNull(gate.NextCheckAtMilliseconds);
    }

    [TestMethod]
    public void ReMinimizeOrHostReplacementInvalidatesThePreviousRevealDeadline()
    {
        var gate = new TitleBarOverlayRevealGate();
        Assert.IsFalse(gate.CanReveal(Host, 1000));
        gate.Reset();
        Assert.IsNull(gate.NextCheckAtMilliseconds);

        var replacement = new Rectangle(80, 60, 1600, 1000);
        Assert.IsFalse(gate.CanReveal(replacement, 1200));
        // An already-queued timer from the earlier restore must not reveal this host.
        Assert.IsFalse(gate.CanReveal(replacement, 1350));
        Assert.AreEqual(1550L, gate.NextCheckAtMilliseconds);
        Assert.IsTrue(gate.CanReveal(replacement, 1550));
    }

    [TestMethod]
    public void OrdinaryMovementAndRefreshAfterRevealDoNotRearmAnyTimer()
    {
        var gate = new TitleBarOverlayRevealGate();
        Assert.IsFalse(gate.CanReveal(Host, 0));
        Assert.IsTrue(gate.CanReveal(Host, 350));
        gate.GeometryChanged(360);
        Assert.IsTrue(gate.CanReveal(Host with { Y = 120 }, 360));
        Assert.IsTrue(gate.CanReveal(Host, 400));
        Assert.IsNull(gate.NextCheckAtMilliseconds);
    }
}
