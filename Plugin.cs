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
using System.Reflection;
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
        [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

        public Configuration Configuration { get; init; }
        public readonly WindowSystem WindowSystem = new("WDIGViewer");
        private Windows.ConfigWindow ConfigWindow { get; init; }
        private Windows.MainWindow MainWindow { get; init; }

        public List<FightStrategy> AllStrategies { get; private set; } = new List<FightStrategy>();

        private const string CommandName = "/wdig";
        // PluginImageFolderName is now used as part of the resource path prefix
        private const string PluginImageFolderName = "PluginImages";
        private const string MainWindowName = "WDIGViewer##WDIGViewerMain";
        private readonly string _resourcePathPrefix;

        public Plugin()
        {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(PluginInterface);

            // Determine the resource path prefix based on RootNamespace and PluginImageFolderName
            string rootNamespace = Assembly.GetExecutingAssembly().GetName().Name ?? "WDIGViewer";
            _resourcePathPrefix = $"{rootNamespace}.{PluginImageFolderName}.";

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
            // Load embedded strategies for ImageSourceType.Plugin
            LoadEmbeddedPluginStrategies();

            // Load user-defined strategies from file system for ImageSourceType.User
            if (!string.IsNullOrEmpty(Configuration.UserImageDirectory) && Directory.Exists(Configuration.UserImageDirectory))
            {
                ScanDirectoryForStrategies(Configuration.UserImageDirectory, ImageSourceType.User);
            }
            else if (!string.IsNullOrEmpty(Configuration.UserImageDirectory))
            {
                Log.Warning($"User image directory not found: {Configuration.UserImageDirectory}");
            }
        }

        private void LoadEmbeddedPluginStrategies()
        {
            Log.Info($"Scanning embedded resources for Plugin strategies with prefix: {_resourcePathPrefix}");
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            var strategyResources = new Dictionary<string, List<string>>(); // Key: Strategy Name, Value: List of phase/image resource names

            foreach (var resourceName in resourceNames)
            {
                if (resourceName.StartsWith(_resourcePathPrefix))
                {
                    string pathPart = resourceName.Substring(_resourcePathPrefix.Length);
                    string[] parts = pathPart.Split('.');

                    // Expected structure: StrategyName.PhaseName.ImageName.ext or StrategyName.territory_id.txt
                    if (parts.Length >= 2) // At least StrategyName.FileName or StrategyName.PhaseName...
                    {
                        // Resource names replace spaces and some chars with '_'. Revert for display/key.
                        string strategyKey = parts[0].Replace("_", " ");
                        if (!strategyResources.ContainsKey(strategyKey))
                        {
                            strategyResources[strategyKey] = new List<string>();
                        }
                        strategyResources[strategyKey].Add(resourceName);
                    }
                }
            }

            foreach (var stratEntry in strategyResources.OrderBy(kvp => kvp.Key))
            {
                string strategyDisplayName = stratEntry.Key;
                // The "rootPath" for embedded strategies is the resource prefix for that strategy
                string strategyResourceRootPath = $"{_resourcePathPrefix}{strategyDisplayName.Replace(" ", "_")}.";

                var strategy = new FightStrategy(strategyDisplayName, ImageSourceType.Plugin, strategyResourceRootPath);

                // Load metadata (territory_id.txt) for this strategy
                string territoryIdResourceName = $"{strategyResourceRootPath}territory_id.txt";
                if (stratEntry.Value.Contains(territoryIdResourceName))
                {
                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(territoryIdResourceName);
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            string content = reader.ReadToEnd().Trim();
                            if (ushort.TryParse(content, out ushort territoryId))
                            {
                                strategy.MetadataTerritoryTypeId = territoryId;
                                Log.Info($"Loaded embedded metadata Territory ID {territoryId} for strategy {strategy.Name}");
                            }
                            else
                            {
                                Log.Warning($"Could not parse embedded Territory ID for strategy {strategy.Name}. Content: '{content}'");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error reading embedded metadata for strategy {strategy.Name}: {ex.Message}");
                    }
                }

                // Group images by phase
                var phaseResources = new Dictionary<string, List<string>>(); // Key: Phase Name, Value: List of image resource names
                foreach (var resourceName in stratEntry.Value)
                {
                    if (resourceName.Equals(territoryIdResourceName)) continue; // Skip metadata file

                    string pathPart = resourceName.Substring(strategyResourceRootPath.Length);
                    string[] parts = pathPart.Split('.');
                    // Expected: PhaseName.ImageName.ext
                    if (parts.Length >= 3) // PhaseName, ImageName, ext
                    {
                        // Resource names replace spaces and some chars with '_'. Revert for display/key.
                        string phaseKey = parts[0].Replace("_", " ");
                        if (!phaseResources.ContainsKey(phaseKey))
                        {
                            phaseResources[phaseKey] = new List<string>();
                        }
                        phaseResources[phaseKey].Add(resourceName);
                    }
                    else
                    {
                        Log.Warning($"Skipping resource, unexpected structure for image: {resourceName}. Expected PhaseName.ImageName.ext after strategy path.");
                    }
                }

                foreach (var phaseEntry in phaseResources.OrderBy(kvp => kvp.Key))
                {
                    var phase = new FightPhase(phaseEntry.Key);
                    foreach (var imageResourceName in phaseEntry.Value.OrderBy(f => f))
                    {
                        // Check if it's a supported image type by extension from resource name
                        string extension = Path.GetExtension(imageResourceName).ToLowerInvariant();
                        if (new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tga" }.Contains(extension))
                        {
                            // For ImageAsset, FilePath will store the full manifest resource name
                            phase.Images.Add(new ImageAsset(imageResourceName));
                        }
                    }
                    if (phase.Images.Any()) strategy.Phases.Add(phase);
                }

                if (strategy.Phases.Any())
                {
                    if (!AllStrategies.Any(s => s.Name == strategy.Name && s.Source == strategy.Source)) AllStrategies.Add(strategy);
                    else { Log.Warning($"Embedded Strategy '{strategy.Name}' from source '{strategy.Source}' already exists. Skipping."); strategy.Dispose(); }
                }
                else { strategy.Dispose(); }
            }
        }


        // This method is kept for ImageSourceType.User (loading from user's custom directory)
        private void ScanDirectoryForStrategies(string basePath, ImageSourceType sourceType)
        {
            try
            {
                Log.Info($"Scanning base path: {basePath} for source type: {sourceType}");
                foreach (var fightDir in Directory.GetDirectories(basePath).OrderBy(d => d))
                {
                    var strategy = new FightStrategy(new DirectoryInfo(fightDir).Name, sourceType, fightDir);

                    // Metadata ID loading for Plugin strategies is handled by LoadEmbeddedPluginStrategies
                    // For User strategies, territory_id.txt is not currently supported but could be added here if desired.

                    foreach (var phaseDir in Directory.GetDirectories(fightDir).OrderBy(d => d))
                    {
                        var phase = new FightPhase(new DirectoryInfo(phaseDir).Name);
                        foreach (var imageFile in Directory.GetFiles(phaseDir).OrderBy(f => f))
                        {
                            if (IsSupportedImageFile(imageFile)) { phase.Images.Add(new ImageAsset(imageFile)); }
                        }
                        if (phase.Images.Any()) { strategy.Phases.Add(phase); }
                    }

                    if (strategy.Phases.Any())
                    {
                        if (!AllStrategies.Any(s => s.Name == strategy.Name && s.Source == strategy.Source)) AllStrategies.Add(strategy);
                        else { Log.Warning($"Strategy '{strategy.Name}' from source '{strategy.Source}' already exists. Skipping."); strategy.Dispose(); }
                    }
                    else { strategy.Dispose(); }
                }
            }
            catch (Exception ex) { Log.Error($"Error scanning {basePath}: {ex.Message}"); }
        }

        private bool IsSupportedImageFile(string filePath) // Used only by ScanDirectoryForStrategies (ImageSourceType.User)
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

        // Updated to handle both file paths (for User images) and resource names (for Plugin images)
        // The 'identifier' will be a file path for User source, and a manifest resource name for Plugin source.
        public IDalamudTextureWrap? LoadTextureFromFile(string identifier)
        {
            // Heuristic: If it contains "PluginImages" and ".png" (or other extensions) and matches the resource prefix,
            // it's likely an embedded resource. Otherwise, assume it's a file path.
            // A more robust way would be to pass ImageSourceType if available from FightStrategy.
            bool isEmbeddedResource = identifier.StartsWith(_resourcePathPrefix);

            if (isEmbeddedResource)
            {
                Log.Debug($"Loading embedded resource: {identifier}");
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using var stream = assembly.GetManifestResourceStream(identifier);
                    if (stream == null)
                    {
                        Log.Warning($"Embedded resource not found: {identifier}");
                        return null;
                    }

                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
                    var rgbaBytes = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(rgbaBytes);
                    return TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to load embedded resource {identifier}: {ex.Message}");
                    return null;
                }
            }
            else // Assume it's a file path for User source
            {
                Log.Debug($"Loading texture from file path: {identifier}");
                if (string.IsNullOrEmpty(identifier) || !File.Exists(identifier)) { Log.Warning($"File not found: {identifier}"); return null; }
                try
                {
                    string extension = Path.GetExtension(identifier).ToLowerInvariant();
                    // ImageSharp for common web formats, TextureProvider for others like .tga, .dds
                    if (new[] { ".webp", ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(extension))
                    {
                        try
                        {
                            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(identifier);
                            var rgbaBytes = new byte[image.Width * image.Height * 4];
                            image.CopyPixelDataTo(rgbaBytes);
                            return TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);
                        }
                        catch (Exception ex) { Log.Error($"ImageSharp failed for {Path.GetFileName(identifier)}: {ex.Message}"); return null; }
                    }
                    else
                    {
                        var texture = TextureProvider.GetFromFile(identifier); // Handles .dds, .tga etc.
                        return texture?.GetWrapOrDefault();
                    }
                }
                catch (Exception ex) { Log.Error($"Generic texture load exception for {Path.GetFileName(identifier)}: {ex.Message}"); return null; }
            }
        }


        private void OnCommand(string command, string args)
        {
            string trimmedArgs = args.Trim();
            var mainWindowInstance = WindowSystem.Windows.FirstOrDefault(w => w.WindowName == MainWindowName) as Windows.MainWindow ?? this.MainWindow;

            if (trimmedArgs.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                ReloadStrategies();
            }
            else
            {
                string? strategyNameToSelect = null;
                ImageSourceType? sourceFilter = null;

                if (string.IsNullOrEmpty(trimmedArgs))
                {
                    ushort currentTerritoryId = ClientState.TerritoryType;
                    if (currentTerritoryId != 0)
                    {
                        // 1. Attempt to select via Metadata ID for Plugin (embedded) strategies
                        foreach (var strategy in AllStrategies.Where(s => s.Source == ImageSourceType.Plugin))
                        {
                            if (strategy.MetadataTerritoryTypeId.HasValue &&
                                strategy.MetadataTerritoryTypeId.Value == currentTerritoryId)
                            {
                                strategyNameToSelect = strategy.Name;
                                sourceFilter = ImageSourceType.Plugin;
                                Log.Info($"WDIGViewer: Matched embedded strategy '{strategy.Name}' via metadata Territory ID: {currentTerritoryId}");
                                break;
                            }
                        }

                        // 2. If no metadata match, attempt to select via Duty/Zone Name (existing logic for any source)
                        if (string.IsNullOrEmpty(strategyNameToSelect))
                        {
                            string? nameFromGameData = null;
                            if (DutyState.IsDutyStarted)
                            {
                                var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
                                if (territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryEntry) && territoryEntry.RowId > 0)
                                {
                                    var cfcFromTerritoryRowRef = territoryEntry.ContentFinderCondition;
                                    if (cfcFromTerritoryRowRef.RowId > 0)
                                    {
                                        var cfcSheet = DataManager.GetExcelSheet<ContentFinderCondition>();
                                        if (cfcSheet != null && cfcSheet.TryGetRow(cfcFromTerritoryRowRef.RowId, out var cfcEntry) && cfcEntry.RowId > 0)
                                        {
                                            if (!cfcEntry.Name.IsEmpty)
                                            {
                                                nameFromGameData = cfcEntry.Name.ToString();
                                                Log.Info($"WDIGViewer: Attempting auto-select for active duty (via Territory's CFC): \"{nameFromGameData}\" (Territory ID: {currentTerritoryId}, CFC ID: {cfcEntry.RowId})");
                                            }
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(nameFromGameData))
                            {
                                var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
                                if (territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryEntry) && territoryEntry.RowId > 0)
                                {
                                    var placeNameRef = territoryEntry.PlaceName;
                                    if (placeNameRef.RowId > 0)
                                    {
                                        PlaceName placeNameActual = placeNameRef.Value;
                                        if (placeNameActual.RowId > 0 && !placeNameActual.Name.IsEmpty)
                                        {
                                            nameFromGameData = placeNameActual.Name.ToString();
                                            Log.Info($"WDIGViewer: Attempting auto-select for zone: \"{nameFromGameData}\" (Territory ID: {currentTerritoryId})");
                                        }
                                    }
                                }
                            }
                            strategyNameToSelect = nameFromGameData;
                        }
                    }
                }
                else
                {
                    strategyNameToSelect = trimmedArgs;
                    Log.Info($"WDIGViewer: Attempting to select strategy by argument: \"{strategyNameToSelect}\"");
                }

                if (!string.IsNullOrEmpty(strategyNameToSelect))
                {
                    // Pass sourceFilter if specific, otherwise it's null and SelectStrategyByName searches all
                    mainWindowInstance?.SelectStrategyByName(strategyNameToSelect, sourceFilter);
                }
            }
            ToggleMainUI();
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
