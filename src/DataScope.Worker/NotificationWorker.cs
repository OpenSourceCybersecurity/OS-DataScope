using System.Net;
using System.Net.Mail;
using DataScope.Persistence;

namespace DataScope.Worker;

/// <summary>Email settings (section <c>Notifications</c>). Email goes only to the organisation's own SMTP server.</summary>
public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    /// <summary>The address people use to open Data Scope, put in review emails. Defaults to https://this-computer:port/.</summary>
    public string? PublicUrl { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>How often review reminders are worked out.</summary>
    public int ReminderMinutes { get; set; } = 15;

    /// <summary>Where review emails send people: the owner portal.</summary>
    public string ReviewLink => (PublicUrl ?? "https://localhost/").TrimEnd('/') + "/#my-reviews";

    public bool EmailConfigured => !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(Smtp.From);
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? From { get; set; }

    /// <summary>Require STARTTLS. Turning it off is only allowed in Development.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Optional. Most on-premises relays accept the server without a password.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }
}

/// <summary>Sends one email. Implemented with the organisation's SMTP server; tests use a fake.</summary>
public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken ct);
}

/// <summary>
/// SMTP with STARTTLS through <see cref="SmtpClient"/>: enough for an internal relay, and no extra dependency.
/// Implicit TLS (port 465) is not supported.
/// </summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(string recipient, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        if (!string.IsNullOrEmpty(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }
        using var message = new MailMessage(options.From!, recipient, subject, body) { IsBodyHtml = false };
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>
/// Works out review reminders and sends the outbox. When email is not set up, messages are marked Skipped and reviews
/// are still reachable in Data Scope.
/// </summary>
public sealed partial class NotificationWorker(
    IServiceScopeFactory scopes, NotificationOptions options, ILogger<NotificationWorker> log, TimeProvider clock, IEmailSender? sender = null,
    Func<NotificationOptions>? current = null, Func<SmtpOptions, IEmailSender>? senderFor = null) : BackgroundService
{
    private DateTimeOffset _nextReminders = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (Persistence.DatabaseOutage.Is(ex))
            {
                LogDatabaseUnavailable(ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>One pass: reminders when due, then every outbox message that is due.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Read on every pass, so mail server changes on System settings apply within a minute (D123).
        var settings = current?.Invoke() ?? options;
        var (mailer, unready) = Choose(settings);
        await using var scope = scopes.CreateAsyncScope();
        if (clock.GetUtcNow() >= _nextReminders)
        {
            var added = await scope.ServiceProvider.GetRequiredService<ReviewReminders>().RunAsync(settings.ReviewLink, ct);
            _nextReminders = clock.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(1, settings.ReminderMinutes));
            if (added > 0)
            {
                LogQueued(added);
            }
        }
        var outbox = scope.ServiceProvider.GetRequiredService<NotificationStore>();
        while (true)
        {
            var batch = await outbox.ClaimDueAsync(20, ct);
            if (batch.Count == 0)
            {
                return;
            }
            foreach (var message in batch)
            {
                if (mailer is null)
                {
                    await outbox.SkipAsync(message.Id, unready!, ct);
                    continue;
                }
                try
                {
                    await mailer.SendAsync(message.RecipientEmail!, message.Subject, message.Body, ct);
                    await outbox.MarkSentAsync(message.Id, ct);
                }
                catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException or FormatException)
                {
                    // The type and the server's status only; never the message body.
                    var error = ex switch
                    {
                        SmtpException smtp => $"SMTP error {smtp.StatusCode}: {smtp.Message}",
                        _ => $"{ex.GetType().Name}: {ex.Message}",
                    };
                    LogSendFailed(message.Id, error);
                    await outbox.MarkFailedAsync(message.Id, error, ct);
                }
            }
        }
    }

    /// <summary>The sender for this pass, or why there is none: the mail server, when it is set up. A test's sender stands in for it.</summary>
    private (IEmailSender? Mailer, string? Unready) Choose(NotificationOptions settings) =>
        settings.EmailConfigured && (sender ?? senderFor?.Invoke(settings.Smtp)) is { } smtp
            ? (smtp, null)
            : (null, "Email is not set up (a mail server on System settings). The review is still waiting in Data Scope.");

    [LoggerMessage(LogLevel.Information, "Queued {Count} review emails")]
    private partial void LogQueued(int count);

    [LoggerMessage(LogLevel.Warning, "Email {MessageId} was not sent: {Error}")]
    private partial void LogSendFailed(long messageId, string error);

    [LoggerMessage(LogLevel.Warning, "Database unavailable for notifications ({ExceptionType}); retrying")]
    private partial void LogDatabaseUnavailable(string exceptionType);
}
