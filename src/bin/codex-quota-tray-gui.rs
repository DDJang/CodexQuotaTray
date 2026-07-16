#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
fn main() {
    if let Err(message) = codex_quota_tray::windows_tray::initialize_dpi_awareness() {
        show_error(&message);
        return;
    }
    // Debug builds default to deterministic fixture data for safe UI development;
    // release builds default to the real read-only runtime. `--demo` is explicit in either.
    let mut demo = cfg!(debug_assertions);
    let mut codex_bin = None;
    let mut shutdown_existing = false;
    let mut args = std::env::args_os().skip(1);
    while let Some(argument) = args.next() {
        match argument.to_string_lossy().as_ref() {
            "--demo" => demo = true,
            "--shutdown-existing" => shutdown_existing = true,
            "--codex-bin" => {
                codex_bin = args.next();
                if codex_bin.is_none() {
                    show_error("--codex-bin requires a path or command");
                    return;
                }
            }
            _ => {
                show_error(
                    "Unknown argument. Supported: --demo, --codex-bin PATH, --shutdown-existing",
                );
                return;
            }
        }
    }
    if shutdown_existing {
        if codex_quota_tray::windows_tray::request_existing_shutdown().is_err() {
            std::process::exit(1);
        }
        return;
    }
    if let Err(message) =
        codex_quota_tray::windows_tray::run(codex_quota_tray::windows_tray::WindowsTrayOptions {
            demo,
            codex_bin,
        })
    {
        show_error(&message);
    }
}

#[cfg(windows)]
fn show_error(message: &str) {
    use windows::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};
    use windows::core::{PCWSTR, w};

    let wide = message
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: `wide` is NUL-terminated and remains alive through this modal call.
    unsafe {
        let _ = MessageBoxW(
            None,
            PCWSTR(wide.as_ptr()),
            w!("CodexQuotaTray"),
            MB_OK | MB_ICONERROR,
        );
    }
}

#[cfg(not(windows))]
fn main() {
    eprintln!("CodexQuotaTray GUI requires Windows 10 or Windows 11.");
}
