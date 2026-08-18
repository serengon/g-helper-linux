namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Phase 1 compatibility surface for the disabled RyzenAdj feature. No binary
/// is embedded, discovered, executed, or granted privilege. The public API is
/// retained so UI/backend callers fail closed until package attestation exists.
/// </summary>
public static class RyzenPower
{
    public static string? Family => null;
    public static bool IsRyzenCpu => false;
    public static HashSet<string>? SupportedParams => null;
    public static bool Available => false;

    public static readonly Dictionary<string, float> Defaults = new()
    {
        ["stapm-limit"] = 25,
        ["fast-limit"] = 25,
        ["slow-limit"] = 10,
        ["apu-slow-limit"] = 25,
        ["stapm-time"] = 900,
        ["slow-time"] = 60,
        ["tctl-temp"] = 85,
        ["apu-skin-temp"] = 45,
        ["dgpu-skin-temp"] = 45,
        ["vrm-current"] = 45,
        ["vrmsoc-current"] = 45,
        ["vrmmax-current"] = 45,
        ["vrmsocmax-current"] = 45,
        ["min-gfxclk"] = 400,
        ["max-gfxclk"] = 2200,
    };

    public static readonly Dictionary<string, (float Min, float Max)> Bounds = new()
    {
        ["stapm-limit"] = (3, 100),
        ["fast-limit"] = (3, 100),
        ["slow-limit"] = (3, 100),
        ["apu-slow-limit"] = (3, 100),
        ["stapm-time"] = (1, 3600),
        ["slow-time"] = (1, 1000),
        ["tctl-temp"] = (50, 105),
        ["apu-skin-temp"] = (40, 100),
        ["dgpu-skin-temp"] = (40, 100),
        ["vrm-current"] = (20, 150),
        ["vrmsoc-current"] = (20, 150),
        ["vrmmax-current"] = (20, 150),
        ["vrmsocmax-current"] = (20, 150),
        ["min-gfxclk"] = (400, 2200),
        ["max-gfxclk"] = (400, 2200),
    };

    public static float Clamp(string param, float value)
        => Bounds.TryGetValue(param, out var bounds)
            ? Math.Clamp(value, bounds.Min, bounds.Max)
            : value;

    public static bool IsSupported(string param) => false;
    public static HashSet<string>? Probe() => null;
    public static Dictionary<string, float>? ReadInfo() => null;
    public static void Invalidate() { }
    public static bool Apply(IReadOnlyCollection<(string Param, int Raw)> settings, bool interactive = true) => false;
    public static bool Set(string param, int value) => false;
    public static int? SavedValue(string param) => null;
    public static int? StockValue(string param) => null;
    public static void SaveValue(string param, int displayValue) { }

    public static int RawScale(string param)
        => param.EndsWith("-limit", StringComparison.Ordinal)
        || param.EndsWith("-current", StringComparison.Ordinal)
            ? 1000 : 1;

    public static void ApplySavedOnStart() { }
    public static bool ResetToStock() => false;

    public static string DebugDump()
        => "  RyzenAdj: disabled; package-managed attestation unavailable\n";

    public static string? PptToParam(string attribute) => attribute switch
    {
        "ppt_pl1_spl" => "stapm-limit",
        "ppt_fppt" => "fast-limit",
        "ppt_pl2_sppt" => "slow-limit",
        "ppt_apu_sppt" => "apu-slow-limit",
        _ => null,
    };

    public static bool TrySetPpt(string attribute, int watts) => false;
}
