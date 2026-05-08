using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Mimir.Api.Services.Email;

/// <summary>
/// Gerçek SMTP gönderim. Smtp:Host configuration set ise Program.cs DI'da bu register edilir,
/// yoksa <see cref="ConsoleEmailSender"/> fallback (mock).
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        var host = _config["Smtp:Host"]!;
        var port = int.TryParse(_config["Smtp:Port"], out var p) ? p : 587;
        var user = _config["Smtp:User"];
        var pass = _config["Smtp:Password"];
        var fromAddr = _config["Smtp:From"] ?? user ?? "noreply@aykutonpc.com";
        var fromName = _config["Smtp:FromName"] ?? "Mimir";

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, fromAddr));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls, ct);
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                await client.AuthenticateAsync(user, pass, ct);
            await client.SendAsync(msg, ct);
            _logger.LogInformation("📧 Email gönderildi: To={To} Subject={Subject}", toEmail, subject);
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, ct);
        }
    }
}
