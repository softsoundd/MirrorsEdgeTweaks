## Changelog

## [4.3.0] - 2026-04-11

### Added
- Added a `set`/`setnopec` executable patcher for the `1.1.0.0` DLC game version (both `PerformSetCommand` calls were removed only in this version for whatever reason)
  - Tweaks Scripts features (e.g. Softimer) that relied on `set`, and user-called `set` commands are now supported in the DLC
  - Automatically applies when installing the Developer Console or Tweaks Scripts if a DLC executable is detected
- Added automatic reapplication for TdGame-linked tweaks when installing/swapping TdGame versions

### Changed
- Integrated Unreal package decompression directly into Mirror's Edge Tweaks. Ported the UEViewer/UModel decompression behavior into native C#
  - Added handling for fully compressed and chunked UE3 package layouts used by Mirror's Edge
  - Switched LZO block decoding to `NativeSharpLzo`
  - Removed the standalone `decompress.exe` dependency
- Refined some areas of the UI

## [4.2.0] - 2026-03-18

### Changed
- Replaced the old `-CmdLineArgs` hijack workaround with an improved command line unlock patch, allowing Mirror's Edge to forward the raw Windows command line directly to UE3
  - The old method limited you to a max of 135 characters and did not support URL-specific paramaters - the new method does not have these limitations
  - Launch arguments can now be entered normally in your game library's launch options/other shortcuts while the patch is active. Launch arguments entered in Tweaks are now stored in `metweaksconfig.ini` instead of being baked into the executable
  - Note: The EA App/Xbox Game Pass for PC executable ships with with OOA-protected .text/.data which does not allow the patch to be written to the executable directly. To circumvent this, enter the arguments in Tweaks and use the 'Launch Game w/ Args' button which will apply the patch in memory during launch
- Reworked the `Unlocked Configs` patch to disable signed config file verification through the executable's embedded SHA hash resource. This replaces the previous version-specific byte patch
  - This makes the patch version-agnostic across all game versions, including the protected EA App/Xbox Game Pass for PC executable.

### Fixed
- Fixed the cm/360° converter writing `MinSensitivityMultiplier` and `MaxSensitivityMultiplier` with the current Windows decimal separator instead of the `.` format expected by `TdInput.ini`, which could break mouse input in some regions

## [4.1.0] - 2026-01-03

### Added
- Added logic to preserve existing `TweaksScriptsSettings` values when updating/reinstalling Tweaks Scripts
- Published folder now gets created if the game hadn't done so

### Changed
- Now using package GUID detection for more reliable identification of TdGame versions
- Improved Developer Console status detection
- Fixed UI freezing issues during certain download/patching operations and other interaction improvements

## [4.0.0] - 2025-10-26 (initial public, open source release)

### Added

- Added command line argument patching
- Added Tweaks Scripts and Tweaks Scripts UI installers
- Added cm/360° converter
- Added adjustable time trial count down delay

### Changed

- Complete codebase rewrite and UI redesign
- Now leveraging UELib for Unreal Package reading
- Changed macro keybindings from the TdInput file to the TweaksScriptsSettings file

