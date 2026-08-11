package com.codexquotatray.android.update

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateCheckCoordinatorTest {
    @Test
    fun automaticCheckWithin24HoursIsSuppressedButManualBypasses() {
        val now = 10_000_000L
        val repository = FakeUpdateSettingsRepository(UpdateSettings(lastCheckAtMillis = now - 1_000L))
        val providerCalls = AtomicInteger()
        val coordinator = coordinator(repository, now) {
            providerCalls.incrementAndGet()
            provider()
        }
        var automatic: UpdateCheckResult? = null
        coordinator.check(UpdateCheckReason.AUTOMATIC) { automatic = it }
        assertTrue(automatic is UpdateCheckResult.Skipped)
        assertEquals(0, providerCalls.get())

        var manual: UpdateCheckResult? = null
        coordinator.check(UpdateCheckReason.MANUAL) { manual = it }
        assertTrue(manual is UpdateCheckResult.Available)
        assertEquals(1, providerCalls.get())
        coordinator.close()
    }

    @Test
    fun automaticDisabledAndGiteeUnavailableDoNotAccessNetwork() {
        val disabled = FakeUpdateSettingsRepository(UpdateSettings(automaticChecksEnabled = false))
        val calls = AtomicInteger()
        val coordinator = coordinator(disabled, 1L) {
            calls.incrementAndGet()
            provider()
        }
        var result: UpdateCheckResult? = null
        coordinator.check(UpdateCheckReason.AUTOMATIC) { result = it }
        assertEquals(UpdateCheckResult.Skipped(SkipReason.AUTO_DISABLED), result)
        assertEquals(0, calls.get())
        coordinator.close()

        val gitee = FakeUpdateSettingsRepository(UpdateSettings(source = UpdateSource.GITEE))
        val giteeCoordinator = UpdateCheckCoordinator(
            settings = gitee,
            providerFor = { error("Gitee provider must not be called") },
            currentVersionName = "0.6.1",
            executor = Executor { it.run() },
            callbackExecutor = Executor { it.run() },
            nowMillis = { 1L },
        )
        var giteeResult: UpdateCheckResult? = null
        giteeCoordinator.check(UpdateCheckReason.MANUAL) { giteeResult = it }
        assertEquals(UpdateCheckResult.Skipped(SkipReason.SOURCE_UNAVAILABLE), giteeResult)
        giteeCoordinator.close()
    }

    @Test
    fun equalOrOlderReleaseDoesNotProduceAnUpdate() {
        listOf(SemVer(0, 6, 1), SemVer(0, 6, 0)).forEach { latest ->
            val repository = FakeUpdateSettingsRepository()
            val coordinator = coordinator(repository, latest.patch.toLong()) {
                provider().fetchLatest().let { release ->
                    object : UpdateProvider {
                        override val source = UpdateSource.GITHUB
                        override fun fetchLatest() = release.copy(
                            tagName = "android-v$latest",
                            version = latest,
                        )
                    }
                }
            }
            var result: UpdateCheckResult? = null
            coordinator.check(UpdateCheckReason.MANUAL) { result = it }
            assertTrue(result is UpdateCheckResult.UpToDate)
            coordinator.close()
        }
    }

    @Test
    fun concurrentChecksUseSingleFlight() {
        val repository = FakeUpdateSettingsRepository()
        val providerStarted = CountDownLatch(1)
        val releaseProvider = CountDownLatch(1)
        val calls = AtomicInteger()
        val executor = Executors.newSingleThreadExecutor()
        val coordinator = UpdateCheckCoordinator(
            settings = repository,
            providerFor = {
                object : UpdateProvider {
                    override val source = UpdateSource.GITHUB
                    override fun fetchLatest(): UpdateRelease {
                        calls.incrementAndGet()
                        providerStarted.countDown()
                        assertTrue(releaseProvider.await(2, TimeUnit.SECONDS))
                        return provider().fetchLatest()
                    }
                }
            },
            currentVersionName = "0.6.1",
            executor = executor,
            callbackExecutor = Executor { it.run() },
            nowMillis = { 2L },
        )
        val callbackLatch = CountDownLatch(2)
        coordinator.check(UpdateCheckReason.MANUAL) { callbackLatch.countDown() }
        assertTrue(providerStarted.await(2, TimeUnit.SECONDS))
        coordinator.check(UpdateCheckReason.MANUAL) { callbackLatch.countDown() }
        releaseProvider.countDown()
        assertTrue(callbackLatch.await(2, TimeUnit.SECONDS))
        assertEquals(1, calls.get())
        coordinator.close()
    }

    @Test
    fun reminderIsEmittedOnceAndRespectsSetting() {
        val repository = FakeUpdateSettingsRepository()
        val coordinator = coordinator(repository, 3L) { provider() }
        var reminders = 0
        coordinator.addReminderListener { reminders++ }
        coordinator.requestAutomaticCheck()
        coordinator.requestAutomaticCheck()
        assertEquals(1, reminders)
        coordinator.close()

        val disabled = FakeUpdateSettingsRepository(UpdateSettings(updateRemindersEnabled = false))
        val disabledCoordinator = coordinator(disabled, 3L) { provider() }
        var disabledReminders = 0
        disabledCoordinator.addReminderListener { disabledReminders++ }
        disabledCoordinator.requestAutomaticCheck()
        assertEquals(0, disabledReminders)
        disabledCoordinator.close()
    }

    @Test
    fun releaseWithoutAndroidAssetIsReportedExplicitly() {
        val repository = FakeUpdateSettingsRepository()
        val coordinator = coordinator(repository, 4L) {
            object : UpdateProvider {
                override val source = UpdateSource.GITHUB
                override fun fetchLatest() = provider().fetchLatest().copy(androidAsset = null)
            }
        }
        var result: UpdateCheckResult? = null
        coordinator.check(UpdateCheckReason.MANUAL) { result = it }
        assertTrue(result is UpdateCheckResult.NoAndroidAsset)
        coordinator.close()
    }

    private fun coordinator(
        repository: FakeUpdateSettingsRepository,
        now: Long,
        providerFactory: () -> UpdateProvider,
    ) = UpdateCheckCoordinator(
        settings = repository,
        providerFor = { providerFactory() },
        currentVersionName = "0.6.1",
        executor = Executor { it.run() },
        callbackExecutor = Executor { it.run() },
        nowMillis = { now },
    )

    private fun provider(): UpdateProvider = object : UpdateProvider {
        override val source = UpdateSource.GITHUB
        override fun fetchLatest() = UpdateRelease(
            tagName = "android-v0.7.0",
            name = "Android 0.7.0",
            notes = "Notes",
            publishedAt = null,
            version = SemVer(0, 7, 0),
            androidAsset = UpdateAsset("CodexQuotaTray-Android-v0.7.0.apk", "https://github.com/DDJang/CodexQuotaTray/a.apk"),
        )
    }

    private class FakeUpdateSettingsRepository(
        initial: UpdateSettings = UpdateSettings(),
    ) : UpdateSettingsRepository {
        var settings = initial
        override fun load(): UpdateSettings = settings
        override fun save(settings: UpdateSettings) { this.settings = settings }
    }
}
