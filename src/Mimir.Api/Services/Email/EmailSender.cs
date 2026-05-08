namespace Mimir.Api.Services.Email;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}

/// <summary>
/// MVP mock — gerçek SMTP yerine log'a yazar. Sprint #2 sonu öncesi gerçek SMTP impl.
/// </summary>
public class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) => _logger = logger;

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "📧 [MOCK EMAIL] To={To} | Subject={Subject}\n{Body}",
            toEmail, subject, body);
        return Task.CompletedTask;
    }
}
