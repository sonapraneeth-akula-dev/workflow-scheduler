using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Wf.ControlPlane.Api.Subscriptions;

namespace Wf.ControlPlane.Engine.Notifications;

public sealed class EmailChannel
{
    public string TypeScope { get; }
    public string Target { get; }
    private readonly string host;
    private readonly int port;
    private readonly bool enableSsl;
    private readonly string from;
    private readonly NetworkCredential? credentials;

    internal EmailChannel(IConfiguration section)
    {
        try
        {
            TypeScope = SubscriptionScope.Parse("type", section["TypeScope"] ?? "").Id;
            Target = new SubscriptionRequest("type", TypeScope, ["Completed"], "Email", section["Target"]!).Validate().Target;
            from = new SubscriptionRequest("type", TypeScope, ["Completed"], "Email", section["From"]!).Validate().Target;
            host = section["Host"] ?? "";
            if (host.Length is < 1 or > 253 || host != host.Trim() || host.Any(char.IsControl) ||
                Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException("Invalid SMTP host.");
            if (IPAddress.TryParse(host, out _)) NotificationHttpClient.Address(host);
            port = section.GetValue("Port", 587);
            enableSsl = section.GetValue("EnableSsl", true);
            var username = section["Username"];
            var password = section["Password"];
            if (port is < 1 or > 65535 ||
                !enableSsl && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address) || username is not null) ||
                (username is null) != (password is null) ||
                username is not null && (username.Length is < 1 or > 320 || username.Any(char.IsControl) || password!.Length is < 1 or > 4096))
                throw new ArgumentException("Invalid SMTP settings.");
            credentials = username is null ? null : new NetworkCredential(username, password);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Invalid notification email configuration; check scope, mailbox, sender, SMTP host, port, TLS and credentials.");
        }
    }

    public async Task SendAsync(byte[] body, CancellationToken token)
    {
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        using var message = new MailMessage(from, Target)
        {
            Subject = $"Kardia: {data.GetProperty("status").GetString()} - {data.GetProperty("workflowId").GetString()}",
            SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8, IsBodyHtml = false,
            Body = string.Join("\n", data.EnumerateObject().Select(property => property.Name + ": " +
                (property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.GetString())))
        };
        message.Headers.Add("Message-ID", $"<{data.GetProperty("runId").GetString()}@notifications.kardia>");
        using var client = new SmtpClient(host, port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network, EnableSsl = enableSsl,
            UseDefaultCredentials = false, Credentials = credentials, Timeout = 10000
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await client.SendMailAsync(message, deadline.Token); }
        catch (SmtpException) when (deadline.IsCancellationRequested)
        {
            throw new OperationCanceledException(deadline.Token);
        }
    }
}
