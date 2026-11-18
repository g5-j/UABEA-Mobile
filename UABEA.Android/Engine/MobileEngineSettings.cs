namespace UABEAvalonia;

/// <summary>
/// Runtime options for the Android engine. The desktop ConfigurationManager
/// is intentionally not imported because it depends on desktop configuration
/// storage and UI infrastructure. Keep Cpp2IL enabled by default, matching UABEA.
/// </summary>
internal static class MobileEngineSettings
{
    public static bool UseCpp2Il { get; set; } = true;
}
