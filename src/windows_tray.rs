use std::ffi::OsString;
use std::mem::size_of;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

use windows::Win32::Foundation::{
    COLORREF, CloseHandle, ERROR_FILE_NOT_FOUND, ERROR_SUCCESS, HANDLE, HINSTANCE, HWND, LPARAM,
    LRESULT, POINT, RECT, WAIT_OBJECT_0, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, BitBlt, CLIP_DEFAULT_PRECIS, CreateCompatibleBitmap, CreateCompatibleDC,
    CreateFontW, CreatePen, CreateSolidBrush, DEFAULT_CHARSET, DEFAULT_PITCH, DRAW_TEXT_FORMAT,
    DT_CENTER, DT_END_ELLIPSIS, DT_LEFT, DT_RIGHT, DT_SINGLELINE, DT_VCENTER, DeleteDC,
    DeleteObject, DrawTextW, EndPaint, FF_DONTCARE, FONT_QUALITY, FW_NORMAL, FW_SEMIBOLD, FillRect,
    GetMonitorInfoW, HDC, HGDIOBJ, InvalidateRect, MONITOR_DEFAULTTONEAREST, MONITORINFO,
    MonitorFromPoint, OUT_DEFAULT_PRECIS, PAINTSTRUCT, PS_SOLID, RoundRect, SRCCOPY, SelectObject,
    SetBkMode, SetTextColor, TRANSPARENT, UpdateWindow,
};
use windows::Win32::NetworkManagement::IpHelper::{
    CancelMibChangeNotify2, NotifyNetworkConnectivityHintChange,
};
use windows::Win32::Networking::WinSock::{
    NL_NETWORK_CONNECTIVITY_HINT, NetworkConnectivityLevelHintInternetAccess,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ, RRF_RT_REG_SZ,
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegGetValueW, RegSetValueExW,
};
use windows::Win32::System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture};
use windows::Win32::UI::Shell::{
    NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIIF_ERROR, NIIF_INFO, NIIF_NOSOUND,
    NIIF_RESPECT_QUIET_TIME, NIIF_WARNING, NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
    Shell_NotifyIconW, ShellExecuteW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, CreatePopupMenu,
    CreateWindowExW, DI_NORMAL, DefWindowProcW, DestroyMenu, DestroyWindow, DrawIconEx,
    FindWindowW, GWLP_USERDATA, GetClientRect, GetCursorPos, GetMessageW, GetWindowThreadProcessId,
    HICON, HMENU, HWND_TOPMOST, IDC_ARROW, IDI_ERROR, IDI_INFORMATION, IDI_QUESTION, IDI_WARNING,
    IsWindowVisible, KillTimer, LoadCursorW, LoadIconW, MF_CHECKED, MF_SEPARATOR, MF_STRING, MSG,
    PBT_APMRESUMEAUTOMATIC, PostMessageW, PostQuitMessage, RegisterClassW, SW_HIDE,
    SW_SHOWNOACTIVATE, SWP_NOACTIVATE, SWP_SHOWWINDOW, SetForegroundWindow, SetTimer,
    SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow, TPM_RETURNCMD, TPM_RIGHTBUTTON,
    TrackPopupMenu, WA_INACTIVE, WM_ACTIVATE, WM_APP, WM_CLOSE, WM_DESTROY, WM_DPICHANGED,
    WM_ENDSESSION, WM_KEYUP, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE, WM_NCCREATE, WM_PAINT,
    WM_POWERBROADCAST, WM_QUERYENDSESSION, WM_RBUTTONUP, WM_TIMER, WNDCLASSW, WS_EX_TOOLWINDOW,
    WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::{PCWSTR, w};

use crate::alerts::{AlertKind, AlertTracker, QuotaAlert};
use crate::host_events::{HostEvent, refresh_reason};
use crate::persistence::{AppSettings, PersistencePaths, QuotaCacheStore, SettingsStore};
use crate::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use crate::refresh::{RefreshPolicy, RefreshReason};
use crate::runtime::{QuotaRuntime, RuntimeConfig};
use crate::state::{AppState, AuthState, DataState};
use crate::ui_model::{TrayIconState, TrayView, ViewPreferences, project_tray_view};
use crate::windows_visuals::{
    CardLayout, IconKind, InteractionTarget, configure_window_chrome, ensure_per_monitor_v2,
    icon_kind, load_app_icon, monitor_effective_dpi, request_mouse_leave, scale_for_dpi,
    status_label,
};

const WINDOW_CLASS: PCWSTR = w!("CodexQuotaTrayWindow");
const WINDOW_TITLE: PCWSTR = w!("CodexQuotaTray");
const TRAY_CALLBACK: u32 = WM_APP + 17;
const NETWORK_RESTORED_MESSAGE: u32 = WM_APP + 18;
const SHOW_CARD_MESSAGE: u32 = WM_APP + 19;
const TRAY_ID: u32 = 1;
const TIMER_ID: usize = 1;
const TIMER_MILLIS: u32 = 30_000;
const CMD_REFRESH: usize = 1001;
const CMD_OPEN_USAGE: usize = 1002;
const CMD_TOGGLE_CACHE: usize = 1003;
const CMD_TOGGLE_NOTIFICATIONS: usize = 1004;
const CMD_TOGGLE_STARTUP: usize = 1005;
const CMD_CLEAR_CACHE: usize = 1006;
const CMD_EXIT: usize = 1099;
const KEY_ESCAPE: usize = 0x1b;
const KEY_RETURN: usize = 0x0d;
const KEY_SPACE: usize = 0x20;
const KEY_TAB: usize = 0x09;
const KEY_F10: usize = 0x79;
const WM_MOUSELEAVE_LOCAL: u32 = 0x02a3;
const USAGE_URL: PCWSTR = w!("https://chatgpt.com/codex/settings/usage");

#[derive(Debug, Clone)]
pub struct WindowsTrayOptions {
    pub demo: bool,
    pub codex_bin: Option<OsString>,
}

pub fn initialize_dpi_awareness() -> Result<(), String> {
    ensure_per_monitor_v2()
}

pub fn run(options: WindowsTrayOptions) -> Result<(), String> {
    // SAFETY: The Win32 window and message loop are created and consumed on this thread.
    unsafe { run_inner(options) }
}

pub fn request_existing_shutdown() -> Result<bool, String> {
    // SAFETY: The class name is static and a null title matches any window in the class.
    let Ok(existing) = (unsafe { FindWindowW(WINDOW_CLASS, PCWSTR::null()) }) else {
        return Ok(false);
    };
    let mut process_id = 0_u32;
    // SAFETY: `existing` is a live window handle and `process_id` is valid writable storage.
    unsafe { GetWindowThreadProcessId(existing, Some(&mut process_id)) };
    if process_id == 0 {
        return Err("could not identify the existing tray process".to_owned());
    }
    // SAFETY: Synchronize-only access is sufficient to wait without inspecting process data.
    let process = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, process_id) }
        .map_err(win_error("open existing tray process"))?;
    // SAFETY: Posting WM_CLOSE allows the target UI thread to run its ordinary cleanup path.
    if let Err(error) = unsafe { PostMessageW(Some(existing), WM_CLOSE, WPARAM(0), LPARAM(0)) } {
        // SAFETY: This function owns the synchronization handle.
        let _ = unsafe { CloseHandle(process) };
        return Err(win_error("request existing tray shutdown")(error));
    }
    // SAFETY: `process` remains valid until it is closed below.
    let wait_result = unsafe { WaitForSingleObject(process, 10_000) };
    // SAFETY: This function owns the synchronization handle.
    let _ = unsafe { CloseHandle(process) };
    if wait_result != WAIT_OBJECT_0 {
        return Err("existing tray did not shut down within 10 seconds".to_owned());
    }
    Ok(true)
}

