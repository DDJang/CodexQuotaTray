use crate::refresh::RefreshReason;

pub const CARD_OPEN_REFRESH_AGE_SECS: u64 = 60;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostEvent {
    CardOpened { last_success_age_secs: Option<u64> },
    SessionResumed,
    NetworkConnectivityChanged { internet_available: bool },
}

pub fn refresh_reason(event: HostEvent, refresh_on_network_restore: bool) -> Option<RefreshReason> {
    match event {
        HostEvent::CardOpened {
            last_success_age_secs: None,
        } => Some(RefreshReason::CardOpened),
        HostEvent::CardOpened {
            last_success_age_secs: Some(age),
        } if age >= CARD_OPEN_REFRESH_AGE_SECS => Some(RefreshReason::CardOpened),
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
