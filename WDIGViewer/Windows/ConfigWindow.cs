using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using Dalamud.Interface.ImGuiFileDialog; // Correct using for FileDialogManager
using Dalamud.Utility; // Added for Util.OpenLink

namespace WDIGViewer.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Plugin plugin;
        private readonly Configuration configuration;
        private readonly FileDialogManager fileDialogManager; // Use the concrete class

        public ConfigWindow(Plugin plugin)
            : base("WDIGViewer Configuration###WDIGViewerConfig")
        {
            this.plugin = plugin;
            this.configuration = plugin.Configuration;
            this.fileDialogManager = new FileDialogManager(); // Instantiate here

            this.Flags = ImGuiWindowFlags.NoResize |
                         ImGuiWindowFlags.NoCollapse |
                         ImGuiWindowFlags.NoScrollbar |
                         ImGuiWindowFlags.NoScrollWithMouse;

            this.Size = new Vector2(450, 260); // Increased height for the new button
            this.SizeCondition = ImGuiCond.Always;
        }

        public void Dispose()
        {
            // It's good practice to call Reset if the FileDialogManager has such a method,
            // or ensure it's disposed of if it implements IDisposable, though it typically manages its own lifecycle.
            // For now, if Reset() isn't a method, this can be omitted or checked against FileDialogManager's API.
            // this.fileDialogManager.Reset(); 
        }

        public override void Draw()
        {
            ImGui.Text("User Image Directory:");
            float browseButtonWidth = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
            float reloadButtonWidth = ImGui.CalcTextSize("Reload Images").X + ImGui.GetStyle().FramePadding.X * 2;
            float inputWidth = ImGui.GetContentRegionAvail().X - browseButtonWidth - reloadButtonWidth - (ImGui.GetStyle().ItemSpacing.X * 2);

            ImGui.PushItemWidth(inputWidth);
            var userImageDir = configuration.UserImageDirectory ?? string.Empty;
            if (ImGui.InputText("##UserImageDirectory", ref userImageDir, 256))
            {
                configuration.UserImageDirectory = userImageDir;
                configuration.Save();
            }
            ImGui.PopItemWidth();

            ImGui.SameLine();
            if (ImGui.Button("Browse..."))
            {
                this.fileDialogManager.OpenFolderDialog(
                    "Select Custom Image Strategy Folder",
                    (isSelected, selectedPath) =>
                    {
                        if (isSelected && !string.IsNullOrEmpty(selectedPath))
                        {
                            configuration.UserImageDirectory = selectedPath;
                            configuration.Save();
                        }
                    },
                    string.IsNullOrWhiteSpace(configuration.UserImageDirectory) ? null : configuration.UserImageDirectory, //
                    true
                );
            }

            ImGui.SameLine();
            if (ImGui.Button("Reload Images"))
            {
                plugin.ReloadStrategies();
            }

            ImGui.Spacing();
            ImGui.TextWrapped("Set the root path to your custom image folder.");
            ImGui.TextWrapped("Expected structure: YourFolder/StrategyName/PhaseName/image.[ext]");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Support Button
            string buttonText = "Donate & Support";
            uint buttonColor = 0xFF312B; // A reddish color
            float btnWidthFull = ImGui.GetContentRegionAvail().X;

            ImGui.PushStyleColor(ImGuiCol.Button, 0xFF000000 | buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xDD000000 | buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xAA000000 | buttonColor);

            if (ImGui.Button(buttonText))
            {
                Util.OpenLink("https://ko-fi.com/rail2025");
            }

            ImGui.PopStyleColor(3);

            // CRITICAL: The FileDialogManager's Draw method must be called for it to function.
            this.fileDialogManager.Draw();
        }
    }
}
