namespace DSI.WorkForce.Extraction;

public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";

    /// <summary>None (manual keying of handwriting, OMR still automatic) | AzureDocumentIntelligence | Ollama</summary>
    public string Recognizer { get; set; } = "None";

    public int AlignedImageJpegQuality { get; set; } = 75;

    /// <summary>Ink ratio below which a handwriting box is treated as empty (skips the recognizer call).</summary>
    public double BlankInkThreshold { get; set; } = 0.006;

    public AzureDocumentIntelligenceOptions Azure { get; set; } = new();
    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class AzureDocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = "";
    /// <summary>Prefer environment variable Extraction__Azure__ApiKey or a secret store; never commit keys.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>prebuilt-read, or the id of a custom model trained on PC-011-A.</summary>
    public string ModelId { get; set; } = "prebuilt-read";
}

public sealed class OllamaOptions
{
    /// <summary>Local vision-language model server (https://ollama.com), e.g. on a GPU box in the server room.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5vl:7b";
    /// <summary>VLMs don't return a confidence; this value is used and master-data checks raise/lower it.</summary>
    public decimal AssumedConfidence { get; set; } = 0.92m;
    public int TimeoutSeconds { get; set; } = 120;
}
