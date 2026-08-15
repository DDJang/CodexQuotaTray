using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class LanReliabilityTests
{
    [TestMethod]
    public async Task DnsSdAsyncRegistrationSuccessRemainsRegisteredUntilDeregistrationCompletes()
    {
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var logs = new List<string>();
        var publisher = Publisher(native, logs);

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCalls == 1);
        Assert.IsFalse(start.IsCompleted, "Register must return PENDING before its callback completes StartAsync.");
        native.ReleaseRegistrationCallback();
        await start;
        Assert.IsTrue(publisher.IsStarted);
        var dispose = publisher.DisposeAsync().AsTask();
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        Assert.IsFalse(dispose.IsCompleted, "Deregister must return PENDING before its callback completes disposal.");
        native.ReleaseDeregistrationCallback();
        await dispose;
        await native.WaitForCallbacksAsync();

        Assert.AreEqual(1, native.DeregisterCalls);
        Assert.AreEqual(1, native.FreeCalls);
        Assert.IsTrue(logs.Any(value => value.Contains("registration success", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(value => value.Contains("deregistration status=0", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DnsSdAsyncRegistrationFailureIsReportedAndNativeStateIsReleased()
    {
        var native = new FakeDnsSdNative(registerStatus: 1234, deregisterStatus: 0);
        var logs = new List<string>();
        var publisher = Publisher(native, logs);

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCalls == 1);
        Assert.IsFalse(start.IsCompleted);
        native.ReleaseRegistrationCallback();
        await Assert.ThrowsAsync<Win32Exception>(() => start);
        await native.WaitForCallbacksAsync();

        Assert.IsFalse(publisher.IsStarted);
        Assert.AreEqual(0, native.DeregisterCalls);
        Assert.AreEqual(1, native.FreeCalls);
        Assert.IsTrue(logs.Any(value => value.Contains("registration failure status=1234", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DnsSdRegistrationTimeoutReleasesContextAndLateCallbackIsHarmless()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821));
        Assert.IsFalse(publisher.IsStarted);
        Assert.AreEqual(1, native.FreeCalls);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);

        native.ReleaseRegistrationCallback();
        await native.WaitForCallbacksAsync();
        Assert.IsNull(native.CallbackFailure);
        Assert.IsFalse(publisher.IsStarted);
        Assert.AreEqual(1, native.FreeCalls);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task ControllerRetriesAfterAddressChangeRestartFailureAndRecoversWithNullServer()
    {
        var firstAddress = IPAddress.Parse("192.168.1.20");
        var secondAddress = IPAddress.Parse("192.168.1.21");
        var currentAddress = firstAddress;
        var created = 0;
        var started = new List<IPAddress>();
        var logs = new List<string>();
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => currentAddress,
            _ => new FakeLanServer(++created == 2, started),
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromMilliseconds(20),
            logs.Add);

        await controller.SetEnabledAsync(true, CancellationToken.None);
        currentAddress = secondAddress;
        await WaitUntilAsync(() => created >= 3 && controller.AddressText.StartsWith(secondAddress.ToString(), StringComparison.Ordinal));

        Assert.AreEqual(3, created);
        CollectionAssert.AreEqual(new[] { firstAddress, secondAddress }, started);
        Assert.AreEqual("正在监听", controller.StatusText);
        Assert.IsTrue(logs.Any(value => value.Contains("start/restart failure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ControllerRetriesWhenInitiallyNoAddressAndStartsAfterNetworkReturns()
    {
        IPAddress? currentAddress = null;
        var created = 0;
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => currentAddress,
            _ => { created++; return new FakeLanServer(false, []); },
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromMilliseconds(20));

        await controller.SetEnabledAsync(true, CancellationToken.None);
        Assert.AreEqual("无可用局域网地址", controller.StatusText);
        currentAddress = IPAddress.Parse("192.168.1.20");
        await WaitUntilAsync(() => created == 1 && controller.AddressText.Length > 0);
        Assert.AreEqual("正在监听", controller.StatusText);
    }

    [TestMethod]
    public async Task ControllerKeepsListenerAndRetriesPublisherAfterAsyncRegistrationFailure()
    {
        var publisherAttempts = 0;
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => IPAddress.Parse("192.168.1.20"),
            _ => new FakeLanServer(false, []),
            (_, _) => new FakePublisher(failStart: ++publisherAttempts == 1),
            43821,
            "",
            TimeSpan.FromMilliseconds(20));

        await controller.SetEnabledAsync(true, CancellationToken.None);
        Assert.AreEqual("正在监听（自动发现不可用）", controller.StatusText);
        Assert.IsTrue(controller.AddressText.Length > 0);
        await WaitUntilAsync(() => publisherAttempts >= 2 && controller.StatusText == "正在监听");
        Assert.AreEqual(2, publisherAttempts);
    }

    [TestMethod]
    public async Task ControllerCanEnableAfterInitialSettingsLoadFails()
    {
        var loadAttempts = 0;
        var serverStarts = 0;
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => ++loadAttempts == 1
                ? Task.FromException<TokenUsageSettings>(new IOException("settings unavailable"))
                : Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => IPAddress.Parse("192.168.1.20"),
            _ => new FakeLanServer(false, [], () => serverStarts++),
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<IOException>(() => controller.SetEnabledAsync(true, CancellationToken.None));
        Assert.AreEqual("已关闭", controller.StatusText);
        Assert.AreEqual(string.Empty, controller.AddressText);
        Assert.AreEqual(0, serverStarts);

        await controller.SetEnabledAsync(true, CancellationToken.None);
        Assert.AreEqual(2, loadAttempts);
        Assert.AreEqual(1, serverStarts);
        Assert.AreEqual("正在监听", controller.StatusText);
    }

    private static DnsSdServicePublisher Publisher(
        FakeDnsSdNative native,
        List<string> logs,
        TimeSpan? callbackTimeout = null) =>
        new(Guid.NewGuid(), "Desk", native: native, callbackTimeout: callbackTimeout ?? TimeSpan.FromSeconds(1), diagnostic: logs.Add);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.IsTrue(condition(), "condition did not become true before timeout");
    }

    private sealed class FakeDnsSdNative(uint registerStatus, uint deregisterStatus) : IDnsSdNative
    {
        private readonly object callbackLock = new();
        private readonly List<Task> callbacks = [];
        private readonly TaskCompletionSource registrationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource deregistrationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RegisterCalls { get; private set; }
        public int DeregisterCalls { get; private set; }
        public int FreeCalls { get; private set; }
        public Exception? CallbackFailure { get; private set; }
        public IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values) => new(42);
        public uint Register(ref DnsSdRegisterRequest request)
        {
            RegisterCalls++;
            ScheduleComplete(request, registerStatus, registrationCallback.Task);
            return DnsSdServicePublisher.DnsRequestPending;
        }
        public uint Deregister(ref DnsSdRegisterRequest request)
        {
            DeregisterCalls++;
            ScheduleComplete(request, deregisterStatus, deregistrationCallback.Task);
            return DnsSdServicePublisher.DnsRequestPending;
        }
        public void FreeInstance(IntPtr instance) => FreeCalls++;
        public void ReleaseRegistrationCallback() => registrationCallback.TrySetResult();
        public void ReleaseDeregistrationCallback() => deregistrationCallback.TrySetResult();
        public async Task WaitForCallbacksAsync()
        {
            Task[] pending;
            lock (callbackLock) pending = callbacks.ToArray();
            await Task.WhenAll(pending);
        }
        private void ScheduleComplete(DnsSdRegisterRequest request, uint status, Task release)
        {
            var callback = Marshal.GetDelegateForFunctionPointer<DnsServiceRegisterComplete>(request.RegisterCompletionCallback);
            var pending = Task.Run(async () =>
            {
                await release;
                try { callback(status, request.QueryContext, request.ServiceInstance); }
                catch (Exception error) { CallbackFailure = error; }
            });
            lock (callbackLock) callbacks.Add(pending);
        }
    }

    private sealed class FakeLanServer(bool failStart, List<IPAddress> started, Action? onStart = null) : ILanSyncServer
    {
        public IPAddress? Address { get; private set; }
        public int Port { get; private set; }
        public void Start(IPAddress address, int port)
        {
            if (failStart) throw new SocketException((int)SocketError.AddressAlreadyInUse);
            Address = address;
            Port = port;
            started.Add(address);
            onStart?.Invoke();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePublisher(bool failStart = false) : IDnsSdPublisher
    {
        public Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken) =>
            failStart ? Task.FromException(new Win32Exception(1234)) : Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
