use std::env;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use serde::{Deserialize, Deserializer, Serialize};

use crate::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use crate::refresh::RefreshMode;
use crate::state::AppState;

const FORMAT_VERSION: u32 = 1;
const MAX_FILE_BYTES: u64 = 64 * 1024;
const MAX_CACHED_WINDOWS: usize = 32;
pub const ALERT_STATE_SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PersistenceError {
    LocationUnavailable,
    Io(std::io::ErrorKind),
    InvalidData,
}

impl fmt::Display for PersistenceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::LocationUnavailable => {
                formatter.write_str("local application data location is unavailable")
            }
            Self::Io(kind) => write!(formatter, "persistence I/O failed: {kind:?}"),
            Self::InvalidData => formatter.write_str("persisted data was invalid or unsupported"),
        }
    }
}

impl std::error::Error for PersistenceError {}

impl From<std::io::Error> for PersistenceError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error.kind())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PersistencePaths {
    pub directory: PathBuf,
    pub settings: PathBuf,
    pub quota_cache: PathBuf,
    pub alert_state: PathBuf,
}

impl PersistencePaths {
    pub fn local_default() -> Result<Self, PersistenceError> {
        let base = env::var_os("LOCALAPPDATA")
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .ok_or(PersistenceError::LocationUnavailable)?;
        Ok(Self::under(base.join("CodexQuotaTray")))
    }

    pub fn under(directory: PathBuf) -> Self {
        Self {
            settings: directory.join("settings.json"),
            quota_cache: directory.join("quota-cache.json"),
            alert_state: directory.join("alert-state.json"),
            directory,
        }
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AppSettings {
    pub start_with_windows: bool,
    pub show_remaining_percent: bool,
    pub use_24_hour_time: bool,
    pub persist_quota_cache: bool,
    pub refresh_mode: RefreshMode,
    pub refresh_on_network_restore: bool,
    pub notifications: NotificationSettings,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            start_with_windows: false,
            show_remaining_percent: true,
            use_24_hour_time: true,
            persist_quota_cache: true,
            refresh_mode: RefreshMode::Auto,
            refresh_on_network_restore: true,
            notifications: NotificationSettings::default(),
        }
    }
}

impl AppSettings {
    pub fn validate(&self) -> Result<(), PersistenceError> {
        Ok(())
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct NotificationSettings {
    pub remaining_50_percent: bool,
    pub remaining_20_percent: bool,
    pub remaining_10_percent: bool,
}

impl Default for NotificationSettings {
    fn default() -> Self {
        Self {
            remaining_50_percent: false,
            remaining_20_percent: true,
            remaining_10_percent: true,
        }
    }
}

#[derive(Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct AppSettingsWire {
    start_with_windows: bool,
    show_remaining_percent: Option<bool>,
    use_24_hour_time: Option<bool>,
    persist_quota_cache: Option<bool>,
    refresh_mode: Option<RefreshMode>,
    fallback_refresh_minutes: Option<u16>,
    refresh_on_network_restore: Option<bool>,
    notifications: Option<NotificationSettings>,
}

impl<'de> Deserialize<'de> for AppSettings {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let wire = AppSettingsWire::deserialize(deserializer)?;
        if wire
            .fallback_refresh_minutes
            .is_some_and(|minutes| !(1..=60).contains(&minutes))
        {
            return Err(serde::de::Error::custom(
                "legacy refresh interval was outside the supported range",
            ));
        }
        let defaults = Self::default();
        let refresh_mode = wire
            .refresh_mode
            .unwrap_or(match wire.fallback_refresh_minutes {
                Some(5) => RefreshMode::Every5Minutes,
                Some(15) => RefreshMode::Every15Minutes,
                Some(30) => RefreshMode::Every30Minutes,
                _ => RefreshMode::Auto,
            });
        Ok(Self {
            start_with_windows: wire.start_with_windows,
            show_remaining_percent: wire
                .show_remaining_percent
                .unwrap_or(defaults.show_remaining_percent),
            use_24_hour_time: wire.use_24_hour_time.unwrap_or(defaults.use_24_hour_time),
            persist_quota_cache: wire
                .persist_quota_cache
                .unwrap_or(defaults.persist_quota_cache),
            refresh_mode,
            refresh_on_network_restore: wire
                .refresh_on_network_restore
                .unwrap_or(defaults.refresh_on_network_restore),
            notifications: wire.notifications.unwrap_or_default(),
        })
    }
}

#[derive(Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct NotificationSettingsWire {
    remaining_50_percent: Option<bool>,
    remaining_20_percent: Option<bool>,
    remaining_10_percent: Option<bool>,
    remaining_5_percent: Option<bool>,
    exhausted: Option<bool>,
    recovered: Option<bool>,
}

