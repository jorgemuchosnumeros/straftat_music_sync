# Straftat Music Sync (STRAFTAT / BepInEx Mono)

Sync the host's playlist, playback position, and pause state for everyone in the lobby and during matches.

## Installation (manual)
Assuming [BepInEx Mono](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_mono.html) is installed, unzip the release in `STRAFTAT/Bepinex/Plugins`.

## Usage
Start the game with the mod installed on all players.

The host controls music as usual. Clients automatically follow the host track, playback time, and pause state in the lobby and in-game.

If a client is missing the host's current custom track, the mod requests it from the host and imports it before resyncing. Large files may take a few seconds to finish transferring.

## Notes
- Custom track transfer is on-demand for the currently active host track, not full playlist mirroring.
- Clients still need this mod installed to follow host music and receive missing tracks.

## Building

Place required game assemblies in `straftat_music_sync/libs`:
- `Assembly-CSharp.dll`
- `ComputerysModdingUtilities.dll`
- `com.rlabrecque.steamworks.net.dll`
- `FishNet.Runtime.dll`
- `Newtonsoft.Json.dll`
- `UnityEngine.dll`
- `UnityEngine.AudioModule.dll`
- `UnityEngine.CoreModule.dll`
- `Unity.TextMeshPro.dll`
- `UnityEngine.JSONSerializeModule.dll`
- `UnityEngine.UnityWebRequestModule.dll`
- `UnityEngine.UnityWebRequestWWWModule.dll`

Then build:

```bash
dotnet build straftat_music_sync.sln
```
