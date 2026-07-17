use std::mem::size_of;

use windows::Win32::Foundation::{GetLastError, HINSTANCE, HWND, RECT};
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
use windows::Win32::UI::WindowsAndMessaging::{HICON, IMAGE_FLAGS, IMAGE_ICON, LoadImageW};
use windows::core::PCWSTR;

use crate::ui_model::TrayIconState;

pub const APP_ICON_RESOURCE_ID: u16 = 101;
pub const BASE_DPI: u32 = 96;
pub const DEFAULT_CREDITS_LINE_HEIGHT: i32 = 14;
pub const DEFAULT_CREDITS_ICON_SIZE: i32 = 16;

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

#[allow(dead_code)]
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
    pub title: RectI,
    pub status: RectI,
    pub window_panels: Vec<RectI>,
    pub credits: RectI,
    pub credits_icon: RectI,
    pub credits_text: RectI,
    pub warning: RectI,
    pub refresh_button: RectI,
    pub usage_button: RectI,
}

impl CardLayout {
    pub fn new(dpi: u32, window_count: usize, show_warning: bool) -> Self {
        Self::new_with_metrics(
            dpi,
            window_count,
            show_warning,
            scale_for_dpi(DEFAULT_CREDITS_LINE_HEIGHT, dpi.max(BASE_DPI)),
            scale_for_dpi(DEFAULT_CREDITS_ICON_SIZE, dpi.max(BASE_DPI)),
        )
    }

