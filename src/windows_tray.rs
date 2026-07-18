use std::ffi::OsString;
use std::mem::size_of;
use std::path::Path;
use std::ptr::null_mut;
#[cfg(debug_assertions)]
use std::sync::OnceLock;
#[cfg(debug_assertions)]
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use windows::Win32::Foundation::{
    COLORREF, CloseHandle, ERROR_FILE_NOT_FOUND, ERROR_SUCCESS, GetLastError, GlobalFree, HANDLE,
    HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WAIT_OBJECT_0, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, BitBlt, CLIP_DEFAULT_PRECIS, CreateCompatibleBitmap, CreateCompatibleDC,
    CreateFontW, CreateRoundRectRgn, CreateSolidBrush, DEFAULT_CHARSET, DEFAULT_PITCH,
    DRAW_TEXT_FORMAT, DT_CALCRECT, DT_CENTER, DT_END_ELLIPSIS, DT_LEFT, DT_RIGHT, DT_SINGLELINE,
    DT_VCENTER, DeleteDC, DeleteObject, DrawTextW, EndPaint, FF_DONTCARE, FONT_QUALITY, FW_NORMAL,
    FW_SEMIBOLD, FillRect, FillRgn, FrameRgn, GetMonitorInfoW, GetTextMetricsW, HDC, HGDIOBJ,
    InvalidateRect, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint, OUT_DEFAULT_PRECIS,
    PAINTSTRUCT, SRCCOPY, SelectObject, SetBkMode, SetTextColor, TEXTMETRICW, TRANSPARENT,
    UpdateWindow,
};
use windows::Win32::Graphics::GdiPlus::{
    FillModeAlternate, GdipAddPathArc, GdipAddPathLine, GdipClosePathFigure, GdipCreateFromHDC,
    GdipCreatePath, GdipCreatePen1, GdipCreateSolidFill, GdipDeleteBrush, GdipDeleteGraphics,
    GdipDeletePath, GdipDeletePen, GdipDrawEllipse, GdipDrawLine, GdipDrawPath, GdipFillPath,
    GdipSetPixelOffsetMode, GdipSetSmoothingMode, GdiplusShutdown, GdiplusStartup,
    GdiplusStartupInput, GdiplusStartupOutput, GpBrush, GpGraphics, GpPath, GpPen, Ok as GdiPlusOk,
    PixelOffsetModeHalf, SmoothingModeAntiAlias8x8, UnitPixel,
};
use windows::Win32::NetworkManagement::IpHelper::{
    CancelMibChangeNotify2, NotifyNetworkConnectivityHintChange,
};
use windows::Win32::Networking::WinSock::{
    NL_NETWORK_CONNECTIVITY_HINT, NetworkConnectivityLevelHintInternetAccess,
};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData,
};
#[cfg(debug_assertions)]
use windows::Win32::System::Diagnostics::Debug::OutputDebugStringW;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Memory::{GMEM_MOVEABLE, GlobalAlloc, GlobalLock, GlobalUnlock};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ, RRF_RT_REG_SZ,
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegGetValueW, RegSetValueExW,
};
use windows::Win32::System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject};
use windows::Win32::UI::HiDpi::{GetDpiForWindow, GetSystemMetricsForDpi};
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture};
use windows::Win32::UI::Shell::{
    NIF_GUID, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIIF_ERROR, NIIF_INFO,
    NIIF_NOSOUND, NIIF_RESPECT_QUIET_TIME, NIIF_WARNING, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NIM_SETVERSION, NIN_BALLOONUSERCLICK, NOTIFYICON_VERSION_4, NOTIFYICONDATAW, Shell_NotifyIconW,
    ShellExecuteW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, CreatePopupMenu,
    CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyMenu, DestroyWindow, FindWindowW,
    GWLP_USERDATA, GetClientRect, GetCursorPos, GetForegroundWindow, GetMessageW,
    GetWindowThreadProcessId, HICON, HMENU, HWND_MESSAGE, HWND_TOPMOST, ICON_BIG, ICON_SMALL,
    IDC_ARROW, IsIconic, IsWindowVisible, KillTimer, LoadCursorW, MF_CHECKED, MF_POPUP,
    MF_SEPARATOR, MF_STRING, MSG, PBT_APMRESUMEAUTOMATIC, PostMessageW, PostQuitMessage,
    RegisterClassExW, RegisterWindowMessageW, SC_MINIMIZE, SM_CXICON, SM_CXSMICON, SW_HIDE,
    SW_SHOWNOACTIVATE, SW_SHOWNORMAL, SWP_NOACTIVATE, SendMessageW, SetForegroundWindow, SetTimer,
    SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow, TPM_RETURNCMD, TPM_RIGHTBUTTON,
    TrackPopupMenu, WM_ACTIVATE, WM_ACTIVATEAPP, WM_APP, WM_CLOSE, WM_DESTROY, WM_DPICHANGED,
    WM_ENDSESSION, WM_KEYUP, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE, WM_NCCREATE, WM_NCDESTROY,
    WM_PAINT, WM_POWERBROADCAST, WM_QUERYENDSESSION, WM_RBUTTONUP, WM_SETICON, WM_SIZE,
    WM_SYSCOMMAND, WM_TIMER, WNDCLASSEXW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
    WS_POPUP,
};
use windows::core::{GUID, PCWSTR, w};

use crate::alerts::{AlertKind, AlertTracker, QuotaAlert};
use crate::compatibility::schema_codex_version;
use crate::host_events::{HostEvent, refresh_reason};
use crate::persistence::{
    AlertStateStore, AppSettings, PersistencePaths, QuotaCacheStore, SettingsStore,
};
use crate::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use crate::refresh::{RefreshMode, RefreshPolicy, RefreshReason};
use crate::runtime::{QuotaRuntime, RuntimeConfig};
use crate::state::{AppState, AuthState, DataState, ProcessState};
use crate::ui_model::{StatusTone, TrayView, ViewPreferences, project_tray_view};
use crate::windows_visuals::{
    CardLayout, InteractionTarget, bottom_right_popup_origin, configure_window_chrome,
    ensure_per_monitor_v2, icon_resource_size_for_target, load_app_icon, monitor_effective_dpi,
    request_mouse_leave, scale_for_dpi,
};

const WINDOW_CLASS: PCWSTR = w!("CodexQuotaTrayWindow");
const TRAY_MESSAGE_CLASS: PCWSTR = w!("CodexQuotaTrayMessageWindow");
const WINDOW_TITLE: PCWSTR = w!("CodexQuotaTray");
const TRAY_CALLBACK: u32 = WM_APP + 17;
const NETWORK_RESTORED_MESSAGE: u32 = WM_APP + 18;
const SHOW_CARD_MESSAGE: u32 = WM_APP + 19;
const TOGGLE_WINDOW_MESSAGE: u32 = WM_APP + 20;
const EXIT_PROCESS_MESSAGE: u32 = WM_APP + 21;
const SHOW_MENU_MESSAGE: u32 = WM_APP + 22;
// The stable GUID is the notification-area identity; uID is retained only for older Shell32
// paths that still inspect it when NIF_GUID is unavailable.
const TRAY_ID: u32 = 0x5143_5452;
const TRAY_GUID: GUID = GUID::from_u128(0x8f4f2c19_0c4c_4e1b_8f5c_50d0f1a4a77d);
const TIMER_ID: usize = 1;
const TIMER_MILLIS: u32 = 30_000;
const REFRESH_STATUS_TIMER_ID: usize = 2;
const REFRESH_STATUS_TIMER_MILLIS: u32 = 100;
const MINIMUM_MANUAL_FEEDBACK: Duration = Duration::from_millis(300);
const CMD_REFRESH: usize = 1001;
const CMD_OPEN_USAGE: usize = 1002;
const CMD_TOGGLE_CACHE: usize = 1003;
const CMD_TOGGLE_STARTUP: usize = 1005;
const CMD_CLEAR_CACHE: usize = 1006;
const CMD_ALERT_50: usize = 1010;
const CMD_ALERT_20: usize = 1011;
const CMD_ALERT_10: usize = 1012;
const CMD_REFRESH_AUTO: usize = 1020;
const CMD_REFRESH_5: usize = 1021;
const CMD_REFRESH_15: usize = 1022;
const CMD_REFRESH_30: usize = 1023;
const CMD_REFRESH_MANUAL: usize = 1024;
const CMD_COPY_DIAGNOSTICS: usize = 1030;
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
    // SAFETY: Use a private exit message so WM_CLOSE can retain its ordinary hide-to-tray
    // behavior for user initiated window closes.
    if let Err(error) =
        unsafe { PostMessageW(Some(existing), EXIT_PROCESS_MESSAGE, WPARAM(0), LPARAM(0)) }
    {
        // SAFETY: This function owns the synchronization handle.
        let _ = unsafe { CloseHandle(process) };
        return Err(win_error("request existing tray shutdown")(error));
    }
    // Queue WM_CLOSE as a compatibility fallback for an older already-running build that does
    // not know EXIT_PROCESS_MESSAGE. In the current build the private exit message is queued
    // first and destroys the window; the later WM_CLOSE is then discarded by Windows.
    let _ = unsafe { PostMessageW(Some(existing), WM_CLOSE, WPARAM(0), LPARAM(0)) };
    // SAFETY: `process` remains valid until it is closed below.
    let wait_result = unsafe { WaitForSingleObject(process, 10_000) };
    // SAFETY: This function owns the synchronization handle.
    let _ = unsafe { CloseHandle(process) };
    if wait_result != WAIT_OBJECT_0 {
        return Err("existing tray did not shut down within 10 seconds".to_owned());
    }
    Ok(true)
}

