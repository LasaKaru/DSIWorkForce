using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Data.Repositories;
using DSI.WorkForce.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSI.WorkForce.Web.Api;

/// <summary>JSON API used by the WPF desktop client (and available for Power Automate / other systems).</summary>
public static class ApiEndpoints
{
    public static void MapDsiApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Policies.Reviewer);

        api.MapGet("/me", (HttpContext ctx) => Results.Ok(new { name = CurrentUser.Name(ctx.User) }));

        api.MapGet("/dashboard", (WorksheetRepository repo, CancellationToken ct) => repo.GetDashboardAsync(ct));

        api.MapGet("/worksheets", (WorksheetRepository repo, string? status, DateOnly? from, DateOnly? to, string? resourceNo, string? epfNo, int? top, CancellationToken ct) =>
            repo.SearchAsync(string.IsNullOrWhiteSpace(status) ? null : status, from, to, resourceNo, epfNo, Math.Clamp(top ?? 200, 1, 2000), ct));

        api.MapGet("/worksheets/{id:long}", async (long id, WorksheetRepository repo, CancellationToken ct) =>
            await repo.GetDetailAsync(id, ct) is { } d ? Results.Ok(d.Detail) : Results.NotFound());

        api.MapGet("/worksheets/{id:long}/image", async (long id, WorksheetRepository repo, CancellationToken ct) =>
        {
            var d = await repo.GetDetailAsync(id, ct);
            return d?.ImagePath is { } path && File.Exists(path)
                ? Results.File(path, "image/jpeg", enableRangeProcessing: false)
                : Results.NotFound();
        });

        api.MapPut("/worksheets/{id:long}", async (long id, SaveCorrectionsRequest req, WorksheetRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                await repo.SaveCorrectionsAsync(id, req, CurrentUser.Name(ctx.User), ct);
                return Results.NoContent();
            }
            catch (ConcurrencyException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        api.MapPost("/worksheets/{id:long}/reject", async (long id, [FromBody] RejectRequest req, WorksheetRepository repo, HttpContext ctx, CancellationToken ct) =>
        {
            await repo.RejectAsync(id, CurrentUser.Name(ctx.User), req.Reason, ct);
            return Results.NoContent();
        });

        api.MapGet("/reports", async (ReportRepository repo, CancellationToken ct) =>
            (await repo.GetAllAsync(ct)).Select(r => new { r.Code, r.Title, r.Subtitle }));

        api.MapGet("/reports/{code}", async (string code, DateOnly from, DateOnly to, string? shift, string? resourceNo, string? epfNo, ReportRepository repo, CancellationToken ct) =>
        {
            try { return Results.Ok(await repo.QueryAsync(new ReportQuery(code, from, to, shift, resourceNo, epfNo), ct: ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        api.MapGet("/reports/{code}/export", async (string code, DateOnly from, DateOnly to, string? shift, string? resourceNo, string? epfNo, ReportRepository repo, CancellationToken ct) =>
        {
            var def = await repo.GetAsync(code, ct);
            if (def is null) return Results.NotFound();
            var result = await repo.QueryAsync(new ReportQuery(code, from, to, shift, resourceNo, epfNo), ct: ct);
            var bytes = ExcelExporter.Export(result, def.Subtitle, from, to);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{def.Title} {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.xlsx");
        });

        api.MapPost("/scans", async (HttpRequest request, UploadService uploads, HttpContext ctx, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data expected");
            var form = await request.ReadFormAsync(ct);
            var template = form["templateCode"].FirstOrDefault() ?? "PC-011-A";
            var results = new List<UploadResultDto>();
            foreach (var file in form.Files)
            {
                if (file.Length > uploads.MaxFileBytes) { results.Add(new(file.FileName, false, "File too large.")); continue; }
                await using var s = file.OpenReadStream();
                results.Add(await uploads.SaveAsync(template, file.FileName, s, CurrentUser.Name(ctx.User), ct));
            }
            return Results.Ok(results);
        }).DisableAntiforgery();

        api.MapPost("/scanfiles/retry-failed", async (ScanQueueRepository queue, CancellationToken ct) =>
            Results.Ok(new { requeued = await queue.RetryFailedAsync(null, ct) })).RequireAuthorization(Policies.Admin);
    }

    public sealed record RejectRequest(string Reason);
}
