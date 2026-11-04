using System.Security.Claims;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Endpoints;

public static class EnrollmentEndpoints
{
    public static void MapEnrollment(this WebApplication app)
    {
        var publicApi = app.MapGroup("/api/public/enrollment").AllowAnonymous()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        publicApi.MapPost("/check", (PublicCodeRequest request, EnrollmentService service) =>
            service.CheckAsync(request.Code)).RequireRateLimiting("public-check");
        publicApi.MapPost("/submit", (PublicSubmitRequest request, EnrollmentService service) =>
            service.SubmitAsync(request)).RequireRateLimiting("public-submit")
            .WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024));

        var groups = app.MapGroup("/api/groups").RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        groups.MapGet("/{id:int}/enrollment-code", async (int id, ClaimsPrincipal user, AppDbContext db,
            HttpContext http, EnrollmentService service) =>
        {
            await ApiEndpoints.EnsureGroupAsync(db, user, id);
            var code = await service.GetActiveAsync(id);
            return code is null ? Results.NoContent() : Results.Ok(service.ToDto(code, http));
        });
        groups.MapPost("/{id:int}/enrollment-code", async (int id, CreateEnrollmentCodeRequest request,
            ClaimsPrincipal user, HttpContext http, AppDbContext db, EnrollmentService service) =>
        {
            await ApiEndpoints.EnsureGroupAsync(db, user, id);
            return service.ToDto(await service.CreateAsync(ApiEndpoints.TeacherId(user), id, request.Hours), http);
        });
        groups.MapDelete("/{id:int}/enrollment-code", async (int id, ClaimsPrincipal user, AppDbContext db, EnrollmentService service) =>
        {
            await ApiEndpoints.EnsureGroupAsync(db, user, id);
            await service.RevokeAsync(id);
            return Results.NoContent();
        });

        var submissions = app.MapGroup("/api/face-submissions").RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        submissions.MapGet("", async (FaceSubmissionStatus? status, ClaimsPrincipal user, AppDbContext db, EnrollmentService service) =>
        {
            var list = await service.ListAsync(status);
            if (user.HasClaim("bistudent:admin", "true")) return list;
            var teacherId = ApiEndpoints.TeacherId(user);
            var groupIds = (await db.Groups.AsNoTracking()
                .Where(group => !db.ScheduleSlots.Any(slot => slot.GroupId == group.Id) ||
                    db.ScheduleSlots.Any(slot => slot.GroupId == group.Id && slot.TeacherId == teacherId))
                .Select(group => group.Id).ToListAsync()).ToHashSet();
            return list.Where(x => groupIds.Contains(x.GroupId)).ToList();
        });
        submissions.MapGet("/{id:int}/images/{index:int}", async (int id, int index, ClaimsPrincipal user, AppDbContext db, EnrollmentService service) =>
        {
            await EnsureSubmissionAsync(db, user, id);
            return Results.File(await service.ImageAsync(id, index), "image/jpeg");
        });
        submissions.MapPost("/{id:int}/approve", async (int id, ClaimsPrincipal user, AppDbContext db, EnrollmentService service) =>
        {
            await EnsureSubmissionAsync(db, user, id);
            await service.ApproveAsync(ApiEndpoints.TeacherId(user), id);
            return Results.NoContent();
        });
        submissions.MapPost("/{id:int}/reject", async (int id, RejectSubmissionRequest request,
            ClaimsPrincipal user, AppDbContext db, EnrollmentService service) =>
        {
            await EnsureSubmissionAsync(db, user, id);
            await service.RejectAsync(ApiEndpoints.TeacherId(user), id, request.Reason);
            return Results.NoContent();
        });
    }

    private static async Task EnsureSubmissionAsync(AppDbContext db, ClaimsPrincipal user, int id)
    {
        var groupId = await db.FaceSubmissions.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (int?)x.Student.GroupId).SingleOrDefaultAsync()
            ?? throw new KeyNotFoundException("Заявка не найдена.");
        await ApiEndpoints.EnsureGroupAsync(db, user, groupId);
    }
}