unsafe fn run_inner(options: WindowsTrayOptions) -> Result<(), String> {
    // SAFETY: Static UTF-16 class/title pointers are valid for the duration of the call.
    if let Ok(existing) = unsafe { FindWindowW(WINDOW_CLASS, PCWSTR::null()) } {
        // SAFETY: `existing` is a window returned by the OS. The owning UI thread performs the
        // card-open refresh and foreground transition.
        unsafe { PostMessageW(Some(existing), SHOW_CARD_MESSAGE, WPARAM(0), LPARAM(0)) }
            .map_err(win_error("activate existing tray window"))?;
        return Ok(());
    }

    let (settings, settings_store, cache_store, settings_warning) = load_persistence(options.demo);
    let source = if options.demo {
        StateSource::Demo {
            state: demo_state(0),
            step: 0,
        }
    } else {
        let mut config = RuntimeConfig::codex(options.codex_bin);
        config.refresh_policy =
            RefreshPolicy::new(10, u64::from(settings.fallback_refresh_minutes) * 60, 15)?;
        config.quota_cache = cache_store.clone();
        StateSource::Runtime(QuotaRuntime::start(config)?)
    };
    // SAFETY: Null module name requests the current module.
    let module = unsafe { GetModuleHandleW(None) }.map_err(win_error("get module handle"))?;
    let instance = HINSTANCE(module.0);
    let app_icon = load_app_icon(instance)?;
    let mut context = Box::new(AppContext {
        hwnd: None,
        source,
        settings,
        settings_store,
        cache_store,
        alert_tracker: AlertTracker::new(),
        view: None,
        tray_added: false,
        settings_warning,
        network_notification: None,
        dpi: 96,
        hover: InteractionTarget::None,
        pressed: InteractionTarget::None,
        focus: InteractionTarget::Refresh,
        tracking_mouse: false,
        app_icon,
    });

    // SAFETY: The stock cursor resource is owned by Windows.
    let cursor = unsafe { LoadCursorW(None, IDC_ARROW) }.map_err(win_error("load cursor"))?;
    let window_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        hCursor: cursor,
        hIcon: app_icon,
        lpszClassName: WINDOW_CLASS,
        ..Default::default()
    };
    // SAFETY: `window_class` contains valid handles and a static class name.
    if unsafe { RegisterClassW(&window_class) } == 0 {
        return Err("could not register the native tray window class".to_owned());
    }

    let context_ptr = (&mut *context as *mut AppContext).cast();
    let extended_style = if options.demo {
        WS_EX_TOPMOST
    } else {
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST
    };
    let initial_layout = CardLayout::new(96, 2);
    // SAFETY: The context pointer remains stable in `Box` until after the message loop exits.
    let hwnd = unsafe {
        CreateWindowExW(
            extended_style,
            WINDOW_CLASS,
            WINDOW_TITLE,
            WS_POPUP,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            initial_layout.width,
            initial_layout.height,
            None,
            None,
            Some(instance),
            Some(context_ptr),
        )
    }
    .map_err(win_error("create native tray window"))?;
    context.hwnd = Some(hwnd);
    context.dpi = unsafe { GetDpiForWindow(hwnd) }.max(96);
    let _ = configure_window_chrome(hwnd);
    if context.settings.refresh_on_network_restore {
        context.network_notification = register_network_notifications(hwnd);
    }
    context.refresh_projection()?;
    context.add_tray_icon()?;
    // SAFETY: `hwnd` belongs to this thread and `TIMER_ID` is application-owned.
    if unsafe { SetTimer(Some(hwnd), TIMER_ID, TIMER_MILLIS, None) } == 0 {
        context.delete_tray_icon();
        return Err("could not start the low-frequency tray timer".to_owned());
    }
    if options.demo {
        context.toggle_card()?;
    }

    let mut message = MSG::default();
    loop {
        // SAFETY: `message` is a valid out parameter and this is the owning UI thread.
        let result = unsafe { GetMessageW(&mut message, None, 0, 0) };
        if result.0 == -1 {
            break Err("native Windows message loop failed".to_owned());
        }
        if result.0 == 0 {
            break Ok(());
        }
        // SAFETY: Message came from GetMessageW.
        unsafe {
            let _ = windows::Win32::UI::WindowsAndMessaging::TranslateMessage(&message);
            windows::Win32::UI::WindowsAndMessaging::DispatchMessageW(&message);
        }
    }?;

    // SAFETY: Timer and tray icon belong to this context/window.
    let _ = unsafe { KillTimer(Some(hwnd), TIMER_ID) };
    context.delete_tray_icon();
    let network_cleanup = context.unregister_network_notifications();
    let runtime_cleanup = context.shutdown_source();
    network_cleanup.and(runtime_cleanup)
}

struct AppContext {
    hwnd: Option<HWND>,
    source: StateSource,
    settings: AppSettings,
    settings_store: Option<SettingsStore>,
    cache_store: Option<QuotaCacheStore>,
    alert_tracker: AlertTracker,
    view: Option<TrayView>,
    tray_added: bool,
    settings_warning: bool,
    network_notification: Option<HANDLE>,
    dpi: u32,
    hover: InteractionTarget,
    pressed: InteractionTarget,
    focus: InteractionTarget,
    tracking_mouse: bool,
    app_icon: HICON,
}

enum StateSource {
    Runtime(QuotaRuntime),
    Demo { state: AppState, step: usize },
}

impl StateSource {
    fn snapshot(&self) -> AppState {
        match self {
            Self::Runtime(runtime) => runtime.snapshot(),
            Self::Demo { state, .. } => state.clone(),
        }
    }

    fn refresh(&mut self, reason: RefreshReason) -> Result<(), String> {
        match self {
            Self::Runtime(runtime) => runtime.request_refresh(reason),
            Self::Demo { state, step } => {
                if reason == RefreshReason::Manual {
                    *step = step.saturating_add(1);
                    *state = demo_state(*step);
                }
                Ok(())
            }
        }
    }
}

impl AppContext {
    fn hwnd(&self) -> Result<HWND, String> {
        self.hwnd
            .ok_or_else(|| "native tray window was unavailable".to_owned())
    }

    fn preferences(&self) -> ViewPreferences {
        ViewPreferences {
            show_remaining_percent: self.settings.show_remaining_percent,
            use_24_hour_time: self.settings.use_24_hour_time,
        }
    }

    fn layout(&self) -> CardLayout {
        CardLayout::new(
            self.dpi,
            self.view
                .as_ref()
                .map(|view| view.windows.len())
                .unwrap_or_default(),
        )
    }

