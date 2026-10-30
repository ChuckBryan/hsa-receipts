using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HsaReceipts.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace HsaReceipts;

public static class ReceiptEndpoints
{
    public static void MapReceiptEndpoints(this WebApplication app)
    {
        app.MapGet("/bff/login", (HttpContext context, string? returnUrl) =>
        {
            var destination = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = destination },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        app.MapGet("/bff/session", (ClaimsPrincipal user) =>
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new SessionResponse(false, null, null));
            }

            return Results.Ok(new SessionResponse(
                true,
                user.Identity.Name,
                GetUserKey(user)));
        }).AllowAnonymous();

        app.MapGet("/bff/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new AntiforgeryResponse(tokens.RequestToken!));
        }).RequireAuthorization();

        app.MapPost("/bff/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme
                ]);
        }).RequireAuthorization();

        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/receipts", (
            ReceiptStore store,
            string? q,
            string? status) =>
        {
            var all = store.All();
            var receipts = all
                .Where(receipt =>
                    (string.IsNullOrWhiteSpace(q)
                     || $"{receipt.Provider} {receipt.Patient} {receipt.Notes}"
                         .Contains(q, StringComparison.OrdinalIgnoreCase))
                    && (status != "open" || receipt.ReimbursedDate is null)
                    && (status != "paid" || receipt.ReimbursedDate is not null))
                .Select(ToDto)
                .ToList();

            return Results.Ok(new ReceiptListResponse(
                receipts,
                all.Where(receipt => receipt.ReimbursedDate is null).Sum(receipt => receipt.Cents),
                DateTimeOffset.UtcNow.ToString("O")));
        });

        api.MapPost("/receipts", CreateReceiptAsync)
            .DisableAntiforgery();

        api.MapPut("/receipts/{id}", async (
            string id,
            EditReceiptRequest request,
            ReceiptStore store,
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (!ReceiptRules.TryNormalize(request, out var fields, out var errors))
            {
                return Results.ValidationProblem(errors);
            }

            return store.Edit(
                id,
                fields.Provider,
                fields.Patient,
                fields.ServiceDate,
                fields.Cents,
                fields.Notes)
                ? Results.NoContent()
                : Results.NotFound();
        });

        api.MapPut("/receipts/{id}/reimbursement", async (
            string id,
            ReimbursementRequest request,
            ReceiptStore store,
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            string? date = null;
            if (!request.Undo)
            {
                if (!DateOnly.TryParseExact(
                        request.Date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [nameof(request.Date)] = ["Choose a valid reimbursement date."]
                    });
                }

                date = request.Date;
            }

            return store.Reimburse(id, date) ? Results.NoContent() : Results.NotFound();
        });

        api.MapGet("/receipts/{id}/file", (string id, ReceiptStore store) =>
        {
            var receipt = store.Find(id);
            return receipt is null
                ? Results.NotFound()
                : Results.File(store.FilePath(id), receipt.ContentType, receipt.FileName);
        });

        api.MapGet("/receipts/export.csv", (ReceiptStore store) =>
        {
            var rows = new List<string>
            {
                "Id,Provider,Patient,ServiceDate,Amount,ReimbursedDate,Notes,OriginalFilename,UploadedBy,CreatedAt"
            };
            rows.AddRange(store.All().Select(receipt => string.Join(
                ",",
                new[]
                {
                    receipt.Id,
                    receipt.Provider,
                    receipt.Patient,
                    receipt.ServiceDate,
                    (receipt.Cents / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                    receipt.ReimbursedDate ?? "",
                    receipt.Notes,
                    receipt.FileName,
                    receipt.UploadedBy,
                    receipt.CreatedAt
                }.Select(CsvCell))));

            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", rows)))
                .ToArray();
            return Results.File(bytes, "text/csv", "hsa-receipts.csv");
        });
    }

    private static async Task<IResult> CreateReceiptAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ReceiptStore store,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var fields = new ReceiptFields
        {
            Provider = form["provider"].ToString(),
            Patient = form["patient"].ToString(),
            ServiceDate = form["serviceDate"].ToString(),
            Notes = form["notes"].ToString()
        };
        if (!decimal.TryParse(
                form["amount"],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            amount = 0;
        }
        fields.Amount = amount;

        if (!ReceiptRules.TryNormalize(fields, out var normalized, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var upload = form.Files.GetFile("upload");
        if (upload is null || upload.Length is <= 0 or > ReceiptRules.MaxUploadBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["upload"] = ["Choose a JPG, PNG, or PDF up to 15 MB."]
            });
        }

        var idempotencyKey = form["idempotencyKey"].ToString();
        if (!Guid.TryParseExact(idempotencyKey, "N", out _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["idempotencyKey"] = ["A valid submission key is required."]
            });
        }

        var stagedPath = store.NewStagingPath();
        string contentType;
        string fileHash;
        try
        {
            await using var input = upload.OpenReadStream();
            await using var output = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var signature = new byte[8];
            var signatureLength = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (signatureLength < signature.Length)
                {
                    var copyLength = Math.Min(signature.Length - signatureLength, read);
                    buffer.AsSpan(0, copyLength)
                        .CopyTo(signature.AsSpan(signatureLength, copyLength));
                    signatureLength += copyLength;
                }
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            contentType = DetectContentType(signature.AsSpan(0, signatureLength))
                ?? throw new InvalidUploadException();
            fileHash = Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (InvalidUploadException)
        {
            File.Delete(stagedPath);
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["upload"] = ["Please choose a JPG, PNG, or PDF. For an iPhone HEIC photo, export it as JPG first."]
            });
        }
        catch
        {
            File.Delete(stagedPath);
            throw;
        }

        var extension = contentType switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            _ => ".jpg"
        };
        var safeBaseName = Path.GetFileNameWithoutExtension(upload.FileName)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(safeBaseName))
        {
            safeBaseName = "receipt";
        }

        var id = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var receipt = new Receipt(
            id,
            normalized.Provider,
            normalized.Patient,
            normalized.ServiceDate,
            normalized.Cents,
            normalized.Notes,
            safeBaseName + extension,
            contentType,
            context.User.Identity!.Name ?? "",
            createdAt,
            null);
        var digest = ComputeDigest(normalized, fileHash);

        try
        {
            var result = await store.CreateAsync(
                receipt,
                GetUserKey(context.User),
                idempotencyKey,
                digest,
                stagedPath,
                cancellationToken);
            return Results.Json(ToDto(result.Receipt), statusCode: result.Created ? 201 : 200);
        }
        catch (IdempotencyConflictException)
        {
            return Results.Conflict(new
            {
                error = "This submission key was already used with different receipt data."
            });
        }
    }

    private static string? DetectContentType(ReadOnlySpan<byte> signature)
    {
        if (signature.Length >= 5 && signature[..5].SequenceEqual("%PDF-"u8))
        {
            return "application/pdf";
        }
        if (signature.Length >= 8
            && signature[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }
        if (signature.Length >= 3
            && signature[0] == 255
            && signature[1] == 216
            && signature[2] == 255)
        {
            return "image/jpeg";
        }
        return null;
    }

    private static string ComputeDigest(NormalizedReceiptFields fields, string fileHash)
    {
        var canonical = string.Join(
            "\n",
            fields.Provider,
            fields.Patient,
            fields.ServiceDate,
            fields.Cents.ToString(CultureInfo.InvariantCulture),
            fields.Notes,
            fileHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string GetUserKey(ClaimsPrincipal user)
    {
        var issuer = user.FindFirst("iss")?.Value ?? "unknown-issuer";
        var subject = user.FindFirst("sub")?.Value
            ?? user.Identity?.Name
            ?? throw new InvalidOperationException("Authenticated user has no stable identifier.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{subject}")));
    }

    private static bool IsLocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || returnUrl[1] is not '/' and not '\\');

    private static ReceiptDto ToDto(Receipt receipt) =>
        new(
            receipt.Id,
            receipt.Provider,
            receipt.Patient,
            receipt.ServiceDate,
            receipt.Cents,
            receipt.Notes,
            receipt.FileName,
            receipt.ContentType,
            receipt.UploadedBy,
            receipt.CreatedAt,
            receipt.ReimbursedDate);

    private static string CsvCell(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('=') || trimmed.StartsWith('+') || trimmed.StartsWith('-')
            || trimmed.StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')
            || value.StartsWith('\n'))
        {
            value = "'" + value;
        }
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class InvalidUploadException : Exception;
}
