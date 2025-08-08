using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
// No direct using for Plugin.Log needed if calling static Plugin.Log from Plugin.cs

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

        // Note: ControlsSectionWidth is no longer the primary driver for the new layout logic for the top row,
        // as button widths are now calculated dynamically in Draw().
        // private const float ControlsSectionWidth = 220f; 

        public MainWindow(Plugin plugin, List<FightStrategy> initialStrategies)
            : base("WDIGViewer##WDIGViewerMain")
        {
            this.pluginInstance = plugin;
            this.activeStrategies = initialStrategies;
            this.SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(500, 550), // Minimum window size
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue) // Allow window to be resized freely
            };
            this.Flags = ImGuiWindowFlags.NoScrollWithMouse; // Disable mouse wheel scrolling for the window itself
        }

        public void Dispose() { }

     

        public void UpdateStrategies(List<FightStrategy> newStrategies)
        {
            activeStrategies = newStrategies;
            // Reset selection and current view when strategies are updated
            selectedStrategy = null;
            currentPhase = null;
            selectedStrategyGlobalIndex = -1;
            selectedStrategyComboPreview = "Select a Strategy";
            currentPhaseImageIndex = 0;
        }

        /// <summary>
        /// Programmatically selects a strategy by its name.
        /// Used for auto-selection based on zone/duty or command arguments.
        /// </summary>
        /// <param name="strategyName">The name of the strategy to select (case-insensitive).</param>
        /// <param name="filterSourceType">Optional: Filter by a specific ImageSourceType.</param>
        public void SelectStrategyByName(string strategyName, ImageSourceType? filterSourceType = null)
        {
            if (string.IsNullOrEmpty(strategyName)) return;

            FightStrategy? strategyToSelect = null;
            int newGlobalIndex = -1; // To store the flat index of the strategy to be selected
            int flatIndexCounter = 0; // Counter for iterating through strategies in a flattened manner
            bool found = false;

            // Group strategies by source and order them for consistent lookup
            var grouped = activeStrategies.GroupBy(s => s.Source).OrderBy(g => g.Key.ToString());
            foreach (var group in grouped)
            {
                // If a source filter is provided, skip groups that don't match
                if (filterSourceType.HasValue && group.Key != filterSourceType.Value)
                {
                    flatIndexCounter += group.Count(); // Increment counter by the number of items in the skipped group
                    continue;
                }

                // Order strategies within the current group by name
                var strategiesInGroup = group.OrderBy(s => s.Name).ToList();
                foreach (var strategy in strategiesInGroup)
                {
                    if (strategy.Name.Equals(strategyName, StringComparison.OrdinalIgnoreCase))
                    {
                        strategyToSelect = strategy;
                        newGlobalIndex = flatIndexCounter;
                        found = true;
                        break; // Strategy found
                    }
                    flatIndexCounter++;
                }
                if (found) break; // Exit outer loop if strategy found
            }

            if (strategyToSelect != null)
            {
                // Only update if the selection is actually different
                if (this.selectedStrategy != strategyToSelect)
                {
                    this.selectedStrategy = strategyToSelect;
                    this.selectedStrategyGlobalIndex = newGlobalIndex;
                    this.selectedStrategyComboPreview = $"{(strategyToSelect.Name ?? "Unknown")} ({(strategyToSelect.Source.ToString() ?? "Unknown")})";

                    // Set to the first phase of the newly selected strategy
                    this.currentPhase = strategyToSelect.Phases.FirstOrDefault();
                    this.currentPhaseImageIndex = 0; // Reset to the first image
                    LoadImagesForCurrentPhase(); // Load images for the new selection

                    Plugin.Log.Info($"WDIGViewer: Automatically selected strategy: {strategyToSelect.Name}");
                }
            }
            else
            {
                Plugin.Log.Info($"WDIGViewer: Strategy named '{strategyName}' not found for auto-selection.");
            }
        }

        public override void Draw()
        {
            // Draw the "Strategy:" label
            ImGui.Text("Strategy:");
            ImGui.SameLine(); // Position subsequent items on the same line

            // Calculate dynamic widths for the Settings and Reload buttons
            // This includes text width and padding.
            float settingsButtonWidth = ImGui.CalcTextSize("Settings").X + ImGui.GetStyle().FramePadding.X * 2.0f;
            float reloadButtonWidth = ImGui.CalcTextSize("Reload All Images").X + ImGui.GetStyle().FramePadding.X * 2.0f;
            float spacing = ImGui.GetStyle().ItemSpacing.X; // Get standard item spacing

            // Calculate the width available for the combo box.
            // This is the total remaining width on the line after the "Strategy: " label,
            // minus the space needed for the two buttons and the spacing between them and the combo box.
            float remainingWidthForControls = ImGui.GetContentRegionAvail().X;
            float comboBoxWidth = remainingWidthForControls - (settingsButtonWidth + spacing + reloadButtonWidth + spacing);

            // Ensure a minimum sensible width for the combo box, e.g., 150 pixels.
            comboBoxWidth = Math.Max(150f, comboBoxWidth);

            // Draw only the combo box part of the strategy selector, providing it with the calculated width.
            DrawStrategySelectorCombo(comboBoxWidth);

            // Position the Settings button on the same line, after the combo box.
            ImGui.SameLine();
            if (ImGui.Button("Settings"))
            {
                pluginInstance.ToggleConfigUI();
            }

            // Position the Reload All Images button on the same line, after the Settings button.
            ImGui.SameLine();
            if (ImGui.Button("Reload All Images"))
            {
                pluginInstance.ReloadStrategies();
            }

            ImGui.Spacing(); // Add vertical spacing
            ImGui.Separator(); // Draw a horizontal separator line
            ImGui.Spacing(); // Add more vertical spacing

            if (selectedStrategy != null)
            {
                DrawPhaseTabs(); // Draw phase selection tabs if a strategy is selected
                ImGui.Spacing();

                // Create a child region for the image content area.
                // This allows the image area to have its own layout and potentially scrollbars if needed,
                // though scrolling is currently disabled by window flags.
                if (ImGui.BeginChild("ImageContentArea", Vector2.Zero, false, ImGuiWindowFlags.None))
                {
                    DrawPhaseContent(); // Draw the content of the selected phase (image, etc.)
                    ImGui.EndChild();
                }
            }
            else
            {
                ImGui.Text("Please select a strategy from the dropdown.");
            }
        }

        /// <summary>
        /// Draws only the strategy selector dropdown combo box with a specified width.
        /// The "Strategy:" label and initial ImGui.SameLine() are handled in the calling Draw() method.
        /// </summary>
        /// <param name="width">The width to assign to the combo box item.</param>
        private void DrawStrategySelectorCombo(float width)
        {
            ImGui.SetNextItemWidth(width); // Set the width for the upcoming combo box

            // Begin the combo box UI element.
            if (ImGui.BeginCombo("##StrategySelect", selectedStrategyComboPreview, ImGuiComboFlags.HeightLargest))
            {
                if (!activeStrategies.Any())
                {
                    ImGui.TextUnformatted("No strategies loaded. Check folders or settings.");
                }
                else
                {
                    int flatIndex = 0; // A counter for all strategies across all groups for unique selection tracking.
                    // Group strategies by their source (Plugin, User) and order these groups alphabetically.
                    var grouped = activeStrategies.GroupBy(s => s.Source).OrderBy(g => g.Key.ToString());

                    foreach (var group in grouped)
                    {
                        // Display each group as a collapsible tree node. Default to open.
                        if (ImGui.TreeNodeEx(group.Key.ToString(), ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
                        {
                            var strategiesInGroup = group.OrderBy(s => s.Name).ToList(); // Order strategies within the group by name.
                            foreach (var strategy in strategiesInGroup)
                            {
                                bool isSelectedUi = (selectedStrategyGlobalIndex == flatIndex); // Check if this strategy is the currently selected one.
                                if (ImGui.Selectable(strategy.Name, isSelectedUi)) // Draw the selectable item.
                                {
                                    // If this item is selected by the user and it's different from the current selection:
                                    if (selectedStrategy != strategy)
                                    {
                                        selectedStrategy = strategy; // Update the selected strategy.
                                        selectedStrategyGlobalIndex = flatIndex; // Update the global index of the selection.
                                        // Update the preview text for the combo box.
                                        selectedStrategyComboPreview = $"{(strategy.Name ?? "Unknown")} ({(strategy.Source.ToString() ?? "Unknown")})";
                                        currentPhase = strategy.Phases.FirstOrDefault(); // Select the first phase of the new strategy.
                                        currentPhaseImageIndex = 0; // Reset to the first image of the new phase.
                                        LoadImagesForCurrentPhase(); // Load images for the newly selected strategy/phase.
                                    }
                                }
                                if (isSelectedUi)
                                {
                                    ImGui.SetItemDefaultFocus(); // Ensure the currently selected item is focused when the combo opens.
                                }
                                flatIndex++; // Increment for the next strategy.
                            }
                            ImGui.TreePop(); // Close the tree node for the current group.
                        }
                        else
                        {
                            // If a tree node is collapsed, still need to add its strategy count to the flatIndex.
                            flatIndex += group.Count();
                        }
                    }
                }
                ImGui.EndCombo(); // End the combo box.
            }
        }

        /// <summary>
        /// Draws the phase selection tabs if a strategy is selected and has phases.
        /// </summary>
        private void DrawPhaseTabs()
        {
            if (selectedStrategy == null || !selectedStrategy.Phases.Any()) return; // Do nothing if no strategy or phases.

            // Begin a tab bar for displaying phases. Allows reordering and scrolling if tabs don't fit.
            if (ImGui.BeginTabBar("PhaseTabs", ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.Reorderable))
            {
                // Order phases by name for consistent display in the tab bar.
                var phasesToDraw = selectedStrategy.Phases.OrderBy(p => p.Name).ToList();
                bool phaseWasChangedByClickThisFrame = false; // Flag to check if a tab click changed the phase.

                foreach (var phase in phasesToDraw)
                {
                    string tabDisplayName = phase.Name; // Use the phase's name as the tab's display text.

                    // Begin a tab item. If this tab is selected, its content block will be active.
                    if (ImGui.BeginTabItem(tabDisplayName))
                    {
                        // If the currently drawn tab corresponds to a different phase than 'currentPhase':
                        if (currentPhase != phase)
                        {
                            currentPhase = phase; // Update the 'currentPhase'.
                            currentPhaseImageIndex = 0; // Reset to the first image of the newly selected phase.
                            phaseWasChangedByClickThisFrame = true; // Mark that the phase was changed by user interaction.
                        }
                        ImGui.EndTabItem(); // End the tab item.
                    }
                }

                // If a tab click resulted in a phase change, reload images for the new current phase.
                if (phaseWasChangedByClickThisFrame)
                {
                    LoadImagesForCurrentPhase();
                }
                ImGui.EndTabBar(); // End the tab bar.
            }
        }

        /// <summary>
        /// Loads or reloads images for the currently selected phase.
        /// This involves disposing old textures and loading new ones via the plugin's texture loader.
        /// </summary>
        private void LoadImagesForCurrentPhase()
        {
            if (selectedStrategy == null || currentPhase == null) return; // Check if selectedStrategy is null
                                                                          // Pass the source type of the currently selected strategy
            currentPhase?.LoadImages(pluginInstance.LoadTextureFromFile, selectedStrategy.Source);
        }

        /// <summary>
        /// Draws the content (primarily images) for the currently selected phase.
        /// Handles image scaling, centering, and pagination.
        /// </summary>
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

            // Ensure currentPhaseImageIndex is within the valid bounds of available images.
            if (currentPhaseImageIndex < 0 || currentPhaseImageIndex >= currentPhase.Images.Count)
            {
                currentPhaseImageIndex = 0; // Default to the first image if the index is out of bounds.
                if (!currentPhase.Images.Any()) // Re-check, though this state should be rare if the above checks passed.
                {
                    ImGui.Text("No images available for this phase.");
                    return;
                }
            }

            currentPhase.CurrentImageIndex = currentPhaseImageIndex; // Update the phase object with the current image index.
            ImageAsset? imageAsset = currentPhase.GetCurrentImage(); // Retrieve the current image asset to display.

            // Check if the image asset and its texture are valid and loaded.
            if (imageAsset?.TextureWrap is { } textureWrap)
            {
                var textureHandle = textureWrap.Handle;
                if (textureHandle.Handle != (ulong)IntPtr.Zero)
                {
                    float imgWidth = imageAsset.Width;   // Original width of the image.
                    float imgHeight = imageAsset.Height; // Original height of the image.

                    // Ensure image dimensions are valid to prevent division by zero or rendering errors.
                    if (imgWidth == 0 || imgHeight == 0)
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Image dimensions are zero for {Path.GetFileName(imageAsset.FilePath)}.");
                        return;
                    }

                    Vector2 contentSize = ImGui.GetContentRegionAvail(); // Get the available size in the current ImGui region.
                                                                         // If there are multiple images in this phase, reserve space at the bottom for pagination controls.
                    if (currentPhase.Images.Count > 1)
                    {
                        contentSize.Y -= (ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y);
                    }
                    // Ensure contentSize.Y is positive to avoid issues with aspect ratio calculation.
                    if (contentSize.Y <= 0) contentSize.Y = 1f;


                    float aspectRatio = imgWidth / imgHeight; // Calculate the image's aspect ratio.
                                                              // Calculate display size: fit to width initially, maintaining aspect ratio.
                    Vector2 displaySize = new Vector2(contentSize.X, contentSize.X / aspectRatio);

                    // If calculated height (based on fitting to width) exceeds available content height, then fit to height instead.
                    if (displaySize.Y > contentSize.Y)
                    {
                        displaySize.Y = contentSize.Y;
                        displaySize.X = contentSize.Y * aspectRatio;
                    }
                    // Ensure display dimensions are at least 1x1 pixel to prevent rendering issues.
                    displaySize.X = Math.Max(1f, displaySize.X);
                    displaySize.Y = Math.Max(1f, displaySize.Y);

                    // Calculate horizontal position to center the image.
                    float cursorPosX = (contentSize.X - displaySize.X) * 0.5f;
                    if (cursorPosX > 0)
                    {
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorPosX);
                    }

                    // Calculate vertical position to center the image within the adjusted available height.
                    float availableDrawingHeight = contentSize.Y; // This height is already reduced if pagination is present.
                    float cursorPosY = (availableDrawingHeight - displaySize.Y) * 0.5f;
                    // Only adjust Y cursor if there's space to center and centering is meaningful.
                    if (cursorPosY > 0 && availableDrawingHeight > displaySize.Y)
                    {
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cursorPosY);
                    }

                    ImGui.Image(textureHandle, displaySize); // Draw the image.
                }
            }
            else if (imageAsset != null) // If there's an image asset, but its texture isn't loaded/valid.
            {
                ImGui.Text($"Texture not loaded for: {Path.GetFileName(imageAsset.FilePath)}");
                if (!File.Exists(imageAsset.FilePath)) // Check if the image file is missing from disk.
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error: Image file not found at path.");
                }
                else // File exists, but couldn't be loaded (e.g., unsupported format, corrupted).
                {
                    ImGui.TextColored(new Vector4(1, 1, 0, 1), " (Image format might not be supported or image is invalid)");
                }
            }
            else // No image asset is currently selected or available for display.
            {
                ImGui.Text("No image to display for this selection.");
            }

            // Draw pagination controls ("Previous", page number, "Next") if there's more than one image.
            if (currentPhase.Images.Count > 1)
            {
                ImGui.Spacing(); // Add some vertical space before pagination.
                string pageText = $"{currentPhaseImageIndex + 1} / {currentPhase.Images.Count}"; // e.g., "1 / 3"

                // Calculate total width needed for pagination controls to center them.
                float prevButtonWidth = ImGui.CalcTextSize("Previous").X + ImGui.GetStyle().FramePadding.X * 2;
                float pageTextWidth = ImGui.CalcTextSize(pageText).X;
                float nextButtonWidth = ImGui.CalcTextSize("Next").X + ImGui.GetStyle().FramePadding.X * 2;
                float spacing = ImGui.GetStyle().ItemSpacing.X;
                float pageControlsTotalWidth = prevButtonWidth + spacing + pageTextWidth + spacing + nextButtonWidth;

                // Indent to center the pagination controls block horizontally.
                float indentX = (ImGui.GetContentRegionAvail().X - pageControlsTotalWidth) * 0.5f;
                if (indentX > 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indentX);
                }

                if (ImGui.Button("Previous"))
                {
                    if (currentPhaseImageIndex > 0) currentPhaseImageIndex--; // Go to previous image if not on the first.
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(pageText); // Display current page number out of total.
                ImGui.SameLine();
                if (ImGui.Button("Next"))
                {
                    if (currentPhaseImageIndex < currentPhase.Images.Count - 1) currentPhaseImageIndex++; // Go to next image if not on the last.
                }
            }
        }
    }
}