/// Owns the process-level GDI+ session used by the anti-aliased rounded primitives. GDI+ is a
/// Windows system component, so this does not add a redistributable runtime to the package. If a
/// legacy/remote-desktop environment cannot initialize it, painting falls back to the existing
/// opaque GDI path and the quota UI remains usable.
struct GdiPlusSession {
    token: usize,
}

impl GdiPlusSession {
    fn start() -> Option<Self> {
        let input = GdiplusStartupInput {
            GdiplusVersion: 1,
            ..Default::default()
        };
        let mut output = GdiplusStartupOutput::default();
        let mut token = 0_usize;
        // SAFETY: GDI+ copies the startup input synchronously; output and token remain valid for
        // the duration of the call and the session is shut down in Drop.
        let status = unsafe { GdiplusStartup(&mut token, &input, &mut output) };
        (status == GdiPlusOk && token != 0).then_some(Self { token })
    }
}

impl Drop for GdiPlusSession {
    fn drop(&mut self) {
        // SAFETY: The token was returned by GdiplusStartup and is owned by this session.
        unsafe { GdiplusShutdown(self.token) };
    }
}

/// Owns the icons used by the visible window and notification area for one effective monitor DPI.
/// The class icons are intentionally kept separate: Windows retains those handles in the
/// registered WNDCLASSEXW until the process exits, so they must never be destroyed during a DPI
/// transition.
struct DpiIconSet {
    dpi: u32,
    window_big: HICON,
    window_small: HICON,
    tray: HICON,
}

impl DpiIconSet {
    fn load(instance: HINSTANCE, dpi: u32) -> Result<Self, String> {
        let dpi = dpi.max(96);
        let large_size = system_icon_resource_size(dpi, SM_CXICON, 32, true);
        let small_size = system_icon_resource_size(dpi, SM_CXSMICON, 16, false);
        let window_big =
            load_icon_for_startup(instance, large_size, large_size, "window_icon_big")?;
        let window_small =
            match load_icon_for_startup(instance, small_size, small_size, "window_icon_small") {
                Ok(icon) => icon,
                Err(error) => {
                    destroy_owned_icon(window_big);
                    return Err(error);
                }
            };
        let tray = match load_icon_for_startup(instance, small_size, small_size, "tray_icon") {
            Ok(icon) => icon,
            Err(error) => {
                destroy_owned_icon(window_big);
                destroy_owned_icon(window_small);
                return Err(error);
            }
        };
        Ok(Self {
            dpi,
            window_big,
            window_small,
            tray,
        })
    }
}

impl Drop for DpiIconSet {
    fn drop(&mut self) {
        for icon in [self.tray, self.window_small, self.window_big] {
            destroy_owned_icon(icon);
        }
    }
}

fn destroy_owned_icon(icon: HICON) {
    if !icon.is_invalid() {
        // SAFETY: Every handle passed here came from LoadImageW without LR_SHARED and is owned by
        // this process. The caller only invokes this after all window/shell references changed.
        unsafe {
            let _ = DestroyIcon(icon);
        }
    }
}

fn system_icon_resource_size(
    dpi: u32,
    metric: windows::Win32::UI::WindowsAndMessaging::SYSTEM_METRICS_INDEX,
    fallback_base: i32,
    large: bool,
) -> i32 {
    // SAFETY: The metric index is one of the documented icon metrics and dpi is clamped to the
    // valid PM-aware range. The fallback keeps the product icon usable on older shells.
    let requested = unsafe { GetSystemMetricsForDpi(metric, dpi) };
    if requested > 0 {
        icon_resource_size_for_target(requested, large)
    } else {
        // Keep the pure mapping as the fallback for shells that do not expose the per-DPI metric.
        crate::windows_visuals::icon_resource_size_for_dpi(dpi, large)
            .max(scale_for_dpi(fallback_base, dpi))
    }
}

fn icon_handle_value(icon: HICON) -> usize {
    icon.0 as usize
}

fn load_icon_for_startup(
    instance: HINSTANCE,
    width: i32,
    height: i32,
    label: &str,
) -> Result<HICON, String> {
    match load_app_icon(instance, width, height) {
        Ok(icon) => {
            debug_log(&format!(
                "{label}: loaded {width}x{height} HICON={}",
                icon_handle_value(icon)
            ));
            Ok(icon)
        }
        Err(error) => {
            let last_error = unsafe { GetLastError().0 };
            debug_log(&format!(
                "{label}: LoadImageW failed HICON=0 error={error} last_error={last_error}"
            ));
            Err(error)
        }
    }
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

    let (settings, settings_store, cache_store, alert_state_store, alert_tracker, settings_warning) =
        load_persistence(options.demo);
    let source = if options.demo {
        StateSource::Demo {
            state: demo_state(0),
            step: 0,
        }
    } else {
        let mut config = RuntimeConfig::codex(options.codex_bin);
        config.refresh_policy = RefreshPolicy::new(10, 15)?;
        config.refresh_mode = settings.refresh_mode;
        config.quota_cache = cache_store.clone();
        StateSource::Runtime(QuotaRuntime::start(config)?)
    };
    // SAFETY: Null module name requests the current module.
    let module = unsafe { GetModuleHandleW(None) }.map_err(win_error("get module handle"))?;
    let instance = HINSTANCE(module.0);
    let class_icon_big = load_icon_for_startup(instance, 32, 32, "class_icon_big")?;
    let class_icon_small = load_icon_for_startup(instance, 16, 16, "class_icon_small")?;
    // The initial set is a valid 96-DPI product icon. It is replaced with the actual window DPI
    // set after the HWND exists and before the first NIM_ADD/ShowWindow call.
    let dpi_icons = DpiIconSet::load(instance, 96)?;
    let taskbar_created_message = unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) };
    let mut context = Box::new(AppContext {
        hwnd: None,
        tray_message_hwnd: None,
        instance,
        source,
        settings,
        settings_store,
        cache_store,
        alert_state_store,
        alert_tracker,
        view: None,
        tray_added: false,
        settings_warning,
        network_notification: None,
        dpi: 96,
        hover: InteractionTarget::None,
        pressed: InteractionTarget::None,
        focus: InteractionTarget::Refresh,
        tracking_mouse: false,
        manual_refresh_started: None,
        initial_sync_pending: true,
        refresh_status_timer_running: false,
        desired_visible: false,
        class_icon_big,
        class_icon_small,
        dpi_icons,
        taskbar_created_message,
        toggle_post_count: 0,
        toggle_handle_count: 0,
        gdiplus: GdiPlusSession::start(),
    });

    // SAFETY: The stock cursor resource is owned by Windows.
    let cursor = unsafe { LoadCursorW(None, IDC_ARROW) }.map_err(win_error("load cursor"))?;
    let window_class = WNDCLASSEXW {
        cbSize: size_of::<WNDCLASSEXW>() as u32,
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        hCursor: cursor,
        hIcon: class_icon_big,
        hIconSm: class_icon_small,
        lpszClassName: WINDOW_CLASS,
        ..Default::default()
    };
    let tray_window_class = WNDCLASSEXW {
        cbSize: size_of::<WNDCLASSEXW>() as u32,
        lpfnWndProc: Some(tray_message_proc),
        hInstance: instance,
        hCursor: cursor,
        hIcon: class_icon_big,
        hIconSm: class_icon_small,
        lpszClassName: TRAY_MESSAGE_CLASS,
        ..Default::default()
    };
    // SAFETY: `window_class` contains valid handles and a static class name.
    if unsafe { RegisterClassExW(&window_class) } == 0 {
        return Err("could not register the native tray window class".to_owned());
    }
    if unsafe { RegisterClassExW(&tray_window_class) } == 0 {
        return Err("could not register the native tray message class".to_owned());
    }

    let context_ptr = (&mut *context as *mut AppContext).cast();
    let extended_style = if options.demo {
        WS_EX_TOPMOST
    } else {
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST
    };
    let initial_layout = CardLayout::new(96, 2, false);
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
    set_window_icons(hwnd, class_icon_big, class_icon_small);
    let context_ptr = (&mut *context as *mut AppContext).cast();
    // SAFETY: A message-only window receives shell callbacks without creating a taskbar button or
    // taking activation. The same stable context pointer is used until both windows are gone.
    let tray_message_hwnd = unsafe {
        CreateWindowExW(
            WS_EX_NOACTIVATE,
            TRAY_MESSAGE_CLASS,
            WINDOW_TITLE,
            WS_POPUP,
            0,
            0,
            0,
            0,
            Some(HWND_MESSAGE),
            None,
            Some(instance),
            Some(context_ptr),
        )
    }
    .map_err(win_error("create tray message window"))?;
    context.tray_message_hwnd = Some(tray_message_hwnd);
    debug_log(&format!(
        "tray_message_hwnd created hwnd={}",
        tray_message_hwnd.0 as usize
    ));
    context.dpi = unsafe { GetDpiForWindow(hwnd) }.max(96);
    let _ = configure_window_chrome(hwnd);
    if context.settings.refresh_on_network_restore {
        context.network_notification = register_network_notifications(hwnd);
    }
    context.refresh_projection()?;
    context.refresh_dpi_icons(context.dpi);
    context.add_tray_icon()?;
    // SAFETY: `hwnd` belongs to this thread and `TIMER_ID` is application-owned.
    if unsafe { SetTimer(Some(hwnd), TIMER_ID, TIMER_MILLIS, None) } == 0 {
        context.delete_tray_icon();
        return Err("could not start the low-frequency tray timer".to_owned());
    }
    if options.demo {
        context.desired_visible = true;
        context.show_main_window()?;
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
    let _ = unsafe { KillTimer(Some(hwnd), REFRESH_STATUS_TIMER_ID) };
    context.delete_tray_icon();
    if let Some(tray_message_hwnd) = context.tray_message_hwnd.take() {
        // SAFETY: The message-only window is owned by this UI thread and has no visible surface.
        let _ = unsafe { DestroyWindow(tray_message_hwnd) };
    }
    let network_cleanup = context.unregister_network_notifications();
    let runtime_cleanup = context.shutdown_source();
    network_cleanup.and(runtime_cleanup)
}