    fn invalidate(&self) {
        if let Some(hwnd) = self.hwnd {
            // SAFETY: The window belongs to this UI thread and no erase is needed for custom paint.
            unsafe {
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
        }
    }

    fn invalidate_buttons(&self) {
        if let Some(hwnd) = self.hwnd {
            let layout = self.layout();
            let area = RECT {
                left: layout.refresh_button.left,
                top: layout.refresh_button.top,
                right: layout.usage_button.right,
                bottom: layout.usage_button.bottom,
            };
            // SAFETY: `area` lives through the call and both button rectangles are inside client.
            unsafe {
                let _ = InvalidateRect(Some(hwnd), Some(&area), false);
            }
        }
    }

    fn refresh_projection(&mut self) -> Result<(), String> {
        let state = self.source.snapshot();
        let view = project_tray_view(&state, unix_now(), self.preferences());
        if matches!(state.data, DataState::Fresh)
            && let Some(quota) = state.quota.as_ref()
        {
            for alert in self
                .alert_tracker
                .observe(quota, &self.settings.notifications)
            {
                self.show_alert(&alert)?;
            }
        }
        let changed = self.view.as_ref() != Some(&view);
        self.view = Some(view);
        if let (Some(hwnd), Some(view)) = (self.hwnd, self.view.as_ref()) {
            set_window_title(hwnd, &view.tooltip)?;
        }
        if changed && self.tray_added {
            self.modify_tray_icon()?;
        }
        if changed && let Some(hwnd) = self.hwnd {
            // SAFETY: `hwnd` is owned by this UI thread; null rect invalidates all client area.
            unsafe {
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
        }
        Ok(())
    }

    fn add_tray_icon(&mut self) -> Result<(), String> {
        let data = self.tray_data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP)?;
        // SAFETY: `data` is fully initialized and references no borrowed pointers.
        if !unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.as_bool() {
            return Err("Windows rejected the tray icon".to_owned());
        }
        self.tray_added = true;
        Ok(())
    }

    fn modify_tray_icon(&self) -> Result<(), String> {
        let data = self.tray_data(NIF_ICON | NIF_TIP | NIF_SHOWTIP)?;
        // SAFETY: `data` is fully initialized and the icon has already been added.
        if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) }.as_bool() {
            return Err("Windows rejected a tray icon update".to_owned());
        }
        Ok(())
    }

    fn delete_tray_icon(&mut self) {
        if !self.tray_added {
            return;
        }
        if let Ok(data) = self.tray_data(NIF_MESSAGE) {
            // SAFETY: Removing a previously added icon is idempotent from the app perspective.
            unsafe {
                let _ = Shell_NotifyIconW(NIM_DELETE, &data);
            }
        }
        self.tray_added = false;
    }

    fn tray_data(
        &self,
        flags: windows::Win32::UI::Shell::NOTIFY_ICON_DATA_FLAGS,
    ) -> Result<NOTIFYICONDATAW, String> {
        let view = self
            .view
            .as_ref()
            .ok_or_else(|| "tray projection was unavailable".to_owned())?;
        let mut data = NOTIFYICONDATAW {
            cbSize: size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.hwnd()?,
            uID: TRAY_ID,
            uFlags: flags,
            uCallbackMessage: TRAY_CALLBACK,
            hIcon: icon_for(view.icon, self.app_icon)?,
            ..Default::default()
        };
        copy_wide(&mut data.szTip, &view.tooltip);
        Ok(data)
    }

    fn show_alert(&self, alert: &QuotaAlert) -> Result<(), String> {
        if !self.tray_added {
            return Ok(());
        }
        let (title, body, icon) = match alert.kind {
            AlertKind::Remaining20 => (
                "Codex 额度剩余 20%",
                format!("{} 已进入注意区间。", alert.window_name),
                NIIF_WARNING,
            ),
            AlertKind::Remaining5 => (
                "Codex 额度剩余 5%",
                format!("{} 即将耗尽。", alert.window_name),
                NIIF_WARNING,
            ),
            AlertKind::Exhausted => (
                "Codex 额度已耗尽",
                format!("{} 等待服务端窗口恢复。", alert.window_name),
                NIIF_ERROR,
            ),
            AlertKind::Recovered => (
                "Codex 额度已恢复",
                format!("{} 已进入新的可用窗口。", alert.window_name),
                NIIF_INFO,
            ),
        };
        let mut data = self.tray_data(NIF_INFO)?;
        copy_wide(&mut data.szInfoTitle, title);
        copy_wide(&mut data.szInfo, &body);
        data.dwInfoFlags = icon | NIIF_NOSOUND | NIIF_RESPECT_QUIET_TIME;
        // SAFETY: Notification text is copied into fixed buffers in `data`.
        if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) }.as_bool() {
            return Err("Windows rejected a quota notification".to_owned());
        }
        Ok(())
    }

    fn toggle_card(&mut self) -> Result<(), String> {
        let hwnd = self.hwnd()?;
        // SAFETY: `hwnd` is the app window.
        if unsafe { IsWindowVisible(hwnd) }.as_bool() {
            // SAFETY: Hiding our own window is valid.
            unsafe {
                let _ = ShowWindow(hwnd, SW_HIDE);
            }
            return Ok(());
        }
        self.show_card()
    }

    fn show_card(&mut self) -> Result<(), String> {
        let hwnd = self.hwnd()?;
        let now = unix_now();
        let last_success_age_secs = self
            .source
            .snapshot()
            .last_success_at
            .map(|last_success| now.saturating_sub(last_success).max(0) as u64);
        self.handle_host_event(HostEvent::CardOpened {
            last_success_age_secs,
        })?;
        self.refresh_projection()?;
        let mut point = POINT::default();
        // SAFETY: `point` is a valid out parameter.
        unsafe { GetCursorPos(&mut point) }.map_err(win_error("read cursor position"))?;
        let monitor = unsafe { MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) };
        self.dpi = monitor_effective_dpi(monitor, unsafe { GetDpiForWindow(hwnd) });
        let layout = self.layout();
        let mut monitor_info = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        // SAFETY: Monitor handle comes from MonitorFromPoint and the structure has a valid size.
        unsafe { GetMonitorInfoW(monitor, &mut monitor_info) }
            .ok()
            .map_err(win_error("read monitor work area"))?;
        let work = monitor_info.rcWork;
        let x =
            (point.x - layout.width).clamp(work.left, (work.right - layout.width).max(work.left));
        let y =
            (point.y - layout.height).clamp(work.top, (work.bottom - layout.height).max(work.top));
        self.hover = InteractionTarget::None;
        self.pressed = InteractionTarget::None;
        self.focus = InteractionTarget::Refresh;
        // SAFETY: Positioning and showing our own popup is valid on this UI thread.
        unsafe {
            SetWindowPos(
                hwnd,
                Some(HWND_TOPMOST),
                x,
                y,
                layout.width,
                layout.height,
                SWP_SHOWWINDOW,
            )
        }
        .map_err(win_error("show quota card"))?;
        // SAFETY: Activating our own visible window enables outside-click dismissal.
        unsafe {
            let _ = SetForegroundWindow(hwnd);
        }
        Ok(())
    }

    fn show_menu(&mut self) -> Result<(), String> {
        let hwnd = self.hwnd()?;
        // SAFETY: Creates an application-owned popup menu.
        let menu = unsafe { CreatePopupMenu() }.map_err(win_error("create tray menu"))?;
        let result = self.populate_and_track_menu(menu, hwnd);
        // SAFETY: `menu` is no longer in use after TrackPopupMenu returns.
        let destroy = unsafe { DestroyMenu(menu) }.map_err(win_error("destroy tray menu"));
        result.and(destroy)
    }

    fn populate_and_track_menu(&mut self, menu: HMENU, hwnd: HWND) -> Result<(), String> {
        append_menu(menu, MF_STRING, CMD_REFRESH, w!("刷新"))?;
        append_menu(menu, MF_STRING, CMD_OPEN_USAGE, w!("打开官方 Usage"))?;
        append_menu(menu, MF_SEPARATOR, 0, PCWSTR::null())?;
        append_menu(
            menu,
            checked(self.settings.persist_quota_cache),
            CMD_TOGGLE_CACHE,
            w!("保存非敏感额度缓存"),
        )?;
        append_menu(
            menu,
            checked(notifications_enabled(&self.settings)),
            CMD_TOGGLE_NOTIFICATIONS,
            w!("启用额度提醒"),
        )?;
        append_menu(
            menu,
            checked(self.settings.start_with_windows),
            CMD_TOGGLE_STARTUP,
            w!("开机启动"),
        )?;
        append_menu(menu, MF_STRING, CMD_CLEAR_CACHE, w!("清除本地额度缓存"))?;
        append_menu(menu, MF_SEPARATOR, 0, PCWSTR::null())?;
        append_menu(menu, MF_STRING, CMD_EXIT, w!("退出"))?;

        let mut point = POINT::default();
        // SAFETY: Valid out parameter and application window.
        unsafe {
            GetCursorPos(&mut point).map_err(win_error("read menu position"))?;
            let _ = SetForegroundWindow(hwnd);
        }
        // SAFETY: Menu, coordinates and owner window are valid for this modal call.
        let command = unsafe {
            TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.x,
                point.y,
                None,
                hwnd,
                None,
            )
        }
        .0 as usize;
        self.handle_command(command)
    }

    fn handle_command(&mut self, command: usize) -> Result<(), String> {
        match command {
            0 => Ok(()),
            CMD_REFRESH => {
                self.source.refresh(RefreshReason::Manual)?;
                self.refresh_projection()
            }
            CMD_OPEN_USAGE => open_usage_page(),
            CMD_TOGGLE_CACHE => {
                self.settings.persist_quota_cache = !self.settings.persist_quota_cache;
                if let Some(cache) = self.cache_store.as_ref() {
                    cache.set_enabled(self.settings.persist_quota_cache);
                    if self.settings.persist_quota_cache {
                        let _ = cache.save(&self.source.snapshot());
                    } else {
                        cache
                            .clear()
                            .map_err(|error| format!("could not clear quota cache: {error}"))?;
                    }
                }
                self.save_settings()
            }
            CMD_TOGGLE_NOTIFICATIONS => {
                let enabled = !notifications_enabled(&self.settings);
                self.settings.notifications.remaining_20_percent = enabled;
                self.settings.notifications.remaining_5_percent = enabled;
                self.settings.notifications.exhausted = enabled;
                self.settings.notifications.recovered = enabled;
                self.save_settings()
            }
            CMD_TOGGLE_STARTUP => {
                let enabled = !self.settings.start_with_windows;
                set_start_with_windows(enabled)?;
                self.settings.start_with_windows = enabled;
                self.save_settings()
            }
            CMD_CLEAR_CACHE => {
                if let Some(cache) = self.cache_store.as_ref() {
                    cache
                        .clear()
                        .map_err(|error| format!("could not clear quota cache: {error}"))?;
                }
                Ok(())
            }
            CMD_EXIT => {
                // SAFETY: Destroying the app-owned window triggers orderly WM_DESTROY cleanup.
                unsafe { DestroyWindow(self.hwnd()?) }.map_err(win_error("close tray window"))
            }
            _ => Ok(()),
        }
    }

    fn save_settings(&mut self) -> Result<(), String> {
        let Some(store) = self.settings_store.as_ref() else {
            return Ok(());
        };
        store
            .save(&self.settings)
            .map_err(|error| format!("could not save settings: {error}"))?;
        self.settings_warning = false;
        Ok(())
    }

    fn shutdown_source(&mut self) -> Result<(), String> {
        let StateSource::Runtime(runtime) = &mut self.source else {
            return Ok(());
        };
        let report = runtime.shutdown()?;
        if report.supervisor.forced_terminations > 0 {
            return Err("App Server required forced termination during tray shutdown".to_owned());
        }
        Ok(())
    }

    fn handle_host_event(&mut self, event: HostEvent) -> Result<(), String> {
        let Some(reason) = refresh_reason(event, self.settings.refresh_on_network_restore) else {
            return Ok(());
        };
        self.source.refresh(reason)
    }

    fn unregister_network_notifications(&mut self) -> Result<(), String> {
        let Some(handle) = self.network_notification.take() else {
            return Ok(());
        };
        // SAFETY: The handle was returned by NotifyNetworkConnectivityHintChange and is consumed
        // exactly once during UI-thread shutdown.
        let result = unsafe { CancelMibChangeNotify2(handle) };
        if result != ERROR_SUCCESS {
            return Err("could not unregister the Windows network notification".to_owned());
        }
        Ok(())
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_NCCREATE {
        // SAFETY: WM_NCCREATE lParam is a valid CREATESTRUCTW for this call.
        let create = unsafe { &*(lparam.0 as *const CREATESTRUCTW) };
        // SAFETY: Stores the stable Box pointer passed to CreateWindowExW.
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
        }
    }

    let context = unsafe { context_for(hwnd) };
    match message {
        TRAY_CALLBACK => {
            if let Some(context) = context {
                match lparam.0 as u32 {
                    WM_LBUTTONUP => {
                        let _ = context.toggle_card();
                    }
                    WM_RBUTTONUP => {
                        let _ = context.show_menu();
                    }
                    _ => {}
                }
            }
            LRESULT(0)
        }
        WM_TIMER if wparam.0 == TIMER_ID => {
            if let Some(context) = context {
                let _ = context.refresh_projection();
            }
            LRESULT(0)
        }
        NETWORK_RESTORED_MESSAGE => {
            if let Some(context) = context {
                let _ = context.handle_host_event(HostEvent::NetworkConnectivityChanged {
                    internet_available: true,
                });
            }
            LRESULT(0)
        }
        SHOW_CARD_MESSAGE => {
            if let Some(context) = context {
                let _ = context.show_card();
            }
            LRESULT(0)
        }
        WM_POWERBROADCAST if wparam.0 == PBT_APMRESUMEAUTOMATIC as usize => {
            if let Some(context) = context {
                let _ = context.handle_host_event(HostEvent::SessionResumed);
            }
            LRESULT(1)
        }
        WM_MOUSEMOVE => {
            if let Some(context) = context {
                let (x, y) = coordinates_from_lparam(lparam);
                let target = context.layout().hit_test(x, y);
                if target != context.hover {
                    context.hover = target;
                    context.invalidate_buttons();
                }
                if !context.tracking_mouse && request_mouse_leave(hwnd).is_ok() {
                    context.tracking_mouse = true;
                }
            }
            // SAFETY: The context borrow above has ended; complete any requested paint now.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_MOUSELEAVE_LOCAL => {
            if let Some(context) = context {
                context.tracking_mouse = false;
                if context.hover != InteractionTarget::None {
                    context.hover = InteractionTarget::None;
                    context.invalidate_buttons();
                }
            }
            // SAFETY: The context borrow above has ended; complete any requested paint now.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            if let Some(context) = context {
                let (x, y) = coordinates_from_lparam(lparam);
                let target = context.layout().hit_test(x, y);
                if target != InteractionTarget::None {
                    context.pressed = target;
                    context.focus = target;
                    // SAFETY: Capturing mouse input for our active button press is balanced on up.
                    unsafe { SetCapture(hwnd) };
                    context.invalidate_buttons();
                }
            }
            // SAFETY: The context borrow above has ended; complete any requested paint now.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if let Some(context) = context {
                let (x, y) = coordinates_from_lparam(lparam);
                let released_over = context.layout().hit_test(x, y);
                let pressed = std::mem::take(&mut context.pressed);
                // SAFETY: Harmless if this window did not own capture.
                let _ = unsafe { ReleaseCapture() };
                context.invalidate_buttons();
                if pressed == released_over {
                    let command = match pressed {
                        InteractionTarget::Refresh => CMD_REFRESH,
                        InteractionTarget::Usage => CMD_OPEN_USAGE,
                        InteractionTarget::None => 0,
                    };
                    let _ = context.handle_command(command);
                }
            }
            // SAFETY: The context borrow above has ended; complete any requested paint now.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_RBUTTONUP => {
            if let Some(context) = context {
                let _ = context.show_menu();
            }
            LRESULT(0)
        }
        WM_KEYUP => {
            if let Some(context) = context {
                match wparam.0 {
                    KEY_ESCAPE => {
                        // SAFETY: Hiding our own popup is valid.
                        unsafe {
                            let _ = ShowWindow(hwnd, SW_HIDE);
                        }
                    }
                    KEY_RETURN | 0x52 => {
                        let _ = context.handle_command(CMD_REFRESH);
                    }
                    KEY_TAB | 0x25 | 0x27 => {
                        context.focus = match context.focus {
                            InteractionTarget::Refresh => InteractionTarget::Usage,
                            _ => InteractionTarget::Refresh,
                        };
                        context.invalidate_buttons();
                    }
                    KEY_SPACE => {
                        let command = match context.focus {
                            InteractionTarget::Usage => CMD_OPEN_USAGE,
                            _ => CMD_REFRESH,
                        };
                        let _ = context.handle_command(command);
                    }
                    0x55 => {
                        let _ = context.handle_command(CMD_OPEN_USAGE);
                    }
                    KEY_F10 => {
                        let _ = context.show_menu();
                    }
                    _ => {}
                }
            }
            // SAFETY: The context borrow above has ended; complete any requested paint now.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_DPICHANGED => {
            if let Some(context) = context {
                context.dpi = (wparam.0 as u32 & 0xffff).max(96);
                let layout = context.layout();
                // SAFETY: WM_DPICHANGED supplies a pointer to the recommended window rectangle.
                let suggested = unsafe { &*(lparam.0 as *const RECT) };
                let _ = unsafe {
                    SetWindowPos(
                        hwnd,
                        None,
                        suggested.left,
                        suggested.top,
                        layout.width,
                        layout.height,
                        SWP_NOACTIVATE,
                    )
                };
                context.invalidate();
            }
            // SAFETY: The context borrow above has ended; complete the DPI repaint immediately.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_ACTIVATE if (wparam.0 & 0xffff) as u32 == WA_INACTIVE => {
            // SAFETY: Hiding the app window on focus loss is valid.
            unsafe {
                let _ = ShowWindow(hwnd, SW_HIDE);
            }
            LRESULT(0)
        }
        WM_PAINT => {
            if let Some(context) = context {
                unsafe { paint_card(hwnd, context) };
            } else {
                unsafe { paint_empty(hwnd) };
            }
            LRESULT(0)
        }
        WM_QUERYENDSESSION => LRESULT(1),
        WM_ENDSESSION if wparam.0 != 0 => {
            // SAFETY: Ends the app-owned window during session shutdown.
            let _ = unsafe { DestroyWindow(hwnd) };
            LRESULT(0)
        }
        WM_DESTROY => {
            // SAFETY: Posts quit to this thread's message loop.
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => {
            // SAFETY: Default processing for messages not handled above.
            unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
        }
    }
}

