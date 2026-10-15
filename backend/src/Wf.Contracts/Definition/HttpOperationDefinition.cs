using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Definition;

/// <summary>
/// Author-declared remote request/reply contract. URLs never contain credentials, queries or fragments.
/// Origins are submission-only unless explicitly trusted; runners must also enforce this on redirects
/// and on references returned by the service. Request payloads and secret values are not manifest data.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpOperationDefinition
{
    public string SubmissionUrl { get; }
    public HttpOperationStatusContract Status { get; }
    public HttpOperationCancelContract? Cancel { get; }
    public HttpOperationDeduplicationContract? Deduplication { get; }
    public HttpOperationLookupContract? Lookup { get; }
    public ImmutableArray<string> TrustedOrigins { get; }
    public string? SecretReference { get; }
    public TimeSpan RequestTimeout { get; }

    public HttpOperationDefinition(string submissionUrl, HttpOperationStatusContract status,
        HttpOperationCancelContract? cancel = null, HttpOperationDeduplicationContract? deduplication = null,
        HttpOperationLookupContract? lookup = null, ImmutableArray<string> trustedOrigins = default,
        string? secretReference = null, TimeSpan? requestTimeout = null)
        : this(submissionUrl, status, requestTimeout ?? TimeSpan.FromSeconds(30), cancel, deduplication, lookup,
            trustedOrigins, secretReference) { }

    [JsonConstructor]
    public HttpOperationDefinition(string submissionUrl, HttpOperationStatusContract status, TimeSpan requestTimeout,
        HttpOperationCancelContract? cancel = null, HttpOperationDeduplicationContract? deduplication = null,
        HttpOperationLookupContract? lookup = null, ImmutableArray<string> trustedOrigins = default,
        string? secretReference = null)
    {
        var submission = HttpOperationContractInput.Url(submissionUrl);
        ArgumentNullException.ThrowIfNull(status);
        var origins = trustedOrigins.IsDefault ? [] : trustedOrigins;
        if (origins.Length > 16)
            throw HttpOperationContractInput.Invalid();
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { submission.GetLeftPart(UriPartial.Authority) };
        foreach (var origin in origins)
        {
            var parsed = HttpOperationContractInput.Url(origin);
            var pathStart = origin.IndexOf('/', origin.IndexOf("://", StringComparison.Ordinal) + 3);
            if (pathStart >= 0 && origin[pathStart..] != "/" ||
                !trusted.Add(parsed.GetLeftPart(UriPartial.Authority)))
                throw HttpOperationContractInput.Invalid();
        }
        CheckOrigin(status.UrlTemplate, "{operationId}");
        CheckOrigin(cancel?.UrlTemplate, "{operationId}", allowStatusUrl: true);
        CheckOrigin(lookup?.UrlTemplate, "{submissionKey}");
        if (secretReference is not null && !global::Wf.Contracts.Definition.SecretReference.IsValidName(secretReference))
            throw HttpOperationContractInput.Invalid();
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(30))
            throw HttpOperationContractInput.Invalid();
        SubmissionUrl = submissionUrl;
        Status = status;
        Cancel = cancel;
        Deduplication = deduplication;
        Lookup = lookup;
        TrustedOrigins = origins;
        SecretReference = secretReference;
        RequestTimeout = requestTimeout;

        void CheckOrigin(string? template, string placeholder, bool allowStatusUrl = false)
        {
            if (template is null)
                return;
            var uri = HttpOperationContractInput.Template(template, placeholder, allowStatusUrl);
            // {statusUrl} inherits a status URL already checked against these origins by the runner.
            if (!(allowStatusUrl && template.StartsWith("{statusUrl}", StringComparison.Ordinal)) &&
                !trusted.Contains(uri.GetLeftPart(UriPartial.Authority)))
                throw HttpOperationContractInput.Invalid();
            if (template.Contains("{operationId}", StringComparison.Ordinal) && status.OperationIdField is null)
                throw HttpOperationContractInput.Invalid();
        }
    }
}

/// <summary>
/// Status GETs use Location, or the declared ID field and URL template. Field names refer to top-level
/// JSON properties; state values are distinct, ordinal strings. Result/error fields are optional.
/// Retention is the service's guaranteed status availability, not a client timeout.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpOperationStatusContract
{
    public string StateField { get; }
    public TimeSpan Retention { get; }
    public string? LocationHeader { get; }
    public string? OperationIdField { get; }
    public string? UrlTemplate { get; }
    public string Pending { get; }
    public string Running { get; }
    public string Succeeded { get; }
    public string Failed { get; }
    public string Cancelled { get; }
    public string? ResultField { get; }
    public string? ErrorField { get; }

    public HttpOperationStatusContract(string stateField, TimeSpan retention, string? locationHeader = "Location",
        string? operationIdField = null, string? urlTemplate = null, string pending = "Pending",
        string running = "Running", string succeeded = "Succeeded", string failed = "Failed",
        string cancelled = "Cancelled", string? resultField = null, string? errorField = null)
    {
        HttpOperationContractInput.Field(stateField);
        HttpOperationContractInput.Retention(retention);
        if (locationHeader is not null)
            HttpOperationContractInput.Header(locationHeader);
        if (operationIdField is not null)
            HttpOperationContractInput.Field(operationIdField);
        if (urlTemplate is not null)
        {
            HttpOperationContractInput.Template(urlTemplate, "{operationId}");
            if (operationIdField is null)
                throw HttpOperationContractInput.Invalid();
        }
        if (locationHeader is null && urlTemplate is null)
            throw HttpOperationContractInput.Invalid();
        string[] states = [pending, running, succeeded, failed, cancelled];
        foreach (var state in states)
            HttpOperationContractInput.Field(state);
        if (states.Distinct(StringComparer.Ordinal).Count() != states.Length)
            throw HttpOperationContractInput.Invalid();
        if (resultField is not null) HttpOperationContractInput.Field(resultField);
        if (errorField is not null) HttpOperationContractInput.Field(errorField);
        StateField = stateField;
        Retention = retention;
        LocationHeader = locationHeader;
        OperationIdField = operationIdField;
        UrlTemplate = urlTemplate;
        Pending = pending;
        Running = running;
        Succeeded = succeeded;
        Failed = failed;
        Cancelled = cancelled;
        ResultField = resultField;
        ErrorField = errorField;
    }
}

