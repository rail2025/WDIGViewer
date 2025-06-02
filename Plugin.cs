// Plugin.cs
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Lumina.Excel.Sheets;      // For TerritoryType, ContentFinderCondition etc.
using Lumina.Text;             // Required for SeString (ReadOnlySeString)

namespace WDIGViewer
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "WDIGViewer";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] internal static IClientState ClientState { get; private set; } = null!;
        [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
        // IDutyState.ContentFinderConditionId error indicates an API mismatch in your environment for this service.
        // This code expects API 12 where IDutyState does NOT directly provide ContentFinderConditionId.
        // We derive CFC from TerritoryType when IsDutyStarted is true.
        [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

        public Configuration Configuration { get; init; }
        public readonly WindowSystem WindowSystem = new("WDIGViewer");
        private Windows.ConfigWindow ConfigWindow { get; init; }
        private Windows.MainWindow MainWindow { get; init; }

        public List<FightStrategy> AllStrategies { get; private set; } = new List<FightStrategy>();

        private const string CommandName = "/wdig";
        private const string PluginImageFolderName = "PluginImages";
        private const string MainWindowName = "WDIGViewer##WDIGViewerMain";

        public Plugin()
        {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(PluginInterface);
            LoadStrategies();

            ConfigWindow = new Windows.ConfigWindow(this);
            MainWindow = new Windows.MainWindow(this, AllStrategies);

            WindowSystem.AddWindow(ConfigWindow);
            WindowSystem.AddWindow(MainWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            { HelpMessage = "Opens WDIGViewer. Use '/wdig reload' to rescan images. '/wdig <strategy name>' to attempt to open specific strategy." });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

            Log.Information("WDIGViewer Plugin Loaded.");
        }

        public void ReloadStrategies()
        {
            Log.Information("Reloading strategies...");
            foreach (var strategy in AllStrategies) { strategy.Dispose(); }
            AllStrategies.Clear();
            LoadStrategies();
            MainWindow.UpdateStrategies(AllStrategies);
            Log.Information("Strategies reloaded.");
        }

        private void LoadStrategies()
        {
            string pluginAssemblyLocation = PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
            if (string.IsNullOrEmpty(pluginAssemblyLocation)) { Log.Error("Could not determine plugin assembly location."); return; }

            string pluginImageDir = Path.Combine(pluginAssemblyLocation, PluginImageFolderName);
            if (Directory.Exists(pluginImageDir))
            {
                ScanDirectoryForStrategies(pluginImageDir, ImageSourceType.Plugin);
            }
            else
            {
                Log.Warning($"Plugin image directory not found: {pluginImageDir}. Creating it.");
                try { Directory.CreateDirectory(pluginImageDir); }
                catch (Exception ex) { Log.Error($"Could not create plugin image directory {pluginImageDir}: {ex.Message}"); }
            }

            if (!string.IsNullOrEmpty(Configuration.UserImageDirectory) && Directory.Exists(Configuration.UserImageDirectory))
            {
                ScanDirectoryForStrategies(Configuration.UserImageDirectory, ImageSourceType.User);
            }
            else if (!string.IsNullOrEmpty(Configuration.UserImageDirectory))
            {
                Log.Warning($"User image directory not found: {Configuration.UserImageDirectory}");
            }
        }

        private void ScanDirectoryForStrategies(string basePath, ImageSourceType sourceType)
        {
            try
            {
                Log.Info($"Scanning base path: {basePath} for source type: {sourceType}");
                foreach (var fightDir in Directory.GetDirectories(basePath).OrderBy(d => d))
                {
                    var strategy = new FightStrategy(new DirectoryInfo(fightDir).Name, sourceType, fightDir); //

                    // ADDED: Metadata ID loading for Plugin strategies
                    if (sourceType == ImageSourceType.Plugin) //
                    {
                        string metadataFilePath = Path.Combine(fightDir, "territory_id.txt");
                        if (File.Exists(metadataFilePath))
                        {
                            try
                            {
                                string content = File.ReadAllText(metadataFilePath).Trim();
                                if (ushort.TryParse(content, out ushort territoryId))
                                {
                                    strategy.MetadataTerritoryTypeId = territoryId;
                                    Log.Info($"Loaded metadata Territory ID {territoryId} for strategy {strategy.Name}");
                                }
                                else
                                {
                                    Log.Warning($"Could not parse Territory ID from {metadataFilePath} for strategy {strategy.Name}. Content: '{content}'");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Error reading metadata file {metadataFilePath} for strategy {strategy.Name}: {ex.Message}");
                            }
                        }
                    }
                    // END ADDED section

                    foreach (var phaseDir in Directory.GetDirectories(fightDir).OrderBy(d => d))
                    {
                        var phase = new FightPhase(new DirectoryInfo(phaseDir).Name); //
                        foreach (var imageFile in Directory.GetFiles(phaseDir).OrderBy(f => f)) //
                        {
                            if (IsSupportedImageFile(imageFile)) { phase.Images.Add(new ImageAsset(imageFile)); } //
                        }
                        if (phase.Images.Any()) { strategy.Phases.Add(phase); } //
                    }

                    if (strategy.Phases.Any()) //
                    {
                        if (!AllStrategies.Any(s => s.Name == strategy.Name && s.Source == strategy.Source)) AllStrategies.Add(strategy); //
                        else { Log.Warning($"Strategy '{strategy.Name}' from source '{strategy.Source}' already exists. Skipping."); strategy.Dispose(); } //
                    }
                    else { strategy.Dispose(); } //
                }
            }
            catch (Exception ex) { Log.Error($"Error scanning {basePath}: {ex.Message}"); } //
        }

        private bool IsSupportedImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tga" }.Contains(ext)) return false;
            try
            {
                using var stream = File.OpenRead(filePath);
                return SixLabors.ImageSharp.Image.DetectFormat(stream) != null;
            }
            catch (Exception ex) { Log.Warning($"Could not detect image format for {Path.GetFileName(filePath)}: {ex.Message}"); return false; }
        }

        public IDalamudTextureWrap? LoadTextureFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) { Log.Warning($"File not found: {filePath}"); return null; }
            try
            {
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (new[] { ".webp", ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(extension))
                {
                    try
                    {
                        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(filePath);
                        var rgbaBytes = new byte[image.Width * image.Height * 4];
                        image.CopyPixelDataTo(rgbaBytes);
                        return TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);
                    }
                    catch (Exception ex) { Log.Error($"ImageSharp failed for {Path.GetFileName(filePath)}: {ex.Message}"); return null; }
                }
                else
                {
                    var texture = TextureProvider.GetFromFile(filePath);
                    return texture?.GetWrapOrDefault();
                }
            }
            catch (Exception ex) { Log.Error($"Generic texture load exception for {Path.GetFileName(filePath)}: {ex.Message}"); return null; }
        }

        private void OnCommand(string command, string args)
        {
            string trimmedArgs = args.Trim(); //
            var mainWindowInstance = WindowSystem.Windows.FirstOrDefault(w => w.WindowName == MainWindowName) as Windows.MainWindow ?? this.MainWindow; //

            if (trimmedArgs.Equals("reload", StringComparison.OrdinalIgnoreCase)) //
            {
                ReloadStrategies(); //
            }
            else
            {
                string? strategyNameToSelect = null;
                ImageSourceType? sourceFilter = null; // To help SelectStrategyByName be more specific if needed

                if (string.IsNullOrEmpty(trimmedArgs)) // Auto-select logic for plain "/wdig"
                {
                    ushort currentTerritoryId = ClientState.TerritoryType; //
                    if (currentTerritoryId != 0)
                    {
                        // 1. Attempt to select via Metadata ID for Plugin (embedded) strategies
                        foreach (var strategy in AllStrategies)
                        {
                            if (strategy.Source == ImageSourceType.Plugin && //
                                strategy.MetadataTerritoryTypeId.HasValue &&
                                strategy.MetadataTerritoryTypeId.Value == currentTerritoryId)
                            {
                                strategyNameToSelect = strategy.Name;
                                sourceFilter = ImageSourceType.Plugin; // Matched a plugin strategy via metadata
                                Log.Info($"WDIGViewer: Matched embedded strategy '{strategy.Name}' via metadata Territory ID: {currentTerritoryId}");
                                break; // Found a match, use this one
                            }
                        }

                        // 2. If no metadata match, attempt to select via Duty/Zone Name (existing logic)
                        if (string.IsNullOrEmpty(strategyNameToSelect))
                        {
                            string? nameFromGameData = null;
                            if (DutyState.IsDutyStarted) //
                            {
                                var territorySheet = DataManager.GetExcelSheet<TerritoryType>(); //
                                if (territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryEntry) && territoryEntry.RowId > 0) //
                                {
                                    var cfcFromTerritoryRowRef = territoryEntry.ContentFinderCondition; //
                                    if (cfcFromTerritoryRowRef.RowId > 0) //
                                    {
                                        var cfcSheet = DataManager.GetExcelSheet<ContentFinderCondition>(); //
                                        if (cfcSheet != null && cfcSheet.TryGetRow(cfcFromTerritoryRowRef.RowId, out var cfcEntry) && cfcEntry.RowId > 0) //
                                        {
                                            if (!cfcEntry.Name.IsEmpty) //
                                            {
                                                nameFromGameData = cfcEntry.Name.ToString(); //
                                                Log.Info($"WDIGViewer: Attempting auto-select for active duty (via Territory's CFC): \"{nameFromGameData}\" (Territory ID: {currentTerritoryId}, CFC ID: {cfcEntry.RowId})"); //
                                            }
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(nameFromGameData)) // Fallback to general zone name
                            {
                                var territorySheet = DataManager.GetExcelSheet<TerritoryType>(); //
                                if (territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryEntry) && territoryEntry.RowId > 0) //
                                {
                                    var placeNameRef = territoryEntry.PlaceName; //
                                    if (placeNameRef.RowId > 0) //
                                    {
                                        PlaceName placeNameActual = placeNameRef.Value; //
                                        if (placeNameActual.RowId > 0 && !placeNameActual.Name.IsEmpty) //
                                        {
                                            nameFromGameData = placeNameActual.Name.ToString(); //
                                            Log.Info($"WDIGViewer: Attempting auto-select for zone: \"{nameFromGameData}\" (Territory ID: {currentTerritoryId})"); //
                                        }
                                    }
                                }
                            }
                            strategyNameToSelect = nameFromGameData; // Assign the found name from game data
                                                                     // sourceFilter remains null here, so SelectStrategyByName will search all sources
                        }
                    }
                }
                else // User provided arguments for a specific strategy name
                {
                    strategyNameToSelect = trimmedArgs; //
                    Log.Info($"WDIGViewer: Attempting to select strategy by argument: \"{strategyNameToSelect}\""); //
                                                                                                                    // sourceFilter remains null here, SelectStrategyByName will search all sources
                }

                if (!string.IsNullOrEmpty(strategyNameToSelect))
                {
                    mainWindowInstance?.SelectStrategyByName(strategyNameToSelect, sourceFilter); //
                }
            }
            ToggleMainUI(); //
        }

        private void DrawUI() => WindowSystem.Draw();
        public void ToggleConfigUI() => ConfigWindow?.Toggle();
        public void ToggleMainUI() => MainWindow?.Toggle();
        public void Dispose()
        {
            WindowSystem.RemoveAllWindows();
            foreach (var strategy in AllStrategies) { strategy.Dispose(); }
            AllStrategies.Clear();
            CommandManager.RemoveHandler(CommandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
            Log.Information("WDIGViewer Plugin Unloaded and Disposed.");
        }
    }
}
