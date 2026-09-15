use std::io::{self, Read};

use ludusavi::{
    api::{parameters, Ludusavi},
    path::StrictPath,
    prelude::Finality,
    report::ApiGame,
    resource::{
        config::{Config, Root},
        manifest::{Manifest, Store},
        ResourceFile,
    },
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const PROTOCOL_VERSION: u32 = 1;
const ENGINE_VERSION: &str = env!("CARGO_PKG_VERSION");
const LUDUSAVI_VERSION: &str = "0.31.0";
const LUDUSAVI_REVISION: &str = "8844d7b67e784909f4ef42f7bfb047b700fe7b15";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RequestEnvelope {
    protocol_version: u32,
    request_id: String,
    operation: String,
    #[serde(default)]
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ResponseEnvelope<T: Serialize> {
    protocol_version: u32,
    request_id: String,
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    result: Option<T>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<ProtocolError>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProtocolError {
    code: &'static str,
    message: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreviewRequest {
    manifest_path: String,
    game_name: String,
    roots: Vec<PreviewRoot>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreviewGameRequest {
    manifest_path: String,
    identity: GameIdentity,
    roots: Vec<PreviewRoot>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GameIdentity {
    store: String,
    external_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PreviewRoot {
    path: String,
    store: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PreviewResult {
    game_name: String,
    file_count: usize,
    total_bytes: u64,
    registry_key_count: usize,
    files: Vec<PreviewFile>,
    registry_keys: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct PreviewFile {
    path: String,
    bytes: u64,
    ignored: bool,
    failed: bool,
}

fn main() {
    let response = match read_request().and_then(handle_request) {
        Ok(response) => response,
        Err((request_id, error)) => failure(request_id, error.code, error.message),
    };

    match serde_json::to_string(&response) {
        Ok(json) => println!("{json}"),
        Err(error) => println!(
            "{{\"protocolVersion\":{PROTOCOL_VERSION},\"requestId\":\"\",\"ok\":false,\"error\":{{\"code\":\"EngineFailure\",\"message\":{}}}}}",
            serde_json::to_string(&format!("Failed to serialize response: {error}")).unwrap_or_else(|_| "\"serialization failure\"".to_string())
        ),
    }
}

fn read_request() -> Result<RequestEnvelope, (String, ProtocolError)> {
    let mut input = String::new();
    io::stdin()
        .take(1024 * 1024)
        .read_to_string(&mut input)
        .map_err(|error| {
            (
                String::new(),
                error_of("EngineFailure", format!("Failed to read request: {error}")),
            )
        })?;

    serde_json::from_str(&input).map_err(|error| {
        (
            String::new(),
            error_of("InvalidRequest", format!("Invalid JSON request: {error}")),
        )
    })
}

fn handle_request(request: RequestEnvelope) -> Result<Value, (String, ProtocolError)> {
    if request.protocol_version != PROTOCOL_VERSION {
        return Err((
            request.request_id,
            error_of(
                "ProtocolMismatch",
                format!("Unsupported protocol version {}", request.protocol_version),
            ),
        ));
    }

    let request_id = request.request_id.clone();
    let result = match request.operation.as_str() {
        "getCapabilities" => Ok(json!({
            "engineVersion": ENGINE_VERSION,
            "ludusaviVersion": LUDUSAVI_VERSION,
            "ludusaviRevision": LUDUSAVI_REVISION,
            "protocolVersion": PROTOCOL_VERSION,
            "operations": ["getCapabilities", "previewSaveData", "previewGameSaveData"]
        })),
        "previewSaveData" => {
            preview_save_data(request.payload).map(|result| serde_json::to_value(result).unwrap())
        }
        "previewGameSaveData" => preview_game_save_data(request.payload)
            .map(|result| serde_json::to_value(result).unwrap()),
        _ => Err(error_of(
            "UnsupportedOperation",
            format!("Unsupported operation: {}", request.operation),
        )),
    };

    match result {
        Ok(result) => Ok(serde_json::to_value(success(request_id, result)).unwrap()),
        Err(error) => {
            Ok(serde_json::to_value(failure(request_id, error.code, error.message)).unwrap())
        }
    }
}

fn preview_save_data(payload: Value) -> Result<PreviewResult, ProtocolError> {
    let request: PreviewRequest = serde_json::from_value(payload).map_err(|error| {
        error_of(
            "InvalidRequest",
            format!("Invalid preview payload: {error}"),
        )
    })?;

    if request.game_name.trim().is_empty() {
        return Err(error_of(
            "InvalidRequest",
            "gameName cannot be empty".to_string(),
        ));
    }
    if request.roots.is_empty() {
        return Err(error_of(
            "InvalidRequest",
            "At least one root is required".to_string(),
        ));
    }

    let manifest_path = StrictPath::new(request.manifest_path.clone());
    let manifest = Manifest::load_from_existing(&manifest_path)
        .map_err(|error| error_of("EngineFailure", format!("Unable to load manifest: {error}")))?;

    preview_loaded_manifest(manifest, request.game_name, request.roots)
}

fn preview_game_save_data(payload: Value) -> Result<PreviewResult, ProtocolError> {
    let request: PreviewGameRequest = serde_json::from_value(payload).map_err(|error| {
        error_of(
            "InvalidRequest",
            format!("Invalid game preview payload: {error}"),
        )
    })?;

    if request.roots.is_empty() {
        return Err(error_of(
            "InvalidRequest",
            "At least one root is required".to_string(),
        ));
    }

    let manifest_path = StrictPath::new(request.manifest_path.clone());
    let manifest = Manifest::load_from_existing(&manifest_path)
        .map_err(|error| error_of("EngineFailure", format!("Unable to load manifest: {error}")))?;
    let game_name = resolve_game_identity(&manifest, &request.identity)?;

    preview_loaded_manifest(manifest, game_name, request.roots)
}

fn resolve_game_identity(
    manifest: &Manifest,
    identity: &GameIdentity,
) -> Result<String, ProtocolError> {
    let mut matches = Vec::new();
    match identity.store.trim().to_ascii_lowercase().as_str() {
        "steam" => {
            let app_id = identity.external_id.trim().parse::<u32>().map_err(|_| {
                error_of(
                    "InvalidRequest",
                    "Steam externalId must be an unsigned integer".to_string(),
                )
            })?;
            for (name, game) in &manifest.0 {
                if game.steam.id == Some(app_id) || game.id.steam_extra.contains(&app_id) {
                    matches.push(name.clone());
                }
            }
        }
        "gog" => {
            let game_id = identity.external_id.trim().parse::<u64>().map_err(|_| {
                error_of(
                    "InvalidRequest",
                    "GOG externalId must be an unsigned integer".to_string(),
                )
            })?;
            for (name, game) in &manifest.0 {
                if game.gog.id == Some(game_id) || game.id.gog_extra.contains(&game_id) {
                    matches.push(name.clone());
                }
            }
        }
        store => {
            return Err(error_of(
                "UnsupportedGame",
                format!("Stable manifest identity mapping is not supported for store: {store}"),
            ))
        }
    }

    match matches.as_slice() {
        [] => Err(error_of(
            "UnsupportedGame",
            format!(
                "No manifest game matches {} identity {}",
                identity.store.trim(),
                identity.external_id.trim()
            ),
        )),
        [name] => Ok(name.clone()),
        _ => Err(error_of(
            "AmbiguousGame",
            format!(
                "Multiple manifest games match {} identity {}: {}",
                identity.store.trim(),
                identity.external_id.trim(),
                matches.join(", ")
            ),
        )),
    }
}

fn preview_loaded_manifest(
    manifest: Manifest,
    game_name: String,
    roots: Vec<PreviewRoot>,
) -> Result<PreviewResult, ProtocolError> {
    let mut config = Config::default();
    config.release.check = false;
    config.cloud.synchronize = false;
    config.roots = roots
        .into_iter()
        .map(parse_root)
        .collect::<Result<Vec<_>, _>>()?;

    let preview_target = std::env::temp_dir().join("GameHours-SaveEngine-preview-never-written");
    config.backup.path = StrictPath::new(preview_target.to_string_lossy().into_owned());

    let mut engine = Ludusavi::new(config, manifest);
    let output = engine
        .back_up(parameters::BackUp {
            games: vec![game_name.clone()],
            finality: Finality::Preview,
            resolve_cloud_conflict: None,
            wine_prefix: None,
            include_disabled: true,
            skip_downgrade: false,
        })
        .map_err(|error| {
            error_of(
                "EngineFailure",
                format!("Ludusavi preview failed: {error:?}"),
            )
        })?;

    if output
        .errors
        .as_ref()
        .and_then(|errors| errors.unknown_games.as_ref())
        .is_some_and(|games| !games.is_empty())
    {
        return Err(error_of(
            "UnsupportedGame",
            format!("Game is not present in the manifest: {game_name}"),
        ));
    }

    let Some(game) = output.games.into_iter().next().map(|(_, game)| game) else {
        return Err(error_of(
            "NoSaveData",
            "No save data was detected".to_string(),
        ));
    };

    let ApiGame::Operative {
        files, registry, ..
    } = game
    else {
        return Err(error_of(
            "EngineFailure",
            "Unexpected Ludusavi preview response".to_string(),
        ));
    };

    let mut preview_files = Vec::with_capacity(files.len());
    let mut total_bytes = 0_u64;
    for (path, file) in files {
        total_bytes = total_bytes.saturating_add(file.bytes);
        preview_files.push(PreviewFile {
            path,
            bytes: file.bytes,
            ignored: file.ignored,
            failed: file.failed,
        });
    }

    let registry_keys = registry.into_keys().collect::<Vec<_>>();
    if preview_files.is_empty() && registry_keys.is_empty() {
        return Err(error_of(
            "NoSaveData",
            "No save data was detected".to_string(),
        ));
    }

    Ok(PreviewResult {
        game_name,
        file_count: preview_files.len(),
        total_bytes,
        registry_key_count: registry_keys.len(),
        files: preview_files,
        registry_keys,
    })
}

fn parse_root(root: PreviewRoot) -> Result<Root, ProtocolError> {
    let store = match root.store.to_ascii_lowercase().as_str() {
        "steam" => Store::Steam,
        "gog" => Store::Gog,
        "goggalaxy" => Store::GogGalaxy,
        "epic" => Store::Epic,
        "heroic" => Store::Heroic,
        "microsoft" => Store::Microsoft,
        "origin" => Store::Origin,
        "ea" => Store::Ea,
        "uplay" => Store::Uplay,
        "otherwindows" | "other" => Store::OtherWindows,
        value => {
            return Err(error_of(
                "InvalidRequest",
                format!("Unsupported root store: {value}"),
            ))
        }
    };
    Ok(Root::new(root.path, store))
}

fn success<T: Serialize>(request_id: String, result: T) -> ResponseEnvelope<T> {
    ResponseEnvelope {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        ok: true,
        result: Some(result),
        error: None,
    }
}

fn failure(request_id: String, code: &'static str, message: String) -> Value {
    serde_json::to_value(ResponseEnvelope::<Value> {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        ok: false,
        result: None,
        error: Some(ProtocolError { code, message }),
    })
    .unwrap()
}

fn error_of(code: &'static str, message: String) -> ProtocolError {
    ProtocolError { code, message }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        fs,
        time::{SystemTime, UNIX_EPOCH},
    };

    #[test]
    fn capabilities_report_pinned_upstream_and_protocol() {
        let response = handle_request(RequestEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: "capabilities".to_string(),
            operation: "getCapabilities".to_string(),
            payload: json!({}),
        })
        .expect("capabilities response");

        assert_eq!(true, response["ok"]);
        assert_eq!(PROTOCOL_VERSION, response["protocolVersion"]);
        assert_eq!(LUDUSAVI_VERSION, response["result"]["ludusaviVersion"]);
        assert_eq!(LUDUSAVI_REVISION, response["result"]["ludusaviRevision"]);
    }

    #[test]
    fn rejects_unsupported_protocol_version() {
        let (request_id, error) = handle_request(RequestEnvelope {
            protocol_version: 99,
            request_id: "bad-version".to_string(),
            operation: "getCapabilities".to_string(),
            payload: json!({}),
        })
        .expect_err("protocol mismatch");

        assert_eq!("bad-version", request_id);
        assert_eq!("ProtocolMismatch", error.code);
    }

    #[test]
    fn preview_uses_ludusavi_scanner_without_creating_backup() {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let fixture = std::env::temp_dir().join(format!(
            "gamehours-saveengine-test-{}-{unique}",
            std::process::id()
        ));
        let root = fixture.join("root");
        let game = root.join("fixture-game");
        let manifest_path = fixture.join("manifest.yaml");
        fs::create_dir_all(&game).unwrap();
        fs::write(game.join("save.dat"), b"1234567890").unwrap();
        fs::write(
            &manifest_path,
            "Fixture Game:\n  files:\n    <root>/<game>/save.dat: {}\n  installDir:\n    fixture-game: {}\n",
        )
        .unwrap();

        let result = preview_save_data(json!({
            "manifestPath": manifest_path.to_string_lossy(),
            "gameName": "Fixture Game",
            "roots": [{ "path": root.to_string_lossy(), "store": "otherWindows" }]
        }))
        .expect("preview result");

        assert_eq!(1, result.file_count);
        assert_eq!(10, result.total_bytes);
        assert_eq!(0, result.registry_key_count);
        assert!(result.files[0]
            .path
            .replace('\\', "/")
            .ends_with("/fixture-game/save.dat"));
        assert!(!std::env::temp_dir()
            .join("GameHours-SaveEngine-preview-never-written")
            .exists());

        let _ = fs::remove_dir_all(fixture);
    }

    #[test]
    fn preview_game_resolves_exact_steam_identity_before_scanning() {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let fixture = std::env::temp_dir().join(format!(
            "gamehours-saveengine-identity-test-{}-{unique}",
            std::process::id()
        ));
        let root = fixture.join("steam-library");
        let game = root.join("steamapps").join("common").join("fixture-game");
        let manifest_path = fixture.join("manifest.yaml");
        fs::create_dir_all(&game).unwrap();
        fs::write(game.join("save.dat"), b"steam-save").unwrap();
        fs::write(
            &manifest_path,
            "Fixture Game:\n  files:\n    <base>/save.dat: {}\n  installDir:\n    fixture-game: {}\n  steam:\n    id: 12345\n",
        )
        .unwrap();

        let result = preview_game_save_data(json!({
            "manifestPath": manifest_path.to_string_lossy(),
            "identity": { "store": "steam", "externalId": "12345" },
            "roots": [{ "path": root.to_string_lossy(), "store": "steam" }]
        }))
        .expect("identity preview result");

        assert_eq!("Fixture Game", result.game_name);
        assert_eq!(1, result.file_count);
        assert_eq!(10, result.total_bytes);

        let _ = fs::remove_dir_all(fixture);
    }

    #[test]
    fn identity_mapping_rejects_ambiguous_store_id() {
        let manifest = Manifest::load_from_string(
            "Game A:\n  steam:\n    id: 42\nGame B:\n  id:\n    steamExtra: [42]\n",
        )
        .expect("manifest");

        let error = resolve_game_identity(
            &manifest,
            &GameIdentity {
                store: "steam".to_string(),
                external_id: "42".to_string(),
            },
        )
        .expect_err("ambiguous identity");

        assert_eq!("AmbiguousGame", error.code);
    }
}
