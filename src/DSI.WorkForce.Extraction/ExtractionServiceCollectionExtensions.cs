using DSI.WorkForce.Extraction.Recognition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DSI.WorkForce.Extraction;

public static class ExtractionServiceCollectionExtensions
{
    public static IServiceCollection AddDsiExtraction(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ExtractionOptions>(config.GetSection(ExtractionOptions.SectionName));
        var recognizer = config.GetSection(ExtractionOptions.SectionName)["Recognizer"] ?? "None";

        switch (recognizer)
        {
            case "AzureDocumentIntelligence":
                services.AddSingleton<IHandwritingRecognizer, AzureDocumentIntelligenceRecognizer>();
                break;
            case "Ollama":
                services.AddHttpClient<IHandwritingRecognizer, OllamaVisionRecognizer>(c => c.Timeout = Timeout.InfiniteTimeSpan);
                break;
            case "None":
                services.AddSingleton<IHandwritingRecognizer, ManualKeyingRecognizer>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Extraction:Recognizer '{recognizer}'. Use None, AzureDocumentIntelligence or Ollama.");
        }

        services.AddSingleton<FormExtractor>();
        return services;
    }
}