public enum HttpOperationCancelMethod { Post, Delete }

/// <summary>
/// Cancellation URL uses {operationId}, or starts with {statusUrl} and appends a literal path.
/// A successful request alone does not confirm remote cancellation; the status contract does.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpOperationCancelContract
{
    public string UrlTemplate { get; }
    public HttpOperationCancelMethod Method { get; }
    public TimeSpan RequestTimeout { get; }

    public HttpOperationCancelContract(string urlTemplate, HttpOperationCancelMethod method = HttpOperationCancelMethod.Post,
        TimeSpan? requestTimeout = null)
        : this(urlTemplate, requestTimeout ?? TimeSpan.FromSeconds(10), method) { }

    [JsonConstructor]
    public HttpOperationCancelContract(string urlTemplate, TimeSpan requestTimeout,
        HttpOperationCancelMethod method = HttpOperationCancelMethod.Post)
    {
        HttpOperationContractInput.Template(urlTemplate, "{operationId}", allowStatusUrl: true);
        if (!Enum.IsDefined(method) || requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(10))
            throw HttpOperationContractInput.Invalid();
        UrlTemplate = urlTemplate;
        Method = method;
        RequestTimeout = requestTimeout;
    }
}

/// <summary>The service guarantees same-key submissions create at most one job for Retention.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpOperationDeduplicationContract
{
    public string HeaderName { get; }
    public TimeSpan Retention { get; }

    public HttpOperationDeduplicationContract(string headerName, TimeSpan retention)
    {
        HttpOperationContractInput.Header(headerName);
        HttpOperationContractInput.Retention(retention);
        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            throw HttpOperationContractInput.Invalid();
        HeaderName = headerName;
        Retention = retention;
    }
}

/// <summary>
/// Authoritative GET lookup by {submissionKey}, returning the same Location/ID contract as submission.
/// A miss NEVER authorizes resubmission without a deduplication guarantee.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpOperationLookupContract
{
    public string UrlTemplate { get; }
    public TimeSpan Retention { get; }
    public bool Authoritative { get; }

    public HttpOperationLookupContract(string urlTemplate, TimeSpan retention, bool authoritative = true)
    {
        HttpOperationContractInput.Template(urlTemplate, "{submissionKey}");
        HttpOperationContractInput.Retention(retention);
        if (!authoritative)
            throw HttpOperationContractInput.Invalid();
        UrlTemplate = urlTemplate;
        Retention = retention;
        Authoritative = authoritative;
    }
}

internal static class HttpOperationContractInput
{
    internal static ArgumentException Invalid() => new("Invalid HTTP operation contract.");

    internal static void Field(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            throw Invalid();
    }

    internal static void Header(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)))
            throw Invalid();
    }

    internal static void Retention(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(365))
            throw Invalid();
    }

    internal static Uri Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '{' or '}') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || value.Contains('?') || value.Contains('#') ||
            Uri.UnescapeDataString(value).Any(char.IsControl))
            throw Invalid();
        return uri;
    }

    internal static Uri Template(string? value, string placeholder, bool allowStatusUrl = false)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048)
            throw Invalid();
        if (allowStatusUrl && value.StartsWith("{statusUrl}", StringComparison.Ordinal))
        {
            var suffix = value["{statusUrl}".Length..];
            if (suffix.Length > 0 && !suffix.StartsWith('/'))
                throw Invalid();
            return Url("https://status.invalid/operation" + suffix);
        }
        if (!value.Contains(placeholder, StringComparison.Ordinal) ||
            value.IndexOf(placeholder, StringComparison.Ordinal) != value.LastIndexOf(placeholder, StringComparison.Ordinal))
            throw Invalid();
        var uri = Url(value.Replace(placeholder, "wf-reference", StringComparison.Ordinal));
        // A reference may vary only a path segment, never an origin.
        var authorityEnd = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        if (authorityEnd < 0 || value.IndexOf(placeholder, StringComparison.Ordinal) <= authorityEnd)
            throw Invalid();
        return uri;
    }
}
