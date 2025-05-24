// Windows/ConfigWindow.cs
using Dalamud.Interface.Windowing;
using ImGuiNET;
using System;
using System.Numerics;

namespace WDIGViewer.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Plugin plugin;
        private readonly Configuration configuration;

        public ConfigWindow(Plugin plugin)
            : base("WDIGViewer Configuration###WDIGViewerConfig")
        {
            this.plugin = plugin;
            this.configuration = plugin.Configuration;

            this.Flags = ImGuiWindowFlags.NoResize |
                         ImGuiWindowFlags.NoCollapse |
                         ImGuiWindowFlags.NoScrollbar |
                         ImGuiWindowFlags.NoScrollWithMouse;

            this.Size = new Vector2(450, 200);
            this.SizeCondition = ImGuiCond.Always;
        }

        public void Dispose() { }

        public override void PreDraw()
        {
            if (configuration.IsConfigWindowMovable)
            {
                this.Flags &= ~ImGuiWindowFlags.NoMove;
            }
            else
            {
                this.Flags |= ImGuiWindowFlags.NoMove;
            }
        }

        public override void Draw()
        {
            var movable = configuration.IsConfigWindowMovable;
            if (ImGui.Checkbox("Movable Config Window", ref movable))
            {
                configuration.IsConfigWindowMovable = movable;
                configuration.Save();
            }

            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

            ImGui.Text("User Image Directory:");
            float buttonWidth = ImGui.CalcTextSize("Reload Images").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);

            var userImageDir = configuration.UserImageDirectory ?? string.Empty;
            if (ImGui.InputText("##UserImageDirectory", ref userImageDir, 256))
            {
                configuration.UserImageDirectory = userImageDir;
                configuration.Save();
            }

            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.Button("Reload Images"))
            {
                plugin.ReloadStrategies();
            }

            ImGui.TextWrapped("Set path to your custom image folder. Structure: YourFolder/FightName/PhaseName/image.filetype");
        }
    }
}