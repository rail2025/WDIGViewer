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

namespace WDIGViewer
{
    public sealed class Plugin : IDalamudPlugin // IDalamudPlugin implies IDisposable
    {
        public string Name => "WDIGViewer";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

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
            Configuration.Initialize(PluginInterface); // Correct initialization
            LoadStrategies();
            ConfigWindow = new Windows.ConfigWindow(this);
            MainWindow = new Windows.MainWindow(this, AllStrategies);
            WindowSystem.AddWindow(ConfigWindow);
            WindowSystem.AddWindow(MainWindow);
            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            { HelpMessage = "Opens WDIGViewer. Use '/wdig reload' to rescan images." });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
            Log.Information("WDIGViewer Plugin Loaded.");
        }

        public void ReloadStrategies()
        {
            Log.Information("Reloading strategies...");
            foreach (var strategy in AllStrategies) strategy.Dispose();
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
            if (Directory.Exists(pluginImageDir)) ScanDirectoryForStrategies(pluginImageDir, ImageSourceType.Plugin);
            else { Log.Warning($"Plugin image dir not found: {pluginImageDir}. Creating."); try { Directory.CreateDirectory(pluginImageDir); } catch (Exception ex) { Log.Error($"Could not create {pluginImageDir}: {ex.Message}"); } }
            if (!string.IsNullOrEmpty(Configuration.UserImageDirectory) && Directory.Exists(Configuration.UserImageDirectory))
            { ScanDirectoryForStrategies(Configuration.UserImageDirectory, ImageSourceType.User); }
            else if (!string.IsNullOrEmpty(Configuration.UserImageDirectory)) Log.Warning($"User image dir not found: {Configuration.UserImageDirectory}");
        }

        private void ScanDirectoryForStrategies(string basePath, ImageSourceType sourceType)
        {
            try
            {
                Log.Info($"Scanning base path: {basePath} for source type: {sourceType}");
                foreach (var fightDir in Directory.GetDirectories(basePath))
                {
                    Log.Info($"Found fight directory: {fightDir}");
                    var strategy = new FightStrategy(new DirectoryInfo(fightDir).Name, sourceType, fightDir);
                    var phaseDirs = Directory.GetDirectories(fightDir).OrderBy(d => d).ToList();
                    Log.Info($"-- Found {phaseDirs.Count} potential phase directories for {strategy.Name}");

                    foreach (var phaseDir in phaseDirs)
                    {
                        Log.Info($"---- Processing phase directory: {phaseDir}");
                        var phase = new FightPhase(new DirectoryInfo(phaseDir).Name);
                        var imageFilesInDir = Directory.GetFiles(phaseDir).ToList();
                        Log.Info($"------ Found {imageFilesInDir.Count} files in {phaseDir} before filtering.");

                        foreach (var imageFile in imageFilesInDir)
                        {
                            Log.Debug($"-------- Checking file: {imageFile}");
                            bool isSupported = IsSupportedImageFile(imageFile);
                            Log.Debug($"-------- IsSupportedFile('{imageFile}') returned: {isSupported}");
                            if (isSupported)
                            {
                                phase.Images.Add(new ImageAsset(imageFile));
                            }
                        }
                        Log.Info($"------ Phase '{phase.Name}' has {phase.Images.Count} supported images.");

                        if (phase.Images.Any())
                        {
                            strategy.Phases.Add(phase);
                            Log.Info($"------ Added phase '{phase.Name}' to strategy '{strategy.Name}'.");
                        }
                        else
                        {
                            Log.Info($"------ Skipped adding phase '{phase.Name}' (no supported images) to strategy '{strategy.Name}'.");
                        }
                    }
                    Log.Info($"-- Strategy '{strategy.Name}' has {strategy.Phases.Count} total phases added.");
                    if (strategy.Phases.Any())
                    {
                        AllStrategies.Add(strategy);
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Error scanning {basePath} for {sourceType}: {ex.Message}"); }
        }

        private bool IsSupportedImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!(ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif"))
                return false;
            try
            {
                using var stream = File.OpenRead(filePath);
                return SixLabors.ImageSharp.Image.DetectFormat(stream) != null;
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not detect format for {filePath}, assuming unsupported: {ex.Message}");
                return false;
            }
        }

        public IDalamudTextureWrap? LoadTextureFromFile(string filePath)
        {
            try
            {
                string extension = Path.GetExtension(filePath).ToLowerInvariant();

                if (extension == ".webp")
                {
                    Log.Verbose($"Attempting to load WebP image using ImageSharp: {filePath}");
                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(filePath);
                    // image.Mutate(x => x.Flip(FlipMode.Vertical)); // Often not needed with CreateFromRaw

                    var rgbaBytes = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(rgbaBytes);

                    var textureWrap = TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), rgbaBytes);

                    if (textureWrap != null)
                    {
                        Log.Verbose($"Successfully loaded WebP (via ImageSharp & CreateFromRaw): {filePath}");
                    }
                    else
                    {
                        Log.Warning($"TextureProvider.CreateFromRaw failed for WebP: {filePath}");
                    }
                    return textureWrap;
                }
                else
                {
                    Log.Verbose($"Attempting to load standard image using TextureProvider: {filePath}");
                    var texture = TextureProvider.GetFromFile(filePath);
                    IDalamudTextureWrap? textureWrap = texture?.GetWrapOrDefault();

                    if (textureWrap == null)
                    {
                        Log.Warning($"TextureProvider failed to get/wrap texture from: {filePath}. Format might not be supported or image is invalid.");
                    }
                    else
                    {
                        Log.Verbose($"Successfully loaded standard image: {filePath}");
                    }
                    return textureWrap;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Exception loading texture from {filePath}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                return null;
            }
        }

        public void Dispose()
        {
            WindowSystem.RemoveAllWindows();
            foreach (var strategy in AllStrategies) strategy.Dispose();
            AllStrategies.Clear();
            CommandManager.RemoveHandler(CommandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
            Log.Information("WDIGViewer Plugin Unloaded.");
        }

        private void OnCommand(string command, string args)
        {
            if (args.Trim().ToLower() == "reload") ReloadStrategies();
            // Ensure MainWindow is accessible. If it's null due to an early error, this could fail.
            // However, it should be initialized in the constructor.
            var mainWindowInstance = WindowSystem.Windows.FirstOrDefault(w => w.WindowName == MainWindowName) as Windows.MainWindow;
            if (mainWindowInstance != null)
            {
                mainWindowInstance.IsOpen = true;
            }
            else if (this.MainWindow != null) // Fallback to the direct reference if not found in WindowSystem (shouldn't happen if added)
            {
                this.MainWindow.IsOpen = true;
            }
            else
            {
                Log.Error("MainWindow instance is null, cannot open.");
            }
        }

        private void DrawUI() => WindowSystem.Draw();
        public void ToggleConfigUI() => ConfigWindow?.Toggle(); // Added null check for safety
        public void ToggleMainUI() => MainWindow?.Toggle();   // Added null check for safety
    }
} // This is the closing brace for the namespace WDIGViewer
