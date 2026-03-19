# Mirror's Edge Tweaks

A tool for modding, tweaking settings and providing game fixes for Mirror's Edge.

![Version](https://img.shields.io/badge/version-4.2.0-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)

![mirrorsedgetweaks](https://github.com/user-attachments/assets/2b98d4a7-cf04-4fdd-808d-f37446384252)

## Features
- Developer console unlocker
- Command line argument unlocker
- Unlocked configs patch (a nicer alternative to [MEMLA](https://github.com/btbd/memla))
- TdGame version selector
- Tweaks Scripts installer (custom UnrealScript package including cheats and trainer functions, Softimer, etc.)
- Persistent FOV with additional viewmodel and input fixes
- Customisable aspect ratios with HOR+/VERT+ scaling
- High-res UI fix for resolutions greater than 1080p
- Highly configurable graphics settings
- Adjustable PhysX cloth simulation rates
- Custom keybind manager + speedrun macros
- Xbox/PS3 gamepad button prompt swapper
- Uniform mouse sensitivity, cm/360° converter
- Game language switcher
- OpenAL Soft audio upgrader
- Various other QoL

## Requirements

- **OS**: Windows 10 or later
- **.NET Runtime**: .NET 8.0 or later
- **Game**: Mirror's Edge (Steam, GOG, EA App/Xbox Game Pass for PC, Retail platforms). All versions supported (1.0.0.0 - 1.1.0.0 DLC)

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract the zip contents to a location of your choice
3. Run `Mirror's Edge Tweaks.exe`
4. Click **"Select Game Directory"** and navigate to your Mirror's Edge installation folder. Typical install locations:
   - Steam: `C:\Program Files (x86)\Steam\steamapps\common\mirrors edge`
   - GOG: `C:\Program Files (x86)\GOG Galaxy\Games\Mirror's Edge`
   - EA: `C:\Program Files\EA Games\Mirrors Edge`

## Building from Source

### Prerequisites
- Visual Studio 2022 or later
- .NET 8.0 SDK
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

# The executable will be in: MirrorsEdgeTweaks/bin/Release/net8.0-windows/
```

### Dependencies
- **Eliot.UELib** (v1.12.0)
- **MaterialDesignThemes** (v5.2.1)
- **Microsoft.Xaml.Behaviors**

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request. For major changes, please open an issue first to discuss what you would like to change.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- EA DICE for creating Mirror's Edge
- The Mirror's Edge community for your continued support and encouragement
- [UELib](https://github.com/EliotVU/Unreal-Library) for Unreal Engine package reading
- [UE Viewer](https://github.com/gildor2/UEViewer) for Unreal Engine package decompression

## Changelog

Refer to the [CHANGELOG](CHANGELOG.md) file for changes.

---

Made with ❤️ by softsoundd

