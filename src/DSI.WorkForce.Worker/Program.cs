using DSI.WorkForce.Data;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Worker;
using DSI.WorkForce.Worker.Services;
using Serilog;

// DSI WorkForce extraction service.
//   Run as console:          DSI.WorkForce.Worker.exe
//   Install Windows service: sc.exe create DSIWorkForceWorker binPath= "C:\apps\dsi-worker\DSI.WorkForce.Worker.exe" start= delayed-auto
//   Calibrate a template:    DSI.WorkForce.Worker.exe --calibrate scan.pdf [--template file.json] [--out folder]

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "DSIWorkForceWorker");

builder.Services.AddSerilog((sp, cfg) => cfg.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext());

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.AddDsiData();
builder.Services.AddDsiExtraction(builder.Configuration);
builder.Services.AddSingleton<ReferenceDataCache>();

var calibrate = args.Contains("--calibrate");
if (!calibrate)
{
    builder.Services.AddHostedService<HotFolderIngestionService>();
    builder.Services.AddHostedService<ExtractionWorker>();
}

// A crash in one page must never stop the whole service.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

using var host = builder.Build();

if (calibrate)
    return await CalibrationRunner.RunAsync(args, host.Services.GetRequiredService<FormExtractor>());

// Development: create the database if it does not exist and bring it up to date (production: run the DbMigrator).
await host.Services.InitializeDatabaseIfConfiguredAsync(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

await host.RunAsync();
return 0;
