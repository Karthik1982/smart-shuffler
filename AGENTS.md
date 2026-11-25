# AGENTS.md

## Scope
- Jellyfin plugin (`net10.0`, C# latest, nullable enabled) packaged as `Jellyfin.Plugin.SmartShufflePlaylist.dll` plus `meta.json`.
- No automated test, lint, or typecheck commands are configured; verify with `dotnet build -c Release`.

## Commands
- Build: `dotnet build -c Release`
- Dev deploy (default `~/jellyfin-dev/data/plugins`): `./deploy.sh`
- Prod deploy example: `sudo ./deploy.sh /var/lib/jellyfin/plugins && sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/SmartShufflePlaylist_1.0.0.0 && sudo systemctl restart jellyfin`
- After install/update, restart Jellyfin to load the plugin.

## Architecture
- `Plugin.cs`: plugin entry; GUID `7a28699c-e118-4666-84a0-e106de3866fa`, config page registration.
- `PluginServiceRegistrator.cs`: DI wiring; `SmartShuffleService` registered as singleton and `IHostedService`; `JavaScriptInjectorBridge` hosted service; controllers added via `AddApplicationPart`.
- `Api/SmartShuffleController.cs`: `/SmartShufflePlaylist` API, `[Authorize]`; user resolved from `Jellyfin-UserId` claim. Endpoints manage multiple playlists per user and playlist-scoped status/items/reset/cleanup.
- `Services/SmartShuffleService.cs`: core logic; playback hooks mark played/skips watched items, manages Jellyfin playlists via `IPlaylistManager`, persists state JSON at `<PluginConfigurationsPath>/SmartShufflePlaylist.state.json`.
- `Services/SmartShuffleState.cs` / `Models/StatusModels.cs`: persisted state and API DTOs. State includes legacy migration fields (`PlaylistId`, `PlayedItemIds`)—preserve migration behavior when changing state shape.
- `Configuration/configPage.html`: embedded admin/user config UI (inline JS/CSS) calling plugin and Jellyfin APIs.
- `Web/nativeProgress.js`: optional client script registered with Jellyfin JavaScript Injector by `Services/JavaScriptInjectorBridge.cs` (reflection over loaded assemblies; retries ~2 min).

## Repo-Specific Rules
- Keep version/GUID/packaging consistent when bumping releases: `Jellyfin.Plugin.SmartShufflePlaylist.csproj` (`Version`/`AssemblyVersion`/`FileVersion`), `meta.json` (`version`, `targetAbi` as needed, changelog), `deploy.sh` versioned folder name, and README install paths.
- Embedded resources must stay aligned: `Configuration/configPage.html` and `Web/nativeProgress.js` are declared in `.csproj` with `LogicalName`s; `JavaScriptInjectorBridge.LoadScript()` loads `nativeProgress.js` by manifest resource name.
- `PluginConfiguration` defaults matter; config API/UI expects current property names. Playlist name config is default for new playlists; service supports multiple managed playlists per user with active playlist.
- Do not assume README is fully current for multi-playlist behavior; trust code for API/state semantics. Keep README accurate if you change user-facing behavior.
- Plugin package contains only DLL + `meta.json` in versioned plugin folder; Jellyfin package refs are `ExcludeAssets=runtime`. Avoid adding runtime-only dependencies without verifying Jellyfin plugin loading.