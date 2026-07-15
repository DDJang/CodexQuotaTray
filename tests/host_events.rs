use codex_quota_tray::host_events::{HostEvent, refresh_reason};
use codex_quota_tray::refresh::RefreshReason;

#[test]
fn visible_card_and_resume_map_to_bounded_refresh_reasons() {
    assert_eq!(
        refresh_reason(HostEvent::CardOpened),
        Some(RefreshReason::CardOpened)
    );
    assert_eq!(
        refresh_reason(HostEvent::SessionResumed),
        Some(RefreshReason::Resume)
    );
}

#[test]
fn only_restored_internet_connectivity_requests_a_refresh() {
    assert_eq!(
        refresh_reason(HostEvent::NetworkConnectivityChanged {
            internet_available: false,
        }),
        None
    );
    assert_eq!(
        refresh_reason(HostEvent::NetworkConnectivityChanged {
            internet_available: true,
        }),
        Some(RefreshReason::NetworkRestored)
    );
}
