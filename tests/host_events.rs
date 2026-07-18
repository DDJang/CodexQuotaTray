use codex_quota_tray::host_events::{HostEvent, refresh_reason};
use codex_quota_tray::refresh::{RefreshMode, RefreshReason};

#[test]
fn all_host_events_obey_refresh_mode() {
    let events = [
        HostEvent::CardOpened {
            last_success_age_secs: None,
        },
        HostEvent::SessionResumed,
        HostEvent::NetworkConnectivityChanged {
            internet_available: true,
        },
    ];
    for event in events {
        assert_eq!(
            refresh_reason(event, true, RefreshMode::ManualOnly, 3_600),
            None
        );
    }
    assert_eq!(
        refresh_reason(
            HostEvent::CardOpened {
                last_success_age_secs: Some(120)
            },
            true,
            RefreshMode::Auto,
            3_600
        ),
        Some(RefreshReason::CardOpened)
    );
    assert_eq!(
        refresh_reason(HostEvent::SessionResumed, true, RefreshMode::Auto, 3_600),
        Some(RefreshReason::Resume)
    );
    assert_eq!(
        refresh_reason(
            HostEvent::NetworkConnectivityChanged {
                internet_available: true
            },
            true,
            RefreshMode::Auto,
            3_600
        ),
        Some(RefreshReason::NetworkRestored)
    );
    assert_eq!(
        refresh_reason(
            HostEvent::NetworkConnectivityChanged {
                internet_available: false
            },
            true,
            RefreshMode::Auto,
            3_600
        ),
        None
    );
}
