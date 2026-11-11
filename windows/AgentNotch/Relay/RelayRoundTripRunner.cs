namespace AgentNotch.Relay;

/// <summary>Console ping against live Odysseus — no UI.</summary>
internal static class RelayRoundTripRunner
{
    public static async Task<int> RunAsync()
    {
        var url = RelayConfig.TryGetBaseUrl();
        if (url is null)
        {
            Console.WriteLine("relay round-trip: no baseUrl (set relay.json or AGENT_NOTCH_RELAY_URL)");
            return 1;
        }
        if (RelayConfig.TryGetApiToken() is null)
        {
            Console.WriteLine("relay round-trip: ODYSSEUS_API_TOKEN not set");
            return 1;
        }

        using var client = new OdysseusRelayClient(url);
        await Task.Delay(1500);
        Console.WriteLine($"relay round-trip: connected={client.IsConnected} status={client.StatusText}");
        if (!client.IsConnected)
            return 1;

        const string prompt = "Reply with exactly the word pong and nothing else.";
        var acc = "";
        void OnToken(string t) => acc += t;
        client.TokenReceived += OnToken;
        try
        {
            await client.SendUserMessageAsync(prompt);
        }
        finally
        {
            client.TokenReceived -= OnToken;
        }

        var fromStream = acc.Length > 0;
        var assistant = client.Transcript.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
        var body = fromStream ? acc : assistant;
        var excerpt = string.IsNullOrEmpty(body)
            ? "(no reply)"
            : body.Length <= 80 ? body : body[..80] + "…";
        var ok = !string.IsNullOrWhiteSpace(body) && !body.StartsWith("Relay ", StringComparison.Ordinal);
        Console.WriteLine($"relay round-trip: reply_received={ok} excerpt={excerpt}");
        return ok ? 0 : 2;
    }
}
