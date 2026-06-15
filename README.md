# Mirror's Edge Tweaks

A tool for modding, tweaking settings and providing game fixes for Mirror's Edge.

![Version](https://img.shields.io/badge/version-4.4.2-blue.svg)
![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)

<img width="1280" height="720" alt="MET" src="https://github.com/user-attachments/assets/3c072b76-7266-4468-bda7-922386558c7c" />

&nbsp;

[![Ko-fi](https://img.shields.io/badge/support_me_on_ko--fi-F16061?style=for-the-badge&logo=kofi&logoColor=f5f5f5)](https://ko-fi.com/softsoundd)

If you like what I do and would like to support my work, please consider visiting my Ko-fi page.

## Features
- Developer console unlocker
- Command line argument unlocker
- Unlocked configs patch (a persistent alternative to [MEMLA](https://github.com/btbd/memla))
- TdGame version selector
- Tweaks Scripts installer (custom UnrealScript package including cheats and trainer functions, Softimer, etc.)
- Persistent FOV with additional viewmodel and input fixes
- Unlocked aspect ratios with HOR+/VERT+ scaling
- High-res UI fix for resolutions greater than 1080p
- Highly configurable graphics settings
- Adjustable PhysX cloth simulation rates
- Custom keybind manager + speedrun macros
- Xbox/PS3 gamepad button prompt swapper
- Uniform mouse sensitivity, cm/360° converter
- Game language switcher
- OpenAL Soft audio upgrader + bespoke UE3 HRTF support
- Various other QoL

For further information and guides, refer to the [wiki](https://github.com/softsoundd/MirrorsEdgeTweaks/wiki).

## Requirements

- **OS**: Windows 10 or later
- **.NET Runtime**: .NET 10.0 or later
- **Game**: Mirror's Edge (Steam, GOG, EA App/Xbox Game Pass for PC, Retail platforms). All versions supported (1.0.0.0 - 1.1.0.0 DLC)

## Setup

1. Download the latest release from the [Releases](../../releases) page
2. Extract the zip to a location of your choice
3. Run `Mirror's Edge Tweaks.exe`
4. Click **"Select Game Directory"** and navigate to your Mirror's Edge installation folder. Typical install locations:
   - Steam: `C:\Program Files (x86)\Steam\steamapps\common\mirrors edge`
   - GOG: `C:\Program Files (x86)\GOG Galaxy\Games\Mirror's Edge`
   - EA: `C:\Program Files\EA Games\Mirrors Edge`

## Building from Source

### Prerequisites
- Visual Studio 2022 or later
- .NET 10.0 SDK
- Windows 10/11

### Build Steps
```bash
# Clone the repository
git clone https://github.com/softsoundd/MirrorsEdgeTweaks.git
cd MirrorsEdgeTweaks

# Restore NuGet packages
dotnet restore

# Build the solution
dotnet build --configuration Release

# The executable will be in: MirrorsEdgeTweaks/bin/Release/net10.0-windows/
```

## Acknowledgments

- EA DICE for creating Mirror's Edge
- The Mirror's Edge community for your continued support and encouragement
- [UELib](https://github.com/EliotVU/Unreal-Library) for Unreal Engine package reading
- [UE Viewer](https://github.com/gildor2/UEViewer) for inspiring the Unreal Engine package decompression behavior ported into this tool

## Changelog

Refer to [CHANGELOG](CHANGELOG.md) for changes.
