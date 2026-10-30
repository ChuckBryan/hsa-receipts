using System.ComponentModel.DataAnnotations;

namespace HsaReceipts.Contracts;

public sealed record SessionResponse(
    bool IsAuthenticated,
    string? DisplayName,
    string? UserKey);

public sealed record AntiforgeryResponse(string Token);

public sealed record ReceiptDto(
    string Id,
    string Provider,
    string Patient,
    string ServiceDate,
    long Cents,
    string Notes,
    string FileName,
    string ContentType,
    string UploadedBy,
    string CreatedAt,
    string? ReimbursedDate);

public sealed record ReceiptListResponse(
    IReadOnlyList<ReceiptDto> Receipts,
    long OutstandingCents,
    string RefreshedAt);

public class ReceiptFields
{
    [Required, StringLength(160)]
    public string Provider { get; set; } = "";

    [Required, StringLength(80)]
    public string Patient { get; set; } = "";

    [Required]
    public string ServiceDate { get; set; } = "";

    [Range(typeof(decimal), "0.01", "1000000")]
    public decimal Amount { get; set; }

    [StringLength(2000)]
    public string Notes { get; set; } = "";
}

public sealed class EditReceiptRequest : ReceiptFields;

public sealed record ReimbursementRequest(string? Date, bool Undo);

public sealed class LocalReceiptDraft : ReceiptFields
{
    public string DraftId { get; set; } = Guid.NewGuid().ToString("N");
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public string State { get; set; } = "draft";
    public string? Error { get; set; }
}

public static class ReceiptRules
{
    public const long MaxUploadBytes = 15 * 1024 * 1024;

    public static bool TryNormalize(
        ReceiptFields fields,
        out NormalizedReceiptFields normalized,
        out Dictionary<string, string[]> errors)
    {
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(fields);
        Validator.TryValidateObject(fields, context, validationResults, validateAllProperties: true);

        if (decimal.Round(fields.Amount, 2) != fields.Amount)
        {
            validationResults.Add(new ValidationResult(
                "Use at most two decimal places.",
                [nameof(fields.Amount)]));
        }

        if (!DateOnly.TryParseExact(
                fields.ServiceDate,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var serviceDate))
        {
            validationResults.Add(new ValidationResult(
                "Choose a valid service date.",
                [nameof(fields.ServiceDate)]));
        }

        errors = validationResults
            .SelectMany(result => result.MemberNames.DefaultIfEmpty("request")
                .Select(member => (member, result.ErrorMessage ?? "Invalid value.")))
            .GroupBy(item => item.member, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Item2).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);

        if (errors.Count > 0)
        {
            normalized = default;
            return false;
        }

        normalized = new NormalizedReceiptFields(
            fields.Provider.Trim(),
            fields.Patient.Trim(),
            serviceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            checked((long)(fields.Amount * 100)),
            fields.Notes.Trim());
        return true;
    }
}

public readonly record struct NormalizedReceiptFields(
    string Provider,
    string Patient,
    string ServiceDate,
    long Cents,
    string Notes);
