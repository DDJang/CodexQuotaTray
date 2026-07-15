use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process;
use std::time::{SystemTime, UNIX_EPOCH};

use codex_quota_tray::persistence::{
    AppSettings, PersistenceError, PersistencePaths, QuotaCacheStore, SettingsStore,
};
use codex_quota_tray::protocol::RateLimitsReadResponse;
use codex_quota_tray::quota::AccountState;
use codex_quota_tray::state::{AppState, AppStateReducer, DataState, StateEvent};

#[test]
fn settings_default_and_round_trip_are_bounded_and_forward_compatible() {
    let fixture = PersistenceFixture::new("settings");
    let store = SettingsStore::new(fixture.paths.settings.clone());
    assert_eq!(store.load().unwrap(), AppSettings::default());

    let mut settings = AppSettings {
        start_with_windows: true,
        fallback_refresh_minutes: 15,
        ..AppSettings::default()
    };
    settings.notifications.recovered = false;
    store.save(&settings).unwrap();
    assert_eq!(store.load().unwrap(), settings);

    let mut json: serde_json::Value =
        serde_json::from_str(&fs::read_to_string(&fixture.paths.settings).unwrap()).unwrap();
    json["futureSetting"] = serde_json::json!(true);
    fs::write(
        &fixture.paths.settings,
        serde_json::to_vec_pretty(&json).unwrap(),
    )
    .unwrap();
    assert_eq!(store.load().unwrap(), settings);
}

#[test]
fn invalid_settings_are_rejected_without_exposing_file_contents() {
    let fixture = PersistenceFixture::new("invalid-settings");
    fs::create_dir_all(&fixture.paths.directory).unwrap();
    fs::write(
        &fixture.paths.settings,
        br#"{"fallbackRefreshMinutes":0,"secret":"do-not-echo"}"#,
    )
    .unwrap();
    let error = SettingsStore::new(fixture.paths.settings.clone())
        .load()
        .unwrap_err();
    assert_eq!(error, PersistenceError::InvalidData);
    assert!(!error.to_string().contains("do-not-echo"));
    assert!(
        !error
            .to_string()
            .contains(&fixture.paths.directory.to_string_lossy().to_string())
    );
}

#[test]
fn quota_cache_excludes_identity_metadata_and_restores_only_safe_fields() {
    let fixture = PersistenceFixture::new("quota");
    let store = QuotaCacheStore::new(fixture.paths.quota_cache.clone());
    let state = quota_state(28, 1_700_000_000);
    assert!(store.save(&state).unwrap());

    let persisted = fs::read_to_string(&fixture.paths.quota_cache).unwrap();
    for forbidden in [
        "limitId",
        "limitName",
        "planType",
        "account",
        "email",
        "token",
        "codexHome",
        "raw",
    ] {
        assert!(
            !persisted
                .to_ascii_lowercase()
                .contains(&forbidden.to_ascii_lowercase()),
            "cache unexpectedly contained {forbidden}"
        );
    }

    let restored = store.load().unwrap().unwrap();
    assert_eq!(restored.last_success_at, 1_700_000_000);
    assert_eq!(restored.source_cli_version.as_deref(), Some("0.137.0"));
    assert_eq!(restored.summary.windows.len(), 1);
    let window = &restored.summary.windows[0];
    assert_eq!(window.used_percent, 28);
    assert_eq!(window.remaining_percent, 72);
    assert_eq!(window.window_duration_mins, Some(10_080));
    assert_eq!(window.limit_id, None);
    assert_eq!(window.limit_name, None);

    let mut reducer = AppStateReducer::new();
    reducer.reduce(StateEvent::CachedQuotaRestored {
        summary: restored.summary,
        last_success_at: restored.last_success_at,
        source_cli_version: restored.source_cli_version,
    });
    assert_eq!(reducer.state().data, DataState::Stale);
    assert_eq!(
        reducer.state().quota.as_ref().unwrap().windows[0].used_percent,
        28
    );
}

