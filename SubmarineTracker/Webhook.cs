using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SubmarineTracker;

public static class Webhook
{
    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan MinimumSendInterval = TimeSpan.FromMilliseconds(500);
    private static DateTimeOffset NextSendAtUtc = DateTimeOffset.MinValue;

    private sealed class RateLimitResponse
    {
        [JsonProperty("message")] public string? Message { get; set; }
        [JsonProperty("retry_after")] public double RetryAfterSeconds { get; set; }
        [JsonProperty("global")] public bool Global { get; set; }
        [JsonProperty("code")] public int? Code { get; set; }
    }

    public struct WebhookContent
    {
        [JsonProperty("username")] public string Username = "[Submarine Tracker]";
        [JsonProperty("avatar_url")] public string AvatarUrl = "https://raw.githubusercontent.com/Infiziert90/SubmarineTracker/master/SubmarineTracker/images/icon.png";
        [JsonProperty("embeds")] public List<object> Embeds = [];

        public WebhookContent() { }
    }

    public static void PostMessage(WebhookContent webhookContent)
    {
        _ = Task.Run(() => PostMessageAsync(webhookContent));
    }

    private static async Task PostMessageAsync(WebhookContent webhookContent)
    {
        try
        {
            var payload = JsonConvert.SerializeObject(webhookContent);
            await SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
                {
                    await WaitForThrottleWindowAsync().ConfigureAwait(false);

                    using var request = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = await Client.PostAsync(Plugin.Configuration.WebhookUrl, request).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        NextSendAtUtc = DateTimeOffset.UtcNow + MinimumSendInterval;
                        return;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode == (HttpStatusCode)429 && attempt < MaxRateLimitRetries)
                    {
                        var delay = GetRetryDelay(responseBody);
                        NextSendAtUtc = DateTimeOffset.UtcNow + delay;
                        Plugin.Log.Warning("Discord webhook rate limited. Retrying after {DelayMs}ms (attempt {Attempt}/{MaxAttempts}).", (int)delay.TotalMilliseconds, attempt + 1, MaxRateLimitRetries);
                        continue;
                    }

                    Plugin.Log.Warning("Discord webhook failed with status code {StatusCode}.", response.StatusCode);
                    Plugin.Log.Warning(responseBody);
                    NextSendAtUtc = DateTimeOffset.UtcNow + MinimumSendInterval;
                    return;
                }
            }
            finally
            {
                SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Webhook post failed");
        }
    }

    private static TimeSpan GetRetryDelay(string responseBody)
    {
        try
        {
            var rateLimit = JsonConvert.DeserializeObject<RateLimitResponse>(responseBody);
            if (rateLimit?.RetryAfterSeconds > 0)
                return TimeSpan.FromMilliseconds(Math.Ceiling(rateLimit.RetryAfterSeconds * 1000d) + 250d);
        }
        catch (JsonException)
        {
            // Fall back to a short delay if the rate-limit body cannot be parsed.
        }

        return TimeSpan.FromSeconds(1);
    }

    private static async Task WaitForThrottleWindowAsync()
    {
        var delay = NextSendAtUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay).ConfigureAwait(false);
    }
}