unsafe fn context_for(hwnd: HWND) -> Option<&'static mut AppContext> {
    // SAFETY: The value was stored from the stable Box pointer during WM_NCCREATE.
    let pointer =
        unsafe { windows::Win32::UI::WindowsAndMessaging::GetWindowLongPtrW(hwnd, GWLP_USERDATA) }
            as *mut AppContext;
    // SAFETY: UI messages are serialized on one thread and the Box outlives the window.
    unsafe { pointer.as_mut() }
}

unsafe fn paint_empty(hwnd: HWND) {
    let mut paint = PAINTSTRUCT::default();
    // SAFETY: Standard BeginPaint/EndPaint pair for WM_PAINT.
    unsafe {
        BeginPaint(hwnd, &mut paint);
        let _ = EndPaint(hwnd, &paint);
    }
}

unsafe fn paint_card(hwnd: HWND, context: &AppContext) {
    let mut paint = PAINTSTRUCT::default();
    // SAFETY: Standard BeginPaint call for WM_PAINT.
    let dc = unsafe { BeginPaint(hwnd, &mut paint) };
    let mut client = RECT::default();
    // SAFETY: `client` is a valid out parameter.
    if unsafe { GetClientRect(hwnd, &mut client) }.is_err() {
        let _ = unsafe { EndPaint(hwnd, &paint) };
        return;
    }
    let layout = context.layout();
    // SAFETY: The compatible memory surface is selected, copied, restored and deleted within
    // this paint cycle. Full-card buffering is required for stable layered-window invalidation.
    let memory_dc = unsafe { CreateCompatibleDC(Some(dc)) };
    let bitmap = unsafe { CreateCompatibleBitmap(dc, client.right, client.bottom) };
    if !memory_dc.0.is_null() && !bitmap.0.is_null() {
        let previous_bitmap = unsafe { SelectObject(memory_dc, HGDIOBJ(bitmap.0)) };
        unsafe { paint_card_surface(memory_dc, context, &layout, &client, true) };
        let _ = unsafe {
            BitBlt(
                dc,
                0,
                0,
                client.right,
                client.bottom,
                Some(memory_dc),
                0,
                0,
                SRCCOPY,
            )
        };
        unsafe {
            SelectObject(memory_dc, previous_bitmap);
            let _ = DeleteObject(HGDIOBJ(bitmap.0));
            let _ = DeleteDC(memory_dc);
        }
    } else {
        unsafe { paint_card_surface(dc, context, &layout, &client, true) };
        if !bitmap.0.is_null() {
            let _ = unsafe { DeleteObject(HGDIOBJ(bitmap.0)) };
        }
        if !memory_dc.0.is_null() {
            let _ = unsafe { DeleteDC(memory_dc) };
        }
    }
    // SAFETY: Completes the paint cycle begun above.
    let _ = unsafe { EndPaint(hwnd, &paint) };
}

