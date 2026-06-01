using System.Net.Http.Json;

public static class PersonConsoleClient
{
    private const int SubscribeTimeoutSeconds = 65;

    public static async Task RunAsync(string[] args)
    {
        var serverUrl = GetServerUrl(args);

        Console.WriteLine("Cupidon person registration");
        Console.WriteLine($"Server: {serverUrl}");

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

        var person = await response.Content.ReadFromJsonAsync<PersonDto>();
        var registeredUsername = person?.Username ?? username;

        Console.WriteLine($"Registered {registeredUsername} from {person?.City}.");
        Console.WriteLine("Subscribed for love letters. Press Ctrl+C to exit.");
        await SubscribeForLettersAsync(httpClient, registeredUsername);
    }

    private static async Task SubscribeForLettersAsync(HttpClient httpClient, string username)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        while (!cancellation.IsCancellationRequested)
        {
            HttpResponseMessage response;

            try
            {
                response = await httpClient.GetAsync(
                    $"/people/{Uri.EscapeDataString(username)}/letters/next?timeoutSeconds={SubscribeTimeoutSeconds}",
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    await PrintFailureAsync("Subscription failed", response);
                    return;
                }

                var letter = await response.Content.ReadFromJsonAsync<LetterDto>(cancellation.Token);
                if (letter is null)
                {
                    Console.WriteLine("Received an empty letter response.");
                    continue;
                }

                PrintLetter(letter);

                Console.Write("Press Enter to confirm that you received this letter: ");
                Console.ReadLine();

                using var ackResponse = await httpClient.PostAsync(
                    $"/people/{Uri.EscapeDataString(username)}/letters/ack",
                    null,
                    cancellation.Token);

                if (!ackResponse.IsSuccessStatusCode)
                {
                    await PrintFailureAsync("Acknowledgement failed", ackResponse);
                    return;
                }

                Console.WriteLine("Letter acknowledged. Waiting for the next one...");
            }
        }

        Console.WriteLine("Subscription stopped.");
    }

    private static void PrintLetter(LetterDto letter)
    {
        Console.WriteLine();
        Console.WriteLine("New love letter");
        Console.WriteLine($"From: {letter.Sender.Username}");
        Console.WriteLine($"City: {letter.Sender.City}");
        Console.WriteLine($"Age: {letter.Sender.Age}");

        if (letter.PhoneVisible)
        {
            Console.WriteLine($"Phone: {letter.Phone}");
        }

        Console.WriteLine($"Message: {letter.Message}");
        Console.WriteLine($"Score: {letter.Score}");
        Console.WriteLine();
    }

    private static async Task PrintFailureAsync(string prefix, HttpResponseMessage response)
    {
        var error = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"{prefix} ({(int)response.StatusCode}): {error}");
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

    private static string ReadUsername()
    {
        while (true)
        {
            var value = ReadRequiredText("Username");
            if (value.Length > 32 || value.Any(character => !IsAllowedUsernameCharacter(character)))
            {
                Console.WriteLine("Username may contain only letters, digits, '.', '_' and '-', up to 32 characters.");
                continue;
            }

            return value;
        }
    }

    private static string ReadRequiredText(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                Console.WriteLine($"{label} is required. Please enter a value.");
                continue;
            }

            if (value.Any(char.IsControl))
            {
                Console.WriteLine($"{label} cannot contain control characters.");
                continue;
            }

            return value;
        }
    }

    private static int ReadPositiveInt(string label, int maxValue)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var raw = Console.ReadLine()?.Trim() ?? string.Empty;

            if (raw.Length == 0)
            {
                Console.WriteLine($"{label} is required. Please enter a number.");
                continue;
            }

            if (!int.TryParse(raw, out var value))
            {
                Console.WriteLine($"{label} must be a number. Characters are not allowed.");
                continue;
            }

            if (value <= 0)
            {
                Console.WriteLine($"{label} must be a positive number.");
                continue;
            }

            if (value > maxValue)
            {
                Console.WriteLine($"{label} must be less than or equal to {maxValue}.");
                continue;
            }

            return value;
        }
    }

    private static string ReadPhone()
    {
        while (true)
        {
            Console.Write("Phone: ");
            var value = Console.ReadLine()?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                Console.WriteLine("Phone is required. Please enter a phone number.");
                continue;
            }

            if (value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.WriteLine("Phone cannot be a negative number.");
                continue;
            }

            if (!value.Any(char.IsDigit) || value.Any(character => !IsAllowedPhoneCharacter(character)))
            {
                Console.WriteLine("Phone may contain only digits, spaces, '+', '-', '(' and ')'.");
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
}
