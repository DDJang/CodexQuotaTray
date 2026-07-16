use codex_quota_tray::host_events::{CARD_OPEN_REFRESH_AGE_SECS, HostEvent, refresh_reason};
use codex_quota_tray::refresh::RefreshReason;

#[test]
fn visible_card_and_resume_map_to_bounded_refresh_reasons() {
    assert_eq!(
        refresh_reason(
            HostEvent::CardOpened {
                last_success_age_secs: None,
            },
            true,
        ),
        Some(RefreshReason::CardOpened)
    );
    assert_eq!(
        refresh_reason(
            HostEvent::CardOpened {
                last_success_age_secs: Some(CARD_OPEN_REFRESH_AGE_SECS - 1),
            },
            true,
        ),
        None
    );
    assert_eq!(
        refresh_reason(
            HostEvent::CardOpened {
                last_success_age_secs: Some(CARD_OPEN_REFRESH_AGE_SECS),
            },
            true,
        ),
        Some(RefreshReason::CardOpened)
    );
    assert_eq!(
        refresh_reason(HostEvent::SessionResumed, true),
        Some(RefreshReason::Resume)
    );
}

#[test]
fn only_restored_internet_connectivity_requests_a_refresh() {
    assert_eq!(
        refresh_reason(
            HostEvent::NetworkConnectivityChanged {
                internet_available: false,
            },
            true,
        ),
        None
    );
    assert_eq!(
        refresh_reason(
            HostEvent::NetworkConnectivityChanged {
                internet_available: true,
            },
            true,
        ),
        Some(RefreshReason::NetworkRestored)
    );
    assert_eq!(
        refresh_reason(
            HostEvent::NetworkConnectivityChanged {
                internet_available: true,
            },
            false,
        ),
        None
    );
}
