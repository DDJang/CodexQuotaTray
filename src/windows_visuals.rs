use std::mem::size_of;

use windows::Win32::Foundation::{HINSTANCE, HWND, RECT};
use windows::Win32::Graphics::Dwm::{
    DWM_WINDOW_CORNER_PREFERENCE, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE,
    DWMWCP_ROUND, DwmSetWindowAttribute,
};
use windows::Win32::Graphics::Gdi::HMONITOR;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, DPI_AWARENESS_PER_MONITOR_AWARE,
    GetAwarenessFromDpiAwarenessContext, GetThreadDpiAwarenessContext,
    SetProcessDpiAwarenessContext,
};
use windows::Win32::UI::Input::KeyboardAndMouse::{TME_LEAVE, TRACKMOUSEEVENT, TrackMouseEvent};
use windows::Win32::UI::Shell::GetScaleFactorForMonitor;
use windows::Win32::UI::WindowsAndMessaging::{HICON, LoadIconW};
use windows::core::PCWSTR;

use crate::ui_model::TrayIconState;

pub const APP_ICON_RESOURCE_ID: u16 = 101;
pub const BASE_DPI: u32 = 96;

pub fn ensure_per_monitor_v2() -> Result<(), String> {
    // SAFETY: This is called at the first line of the GUI entry point, before any HWND is created.
    // A manifest-configured process returns access denied here, so the effective context is the
    // source of truth rather than the setter result.
    let _ = unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    // SAFETY: Reading the current thread context has no side effects.
    let awareness = unsafe { GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) };
    if is_per_monitor_aware(awareness) {
        Ok(())
    } else {
        Err("Windows could not enable Per-Monitor V2 DPI awareness".to_owned())
    }
}

fn is_per_monitor_aware(awareness: DPI_AWARENESS) -> bool {
    awareness == DPI_AWARENESS_PER_MONITOR_AWARE
}

pub fn monitor_effective_dpi(monitor: HMONITOR, fallback: u32) -> u32 {
    // SAFETY: `monitor` was returned by MonitorFromPoint. This API reports the configured scale
    // independently of the caller's awareness, unlike GetDpiForMonitor in a PM-aware process.
    unsafe { GetScaleFactorForMonitor(monitor) }
        .ok()
        .map(|factor| dpi_from_scale_factor(factor.0, fallback))
        .unwrap_or_else(|| fallback.max(BASE_DPI))
}

