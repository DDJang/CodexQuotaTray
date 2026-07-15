const SCHEMA_VERSION_RECORD: &str = include_str!("../schemas/CODEX_VERSION");

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VersionCompatibility {
    Unknown,
    Match {
        schema_version: String,
        runtime_version: String,
    },
    Mismatch {
        schema_version: String,
        runtime_version: String,
    },
    Unreported {
        schema_version: String,
    },
}

impl VersionCompatibility {
    pub fn runtime_version(&self) -> Option<&str> {
        match self {
            Self::Match {
                runtime_version, ..
            }
            | Self::Mismatch {
                runtime_version, ..
            } => Some(runtime_version),
            Self::Unknown | Self::Unreported { .. } => None,
        }
    }
}

pub fn schema_codex_version() -> &'static str {
    SCHEMA_VERSION_RECORD
        .trim()
        .strip_prefix("codex-cli ")
        .unwrap_or(SCHEMA_VERSION_RECORD.trim())
}

pub fn evaluate_user_agent(user_agent: &str, schema_version: &str) -> VersionCompatibility {
    let schema_version = schema_version.to_owned();
    match extract_version(user_agent) {
        Some(runtime_version) if runtime_version == schema_version => VersionCompatibility::Match {
            schema_version,
            runtime_version,
        },
        Some(runtime_version) => VersionCompatibility::Mismatch {
            schema_version,
            runtime_version,
        },
        None => VersionCompatibility::Unreported { schema_version },
    }
}

fn extract_version(user_agent: &str) -> Option<String> {
    user_agent
        .split(|character: char| {
            !(character.is_ascii_alphanumeric() || matches!(character, '.' | '-' | '+'))
        })
        .find(|candidate| is_version(candidate))
        .map(str::to_owned)
}

fn is_version(candidate: &str) -> bool {
    if !candidate
        .chars()
        .next()
        .is_some_and(|character| character.is_ascii_digit())
    {
        return false;
    }
    let core = candidate
        .split_once(['-', '+'])
        .map_or(candidate, |(core, _)| core);
    let mut components = core.split('.');
    (0..3).all(|_| {
        components
            .next()
            .is_some_and(|part| !part.is_empty() && part.chars().all(|c| c.is_ascii_digit()))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn checked_in_schema_version_is_read_from_the_generated_bundle_record() {
        assert_eq!(schema_codex_version(), "0.137.0");
    }

    #[test]
    fn app_server_user_agent_versions_are_compared_exactly() {
        assert_eq!(
            evaluate_user_agent("codex_app_server_rs/0.137.0 (Windows)", "0.137.0"),
            VersionCompatibility::Match {
                schema_version: "0.137.0".to_owned(),
                runtime_version: "0.137.0".to_owned(),
            }
        );
        assert_eq!(
            evaluate_user_agent("codex_app_server_rs/0.138.0-dev.1", "0.137.0"),
            VersionCompatibility::Mismatch {
                schema_version: "0.137.0".to_owned(),
                runtime_version: "0.138.0-dev.1".to_owned(),
            }
        );
    }

    #[test]
    fn missing_version_is_explicit_and_does_not_store_the_full_user_agent() {
        let result = evaluate_user_agent("custom app server", "0.137.0");
        assert_eq!(
            result,
            VersionCompatibility::Unreported {
                schema_version: "0.137.0".to_owned()
            }
        );
        assert!(!format!("{result:?}").contains("custom app server"));
    }
}
