using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class LanReliabilityTests
{
    [TestMethod]
    public async Task DnsSdAsyncRegistrationSuccessRemainsRegisteredUntilDeregistrationCompletes()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var logs = new List<string>();
        var publisher = Publisher(native, logs);

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821, interfaceIndex: 19);
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
        Assert.AreEqual(19u, native.RegisteredInterfaceIndex);
        Assert.AreEqual(0, native.RegisterCancelCalls);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
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
        Assert.AreEqual(0, native.RegisterCancelCalls);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: false);
        Assert.IsTrue(logs.Any(value => value.Contains("registration failure status=1234", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DnsSdRegistrationTimeoutCancelsThenWaitsForLateCallbackBeforeCleanup()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromMilliseconds(25));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        Assert.IsFalse(start.IsCompleted, "Cleanup must wait for the native cancellation callback.");
        Assert.AreEqual(activeBefore + 1, DnsSdServicePublisher.ActivePublisherCount);

        native.ReleaseRegistrationCallback();
        await Assert.ThrowsAsync<TimeoutException>(() => start);
        await native.WaitForCallbacksAsync();
        Assert.IsNull(native.CallbackFailure);
        Assert.IsFalse(publisher.IsStarted);
        Assert.AreEqual(1, native.RegisterCancelCalls);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: false);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task DnsSdRegistrationCancellationTokenCancelsThenCleansUp()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821, cancellation.Token);
        await WaitUntilAsync(() => native.RegisterCalls == 1);
        cancellation.Cancel();
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        Assert.IsFalse(start.IsCompleted);
        native.ReleaseRegistrationCallback();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        await native.WaitForCallbacksAsync();
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: false);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
        Assert.IsFalse(publisher.IsStarted);
    }

    [TestMethod]
    public async Task DnsSdTimeoutRacingSuccessfulCallbackDeregistersBeforeCleanup()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, cancelledRegistrationStatus: 0);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromMilliseconds(25));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        native.ReleaseRegistrationCallback();
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        Assert.IsFalse(start.IsCompleted, "A raced successful registration must be deregistered before cleanup.");
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        await native.WaitForCallbacksAsync();
        Assert.AreEqual(1, native.RegisterCancelCalls);
        Assert.AreEqual(1, native.DeregisterCalls);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
        Assert.IsFalse(publisher.IsStarted);
    }

    [TestMethod]
    public async Task DnsSdSuccessfulCallbackWinningCancellationRaceIsNotCancelled()
    {
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        var publisher = Publisher(native, []);
        using var cancellation = new CancellationTokenSource();

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821, cancellation.Token);
        await WaitUntilAsync(() => native.RegisterCalls == 1);
        native.ReleaseRegistrationCallback();
        await start;
        cancellation.Cancel();

        Assert.IsTrue(publisher.IsStarted);
        Assert.AreEqual(0, native.RegisterCancelCalls);
        var dispose = publisher.DisposeAsync().AsTask();
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();
        await dispose;
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
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
            () => new LanEndpointSelection(currentAddress, 7),
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
            () => currentAddress is null ? null : new LanEndpointSelection(currentAddress, 7),
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
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 7),
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
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 7),
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

    [TestMethod]
    public async Task ControllerRestartsUnhealthyListenerAndPropagatesInterfaceIndex()
    {
        var created = new List<FakeLanServer>();
        uint publishedInterface = 0;
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 19),
            _ => { var value = new FakeLanServer(false, []); created.Add(value); return value; },
            (_, _) => new FakePublisher(onStart: index => publishedInterface = index),
            43821,
            "",
            TimeSpan.FromMilliseconds(20));

        await controller.SetEnabledAsync(true, CancellationToken.None);
        Assert.AreEqual(19u, publishedInterface);
        created[0].Healthy = false;
        await WaitUntilAsync(() => created.Count == 2 && controller.StatusText == "正在监听");

        Assert.IsTrue(created[0].Disposed);
        Assert.AreEqual(19u, publishedInterface);
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

    private sealed class FakeDnsSdNative(
        uint registerStatus,
        uint deregisterStatus,
        uint cancelledRegistrationStatus = 1223) : IDnsSdNative
    {
        private static readonly IntPtr OriginalInstance = new(42);
        private static readonly IntPtr RegistrationCallbackInstance = new(43);
        private static readonly IntPtr DeregistrationCallbackInstance = new(44);
        private static readonly IntPtr CancelHandle = new(45);
        private readonly object callbackLock = new();
        private readonly List<Task> callbacks = [];
        private readonly List<IntPtr> freeInstanceCalls = [];
        private readonly TaskCompletionSource registrationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource deregistrationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int currentRegistrationStatus = unchecked((int)registerStatus);
        public int RegisterCalls { get; private set; }
        public int RegisterCancelCalls { get; private set; }
        public int DeregisterCalls { get; private set; }
        public uint RegisteredInterfaceIndex { get; private set; }
        public Exception? CallbackFailure { get; private set; }
        public IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values) => OriginalInstance;
        public uint Register(ref DnsSdRegisterRequest request, ref DnsSdCancel cancel)
        {
            RegisterCalls++;
            RegisteredInterfaceIndex = request.InterfaceIndex;
            cancel.Reserved = CancelHandle;
            ScheduleComplete(
                request,
                () => unchecked((uint)Volatile.Read(ref currentRegistrationStatus)),
                RegistrationCallbackInstance,
                registrationCallback.Task);
            return DnsSdServicePublisher.DnsRequestPending;
        }
        public uint CancelRegistration(ref DnsSdCancel cancel)
        {
            if (cancel.Reserved != CancelHandle) throw new InvalidOperationException("Registration cancel handle was not preserved.");
            RegisterCancelCalls++;
            Volatile.Write(ref currentRegistrationStatus, unchecked((int)cancelledRegistrationStatus));
            return 0;
        }
        public uint Deregister(ref DnsSdRegisterRequest request)
        {
            DeregisterCalls++;
            ScheduleComplete(request, () => deregisterStatus, DeregistrationCallbackInstance, deregistrationCallback.Task);
            return DnsSdServicePublisher.DnsRequestPending;
        }
        public void FreeInstance(IntPtr instance)
        {
            lock (callbackLock)
            {
                freeInstanceCalls.Add(instance);
            }
        }
        public void ReleaseRegistrationCallback() => registrationCallback.TrySetResult();
        public void ReleaseDeregistrationCallback() => deregistrationCallback.TrySetResult();
        public async Task WaitForCallbacksAsync()
        {
            Task[] pending;
            lock (callbackLock) pending = callbacks.ToArray();
            await Task.WhenAll(pending);
        }
        public void AssertEveryInstanceFreedOnce(bool includeDeregistrationCallback)
        {
            IntPtr[] actual;
            lock (callbackLock) actual = freeInstanceCalls.Order().ToArray();
            var expected = includeDeregistrationCallback
                ? new[] { OriginalInstance, RegistrationCallbackInstance, DeregistrationCallbackInstance }
                : new[] { OriginalInstance, RegistrationCallbackInstance };
            CollectionAssert.AreEqual(expected.Order().ToArray(), actual);
        }
        private void ScheduleComplete(
            DnsSdRegisterRequest request,
            Func<uint> status,
            IntPtr callbackInstance,
            Task release)
        {
            var callback = Marshal.GetDelegateForFunctionPointer<DnsServiceRegisterComplete>(request.RegisterCompletionCallback);
            var pending = Task.Run(async () =>
            {
                await release;
                try { callback(status(), request.QueryContext, callbackInstance); }
                catch (Exception error) { CallbackFailure = error; }
            });
            lock (callbackLock) callbacks.Add(pending);
        }
    }

    private sealed class FakeLanServer(bool failStart, List<IPAddress> started, Action? onStart = null) : ILanSyncServer
    {
        public IPAddress? Address { get; private set; }
        public int Port { get; private set; }
        public bool Healthy { get; set; } = true;
        public bool IsHealthy => Healthy;
        public Exception? ListenerFault => Healthy ? null : new IOException("accept failed");
        public bool Disposed { get; private set; }
        public void Start(IPAddress address, int port)
        {
            if (failStart) throw new SocketException((int)SocketError.AddressAlreadyInUse);
            Address = address;
            Port = port;
            started.Add(address);
            onStart?.Invoke();
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakePublisher(bool failStart = false, Action<uint>? onStart = null) : IDnsSdPublisher
    {
        public Task StartAsync(IPAddress address, int port, uint interfaceIndex, CancellationToken cancellationToken)
        {
            onStart?.Invoke(interfaceIndex);
            return failStart ? Task.FromException(new Win32Exception(1234)) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
