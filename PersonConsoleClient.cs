using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

public static class PersonConsoleClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object ConsoleGate = new();

    public static async Task RunAsync(string[] args)
    {
        var serverUrl = GetServerUrl(args);

        WriteLine("Cupidon person registration");
        WriteLine($"Server: {serverUrl}");

        var username = ReadUsername();
        var city = ReadRequiredText("City");
        var age = ReadPositiveInt("Age", 120);
        var phone = ReadPhone();

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(serverUrl)
        };

        var request = new RegisterPersonRequest(username, city, age, phone);
        using var response = await httpClient.PostAsJsonAsync("/people/init-single-person", request);

        if (!response.IsSuccessStatusCode)
        {
            await PrintFailureAsync("Registration failed", response);
            return;
        }

        var person = await response.Content.ReadFromJsonAsync<PersonDto>(JsonOptions);
        if (person is null)
        {
            WriteLine("Registration succeeded but the server returned an empty person response.");
            return;
        }

        WriteLine($"Registered {person.Username} ({person.Id}) from {person.City}.");
        await SubscribeForEventsAsync(serverUrl, person.Id);
    }

    private static async Task SubscribeForEventsAsync(string serverUrl, Guid personId)
    {
        using var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource();
        var state = new SubscriptionState();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var socketUri = BuildSocketUri(serverUrl, personId);
        await socket.ConnectAsync(socketUri, cancellation.Token);

        WriteLine("Subscribed through WebSocket.");
        WriteLine("Commands: /block username, or press Enter to confirm the current letter. Press Ctrl+C to exit.");

        var receiveTask = ReceiveEventsAsync(socket, state, cancellation.Token);
        var inputTask = ReadCommandsAsync(socket, state, cancellation.Token);

        var completedTask = await Task.WhenAny(receiveTask, inputTask);
        await cancellation.CancelAsync();

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Console client stopped", CancellationToken.None);
        }

        await ObserveCompletedTaskAsync(completedTask);
        WriteLine("Subscription stopped.");
    }

    private static async Task ReceiveEventsAsync(
        ClientWebSocket socket,
        SubscriptionState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var messageJson = await ReceiveTextMessageAsync(socket, cancellationToken);
            if (messageJson is null)
            {
                break;
            }

            PubSubEventDto<JsonElement>? pubSubEvent;
            try
            {
                pubSubEvent = JsonSerializer.Deserialize<PubSubEventDto<JsonElement>>(messageJson, JsonOptions);
            }
            catch (JsonException)
            {
                WriteLine("Received an invalid event payload from the server.");
                continue;
            }

            if (pubSubEvent is null)
            {
                WriteLine("Received an empty event from the server.");
                continue;
            }

            HandleServerEvent(pubSubEvent, state);
        }
    }

    private static async Task ReadCommandsAsync(
        ClientWebSocket socket,
        SubscriptionState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var line = await Task.Run(Console.ReadLine, cancellationToken);
            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                await SendAcknowledgementAsync(socket, state, cancellationToken);
                continue;
            }

            if (trimmed.StartsWith("/block ", StringComparison.OrdinalIgnoreCase))
            {
                var blockedUsername = trimmed["/block ".Length..].Trim();
                await SendBlockCommandAsync(socket, blockedUsername, cancellationToken);
                continue;
            }

            WriteLine("Unknown command. Use /block username, or press Enter to acknowledge the current letter.");
        }
    }

    private static void HandleServerEvent(
        PubSubEventDto<JsonElement> pubSubEvent,
        SubscriptionState state)
    {
        switch (pubSubEvent.Type)
        {
            case CupidEventTypes.LetterReceived:
                var letter = pubSubEvent.Payload.Deserialize<LetterDto>(JsonOptions);
                if (letter is null)
                {
                    WriteLine("Received an empty letter event.");
                    return;
                }

                state.SetPendingLetter(letter.Id);
                PrintLetter(letter);
                WriteLine("Press Enter to confirm that you received this letter.");
                break;

            case CupidEventTypes.LetterAcknowledged:
                var acknowledgement = pubSubEvent.Payload.Deserialize<AcknowledgedLetterDto>(JsonOptions);
                WriteLine(acknowledgement is null
                    ? "Letter acknowledged."
                    : $"Letter acknowledged: {acknowledgement.LetterId}");
                break;

            case CupidEventTypes.UserBlocked:
                var block = pubSubEvent.Payload.Deserialize<BlockUserResponseDto>(JsonOptions);
                WriteLine(block is null
                    ? "User blocked."
                    : $"Blocked {block.Username} ({block.BlockedPersonId}).");
                break;

            case CupidEventTypes.SocketError:
                var error = pubSubEvent.Payload.Deserialize<SocketErrorDto>(JsonOptions);
                WriteLine(error?.Message ?? "The server returned an unspecified socket error.");
                break;

            default:
                WriteLine($"Received event {pubSubEvent.Type} ({pubSubEvent.EventId}).");
                break;
        }
    }

    private static async Task SendAcknowledgementAsync(
        ClientWebSocket socket,
        SubscriptionState state,
        CancellationToken cancellationToken)
    {
        if (!state.TryTakePendingLetter(out var letterId))
        {
            WriteLine("There is no pending letter to acknowledge.");
            return;
        }

        await SendCommandAsync(
            socket,
            new ClientSocketMessage(CupidCommandTypes.AcknowledgeLetter, letterId, null),
            cancellationToken);
    }

    private static async Task SendBlockCommandAsync(
        ClientWebSocket socket,
        string blockedUsername,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blockedUsername))
        {
            WriteLine("Usage: /block username");
            return;
        }

        await SendCommandAsync(
            socket,
            new ClientSocketMessage(CupidCommandTypes.BlockUser, null, blockedUsername),
            cancellationToken);
    }

    private static async Task SendCommandAsync(
        ClientWebSocket socket,
        ClientSocketMessage message,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            WriteLine("Socket is not connected.");
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                WriteLine("Ignoring non-text socket message.");
                continue;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static void PrintLetter(LetterDto letter)
    {
        WriteLine("");
        WriteLine($"New love letter: {letter.Id}");
        WriteLine($"From: {letter.Sender.Username} ({letter.Sender.Id})");
        WriteLine($"City: {letter.Sender.City}");
        WriteLine($"Age: {letter.Sender.Age}");

        if (letter.PhoneVisible)
        {
            WriteLine($"Phone: {letter.Phone}");
        }

        WriteLine($"Message: {letter.Message}");
        WriteLine($"Score: {letter.Score}");
        WriteLine("");
    }

    private static async Task PrintFailureAsync(string prefix, HttpResponseMessage response)
    {
        var error = await response.Content.ReadAsStringAsync();
        WriteLine($"{prefix} ({(int)response.StatusCode}): {error}");
    }

    private static string GetServerUrl(string[] args)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, "--server", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1].TrimEnd('/');
        }

        return "http://localhost:5050";
    }

    private static Uri BuildSocketUri(string serverUrl, Guid personId)
    {
        var serverUri = new Uri(serverUrl.TrimEnd('/') + "/");
        var builder = new UriBuilder(serverUri)
        {
            Scheme = string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = $"/people/{personId}/events",
            Query = string.Empty
        };

        return builder.Uri;
    }

    private static string ReadUsername()
    {
        while (true)
        {
            var value = ReadRequiredText("Username");
            if (value.Length > 32 || value.Any(character => !IsAllowedUsernameCharacter(character)))
            {
                WriteLine("Username may contain only letters, digits, '.', '_' and '-', up to 32 characters.");
                continue;
            }

            return value;
        }
    }

    private static string ReadRequiredText(string label)
    {
        while (true)
        {
            Write($"{label}: ");
            var value = Console.ReadLine()?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                WriteLine($"{label} is required. Please enter a value.");
                continue;
            }

            if (value.Any(char.IsControl))
            {
                WriteLine($"{label} cannot contain control characters.");
                continue;
            }

            return value;
        }
    }

    private static int ReadPositiveInt(string label, int maxValue)
    {
        while (true)
        {
            Write($"{label}: ");
            var raw = Console.ReadLine()?.Trim() ?? string.Empty;

            if (raw.Length == 0)
            {
                WriteLine($"{label} is required. Please enter a number.");
                continue;
            }

            if (!int.TryParse(raw, out var value))
            {
                WriteLine($"{label} must be a number. Characters are not allowed.");
                continue;
            }

            if (value <= 0)
            {
                WriteLine($"{label} must be a positive number.");
                continue;
            }

            if (value > maxValue)
            {
                WriteLine($"{label} must be less than or equal to {maxValue}.");
                continue;
            }

            return value;
        }
    }

    private static string ReadPhone()
    {
        while (true)
        {
            Write("Phone: ");
            var value = Console.ReadLine()?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                WriteLine("Phone is required. Please enter a phone number.");
                continue;
            }

            if (value.StartsWith("-", StringComparison.Ordinal))
            {
                WriteLine("Phone cannot be a negative number.");
                continue;
            }

            if (!value.Any(char.IsDigit) || value.Any(character => !IsAllowedPhoneCharacter(character)))
            {
                WriteLine("Phone may contain only digits, spaces, '+', '-', '(' and ')'.");
                continue;
            }

            return value;
        }
    }

    private static bool IsAllowedUsernameCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '_' or '-';
    }

    private static bool IsAllowedPhoneCharacter(char character)
    {
        return char.IsDigit(character) || character is ' ' or '+' or '-' or '(' or ')';
    }

    private static async Task ObserveCompletedTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C stops the socket session.
        }
        catch (WebSocketException exception)
        {
            WriteLine($"Socket closed: {exception.Message}");
        }
    }

    private static void Write(string value)
    {
        lock (ConsoleGate)
        {
            Console.Write(value);
        }
    }

    private static void WriteLine(string value)
    {
        lock (ConsoleGate)
        {
            Console.WriteLine(value);
        }
    }

    private sealed class SubscriptionState
    {
        private readonly object _gate = new();
        private Guid? _pendingLetterId;

        public void SetPendingLetter(Guid letterId)
        {
            lock (_gate)
            {
                _pendingLetterId = letterId;
            }
        }

        public bool TryTakePendingLetter(out Guid letterId)
        {
            lock (_gate)
            {
                if (_pendingLetterId is null)
                {
                    letterId = default;
                    return false;
                }

                letterId = _pendingLetterId.Value;
                _pendingLetterId = null;
                return true;
            }
        }
    }
}
