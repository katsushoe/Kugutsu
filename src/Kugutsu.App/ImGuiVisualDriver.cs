using System.Text.Json;
using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Image and rule driver; it does not obtain ImGui internal IDs or require an instrumented application.</summary>
internal sealed class ImGuiVisualDriver(VisualAnalysisPipeline pipeline, VisualControlActions actions) : VisualDriverBase("imgui_visual", pipeline, actions);