impl<'de> Deserialize<'de> for NotificationSettings {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let wire = NotificationSettingsWire::deserialize(deserializer)?;
        let legacy = wire.remaining_5_percent.is_some()
            || wire.exhausted.is_some()
            || wire.recovered.is_some();
        let remaining_20_percent = wire.remaining_20_percent.unwrap_or(true);
        let remaining_10_percent = wire.remaining_10_percent.unwrap_or_else(|| {
            if legacy {
                wire.remaining_5_percent.unwrap_or(false) || wire.exhausted.unwrap_or(false)
            } else {
                true
            }
        });
        Ok(Self {
            remaining_50_percent: wire.remaining_50_percent.unwrap_or(false),
            remaining_20_percent,
            remaining_10_percent,
        })
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PersistedAlertState {
    pub schema_version: u32,
    pub baseline_thresholds: Vec<u8>,
    pub windows: Vec<PersistedAlertWindow>,
}

impl Default for PersistedAlertState {
    fn default() -> Self {
        Self {
            schema_version: ALERT_STATE_SCHEMA_VERSION,
            baseline_thresholds: Vec::new(),
            windows: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PersistedAlertWindow {
    pub pseudonymous_key: String,
    pub window_duration_mins: Option<i64>,
    pub resets_at_utc: Option<i64>,
    pub last_remaining_percent: i64,
    pub handled_thresholds: Vec<u8>,
}

#[derive(Debug, Clone)]
pub struct AlertStateStore {
    path: PathBuf,
}

impl AlertStateStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn load(&self) -> Result<Option<PersistedAlertState>, PersistenceError> {
        let state = read_json::<PersistedAlertState>(&self.path)?;
        if let Some(state) = state.as_ref()
            && (state.schema_version != ALERT_STATE_SCHEMA_VERSION
                || state
                    .baseline_thresholds
                    .iter()
                    .any(|threshold| !matches!(threshold, 10 | 20 | 50))
                || state.windows.len() > MAX_CACHED_WINDOWS
                || state.windows.iter().any(|window| {
                    window.pseudonymous_key.is_empty()
                        || window.pseudonymous_key.len() > 160
                        || !(0..=100).contains(&window.last_remaining_percent)
                        || window
                            .handled_thresholds
                            .iter()
                            .any(|threshold| !matches!(threshold, 10 | 20 | 50))
                }))
        {
            return Err(PersistenceError::InvalidData);
        }
        Ok(state)
    }

    pub fn save(&self, state: &PersistedAlertState) -> Result<(), PersistenceError> {
        if state.schema_version != ALERT_STATE_SCHEMA_VERSION
            || state.windows.len() > MAX_CACHED_WINDOWS
        {
            return Err(PersistenceError::InvalidData);
        }
        write_json(&self.path, state)
    }

    pub fn clear(&self) -> Result<(), PersistenceError> {
        remove_if_exists(&self.path)?;
        remove_if_exists(&backup_path(&self.path))?;
        remove_if_exists(&temporary_path(&self.path))
    }
}

#[derive(Debug, Clone)]
pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn load(&self) -> Result<AppSettings, PersistenceError> {
        let Some(settings) = read_json::<AppSettings>(&self.path)? else {
            return Ok(AppSettings::default());
        };
        settings.validate()?;
        Ok(settings)
    }

    pub fn save(&self, settings: &AppSettings) -> Result<(), PersistenceError> {
        settings.validate()?;
        write_json(&self.path, settings)
    }
}

#[derive(Debug, Clone)]
pub struct QuotaCacheStore {
    path: PathBuf,
    enabled: Arc<AtomicBool>,
}

impl QuotaCacheStore {
    pub fn new(path: PathBuf) -> Self {
        Self {
            path,
            enabled: Arc::new(AtomicBool::new(true)),
        }
    }

    pub fn set_enabled(&self, enabled: bool) {
        self.enabled.store(enabled, Ordering::Release);
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled.load(Ordering::Acquire)
    }

    pub fn load(&self) -> Result<Option<RestoredQuota>, PersistenceError> {
        if !self.is_enabled() {
            return Ok(None);
        }
        let Some(cache) = read_json::<CacheFile>(&self.path)? else {
            return Ok(None);
        };
        cache.restore().map(Some)
    }

    pub fn save(&self, state: &AppState) -> Result<bool, PersistenceError> {
        if !self.is_enabled() {
            return Ok(false);
        }
        let Some(cache) = CacheFile::from_state(state) else {
            return Ok(false);
        };
        write_json(&self.path, &cache)?;
        Ok(true)
    }

