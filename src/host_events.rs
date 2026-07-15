use crate::refresh::RefreshReason;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostEvent {
    CardOpened,
    SessionResumed,
    NetworkConnectivityChanged { internet_available: bool },
}

pub fn refresh_reason(event: HostEvent) -> Option<RefreshReason> {
    match event {
        HostEvent::CardOpened => Some(RefreshReason::CardOpened),
        HostEvent::SessionResumed => Some(RefreshReason::Resume),
        HostEvent::NetworkConnectivityChanged {
            internet_available: true,
        } => Some(RefreshReason::NetworkRestored),
        HostEvent::NetworkConnectivityChanged {
            internet_available: false,
        } => None,
    }
}
