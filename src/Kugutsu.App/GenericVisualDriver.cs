namespace Kugutsu.App;

/// <summary>General geometric image candidates; unknown control attributes remain unknown.</summary>
internal sealed class GenericVisualDriver(VisualAnalysisPipeline pipeline, VisualControlActions actions) : VisualDriverBase("generic_visual", pipeline, actions);
