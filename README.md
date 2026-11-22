# Forever Ago Pace

Faster walking/running and line skipping for **Forever Ago** (Unity 6, MelonLoader + LibriaMods.Unity.Loader).

| Key | Action |
|---|---|
| **+** / **−** | Speed multiplier ±0.1 (default ×1.6) |
| **F8** | Extra speed on/off |
| **Tab** | Skip the current spoken line |

Settings: `UserData\LibriaMods\LibriaMods.FA.Pace.cfg` (keys are Unity `KeyCode` names).

## Install
1. [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) 0.7.3 in the game folder.
2. `Mods\LibriaMods.Unity.Loader.dll`, `UserLibs\LibriaMods.Unity.Shared.dll`, `UserData\LibriaMods\Mods\LibriaMods.FA.Pace.dll`.

## Build
Clone next to [LibriaMods.Unity.Shared](https://github.com/LibriaMods/LibriaMods.Unity.Shared) and open
`LibriaMods.FA.Pace.slnx`. With the game in the default Steam folder (or `ForeverAgoDir` in `..\LibriaMods.user.props`)
the build installs everything; the mod is hot-reloaded.

For a game installed elsewhere, create `LibriaMods.user.props` in the folder that contains the repos:

```xml
<Project>
  <PropertyGroup>
    <ForeverAgoDir>D:\Games\Forever Ago</ForeverAgoDir>
  </PropertyGroup>
</Project>
```