#[test]
fn atomic_replacement_keeps_the_latest_complete_cache() {
    let fixture = PersistenceFixture::new("replace");
    let store = QuotaCacheStore::new(fixture.paths.quota_cache.clone());
    store.save(&quota_state(28, 1_700_000_000)).unwrap();
    store.save(&quota_state(40, 1_700_000_100)).unwrap();

    let restored = store.load().unwrap().unwrap();
    assert_eq!(restored.last_success_at, 1_700_000_100);
    assert_eq!(restored.summary.windows[0].used_percent, 40);
    assert!(!backup_path(&fixture.paths.quota_cache).exists());
}

#[test]
fn corrupt_primary_can_recover_from_a_complete_backup() {
    let fixture = PersistenceFixture::new("backup");
    let store = QuotaCacheStore::new(fixture.paths.quota_cache.clone());
    store.save(&quota_state(33, 1_700_000_000)).unwrap();
    fs::rename(
        &fixture.paths.quota_cache,
        backup_path(&fixture.paths.quota_cache),
    )
    .unwrap();
    fs::write(&fixture.paths.quota_cache, b"not json").unwrap();

    let restored = store.load().unwrap().unwrap();
    assert_eq!(restored.summary.windows[0].used_percent, 33);
}

#[test]
fn malformed_or_oversized_cache_fails_safely_and_clear_is_idempotent() {
    let fixture = PersistenceFixture::new("malformed");
    let store = QuotaCacheStore::new(fixture.paths.quota_cache.clone());
    fs::create_dir_all(&fixture.paths.directory).unwrap();
    fs::write(&fixture.paths.quota_cache, b"secret raw response").unwrap();
    let error = store.load().unwrap_err();
    assert_eq!(error, PersistenceError::InvalidData);
    assert!(!error.to_string().contains("secret raw response"));

    fs::write(&fixture.paths.quota_cache, vec![b'x'; 65 * 1024]).unwrap();
    assert_eq!(store.load().unwrap_err(), PersistenceError::InvalidData);
    store.clear().unwrap();
    store.clear().unwrap();
    assert!(store.load().unwrap().is_none());
}

#[test]
fn disabled_quota_cache_neither_reads_nor_writes_until_reenabled() {
    let fixture = PersistenceFixture::new("disabled-cache");
    let store = QuotaCacheStore::new(fixture.paths.quota_cache.clone());
    store.set_enabled(false);

    assert!(!store.save(&quota_state(28, 1_700_000_000)).unwrap());
    assert!(!fixture.paths.quota_cache.exists());
    assert!(store.load().unwrap().is_none());

    store.set_enabled(true);
    assert!(store.save(&quota_state(40, 1_700_000_100)).unwrap());
    assert_eq!(
        store.load().unwrap().unwrap().summary.windows[0].used_percent,
        40
    );
}

fn quota_state(used_percent: i64, received_at: i64) -> AppState {
    let response: RateLimitsReadResponse = serde_json::from_value(serde_json::json!({
        "rateLimits": {
            "limitId": "identity-like-limit-id-must-not-persist",
            "limitName": "Private workspace name must not persist",
            "planType": "plus",
            "primary": {
                "usedPercent": used_percent,
                "windowDurationMins": 10_080,
                "resetsAt": 1_800_000_000
            }
        }
    }))
    .unwrap();
    let mut reducer = AppStateReducer::new();
    reducer.reduce(StateEvent::AccountUpdated(AccountState::ChatGpt {
        plan_type: Some("plus".to_owned()),
    }));
    reducer.reduce(StateEvent::RateLimitsReplaced {
        response,
        received_at,
        source_cli_version: Some("0.137.0".to_owned()),
    });
    reducer.state().clone()
}

fn backup_path(path: &Path) -> PathBuf {
    path.with_extension("json.bak")
}

struct PersistenceFixture {
    paths: PersistencePaths,
}

impl PersistenceFixture {
    fn new(name: &str) -> Self {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let directory = env::temp_dir().join(format!(
            "codex-quota-persistence-{name}-{}-{unique}",
            process::id()
        ));
        Self {
            paths: PersistencePaths::under(directory),
        }
    }
}

impl Drop for PersistenceFixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.paths.directory);
    }
}
