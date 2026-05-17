using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DynamoCopilot.Server.Services;

public class AdminNotifier(IHttpClientFactory httpFactory, IConfiguration config, ILogger<AdminNotifier> logger)
{
    private static readonly string _resendUrl = "https://api.resend.com/emails";

    public async Task NotifyNewRegistrationAsync(string userEmail)
    {
        var apiKey    = config["Resend:ApiKey"];
        var adminEmail = config["Resend:AdminEmail"];

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(adminEmail))
        {
            logger.LogWarning("Resend:ApiKey or Resend:AdminEmail not configured — skipping registration notification.");
            return;
        }

        try
        {
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var payload = new Dictionary<string, object>
            {
                ["from"]    = "DynamoCopilot <info@bimera.com>",
                ["to"]      = new[] { adminEmail },
                ["subject"] = $"New DynamoCopilot registration: {userEmail}",
                ["html"]    = BuildHtml(userEmail)
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(_resendUrl, content);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Resend returned {Status} for registration notification.", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send registration notification for {Email}.", userEmail);
        }
    }

    private static string BuildHtml(string userEmail) =>
        $"<p>A new user registered for <strong>DynamoCopilot</strong>.</p>" +
        $"<p><strong>Email:</strong> {userEmail}</p>" +
        "<hr/>" +
        "<p>They automatically received a free <strong>Suggest Nodes</strong> licence. " +
        "To grant them a <strong>Copilot</strong> licence, use the admin dashboard or run:</p>" +
        "<pre style=\"background:#f4f4f4;padding:12px;border-radius:4px\">" +
        "POST /admin/grant\nContent-Type: application/json\n\n" +
        $"{{\n  \"email\": \"{userEmail}\",\n  \"extension\": \"Copilot\",\n  \"months\": 12\n}}" +
        "</pre>" +
        "<p><a href=\"https://copilot-dynamo-extension-production.up.railway.app/Dashboard/Users\">Open Dashboard &rarr; Users</a></p>";
}
