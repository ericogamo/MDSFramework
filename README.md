# MDSFramework (Modular Defence System Framework)

Core framework mod for **Nuclear Option** (BepInEx 5): custom GLB 3D units & missiles, AESA/ELINT/Home-on-Jam (HoJ) sensor networks, Smart Weapon Target Assignment (HEL laser, 35mm AHEAD, SAM/VLS), and airdrop/sling logistics.

## Prerequisites
- **Nuclear Option** with **BepInEx 5** installed (`BepInEx\core\BepInEx.dll`)

## Quick Install / Update (Prebuilt DLL included)
```powershell
git clone https://github.com/ericogamo/MDSFramework.git
cd MDSFramework
powershell -ExecutionPolicy Bypass -File .\install.ps1
```
Optional explicit game path:
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Nuclear Option"
```

## Companion Mods
- [`VanguardDefence`](https://github.com/ericogamo/VanguardDefence) — Skyshield & SeaWraith Contractor Pack
- [`GroundControlRtsExpanded`](https://github.com/ericogamo/GroundControlRtsExpanded) — RTS Commander & AI Expansion

## Build from Source
```powershell
dotnet build MDSFramework.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option"
```
