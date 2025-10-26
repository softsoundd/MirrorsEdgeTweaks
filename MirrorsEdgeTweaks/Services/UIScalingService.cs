using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Helpers;
using UELib;
using UELib.Core;
using UELib.Flags;
using UELib.Types;

namespace MirrorsEdgeTweaks.Services
{
    public interface IUIScalingService
    {
        bool ShouldOfferUIScaling(int width);
        Task<bool> AskUserForUIScalingConfirmationAsync();
        Task ApplyUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null);
        Task RollbackUIScalingToDefaultsAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null);
        bool IsUIScalingActive(string gameDirectoryPath);
    }

    public class UIScalingService : IUIScalingService
    {
        private readonly IPackageService _packageService;
        private readonly IFileService _fileService;
        private readonly IOffsetFinderService _offsetFinderService;
        private readonly IDecompressionService _decompressionService;

        public UIScalingService(IPackageService packageService, IFileService fileService, IOffsetFinderService offsetFinderService, IDecompressionService decompressionService)
        {
            _packageService = packageService;
            _fileService = fileService;
            _offsetFinderService = offsetFinderService;
            _decompressionService = decompressionService;
        }

        public bool ShouldOfferUIScaling(int width)
        {
            // blurry UI occurs only above 1920 width
            return width > 1920;
        }

        public async Task<bool> AskUserForUIScalingConfirmationAsync()
        {
            return await DialogHelper.ShowConfirmationAsync(
                "Fix UI and blurry text?",
                "Do you wish to fix the game's UI to render natively at this resolution? This resolves the blurry text issues at higher resolutions and ensures consistent UI scaling.\n\n" +
                "Note: This solution partially works at the moment. While blurriness is resolved, some text elements such as subtitles, lists, timer HUD and loading screen " +
                "text will appear smaller as you increase the resolution.");
        }

        public async Task ApplyUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null)
        {
            try
            {
                if (width <= 1920)
                {
                    await RollbackUIScalingToDefaultsAsync(width, height, gameDirectoryPath, beforeShowingDialog);
                    return;
                }

                await ApplyHighResolutionUIScalingAsync(width, height, gameDirectoryPath, beforeShowingDialog);
            }
            catch (Exception)
            {
            }
        }

        private async Task ApplyHighResolutionUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null)
        {
            try
            {
                double aspectRatio = (double)width / height;
                double constrainedAspectRatio = 16.0 / 9.0;
                int restestValue = (int)((height * (aspectRatio / constrainedAspectRatio)) + 0.5);

                double scalingFactor = (double)height / 1080.0;
                int cursorScalingValue = (int)Math.Round(32 * scalingFactor);

                string cookedPcPath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");
                
                if (!Directory.Exists(cookedPcPath))
                {
                    throw new DirectoryNotFoundException("CookedPC directory not found");
                }

                DecompressFilesWithPattern(cookedPcPath, "Ts_LOC_*");
                
                DecompressFilesWithPattern(cookedPcPath, "Startup_*");

                await ProcessFilesWithPatternAsync(cookedPcPath, "Ts_LOC_*", restestValue);
                
                await ProcessStartupFilesAsync(cookedPcPath, restestValue, cursorScalingValue);

                await ProcessEngineUIStyleTextAsync(gameDirectoryPath, scalingFactor);

                await ProcessCrosshairScalingAsync(cookedPcPath, scalingFactor);

                beforeShowingDialog?.Invoke();

                await DialogHelper.ShowMessageAsync(
                    "Resolution Updated",
                    $"Resolution set to {width} x {height}\nUI fix applied successfully.",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception)
            {
            }
        }

        private void DecompressFilesWithPattern(string directoryPath, string pattern)
        {
            try
            {
                var files = Directory.GetFiles(directoryPath, pattern);
                
                foreach (string filePath in files)
                {
                    try
                    {
                        _decompressionService.RunDecompressor(filePath);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessFilesWithPatternAsync(string directoryPath, string pattern, int restestValue)
        {
            var files = Directory.GetFiles(directoryPath, pattern);
            
            foreach (string filePath in files)
            {
                try
                {
                    await ProcessFileUELibAsync(filePath, restestValue);
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }

        private async Task ProcessFileUELibAsync(string filePath, int restestValue)
        {
            try
            {
                // UELib can't deserialise ResolutionTestTable out of the box, we manually define its array type here
                if (UnrealConfig.VariableTypes == null)
                {
                    UnrealConfig.VariableTypes = new Dictionary<string, Tuple<string, PropertyType>>();
                }
                
                if (!UnrealConfig.VariableTypes.ContainsKey("ResolutionTestTable"))
                {
                    UnrealConfig.VariableTypes["ResolutionTestTable"] = 
                        new Tuple<string, PropertyType>("FloatProperty", PropertyType.FloatProperty);
                }

                using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                int nameIndex = package.Names.FindIndex(n => n.ToString() == "ResolutionTestTable");
                if (nameIndex == -1)
                    return;

                var multiFontExports = package.Exports
                    .Where(e => e.Class?.ObjectName?.ToString() == "MultiFont")
                    .ToList();

                if (multiFontExports.Count == 0)
                    return;

                byte[] data = await File.ReadAllBytesAsync(filePath);
                bool anyModified = false;

                foreach (var export in multiFontExports)
                {
                    bool modified = await ModifyResolutionTestTableInExportAsync(data, package, export, nameIndex, (float)restestValue);
                    if (modified)
                        anyModified = true;
                }

                if (anyModified)
                {
                    var fileInfo = new FileInfo(filePath);
                    bool wasReadOnly = fileInfo.IsReadOnly;

                    try
                    {
                        if (wasReadOnly)
                            fileInfo.IsReadOnly = false;

                        await File.WriteAllBytesAsync(filePath, data);
                    }
                    finally
                    {
                        if (wasReadOnly)
                            fileInfo.IsReadOnly = true;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private Task<bool> ModifyResolutionTestTableInExportAsync(byte[] data, UnrealPackage package, UExportTableItem export, int nameIndex, float newValue)
        {
            try
            {
                long searchStart = export.SerialOffset;
                long searchEnd = searchStart + export.SerialSize;

                byte[] nameIndexBytes = BitConverter.GetBytes((long)nameIndex);

                for (long i = searchStart; i < searchEnd - 32; i++)
                {
                    bool nameMatch = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != nameIndexBytes[j])
                        {
                            nameMatch = false;
                            break;
                        }
                    }

                    if (nameMatch)
                    {
                        long arrayCountOffset = i + 24;
                        if (arrayCountOffset + 4 > data.Length)
                            return Task.FromResult(false);

                        int actualArrayCount = BitConverter.ToInt32(data, (int)arrayCountOffset);
                        
                        if (actualArrayCount <= 0 || actualArrayCount > 100)
                            return Task.FromResult(false);
                        
                        long lastFloatOffset = i + 28 + (actualArrayCount - 1) * 4;
                        
                        if (lastFloatOffset + 4 <= data.Length)
                        {
                            float currentValue = BitConverter.ToSingle(data, (int)lastFloatOffset);

                            if (Math.Abs(currentValue - newValue) < 0.01f)
                                return Task.FromResult(false);

                            byte[] newValueBytes = BitConverter.GetBytes(newValue);
                            Array.Copy(newValueBytes, 0, data, lastFloatOffset, 4);

                            return Task.FromResult(true);
                        }
                        break;
                    }
                }

                return Task.FromResult(false);
            }
            catch (Exception)
            {
                return Task.FromResult(false);
            }
        }

        private async Task ProcessStartupFilesAsync(string directoryPath, int restestValue, int cursorScalingValue)
        {
            var files = Directory.GetFiles(directoryPath, "Startup_*");
            
            foreach (string filePath in files)
            {
                try
                {
                    await ProcessStartupFileUELibAsync(filePath, restestValue, cursorScalingValue);
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }

        private async Task ProcessStartupFileUELibAsync(string filePath, int restestValue, int cursorScalingValue)
        {
            try
            {
                await ProcessFileUELibAsync(filePath, restestValue);

                await ProcessCursorScalingInStartupAsync(filePath, cursorScalingValue);
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessCursorScalingInStartupAsync(string filePath, int cursorScalingValue)
        {
            try
            {
                using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                var arrowExport = package.Exports.FirstOrDefault(e => 
                    e.ObjectName?.ToString() == "Arrow" &&
                    e.Class?.ObjectName?.ToString() == "Texture2D");

                if (arrowExport == null)
                    return;

                var arrowObject = arrowExport.Object as UObject;
                
                if (arrowObject == null)
                    return;

                arrowObject.Load<UObjectRecordStream>();

                if (arrowObject.Properties == null)
                    return;

                var sizeXProp = arrowObject.Properties
                    .OfType<UDefaultProperty>()
                    .FirstOrDefault(prop => prop.Name?.ToString() == "SizeX");

                var sizeYProp = arrowObject.Properties
                    .OfType<UDefaultProperty>()
                    .FirstOrDefault(prop => prop.Name?.ToString() == "SizeY");

                if (sizeXProp != null && sizeYProp != null)
                {
                    await ModifyCrosshairPropertyAsync(filePath, arrowObject, sizeXProp, cursorScalingValue);
                    await ModifyCrosshairPropertyAsync(filePath, arrowObject, sizeYProp, cursorScalingValue);
                }
            }
            catch (Exception)
            {
            }
        }

        private Task ProcessEngineUIStyleTextAsync(string gameDirectoryPath, double scalingFactor)
        {
            try
            {
                string engineUPath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "Engine.u");
                
                if (File.Exists(engineUPath))
                {
                    using var package = UnrealLoader.LoadPackage(engineUPath, FileAccess.Read);
                    package?.InitializePackage();
                    
                    if (package != null)
                    {
                        var uiStyleTextClass = package.FindObject<UClass>("UIStyle_Text");
                        
                        if (uiStyleTextClass != null)
                        {
                            if (uiStyleTextClass.Default is UObject uiStyleTextCDO)
                            {
                                uiStyleTextCDO.Load<UObjectRecordStream>();
                                
                                if (uiStyleTextCDO.Properties != null)
                                {
                                    var scaleProperty = uiStyleTextCDO.Properties
                                        .OfType<UDefaultProperty>()
                                        .FirstOrDefault(prop => prop.Name?.ToString() == "Scale");
                                    
                                    if (scaleProperty != null)
                                    {
                                        float currentScaleX = 1.0f;
                                        float currentScaleY = 1.0f;
                                        
                                        if (!string.IsNullOrEmpty(scaleProperty.Value))
                                        {
                                            var valueStr = scaleProperty.Value.TrimEnd(')');
                                            var parts = valueStr.Split(',');
                                            
                                            if (parts.Length >= 2)
                                            {
                                                var xPart = parts[0].Split('=');
                                                if (xPart.Length >= 2 && float.TryParse(xPart[1], out float parsedX))
                                                {
                                                    currentScaleX = parsedX;
                                                }
                                                
                                                var yPart = parts[1].Split('=');
                                                if (yPart.Length >= 2 && float.TryParse(yPart[1], out float parsedY))
                                                {
                                                    currentScaleY = parsedY;
                                                }
                                            }
                                        }
                                        
                                        ModifyUIStyleTextScaleDirectlyAsync(engineUPath, scalingFactor, uiStyleTextCDO, scaleProperty, currentScaleX, currentScaleY);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return Task.CompletedTask;
        }

        private void ModifyUIStyleTextScaleDirectlyAsync(string engineUPath, double scalingFactor, UObject uiStyleTextCDO, UDefaultProperty scaleProperty, float currentScaleX, float currentScaleY)
        {
            try
            {
                var fileInfo = new FileInfo(engineUPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                    fileInfo.IsReadOnly = false;

                try
                {
                    byte[] data = _fileService.ReadAllBytes(engineUPath);
                    
                    byte[] scaleBytes = BitConverter.GetBytes((float)scalingFactor);

                    byte[] currentScaleXBytes = BitConverter.GetBytes(currentScaleX);
                    byte[] currentScaleYBytes = BitConverter.GetBytes(currentScaleY);

                    byte[][] scalePatterns = new byte[][]
                    {
                        new byte[] { currentScaleXBytes[0], currentScaleXBytes[1], currentScaleXBytes[2], currentScaleXBytes[3],
                                     currentScaleYBytes[0], currentScaleYBytes[1], currentScaleYBytes[2], currentScaleYBytes[3] },
                        currentScaleXBytes,
                    };
                    
                    if (uiStyleTextCDO.ExportTable != null)
                    {
                        long searchStart = uiStyleTextCDO.ExportTable.SerialOffset;
                        long searchEnd = searchStart + uiStyleTextCDO.ExportTable.SerialSize;
                        
                        searchStart = Math.Max(0, searchStart);
                        searchEnd = Math.Min(data.Length, searchEnd);
                        
                        foreach (var scalePattern in scalePatterns)
                        {
                            for (long i = searchStart; i <= searchEnd - scalePattern.Length; i++)
                            {
                                bool found = true;
                                for (int j = 0; j < scalePattern.Length; j++)
                                {
                                    if (data[i + j] != scalePattern[j])
                                    {
                                        found = false;
                                        break;
                                    }
                                }
                                
                                if (found)
                                {
                                    if (scalePattern.Length == 8)
                                    {
                                        Array.Copy(scaleBytes, 0, data, i, 4);
                                        Array.Copy(scaleBytes, 0, data, i + 4, 4);
                                    }
                                    else if (scalePattern.Length == 4)
                                    {
                                        Array.Copy(scaleBytes, 0, data, i, 4);

                                        for (long k = i + 4; k <= Math.Min(i + 20, searchEnd - 4); k++)
                                        {
                                            bool secondFound = true;
                                            for (int l = 0; l < 4; l++)
                                            {
                                                if (data[k + l] != scalePattern[l])
                                                {
                                                    secondFound = false;
                                                    break;
                                                }
                                            }
                                            if (secondFound)
                                            {
                                                Array.Copy(scaleBytes, 0, data, k, 4);
                                                break;
                                            }
                                        }
                                    }
                                    
                                    _fileService.WriteAllBytes(engineUPath, data);
                                    return;
                                }
                            }
                        }
                        
                        var foundPositions = new List<long>();
                        
                        for (long i = searchStart; i <= searchEnd - 4; i++)
                        {
                            bool found = true;
                            for (int j = 0; j < 4; j++)
                            {
                                if (data[i + j] != currentScaleXBytes[j])
                                {
                                    found = false;
                                    break;
                                }
                            }
                            if (found)
                            {
                                foundPositions.Add(i);
                            }
                        }
                        
                        if (foundPositions.Count >= 2)
                        {
                            Array.Copy(scaleBytes, 0, data, foundPositions[0], 4);
                            Array.Copy(scaleBytes, 0, data, foundPositions[1], 4);
                            
                            _fileService.WriteAllBytes(engineUPath, data);
                            return;
                        }
                        
                    }
                    else
                    {
                    }
                }
                finally
                {
                    if (wasReadOnly)
                        fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessCrosshairScalingAsync(string cookedPcPath, double scalingFactor)
        {
            try
            {
                await ProcessTdGameCrosshairScalingAsync(cookedPcPath, scalingFactor);

                await ProcessTdUIResourcesCrosshairScalingAsync(cookedPcPath, scalingFactor);
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessTdGameCrosshairScalingAsync(string cookedPcPath, double scalingFactor)
        {
            try
            {
                string tdGamePath = Path.Combine(cookedPcPath, "TdGame.u");
                if (!File.Exists(tdGamePath))
                    return;

                using var package = UnrealLoader.LoadPackage(tdGamePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                // default sizes for crosshairs
                var crosshairs = new[]
                {
                    new { Name = "CrossHair_Reaction", BaseSize = 16 },
                    new { Name = "CrossHair_Standard", BaseSize = 32 },
                    new { Name = "CrossHair_Unarmed", BaseSize = 16 },
                    new { Name = "CrossHair_Weapon", BaseSize = 64 }
                };

                foreach (var crosshair in crosshairs)
                {
                    var crosshairExport = package.Exports.FirstOrDefault(e => 
                        e.ObjectName?.ToString() == crosshair.Name && 
                        e.Outer?.ObjectName?.ToString() == "TdUIResources_InGame");

                    if (crosshairExport == null)
                        continue;

                    var crosshairObject = crosshairExport.Object as UObject;
                    
                    if (crosshairObject != null)
                    {
                        crosshairObject.Load<UObjectRecordStream>();

                        if (crosshairObject.Properties != null)
                        {
                            var sizeXProp = crosshairObject.Properties
                                .OfType<UDefaultProperty>()
                                .FirstOrDefault(prop => prop.Name?.ToString() == "SizeX");

                            var sizeYProp = crosshairObject.Properties
                                .OfType<UDefaultProperty>()
                                .FirstOrDefault(prop => prop.Name?.ToString() == "SizeY");

                            if (sizeXProp != null && sizeYProp != null)
                            {
                                int newSize = (int)Math.Round(crosshair.BaseSize * scalingFactor);

                                await ModifyCrosshairPropertyAsync(tdGamePath, crosshairObject, sizeXProp, newSize);
                                await ModifyCrosshairPropertyAsync(tdGamePath, crosshairObject, sizeYProp, newSize);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task<bool> ModifyCrosshairPropertyAsync(string filePath, UObject crosshairObject, UDefaultProperty property, int newValue)
        {
            try
            {
                byte[] data = await File.ReadAllBytesAsync(filePath);

                var exportTable = crosshairObject.ExportTable;
                if (exportTable == null)
                    return false;

                if (!int.TryParse(property.Value, out int currentValue))
                    return false;

                if (currentValue == newValue)
                    return false;

                using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return false;

                int nameIndex = package.Names.FindIndex(n => n.ToString() == property.Name?.ToString());
                if (nameIndex == -1)
                    return false;

                byte[] nameIndexBytes = BitConverter.GetBytes((long)nameIndex);

                long searchStart = exportTable.SerialOffset;
                long searchEnd = searchStart + exportTable.SerialSize;

                long propertyOffset = -1;

                for (long i = searchStart; i < searchEnd - 28; i++)
                {
                    bool nameMatch = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != nameIndexBytes[j])
                        {
                            nameMatch = false;
                            break;
                        }
                    }

                    if (nameMatch)
                    {
                        long valueOffset = i + 24;
                        
                        if (valueOffset + 4 <= data.Length)
                        {
                            int valueAtOffset = BitConverter.ToInt32(data, (int)valueOffset);

                            if (valueAtOffset == currentValue)
                            {
                                propertyOffset = valueOffset;
                                break;
                            }
                        }
                    }
                }

                if (propertyOffset == -1)
                    return false;

                byte[] newValueBytes = BitConverter.GetBytes(newValue);

                if (propertyOffset + 4 > data.Length)
                    return false;

                Array.Copy(newValueBytes, 0, data, propertyOffset, 4);

                var fileInfo = new FileInfo(filePath);
                bool wasReadOnly = fileInfo.IsReadOnly;

                try
                {
                    if (wasReadOnly)
                        fileInfo.IsReadOnly = false;

                    await File.WriteAllBytesAsync(filePath, data);
                    return true;
                }
                finally
                {
                    if (wasReadOnly)
                        fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task ProcessTdUIResourcesCrosshairScalingAsync(string cookedPcPath, double scalingFactor)
        {
            try
            {
                string tdUIResourcesPath = Path.Combine(cookedPcPath, "UI", "TdUIResources_InGame.upk");
                if (!File.Exists(tdUIResourcesPath))
                    return;

                using var package = UnrealLoader.LoadPackage(tdUIResourcesPath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                var crosshairs = new[]
                {
                    new { Name = "CrossHair_Reaction", BaseSize = 16 },
                    new { Name = "CrossHair_Standard", BaseSize = 32 },
                    new { Name = "CrossHair_Unarmed", BaseSize = 16 },
                    new { Name = "CrossHair_Weapon", BaseSize = 64 }
                };

                foreach (var crosshair in crosshairs)
                {
                    var crosshairExport = package.Exports.FirstOrDefault(e => 
                        e.ObjectName?.ToString() == crosshair.Name &&
                        e.Class?.ObjectName?.ToString() == "Texture2D");

                    if (crosshairExport == null)
                        continue;

                    var crosshairObject = crosshairExport.Object as UObject;
                    
                    if (crosshairObject != null)
                    {
                        crosshairObject.Load<UObjectRecordStream>();

                        if (crosshairObject.Properties != null)
                        {
                            var sizeXProp = crosshairObject.Properties
                                .OfType<UDefaultProperty>()
                                .FirstOrDefault(prop => prop.Name?.ToString() == "SizeX");

                            var sizeYProp = crosshairObject.Properties
                                .OfType<UDefaultProperty>()
                                .FirstOrDefault(prop => prop.Name?.ToString() == "SizeY");

                            if (sizeXProp != null && sizeYProp != null)
                            {
                                int newSize = (int)Math.Round(crosshair.BaseSize * scalingFactor);

                                await ModifyCrosshairPropertyAsync(tdUIResourcesPath, crosshairObject, sizeXProp, newSize);
                                await ModifyCrosshairPropertyAsync(tdUIResourcesPath, crosshairObject, sizeYProp, newSize);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public bool IsUIScalingActive(string gameDirectoryPath)
        {
            try
            {
                string cookedPcPath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");
                
                if (!Directory.Exists(cookedPcPath))
                {
                    return false;
                }

                string engineUPath = Path.Combine(cookedPcPath, "Engine.u");
                if (File.Exists(engineUPath))
                {
                    using var package = UnrealLoader.LoadPackage(engineUPath, FileAccess.Read);
                    package?.InitializePackage();
                    
                    if (package != null)
                    {
                        var uiStyleTextClass = package.FindObject<UClass>("UIStyle_Text");
                        if (uiStyleTextClass?.Default is UObject uiStyleTextCDO)
                        {
                            uiStyleTextCDO.Load<UObjectRecordStream>();
                            
                            if (uiStyleTextCDO.Properties != null)
                            {
                                var scaleProperty = uiStyleTextCDO.Properties
                                    .OfType<UDefaultProperty>()
                                    .FirstOrDefault(prop => prop.Name?.ToString() == "Scale");
                                
                                if (scaleProperty != null && !string.IsNullOrEmpty(scaleProperty.Value))
                                {
                                    var valueStr = scaleProperty.Value.TrimEnd(')');
                                    var parts = valueStr.Split(',');
                                    
                                    if (parts.Length >= 2)
                                    {
                                        var xPart = parts[0].Split('=');
                                        var yPart = parts[1].Split('=');
                                        
                                        if (xPart.Length >= 2 && yPart.Length >= 2 &&
                                            float.TryParse(xPart[1], out float scaleX) &&
                                            float.TryParse(yPart[1], out float scaleY))
                                        {
                                            return Math.Abs(scaleX - 1.0f) > 0.01f || Math.Abs(scaleY - 1.0f) > 0.01f;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        public async Task RollbackUIScalingToDefaultsAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null)
        {
            try
            {
                int defaultRestestValue = 1080;
                int defaultCursorScalingValue = 32;
                double defaultScalingFactor = 1.0;

                string cookedPcPath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");
                
                if (!Directory.Exists(cookedPcPath))
                {
                    throw new DirectoryNotFoundException("CookedPC directory not found");
                }

                DecompressFilesWithPattern(cookedPcPath, "Ts_LOC_*");
                
                DecompressFilesWithPattern(cookedPcPath, "Startup_*");

                await ProcessFilesWithPatternAsync(cookedPcPath, "Ts_LOC_*", defaultRestestValue);
                
                await ProcessStartupFilesAsync(cookedPcPath, defaultRestestValue, defaultCursorScalingValue);

                await ProcessEngineUIStyleTextAsync(gameDirectoryPath, defaultScalingFactor);

                await ProcessCrosshairScalingAsync(cookedPcPath, defaultScalingFactor);

                beforeShowingDialog?.Invoke();

                await DialogHelper.ShowMessageAsync(
                    "Resolution Updated",
                    $"Resolution set to {width} x {height}\nUI fix reset to defaults.",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception)
            {
            }
        }
    }
}
