# MDSFramework (Modular Defence System Framework)

Core framework mod for **Nuclear Option** (BepInEx 5): custom GLB 3D units & missiles, AESA/ELINT/Home-on-Jam (HoJ) sensor networks, Smart Weapon Target Assignment (HEL laser, 35mm AHEAD, SAM/VLS), and airdrop/sling logistics.

## Prerequisites
- **Nuclear Option** with **BepInEx 5** installed (`BepInEx\core\BepInEx.dll`)

## Installation
### Via NOMM (Nuclear Option Mod Manager)
Install `MDSFramework` directly inside NOMM (auto-updates via NOMNOM).

### Manual Release ZIP
1. Download `MDSFramework-v0.1.7.zip` from [Releases](https://github.com/ericogamo/MDSFramework/releases/latest).
2. Extract directly into `Nuclear Option\BepInEx\plugins\MDSFramework\` (so `MDSFramework.dll` sits inside `BepInEx\plugins\MDSFramework\`).

### From Git Clone
```powershell
git clone https://github.com/ericogamo/MDSFramework.git
cd MDSFramework
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

## Multiplayer & Authority
All WTA targeting, interception, missile launches, and sensor network updates run strictly under host/server authority (`IsServerAuthority()`).

## Companion Mods
- [`VanguardDefence`](https://github.com/ericogamo/VanguardDefence) — Skyshield & SeaWraith Contractor Pack
- [`GroundControlRtsExpanded`](https://github.com/ericogamo/GroundControlRtsExpanded) — RTS Commander & AI Expansion

## Build from Source
```powershell
dotnet build MDSFramework.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option"
```

## License & AI Disclosure
- **License:** [MIT License](LICENSE)
- **AI Assistance:** Created with substantial agentic/generative AI assistance (`isAiAssisted: True`).
- **Assets:** No ripped game assets are redistributed.
