namespace PaperTodo;

/// <summary>Saved surface preference, independent of the opaque semantic color palette.</summary>
public static class PaperSkins
{
    public const string Paper = "paper";
    public const string Mica = "mica";
    public const string Acrylic = "acrylic";
    public const string ClearAcrylic = "clearAcrylic";
    public const string TracingPaper = "tracingPaper";
    public const string LiquidGlass = "liquidGlass";
    public const string Ceramic = "ceramic";
    public const string Aero = "aero";
    public const string Pixel = "pixel";
    public static readonly string[] All =
        { Paper, Mica, Acrylic, ClearAcrylic, TracingPaper, LiquidGlass, Ceramic, Aero, Pixel };
    public static bool IsValid(string? id) => id is Paper or Mica or Acrylic or ClearAcrylic or
        TracingPaper or LiquidGlass or Ceramic or Aero or Pixel;
    public static string Normalize(string? id) => IsValid(id) ? id! : Paper;
    // A missing field means legacy data; an explicit unknown ID means safe fallback.
    public static string Resolve(string? skin, string? colorScheme, string? oldBackdrop) =>
        skin != null ? Normalize(skin) : colorScheme == ColorSchemes.Mica
            ? MicaBackdropTypes.Normalize(oldBackdrop) : Paper;
    public static string Resolve(AppState? state) =>
        Resolve(state?.PaperSkin, state?.ColorScheme, state?.MicaBackdropType);
    public static bool UsesNativeBackdrop(string? id) => id is Mica or Acrylic or ClearAcrylic or
        TracingPaper or LiquidGlass or Aero;
    public static bool UsesSystemPalette(string? id) => id is Mica or Acrylic or ClearAcrylic;
    public static bool IsDecorated(string? id) => id is TracingPaper or LiquidGlass or Ceramic or Aero or Pixel;
    public static bool Decorate(string? id, bool highContrast) => !highContrast && IsDecorated(id);
    // Tracing retains system Acrylic. Aero uses low-tint accent blur; liquid uses unblurred alpha.
    public static string NativeBackdrop(string? id) => id switch
    {
        Acrylic or TracingPaper => MicaBackdropTypes.Acrylic,
        Aero => NativeMicaBackdrop.AeroGlassMaterial,
        ClearAcrylic => MicaBackdropTypes.ClearAcrylic,
        LiquidGlass => NativeMicaBackdrop.ClearGlassMaterial,
        _ => MicaBackdropTypes.Mica
    };
    public static string LabelKey(string id) => id switch
    {
        Mica => "MicaBackdropMica", Acrylic => "MicaBackdropAcrylic", ClearAcrylic => "MicaBackdropClearAcrylic",
        TracingPaper => "SkinTracingPaper", LiquidGlass => "SkinLiquidGlass",
        Ceramic => "SkinCeramic", Aero => "SkinAero", Pixel => "SkinPixel", _ => "SkinPaper"
    };
}
