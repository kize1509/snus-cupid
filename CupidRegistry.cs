using System.Collections.Concurrent;
using System.Security.Cryptography;

public sealed class CupidRegistry
{
    private static readonly string[] LetterMessages =
    {
        "Radujem se nasem susretu!",
        "Zelim da se upoznamo.",
        "Nisam zainteresovan/a za upoznavanje."
    };

    private const string UninterestedMessage = "Nisam zainteresovan/a za upoznavanje.";

    private readonly ConcurrentDictionary<string, PersonState> _people = new(StringComparer.OrdinalIgnoreCase);

    public CupidApiResult<PersonDto> InitSinglePerson(RegisterPersonRequest request)
    {
        var validation = ValidateRegistration(request);
        if (validation.Error is not null)
        {
            return CupidApiResult<PersonDto>.BadRequest(validation.Error);
        }

        var person = new RegisteredPerson(
            validation.Username,
            validation.City,
            validation.Age,
            validation.Phone);

        var state = new PersonState(person);
        if (!_people.TryAdd(person.NormalizedUsername, state))
        {
            return CupidApiResult<PersonDto>.Conflict("Username is already registered.");
        }

        return CupidApiResult<PersonDto>.Created(state.ToDto());
    }

    public IReadOnlyCollection<PersonDto> GetPeople()
    {
        return _people.Values
            .Select(person => person.ToDto())
            .OrderBy(person => person.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CupidApiResult<PersonDto> GetPerson(string username)
    {
        if (!TryGetPerson(username, out var person))
        {
            return CupidApiResult<PersonDto>.NotFound("Person is not registered.");
        }

        return CupidApiResult<PersonDto>.Ok(person.ToDto());
    }

    public async Task<CupidApiResult<LetterDto>> WaitForNextLetterAsync(
        string username,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!TryGetPerson(username, out var person))
        {
            return CupidApiResult<LetterDto>.NotFound("Person is not registered.");
        }

        var pending = person.GetPendingLetter();
        if (pending is not null)
        {
            return CupidApiResult<LetterDto>.Ok(pending);
        }

        try
        {
            var letter = await person.WaitForLetterAsync(timeout, cancellationToken);
            return letter is null
                ? CupidApiResult<LetterDto>.NoContent()
                : CupidApiResult<LetterDto>.Ok(letter);
        }
        catch (OperationCanceledException)
        {
            return CupidApiResult<LetterDto>.NoContent();
        }
    }

    public CupidApiResult<object> AcknowledgeLetter(string username)
    {
        if (!TryGetPerson(username, out var person))
        {
            return CupidApiResult<object>.NotFound("Person is not registered.");
        }

        return person.AcknowledgeLetter()
            ? CupidApiResult<object>.NoContent()
            : CupidApiResult<object>.Conflict("There is no pending letter to acknowledge.");
    }

    public CupidApiResult<PersonDto> BlockUser(string username, string blockedUsername)
    {
        if (!TryGetPerson(username, out var person))
        {
            return CupidApiResult<PersonDto>.NotFound("Person is not registered.");
        }

        if (!TryGetPerson(blockedUsername, out var blockedPerson))
        {
            return CupidApiResult<PersonDto>.NotFound("Blocked person is not registered.");
        }

        if (person.Person.NormalizedUsername == blockedPerson.Person.NormalizedUsername)
        {
            return CupidApiResult<PersonDto>.BadRequest("A person cannot block themselves.");
        }

        person.Block(blockedPerson.Person.NormalizedUsername);
        return CupidApiResult<PersonDto>.Ok(person.ToDto());
    }

    public CupidTickResult SendLetters()
    {
        var people = _people.Values
            .Select(person => person.Snapshot())
            .OrderBy(person => person.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var attempts = new List<DeliveryAttemptDto>(people.Length);
        var delivered = 0;

        foreach (var recipient in people)
        {
            if (!_people.TryGetValue(recipient.NormalizedUsername, out var recipientState))
            {
                attempts.Add(new DeliveryAttemptDto(recipient.Username, null, null, false, "Recipient disappeared."));
                continue;
            }

            if (recipientState.HasPendingLetter)
            {
                attempts.Add(new DeliveryAttemptDto(recipient.Username, null, null, false, "Recipient has an unacknowledged letter."));
                continue;
            }

            var match = FindBestMatch(recipient, people);
            if (match is null)
            {
                attempts.Add(new DeliveryAttemptDto(recipient.Username, null, null, false, "No eligible sender."));
                continue;
            }

            var message = PickMessage();
            var phoneVisible = message != UninterestedMessage;
            var letter = new LetterDto(
                new PublicPersonDto(match.Sender.Username, match.Sender.City, match.Sender.Age),
                message,
                match.Score,
                phoneVisible,
                phoneVisible ? match.Sender.Phone : null);

            if (recipientState.TryDeliverFrom(match.Sender.NormalizedUsername, letter))
            {
                delivered++;
                attempts.Add(new DeliveryAttemptDto(
                    recipient.Username,
                    match.Sender.Username,
                    match.Score,
                    true,
                    "Delivered."));
            }
            else
            {
                attempts.Add(new DeliveryAttemptDto(
                    recipient.Username,
                    match.Sender.Username,
                    match.Score,
                    false,
                    "Recipient became busy before delivery."));
            }
        }

        return new CupidTickResult(DateTimeOffset.UtcNow, people.Length, delivered, attempts);
    }

    private MatchCandidate? FindBestMatch(RegisteredPerson recipient, IReadOnlyCollection<RegisteredPerson> people)
    {
        MatchCandidate? best = null;

        foreach (var sender in people)
        {
            if (sender.NormalizedUsername == recipient.NormalizedUsername)
            {
                continue;
            }

            if (!_people.TryGetValue(sender.NormalizedUsername, out var senderState))
            {
                continue;
            }

            if (!_people.TryGetValue(recipient.NormalizedUsername, out var recipientState))
            {
                continue;
            }

            if (recipientState.HasBlocked(sender.NormalizedUsername) ||
                senderState.HasBlocked(recipient.NormalizedUsername))
            {
                continue;
            }

            var score = CalculateScore(recipient, sender);
            if (best is null || score > best.Score)
            {
                best = new MatchCandidate(sender, score);
            }
        }

        return best;
    }

    private static int CalculateScore(RegisteredPerson recipient, RegisteredPerson sender)
    {
        var score = RandomNumberGenerator.GetInt32(0, 101);

        if (string.Equals(recipient.City, sender.City, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (Math.Abs(recipient.Age - sender.Age) <= 2)
        {
            score += 20;
        }

        return score;
    }

    private static string PickMessage()
    {
        return LetterMessages[RandomNumberGenerator.GetInt32(0, LetterMessages.Length)];
    }

    private bool TryGetPerson(string username, out PersonState person)
    {
        var normalized = Normalize(username);
        if (normalized.Length == 0)
        {
            person = default!;
            return false;
        }

        return _people.TryGetValue(normalized, out person!);
    }

    private static RegistrationValidation ValidateRegistration(RegisterPersonRequest request)
    {
        var username = (request.Username ?? string.Empty).Trim();
        var city = (request.City ?? string.Empty).Trim();
        var phone = (request.Phone ?? string.Empty).Trim();

        if (username.Length == 0)
        {
            return RegistrationValidation.Invalid("Username is required.");
        }

        if (username.Length > 32 || username.Any(character => !IsAllowedUsernameCharacter(character)))
        {
            return RegistrationValidation.Invalid("Username may contain only letters, digits, '.', '_' and '-', up to 32 characters.");
        }

        if (city.Length == 0 || city.Any(char.IsControl))
        {
            return RegistrationValidation.Invalid("City is required and cannot contain control characters.");
        }

        if (request.Age is < 1 or > 120)
        {
            return RegistrationValidation.Invalid("Age must be between 1 and 120.");
        }

        if (phone.Length == 0)
        {
            return RegistrationValidation.Invalid("Phone is required.");
        }

        if (phone.StartsWith('-', StringComparison.Ordinal))
        {
            return RegistrationValidation.Invalid("Phone cannot be a negative number.");
        }

        if (!phone.Any(char.IsDigit) || phone.Any(character => !IsAllowedPhoneCharacter(character)))
        {
            return RegistrationValidation.Invalid("Phone may contain only digits, spaces, '+', '-', '(' and ')'.");
        }

        return RegistrationValidation.Valid(username, city, request.Age, phone);
    }

    private static bool IsAllowedUsernameCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '_' or '-';
    }

    private static bool IsAllowedPhoneCharacter(char character)
    {
        return char.IsDigit(character) || character is ' ' or '+' or '-' or '(' or ')';
    }

    private static string Normalize(string? username)
    {
        return (username ?? string.Empty).Trim().ToUpperInvariant();
    }

    private sealed record MatchCandidate(RegisteredPerson Sender, int Score);

    private sealed record RegistrationValidation(
        string Username,
        string City,
        int Age,
        string Phone,
        string? Error)
    {
        public static RegistrationValidation Valid(string username, string city, int age, string phone)
        {
            return new RegistrationValidation(username, city, age, phone, null);
        }

        public static RegistrationValidation Invalid(string error)
        {
            return new RegistrationValidation(string.Empty, string.Empty, 0, string.Empty, error);
        }
    }

    private sealed record RegisteredPerson(
        string Username,
        string City,
        int Age,
        string Phone)
    {
        public string NormalizedUsername { get; } = Normalize(Username);
    }

    private sealed class PersonState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _blockedUsers = new(StringComparer.OrdinalIgnoreCase);
        private LetterDto? _pendingLetter;
        private TaskCompletionSource<LetterDto>? _letterWaiter;

        public PersonState(RegisteredPerson person)
        {
            Person = person;
        }

        public RegisteredPerson Person { get; }

        public bool HasPendingLetter
        {
            get
            {
                lock (_gate)
                {
                    return _pendingLetter is not null;
                }
            }
        }

        public RegisteredPerson Snapshot() => Person;

        public PersonDto ToDto()
        {
            lock (_gate)
            {
                return new PersonDto(
                    Person.Username,
                    Person.City,
                    Person.Age,
                    Person.Phone,
                    _pendingLetter is not null,
                    _blockedUsers.OrderBy(username => username, StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }

        public bool TryDeliverFrom(string senderUsername, LetterDto letter)
        {
            TaskCompletionSource<LetterDto>? waiter;

            lock (_gate)
            {
                if (_pendingLetter is not null || _blockedUsers.Contains(senderUsername))
                {
                    return false;
                }

                _pendingLetter = letter;
                waiter = _letterWaiter;
                _letterWaiter = null;
            }

            waiter?.TrySetResult(letter);
            return true;
        }

        public LetterDto? GetPendingLetter()
        {
            lock (_gate)
            {
                return _pendingLetter;
            }
        }

        public async Task<LetterDto?> WaitForLetterAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            TaskCompletionSource<LetterDto> waiter;

            lock (_gate)
            {
                if (_pendingLetter is not null)
                {
                    return _pendingLetter;
                }

                _letterWaiter ??= new TaskCompletionSource<LetterDto>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _letterWaiter;
            }

            var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeout, cancellationToken));
            if (completed == waiter.Task)
            {
                return await waiter.Task;
            }

            return null;
        }

        public bool AcknowledgeLetter()
        {
            lock (_gate)
            {
                if (_pendingLetter is null)
                {
                    return false;
                }

                _pendingLetter = null;
                return true;
            }
        }

        public void Block(string normalizedUsername)
        {
            lock (_gate)
            {
                _blockedUsers.Add(normalizedUsername);
            }
        }

        public bool HasBlocked(string normalizedUsername)
        {
            lock (_gate)
            {
                return _blockedUsers.Contains(normalizedUsername);
            }
        }
    }
}
