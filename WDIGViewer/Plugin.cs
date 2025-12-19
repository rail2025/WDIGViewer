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
using Lumina.Excel.Sheets;      // For TerritoryType, ContentFinderCondition etc.
using Lumina.Text;             // Required for SeString (ReadOnlySeString)
using System.Text.RegularExpressions;

namespace WDIGViewer
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "WDIGViewer";

        // Dalamud Services
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

        // Constants
        private const string CommandName = "/wdig";
        private const string PluginImageFolderName = "PluginImages"; // Used to construct resource paths
        private const string MainWindowName = "WDIGViewer##WDIGViewerMain"; // Unique ID for the main window

        // The root path for embedded image resources within the assembly.
        private readonly string resourcePathPrefix;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// Sets up configuration, loads initial strategies, initializes windows, and registers commands.
        /// </summary>
        public Plugin()
        {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(PluginInterface);

            string rootNamespace = Assembly.GetExecutingAssembly().GetName().Name ?? "WDIGViewer";
            resourcePathPrefix = $"{rootNamespace}.{PluginImageFolderName}.";

            LoadStrategies();

            ConfigWindow = new Windows.ConfigWindow(this);
            MainWindow = new Windows.MainWindow(this, AllStrategies);

            WindowSystem.AddWindow(ConfigWindow);
            WindowSystem.AddWindow(MainWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            { HelpMessage = "Opens WDIGViewer. Use '/wdig reload' to rescan images. '/wdig <strategy name>' to attempt to open specific strategy." });

            // Subscribe to Dalamud UI events
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

            Log.Information("WDIGViewer Plugin Loaded.");
        }

        /// <summary>
        /// Reloads all fight strategies from embedded resources and user-defined directories.
        /// Clears existing strategies and rescans. Updates the main window.
        /// </summary>
        public void ReloadStrategies()
        {
            Log.Information("Reloading strategies...");
            foreach (var strategy in AllStrategies) { strategy.Dispose(); }
            AllStrategies.Clear();

            LoadStrategies();
            MainWindow.UpdateStrategies(AllStrategies); // Notify the main window of the changes
            Log.Information("Strategies reloaded.");
        }

        /// <summary>
        /// Loads strategies from both embedded plugin resources and user-specified directories.
        /// </summary>
        private void LoadStrategies()
        {
            LoadEmbeddedPluginStrategies();

            if (!string.IsNullOrEmpty(Configuration.UserImageDirectory) && Directory.Exists(Configuration.UserImageDirectory))
            {
                ScanDirectoryForStrategies(Configuration.UserImageDirectory, ImageSourceType.User);
            }
            else if (!string.IsNullOrEmpty(Configuration.UserImageDirectory))
            {
                // Log a warning if the user-specified directory is set but not found.
                Log.Warning($"User image directory not found: {Configuration.UserImageDirectory}");
            }
        }

        /// <summary>
        /// Converts mangled resource name segments (used for folder/file names that become part of resource paths)
        /// back to a more display-friendly format. E.g., "My___Fight_Strategy" becomes "My - Fight Strategy".
        /// </summary>
        /// <param name="segment">The mangled segment string.</param>
        /// <returns>An unmangled string suitable for display.</returns>
        private string UnmangleResourceSegment(string segment)
        {
            return segment.Replace("___", " - ").Replace("_", " ");
        }

        /// <summary>
        /// Scans the executing assembly for embedded resources that represent plugin-defined fight strategies.
        /// Populates the AllStrategies list with found strategies.
        /// Expected resource structure: Namespace.PluginImageFolderName.StrategyName.PhaseName.ImageFile.ext
        /// Also looks for an optional "territory_id.txt" in each strategy's resource path for auto-matching.
        /// </summary>
        private void LoadEmbeddedPluginStrategies()
        {
            Log.Info($"Scanning embedded resources for Plugin strategies with prefix: {resourcePathPrefix}");
            var assembly = Assembly.GetExecutingAssembly();
            var allResourceNames = assembly.GetManifestResourceNames();

            // Group resources by the first segment after the prefix (assumed to be the mangled strategy name)
            var resourcesByMangledStrategy = new Dictionary<string, List<string>>();
            foreach (var resourceName in allResourceNames)
            {
                if (resourceName.StartsWith(resourcePathPrefix))
                {
                    string pathAfterPrefix = resourceName.Substring(resourcePathPrefix.Length);
                    string[] parts = pathAfterPrefix.Split(new[] { '.' }, 2); // Split only on the first dot to get strategy segment
                    if (parts.Length > 0)
                    {
                        string mangledStrategySegment = parts[0];
                        if (!string.IsNullOrEmpty(mangledStrategySegment))
                        {
                            if (!resourcesByMangledStrategy.ContainsKey(mangledStrategySegment))
                            {
                                resourcesByMangledStrategy[mangledStrategySegment] = new List<string>();
                            }
                            resourcesByMangledStrategy[mangledStrategySegment].Add(resourceName);
                        }
                    }
                }
            }

            foreach (var stratEntry in resourcesByMangledStrategy.OrderBy(kvp => kvp.Key)) // Process strategies alphabetically by mangled name
            {
                string actualMangledStrategyName = stratEntry.Key;
                string strategyDisplayName = UnmangleResourceSegment(actualMangledStrategyName);
                string strategyResourcePathBase = resourcePathPrefix + actualMangledStrategyName + "."; // Base path for resources under this strategy

                var strategy = new FightStrategy(strategyDisplayName, ImageSourceType.Plugin, strategyResourcePathBase);

                // Attempt to load optional territory ID metadata
                string territoryIdResourceFullName = strategyResourcePathBase + "territory_id.txt";
                if (stratEntry.Value.Any(r => r.Equals(territoryIdResourceFullName, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(territoryIdResourceFullName);
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            string content = reader.ReadToEnd().Trim();
                            if (ushort.TryParse(content, out ushort territoryId))
                            {
                                strategy.MetadataTerritoryTypeId = territoryId;
                                Log.Info($"Loaded embedded metadata Territory ID {territoryId} for strategy '{strategyDisplayName}' (Mangled: '{actualMangledStrategyName}')");
                            }
                            else
                            {
                                Log.Warning($"Could not parse embedded Territory ID for strategy '{strategyDisplayName}'. Content: '{content}' from '{territoryIdResourceFullName}'");
                            }
                        }
                        else
                        {
                            Log.Warning($"Stream was null for territory ID resource: {territoryIdResourceFullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error reading embedded metadata for strategy '{strategyDisplayName}' from '{territoryIdResourceFullName}': {ex.Message}");
                    }
                }

                // Group remaining resources by phase name
                var phaseResources = new Dictionary<string, List<string>>();
                foreach (var fullResourceName in stratEntry.Value)
                {
                    if (fullResourceName.Equals(territoryIdResourceFullName, StringComparison.OrdinalIgnoreCase)) continue; // Skip metadata file

                    if (fullResourceName.StartsWith(strategyResourcePathBase))
                    {
                        string pathAfterStrategy = fullResourceName.Substring(strategyResourcePathBase.Length);
                        string[] parts = pathAfterStrategy.Split('.'); // parts are [PhaseName, ImageName, Ext] or similar

                        if (parts.Length >= 2) // Expect at least MangledPhaseName.ImageFileWithExtension
                        {
                            string mangledPhaseSegment = parts[0];
                            string phaseDisplayName = UnmangleResourceSegment(mangledPhaseSegment);

                            // Identify image files by common extensions
                            string fileExtension = parts.Last().ToLowerInvariant();
                            if (new[] { "png", "webp", "jpg", "jpeg", "bmp", "gif", "tga" }.Contains(fileExtension))
                            {
                                if (!phaseResources.ContainsKey(phaseDisplayName))
                                {
                                    phaseResources[phaseDisplayName] = new List<string>();
                                }
                                phaseResources[phaseDisplayName].Add(fullResourceName); // Store the full resource name as FilePath
                            }
                        }
                        else
                        {
                            Log.Warning($"Skipping resource, unexpected structure for image: {fullResourceName}. Expected Phase.Image.Ext after strategy path: {strategyResourcePathBase}");
                        }
                    }
                }

                // Create phases and add images
                foreach (var phaseEntry in phaseResources.OrderBy(kvp => kvp.Key)) // Process phases alphabetically
                {
                    var phase = new FightPhase(phaseEntry.Key);
                    foreach (var imageResourceName in phaseEntry.Value.OrderBy(f => Regex.Replace(f, @"\d+", m => m.Value.PadLeft(10, '0')))) // Add images in numerical alphabetical order of their resource name
                    {
                        phase.Images.Add(new ImageAsset(imageResourceName));
                    }
                    if (phase.Images.Any()) strategy.Phases.Add(phase);
                }

                // Add strategy if it has phases or if it has metadata (even if no phases, for potential future use)
                if (strategy.Phases.Any() || strategy.MetadataTerritoryTypeId.HasValue)
                {
                    if (!AllStrategies.Any(s => s.Name == strategy.Name && s.Source == strategy.Source))
                    {
                        AllStrategies.Add(strategy);
                        if (strategy.Phases.Any())
                            Log.Info($"Successfully loaded embedded strategy: '{strategyDisplayName}' with {strategy.Phases.Count} phase(s). Metadata loaded: {strategy.MetadataTerritoryTypeId.HasValue}");
                        else if (strategy.MetadataTerritoryTypeId.HasValue) // Has metadata but no image phases
                            Log.Info($"Loaded embedded strategy '{strategyDisplayName}' with metadata ID {strategy.MetadataTerritoryTypeId.Value} but no image phases.");

                    }
                    else
                    {
                        Log.Warning($"Embedded Strategy '{strategy.Name}' (from mangled '{actualMangledStrategyName}') already exists. Skipping.");
                        strategy.Dispose(); // Dispose if duplicate
                    }
                }
                else
                {
                    // No phases and no metadata, unlikely to be useful
                    Log.Warning($"Strategy '{strategyDisplayName}' (from mangled '{actualMangledStrategyName}') has no phases and no territory ID. Skipping.");
                    strategy.Dispose();
                }
            }
        }

        /// <summary>
        /// Scans a given directory for fight strategies organized in subfolders.
        /// Expected structure: basePath/StrategyName/PhaseName/image.[ext]
        /// </summary>
        /// <param name="basePath">The root directory to scan.</param>
        /// <param name="sourceType">The source type of these strategies (e.g., User).</param>
        private void ScanDirectoryForStrategies(string basePath, ImageSourceType sourceType)
        {
            try
            {
                Log.Info($"Scanning base path: {basePath} for source type: {sourceType}");
                // Iterate over subdirectories in the base path (each is a Strategy)
                foreach (var fightDir in Directory.GetDirectories(basePath).OrderBy(d => d))
                {
                    var strategy = new FightStrategy(new DirectoryInfo(fightDir).Name, sourceType, fightDir);
                    // Iterate over subdirectories in the strategy folder (each is a Phase)
                    foreach (var phaseDir in Directory.GetDirectories(fightDir).OrderBy(d => d))
                    {
                        var phase = new FightPhase(new DirectoryInfo(phaseDir).Name);
                        // Iterate over files in the phase folder (each is an Image)
                        foreach (var imageFile in Directory.GetFiles(phaseDir).OrderBy(f => Regex.Replace(f, @"\d+", m => m.Value.PadLeft(10, '0'))))
                        {
                            if (IsSupportedImageFile(imageFile))
                            {
                                phase.Images.Add(new ImageAsset(imageFile));
                            }
                        }
                        if (phase.Images.Any()) { strategy.Phases.Add(phase); }
                    }

                    if (strategy.Phases.Any())
                    {
                        if (!AllStrategies.Any(s => s.Name == strategy.Name && s.Source == strategy.Source))
                        {
                            AllStrategies.Add(strategy);
                        }
                        else
                        {
                            Log.Warning($"User Strategy '{strategy.Name}' from source '{strategy.Source}' already exists. Skipping.");
                            strategy.Dispose(); // Dispose if duplicate
                        }
                    }
                    else { strategy.Dispose(); } // Dispose if no phases were found
                }
            }
            catch (Exception ex) { Log.Error($"Error scanning {basePath}: {ex.Message}"); }
        }

        /// <summary>
        /// Checks if a given file path points to a supported image file format.
        /// </summary>
        /// <param name="filePath">The path to the file.</param>
        /// <returns>True if the file is a supported image type, false otherwise.</returns>
        private bool IsSupportedImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            // Check against a list of common supported extensions
            if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tga" }.Contains(ext)) return false;

            // Further validation by trying to detect format using ImageSharp (more reliable)
            try
            {
                using var stream = File.OpenRead(filePath);
                return SixLabors.ImageSharp.Image.DetectFormat(stream) != null;
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not detect image format for {Path.GetFileName(filePath)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Loads a texture from either an embedded resource or the file system.
        /// </summary>
        /// <param name="identifier">For embedded resources, this is the manifest resource name. For file system, this is the full file path.</param>
        /// <returns>An <see cref="IDalamudTextureWrap"/> if successful, otherwise null.</returns>
        public IDalamudTextureWrap? LoadTextureFromFile(string identifier)
        {
            // Enable the following Log.Debug lines if troubleshooting texture loading issues.
            // Log.Debug($"[LOAD TEXTURE ATTEMPT] Identifier received: '{identifier}'");

            bool isEmbeddedResource = !string.IsNullOrEmpty(identifier) && identifier.StartsWith(resourcePathPrefix);

            if (isEmbeddedResource)
            {
                // Log.Debug($"[LOAD TEXTURE ATTEMPT] Entering EMBEDDED resource path for: '{identifier}'"); // Enable for debug
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using var stream = assembly.GetManifestResourceStream(identifier);
                    if (stream == null)
                    {
                        Log.Warning($"[EMBEDDED] Embedded resource stream NOT FOUND (stream was null): {identifier}");
                        return null;
                    }
                    if (stream.Length == 0)
                    {
                        Log.Warning($"[EMBEDDED] Embedded resource stream IS EMPTY: {identifier}");
                        return null;
                    }

                    // Log.Debug($"[EMBEDDED] Stream found for {identifier}, Length: {stream.Length}. Attempting Image.Load..."); // Enable for debug
                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
                    // Log.Debug($"[EMBEDDED] Image.Load successful for {identifier}. Width: {image.Width}, Height: {image.Height}"); // Enable for debug

                    if (image.Width == 0 || image.Height == 0)
                    {
                        Log.Warning($"[EMBEDDED] Image loaded but has zero dimensions: {identifier}");
                        return null;
                    }

                    var rgbaBytes = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(rgbaBytes);
                    return TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);
                }
                catch (Exception ex)
                {
                    Log.Error($"[EMBEDDED] Failed to load embedded resource {identifier}: {ex.GetType().Name} - {ex.Message} - StackTrace: {ex.StackTrace}");
                    return null;
                }
            }
            else // File system path
            {
                // Log.Debug($"[LOAD TEXTURE ATTEMPT] Entering FILE SYSTEM path for: '{identifier}'"); // Enable for debug
                if (string.IsNullOrEmpty(identifier) || !File.Exists(identifier))
                {
                    Log.Warning($"[FILE SYSTEM] File not found: {identifier}");
                    return null;
                }
                try
                {
                    string extension = Path.GetExtension(identifier).ToLowerInvariant();
                    // Prioritize ImageSharp for common web/editable formats for better compatibility and control
                    if (new[] { ".webp", ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(extension))
                    {
                        try
                        {
                            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(identifier);
                            if (image.Width == 0 || image.Height == 0)
                            {
                                Log.Warning($"[FILE SYSTEM] Image loaded but has zero dimensions: {identifier}");
                                return null;
                            }
                            var rgbaBytes = new byte[image.Width * image.Height * 4];
                            image.CopyPixelDataTo(rgbaBytes);
                            // Log.Debug($"[FILE SYSTEM] ImageSharp load successful for {Path.GetFileName(identifier)}"); // Enable for debug
                            return TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[FILE SYSTEM] ImageSharp failed for {Path.GetFileName(identifier)}: {ex.Message}");
                            return null; // Fallback or failure
                        }
                    }
                    else // For other formats (like .tga, .dds potentially handled by TextureProvider directly)
                    {
                        var texture = TextureProvider.GetFromFile(identifier);
                        if (texture == null) Log.Warning($"[FILE SYSTEM] TextureProvider.GetFromFile returned null for {identifier}");
                        // else Log.Debug($"[FILE SYSTEM] TextureProvider.GetFromFile successful for {identifier}"); // Enable for debug
                        return texture?.GetWrapOrDefault();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[FILE SYSTEM] Generic texture load exception for {Path.GetFileName(identifier)}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Handles the execution of the "/wdig" command.
        /// Can toggle the main UI, reload strategies, or attempt to open a specific strategy.
        /// </summary>
        /// <param name="command">The command string.</param>
        /// <param name="args">The arguments passed to the command.</param>
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

                if (string.IsNullOrEmpty(trimmedArgs)) // No arguments, try auto-selection
                {
                    ushort currentTerritoryId = ClientState.TerritoryType;
                    if (currentTerritoryId != 0)
                    {
                        // Attempt to match Plugin strategies via metadata Territory ID first
                        foreach (var strategy in AllStrategies.Where(s => s.Source == ImageSourceType.Plugin))
                        {
                            if (strategy.MetadataTerritoryTypeId.HasValue)
                            {
                                if (strategy.MetadataTerritoryTypeId.Value == currentTerritoryId)
                                {
                                    strategyNameToSelect = strategy.Name;
                                    sourceFilter = ImageSourceType.Plugin;
                                    Log.Info($"WDIGViewer: Matched embedded strategy '{strategy.Name}' via metadata Territory ID: {currentTerritoryId}");
                                    break;
                                }
                            }
                        }

                        // If no plugin strategy matched, try User strategies via metadata
                        if (string.IsNullOrEmpty(strategyNameToSelect))
                        {
                            foreach (var strategy in AllStrategies.Where(s => s.Source == ImageSourceType.User))
                            {
                                if (strategy.MetadataTerritoryTypeId.HasValue)
                                {
                                    if (strategy.MetadataTerritoryTypeId.Value == currentTerritoryId)
                                    {
                                        strategyNameToSelect = strategy.Name;
                                        sourceFilter = ImageSourceType.User;
                                        Log.Info($"WDIGViewer: Matched user strategy '{strategy.Name}' via metadata Territory ID: {currentTerritoryId}");
                                        break;
                                    }
                                }
                            }
                        }

                        // If still no match by metadata, try to match by game data name (Duty or Zone name)
                        if (string.IsNullOrEmpty(strategyNameToSelect))
                        {
                            string? nameFromGameData = null;
                            // Try Duty Name first if in a duty
                            if (DutyState.IsDutyStarted)
                            {
                                var territorySheet = DataManager.GetExcelSheet<TerritoryType>();
                                if (territorySheet != null && territorySheet.TryGetRow(currentTerritoryId, out var territoryEntry) && territoryEntry.RowId > 0)
                                {
                                    var cfcFromTerritoryRowRef = territoryEntry.ContentFinderCondition;
                                    if (cfcFromTerritoryRowRef.RowId > 0)
                                    {
                                        var cfcSheet = DataManager.GetExcelSheet<ContentFinderCondition>();
                                        if (cfcSheet != null && cfcSheet.TryGetRow(cfcFromTerritoryRowRef.RowId, out var cfcEntry) && cfcEntry.RowId > 0 && !cfcEntry.Name.IsEmpty)
                                        {
                                            nameFromGameData = cfcEntry.Name.ToString();
                                            Log.Info($"WDIGViewer: Current Duty Name for auto-select: \"{nameFromGameData}\" (Territory ID: {currentTerritoryId}, CFC ID: {cfcEntry.RowId})");
                                        }
                                    }
                                }
                            }

                            // If not in a duty or duty name not found/matched, try Zone Name
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
                                            Log.Info($"WDIGViewer: Current Zone Name for auto-select: \"{nameFromGameData}\" (Territory ID: {currentTerritoryId})");
                                        }
                                    }
                                }
                            }

                            // If a name was found from game data, try to match it against loaded strategies
                            if (!string.IsNullOrEmpty(nameFromGameData))
                            {
                                var matchedStrategy = AllStrategies.FirstOrDefault(s => s.Name.Equals(nameFromGameData, StringComparison.OrdinalIgnoreCase)) ??
                                                    AllStrategies.FirstOrDefault(s => s.Name.IndexOf(nameFromGameData, StringComparison.OrdinalIgnoreCase) >= 0); // Fallback to partial match
                                if (matchedStrategy != null)
                                {
                                    strategyNameToSelect = matchedStrategy.Name;
                                    sourceFilter = matchedStrategy.Source;
                                    Log.Info($"WDIGViewer: Auto-selected strategy '{strategyNameToSelect}' (Source: {sourceFilter}) based on game data name match.");
                                }
                                else
                                {
                                    Log.Info($"WDIGViewer: Game data name '{nameFromGameData}' found, but no matching strategy name in AllStrategies.");
                                }
                            }
                        }
                    }
                }
                else // Arguments provided, attempt to select strategy by name from args
                {
                    strategyNameToSelect = trimmedArgs;
                    // Source filter is not used here, as the argument might uniquely identify the strategy or user expects global search by name.
                    Log.Info($"WDIGViewer: Attempting to select strategy by argument: \"{strategyNameToSelect}\"");
                }

                // If a strategy was determined for selection, select it in the main window
                if (!string.IsNullOrEmpty(strategyNameToSelect))
                {
                    mainWindowInstance?.SelectStrategyByName(strategyNameToSelect, sourceFilter);
                }
            }
            ToggleMainUI(); // Always toggle main UI after command execution
        }

        /// <summary>
        /// Draws all registered windows in the WindowSystem.
        /// </summary>
        private void DrawUI() => WindowSystem.Draw();

        /// <summary> Toggles the visibility of the configuration window. </summary>
        public void ToggleConfigUI() => ConfigWindow?.Toggle();

        /// <summary> Toggles the visibility of the main application window. </summary>
        public void ToggleMainUI() => MainWindow?.Toggle();

        /// <summary>
        /// Disposes of resources used by the plugin.
        /// Removes windows, disposes strategies, unregisters commands, and unsubscribes from UI events.
        /// </summary>
        public void Dispose()
        {
            WindowSystem.RemoveAllWindows();

            foreach (var strategy in AllStrategies)
            {
                strategy.Dispose();
            }
            AllStrategies.Clear();

            CommandManager.RemoveHandler(CommandName);

            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;

            Log.Information("WDIGViewer Plugin Unloaded and Disposed.");
        }
    }
}