unsafe fn paint_card_surface(
    dc: HDC,
    context: &AppContext,
    layout: &CardLayout,
    client: &RECT,
    fill_root: bool,
) {
    if fill_root {
        fill(dc, client, rgb(13, 21, 28));
    }
    let body = create_font(layout.dpi, 11, FW_NORMAL.0 as i32, w!("Segoe UI"));
    let small = create_font(layout.dpi, 10, FW_NORMAL.0 as i32, w!("Segoe UI"));
    let title = create_font(
        layout.dpi,
        14,
        FW_SEMIBOLD.0 as i32,
        w!("Segoe UI Variable Display"),
    );
    let percent = create_font(
        layout.dpi,
        18,
        FW_SEMIBOLD.0 as i32,
        w!("Segoe UI Variable Display"),
    );
    // SAFETY: Fonts remain alive through all selections and the original object is restored.
    let previous_font = unsafe {
        let previous = SelectObject(dc, HGDIOBJ(body.0));
        SetBkMode(dc, TRANSPARENT);
        previous
    };
    if let Some(view) = context.view.as_ref() {
        let _ = unsafe {
            DrawIconEx(
                dc,
                layout.icon.left,
                layout.icon.top,
                context.app_icon,
                layout.icon.width(),
                layout.icon.height(),
                0,
                None,
                DI_NORMAL,
            )
        };
        unsafe { SelectObject(dc, HGDIOBJ(title.0)) };
        draw_text(dc, &view.title, layout.title.to_win32(), rgb(245, 250, 248));
        unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
        draw_status_badge(dc, layout.status_badge, view.icon, status_label(view.icon));
        draw_text(
            dc,
            &view.status,
            layout.status.to_win32(),
            rgb(144, 163, 174),
        );

        for (window, panel) in view.windows.iter().take(3).zip(&layout.window_panels) {
            rounded_fill(dc, *panel, scale_for_dpi(14, layout.dpi), rgb(19, 35, 43));
            unsafe { SelectObject(dc, HGDIOBJ(body.0)) };
            draw_text(
                dc,
                &window.name,
                rect(
                    panel.left + scale_for_dpi(14, layout.dpi),
                    panel.top + scale_for_dpi(8, layout.dpi),
                    panel.right - scale_for_dpi(150, layout.dpi),
                    panel.top + scale_for_dpi(36, layout.dpi),
                ),
                rgb(226, 237, 234),
            );
            unsafe { SelectObject(dc, HGDIOBJ(percent.0)) };
            draw_text_with_format(
                dc,
                &window.percent_label,
                rect(
                    panel.right - scale_for_dpi(150, layout.dpi),
                    panel.top + scale_for_dpi(3, layout.dpi),
                    panel.right - scale_for_dpi(14, layout.dpi),
                    panel.top + scale_for_dpi(38, layout.dpi),
                ),
                severity_color(window.remaining_percent),
                DT_RIGHT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
            );
            let rail = crate::windows_visuals::RectI {
                left: panel.left + scale_for_dpi(14, layout.dpi),
                top: panel.top + scale_for_dpi(42, layout.dpi),
                right: panel.right - scale_for_dpi(14, layout.dpi),
                bottom: panel.top + scale_for_dpi(50, layout.dpi),
            };
            rounded_fill(dc, rail, rail.height(), rgb(43, 61, 68));
            let progress_width = rail.width() * i32::from(window.progress_percent) / 100;
            if progress_width > 0 {
                rounded_fill(
                    dc,
                    crate::windows_visuals::RectI {
                        right: rail.left + progress_width,
                        ..rail
                    },
                    rail.height(),
                    severity_color(window.remaining_percent),
                );
            }
            unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
            draw_text(
                dc,
                &window.reset_label,
                rect(
                    panel.left + scale_for_dpi(14, layout.dpi),
                    panel.top + scale_for_dpi(55, layout.dpi),
                    panel.right - scale_for_dpi(14, layout.dpi),
                    panel.bottom - scale_for_dpi(7, layout.dpi),
                ),
                rgb(144, 163, 174),
            );
        }

        rounded_fill(
            dc,
            layout.credits,
            scale_for_dpi(12, layout.dpi),
            rgb(17, 31, 38),
        );
        unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
        draw_text(
            dc,
            &view.reset_credits,
            layout
                .credits
                .inset(scale_for_dpi(12, layout.dpi))
                .to_win32(),
            rgb(168, 187, 185),
        );
        draw_text(
            dc,
            &view.last_updated,
            layout.updated.to_win32(),
            rgb(103, 126, 136),
        );
        if context.settings_warning {
            draw_text(
                dc,
                "设置文件无效，当前使用安全默认值",
                layout.warning.to_win32(),
                rgb(242, 184, 76),
            );
        }
        unsafe { SelectObject(dc, HGDIOBJ(body.0)) };
        draw_button(
            dc,
            layout.refresh_button,
            "刷新  R",
            InteractionTarget::Refresh,
            context,
            layout.dpi,
            false,
        );
        draw_button(
            dc,
            layout.usage_button,
            "打开官方 Usage  U",
            InteractionTarget::Usage,
            context,
            layout.dpi,
            true,
        );
    }
    unsafe {
        SelectObject(dc, previous_font);
        for font in [body, small, title, percent] {
            let _ = DeleteObject(HGDIOBJ(font.0));
        }
    }
}

