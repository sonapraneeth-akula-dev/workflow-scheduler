using System.Globalization;
using System.Net.Mail;
using System.Text.Json.Serialization;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api.Subscriptions;

public sealed record SubscriptionScope(string Kind, string Id)
{
    public static SubscriptionScope Parse(string kind, string id)
    {
        if (kind == "workflow" && WorkflowIdentity.TryGetTypeScope(id, out _) ||
            kind == "type" && WorkflowIdentity.TryGetTypeScope(id + "/workflow", out _) ||
            kind == "schedule" && IsUuid(id))
            return new(kind, id);
        throw new ArgumentException("Use workflow team/type/workflow, type team/type, or a canonical nonempty schedule UUID.");
    }

    internal static bool IsUuid(string? value) => Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty && value == id.ToString("D");
}

public sealed record SubscriptionRequest(string Scope, string ScopeId, string[] Events, string Channel, string Target,
    string? SecretRef = null)
{
    public SubscriptionRequest Validate()
    {
        SubscriptionScope.Parse(Scope, ScopeId);
        if (Events is null || Events.Length is < 1 or > 2 ||
            Events.Any(value => value is not ("Completed" or "Failed")) ||
            Events.Distinct(StringComparer.Ordinal).Count() != Events.Length)
            throw new ArgumentException("Use distinct Completed and/or Failed events. Completed includes every terminal outcome.");
        if (Target is null || Target.Length is < 1 or > 2048 || Target != Target.Trim() || Target.Any(char.IsControl))
            throw new ArgumentException("Supply one destination without surrounding whitespace or control characters.");
        var target = Target;
        switch (Channel)
        {
            case "Webhook":
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                    !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query) ||
                    uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
                    !SecretReference.IsValidName(SecretRef))
                    throw new ArgumentException("Webhook requires an uncredentialed HTTPS URL (loopback HTTP for local use) without query/fragment, and a signing secret reference, never a literal key.");
                target = uri.AbsoluteUri;
                break;
            case "Email":
                if (!MailAddress.TryCreate(target, out var address) || address.Address != target || SecretRef is not null)
                    throw new ArgumentException("Email requires one mailbox without a display name and no signing secret reference.");
                break;
            case "Teams":
                if (!SecretReference.IsValidName(target) || SecretRef is not null)
                    throw new ArgumentException("Teams requires a configured destination secret reference, never a workflow webhook URL or literal credential.");
                break;
            default:
                throw new ArgumentException("Use Webhook, Email or Teams channel.");
        }
        return this with { Target = target, Events = Events.Order(StringComparer.Ordinal).ToArray() };
    }
}

public sealed record SubscriptionRow(Guid Id, string Scope, string ScopeId, string[] Events, string Channel,
    string Target, string? SecretRef, DateTimeOffset CreatedAt);
public sealed record SubscriptionPage(SubscriptionRow[] Items, string? NextCursor);
public sealed record SubscriptionQuery(SubscriptionScope? Scope, int Limit, Guid? After)
{
    public static SubscriptionQuery Parse(IQueryCollection query)
    {
        if (query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("scope" or "scopeId" or "limit" or "cursor")))
            throw new ArgumentException("Use distinct scope, scopeId, limit and cursor filters.");
        SubscriptionScope? scope = null;
        if (query.ContainsKey("scope") || query.ContainsKey("scopeId"))
            scope = SubscriptionScope.Parse(query["scope"].ToString(), query["scopeId"].ToString());
        var limit = 50;
        if (query.TryGetValue("limit", out var size) &&
            (!int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                limit is < 1 or > 100 || size[0] != limit.ToString(CultureInfo.InvariantCulture)))
            throw new ArgumentException("limit must be a canonical integer from 1 to 100.");
        Guid? after = null;
        if (query.TryGetValue("cursor", out var cursor))
        {
            if (!SubscriptionScope.IsUuid(cursor[0])) throw new ArgumentException("Use the canonical subscription UUID cursor.");
            after = Guid.ParseExact(cursor[0]!, "D");
        }
        return new(scope, limit, after);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SubscriptionRequest))]
[JsonSerializable(typeof(SubscriptionRow))]
[JsonSerializable(typeof(SubscriptionPage))]
public partial class SubscriptionJson : JsonSerializerContext;