struct AppContext {
    hwnd: Option<HWND>,
    tray_message_hwnd: Option<HWND>,
    instance: HINSTANCE,
    source: StateSource,
    settings: AppSettings,
    settings_store: Option<SettingsStore>,
    cache_store: Option<QuotaCacheStore>,
    alert_state_store: Option<AlertStateStore>,
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
    manual_refresh_started: Option<Instant>,
    initial_sync_pending: bool,
    refresh_status_timer_running: bool,
    desired_visible: bool,
    class_icon_big: HICON,
    class_icon_small: HICON,
    dpi_icons: DpiIconSet,
    taskbar_created_message: u32,
    toggle_post_count: u64,
    toggle_handle_count: u64,
    gdiplus: Option<GdiPlusSession>,
}

enum StateSource {
    Runtime(QuotaRuntime),
    Demo { state: AppState, step: usize },
}

impl Drop for AppContext {
    fn drop(&mut self) {
        // The class handles are retained until the process exits because RegisterClassExW stores
        // them for the lifetime of the class. Dynamic DPI icons are released by DpiIconSet after
        // the tray entry and window have already been removed.
        destroy_owned_icon(self.class_icon_big);
        destroy_owned_icon(self.class_icon_small);
    }
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

    fn set_refresh_mode(&mut self, mode: RefreshMode) -> Result<(), String> {
        match self {
            Self::Runtime(runtime) => runtime.set_refresh_mode(mode),
            Self::Demo { .. } => Ok(()),
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
        let line_height = measure_small_line_height(self.dpi);
        let layout = CardLayout::new_with_metrics(
            self.dpi,
            self.view
                .as_ref()
                .map(|view| view.windows.len())
                .unwrap_or_default(),
            self.settings_warning,
            line_height,
            scale_for_dpi(16, self.dpi),
        );
        debug_layout(&layout, line_height);
        layout
    }

    fn refresh_dpi_icons(&mut self, dpi: u32) {
        let dpi = dpi.max(96);
        if self.dpi_icons.dpi == dpi {
            return;
        }
        let next = match DpiIconSet::load(self.instance, dpi) {
            Ok(next) => next,
            Err(error) => {
                debug_log(&format!(
                    "DPI icon refresh failed dpi={dpi}; keeping existing product icons: {error}"
                ));
                return;
            }
        };
        if self.tray_added
            && let Err(error) = self.modify_tray_icon_with(next.tray)
        {
            debug_log(&format!(
                "DPI tray icon update failed dpi={dpi}; keeping existing product icons: {error}"
            ));
            return;
        }
        if let Some(hwnd) = self.hwnd {
            set_window_icons(hwnd, next.window_big, next.window_small);
        }
        let old = std::mem::replace(&mut self.dpi_icons, next);
        self.dpi = dpi;
        debug_log(&format!(
            "DPI icon set activated dpi={} window_big={} window_small={} tray={}",
            self.dpi,
            icon_handle_value(self.dpi_icons.window_big),
            icon_handle_value(self.dpi_icons.window_small),
            icon_handle_value(self.dpi_icons.tray),
        ));
        self.invalidate();
        drop(old);
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
        let mut view = project_tray_view(&state, unix_now(), self.preferences());
        let runtime_refreshing = matches!(state.data, DataState::Refreshing { .. });
        let manual_feedback = self.manual_refresh_started.is_some_and(|started| {
            should_show_manual_refresh_feedback(started.elapsed(), runtime_refreshing)
        });
        if manual_feedback {
            view.status_line = "正在刷新…".to_owned();
            view.status_tone = StatusTone::Refreshing;
            view.refresh_label = "正在刷新…".to_owned();
            view.can_refresh = false;
        } else if self.manual_refresh_started.is_some() && !runtime_refreshing {
            self.manual_refresh_started = None;
        }
        if self.initial_sync_pending && initial_sync_is_complete(&state, self.settings.refresh_mode)
        {
            self.initial_sync_pending = false;
        }
        if refresh_status_poll_required(
            self.initial_sync_pending,
            runtime_refreshing,
            manual_feedback,
        ) {
            self.ensure_refresh_status_timer();
        } else {
            self.stop_refresh_status_timer();
        }
        if matches!(state.data, DataState::Fresh)
            && let Some(quota) = state.quota.as_ref()
        {
            let observation = self
                .alert_tracker
                .observe(quota, &self.settings.notifications);
            let state_saved = if observation.changed {
                self.save_alert_state().is_ok()
            } else {
                true
            };
            if state_saved {
                for alert in observation.alerts {
                    // At-most-once is deliberate: state is durable before asking Windows to show
                    // the notification. A Shell failure may lose a notification but never repeats
                    // it on the next projection.
                    let _ = self.show_alert(&alert);
                }
            } else {
                self.settings_warning = true;
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

    fn begin_manual_refresh_feedback(&mut self) {
        self.manual_refresh_started = Some(Instant::now());
        self.ensure_refresh_status_timer();
    }

    fn ensure_refresh_status_timer(&mut self) {
        if self.refresh_status_timer_running {
            return;
        }
        if let Some(hwnd) = self.hwnd {
            // SAFETY: The window and timer ID are application-owned. This temporary poll exists
            // only while a manual refresh is active; the 30-second idle timer remains unchanged.
            if unsafe {
                SetTimer(
                    Some(hwnd),
                    REFRESH_STATUS_TIMER_ID,
                    REFRESH_STATUS_TIMER_MILLIS,
                    None,
                )
            } == 0
            {
                debug_log("refresh status timer could not be started");
            } else {
                self.refresh_status_timer_running = true;
            }
        }
    }

    fn stop_refresh_status_timer(&mut self) {
        if !self.refresh_status_timer_running {
            return;
        }
        if let Some(hwnd) = self.hwnd {
            // SAFETY: This app-owned timer is stopped only on its UI thread.
            let _ = unsafe { KillTimer(Some(hwnd), REFRESH_STATUS_TIMER_ID) };
        }
        self.refresh_status_timer_running = false;
    }

    fn save_alert_state(&self) -> Result<(), String> {
        let Some(store) = self.alert_state_store.as_ref() else {
            return Ok(());
        };
        store
            .save(&self.alert_tracker.snapshot())
            .map_err(|error| format!("could not save alert state: {error}"))
    }

    fn add_tray_icon(&mut self) -> Result<(), String> {
        let data = self.tray_data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP | NIF_GUID)?;
        debug_shell_notify("NIM_ADD", &data);
        // SAFETY: `data` is fully initialized and references no borrowed pointers.
        if !unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.as_bool() {
            let error = unsafe { GetLastError().0 };
            debug_log(&format!("NIM_ADD failed error={error}"));
            return Err(format!(
                "Windows rejected the tray icon (error {error}, hwnd={}, icon={}, cbSize={}, flags=0x{:x})",
                data.hWnd.0 as usize,
                icon_handle_value(data.hIcon),
                data.cbSize,
                data.uFlags.0
            ));
        }
        debug_log("NIM_ADD succeeded");
        self.tray_added = true;
        let mut version_data = data;
        version_data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        debug_shell_notify("NIM_SETVERSION", &version_data);
        // SAFETY: The icon was just added and the version field is the documented v4 value.
        if !unsafe { Shell_NotifyIconW(NIM_SETVERSION, &version_data) }.as_bool() {
            let error = unsafe { GetLastError().0 };
            debug_log(&format!("NIM_SETVERSION failed error={error}"));
            self.delete_tray_icon();
            return Err(format!(
                "Windows rejected NOTIFYICON_VERSION_4 (error {error})"
            ));
        }
        debug_log("NIM_SETVERSION succeeded version=4");
        Ok(())
    }

    fn modify_tray_icon(&self) -> Result<(), String> {
        self.modify_tray_icon_with(self.dpi_icons.tray)
    }

    fn modify_tray_icon_with(&self, icon: HICON) -> Result<(), String> {
        let data = self.tray_data_with_icon(NIF_ICON | NIF_TIP | NIF_SHOWTIP | NIF_GUID, icon)?;
        debug_shell_notify("NIM_MODIFY", &data);
        // SAFETY: `data` is fully initialized and the icon has already been added.
        if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) }.as_bool() {
            let error = unsafe { GetLastError().0 };
            return Err(format!(
                "Windows rejected a tray icon update (error {error})"
            ));
        }
        debug_log("NIM_MODIFY succeeded");
        Ok(())
    }

    fn delete_tray_icon(&mut self) {
        if !self.tray_added {
            return;
        }
        if let Ok(data) = self.tray_data(NIF_MESSAGE | NIF_GUID) {
            debug_shell_notify("NIM_DELETE", &data);
            // SAFETY: Removing a previously added icon is idempotent from the app perspective.
            if unsafe { Shell_NotifyIconW(NIM_DELETE, &data) }.as_bool() {
                debug_log("NIM_DELETE succeeded");
            } else {
                let error = unsafe { GetLastError().0 };
                debug_log(&format!("NIM_DELETE failed error={error}"));
            }
        }
        self.tray_added = false;
    }

    fn tray_data(
        &self,
        flags: windows::Win32::UI::Shell::NOTIFY_ICON_DATA_FLAGS,
    ) -> Result<NOTIFYICONDATAW, String> {
        self.tray_data_with_icon(flags, self.dpi_icons.tray)
    }

    fn tray_data_with_icon(
        &self,
        flags: windows::Win32::UI::Shell::NOTIFY_ICON_DATA_FLAGS,
        icon: HICON,
    ) -> Result<NOTIFYICONDATAW, String> {
        let view = self
            .view
            .as_ref()
            .ok_or_else(|| "tray projection was unavailable".to_owned())?;
        let mut data = NOTIFYICONDATAW {
            cbSize: size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self
                .tray_message_hwnd
                .ok_or_else(|| "tray message window was unavailable".to_owned())?,
            uID: TRAY_ID,
            uFlags: flags,
            uCallbackMessage: TRAY_CALLBACK,
            hIcon: icon,
            guidItem: TRAY_GUID,
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
            AlertKind::Remaining50 => (
                "Codex 额度提醒",
                format!("{} 剩余 {}%。", alert.window_name, alert.remaining_percent),
                NIIF_INFO,
            ),
            AlertKind::Remaining20 => (
                "Codex 额度提醒",
                format!("{} 剩余 {}%。", alert.window_name, alert.remaining_percent),
                NIIF_WARNING,
            ),
            AlertKind::Remaining10 => (
                "Codex 额度提醒",
                format!("{} 剩余 {}%。", alert.window_name, alert.remaining_percent),
                NIIF_ERROR,
            ),
        };
        let mut data = self.tray_data(NIF_INFO | NIF_GUID)?;
        copy_wide(&mut data.szInfoTitle, title);
        copy_wide(&mut data.szInfo, &body);
        data.dwInfoFlags = icon | NIIF_NOSOUND | NIIF_RESPECT_QUIET_TIME;
        debug_shell_notify("NIM_MODIFY(info)", &data);
        // SAFETY: Notification text is copied into fixed buffers in `data`.
        if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) }.as_bool() {
            let error = unsafe { GetLastError().0 };
            debug_log(&format!("NIM_MODIFY(info) failed error={error}"));
            return Err("Windows rejected a quota notification".to_owned());
        }
        debug_log("NIM_MODIFY(info) succeeded");
        Ok(())
    }

    fn show_main_window(&mut self) -> Result<(), String> {
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
        let work = crate::windows_visuals::RectI {
            left: work.left,
            top: work.top,
            right: work.right,
            bottom: work.bottom,
        };
        let (x, y) = bottom_right_popup_origin(work, layout.width, layout.height);
        self.hover = InteractionTarget::None;
        self.pressed = InteractionTarget::None;
        self.focus = InteractionTarget::Refresh;
        // SAFETY: Positioning and showing our own popup is valid on this UI thread.
        set_window_pos_logged(
            hwnd,
            Some(HWND_TOPMOST),
            x,
            y,
            layout.width,
            layout.height,
            SWP_NOACTIVATE,
            "show_main_window",
        )?;
        show_window_logged(hwnd, SW_SHOWNORMAL, "show_main_window");
        // SAFETY: Activation is restricted to the explicit show entry point.
        unsafe {
            let _ = SetForegroundWindow(hwnd);
        }
        Ok(())
    }

    fn hide_main_window(&mut self) {
        let Ok(hwnd) = self.hwnd() else {
            return;
        };
        show_window_logged(hwnd, SW_HIDE, "hide_main_window");
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
        let alerts = unsafe { CreatePopupMenu() }.map_err(win_error("create alert menu"))?;
        for (enabled, id, label) in [
            (
                self.settings.notifications.remaining_50_percent,
                CMD_ALERT_50,
                w!("剩余 50%"),
            ),
            (
                self.settings.notifications.remaining_20_percent,
                CMD_ALERT_20,
                w!("剩余 20%"),
            ),
            (
                self.settings.notifications.remaining_10_percent,
                CMD_ALERT_10,
                w!("剩余 10%"),
            ),
        ] {
            append_menu(alerts, checked(enabled), id, label)?;
        }
        append_menu(menu, MF_POPUP, alerts.0 as usize, w!("额度提醒"))?;
        let refresh_modes =
            unsafe { CreatePopupMenu() }.map_err(win_error("create refresh menu"))?;
        for (mode, id, label) in [
            (RefreshMode::Auto, CMD_REFRESH_AUTO, w!("自动")),
            (RefreshMode::Every5Minutes, CMD_REFRESH_5, w!("每 5 分钟")),
            (
                RefreshMode::Every15Minutes,
                CMD_REFRESH_15,
                w!("每 15 分钟"),
            ),
            (
                RefreshMode::Every30Minutes,
                CMD_REFRESH_30,
                w!("每 30 分钟"),
            ),
            (RefreshMode::ManualOnly, CMD_REFRESH_MANUAL, w!("仅手动")),
        ] {
            append_menu(
                refresh_modes,
                checked(self.settings.refresh_mode == mode),
                id,
                label,
            )?;
        }
        append_menu(menu, MF_POPUP, refresh_modes.0 as usize, w!("刷新间隔"))?;
        append_menu(menu, MF_SEPARATOR, 0, PCWSTR::null())?;
        append_menu(
            menu,
            checked(self.settings.persist_quota_cache),
            CMD_TOGGLE_CACHE,
            w!("保存非敏感额度缓存"),
        )?;
        append_menu(
            menu,
            checked(self.settings.start_with_windows),
            CMD_TOGGLE_STARTUP,
            w!("开机启动"),
        )?;
        append_menu(menu, MF_STRING, CMD_CLEAR_CACHE, w!("清除本地额度缓存"))?;
        append_menu(menu, MF_STRING, CMD_COPY_DIAGNOSTICS, w!("复制诊断信息"))?;
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
                if self.view.as_ref().is_some_and(|view| !view.can_refresh) {
                    return Ok(());
                }
                self.source.refresh(RefreshReason::Manual)?;
                self.begin_manual_refresh_feedback();
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
            CMD_ALERT_50 => self.toggle_alert_threshold(50),
            CMD_ALERT_20 => self.toggle_alert_threshold(20),
            CMD_ALERT_10 => self.toggle_alert_threshold(10),
            CMD_REFRESH_AUTO => self.change_refresh_mode(RefreshMode::Auto),
            CMD_REFRESH_5 => self.change_refresh_mode(RefreshMode::Every5Minutes),
            CMD_REFRESH_15 => self.change_refresh_mode(RefreshMode::Every15Minutes),
            CMD_REFRESH_30 => self.change_refresh_mode(RefreshMode::Every30Minutes),
            CMD_REFRESH_MANUAL => self.change_refresh_mode(RefreshMode::ManualOnly),
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
            CMD_COPY_DIAGNOSTICS => copy_diagnostics(&self.source.snapshot()),
            CMD_EXIT => {
                // SAFETY: Destroying the app-owned window triggers orderly WM_DESTROY cleanup.
                unsafe { DestroyWindow(self.hwnd()?) }.map_err(win_error("close tray window"))
            }
            _ => Ok(()),
        }
    }

    fn toggle_alert_threshold(&mut self, threshold: u8) -> Result<(), String> {
        let current = match threshold {
            50 => self.settings.notifications.remaining_50_percent,
            20 => self.settings.notifications.remaining_20_percent,
            10 => self.settings.notifications.remaining_10_percent,
            _ => return Ok(()),
        };
        if !current {
            let quota = self.source.snapshot().quota;
            if self
                .alert_tracker
                .enable_threshold(threshold, quota.as_ref())
            {
                self.save_alert_state()?;
            }
        }
        match threshold {
            50 => self.settings.notifications.remaining_50_percent = !current,
            20 => self.settings.notifications.remaining_20_percent = !current,
            10 => self.settings.notifications.remaining_10_percent = !current,
            _ => {}
        }
        self.save_settings()
    }

    fn change_refresh_mode(&mut self, mode: RefreshMode) -> Result<(), String> {
        if self.settings.refresh_mode == mode {
            return Ok(());
        }
        let previous = self.settings.refresh_mode;
        self.source.set_refresh_mode(mode)?;
        self.settings.refresh_mode = mode;
        if let Err(error) = self.save_settings() {
            self.settings.refresh_mode = previous;
            let _ = self.source.set_refresh_mode(previous);
            return Err(error);
        }
        self.refresh_projection()
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
        let state = self.source.snapshot();
        let Some(reason) = refresh_reason(
            event,
            self.settings.refresh_on_network_restore,
            self.settings.refresh_mode,
            state.stale_after_secs.max(0) as u64,
        ) else {
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

unsafe extern "system" fn tray_message_proc(
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
    if context.as_ref().is_some_and(|context| {
        context.taskbar_created_message != 0 && message == context.taskbar_created_message
    }) {
        debug_event(hwnd, "TaskbarCreated", wparam, lparam);
        if let Some(context) = unsafe { context_for(hwnd) } {
            // Explorer owns the notification area. Re-add only after it explicitly announces a
            // restart; desired_visible is deliberately not touched here.
            context.delete_tray_icon();
            let _ = context.add_tray_icon();
        }
        return LRESULT(0);
    }

    if message == TRAY_CALLBACK {
        let event = tray_event_from_lparam(lparam);
        debug_log(&format!(
            "tray_message_hwnd event raw_wparam={} raw_lparam={} event={event}",
            wparam.0, lparam.0
        ));
        if let Some(context) = context {
            match event {
                event if tray_event_is_toggle(event) => {
                    let Some(main_hwnd) = context.hwnd else {
                        return LRESULT(0);
                    };
                    context.toggle_post_count = context.toggle_post_count.saturating_add(1);
                    debug_log(&format!(
                        "TRAY WM_LBUTTONUP -> PostMessage WM_APP_TOGGLE_WINDOW count={} main_hwnd={}",
                        context.toggle_post_count, main_hwnd.0 as usize
                    ));
                    // SAFETY: The main window belongs to this UI thread; the queued message is
                    // the sole visibility toggle entry point for a tray click.
                    if let Err(error) = unsafe {
                        PostMessageW(Some(main_hwnd), TOGGLE_WINDOW_MESSAGE, WPARAM(0), LPARAM(0))
                    } {
                        debug_log(&format!(
                            "PostMessage WM_APP_TOGGLE_WINDOW failed: {}",
                            win_error("post tray toggle message")(error)
                        ));
                    }
                }
                WM_RBUTTONUP => {
                    let Some(main_hwnd) = context.hwnd else {
                        return LRESULT(0);
                    };
                    debug_log("TRAY WM_RBUTTONUP -> PostMessage WM_APP_SHOW_MENU");
                    // SAFETY: Menu creation and tracking stay on the main window's UI message
                    // path, keeping the shell callback free of visibility side effects.
                    let _ = unsafe {
                        PostMessageW(Some(main_hwnd), SHOW_MENU_MESSAGE, WPARAM(0), LPARAM(0))
                    };
                }
                event if event == NIN_BALLOONUSERCLICK => {
                    let Some(main_hwnd) = context.hwnd else {
                        return LRESULT(0);
                    };
                    let _ = unsafe {
                        PostMessageW(Some(main_hwnd), SHOW_CARD_MESSAGE, WPARAM(0), LPARAM(0))
                    };
                }
                _ => {
                    debug_log(&format!("tray event ignored event={event}"));
                }
            }
        }
        return LRESULT(0);
    }

    if message == WM_NCDESTROY {
        // SAFETY: Default cleanup for the private message-only window.
        return unsafe { DefWindowProcW(hwnd, message, wparam, lparam) };
    }
    // SAFETY: Default processing for shell messages not owned by this app.
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
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
        TOGGLE_WINDOW_MESSAGE => {
            debug_event(hwnd, "WM_APP_TOGGLE_WINDOW", wparam, lparam);
            if let Some(context) = context {
                context.toggle_handle_count = context.toggle_handle_count.saturating_add(1);
                let before = context.desired_visible;
                context.desired_visible = !before;
                debug_log(&format!(
                    "WM_APP_TOGGLE_WINDOW handled count={} desired_visible {} -> {}",
                    context.toggle_handle_count, before, context.desired_visible
                ));
                if context.desired_visible {
                    let _ = context.show_main_window();
                } else {
                    context.hide_main_window();
                }
            }
            LRESULT(0)
        }
        SHOW_MENU_MESSAGE => {
            debug_event(hwnd, "WM_APP_SHOW_MENU", wparam, lparam);
            if let Some(context) = context {
                let _ = context.show_menu();
            }
            LRESULT(0)
        }
        EXIT_PROCESS_MESSAGE => {
            debug_event(hwnd, "WM_APP_EXIT_PROCESS", wparam, lparam);
            // SAFETY: This private message is posted only by request_existing_shutdown or the
            // owning exit command, and DestroyWindow drives the normal cleanup path.
            let _ = unsafe { DestroyWindow(hwnd) };
            LRESULT(0)
        }
        WM_TIMER if wparam.0 == TIMER_ID => {
            if let Some(context) = context {
                let _ = context.refresh_projection();
            }
            LRESULT(0)
        }
        WM_TIMER if wparam.0 == REFRESH_STATUS_TIMER_ID => {
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
                context.desired_visible = true;
                let _ = context.show_main_window();
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
                        context.desired_visible = false;
                        context.hide_main_window();
                    }
                    KEY_RETURN => {
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
            debug_event(hwnd, "WM_DPICHANGED", wparam, lparam);
            if let Some(context) = context {
                context.dpi = (wparam.0 as u32 & 0xffff).max(96);
                context.refresh_dpi_icons(context.dpi);
                let layout = context.layout();
                // SAFETY: WM_DPICHANGED supplies a pointer to the recommended window rectangle.
                let suggested = unsafe { &*(lparam.0 as *const RECT) };
                let _ = set_window_pos_logged(
                    hwnd,
                    None,
                    suggested.left,
                    suggested.top,
                    layout.width,
                    layout.height,
                    SWP_NOACTIVATE,
                    "WM_DPICHANGED",
                );
                context.invalidate();
            }
            // SAFETY: The context borrow above has ended; complete the DPI repaint immediately.
            let _ = unsafe { UpdateWindow(hwnd) };
            LRESULT(0)
        }
        WM_ACTIVATE => {
            debug_event(hwnd, "WM_ACTIVATE", wparam, lparam);
            LRESULT(0)
        }
        WM_ACTIVATEAPP => {
            debug_event(hwnd, "WM_ACTIVATEAPP", wparam, lparam);
            LRESULT(0)
        }
        WM_SIZE => {
            debug_event(hwnd, "WM_SIZE", wparam, lparam);
            LRESULT(0)
        }
        WM_SYSCOMMAND => {
            debug_event(hwnd, "WM_SYSCOMMAND", wparam, lparam);
            if (wparam.0 & 0xfff0) == SC_MINIMIZE as usize {
                if let Some(context) = context {
                    context.desired_visible = false;
                    context.hide_main_window();
                }
                LRESULT(0)
            } else {
                // SAFETY: Preserve normal system command behavior for commands we do not own.
                unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
            }
        }
        WM_CLOSE => {
            debug_event(hwnd, "WM_CLOSE", wparam, lparam);
            if let Some(context) = context {
                context.desired_visible = false;
                context.hide_main_window();
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
        WM_NCDESTROY => LRESULT(0),
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
        fill(dc, client, rgb(245, 247, 250));
    }
    let body = create_font(layout.dpi, 11, FW_NORMAL.0 as i32, w!("Segoe UI"));
    let small = create_font(layout.dpi, 10, FW_NORMAL.0 as i32, w!("Segoe UI"));
    let credit = create_font(layout.dpi, 10, FW_SEMIBOLD.0 as i32, w!("Segoe UI"));
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
        unsafe { SelectObject(dc, HGDIOBJ(title.0)) };
        draw_text(dc, &view.title, layout.title.to_win32(), rgb(23, 33, 43));
        if let Some(badge) = view.plan_badge.as_deref() {
            let title_width = measure_text_width(dc, &view.title);
            unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
            let badge_width = measure_text_width(dc, badge) + scale_for_dpi(18, layout.dpi);
            let badge_left = (layout.title.left + title_width + scale_for_dpi(10, layout.dpi))
                .min(layout.title.right - badge_width);
            let badge_rect = crate::windows_visuals::RectI {
                left: badge_left,
                top: layout.title.top + scale_for_dpi(3, layout.dpi),
                right: badge_left + badge_width,
                bottom: layout.title.bottom - scale_for_dpi(3, layout.dpi),
            };
            rounded_fill(
                dc,
                badge_rect,
                badge_rect.height() / 2,
                rgb(226, 247, 240),
                context.gdiplus.as_ref(),
            );
            draw_text_with_format(
                dc,
                badge,
                badge_rect.to_win32(),
                rgb(10, 118, 87),
                DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
            );
        }
        unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
        draw_text(
            dc,
            &view.status_line,
            layout.status.to_win32(),
            status_text_color(view.status_tone),
        );

        for (window, panel) in view.windows.iter().take(3).zip(&layout.window_panels) {
            rounded_fill(
                dc,
                *panel,
                scale_for_dpi(14, layout.dpi),
                rgb(255, 255, 255),
                context.gdiplus.as_ref(),
            );
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
                rgb(23, 33, 43),
            );
            unsafe { SelectObject(dc, HGDIOBJ(percent.0)) };
            draw_text_with_format(
                dc,
                &window.percent_value,
                rect(
                    panel.right - scale_for_dpi(150, layout.dpi),
                    panel.top + scale_for_dpi(3, layout.dpi),
                    panel.right - scale_for_dpi(56, layout.dpi),
                    panel.top + scale_for_dpi(38, layout.dpi),
                ),
                severity_color(window.remaining_percent),
                DT_RIGHT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
            );
            unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
            draw_text_with_format(
                dc,
                &window.percent_suffix,
                rect(
                    panel.right - scale_for_dpi(54, layout.dpi),
                    panel.top + scale_for_dpi(8, layout.dpi),
                    panel.right - scale_for_dpi(14, layout.dpi),
                    panel.top + scale_for_dpi(36, layout.dpi),
                ),
                severity_color(window.remaining_percent),
                DT_RIGHT | DT_SINGLELINE | DT_VCENTER,
            );
            let rail = crate::windows_visuals::RectI {
                left: panel.left + scale_for_dpi(14, layout.dpi),
                top: panel.top + scale_for_dpi(42, layout.dpi),
                right: panel.right - scale_for_dpi(14, layout.dpi),
                bottom: panel.top + scale_for_dpi(50, layout.dpi),
            };
            draw_progress_bar(
                dc,
                rail,
                window.progress_percent,
                severity_color(window.remaining_percent),
                context.gdiplus.as_ref(),
            );
            unsafe { SelectObject(dc, HGDIOBJ(small.0)) };
            draw_text(
                dc,
                &window.reset_line,
                rect(
                    panel.left + scale_for_dpi(14, layout.dpi),
                    panel.top + scale_for_dpi(55, layout.dpi),
                    panel.right - scale_for_dpi(14, layout.dpi),
                    panel.bottom - scale_for_dpi(7, layout.dpi),
                ),
                rgb(71, 85, 105),
            );
        }

        unsafe { SelectObject(dc, HGDIOBJ(credit.0)) };
        draw_clock_icon(
            dc,
            layout.credits_icon,
            rgb(100, 116, 139),
            context.gdiplus.as_ref(),
        );
        draw_text(
            dc,
            &view.reset_credits,
            layout.credits_text.to_win32(),
            rgb(71, 85, 105),
        );
        if context.settings_warning {
            draw_text(
                dc,
                "设置文件无效，已使用安全默认值",
                layout.warning.to_win32(),
                rgb(180, 83, 9),
            );
        }
        unsafe { SelectObject(dc, HGDIOBJ(body.0)) };
        draw_button(
            dc,
            layout.refresh_button,
            InteractionTarget::Refresh,
            context,
            layout.dpi,
            context.gdiplus.as_ref(),
            ButtonPresentation {
                label: &view.refresh_label,
                primary: false,
            },
        );
        draw_button(
            dc,
            layout.usage_button,
            InteractionTarget::Usage,
            context,
            layout.dpi,
            context.gdiplus.as_ref(),
            ButtonPresentation {
                label: "打开官方用量页面",
                primary: true,
            },
        );
    }
    unsafe {
        SelectObject(dc, previous_font);
        for font in [body, small, credit, title, percent] {
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

fn measure_small_line_height(dpi: u32) -> i32 {
    // Measure the exact HFONT used for the information strip. The result is in physical pixels
    // because the backing DC and the PM-aware window are both physical-pixel surfaces.
    let dc = unsafe { CreateCompatibleDC(None) };
    if dc.0.is_null() {
        return point_size_to_pixels(10, dpi).max(1);
    }
    let font = create_font(dpi, 10, FW_NORMAL.0 as i32, w!("Segoe UI"));
    let previous = unsafe { SelectObject(dc, HGDIOBJ(font.0)) };
    let mut metrics = TEXTMETRICW::default();
    let measured = if unsafe { GetTextMetricsW(dc, &mut metrics) }.as_bool() {
        metrics.tmHeight
    } else {
        point_size_to_pixels(10, dpi)
    };
    unsafe {
        SelectObject(dc, previous);
        let _ = DeleteObject(HGDIOBJ(font.0));
        let _ = DeleteDC(dc);
    }
    measured.max(1)
}

#[cfg(debug_assertions)]
fn debug_layout(layout: &CardLayout, line_height: i32) {
    let message = format!(
        "CodexQuotaTray layout dpi={} physical={}x{} credits=({},{}-{},{}), text_height={}, buttons=({},{}-{},{}),({},{}-{},{})\n",
        layout.dpi,
        layout.width,
        layout.height,
        layout.credits.left,
        layout.credits.top,
        layout.credits.right,
        layout.credits.bottom,
        line_height,
        layout.refresh_button.left,
        layout.refresh_button.top,
        layout.refresh_button.right,
        layout.refresh_button.bottom,
        layout.usage_button.left,
        layout.usage_button.top,
        layout.usage_button.right,
        layout.usage_button.bottom,
    );
    let wide = message
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: `wide` is NUL-terminated and valid for the synchronous debug output call.
    unsafe { OutputDebugStringW(PCWSTR(wide.as_ptr())) };
}

#[cfg(not(debug_assertions))]
fn debug_layout(_layout: &CardLayout, _line_height: i32) {}

fn point_size_to_pixels(points: i32, dpi: u32) -> i32 {
    ((i64::from(points) * i64::from(dpi) + 36) / 72).clamp(1, i64::from(i32::MAX)) as i32
}

struct ButtonPresentation<'a> {
    label: &'a str,
    primary: bool,
}

fn draw_button(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    target: InteractionTarget,
    context: &AppContext,
    dpi: u32,
    gdiplus: Option<&GdiPlusSession>,
    presentation: ButtonPresentation<'_>,
) {
    let ButtonPresentation { label, primary } = presentation;
    let radius = scale_for_dpi(12, dpi);
    let focused = context.focus == target;
    let inner = area;
    let color = if context.pressed == target {
        if primary {
            rgb(15, 159, 123)
        } else {
            rgb(226, 232, 240)
        }
    } else if context.hover == target {
        if primary {
            rgb(25, 201, 154)
        } else {
            rgb(240, 253, 250)
        }
    } else if primary {
        rgb(22, 184, 138)
    } else {
        rgb(255, 255, 255)
    };
    rounded_shape(
        dc,
        inner,
        radius,
        color,
        if focused {
            Some((
                if primary {
                    rgb(22, 140, 103)
                } else {
                    rgb(22, 184, 138)
                },
                if primary { 2.0 } else { 1.0 },
            ))
        } else if primary {
            None
        } else {
            Some((rgb(220, 228, 234), 1.0))
        },
        gdiplus,
    );
    let label_area = rect(
        inner.left + scale_for_dpi(8, dpi),
        inner.top,
        inner.right - scale_for_dpi(8, dpi),
        inner.bottom,
    );
    draw_text_with_format(
        dc,
        label,
        label_area,
        if primary {
            rgb(255, 255, 255)
        } else {
            rgb(23, 33, 43)
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

fn measure_text_width(dc: HDC, text: &str) -> i32 {
    let mut wide = text.encode_utf16().collect::<Vec<_>>();
    let mut area = RECT::default();
    // SAFETY: DT_CALCRECT only updates the supplied rectangle and does not paint.
    unsafe {
        DrawTextW(dc, &mut wide, &mut area, DT_CALCRECT | DT_SINGLELINE);
    }
    area.right.saturating_sub(area.left).max(0)
}

fn fill(dc: windows::Win32::Graphics::Gdi::HDC, area: &RECT, color: COLORREF) {
    // SAFETY: Brush is created for this call and deleted immediately after FillRect.
    unsafe {
        let brush = CreateSolidBrush(color);
        FillRect(dc, area, brush);
        let _ = DeleteObject(HGDIOBJ(brush.0));
    }
}

fn rounded_fill(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    radius: i32,
    color: COLORREF,
    gdiplus: Option<&GdiPlusSession>,
) {
    if area.width() <= 0 || area.height() <= 0 {
        return;
    }
    if let Some(gdiplus) = gdiplus
        && draw_rounded_gdiplus(dc, area, radius, color, None, gdiplus)
    {
        return;
    }
    rounded_fill_gdi(dc, area, radius, color);
}

fn rounded_shape(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    radius: i32,
    fill_color: COLORREF,
    outline: Option<(COLORREF, f32)>,
    gdiplus: Option<&GdiPlusSession>,
) {
    if area.width() <= 0 || area.height() <= 0 {
        return;
    }
    if let Some(gdiplus) = gdiplus
        && draw_rounded_gdiplus(dc, area, radius, fill_color, outline, gdiplus)
    {
        return;
    }
    rounded_shape_gdi(dc, area, radius, fill_color, outline);
}

fn rounded_fill_gdi(dc: HDC, area: crate::windows_visuals::RectI, radius: i32, color: COLORREF) {
    // SAFETY: The fallback region and brush are owned by this call and released immediately.
    unsafe {
        let diameter = radius.saturating_mul(2).max(1);
        let region = CreateRoundRectRgn(
            area.left,
            area.top,
            area.right,
            area.bottom,
            diameter,
            diameter,
        );
        let brush = CreateSolidBrush(color);
        let _ = FillRgn(dc, region, brush);
        let _ = DeleteObject(HGDIOBJ(region.0));
        let _ = DeleteObject(HGDIOBJ(brush.0));
    }
}

fn rounded_shape_gdi(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    radius: i32,
    fill_color: COLORREF,
    outline: Option<(COLORREF, f32)>,
) {
    let (outline_color, outline_width) = outline.unwrap_or((fill_color, 0.0));
    // SAFETY: The fallback region and brushes are owned by this call and released immediately.
    unsafe {
        let diameter = radius.saturating_mul(2).max(1);
        let region = CreateRoundRectRgn(
            area.left,
            area.top,
            area.right,
            area.bottom,
            diameter,
            diameter,
        );
        let brush = CreateSolidBrush(fill_color);
        let _ = FillRgn(dc, region, brush);
        if outline_width > 0.0 {
            let outline_brush = CreateSolidBrush(outline_color);
            let _ = FrameRgn(
                dc,
                region,
                outline_brush,
                outline_width.round().max(1.0) as i32,
                outline_width.round().max(1.0) as i32,
            );
            let _ = DeleteObject(HGDIOBJ(outline_brush.0));
        }
        let _ = DeleteObject(HGDIOBJ(region.0));
        let _ = DeleteObject(HGDIOBJ(brush.0));
    }
}

fn draw_rounded_gdiplus(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    radius: i32,
    fill_color: COLORREF,
    outline: Option<(COLORREF, f32)>,
    _session: &GdiPlusSession,
) -> bool {
    // SAFETY: All GDI+ handles are created and released within this function. The HDC is owned by
    // the current paint cycle and remains valid until the synchronous GDI+ calls return.
    unsafe {
        let mut graphics: *mut GpGraphics = null_mut();
        if GdipCreateFromHDC(dc, &mut graphics) != GdiPlusOk || graphics.is_null() {
            return false;
        }
        let _ = GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias8x8);
        let _ = GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);

        let stroke_inset = outline.map_or(0.0, |(_, width)| width / 2.0);
        let left = area.left as f32 + stroke_inset + 0.5;
        let top = area.top as f32 + stroke_inset + 0.5;
        let right = area.right as f32 - stroke_inset - 0.5;
        let bottom = area.bottom as f32 - stroke_inset - 0.5;
        let radius = (radius as f32 - stroke_inset)
            .max(0.5)
            .min(((right - left) / 2.0).min((bottom - top) / 2.0));
        let mut path: *mut GpPath = null_mut();
        if GdipCreatePath(FillModeAlternate, &mut path) != GdiPlusOk || path.is_null() {
            let _ = GdipDeleteGraphics(graphics);
            return false;
        }
        let diameter = radius * 2.0;
        let path_ok = [
            GdipAddPathLine(path, left + radius, top, right - radius, top),
            GdipAddPathArc(path, right - diameter, top, diameter, diameter, 270.0, 90.0),
            GdipAddPathLine(path, right, top + radius, right, bottom - radius),
            GdipAddPathArc(
                path,
                right - diameter,
                bottom - diameter,
                diameter,
                diameter,
                0.0,
                90.0,
            ),
            GdipAddPathLine(path, right - radius, bottom, left + radius, bottom),
            GdipAddPathArc(
                path,
                left,
                bottom - diameter,
                diameter,
                diameter,
                90.0,
                90.0,
            ),
            GdipAddPathLine(path, left, bottom - radius, left, top + radius),
            GdipAddPathArc(path, left, top, diameter, diameter, 180.0, 90.0),
            GdipClosePathFigure(path),
        ]
        .into_iter()
        .all(|status| status == GdiPlusOk);
        if !path_ok {
            let _ = GdipDeletePath(path);
            let _ = GdipDeleteGraphics(graphics);
            return false;
        }

        let mut brush = null_mut();
        let fill_ok =
            GdipCreateSolidFill(to_argb(fill_color), &mut brush) == GdiPlusOk && !brush.is_null();
        if fill_ok {
            let _ = GdipFillPath(graphics, brush.cast::<GpBrush>(), path);
            let _ = GdipDeleteBrush(brush.cast::<GpBrush>());
        }
        let mut pen: *mut GpPen = null_mut();
        let outline_ok = if let Some((color, width)) = outline {
            GdipCreatePen1(to_argb(color), width.max(1.0), UnitPixel, &mut pen) == GdiPlusOk
                && !pen.is_null()
        } else {
            true
        };
        if outline_ok && let Some(_) = outline {
            let _ = GdipDrawPath(graphics, pen, path);
        }
        if !pen.is_null() {
            let _ = GdipDeletePen(pen);
        }
        let _ = GdipDeletePath(path);
        let _ = GdipDeleteGraphics(graphics);
        fill_ok && outline_ok
    }
}

fn to_argb(color: COLORREF) -> u32 {
    let value = color.0;
    0xff00_0000 | ((value & 0x0000_00ff) << 16) | (value & 0x0000_ff00) | ((value >> 16) & 0xff)
}

fn draw_progress_bar(
    dc: HDC,
    rail: crate::windows_visuals::RectI,
    percent: u8,
    color: COLORREF,
    gdiplus: Option<&GdiPlusSession>,
) {
    let radius = (rail.height() / 2).max(1);
    rounded_fill(dc, rail, radius, rgb(220, 228, 234), gdiplus);
    let fill = crate::windows_visuals::progress_fill_rect(rail, percent);
    if fill.width() > 0 {
        rounded_fill(
            dc,
            fill,
            crate::windows_visuals::progress_radius(rail, fill),
            color,
            gdiplus,
        );
    }
}

fn draw_clock_icon(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    color: COLORREF,
    gdiplus: Option<&GdiPlusSession>,
) {
    if let Some(gdiplus) = gdiplus {
        let _ = draw_clock_icon_gdiplus(dc, area, color, gdiplus);
    }
    // GDI+ is present on supported Windows versions. If it cannot initialize (for example in a
    // constrained remote session), leave the strip text-only rather than reintroducing a legacy
    // shell bitmap with a mismatched visual style.
}

fn draw_clock_icon_gdiplus(
    dc: HDC,
    area: crate::windows_visuals::RectI,
    color: COLORREF,
    _session: &GdiPlusSession,
) -> bool {
    // SAFETY: Every GDI+ object is created and released within this synchronous paint operation.
    unsafe {
        let mut graphics: *mut GpGraphics = null_mut();
        if GdipCreateFromHDC(dc, &mut graphics) != GdiPlusOk || graphics.is_null() {
            return false;
        }
        let _ = GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias8x8);
        let _ = GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);

        let size = area.width().min(area.height()).max(1) as f32;
        let left = area.left as f32 + 0.75;
        let top = area.top as f32 + 0.75;
        let size = (size - 1.5).max(1.0);
        let stroke = (size / 14.0).clamp(1.0, 2.0);
        let mut pen: *mut GpPen = null_mut();
        if GdipCreatePen1(to_argb(color), stroke, UnitPixel, &mut pen) != GdiPlusOk || pen.is_null()
        {
            let _ = GdipDeleteGraphics(graphics);
            return false;
        }
        let center_x = left + size / 2.0;
        let center_y = top + size / 2.0;
        let _ = GdipDrawEllipse(graphics, pen, left, top, size, size);
        let _ = GdipDrawLine(
            graphics,
            pen,
            center_x,
            top + size * 0.24,
            center_x,
            center_y,
        );
        let _ = GdipDrawLine(
            graphics,
            pen,
            center_x,
            center_y,
            left + size * 0.72,
            top + size * 0.62,
        );
        let _ = GdipDeletePen(pen);
        let _ = GdipDeleteGraphics(graphics);
        true
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
        i64::MIN..=19 => rgb(190, 58, 52),
        20..=50 => rgb(180, 83, 9),
        _ => rgb(22, 140, 103),
    }
}

fn status_text_color(state: StatusTone) -> COLORREF {
    match state {
        StatusTone::Success => rgb(22, 140, 103),
        StatusTone::Warning => rgb(180, 83, 9),
        StatusTone::Error => rgb(190, 58, 52),
        StatusTone::Refreshing => rgb(37, 99, 235),
        StatusTone::Neutral => rgb(71, 85, 105),
    }
}

fn set_window_icons(hwnd: HWND, window_icon_big: HICON, window_icon_small: HICON) {
    // SAFETY: Both icon handles remain owned by AppContext until after the window is destroyed;
    // WM_SETICON only stores the handles and does not transfer ownership.
    unsafe {
        let _ = SendMessageW(
            hwnd,
            WM_SETICON,
            Some(WPARAM(ICON_BIG as usize)),
            Some(LPARAM(window_icon_big.0 as isize)),
        );
        let _ = SendMessageW(
            hwnd,
            WM_SETICON,
            Some(WPARAM(ICON_SMALL as usize)),
            Some(LPARAM(window_icon_small.0 as isize)),
        );
    }
    debug_log(&format!(
        "WM_SETICON hwnd={} big={} small={}",
        hwnd.0 as usize,
        icon_handle_value(window_icon_big),
        icon_handle_value(window_icon_small)
    ));
}

fn window_state(hwnd: HWND) -> (bool, bool, bool) {
    // SAFETY: The HWND is owned by this process and is queried synchronously on the UI thread.
    unsafe {
        (
            IsWindowVisible(hwnd).as_bool(),
            IsIconic(hwnd).as_bool(),
            GetForegroundWindow() == hwnd,
        )
    }
}

fn show_window_logged(
    hwnd: HWND,
    command: windows::Win32::UI::WindowsAndMessaging::SHOW_WINDOW_CMD,
    source: &str,
) {
    let before = window_state(hwnd);
    // SAFETY: The HWND belongs to this process and the command is a valid ShowWindow command.
    unsafe {
        let _ = ShowWindow(hwnd, command);
    }
    let after = window_state(hwnd);
    debug_log(&format!(
        "{source}: ShowWindow({command:?}) visible={} iconic={} foreground={} -> visible={} iconic={} foreground={}",
        before.0, before.1, before.2, after.0, after.1, after.2
    ));
}

#[allow(clippy::too_many_arguments)]
fn set_window_pos_logged(
    hwnd: HWND,
    insert_after: Option<HWND>,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    flags: windows::Win32::UI::WindowsAndMessaging::SET_WINDOW_POS_FLAGS,
    source: &str,
) -> Result<(), String> {
    let before = window_state(hwnd);
    // SAFETY: Coordinates and flags are calculated for this app-owned window on the UI thread.
    unsafe { SetWindowPos(hwnd, insert_after, x, y, width, height, flags) }
        .map_err(|error| format!("{source}: SetWindowPos failed: {error}"))?;
    let after = window_state(hwnd);
    debug_log(&format!(
        "{source}: SetWindowPos x={x} y={y} size={width}x{height} flags={flags:?} visible={} iconic={} foreground={} -> visible={} iconic={} foreground={}",
        before.0, before.1, before.2, after.0, after.1, after.2
    ));
    Ok(())
}

#[cfg(debug_assertions)]
static DEBUG_SEQUENCE: AtomicU64 = AtomicU64::new(1);
#[cfg(debug_assertions)]
static DEBUG_START: OnceLock<Instant> = OnceLock::new();

#[cfg(debug_assertions)]
fn debug_log(message: &str) {
    let sequence = DEBUG_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let elapsed = DEBUG_START.get_or_init(Instant::now).elapsed().as_millis();
    let line = format!("CodexQuotaTray [#{sequence} +{elapsed}ms] {message}\n");
    let wide = line
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: `wide` is NUL-terminated and valid for this synchronous call.
    unsafe { OutputDebugStringW(PCWSTR(wide.as_ptr())) };
}

#[cfg(not(debug_assertions))]
fn debug_log(_message: &str) {}

fn debug_event(hwnd: HWND, event: &str, wparam: WPARAM, lparam: LPARAM) {
    let state = window_state(hwnd);
    debug_log(&format!(
        "{event} wparam={} lparam={} visible={} iconic={} foreground={}",
        wparam.0, lparam.0, state.0, state.1, state.2
    ));
}

fn debug_shell_notify(operation: &str, data: &NOTIFYICONDATAW) {
    debug_log(&format!(
        "Shell_NotifyIcon {operation} hwnd={} uid={} icon={} flags=0x{:x}",
        data.hWnd.0 as usize,
        data.uID,
        icon_handle_value(data.hIcon),
        data.uFlags.0
    ));
}

fn rgb(red: u8, green: u8, blue: u8) -> COLORREF {
    COLORREF(u32::from(red) | (u32::from(green) << 8) | (u32::from(blue) << 16))
}

fn coordinates_from_lparam(lparam: LPARAM) -> (i32, i32) {
    ((lparam.0 as i16) as i32, ((lparam.0 >> 16) as i16) as i32)
}

fn tray_event_from_lparam(lparam: LPARAM) -> u32 {
    (lparam.0 as u32) & 0xffff
}

fn tray_event_is_toggle(event: u32) -> bool {
    event == WM_LBUTTONUP
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
    Option<AlertStateStore>,
    AlertTracker,
    bool,
) {
    if demo {
        return (
            AppSettings::default(),
            None,
            None,
            None,
            AlertTracker::new(),
            false,
        );
    }
    let Ok(paths) = PersistencePaths::local_default() else {
        return (
            AppSettings::default(),
            None,
            None,
            None,
            AlertTracker::new(),
            true,
        );
    };
    let settings_store = SettingsStore::new(paths.settings);
    let (mut settings, warning) = match settings_store.load() {
        Ok(settings) => (settings, false),
        Err(_) => (AppSettings::default(), true),
    };
    settings.start_with_windows = start_with_windows_enabled();
    let cache = QuotaCacheStore::new(paths.quota_cache);
    cache.set_enabled(settings.persist_quota_cache);
    let alert_store = AlertStateStore::new(paths.alert_state);
    let (alert_tracker, alert_warning) = match alert_store.load() {
        Ok(Some(state)) => (AlertTracker::from_persisted(state), false),
        Ok(None) => (AlertTracker::new(), false),
        Err(_) => (AlertTracker::new(), true),
    };
    (
        settings,
        Some(settings_store),
        Some(cache),
        Some(alert_store),
        alert_tracker,
        warning || alert_warning,
    )
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
            reset_credits: ResetCreditsState::Unavailable,
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
        percentage_valid: true,
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

fn copy_diagnostics(state: &AppState) -> Result<(), String> {
    let diagnostics = diagnostics_text(state);
    let mut wide = diagnostics.encode_utf16().collect::<Vec<_>>();
    wide.push(0);
    // SAFETY: The movable allocation remains owned locally until SetClipboardData succeeds;
    // after that call Windows owns it. Clipboard access is confined to the UI thread.
    unsafe {
        OpenClipboard(None).map_err(win_error("open clipboard"))?;
        let result = (|| {
            EmptyClipboard().map_err(win_error("empty clipboard"))?;
            let allocation = GlobalAlloc(GMEM_MOVEABLE, wide.len() * size_of::<u16>())
                .map_err(win_error("allocate clipboard text"))?;
            let destination = GlobalLock(allocation).cast::<u16>();
            if destination.is_null() {
                let _ = GlobalFree(Some(allocation));
                return Err("native Windows operation failed: lock clipboard text".to_owned());
            }
            std::ptr::copy_nonoverlapping(wide.as_ptr(), destination, wide.len());
            let _ = GlobalUnlock(allocation);
            if SetClipboardData(13, Some(HANDLE(allocation.0))).is_err() {
                let _ = GlobalFree(Some(allocation));
                return Err("native Windows operation failed: set clipboard text".to_owned());
            }
            Ok(())
        })();
        let close = CloseClipboard().map_err(win_error("close clipboard"));
        result.and(close)
    }
}

fn diagnostics_text(state: &AppState) -> String {
    let initialized = matches!(state.process, crate::state::ProcessState::Ready { .. });
    let (available_count, detail_count) =
        match state.quota.as_ref().map(|quota| &quota.reset_credits) {
            Some(ResetCreditsState::Available {
                available_count,
                detail_count,
                ..
            }) => (available_count.to_string(), detail_count.to_string()),
            Some(ResetCreditsState::Unavailable) | None => {
                ("不可用".to_owned(), "不可用".to_owned())
            }
        };
    format!(
        "CodexQuotaTray: {}\r\nCodex CLI: {}\r\n协议基线: {}\r\nApp Server 已初始化: {}\r\naccount/rateLimits/read 成功: {}\r\nrateLimitResetCredits 字段存在: {}\r\navailableCount: {}\r\ncredits 明细条数: {}\r\n最近刷新 UTC 秒: {}\r\n",
        env!("CARGO_PKG_VERSION"),
        state.source_cli_version.as_deref().unwrap_or("未报告"),
        schema_codex_version(),
        yes_no(initialized),
        yes_no(state.rate_limits_read_succeeded),
        yes_no(state.reset_credits_field_present),
        available_count,
        detail_count,
        state
            .last_success_at
            .map_or_else(|| "无".to_owned(), |value| value.to_string()),
    )
}

fn yes_no(value: bool) -> &'static str {
    if value { "是" } else { "否" }
}

fn should_show_manual_refresh_feedback(elapsed: Duration, runtime_refreshing: bool) -> bool {
    runtime_refreshing || elapsed < MINIMUM_MANUAL_FEEDBACK
}

fn initial_sync_is_complete(state: &AppState, mode: RefreshMode) -> bool {
    state.last_success_at.is_some()
        || state.last_failure.is_some()
        || !matches!(state.auth, AuthState::Unknown)
        || (mode == RefreshMode::ManualOnly && matches!(state.process, ProcessState::Ready { .. }))
}

fn refresh_status_poll_required(
    initial_sync_pending: bool,
    runtime_refreshing: bool,
    manual_feedback: bool,
) -> bool {
    initial_sync_pending || runtime_refreshing || manual_feedback
}

fn win_error(operation: &'static str) -> impl FnOnce(windows::core::Error) -> String {
    move |_| format!("native Windows operation failed: {operation}")
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use super::{
        diagnostics_text, initial_sync_is_complete, point_size_to_pixels,
        refresh_status_poll_required, rgb, severity_color, should_show_manual_refresh_feedback,
        startup_command_matches, status_text_color, tray_event_from_lparam, tray_event_is_toggle,
    };
    use crate::quota::{QuotaSummary, ResetCreditsState};
    use crate::refresh::RefreshMode;
    use crate::state::{AppState, ProcessState};
    use crate::ui_model::StatusTone;
    use std::time::Duration;
    use windows::Win32::Foundation::LPARAM;
    use windows::Win32::UI::Shell::NIN_SELECT;
    use windows::Win32::UI::WindowsAndMessaging::{WM_LBUTTONDOWN, WM_LBUTTONUP};

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

    #[test]
    fn light_theme_state_colors_remain_semantically_distinct() {
        let colors = [
            status_text_color(StatusTone::Success),
            status_text_color(StatusTone::Warning),
            status_text_color(StatusTone::Error),
            status_text_color(StatusTone::Refreshing),
            status_text_color(StatusTone::Neutral),
        ];
        for (index, color) in colors.iter().enumerate() {
            assert!(colors[..index].iter().all(|previous| previous != color));
        }
    }

    #[test]
    fn quota_colors_follow_remaining_percentage_bands() {
        assert_eq!(severity_color(51), rgb(22, 140, 103));
        assert_eq!(severity_color(50), rgb(180, 83, 9));
        assert_eq!(severity_color(20), rgb(180, 83, 9));
        assert_eq!(severity_color(19), rgb(190, 58, 52));
    }

    #[test]
    fn tray_event_uses_low_word_and_only_left_button_up_is_a_toggle() {
        let packed = LPARAM(((0x1234_u32 << 16) | WM_LBUTTONUP) as isize);
        assert_eq!(tray_event_from_lparam(packed), WM_LBUTTONUP);
        assert_eq!(
            tray_event_from_lparam(LPARAM(WM_LBUTTONDOWN as isize)),
            WM_LBUTTONDOWN
        );
        assert_ne!(tray_event_from_lparam(packed), WM_LBUTTONDOWN);
        assert!(tray_event_is_toggle(WM_LBUTTONUP));
        assert!(!tray_event_is_toggle(WM_LBUTTONDOWN));
        assert!(!tray_event_is_toggle(NIN_SELECT));
    }

    #[test]
    fn desired_visibility_flips_once_per_queued_toggle() {
        let mut desired_visible = false;
        desired_visible = !desired_visible;
        assert!(desired_visible);
        desired_visible = !desired_visible;
        assert!(!desired_visible);
    }

    #[test]
    fn diagnostics_are_bounded_and_exclude_reset_credit_identity() {
        let state = AppState {
            source_cli_version: Some("0.144.5".to_owned()),
            rate_limits_read_succeeded: true,
            reset_credits_field_present: true,
            quota: Some(QuotaSummary {
                windows: Vec::new(),
                issues: Vec::new(),
                reset_credits: ResetCreditsState::Available {
                    available_count: 2,
                    detail_count: 1,
                    valid_expirations: vec![1_800_000_000],
                },
                rate_limit_reached: false,
            }),
            ..AppState::default()
        };
        let text = diagnostics_text(&state);
        assert!(text.contains("availableCount: 2"));
        assert!(text.contains("credits 明细条数: 1"));
        for forbidden in ["token", "email", "account ID", "credit ID", "limit ID"] {
            assert!(!text.contains(forbidden));
        }
    }

    #[test]
    fn manual_refresh_feedback_is_immediate_but_not_an_idle_timer() {
        assert!(should_show_manual_refresh_feedback(Duration::ZERO, false));
        assert!(should_show_manual_refresh_feedback(
            Duration::from_secs(8),
            true
        ));
        assert!(!should_show_manual_refresh_feedback(
            Duration::from_millis(301),
            false
        ));
    }

    #[test]
    fn startup_and_runtime_refreshes_poll_only_until_state_is_terminal() {
        assert!(refresh_status_poll_required(true, false, false));
        assert!(refresh_status_poll_required(false, true, false));
        assert!(!refresh_status_poll_required(false, false, false));

        let ready_manual = AppState {
            process: ProcessState::Ready { generation: 0 },
            ..AppState::default()
        };
        assert!(initial_sync_is_complete(
            &ready_manual,
            RefreshMode::ManualOnly
        ));
        assert!(!initial_sync_is_complete(&ready_manual, RefreshMode::Auto));
    }
}
