namespace DSI.WorkForce.Core.Contracts;

/// <summary>Look-and-feel options changed on Administration › Settings (stored in dsi.AppSetting).</summary>
public sealed class BrandingSettings
{
    /// <summary>Shows the Helao2 logos, "Powered by" and the vendor copyright line. Off = neutral product branding.</summary>
    public bool ShowVendorBranding { get; set; } = true;

    /// <summary>Shows the OCR engine panel (name, logo, status) on the License page.</summary>
    public bool ShowEnginePanel { get; set; } = true;

    public string EngineName { get; set; } = "Helao2 Vision OCR";
    public string EngineTagline { get; set; } = "AI handwriting recognition for the automation industry";
}

/// <summary>What the Worker reports about its recognition engine after start-up.</summary>
public sealed record EngineStatus(string Worker, string Recognizer, string TextModel, bool DigitModel, DateTime ReadyUtc);