    /// Builds the layout in physical pixels using the metrics of the fonts that will actually be
    /// selected into the backing DC. Keeping those measurements here prevents a high-DPI font
    /// from being placed in a fixed-height, low-DPI information strip.
    pub fn new_with_metrics(
        dpi: u32,
        window_count: usize,
        show_warning: bool,
        credits_line_height: i32,
        credits_icon_size: i32,
    ) -> Self {
        let dpi = dpi.max(BASE_DPI);
        let window_count = window_count.min(3);
        let scale = |value| scale_for_dpi(value, dpi);
        let logical_rect = |left, top, right, bottom| RectI {
            left: scale(left),
            top: scale(top),
            right: scale(right),
            bottom: scale(bottom),
        };
        let mut window_panels = Vec::with_capacity(window_count);
        for index in 0..window_count {
            let top = 82 + index as i32 * 112;
            window_panels.push(logical_rect(18, top, 382, top + 104));
        }
        let last_panel_bottom = if window_count == 0 {
            82
        } else {
            82 + (window_count as i32 - 1) * 112 + 104
        };
        let credits_top = last_panel_bottom + 10;
        let credits_padding = scale(8);
        let credits_content_height = credits_line_height.max(credits_icon_size).max(1);
        let credits_height = credits_content_height + credits_padding * 2;
        let credits_bottom = credits_top
            + ((credits_height * i32::try_from(BASE_DPI).unwrap_or(96)
                + i32::try_from(dpi / 2).unwrap_or(48))
                / i32::try_from(dpi).unwrap_or(96));
        let warning_top = credits_bottom + 8;
        let warning = if show_warning {
            logical_rect(20, warning_top, 380, warning_top + 22)
        } else {
            RectI::default()
        };
        let footer_top = if show_warning {
            warning_top + 22 + 8
        } else {
            credits_bottom + 12
        };
        let height = scale(footer_top + 40 + 14);
        let credits = logical_rect(18, credits_top, 382, credits_bottom);
        let icon_top = credits.top + (credits.height() - credits_icon_size) / 2;
        let credits_icon = RectI {
            left: credits.left + credits_padding,
            top: icon_top,
            right: credits.left + credits_padding + credits_icon_size,
            bottom: icon_top + credits_icon_size,
        };
        let credits_text = RectI {
            left: credits.left + credits_padding + credits_icon_size + scale(8),
            top: credits.top + credits_padding,
            right: credits.right - credits_padding,
            bottom: credits.bottom - credits_padding,
        };
        Self {
            dpi,
            width: scale(400),
            height,
            title: logical_rect(18, 14, 382, 42),
            status: logical_rect(18, 44, 382, 72),
            window_panels,
            credits,
            credits_icon,
            credits_text,
            warning,
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

pub fn progress_fill_rect(rail: RectI, percent: u8) -> RectI {
    if rail.width() <= 0 || rail.height() <= 0 || percent == 0 {
        return RectI {
            left: rail.left,
            top: rail.top,
            right: rail.left,
            bottom: rail.bottom,
        };
    }
    let percent = i32::from(percent.min(100));
    let width = ((i64::from(rail.width()) * i64::from(percent) + 50) / 100)
        .clamp(1, i64::from(rail.width())) as i32;
    RectI {
        left: rail.left,
        top: rail.top,
        right: rail.left + width,
        bottom: rail.bottom,
    }
}

pub fn progress_radius(rail: RectI, fill: RectI) -> i32 {
    if fill.width() <= 0 || rail.height() <= 0 {
        return 0;
    }
    (rail.height() / 2).min(fill.width() / 2)
}

pub fn scale_for_dpi(value: i32, dpi: u32) -> i32 {
    ((i64::from(value) * i64::from(dpi) + i64::from(BASE_DPI / 2)) / i64::from(BASE_DPI))
        .clamp(i64::from(i32::MIN), i64::from(i32::MAX)) as i32
}

/// Selects the nearest embedded ICO frame that is at least as large as the requested physical
/// size. Downsampling a nearby larger frame is preferable to enlarging a smaller frame, which is
/// the source of the previous high-DPI blur.
pub fn icon_resource_size_for_target(target: i32, large: bool) -> i32 {
    let candidates: &[i32] = if large {
        &[32, 40, 48, 64, 128, 256]
    } else {
        &[16, 20, 24, 32, 40, 48, 64, 128, 256]
    };
    let target = target.max(1);
    candidates
        .iter()
        .copied()
        .find(|size| *size >= target)
        .unwrap_or_else(|| *candidates.last().unwrap_or(&target))
}

/// Maps the supported Per-Monitor DPI values to the embedded frame used for a window or tray
/// icon. The small-icon mapping includes 20/24px frames used by scaled title bars and shells.
pub fn icon_resource_size_for_dpi(dpi: u32, large: bool) -> i32 {
    let dpi = dpi.max(BASE_DPI);
    let base = if large { 32 } else { 16 };
    icon_resource_size_for_target(scale_for_dpi(base, dpi), large)
}

/// Place a popup flush with the lower-right corner of a monitor work area.
/// The work area excludes the taskbar; clamping also handles tiny or negative-coordinate monitors.
pub fn bottom_right_popup_origin(work: RectI, width: i32, height: i32) -> (i32, i32) {
    let width = width.max(1);
    let height = height.max(1);
    let max_x = (work.right - width).max(work.left);
    let max_y = (work.bottom - height).max(work.top);
    (
        (work.right - width).clamp(work.left, max_x),
        (work.bottom - height).clamp(work.top, max_y),
    )
}

#[allow(dead_code)]
pub fn icon_kind(state: TrayIconState) -> IconKind {
    match state {
        TrayIconState::Normal => IconKind::App,
        TrayIconState::Caution => IconKind::Warning,
        TrayIconState::Critical | TrayIconState::Exhausted => IconKind::Error,
        TrayIconState::Refreshing => IconKind::Information,
        TrayIconState::Offline => IconKind::Offline,
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
    let dark_mode = 0_i32;
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

pub fn load_app_icon(instance: HINSTANCE, width: i32, height: i32) -> Result<HICON, String> {
    let resource = PCWSTR(usize::from(APP_ICON_RESOURCE_ID) as *const u16);
    // SAFETY: The integer resource ID is embedded in this module by build.rs/windows.rc. The
    // returned handle is an owned icon because LR_SHARED is intentionally not requested.
    let handle = unsafe {
        LoadImageW(
            Some(instance),
            resource,
            IMAGE_ICON,
            width,
            height,
            IMAGE_FLAGS(0),
        )
    }
    .map_err(|error| {
        let last_error = unsafe { GetLastError().0 };
        format!("load embedded app icon {width}x{height} failed: {error} (last_error={last_error})")
    })?;
    let icon = HICON(handle.0);
    if icon.is_invalid() {
        let last_error = unsafe { GetLastError().0 };
        return Err(format!(
            "load embedded app icon {width}x{height} returned null (last_error={last_error})"
        ));
    }
    Ok(icon)
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
    fn icon_frames_follow_dpi_without_upscaling_a_smaller_frame() {
        assert_eq!(icon_resource_size_for_dpi(96, true), 32);
        assert_eq!(icon_resource_size_for_dpi(120, true), 40);
        assert_eq!(icon_resource_size_for_dpi(144, true), 48);
        assert_eq!(icon_resource_size_for_dpi(168, true), 64);
        assert_eq!(icon_resource_size_for_dpi(192, true), 64);

        assert_eq!(icon_resource_size_for_dpi(96, false), 16);
        assert_eq!(icon_resource_size_for_dpi(120, false), 20);
        assert_eq!(icon_resource_size_for_dpi(144, false), 24);
        assert_eq!(icon_resource_size_for_dpi(168, false), 32);
        assert_eq!(icon_resource_size_for_dpi(192, false), 32);

        assert_eq!(icon_resource_size_for_target(33, true), 40);
        assert_eq!(icon_resource_size_for_target(59, true), 64);
        assert_eq!(icon_resource_size_for_target(25, false), 32);
    }

    #[test]
    fn header_text_aligns_with_the_card_content_margin() {
        for dpi in [96, 120, 144, 168, 192] {
            let layout = CardLayout::new(dpi, 1, false);
            let panel = layout.window_panels[0];
            assert_eq!(layout.title.left, panel.left);
            assert_eq!(layout.status.left, panel.left);
            assert_eq!(layout.title.right, panel.right);
            assert_eq!(layout.status.right, panel.right);
        }
    }

    #[test]
    fn layout_stays_inside_the_card_at_supported_dpis() {
        for dpi in [96, 120, 144, 168, 192] {
            for count in 0..=3 {
                for show_warning in [false, true] {
                    let layout = CardLayout::new(dpi, count, show_warning);
                    let mut all_rects = layout.window_panels.clone();
                    all_rects.extend([
                        layout.title,
                        layout.status,
                        layout.credits,
                        layout.credits_icon,
                        layout.credits_text,
                        layout.refresh_button,
                        layout.usage_button,
                    ]);
                    if show_warning {
                        all_rects.push(layout.warning);
                    }
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
    }

    #[test]
    fn window_panels_and_footer_never_overlap() {
        for dpi in [96, 120, 144, 168, 192] {
            for count in 0..=3 {
                let layout = CardLayout::new(dpi, count, false);
                if let Some(last) = layout.window_panels.last() {
                    assert!(last.bottom < layout.credits.top);
                }
                assert!(layout.credits.bottom <= layout.refresh_button.top);
                assert_eq!(layout.warning, RectI::default());
                let warning_layout = CardLayout::new(dpi, count, true);
                assert!(warning_layout.height > layout.height);
                assert!(warning_layout.warning.bottom <= warning_layout.refresh_button.top);
            }
        }
    }

    #[test]
    fn credit_strip_is_sized_from_real_text_and_icon_metrics() {
        for dpi in [96, 120, 144, 168, 192] {
            let line_height = scale_for_dpi(14, dpi);
            let icon_size = scale_for_dpi(16, dpi);
            let layout = CardLayout::new_with_metrics(dpi, 0, false, line_height, icon_size);
            let padding = scale_for_dpi(8, dpi);
            let content_height = line_height.max(icon_size);
            assert!(layout.credits.height() >= content_height + padding * 2);
            assert!(
                layout
                    .credits
                    .contains(layout.credits_icon.left, layout.credits_icon.top)
            );
            assert!(layout.credits_text.left < layout.credits_text.right);
            assert!(layout.credits_text.top < layout.credits_text.bottom);
            assert!(layout.credits_text.bottom <= layout.credits.bottom - padding);
            assert_eq!(layout.credits_icon.height(), icon_size);
            assert_eq!(
                layout.credits_icon.top + layout.credits_icon.height() / 2,
                layout.credits.top + layout.credits.height() / 2
            );
        }
    }

    #[test]
    fn progress_geometry_stays_inside_the_rail_at_all_endpoints() {
        let rail = RectI {
            left: 10,
            top: 20,
            right: 210,
            bottom: 28,
        };
        for percent in [0, 1, 20, 50, 99, 100] {
            let fill = progress_fill_rect(rail, percent);
            assert_eq!(fill.top, rail.top);
            assert_eq!(fill.bottom, rail.bottom);
            assert!(fill.left >= rail.left && fill.right <= rail.right);
            if percent == 0 {
                assert_eq!(fill.width(), 0);
                assert_eq!(progress_radius(rail, fill), 0);
            } else {
                assert!(progress_radius(rail, fill) <= rail.height() / 2);
            }
        }
        let tiny = progress_fill_rect(rail, 1);
        assert!(progress_radius(rail, tiny) <= tiny.width() / 2);
    }

    #[test]
    fn hit_testing_uses_the_same_button_rectangles_as_painting() {
        let layout = CardLayout::new(144, 3, false);
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

    #[test]
    fn popup_is_flush_with_work_area_bottom_right() {
        assert_eq!(
            bottom_right_popup_origin(
                RectI {
                    left: 0,
                    top: 0,
                    right: 1920,
                    bottom: 1040
                },
                400,
                500,
            ),
            (1520, 540)
        );
        assert_eq!(
            bottom_right_popup_origin(
                RectI {
                    left: -1920,
                    top: 0,
                    right: 0,
                    bottom: 1040
                },
                400,
                500,
            ),
            (-400, 540)
        );
    }

    #[test]
    fn popup_clamps_when_card_is_larger_than_work_area() {
        assert_eq!(
            bottom_right_popup_origin(
                RectI {
                    left: 10,
                    top: 20,
                    right: 100,
                    bottom: 80
                },
                400,
                500,
            ),
            (10, 20)
        );
    }
}