fn create_font(
    dpi: u32,
    points: i32,
    weight: i32,
    face: PCWSTR,
) -> windows::Win32::Graphics::Gdi::HFONT {
    let pixel_height = point_size_to_pixels(points, dpi);
    // SAFETY: The face pointer is static and CreateFontW copies all requested font properties.
    unsafe {
        CreateFontW(
            -pixel_height,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            FONT_QUALITY(6),
            u32::from(DEFAULT_PITCH.0) | u32::from(FF_DONTCARE.0),
            face,
        )
    }
}

fn point_size_to_pixels(points: i32, dpi: u32) -> i32 {
    ((i64::from(points) * i64::from(dpi) + 36) / 72).clamp(1, i64::from(i32::MAX)) as i32
}

fn draw_status_badge(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    state: TrayIconState,
    label: &str,
) {
    rounded_fill(dc, area, area.height(), muted_state_color(state));
    draw_text_with_format(
        dc,
        label,
        area.to_win32(),
        rgb(236, 246, 242),
        DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );
}

fn draw_button(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    label: &str,
    target: InteractionTarget,
    context: &AppContext,
    dpi: u32,
    primary: bool,
) {
    let radius = scale_for_dpi(12, dpi);
    let focused = context.focus == target;
    let mut inner = area;
    if focused {
        rounded_fill(dc, area, radius, rgb(47, 224, 179));
        inner = area.inset(scale_for_dpi(2, dpi));
    }
    let color = if context.pressed == target {
        if primary {
            rgb(24, 144, 117)
        } else {
            rgb(31, 54, 63)
        }
    } else if context.hover == target {
        if primary {
            rgb(57, 232, 187)
        } else {
            rgb(36, 62, 72)
        }
    } else if primary {
        rgb(47, 224, 179)
    } else {
        rgb(25, 45, 54)
    };
    rounded_fill(
        dc,
        inner,
        radius.saturating_sub(scale_for_dpi(2, dpi)),
        color,
    );
    draw_text_with_format(
        dc,
        label,
        inner.to_win32(),
        if primary {
            rgb(5, 31, 27)
        } else {
            rgb(226, 237, 234)
        },
        DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );
}

