use crate::refresh::{RefreshMode, RefreshReason};

pub const AUTO_CARD_OPEN_REFRESH_AGE_SECS: u64 = 2 * 60;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostEvent {
    CardOpened { last_success_age_secs: Option<u64> },
    SessionResumed,
    NetworkConnectivityChanged { internet_available: bool },
}

pub fn refresh_reason(
    event: HostEvent,
    refresh_on_network_restore: bool,
    mode: RefreshMode,
    stale_after_secs: u64,
) -> Option<RefreshReason> {
    if mode == RefreshMode::ManualOnly {
        return None;
    }
    match event {
        HostEvent::CardOpened {
            last_success_age_secs: None,
        } => Some(RefreshReason::CardOpened),
        HostEvent::CardOpened {
            last_success_age_secs: Some(age),
        } if mode == RefreshMode::Auto && age >= AUTO_CARD_OPEN_REFRESH_AGE_SECS => {
            Some(RefreshReason::CardOpened)
        }
        HostEvent::CardOpened {
            last_success_age_secs: Some(age),
        } if age >= stale_after_secs => Some(RefreshReason::CardOpened),
        HostEvent::CardOpened {
            last_success_age_secs: Some(_),
        } => None,
        HostEvent::SessionResumed => Some(RefreshReason::Resume),
        HostEvent::NetworkConnectivityChanged {
            internet_available: true,
        } if refresh_on_network_restore => Some(RefreshReason::NetworkRestored),
        HostEvent::NetworkConnectivityChanged {
            internet_available: true,
        } => None,
        HostEvent::NetworkConnectivityChanged {
            internet_available: false,
        } => None,
    }
}