    pub fn clear(&self) -> Result<(), PersistenceError> {
        remove_if_exists(&self.path)?;
        remove_if_exists(&backup_path(&self.path))?;
        remove_if_exists(&temporary_path(&self.path))?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RestoredQuota {
    pub summary: QuotaSummary,
    pub last_success_at: i64,
    pub source_cli_version: Option<String>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CacheFile {
    format_version: u32,
    saved_at: i64,
    last_success_at: i64,
    source_cli_version: Option<String>,
    windows: Vec<CachedWindow>,
}

impl CacheFile {
    fn from_state(state: &AppState) -> Option<Self> {
        let quota = state.quota.as_ref()?;
        let last_success_at = state.last_success_at?;
        if quota.windows.is_empty() || quota.windows.len() > MAX_CACHED_WINDOWS {
            return None;
        }
        Some(Self {
            format_version: FORMAT_VERSION,
            saved_at: unix_now(),
            last_success_at,
            source_cli_version: state.source_cli_version.clone(),
            windows: quota.windows.iter().map(CachedWindow::from).collect(),
        })
    }

    fn restore(self) -> Result<RestoredQuota, PersistenceError> {
        if self.format_version != FORMAT_VERSION
            || self.saved_at <= 0
            || self.last_success_at <= 0
            || self.windows.is_empty()
            || self.windows.len() > MAX_CACHED_WINDOWS
        {
            return Err(PersistenceError::InvalidData);
        }
        let windows = self
            .windows
            .into_iter()
            .map(CachedWindow::restore)
            .collect::<Result<Vec<_>, _>>()?;
        Ok(RestoredQuota {
            summary: QuotaSummary {
                windows,
                issues: Vec::new(),
                reset_credits: ResetCreditsState::Unavailable,
                rate_limit_reached: false,
            },
            last_success_at: self.last_success_at,
            source_cli_version: self.source_cli_version,
        })
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CachedWindow {
    source_slot: CachedSourceSlot,
    used_percent: i64,
    window_duration_mins: Option<i64>,
    resets_at: Option<i64>,
}

impl From<&QuotaWindow> for CachedWindow {
    fn from(window: &QuotaWindow) -> Self {
        Self {
            source_slot: if window.source_slot == "secondary" {
                CachedSourceSlot::Secondary
            } else {
                CachedSourceSlot::Primary
            },
            used_percent: window.used_percent,
            window_duration_mins: window.window_duration_mins,
            resets_at: window.resets_at,
        }
    }
}

impl CachedWindow {
    fn restore(self) -> Result<QuotaWindow, PersistenceError> {
        if !(0..=100).contains(&self.used_percent)
            || self.window_duration_mins.is_some_and(|value| value <= 0)
            || self.resets_at.is_some_and(|value| value <= 0)
        {
            return Err(PersistenceError::InvalidData);
        }
        Ok(QuotaWindow {
            limit_id: None,
            limit_name: None,
            source_slot: match self.source_slot {
                CachedSourceSlot::Primary => "primary",
                CachedSourceSlot::Secondary => "secondary",
            },
            used_percent: self.used_percent,
            remaining_percent: 100 - self.used_percent,
            percentage_valid: true,
            window_duration_mins: self.window_duration_mins,
            resets_at: self.resets_at,
        })
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
enum CachedSourceSlot {
    Primary,
    Secondary,
}

fn read_json<T: for<'de> Deserialize<'de>>(path: &Path) -> Result<Option<T>, PersistenceError> {
    let Some(bytes) = read_bounded(path)? else {
        let backup = backup_path(path);
        return read_bounded(&backup)?.map(parse_json).transpose();
    };
    parse_json(bytes).map(Some).or_else(|_| {
        let backup = backup_path(path);
        read_bounded(&backup)?
            .map(parse_json)
            .transpose()?
            .ok_or(PersistenceError::InvalidData)
            .map(Some)
    })
}

fn read_bounded(path: &Path) -> Result<Option<Vec<u8>>, PersistenceError> {
    let file = match File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    };
    if file.metadata()?.len() > MAX_FILE_BYTES {
        return Err(PersistenceError::InvalidData);
    }
    let mut bytes = Vec::new();
    file.take(MAX_FILE_BYTES + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_FILE_BYTES {
        return Err(PersistenceError::InvalidData);
    }
    Ok(Some(bytes))
}

fn parse_json<T: for<'de> Deserialize<'de>>(bytes: Vec<u8>) -> Result<T, PersistenceError> {
    serde_json::from_slice(&bytes).map_err(|_| PersistenceError::InvalidData)
}

fn write_json<T: Serialize>(path: &Path, value: &T) -> Result<(), PersistenceError> {
    let bytes = serde_json::to_vec_pretty(value).map_err(|_| PersistenceError::InvalidData)?;
    if bytes.len() as u64 > MAX_FILE_BYTES {
        return Err(PersistenceError::InvalidData);
    }
    let parent = path.parent().ok_or(PersistenceError::LocationUnavailable)?;
    fs::create_dir_all(parent)?;
    let temporary = temporary_path(path);
    remove_if_exists(&temporary)?;
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temporary)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    drop(file);

    let backup = backup_path(path);
    remove_if_exists(&backup)?;
    let had_original = path.exists();
    if had_original {
        fs::rename(path, &backup)?;
    }
    if let Err(error) = fs::rename(&temporary, path) {
        if had_original {
            let _ = fs::rename(&backup, path);
        }
        let _ = fs::remove_file(&temporary);
        return Err(error.into());
    }
    if had_original {
        remove_if_exists(&backup)?;
    }
    Ok(())
}

fn remove_if_exists(path: &Path) -> Result<(), PersistenceError> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn backup_path(path: &Path) -> PathBuf {
    path.with_extension("json.bak")
}

fn temporary_path(path: &Path) -> PathBuf {
    path.with_extension("json.tmp")
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_secs().min(i64::MAX as u64) as i64)
        .unwrap_or(0)
}