fn dpi_from_scale_factor(percent: i32, fallback: u32) -> u32 {
    if percent < 100 {
        return fallback.max(BASE_DPI);
    }
    ((i64::from(BASE_DPI) * i64::from(percent) + 50) / 100)
        .clamp(i64::from(BASE_DPI), i64::from(u32::MAX)) as u32
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WindowChrome {
    DwmRounded,
    Solid,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum InteractionTarget {
    #[default]
    None,
    Refresh,
    Usage,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IconKind {
    App,
    Warning,
    Error,
    Information,
    Offline,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct RectI {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl RectI {
    pub fn width(self) -> i32 {
        self.right - self.left
    }

    pub fn height(self) -> i32 {
        self.bottom - self.top
    }

    pub fn contains(self, x: i32, y: i32) -> bool {
        x >= self.left && x < self.right && y >= self.top && y < self.bottom
    }

    pub fn inset(self, amount: i32) -> Self {
        Self {
            left: self.left + amount,
            top: self.top + amount,
            right: self.right - amount,
            bottom: self.bottom - amount,
        }
    }

    pub fn to_win32(self) -> RECT {
        RECT {
            left: self.left,
            top: self.top,
            right: self.right,
            bottom: self.bottom,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CardLayout {
    pub dpi: u32,
    pub width: i32,
    pub height: i32,
    pub icon: RectI,
    pub title: RectI,
    pub status_badge: RectI,
    pub status: RectI,
    pub window_panels: Vec<RectI>,
    pub credits: RectI,
    pub updated: RectI,
    pub warning: RectI,
    pub refresh_button: RectI,
    pub usage_button: RectI,
}

impl CardLayout {
    pub fn new(dpi: u32, window_count: usize) -> Self {
        let dpi = dpi.max(BASE_DPI);
        let window_count = window_count.min(3);
        let logical_height = (300 + window_count as i32 * 92).max(400);
        let scale = |value| scale_for_dpi(value, dpi);
        let logical_rect = |left, top, right, bottom| RectI {
            left: scale(left),
            top: scale(top),
            right: scale(right),
            bottom: scale(bottom),
        };
        let mut window_panels = Vec::with_capacity(window_count);
        for index in 0..window_count {
            let top = 92 + index as i32 * 98;
            window_panels.push(logical_rect(18, top, 382, top + 88));
        }
        let info_top = (92 + window_count as i32 * 98 + 8).max(190);
        let footer_top = logical_height - 58;
        Self {
            dpi,
            width: scale(400),
            height: scale(logical_height),
            icon: logical_rect(18, 18, 48, 48),
            title: logical_rect(58, 14, 270, 42),
            status_badge: logical_rect(286, 17, 382, 43),
            status: logical_rect(58, 44, 382, 72),
            window_panels,
            credits: logical_rect(18, info_top, 382, info_top + 44),
            updated: logical_rect(20, info_top + 48, 380, info_top + 72),
            warning: logical_rect(20, info_top + 72, 380, info_top + 94),
            refresh_button: logical_rect(18, footer_top, 140, footer_top + 40),
            usage_button: logical_rect(150, footer_top, 382, footer_top + 40),
        }
    }

    pub fn hit_test(&self, x: i32, y: i32) -> InteractionTarget {
        if self.refresh_button.contains(x, y) {
            InteractionTarget::Refresh
        } else if self.usage_button.contains(x, y) {
            InteractionTarget::Usage
        } else {
            InteractionTarget::None
        }
    }
}

pub fn scale_for_dpi(value: i32, dpi: u32) -> i32 {
    ((i64::from(value) * i64::from(dpi) + i64::from(BASE_DPI / 2)) / i64::from(BASE_DPI))
        .clamp(i64::from(i32::MIN), i64::from(i32::MAX)) as i32
}

pub fn icon_kind(state: TrayIconState) -> IconKind {
    match state {
        TrayIconState::Normal => IconKind::App,
        TrayIconState::Caution => IconKind::Warning,
        TrayIconState::Critical | TrayIconState::Exhausted => IconKind::Error,
        TrayIconState::Refreshing => IconKind::Information,
        TrayIconState::Offline => IconKind::Offline,
    }
}

pub fn status_label(state: TrayIconState) -> &'static str {
    match state {
        TrayIconState::Normal => "状态正常",
        TrayIconState::Caution => "额度偏低",
        TrayIconState::Critical => "额度紧张",
        TrayIconState::Exhausted => "额度耗尽",
        TrayIconState::Refreshing => "正在更新",
        TrayIconState::Offline => "当前离线",
    }
}

pub fn window_chrome_from_result(dwm_succeeded: bool) -> WindowChrome {
    if dwm_succeeded {
        WindowChrome::DwmRounded
    } else {
        WindowChrome::Solid
    }
}

pub fn configure_window_chrome(hwnd: HWND) -> WindowChrome {
    let dark_mode = 1_i32;
    let corner = DWMWCP_ROUND;
    // SAFETY: Every pointer references a stack value of the exact attribute type for the call.
    unsafe {
        let dark_mode_result = DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            (&dark_mode as *const i32).cast(),
            size_of::<i32>() as u32,
        );
        let corner_result = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            (&corner as *const DWM_WINDOW_CORNER_PREFERENCE).cast(),
            size_of_val(&corner) as u32,
        );
        window_chrome_from_result(dark_mode_result.is_ok() || corner_result.is_ok())
    }
}

pub fn load_app_icon(instance: HINSTANCE) -> Result<HICON, String> {
    let resource = PCWSTR(usize::from(APP_ICON_RESOURCE_ID) as *const u16);
    // SAFETY: The integer resource ID is embedded in this module by build.rs/windows.rc.
    unsafe { LoadIconW(Some(instance), resource) }
        .map_err(|_| "native Windows operation failed: load embedded app icon".to_owned())
}

pub fn request_mouse_leave(hwnd: HWND) -> Result<(), String> {
    let mut event = TRACKMOUSEEVENT {
        cbSize: size_of::<TRACKMOUSEEVENT>() as u32,
        dwFlags: TME_LEAVE,
        hwndTrack: hwnd,
        ..Default::default()
    };
    // SAFETY: `event` is fully initialized and lives through the synchronous call.
    unsafe { TrackMouseEvent(&mut event) }
        .map_err(|_| "native Windows operation failed: track mouse leave".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_per_monitor_awareness_is_accepted_for_crisp_rendering() {
        assert!(is_per_monitor_aware(DPI_AWARENESS_PER_MONITOR_AWARE));
        assert!(!is_per_monitor_aware(
            windows::Win32::UI::HiDpi::DPI_AWARENESS_UNAWARE
        ));
        assert!(!is_per_monitor_aware(
            windows::Win32::UI::HiDpi::DPI_AWARENESS_SYSTEM_AWARE
        ));
    }

    #[test]
    fn monitor_scale_factor_maps_to_physical_window_dpi() {
        assert_eq!(dpi_from_scale_factor(100, 144), 96);
        assert_eq!(dpi_from_scale_factor(125, 96), 120);
        assert_eq!(dpi_from_scale_factor(150, 96), 144);
        assert_eq!(dpi_from_scale_factor(200, 96), 192);
        assert_eq!(dpi_from_scale_factor(0, 144), 144);
    }

    #[test]
    fn layout_stays_inside_the_card_at_supported_dpis() {
        for dpi in [96, 120, 144, 192] {
            for count in 0..=3 {
                let layout = CardLayout::new(dpi, count);
                let all_rects = layout.window_panels.iter().copied().chain([
                    layout.icon,
                    layout.title,
                    layout.status_badge,
                    layout.status,
                    layout.credits,
                    layout.updated,
                    layout.refresh_button,
                    layout.usage_button,
                ]);
                for area in all_rects {
                    assert!(area.left >= 0 && area.top >= 0);
                    assert!(area.right <= layout.width && area.bottom <= layout.height);
                    assert!(area.width() > 0 && area.height() > 0);
                }
                assert_eq!(layout.window_panels.len(), count);
                assert!(layout.refresh_button.bottom <= layout.height);
                assert!(layout.usage_button.bottom <= layout.height);
            }
        }
    }

    #[test]
    fn window_panels_and_footer_never_overlap() {
        for dpi in [96, 120, 144, 192] {
            for count in 0..=3 {
                let layout = CardLayout::new(dpi, count);
                if let Some(last) = layout.window_panels.last() {
                    assert!(last.bottom < layout.credits.top);
                }
                assert!(layout.updated.bottom <= layout.refresh_button.top);
            }
        }
    }

    #[test]
    fn hit_testing_uses_the_same_button_rectangles_as_painting() {
        let layout = CardLayout::new(144, 3);
        for (area, expected) in [
            (layout.refresh_button, InteractionTarget::Refresh),
            (layout.usage_button, InteractionTarget::Usage),
        ] {
            assert_eq!(
                layout.hit_test((area.left + area.right) / 2, (area.top + area.bottom) / 2),
                expected
            );
        }
        assert_eq!(layout.hit_test(0, 0), InteractionTarget::None);
    }

    #[test]
    fn tray_states_keep_shape_distinctions() {
        assert_eq!(icon_kind(TrayIconState::Normal), IconKind::App);
        assert_eq!(icon_kind(TrayIconState::Caution), IconKind::Warning);
        assert_eq!(icon_kind(TrayIconState::Critical), IconKind::Error);
        assert_eq!(icon_kind(TrayIconState::Exhausted), IconKind::Error);
        assert_eq!(icon_kind(TrayIconState::Refreshing), IconKind::Information);
        assert_eq!(icon_kind(TrayIconState::Offline), IconKind::Offline);
    }

    #[test]
    fn failed_dwm_configuration_selects_safe_fallback() {
        assert_eq!(window_chrome_from_result(false), WindowChrome::Solid);
        assert_eq!(window_chrome_from_result(true), WindowChrome::DwmRounded);
    }
}