fn draw_text(dc: windows::Win32::Graphics::Gdi::HDC, text: &str, area: RECT, color: COLORREF) {
    draw_text_with_format(
        dc,
        text,
        area,
        color,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );
}

fn draw_text_with_format(
    dc: windows::Win32::Graphics::Gdi::HDC,
    text: &str,
    mut area: RECT,
    color: COLORREF,
    format: DRAW_TEXT_FORMAT,
) {
    let mut wide = text.encode_utf16().collect::<Vec<_>>();
    // SAFETY: `wide` and `area` remain valid and mutable for DrawTextW.
    unsafe {
        SetTextColor(dc, color);
        DrawTextW(dc, &mut wide, &mut area, format);
    }
}

fn fill(dc: windows::Win32::Graphics::Gdi::HDC, area: &RECT, color: COLORREF) {
    // SAFETY: Brush is created for this call and deleted immediately after FillRect.
    unsafe {
        let brush = CreateSolidBrush(color);
        FillRect(dc, area, brush);
        let _ = DeleteObject(HGDIOBJ(brush.0));
    }
}

fn rounded_fill(dc: HDC, area: crate::windows_visuals::RectI, radius: i32, color: COLORREF) {
    if area.width() <= 0 || area.height() <= 0 {
        return;
    }
    // SAFETY: Brush and pen are selected only for this call, then restored and destroyed.
    unsafe {
        let brush = CreateSolidBrush(color);
        let pen = CreatePen(PS_SOLID, 1, color);
        let old_brush = SelectObject(dc, HGDIOBJ(brush.0));
        let old_pen = SelectObject(dc, HGDIOBJ(pen.0));
        let diameter = radius.saturating_mul(2).max(1);
        let _ = RoundRect(
            dc,
            area.left,
            area.top,
            area.right,
            area.bottom,
            diameter,
            diameter,
        );
        SelectObject(dc, old_brush);
        SelectObject(dc, old_pen);
        let _ = DeleteObject(HGDIOBJ(brush.0));
        let _ = DeleteObject(HGDIOBJ(pen.0));
    }
}

fn rect(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
    RECT {
        left,
        top,
        right,
        bottom,
    }
}

fn set_window_title(hwnd: HWND, title: &str) -> Result<(), String> {
    let wide = title
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: `wide` is NUL-terminated, contains only the normalized non-sensitive tooltip, and
    // remains valid for the duration of the call.
    unsafe { SetWindowTextW(hwnd, PCWSTR(wide.as_ptr())) }
        .map_err(win_error("set accessible window title"))
}

fn register_network_notifications(hwnd: HWND) -> Option<HANDLE> {
    let mut handle = HANDLE::default();
    // SAFETY: The callback stores no borrowed Rust pointer. Its context is the value of an
    // OS-owned HWND; posting to an already destroyed window simply fails safely.
    let result = unsafe {
        NotifyNetworkConnectivityHintChange(
            Some(network_connectivity_changed),
            Some(hwnd.0.cast_const()),
            false,
            &mut handle,
        )
    };
    (result == ERROR_SUCCESS).then_some(handle)
}

unsafe extern "system" fn network_connectivity_changed(
    caller_context: *const core::ffi::c_void,
    connectivity_hint: NL_NETWORK_CONNECTIVITY_HINT,
) {
    if connectivity_hint.ConnectivityLevel != NetworkConnectivityLevelHintInternetAccess {
        return;
    }
    let hwnd = HWND(caller_context.cast_mut());
    // SAFETY: The HWND value came from the registering UI thread. Failure is benign if the
    // window has already begun shutdown.
    let _ = unsafe { PostMessageW(Some(hwnd), NETWORK_RESTORED_MESSAGE, WPARAM(0), LPARAM(0)) };
}

fn severity_color(remaining: i64) -> COLORREF {
    match remaining {
        0 => rgb(245, 102, 101),
        1..=5 => rgb(242, 124, 91),
        6..=20 => rgb(242, 184, 76),
        _ => rgb(47, 224, 179),
    }
}

fn muted_state_color(state: TrayIconState) -> COLORREF {
    match state {
        TrayIconState::Normal => rgb(25, 91, 76),
        TrayIconState::Caution => rgb(105, 79, 29),
        TrayIconState::Critical | TrayIconState::Exhausted => rgb(111, 48, 48),
        TrayIconState::Refreshing => rgb(30, 75, 91),
        TrayIconState::Offline => rgb(57, 67, 75),
    }
}

fn rgb(red: u8, green: u8, blue: u8) -> COLORREF {
    COLORREF(u32::from(red) | (u32::from(green) << 8) | (u32::from(blue) << 16))
}

fn icon_for(state: TrayIconState, app_icon: HICON) -> Result<HICON, String> {
    let resource = match icon_kind(state) {
        IconKind::App => return Ok(app_icon),
        IconKind::Warning => IDI_WARNING,
        IconKind::Error => IDI_ERROR,
        IconKind::Information => IDI_INFORMATION,
        IconKind::Offline => IDI_QUESTION,
    };
    // SAFETY: Stock icon resources are owned by Windows and need not be destroyed.
    unsafe { LoadIconW(None, resource) }.map_err(win_error("load tray icon"))
}

fn coordinates_from_lparam(lparam: LPARAM) -> (i32, i32) {
    ((lparam.0 as i16) as i32, ((lparam.0 >> 16) as i16) as i32)
}

fn checked(value: bool) -> windows::Win32::UI::WindowsAndMessaging::MENU_ITEM_FLAGS {
    if value {
        MF_STRING | MF_CHECKED
    } else {
        MF_STRING
    }
}

fn append_menu(
    menu: HMENU,
    flags: windows::Win32::UI::WindowsAndMessaging::MENU_ITEM_FLAGS,
    id: usize,
    label: PCWSTR,
) -> Result<(), String> {
    // SAFETY: Menu handle is owned by the caller; label is static or null for separator.
    unsafe { AppendMenuW(menu, flags, id, label) }.map_err(win_error("append tray menu item"))
}

fn open_usage_page() -> Result<(), String> {
    // SAFETY: Opens a fixed HTTPS URL with the user's default handler; no shell parameters.
    let result = unsafe {
        ShellExecuteW(
            None,
            w!("open"),
            USAGE_URL,
            PCWSTR::null(),
            PCWSTR::null(),
            SW_SHOWNOACTIVATE,
        )
    };
    if result.0 as isize <= 32 {
        Err("Windows could not open the official Codex Usage page".to_owned())
    } else {
        Ok(())
    }
}

