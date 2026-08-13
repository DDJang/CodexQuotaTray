using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class CrashSessionLogTests
{
    [TestMethod]
    public void CleanShutdownDoesNotReportPreviousCrash()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);

        Assert.IsNull(first.StartSession());
        first.CompleteSession();

        var second = new CrashSessionLog(directory.Path);
        Assert.IsNull(second.StartSession());
        second.CompleteSession();
    }

    [TestMethod]
    public void ConfirmedWindowsSessionEndDoesNotReportPreviousCrash()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();

        Assert.IsTrue(SessionEndingPolicy.IsConfirmed(NativeMethods.WmEndSession, 1));
        first.MarkExpectedTermination();

        var second = new CrashSessionLog(directory.Path);
        Assert.IsNull(second.StartSession());
        second.CompleteSession();
    }

    [TestMethod]
    public void CancelledWindowsSessionEndStillReportsUncleanTermination()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();

        Assert.IsFalse(SessionEndingPolicy.IsConfirmed(NativeMethods.WmQueryEndSession, 1));
        Assert.IsFalse(SessionEndingPolicy.IsConfirmed(NativeMethods.WmEndSession, 0));

        var second = new CrashSessionLog(directory.Path);
        var previousCrash = second.StartSession();
        Assert.IsNotNull(previousCrash);
        Assert.IsTrue(second.AcknowledgePreviousCrash(previousCrash));
        second.CompleteSession();
    }

    [TestMethod]
    public void UncleanShutdownReportsAndPreservesCrashDetails()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();
        first.Record(new InvalidOperationException("test failure"), "test");

        var second = new CrashSessionLog(directory.Path);
        var previousCrash = second.StartSession();

        Assert.IsNotNull(previousCrash);
        Assert.AreEqual(first.LogPath, previousCrash.LogPath);
        StringAssert.Contains(File.ReadAllText(previousCrash.LogPath), "InvalidOperationException");
        second.CompleteSession();

        var third = new CrashSessionLog(directory.Path);
        var pendingCrash = third.StartSession();
        Assert.IsNotNull(pendingCrash, "Normal shutdown must not discard a notice that was never shown.");
        Assert.IsTrue(third.AcknowledgePreviousCrash(pendingCrash));
        third.CompleteSession();

        var fourth = new CrashSessionLog(directory.Path);
        Assert.IsNull(fourth.StartSession(), "A shown and acknowledged notice must not repeat.");
        fourth.CompleteSession();
    }

    [TestMethod]
    public void UncleanShutdownWithoutManagedExceptionCreatesLifecycleLog()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();

        var second = new CrashSessionLog(directory.Path);
        var previousCrash = second.StartSession();

        Assert.IsNotNull(previousCrash);
        StringAssert.Contains(File.ReadAllText(previousCrash.LogPath), "ProcessLifecycle");
        Assert.IsTrue(second.AcknowledgePreviousCrash(previousCrash));
        second.CompleteSession();
    }

    [TestMethod]
    public void AcknowledgedNoticeSuppressesAStalePendingMarkerAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();
        first.Record(new InvalidOperationException("test failure"), "test");

        var second = new CrashSessionLog(directory.Path);
        var previousCrash = second.StartSession();
        Assert.IsNotNull(previousCrash);
        Assert.IsTrue(second.AcknowledgePreviousCrash(previousCrash));
        second.CompleteSession();

        var pendingMarker = Path.Combine(directory.Path, "crash-pending.marker");
        File.WriteAllText(pendingMarker, previousCrash.NoticeId);
        var third = new CrashSessionLog(directory.Path);

        Assert.IsNull(third.StartSession(), "The same acknowledged notice ID must not be shown again.");
        Assert.IsFalse(File.Exists(pendingMarker));
        third.CompleteSession();
    }

    [TestMethod]
    public void AcknowledgingOldNoticeDoesNotClearNewerCrash()
    {
        using var directory = new TemporaryDirectory();
        var first = new CrashSessionLog(directory.Path);
        _ = first.StartSession();
        first.Record(new InvalidOperationException("first failure"), "test");

        var second = new CrashSessionLog(directory.Path);
        var firstNotice = second.StartSession();
        Assert.IsNotNull(firstNotice);
        second.Record(new InvalidOperationException("second failure"), "test");

        Assert.IsFalse(second.AcknowledgePreviousCrash(firstNotice));
        second.CompleteSession();

        var third = new CrashSessionLog(directory.Path);
        var secondNotice = third.StartSession();
        Assert.IsNotNull(secondNotice);
        Assert.AreNotEqual(firstNotice.NoticeId, secondNotice.NoticeId);
        Assert.IsTrue(third.AcknowledgePreviousCrash(secondNotice));
        third.CompleteSession();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexQuotaTray.CrashSessionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
