using System.Security.Claims;
using AuditPlatform.Contracts.Evidence;
using AuditPlatform.Evidence;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Evidence;

internal static class EvidenceEndpoints
{
    public const int MaxFileNameLength = 400;

    /// <summary>
    /// Evidence travels over plain HTTP requests next to the sync hub: a file is sent whole under its content hash
    /// (PUT is idempotent), listed per engagement, and read back by members only.
    /// </summary>
    public static void MapEvidenceApi(this WebApplication app)
    {
        app.MapPut(EvidenceRoutes.File, async (
                Guid engagementId, string hash, HttpContext http, ClaimsPrincipal user, EvidenceService evidence, IOptions<MasterOptions> options,
                CancellationToken ct) =>
            {
                if (!EvidenceHash.IsValid(hash))
                {
                    return Fail(StatusCodes.Status400BadRequest, "hash_invalid", "The address must be a lowercase hex BLAKE3-256 hash.");
                }

                var name = CleanFileName(http.Request.Query[EvidenceRoutes.FileNameParameter]);
                if (name is null)
                {
                    return Fail(StatusCodes.Status400BadRequest, "name_missing", "The file name is required.");
                }

                var max = options.Value.MaxEvidenceBytes;
                if (http.Request.ContentLength is not { } length)
                {
                    return Fail(StatusCodes.Status411LengthRequired, "length_required", "The size of the file must be declared.");
                }

                if (length > max)
                {
                    return Fail(StatusCodes.Status413PayloadTooLarge, "too_large", $"A file may be at most {max / (1024 * 1024)} MB.");
                }

                // Kestrel then refuses a body longer than declared; the default limit (30 MB) is too small for scans.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = max;
                }

                var result = await evidence.ReceiveAsync(engagementId, user.GetUserId(), hash, name, http.Request.ContentType, http.Request.Body, ct);
                return result.Outcome switch
                {
                    EvidenceOutcome.Ok => Results.Ok(result.Info),
                    EvidenceOutcome.NotMember => Fail(StatusCodes.Status403Forbidden, "not_member", result.Reason!),
                    EvidenceOutcome.NotAllowed => Fail(StatusCodes.Status403Forbidden, "not_editor", result.Reason!),
                    EvidenceOutcome.Locked => Fail(StatusCodes.Status409Conflict, "engagement_locked", result.Reason!),
                    _ => Fail(StatusCodes.Status422UnprocessableEntity, "hash_mismatch", result.Reason!),
                };
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        MapPartedUpload(app);

        app.MapGet(EvidenceRoutes.List, async (Guid engagementId, ClaimsPrincipal user, EvidenceService evidence, CancellationToken ct) =>
                await evidence.ListAsync(engagementId, user.GetUserId(), ct) is { } list
                    ? Results.Ok(list)
                    : Fail(StatusCodes.Status403Forbidden, "not_member", "Not a member of this engagement."))
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapGet(EvidenceRoutes.File, async (Guid engagementId, string hash, ClaimsPrincipal user, EvidenceService evidence, CancellationToken ct) =>
            {
                var found = await evidence.OpenAsync(engagementId, user.GetUserId(), hash, ct);
                return found.Outcome switch
                {
                    // Always as a download of opaque bytes: the master never renders what an auditee handed over.
                    EvidenceOutcome.Ok => Results.Stream(found.Content!, "application/octet-stream", found.Info!.FileName),
                    EvidenceOutcome.NotMember => Fail(StatusCodes.Status403Forbidden, "not_member", "Not a member of this engagement."),
                    EvidenceOutcome.Damaged => Fail(StatusCodes.Status500InternalServerError, "evidence_damaged", found.Reason!),
                    _ => Fail(StatusCodes.Status404NotFound, "not_found", "This engagement has no such evidence file."),
                };
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();
    }

    /// <summary>
    /// A file larger than one request may carry is sent in numbered parts, resumable after a broken connection, and then
    /// completed; the master assembles it and checks it against its hash as it would a file sent whole.
    /// </summary>
    private static void MapPartedUpload(WebApplication app)
    {
        app.MapGet(EvidenceRoutes.Parts, async (
                Guid engagementId, string hash, long total, ClaimsPrincipal user, EvidenceUploads uploads, CancellationToken ct) =>
            {
                if (!EvidenceHash.IsValid(hash))
                {
                    return Fail(StatusCodes.Status400BadRequest, "hash_invalid", "The address must be a lowercase hex BLAKE3-256 hash.");
                }

                var (refusal, parts) = await uploads.StatusAsync(engagementId, user.GetUserId(), hash, total, ct);
                return refusal is null ? Results.Ok(parts) : Refuse(refusal);
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapPut(EvidenceRoutes.Part, async (
                Guid engagementId, string hash, int number, long total, HttpContext http, ClaimsPrincipal user, EvidenceUploads uploads,
                IOptions<MasterOptions> options, CancellationToken ct) =>
            {
                if (!EvidenceHash.IsValid(hash))
                {
                    return Fail(StatusCodes.Status400BadRequest, "hash_invalid", "The address must be a lowercase hex BLAKE3-256 hash.");
                }

                var partBytes = Math.Max(1, options.Value.EvidencePartBytes);
                if (http.Request.ContentLength is not { } length)
                {
                    return Fail(StatusCodes.Status411LengthRequired, "length_required", "The size of the part must be declared.");
                }

                if (length > partBytes)
                {
                    return Fail(StatusCodes.Status413PayloadTooLarge, "too_large", $"A part may be at most {partBytes} bytes.");
                }

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = partBytes;
                }

                return Refuse(await uploads.ReceivePartAsync(engagementId, user.GetUserId(), hash, number, total, length, http.Request.Body, ct));
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapPost(EvidenceRoutes.Complete, async (
                Guid engagementId, string hash, long total, HttpContext http, ClaimsPrincipal user, EvidenceUploads uploads, CancellationToken ct) =>
            {
                if (!EvidenceHash.IsValid(hash))
                {
                    return Fail(StatusCodes.Status400BadRequest, "hash_invalid", "The address must be a lowercase hex BLAKE3-256 hash.");
                }

                var name = CleanFileName(http.Request.Query[EvidenceRoutes.FileNameParameter]);
                if (name is null)
                {
                    return Fail(StatusCodes.Status400BadRequest, "name_missing", "The file name is required.");
                }

                return Refuse(await uploads.CompleteAsync(engagementId, user.GetUserId(), hash, total, name, http.Request.ContentType, ct));
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();
    }

    private static IResult Refuse(EvidenceResult result) => result.Outcome switch
    {
        EvidenceOutcome.Ok => result.Info is null ? Results.NoContent() : Results.Ok(result.Info),
        EvidenceOutcome.NotMember => Fail(StatusCodes.Status403Forbidden, "not_member", result.Reason!),
        EvidenceOutcome.NotAllowed => Fail(StatusCodes.Status403Forbidden, "not_editor", result.Reason!),
        EvidenceOutcome.Locked => Fail(StatusCodes.Status409Conflict, "engagement_locked", result.Reason!),
        EvidenceOutcome.Incomplete => Fail(StatusCodes.Status409Conflict, "incomplete", result.Reason!),
        EvidenceOutcome.BadRequest => Fail(StatusCodes.Status400BadRequest, "part_invalid", result.Reason!),
        _ => Fail(StatusCodes.Status422UnprocessableEntity, "hash_mismatch", result.Reason!),
    };

    /// <summary>The last path segment without control characters, or null when nothing usable is left.</summary>
    internal static string? CleanFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (name.Length > MaxFileNameLength)
        {
            name = name[..MaxFileNameLength];
        }

        return name.Length == 0 || name is "." or ".." ? null : name;
    }

    private static IResult Fail(int status, string code, string message) => Results.Json(new EvidenceError(code, message), statusCode: status);
}