fn load_persistence(
    demo: bool,
) -> (
    AppSettings,
    Option<SettingsStore>,
    Option<QuotaCacheStore>,
    bool,
) {
    if demo {
        return (AppSettings::default(), None, None, false);
    }
    let Ok(paths) = PersistencePaths::local_default() else {
        return (AppSettings::default(), None, None, true);
    };
    let settings_store = SettingsStore::new(paths.settings);
    let (mut settings, warning) = match settings_store.load() {
        Ok(settings) => (settings, false),
        Err(_) => (AppSettings::default(), true),
    };
    settings.start_with_windows = start_with_windows_enabled();
    let cache = QuotaCacheStore::new(paths.quota_cache);
    cache.set_enabled(settings.persist_quota_cache);
    (settings, Some(settings_store), Some(cache), warning)
}

fn notifications_enabled(settings: &AppSettings) -> bool {
    settings.notifications.remaining_20_percent
        || settings.notifications.remaining_5_percent
        || settings.notifications.exhausted
        || settings.notifications.recovered
}

fn set_start_with_windows(enabled: bool) -> Result<(), String> {
    let command = if enabled {
        let executable = std::env::current_exe()
            .map_err(|error| format!("could not locate the tray executable: {:?}", error.kind()))?;
        Some(format!("\"{}\"", executable.display()))
    } else {
        None
    };
    let mut key = HKEY::default();
    // SAFETY: Opens/creates the current user's standard Run key with write-only access.
    let status = unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            None,
            &mut key,
            None,
        )
    };
    if status != ERROR_SUCCESS {
        return Err(format!(
            "could not open Windows startup settings: {status:?}"
        ));
    }
    let result = if let Some(command) = command.as_deref() {
        let wide = command
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect::<Vec<_>>();
        // SAFETY: UTF-16 bytes include the required terminating NUL and live through the call.
        let bytes = unsafe {
            std::slice::from_raw_parts(wide.as_ptr().cast::<u8>(), wide.len() * size_of::<u16>())
        };
        // SAFETY: Key is open and byte slice encodes a NUL-terminated REG_SZ value.
        let status =
            unsafe { RegSetValueExW(key, w!("CodexQuotaTray"), None, REG_SZ, Some(bytes)) };
        (status == ERROR_SUCCESS)
            .then_some(())
            .ok_or_else(|| format!("could not enable Windows startup: {status:?}"))
    } else {
        // SAFETY: Key is open; deleting a missing value is considered success.
        let status = unsafe { RegDeleteValueW(key, w!("CodexQuotaTray")) };
        (status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND)
            .then_some(())
            .ok_or_else(|| format!("could not disable Windows startup: {status:?}"))
    };
    // SAFETY: Closes the registry handle opened above.
    unsafe {
        let _ = RegCloseKey(key);
    }
    result
}

fn start_with_windows_enabled() -> bool {
    let Ok(executable) = std::env::current_exe() else {
        return false;
    };
    let mut byte_count = 0u32;
    // SAFETY: This size query reads one REG_SZ value from the current user's Run key.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
            w!("CodexQuotaTray"),
            RRF_RT_REG_SZ,
            None,
            None,
            Some(&mut byte_count),
        )
    };
    if status != ERROR_SUCCESS || byte_count == 0 {
        return false;
    }
    let mut value = vec![0u16; (byte_count as usize).div_ceil(size_of::<u16>())];
    // SAFETY: `value` has the byte capacity returned by the size query and remains live.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
            w!("CodexQuotaTray"),
            RRF_RT_REG_SZ,
            None,
            Some(value.as_mut_ptr().cast()),
            Some(&mut byte_count),
        )
    };
    if status != ERROR_SUCCESS {
        return false;
    }
    let length = value
        .iter()
        .position(|character| *character == 0)
        .unwrap_or(value.len());
    startup_command_matches(&String::from_utf16_lossy(&value[..length]), &executable)
}

fn startup_command_matches(value: &str, executable: &Path) -> bool {
    value.eq_ignore_ascii_case(&format!("\"{}\"", executable.display()))
}

fn demo_state(step: usize) -> AppState {
    let first_remaining = [72, 20, 5, 0, 84][step % 5];
    let second_remaining = [41, 35, 29, 24, 90][step % 5];
    let now = unix_now();
    AppState {
        auth: AuthState::Authenticated {
            plan_type: Some("demo".to_owned()),
        },
        data: DataState::Fresh,
        quota: Some(QuotaSummary {
            windows: vec![
                demo_window("primary", 300, first_remaining, now + 2 * 60 * 60),
                demo_window(
                    "secondary",
                    10_080,
                    second_remaining,
                    now + 4 * 24 * 60 * 60,
                ),
            ],
            issues: Vec::new(),
            reset_credits: ResetCreditsState::UnavailableInSchema,
            rate_limit_reached: false,
        }),
        last_success_at: Some(now),
        source_cli_version: Some("0.137.0-demo".to_owned()),
        ..AppState::default()
    }
}

fn demo_window(
    source_slot: &'static str,
    duration: i64,
    remaining: i64,
    resets_at: i64,
) -> QuotaWindow {
    QuotaWindow {
        limit_id: None,
        limit_name: None,
        source_slot,
        used_percent: 100 - remaining,
        remaining_percent: remaining,
        window_duration_mins: Some(duration),
        resets_at: Some(resets_at),
    }
}

fn copy_wide<const N: usize>(destination: &mut [u16; N], text: &str) {
    destination.fill(0);
    for (slot, value) in destination
        .iter_mut()
        .take(N.saturating_sub(1))
        .zip(text.encode_utf16())
    {
        *slot = value;
    }
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs().min(i64::MAX as u64) as i64)
        .unwrap_or(0)
}

fn win_error(operation: &'static str) -> impl FnOnce(windows::core::Error) -> String {
    move |_| format!("native Windows operation failed: {operation}")
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use super::{point_size_to_pixels, startup_command_matches};

    #[test]
    fn point_sizes_map_to_integer_physical_pixels() {
        assert_eq!(point_size_to_pixels(10, 96), 13);
        assert_eq!(point_size_to_pixels(11, 120), 18);
        assert_eq!(point_size_to_pixels(14, 144), 28);
        assert_eq!(point_size_to_pixels(18, 192), 48);
    }

    #[test]
    fn startup_registration_requires_the_exact_quoted_executable() {
        let executable = Path::new(r"C:\Program Files\CodexQuotaTray\codex-quota-tray-gui.exe");
        assert!(startup_command_matches(
            r#""C:\Program Files\CodexQuotaTray\codex-quota-tray-gui.exe""#,
            executable,
        ));
        assert!(!startup_command_matches(
            r"C:\Program Files\CodexQuotaTray\codex-quota-tray-gui.exe",
            executable,
        ));
        assert!(!startup_command_matches(
            r#""C:\Old\codex-quota-tray-gui.exe""#,
            executable,
        ));
    }
}
