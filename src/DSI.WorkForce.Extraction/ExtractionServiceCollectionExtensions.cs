using DSI.WorkForce.Extraction.Recognition;
using DSI.WorkForce.Extraction.Recognition.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Extraction;

public static class ExtractionServiceCollectionExtensions
{
    public static IServiceCollection AddDsiExtraction(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(ExtractionOptions.SectionName);
        services.Configure<ExtractionOptions>(section);
        var primary = section["Recognizer"] ?? "Local";
        var escalate = section["EscalateTo"] ?? "None";

        // Concrete recognizers are registered by type so they can be combined.
        services.AddSingleton<ManualKeyingRecognizer>();
        services.AddSingleton<PaddleLocalRecognizer>();
        services.AddSingleton<AzureDocumentIntelligenceRecognizer>();
        services.AddHttpClient<OllamaVisionRecognizer>(c => c.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<IHandwritingRecognizer>(sp =>
        {
            var first = Resolve(sp, primary);
            if (escalate.Equals("None", StringComparison.OrdinalIgnoreCase) || escalate.Equals(primary, StringComparison.OrdinalIgnoreCase))
                return first;
            var o = sp.GetRequiredService<IOptions<ExtractionOptions>>().Value;
            return new CascadingRecognizer(first, Resolve(sp, escalate), o.EscalateBelowConfidence,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CascadingRecognizer>());
        });

        services.AddSingleton<FormExtractor>();
        return services;
    }

    private static IHandwritingRecognizer Resolve(IServiceProvider sp, string name) => name switch
    {
        "Local" or "PaddleOCR" => sp.GetRequiredService<PaddleLocalRecognizer>(),
        "AzureDocumentIntelligence" => sp.GetRequiredService<AzureDocumentIntelligenceRecognizer>(),
        "Ollama" => sp.GetRequiredService<OllamaVisionRecognizer>(),
        "None" => sp.GetRequiredService<ManualKeyingRecognizer>(),
        _ => throw new InvalidOperationException($"Unknown recognizer '{name}'. Use Local, AzureDocumentIntelligence, Ollama or None."),
    };
}
