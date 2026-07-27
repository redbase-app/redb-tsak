using System.Net;
using System.Net.Mail;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Email delivery over SMTP using the BCL <see cref="SmtpClient"/> — no extra package. Adequate
/// for fire-and-forget operational alerts; each send opens and closes its own connection.
/// </summary>
public sealed class EmailAlertChannel : IAlertChannel
{
    private readonly EmailChannelOptions _options;

    public EmailAlertChannel(EmailChannelOptions options)
    {
        _options = options;
    }

    public string Name => "email";

    public bool IsEnabled =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.Host)
        && !string.IsNullOrWhiteSpace(_options.From)
        && !string.IsNullOrWhiteSpace(_options.To);

    public async Task SendAsync(AlertNotification alert, CancellationToken ct)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrEmpty(_options.Username))
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);

        using var message = new MailMessage(_options.From, _options.To)
        {
            Subject = $"[Tsak {alert.Level}] {alert.Title}",
            Body = alert.ToText()
        };

        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}
