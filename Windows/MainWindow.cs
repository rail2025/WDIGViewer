// Windows/MainWindow.cs
using Dalamud.Interface.Windowing;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace WDIGViewer.Windows
{
    public class MainWindow : Window, IDisposable
    {
        private readonly Plugin pluginInstance;
        private List<FightStrategy> activeStrategies;

        private FightStrategy? selectedStrategy;
        private int selectedStrategyGlobalIndex = -1;
        private string selectedStrategyComboPreview = "Select a Strategy";

        private FightPhase? currentPhase;
        private int currentPhaseImageIndex = 0;

        public MainWindow(Plugin plugin, List<FightStrategy> initialStrategies)
            : base("WDIGViewer##WDIGViewerMain")
        {
            this.pluginInstance = plugin;
            this.activeStrategies = initialStrategies;
            this.SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(500, 550),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
            this.Flags = ImGuiWindowFlags.NoScrollWithMouse;
        }

        public void Dispose() { }

        public void UpdateStrategies(List<FightStrategy> newStrategies)
        {
            activeStrategies = newStrategies;
            selectedStrategy = null;
            currentPhase = null;
            selectedStrategyGlobalIndex = -1;
            selectedStrategyComboPreview = "Select a Strategy";
            currentPhaseImageIndex = 0;
        }

        public override void Draw()
        {
            DrawStrategySelector();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (selectedStrategy != null)
            {
                DrawPhaseTabs();
                ImGui.Spacing();

                if (ImGui.BeginChild("ImageContentArea"))
                {
                    DrawPhaseContent();
                }
                ImGui.EndChild();
            }
            else
            {
                ImGui.Text("Please select a strategy from the dropdown.");
            }

            ImGui.Separator();
            if (ImGui.Button("Settings"))
            {
                pluginInstance.ToggleConfigUI();
            }
            ImGui.SameLine();
            if (ImGui.Button("Reload All Images"))
            {
                pluginInstance.ReloadStrategies();
            }
        }

        private void DrawStrategySelector()
        {
            ImGui.Text("Strategy:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

            if (ImGui.BeginCombo("##StrategySelect", selectedStrategyComboPreview, ImGuiComboFlags.HeightLargest))
            {
                if (!activeStrategies.Any())
                {
                    ImGui.TextUnformatted("No strategies loaded. Check folders or settings.");
                }
                else
                {
                    int flatIndex = 0;
                    var grouped = activeStrategies.GroupBy(s => s.Source).OrderBy(g => g.Key.ToString());

                    foreach (var group in grouped)
                    {
                        if (ImGui.TreeNodeEx(group.Key.ToString(), ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
                        {
                            var strategiesInGroup = group.OrderBy(s => s.Name).ToList();
                            foreach (var strategy in strategiesInGroup)
                            {
                                bool isSelectedUi = (selectedStrategyGlobalIndex == flatIndex);
                                if (ImGui.Selectable(strategy.Name, isSelectedUi))
                                {
                                    if (selectedStrategy != strategy)
                                    {
                                        selectedStrategy = strategy;
                                        selectedStrategyGlobalIndex = flatIndex;
                                        selectedStrategyComboPreview = $"{(strategy.Name ?? "Unknown")} ({(strategy.Source.ToString() ?? "Unknown")})";
                                        currentPhase = strategy.Phases.FirstOrDefault();
                                        currentPhaseImageIndex = 0;
                                        LoadImagesForCurrentPhase();
                                    }
                                }
                                if (isSelectedUi)
                                {
                                    ImGui.SetItemDefaultFocus();
                                }
                                flatIndex++;
                            }
                            ImGui.TreePop();
                        }
                        else
                        {
                            flatIndex += group.Count();
                        }
                    }
                }
                ImGui.EndCombo();
            }
        }

        private void DrawPhaseTabs()
        {
            if (selectedStrategy == null || !selectedStrategy.Phases.Any()) return;

            if (ImGui.BeginTabBar("PhaseTabs", ImGuiTabBarFlags.FittingPolicyScroll))
            {
                var phasesToDraw = selectedStrategy.Phases.OrderBy(p => p.Name).ToList();
                bool phaseWasChangedByClickThisFrame = false;

                foreach (var phase in phasesToDraw)
                {
                    string tabDisplayName = phase.Name;

                    // Use the simplest ImGui.BeginTabItem overload.
                    // This returns true if this tab is the currently selected one.
                    if (ImGui.BeginTabItem(tabDisplayName))
                    {
                        // If ImGui indicates this tab is now selected, and it's different from our logical currentPhase,
                        // it means the user clicked this tab (or it's the first tab being auto-selected).
                        if (currentPhase != phase)
                        {
                            currentPhase = phase;
                            currentPhaseImageIndex = 0;
                            phaseWasChangedByClickThisFrame = true;
                        }
                        ImGui.EndTabItem(); // Must be called if BeginTabItem returned true
                    }
                }

                // If a phase was changed as a result of a tab click, load its images.
                if (phaseWasChangedByClickThisFrame)
                {
                    LoadImagesForCurrentPhase();
                }

                ImGui.EndTabBar();
            }
        }

        private void LoadImagesForCurrentPhase()
        {
            currentPhase?.LoadImages(pluginInstance.LoadTextureFromFile);
        }

        private void DrawPhaseContent()
        {
            if (currentPhase == null)
            {
                ImGui.Text("Select a phase.");
                return;
            }
            if (!currentPhase.Images.Any())
            {
                ImGui.Text($"No images found for {currentPhase.Name}.");
                return;
            }

            if (currentPhaseImageIndex < 0 || currentPhaseImageIndex >= currentPhase.Images.Count)
            {
                currentPhaseImageIndex = 0;
                if (!currentPhase.Images.Any())
                {
                    ImGui.Text("No images available.");
                    return;
                }
            }

            currentPhase.CurrentImageIndex = currentPhaseImageIndex;
            ImageAsset? imageAsset = currentPhase.GetCurrentImage();

            if (imageAsset?.TextureWrap?.ImGuiHandle != null && imageAsset.TextureWrap.ImGuiHandle != IntPtr.Zero)
            {
                IntPtr textureHandle = imageAsset.TextureWrap.ImGuiHandle;
                float imgWidth = imageAsset.Width;
                float imgHeight = imageAsset.Height;

                if (imgWidth == 0 || imgHeight == 0)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Image dimensions are zero for {Path.GetFileName(imageAsset.FilePath)}.");
                    return;
                }

                Vector2 contentSize = ImGui.GetContentRegionAvail();
                if (currentPhase.Images.Count > 1)
                {
                    contentSize.Y -= (ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y);
                }

                float aspectRatio = imgWidth / imgHeight;
                Vector2 displaySize = new Vector2(contentSize.X, contentSize.X / aspectRatio);

                if (displaySize.Y > contentSize.Y && contentSize.Y > 0)
                {
                    displaySize.Y = contentSize.Y;
                    displaySize.X = contentSize.Y * aspectRatio;
                }
                displaySize.X = Math.Max(1f, displaySize.X);
                displaySize.Y = Math.Max(1f, displaySize.Y);

                float cursorPosX = (contentSize.X - displaySize.X) * 0.5f;
                if (cursorPosX > 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorPosX);
                }

                float availableDrawingHeight = contentSize.Y;
                float cursorPosY = (availableDrawingHeight - displaySize.Y) * 0.5f;
                if (cursorPosY > 0 && availableDrawingHeight > displaySize.Y)
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cursorPosY);
                }

                ImGui.Image(textureHandle, displaySize);
            }
            else if (imageAsset != null)
            {
                ImGui.Text($"Texture not loaded for: {Path.GetFileName(imageAsset.FilePath)}");
                if (!File.Exists(imageAsset.FilePath))
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error: Image file not found at path.");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 1, 0, 1), " (Image format might not be supported or image is invalid)");
                }
            }
            else
            {
                ImGui.Text("No image to display for this selection.");
            }

            if (currentPhase.Images.Count > 1)
            {
                ImGui.Spacing();
                string pageText = $"{currentPhaseImageIndex + 1} / {currentPhase.Images.Count}";
                float pageControlsTotalWidth = ImGui.CalcTextSize("Previous").X + ImGui.GetStyle().ItemSpacing.X +
                                               ImGui.CalcTextSize(pageText).X + ImGui.GetStyle().ItemSpacing.X +
                                               ImGui.CalcTextSize("Next").X + (ImGui.GetStyle().FramePadding.X * 4);

                float indentX = (ImGui.GetContentRegionAvail().X - pageControlsTotalWidth) * 0.5f;
                if (indentX > 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indentX);
                }

                if (ImGui.Button("Previous"))
                {
                    if (currentPhaseImageIndex > 0) currentPhaseImageIndex--;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(pageText);
                ImGui.SameLine();
                if (ImGui.Button("Next"))
                {
                    if (currentPhaseImageIndex < currentPhase.Images.Count - 1) currentPhaseImageIndex++;
                }
            }
        }
    }
}
