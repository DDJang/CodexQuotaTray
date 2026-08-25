using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TrayBalloonAttemptGateTests
{
    [TestMethod]
    public void LateShowAfterTimeoutIsQuarantinedBeforeNextAttempt()
    {
        var gate = new TrayBalloonAttemptGate();
        var attemptA = gate.Begin();
        gate.BeginDrain(attemptA);

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Quarantined,
            gate.Handle(TrayBalloonCallback.Show));
        Assert.IsFalse(attemptA.ShowCompletion.Task.IsCompletedSuccessfully);
        Assert.ThrowsExactly<InvalidOperationException>(() => gate.Begin());

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Drained,
            gate.Handle(TrayBalloonCallback.Hide));
        gate.End(attemptA);

        var attemptB = gate.Begin();
        Assert.AreNotEqual(attemptA.Id, attemptB.Id);
        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Acknowledged,
            gate.Handle(TrayBalloonCallback.Show));
        Assert.IsTrue(attemptB.ShowCompletion.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void ActiveTerminalCallbackDoesNotShortCircuitTheLaterDrain()
    {
        var gate = new TrayBalloonAttemptGate();
        var attempt = gate.Begin();

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Ignored,
            gate.Handle(TrayBalloonCallback.Timeout));
        Assert.IsFalse(attempt.DrainCompletion.Task.IsCompleted);

        gate.BeginDrain(attempt);
        Assert.IsFalse(attempt.DrainCompletion.Task.IsCompleted);

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Drained,
            gate.Handle(TrayBalloonCallback.Hide));
        Assert.IsTrue(attempt.DrainCompletion.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void LateTimeoutAfterDismissIsConsumedBeforeNextAttempt()
    {
        var gate = new TrayBalloonAttemptGate();
        var attemptA = gate.Begin();
        gate.BeginDrain(attemptA);

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Drained,
            gate.Handle(TrayBalloonCallback.Timeout));
        gate.End(attemptA);

        var attemptB = gate.Begin();
        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Ignored,
            gate.Handle(TrayBalloonCallback.Timeout));
        Assert.IsFalse(attemptB.ShowCompletion.Task.IsCompleted);

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Acknowledged,
            gate.Handle(TrayBalloonCallback.Show));
        Assert.IsTrue(attemptB.ShowCompletion.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void LateHideDoesNotImmediatelyFailNextAttempt()
    {
        var gate = new TrayBalloonAttemptGate();
        var attemptA = gate.Begin();
        gate.BeginDrain(attemptA);
        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Drained,
            gate.Handle(TrayBalloonCallback.Hide));
        gate.End(attemptA);

        var attemptB = gate.Begin();
        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Ignored,
            gate.Handle(TrayBalloonCallback.Hide));
        Assert.IsFalse(attemptB.ShowCompletion.Task.IsCompleted);
    }

    [TestMethod]
    public void ExplorerFailureCompletesTheCurrentAttemptWithoutOpeningAnotherOne()
    {
        var gate = new TrayBalloonAttemptGate();
        var attempt = gate.Begin();
        var error = new InvalidOperationException("Explorer restarted");

        gate.FailCurrent(error);

        Assert.IsTrue(attempt.ShowCompletion.Task.IsFaulted);
        Assert.IsFalse(attempt.DrainCompletion.Task.IsCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(() => gate.Begin());

        Assert.AreEqual(
            TrayBalloonCallbackDisposition.Drained,
            gate.Handle(TrayBalloonCallback.Timeout));
        Assert.IsTrue(attempt.DrainCompletion.Task.IsCompletedSuccessfully);

        gate.End(attempt);
        Assert.IsFalse(gate.HasCurrent);
    }
}
