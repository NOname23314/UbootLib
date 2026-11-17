# Changelog

## [0.4.0] — 2026-11-17

### Added
- Content pipeline (`UbootLib.Assets`):
  - `LangPacks` validates `Mods/*/lang/*.lang` and installs them into
    `Wolfpack_Data\Languages` before the Setup scene loads languages.
  - `Missions` parses/validates the game mission format, reports invalid or
    incomplete missions, and can copy a mission to the clipboard for the editor's
    "Import mission from clipboard".
- `MissionDoc` round-trips unknown keys.
- Sample mission in `samples/SampleHud/missions/demo_mission.txt`.

## [0.3.0] — 2026-11-17

### Added
- **Game API layer** (`UbootLib.Api`):
  - `Catalog` — 3,106 game types indexed from IL2CPP metadata (embedded `api-catalog.json`),
    exact full/short-name lookup plus substring search, lazy interop `Type` resolution.
  - `Reflect` — guarded field/property/method access with metadata caching and
    `Chain("crew.uboat.rpm")` path reads; failures degrade to `null`, never throw.
  - `Hub` — typed state façade: `SceneName`, `InLobby`, `IsServer`, `Players`,
    `LocalPlayer`, `Uboats`, `Pawns`, `Summary()`.
- Renamed the whole product to **UbootLib** (assembly, namespace, plugin GUID `uboot.lib`,
  sample mod id `uboot.samplehud`).
- Startup log now reports the catalog size and a sample of matching types.

### Changed
- `SampleHud` heartbeat now prints `Hub.Summary()` so the API layer is exercised every run.

### Fixed
- Catalog JSON used short keys (`t/n/ns/...`) and deserialized to empty strings;
  mapped them with `[JsonPropertyName]`.

## [0.2.0] — 2026-11-17

### Added
- Main-menu Mod manager (`MODS` button): enable/disable, refresh, restart; writes `Mods/modkit.disabled`.
- `GameRestart` for safe relaunch.
- `ModLoader`: `Rescan`, `SetEnabled`, `IsLoaded`, `IsActive`, `IsDisabledOnDisk`, duplicate-id de-duplication.
- Manifest handshake (`Hello`/`HelloAck`) with non-blocking mismatch reporting.

### Fixed
- P2P packet reads returned zeroed payloads — `byte[]` out-params are copies under
  Il2CppInterop; switched to `Il2CppStructArray<byte>`.
- Codec self-test failures (buffer size, checksum range, trailing-byte check).
- Log spam before Steam initialised; config watcher being garbage collected.

## [0.1.0] — 2026-11-17

### Added
- Manifest discovery, dependency ordering, load isolation and quarantine.
- Harmony frame pump and scene events (no injected MonoBehaviour — ClassInjector crashes this build).
- JSON config with hot reload; uGUI overlay; runtime PNG → `Texture2D`; frame codec with self-test.
