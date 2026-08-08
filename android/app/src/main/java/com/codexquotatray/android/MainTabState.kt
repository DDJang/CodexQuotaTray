package com.codexquotatray.android

enum class MainTab {
    QUOTA,
    USAGE,
}

data class MainTabState(
    val selectedTab: MainTab = MainTab.QUOTA,
    val usageHasBeenShown: Boolean = false,
) {
    fun shouldAutoSyncUsageOnShow(): Boolean =
        selectedTab != MainTab.USAGE && !usageHasBeenShown

    fun select(tab: MainTab): MainTabState = copy(
        selectedTab = tab,
        usageHasBeenShown = usageHasBeenShown || tab == MainTab.USAGE,
    )

    fun backToQuota(): MainTabState? =
        if (selectedTab == MainTab.USAGE) copy(selectedTab = MainTab.QUOTA) else null
}

enum class TokenUsagePageMode {
    EMPTY_UNPAIRED,
    CONTENT,
}

fun tokenUsagePageMode(pairingConfigured: Boolean): TokenUsagePageMode =
    if (pairingConfigured) TokenUsagePageMode.CONTENT else TokenUsagePageMode.EMPTY_UNPAIRED
