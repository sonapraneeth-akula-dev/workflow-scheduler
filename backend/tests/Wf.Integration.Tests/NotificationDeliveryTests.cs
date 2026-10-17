using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Wf.Contracts;
using Wf.ControlPlane.Api.Subscriptions;
using Wf.ControlPlane.Engine.Notifications;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed partial class TerminalNotificationTests
{
    [Fact]
    public async Task Real_user_subscriptions_deliver_webhook_email_and_Teams_under_30_seconds_with_independent_retries()
    {
        await using var webhook = await Receiver.Start(Token, count => count == 1 ? 503 : 204);
        await using var teams = await Receiver.Start(Token, count => count == 1 ? 503 : 202);
        await using var smtp = SmtpReceiver.Start(count => count == 1 ? 451 : 250);
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        foreach (var (channel, target, secret) in new (string, string, string?)[]
        {
            ("Webhook", webhook.Target, Secret), ("Email", "subscriber@example.test", null),
            ("Teams", "secret://notifications/teams", null)
        })
        {
            var request = new SubscriptionRequest("workflow", scope.Workflow, ["Completed"], channel, target, secret);
            using var created = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", api.Jwt(scope.Group),
                new StringContent(JsonSerializer.Serialize(request, SubscriptionJson.Default.SubscriptionRequest),
                    Encoding.UTF8, "application/json"), Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using var options = ChannelOptions(scope, webhook.Target, teams.Target, smtp.Port);
        using var dispatcher = Dispatcher(options);
        var run = await Create(scope);
        var started = Stopwatch.StartNew();
        await Finish(run, RunStatus.Failed);
        await dispatcher.StartAsync(Token);
        try
        {
            var firstWebhook = await webhook.Next(Token);
            var secondWebhook = await webhook.Next(Token);
            VerifySignature(firstWebhook);
            VerifySignature(secondWebhook);
            Assert.Equal(firstWebhook.Body, secondWebhook.Body);
            var firstTeams = await teams.Next(Token);
            var secondTeams = await teams.Next(Token);
            VerifyTeams(firstTeams, run.Snapshot.RunId, scope.Workflow, "Failed");
            VerifyTeams(secondTeams, run.Snapshot.RunId, scope.Workflow, "Failed");
            Assert.Equal(firstTeams.Body, secondTeams.Body);
            var firstEmail = await smtp.Next(Token);
            var secondEmail = await smtp.Next(Token);
            AssertEmail(firstEmail, run.Snapshot.RunId, scope.Workflow);
            AssertEmail(secondEmail, run.Snapshot.RunId, scope.Workflow);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Actual delivery took {started.Elapsed}.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (await Scalar("SELECT count(*) FROM notification_outbox WHERE run_id=$1 AND delivered_at IS NOT NULL", run.Snapshot.RunId) != 3)
                await Task.Delay(20, deadline.Token);
            Assert.Equal(3L, await Scalar("SELECT count(*) FROM notification_outbox WHERE run_id=$1 AND attempts=2 AND failed_at IS NULL AND last_error IS NULL",
                run.Snapshot.RunId));
            Assert.DoesNotContain(smtp.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        }
        finally { await dispatcher.StopAsync(Token); }
    }

    [Theory]
    [InlineData(450, false)]
    [InlineData(451, false)]
    [InlineData(550, true)]
    [InlineData(554, true)]
    public async Task SMTP_statuses_have_bounded_sanitized_retry_or_permanent_state_and_restart_preserves_message_ID(int status, bool permanent)
    {
        await using var smtp = SmtpReceiver.Start(count => count == 1 ? status : 250);
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        var clock = new Clock();
        using var options = ChannelOptions(scope, smtp: smtp.Port);
        using (var dispatcher = Dispatcher(options, clock)) Assert.True(await dispatcher.DispatchOneAsync(Token));
        var state = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, permanent, "smtp_" + status), (state.Attempts, state.Delivered, state.Failed, state.Error));
        Assert.DoesNotContain("private", state.Error);
        using var restarted = Dispatcher(options, clock);
        Assert.False(await restarted.DispatchOneAsync(Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(!permanent, await restarted.DispatchOneAsync(Token));
        Assert.Equal(!permanent, (await State(run.Snapshot.RunId)).Delivered);
        var first = await smtp.Next(Token);
        Assert.Contains($"Message-ID: <{run.Snapshot.RunId}@notifications.kardia>", first);
        if (!permanent) Assert.Contains($"Message-ID: <{run.Snapshot.RunId}@notifications.kardia>", await smtp.Next(Token));
        Assert.Equal(permanent ? 1 : 2, smtp.Messages.Count);
    }

    [Fact]
    public async Task SMTP_recipient_rejection_is_permanent_without_data_or_other_recipient_delivery()
    {
        await using var smtp = SmtpReceiver.Start(recipientStatus: 550);
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, smtp: smtp.Port);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.True(row.Failed);
        Assert.Equal("smtp_550", row.Error);
        Assert.Empty(smtp.Messages);
        Assert.DoesNotContain(smtp.Commands, command => command == "DATA");
    }

    [Fact]
    public async Task SMTP_requiring_TLS_never_downgrades_or_sends_credentials_or_mail_if_STARTTLS_is_missing()
    {
        await using var smtp = SmtpReceiver.Start();
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        var values = ChannelValues(scope, smtp: smtp.Port);
        values["Notifications:Emails:0:EnableSsl"] = "true";
        values["Notifications:Emails:0:Username"] = "private-smtp-user";
        values["Notifications:Emails:0:Password"] = "private-smtp-password";
        using var options = NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal("smtp_unavailable", (await State(run.Snapshot.RunId)).Error);
        Assert.Empty(smtp.Messages);
        Assert.DoesNotContain(smtp.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SMTP_cancellation_rolls_back_attempt_and_stalled_delivery_times_out_without_blocking_the_outbox(bool cancel)
    {
        await using var smtp = SmtpReceiver.Start(stall: true);
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, smtp: smtp.Port);
        using var dispatcher = Dispatcher(options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var timer = Stopwatch.StartNew();
        var send = dispatcher.DispatchOneAsync(cancellation.Token);
        await smtp.Connected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5), Token));
            var row = await State(run.Snapshot.RunId);
            Assert.Equal((0, false, false, (string?)null), (row.Attempts, row.Delivered, row.Failed, row.Error));
        }
        else
        {
            Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(15), Token));
            var row = await State(run.Snapshot.RunId);
            Assert.Equal((1, false, false, "delivery_timeout"), (row.Attempts, row.Delivered, row.Failed, row.Error));
            Assert.InRange(timer.Elapsed.TotalSeconds, 9, 15);
        }
    }

    [Fact]
    public async Task SMTP_STARTTLS_keeps_certificate_verification_and_never_sends_credentials_to_an_untrusted_server()
    {
        await using var smtp = SmtpReceiver.Start(tls: true);
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        var values = ChannelValues(scope, smtp: smtp.Port);
        values["Notifications:Emails:0:EnableSsl"] = "true";
        values["Notifications:Emails:0:Username"] = "private-smtp-user";
        values["Notifications:Emails:0:Password"] = "private-smtp-password";
        using var options = NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, false, "smtp_unavailable"), (row.Attempts, row.Delivered, row.Failed, row.Error));
        Assert.Contains("STARTTLS", smtp.Commands);
        Assert.DoesNotContain(smtp.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(smtp.Messages);
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Teams")]
    public async Task Concurrent_dispatchers_deliver_each_new_channel_once_without_overlapping_attempts(string channel)
    {
        await using var smtp = SmtpReceiver.Start();
        await using var teams = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, channel: channel);
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, teams: teams.Target, smtp: smtp.Port);
        var dispatchers = Enumerable.Range(0, 12).Select(_ => Dispatcher(options)).ToArray();
        try
        {
            var results = await Task.WhenAll(dispatchers.Select(dispatcher => dispatcher.DispatchOneAsync(Token)));
            Assert.Single(results, result => result);
            Assert.True((await State(run.Snapshot.RunId)).Delivered);
            Assert.Equal(channel == "Email" ? 1 : 0, smtp.Messages.Count);
            Assert.Equal(channel == "Teams" ? 1 : 0, teams.Deliveries.Count);
        }
        finally { foreach (var dispatcher in dispatchers) dispatcher.Dispose(); }
    }

    [Fact]
    public async Task SMTP_refused_connection_is_a_sanitized_transient_failure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var scope = await Registry();
        await Subscribe(scope, channel: "Email");
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, smtp: port);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, false, "smtp_unavailable"), (row.Attempts, row.Delivered, row.Failed, row.Error));
    }

    [Theory]
    [InlineData("Email", "type")]
    [InlineData("Email", "target")]
    [InlineData("Teams", "type")]
    [InlineData("Teams", "target")]
    [InlineData("Email", "payload")]
    [InlineData("Teams", "payload")]
    public async Task Unapproved_or_corrupt_new_channel_rows_fail_before_network_access(string channel, string mismatch)
    {
        await using var smtp = SmtpReceiver.Start();
        await using var teams = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, channel: channel, target: mismatch == "target" ?
            channel == "Email" ? "other@example.test" : "secret://notifications/other" : null);
        var run = await Create(scope);
        await Finish(run);
        if (mismatch == "payload")
            await Execute("UPDATE notification_outbox SET payload=payload||'{\"private\":\"never-send\"}'::jsonb WHERE run_id=$1",
                run.Snapshot.RunId);
        using var options = ChannelOptions(mismatch == "type" ? scope with { Type = "foreign/type" } : scope,
            teams: teams.Target, smtp: smtp.Port);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.True(row.Failed);
        Assert.Equal(mismatch == "payload" ? "invalid_cloud_event" : "destination_not_approved", row.Error);
        Assert.Empty(smtp.Commands);
        Assert.Empty(teams.Deliveries);
    }

    [Theory]
    [InlineData(307, false)]
    [InlineData(401, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task Teams_uses_shared_retry_policy_never_follows_redirects_or_leaks_URL_credentials_to_another_host(int status, bool retry)
    {
        await using var forbidden = await Receiver.Start(Token);
        await using var teams = await Receiver.Start(Token, _ => status, redirect: forbidden.Target);
        var scope = await Registry();
        await Subscribe(scope, channel: "Teams");
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, teams: teams.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, !retry, "http_" + status), (row.Attempts, row.Delivered, row.Failed, row.Error));
        Assert.Empty(forbidden.Deliveries);
        Assert.Equal("?sig=private-teams-credential", Assert.Single(teams.Deliveries).Query);
    }

    [Fact]
    public async Task Teams_keeps_normal_TLS_verification_and_rejects_an_untrusted_certificate()
    {
        await using var teams = await Receiver.Start(Token, tls: true);
        var scope = await Registry();
        await Subscribe(scope, channel: "Teams");
        var run = await Create(scope);
        await Finish(run);
        using var options = ChannelOptions(scope, teams: teams.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal("transport_unavailable", (await State(run.Snapshot.RunId)).Error);
        Assert.Empty(teams.Deliveries);
    }

    private static void AssertEmail(string message, Guid run, string workflow)
    {
        Assert.Contains("From: kardia@example.test", message);
        Assert.Contains("To: subscriber@example.test", message);
        Assert.Contains($"Message-ID: <{run}@notifications.kardia>", message);
        Assert.Contains("Subject: Kardia: Failed - " + workflow, Regex.Replace(message, @"\r?\n[ \t]+", " "));
        Assert.Contains("Content-Type: text/plain", message);
        Assert.Contains(run.ToString(), message);
        Assert.DoesNotContain("private", message);
        Assert.DoesNotContain("secret://", message);
    }

    private static void VerifyTeams(Receipt receipt, Guid run, string workflow, string status)
    {
        Assert.Equal("application/json", receipt.ContentType);
        Assert.Empty(receipt.Timestamp);
        Assert.Empty(receipt.Signature);
        Assert.Empty(receipt.Cookie);
        Assert.Equal("?sig=private-teams-credential", receipt.Query);
        using var json = JsonDocument.Parse(receipt.Body);
        Assert.Equal("message", json.RootElement.GetProperty("type").GetString());
        var attachment = Assert.Single(json.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("contentUrl").ValueKind);
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.2", card.GetProperty("version").GetString());
        var facts = card.GetProperty("body")[1].GetProperty("facts").EnumerateArray()
            .ToDictionary(value => value.GetProperty("title").GetString()!, value => value.GetProperty("value").GetString());
        Assert.Equal(6, facts.Count);
        Assert.Equal(run.ToString(), facts["runId"]);
        Assert.Equal(workflow, facts["workflowId"]);
        Assert.Equal(status, facts["status"]);
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(receipt.Body));
        Assert.DoesNotContain("secret://", Encoding.UTF8.GetString(receipt.Body));
    }

    private static NotificationOptions ChannelOptions(Scope scope, string? webhook = null, string? teams = null, int? smtp = null) =>
        NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(ChannelValues(scope, webhook, teams, smtp)).Build());

    private static Dictionary<string, string?> ChannelValues(Scope scope, string? webhook = null, string? teams = null, int? smtp = null)
    {
        var values = new Dictionary<string, string?> { ["Notifications:Enabled"] = "true" };
        if (webhook is not null)
        {
            values["Notifications:Webhooks:0:TypeScope"] = scope.Type;
            values["Notifications:Webhooks:0:Target"] = webhook;
            values["Notifications:Webhooks:0:SecretRef"] = Secret;
            values["Notifications:Webhooks:0:SigningKeyBase64"] = Convert.ToBase64String(Key);
            values["Notifications:Webhooks:0:Addresses:0"] = "127.0.0.1";
        }
        if (teams is not null)
        {
            values["Notifications:Teams:0:TypeScope"] = scope.Type;
            values["Notifications:Teams:0:Target"] = "secret://notifications/teams";
            values["Notifications:Teams:0:Url"] = teams + "?sig=private-teams-credential";
            values["Notifications:Teams:0:Addresses:0"] = "127.0.0.1";
        }
        if (smtp is not null)
        {
            values["Notifications:Emails:0:TypeScope"] = scope.Type;
            values["Notifications:Emails:0:Target"] = "subscriber@example.test";
            values["Notifications:Emails:0:Host"] = "127.0.0.1";
            values["Notifications:Emails:0:Port"] = smtp.Value.ToString(CultureInfo.InvariantCulture);
            values["Notifications:Emails:0:From"] = "kardia@example.test";
            values["Notifications:Emails:0:EnableSsl"] = "false";
        }
        return values;
    }

    private sealed class SmtpReceiver : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Channel<string> received = Channel.CreateUnbounded<string>();
        private readonly Func<int, int>? status;
        private readonly int recipientStatus;
        private readonly bool stall;
        private readonly X509Certificate2? certificate;
        private readonly Task worker;
        public int Port { get; }
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Commands { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();

        private SmtpReceiver(Func<int, int>? status, int recipientStatus, bool stall, bool tls)
        {
            this.status = status;
            this.recipientStatus = recipientStatus;
            this.stall = stall;
            if (tls)
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=untrusted-smtp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            }
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Serve();
        }

        public static SmtpReceiver Start(Func<int, int>? status = null, int recipientStatus = 250, bool stall = false, bool tls = false) =>
            new(status, recipientStatus, stall, tls);

        public async Task<string> Next(CancellationToken token) =>
            await received.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token);

        private async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Connected.TrySetResult();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true, NewLine = "\r\n" };
                    if (stall) { await Task.Delay(Timeout.Infinite, stop.Token); continue; }
                    await writer.WriteLineAsync("220 mock SMTP ready");
                    while (await reader.ReadLineAsync(stop.Token) is { } command)
                    {
                        Commands.Enqueue(command);
                        if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                            await writer.WriteLineAsync(certificate is null ? "250 mock" : "250-mock\r\n250 STARTTLS");
                        else if (command == "STARTTLS" && certificate is not null)
                        {
                            await writer.WriteLineAsync("220 ready for TLS");
                            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                            try
                            {
                                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                    { ServerCertificate = certificate }, stop.Token);
                                Assert.Equal(0, await ssl.ReadAsync(new byte[1], stop.Token));
                            }
                            catch (AuthenticationException) { }
                            catch (IOException) { }
                            break;
                        }
                        else if (command.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase) ||
                            command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) || command == "RSET")
                            await writer.WriteLineAsync("250 mock");
                        else if (command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                            await writer.WriteLineAsync(recipientStatus.ToString(CultureInfo.InvariantCulture) + " private-remote-detail");
                        else if (command == "DATA")
                        {
                            await writer.WriteLineAsync("354 continue");
                            var body = new StringBuilder();
                            while (await reader.ReadLineAsync(stop.Token) is { } line && line != ".")
                            {
                                body.AppendLine(line);
                                if (body.Length > 32 * 1024) throw new InvalidDataException("Unexpected oversized test mail.");
                            }
                            var message = body.ToString();
                            Messages.Enqueue(message);
                            await received.Writer.WriteAsync(message, stop.Token);
                            await writer.WriteLineAsync((status?.Invoke(Messages.Count) ?? 250).ToString(CultureInfo.InvariantCulture) +
                                " private-remote-detail");
                        }
                        else if (command == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                        else throw new InvalidDataException("Unexpected SMTP test command: " + command);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (IOException) when (stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await worker;
            received.Writer.TryComplete();
            certificate?.Dispose();
            stop.Dispose();
        }
    }
}
