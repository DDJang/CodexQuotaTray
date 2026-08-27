using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
        Assert.AreNotEqual(native.RegistrationQueryContext, native.DeregistrationQueryContext);
        Assert.AreEqual(native.OriginalConstructedInstance, native.DeregistrationServiceInstance);
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
    public async Task DnsSdLateCallbackAfterContextRemovalUsesOwningNativeExactlyOnce()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromMilliseconds(25));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);

        native.EmitLateRegistrationCallback();
        native.EmitLateRegistrationCallback();
        await native.WaitForCallbacksAsync();

        Assert.AreEqual(1, native.RegistrationCallbackFreeCount);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true, includeRegistrationCallback: true);
    }

    [TestMethod]
    public async Task DnsSdNoCallbackEventuallyExpiresCallbackContext()
    {
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(
            native,
            [],
            callbackTimeout: TimeSpan.FromMilliseconds(25),
            lateCallbackGracePeriod: TimeSpan.FromMilliseconds(25));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        var registrationContext = native.RegistrationQueryContext;
        var deregistrationContext = native.DeregistrationQueryContext;
        await WaitUntilAsync(
            () => !DnsSdServicePublisher.HasCallbackContext(registrationContext)
                && !DnsSdServicePublisher.HasCallbackContext(deregistrationContext),
            timeout: TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, native.RegistrationCallbackFreeCount);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task DnsSdLateCallbackWithinGraceUsesOwningNativeExactlyOnce()
    {
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(
            native,
            [],
            callbackTimeout: TimeSpan.FromMilliseconds(25),
            lateCallbackGracePeriod: TimeSpan.FromMilliseconds(250));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        var registrationContext = native.RegistrationQueryContext;
        await WaitUntilAsync(() => DnsSdServicePublisher.HasCallbackContext(registrationContext));
        native.EmitLateRegistrationCallback();
        native.EmitLateRegistrationCallback();
        await native.WaitForCallbacksAsync();

        Assert.AreEqual(1, native.RegistrationCallbackFreeCount);
        await WaitUntilAsync(() => !DnsSdServicePublisher.HasCallbackContext(registrationContext));
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true, includeRegistrationCallback: true);
    }

    [TestMethod]
    public async Task DnsSdLateCallbackAfterGraceUsesOwningNativeExactlyOnce()
    {
        var contextBefore = DnsSdServicePublisher.CallbackContextCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(
            native,
            [],
            callbackTimeout: TimeSpan.FromMilliseconds(25),
            lateCallbackGracePeriod: TimeSpan.FromMilliseconds(40));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        await WaitUntilAsync(() => DnsSdServicePublisher.CallbackContextCount == contextBefore);
        native.EmitLateRegistrationCallback();
        await native.WaitForCallbacksAsync();

        Assert.IsNull(native.CallbackFailure);
        Assert.AreEqual(1, native.RegistrationCallbackFreeCount);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true, includeRegistrationCallback: true);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task DnsSdUnknownOwnerCallbackDoesNotUseFallbackOrWrongOwner()
    {
        var contextBefore = DnsSdServicePublisher.CallbackContextCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var wrongOwner = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(
            native,
            [],
            callbackTimeout: TimeSpan.FromMilliseconds(25),
            lateCallbackGracePeriod: TimeSpan.FromMilliseconds(40));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        await WaitUntilAsync(() => DnsSdServicePublisher.CallbackContextCount == contextBefore);

        var unknownOwnerContext = new IntPtr(unchecked((long)0xDEAD_BEEF_0000_0001UL));
        native.EmitRegistrationCallback(unknownOwnerContext, new IntPtr(99));

        Assert.AreEqual(0, native.RegistrationCallbackFreeCount);
        Assert.AreEqual(0, wrongOwner.FreeInstanceCallCount);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task DnsSdReusesStableNativeOwnerIdAcrossOperations()
    {
        var ownerCountBefore = DnsSdServicePublisher.NativeOwnerRegistryCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0);
        uint? ownerId = null;
        var registrationContexts = new List<IntPtr>();

        for (var operation = 0; operation < 3; operation++)
        {
            var publisher = Publisher(native, []);
            var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
            await WaitUntilAsync(() => native.RegisterCalls == operation + 1);
            native.ReleaseRegistrationCallback();
            await start;
            var context = native.RegistrationQueryContext;
            registrationContexts.Add(context);
            var currentOwnerId = unchecked((uint)(unchecked((ulong)context.ToInt64()) >> 32));
            if (ownerId is null)
            {
                ownerId = currentOwnerId;
            }
            else
            {
                Assert.AreEqual(ownerId.Value, currentOwnerId);
            }

            var dispose = publisher.DisposeAsync().AsTask();
            await WaitUntilAsync(() => native.DeregisterCalls == operation + 1);
            native.ReleaseDeregistrationCallback();
            await dispose;
        }

        Assert.AreEqual(3, registrationContexts.Distinct().Count());
        Assert.IsTrue(
            DnsSdServicePublisher.NativeOwnerRegistryCount <= ownerCountBefore + 1,
            "one native owner must not create one registry entry per operation");
        await native.WaitForCallbacksAsync();
    }

    [TestMethod]
    public async Task DnsSdRegistrationTimeoutWithoutCallbackUsesDeregistrationFence()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromMilliseconds(25));

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821);
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        Assert.IsFalse(start.IsCompleted, "Cleanup must wait for the native deregistration fence, not registration callback.");
        Assert.AreEqual(activeBefore + 1, DnsSdServicePublisher.ActivePublisherCount);

        native.ReleaseDeregistrationCallback();
        await Assert.ThrowsAsync<TimeoutException>(() => start);
        await native.WaitForCallbacksAsync();
        Assert.IsNull(native.CallbackFailure);
        Assert.IsFalse(publisher.IsStarted);
        Assert.AreEqual(1, native.RegisterCancelCalls);
        Assert.AreEqual(1, native.DeregisterCalls);
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
        Assert.AreEqual(activeBefore, DnsSdServicePublisher.ActivePublisherCount);
        await publisher.DisposeAsync();
    }

    [TestMethod]
    public async Task DnsSdRegistrationCancellationTokenCancelsThenCleansUp()
    {
        var activeBefore = DnsSdServicePublisher.ActivePublisherCount;
        var native = new FakeDnsSdNative(registerStatus: 0, deregisterStatus: 0, emitRegistrationCallback: false);
        var publisher = Publisher(native, [], callbackTimeout: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var start = publisher.StartAsync(IPAddress.Parse("192.168.1.20"), 43821, cancellation.Token);
        await WaitUntilAsync(() => native.RegisterCalls == 1);
        cancellation.Cancel();
        await WaitUntilAsync(() => native.RegisterCancelCalls == 1);
        await WaitUntilAsync(() => native.DeregisterCalls == 1);
        Assert.IsFalse(start.IsCompleted);
        native.ReleaseDeregistrationCallback();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        await native.WaitForCallbacksAsync();
        native.AssertEveryInstanceFreedOnce(includeDeregistrationCallback: true);
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
        Assert.AreNotEqual(native.RegistrationQueryContext, native.DeregistrationQueryContext);
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

    [TestMethod]
    public async Task NetworkEventsDebounceAndDoNotRestartHealthyUnchangedListener()
    {
        var created = 0;
        var logs = new List<string>();
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 7),
            _ => { created++; return new FakeLanServer(false, []); },
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromSeconds(10),
            logs.Add,
            networkChangeDebounce: TimeSpan.FromMilliseconds(30));

        await controller.SetEnabledAsync(true, CancellationToken.None);
        controller.OnNetworkChanged("NETWORK_ADDRESS_CHANGED");
        Assert.AreEqual("正在监听", controller.StatusText);
        controller.OnNetworkChanged("NETWORK_AVAILABILITY_CHANGED");
        controller.OnNetworkChanged("CONNECTIVITY_HINT");
        await WaitUntilAsync(() => logs.Count(value => value.Contains("LAN reconcile result=no-change", StringComparison.Ordinal)) == 1);

        Assert.AreEqual(1, created);
        Assert.AreEqual("正在监听", controller.StatusText);
        Assert.AreEqual(1, logs.Count(value => value.Contains("LAN reconcile result=no-change", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NetworkAddressChangeUsesExistingReconcileAndPublishesNewInterface()
    {
        var current = new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 7);
        var created = new List<FakeLanServer>();
        var publishedInterfaces = new List<uint>();
        var logs = new List<string>();
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => current,
            _ => { var server = new FakeLanServer(false, []); created.Add(server); return server; },
            (_, _) => new FakePublisher(onStart: publishedInterfaces.Add),
            43821,
            "",
            TimeSpan.FromSeconds(10),
            logs.Add,
            networkChangeDebounce: TimeSpan.FromMilliseconds(20));

        await controller.SetEnabledAsync(true, CancellationToken.None);
        current = new LanEndpointSelection(IPAddress.Parse("192.168.1.21"), 19);
        controller.OnNetworkChanged("NETWORK_ADDRESS_CHANGED");
        await WaitUntilAsync(() => created.Count == 2 && controller.AddressText.StartsWith("192.168.1.21", StringComparison.Ordinal));

        Assert.AreEqual(2, created.Count);
        CollectionAssert.AreEqual(new uint[] { 7, 19 }, publishedInterfaces);
        Assert.IsTrue(logs.Any(value => value.Contains("LAN reconcile result=restarted", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LanStatusTimeFormatterUsesRelativeLocalLabels()
    {
        var now = new DateTimeOffset(2026, 8, 26, 19, 30, 0, TimeSpan.Zero);

        Assert.AreEqual(
            "今天 19:12",
            LanStatusTimeFormatter.Format(
                new DateTimeOffset(2026, 8, 26, 19, 12, 0, TimeSpan.Zero),
                now,
                TimeZoneInfo.Utc));
        Assert.AreEqual(
            "昨天 22:06",
            LanStatusTimeFormatter.Format(
                new DateTimeOffset(2026, 8, 25, 22, 6, 0, TimeSpan.Zero),
                now,
                TimeZoneInfo.Utc));
        Assert.AreEqual(
            "08-24 16:30",
            LanStatusTimeFormatter.Format(
                new DateTimeOffset(2026, 8, 24, 16, 30, 0, TimeSpan.Zero),
                now,
                TimeZoneInfo.Utc));
    }

    [TestMethod]
    public async Task RepairWithoutSuccessfulRemoteDoesNotProbe()
    {
        var probes = 0;
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.20"), 7),
            _ => new FakeLanServer(false, []),
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromSeconds(10),
            diagnosticStateProvider: () => new LanDiagnosticState(
                LastRemoteAddress: "192.168.1.92",
                LastRequestResult: "AUTH_FAILED"),
            repairProbe: (_, _, _) => { probes++; return Task.FromResult(LanRepairProbeResult.REPLY); });

        await controller.SetEnabledAsync(true, CancellationToken.None);
        var result = await controller.RepairPhoneConnectionAsync(CancellationToken.None);

        Assert.AreEqual("暂无可修复的手机连接记录", result);
        Assert.AreEqual(0, probes);
    }

    [TestMethod]
    public async Task RepairUsesOnlySuccessfulOnLinkRemoteAndTimeoutIsNotFailure()
    {
        var probes = 0;
        var logs = new List<string>();
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.58"), 7),
            _ => new FakeLanServer(false, []),
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromSeconds(10),
            logs.Add,
            diagnosticStateProvider: () => new LanDiagnosticState(
                LastSuccessfulRemoteAddress: "192.168.1.92",
                LastRequestResult: "SUCCESS"),
            repairProbe: (_, _, _) => { probes++; return Task.FromResult(LanRepairProbeResult.TIMEOUT); },
            neighborReader: (_, index) => new LanNeighborSnapshot("Incomplete", null, index),
            repairRouteValidator: (_, _) => true);

        await controller.SetEnabledAsync(true, CancellationToken.None);
        var result = await controller.RepairPhoneConnectionAsync(CancellationToken.None);
        var second = await controller.RepairPhoneConnectionAsync(CancellationToken.None);

        Assert.AreEqual("已尝试修复，请在手机端重新刷新", result);
        Assert.AreEqual(1, probes);
        Assert.AreEqual("请稍后再试", second);
        Assert.IsTrue(logs.Any(value => value.Contains("probeResult=TIMEOUT actionResult=PROBE_SENT", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(value => value.Contains("failureKind=NEIGHBOR_RESOLUTION", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Any(value => value.Contains("localAddress=192.168.1.58 interfaceIndex=7 sourceBound=true", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RepairIsSingleFlightWhileProbeIsRunning()
    {
        var probes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new TokenUsageSettings(2, Guid.NewGuid(), new string('a', 64));
        await using var controller = new TokenUsageSyncController(
            _ => Task.FromResult(settings),
            _ => Task.FromResult(settings),
            () => new LanEndpointSelection(IPAddress.Parse("192.168.1.58"), 7),
            _ => new FakeLanServer(false, []),
            (_, _) => new FakePublisher(),
            43821,
            "",
            TimeSpan.FromSeconds(10),
            diagnosticStateProvider: () => new LanDiagnosticState(
                LastSuccessfulRemoteAddress: "192.168.1.92",
                LastRequestResult: "SUCCESS"),
            repairProbe: async (_, _, _) =>
            {
                probes++;
                entered.SetResult();
                await release.Task;
                return LanRepairProbeResult.TIMEOUT;
            },
            neighborReader: (_, index) => new LanNeighborSnapshot("Reachable", "AA-BB-CC-DD-EE-FF", index),
            repairRouteValidator: (_, _) => true);

        await controller.SetEnabledAsync(true, CancellationToken.None);
        var first = controller.RepairPhoneConnectionAsync(CancellationToken.None);
        await entered.Task;
        var concurrent = await controller.RepairPhoneConnectionAsync(CancellationToken.None);
        release.SetResult();
        await first;

        Assert.AreEqual("正在尝试修复，请稍候", concurrent);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public void RepairFailureClassificationUsesNeighborEvidenceBeforeIcmpGuessing()
    {
        Assert.AreEqual(
            "NEIGHBOR_RESOLUTION",
            TokenUsageSyncController.ClassifyRepairFailure(
                LanRepairProbeResult.TIMEOUT,
                new LanNeighborSnapshot("Incomplete", null, 7)));
        Assert.AreEqual(
            "ICMP_TIMEOUT_OR_FILTERED",
            TokenUsageSyncController.ClassifyRepairFailure(
                LanRepairProbeResult.TIMEOUT,
                new LanNeighborSnapshot("Reachable", "AA-BB-CC-DD-EE-FF", 7)));
        Assert.AreEqual(
            "ROUTE_OR_REMOTE_UNREACHABLE",
            TokenUsageSyncController.ClassifyRepairFailure(
                LanRepairProbeResult.UNREACHABLE,
                new LanNeighborSnapshot("Stale", "AA-BB-CC-DD-EE-FF", 7)));
    }

    private static DnsSdServicePublisher Publisher(
        FakeDnsSdNative native,
        List<string> logs,
        TimeSpan? callbackTimeout = null,
        TimeSpan? lateCallbackGracePeriod = null) =>
        new(
            Guid.NewGuid(),
            "Desk",
            native: native,
            callbackTimeout: callbackTimeout ?? TimeSpan.FromSeconds(1),
            diagnostic: logs.Add,
            lateCallbackGracePeriod: lateCallbackGracePeriod ?? TimeSpan.FromMilliseconds(100));

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionDescription = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < limit)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(
            condition(),
            $"Condition '{conditionDescription ?? "unknown"}' did not become true before timeout {limit}.");
    }

    private sealed class FakeDnsSdNative(
        uint registerStatus,
        uint deregisterStatus,
        uint cancelledRegistrationStatus = 1223,
        bool emitRegistrationCallback = true) : IDnsSdNative
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
        private IntPtr registrationCompletionCallback;
        public int RegisterCalls { get; private set; }
        public int RegisterCancelCalls { get; private set; }
        public int DeregisterCalls { get; private set; }
        public uint RegisteredInterfaceIndex { get; private set; }
        public IntPtr RegistrationQueryContext { get; private set; }
        public IntPtr DeregistrationQueryContext { get; private set; }
        public IntPtr DeregistrationServiceInstance { get; private set; }
        public IntPtr OriginalConstructedInstance => OriginalInstance;
        public int RegistrationCallbackFreeCount
        {
            get
            {
                lock (callbackLock) return freeInstanceCalls.Count(value => value == RegistrationCallbackInstance);
            }
        }
        public int FreeInstanceCallCount
        {
            get
            {
                lock (callbackLock) return freeInstanceCalls.Count;
            }
        }
        public Exception? CallbackFailure { get; private set; }
        public IntPtr ConstructInstance(string serviceName, string hostName, IntPtr ip4Address, ushort port, uint propertyCount, IntPtr keys, IntPtr values) => OriginalInstance;
        public uint Register(ref DnsSdRegisterRequest request, ref DnsSdCancel cancel)
        {
            RegisterCalls++;
            RegisteredInterfaceIndex = request.InterfaceIndex;
            cancel.Reserved = CancelHandle;
            RegistrationQueryContext = request.QueryContext;
            registrationCompletionCallback = request.RegisterCompletionCallback;
            if (emitRegistrationCallback)
            {
                ScheduleComplete(
                    request,
                    () => unchecked((uint)Volatile.Read(ref currentRegistrationStatus)),
                    RegistrationCallbackInstance,
                    registrationCallback.Task);
            }
            return 9506;
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
            DeregistrationQueryContext = request.QueryContext;
            DeregistrationServiceInstance = request.ServiceInstance;
            ScheduleComplete(request, () => deregisterStatus, DeregistrationCallbackInstance, deregistrationCallback.Task);
            return 9506;
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
        public void EmitLateRegistrationCallback() => EmitRegistrationCallback(RegistrationQueryContext, RegistrationCallbackInstance);
        public void EmitRegistrationCallback(IntPtr queryContext, IntPtr instance)
        {
            var callback = Marshal.GetDelegateForFunctionPointer<DnsServiceRegisterComplete>(registrationCompletionCallback);
            callback(0, queryContext, instance);
        }
        public async Task WaitForCallbacksAsync()
        {
            Task[] pending;
            lock (callbackLock) pending = callbacks.ToArray();
            await Task.WhenAll(pending);
        }
        public void AssertEveryInstanceFreedOnce(bool includeDeregistrationCallback, bool includeRegistrationCallback = false)
        {
            IntPtr[] actual;
            lock (callbackLock) actual = freeInstanceCalls.Order().ToArray();
            var expected = new List<IntPtr> { OriginalInstance };
            if (emitRegistrationCallback || includeRegistrationCallback) expected.Add(RegistrationCallbackInstance);
            if (includeDeregistrationCallback) expected.Add(DeregistrationCallbackInstance);
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
