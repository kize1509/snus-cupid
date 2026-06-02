using System.Collections.Concurrent;
using System.Net.WebSockets;
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

#pragma warning disable SYSLIB0023
    private static readonly RandomNumberGenerator CryptoRandom = new RNGCryptoServiceProvider();
#pragma warning restore SYSLIB0023

    private readonly ConcurrentDictionary<Guid, PersonState> _peopleById = new();
    private readonly ConcurrentDictionary<string, Guid> _personIdsByUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly CupidPubSubHub _pubSubHub;

    public CupidRegistry(CupidPubSubHub pubSubHub)
    {
        _pubSubHub = pubSubHub;
    }

    public CupidApiResult<PersonDto> InitSinglePerson(RegisterPersonRequest request)
    {
        var validation = ValidateRegistration(request);
        if (validation.Error is not null)
        {
            return CupidApiResult<PersonDto>.BadRequest(validation.Error);
        }

        var person = new RegisteredPerson(
            Guid.NewGuid(),
            validation.Username,
            validation.City,
            validation.Age,
            validation.Phone);

        var state = new PersonState(person);
        if (!_personIdsByUsername.TryAdd(person.NormalizedUsername, person.Id))
        {
            return CupidApiResult<PersonDto>.Conflict("Username is already registered.");
        }

        if (!_peopleById.TryAdd(person.Id, state))
        {
            _personIdsByUsername.TryRemove(person.NormalizedUsername, out _);
            return CupidApiResult<PersonDto>.Conflict("Person ID collision. Please try again.");
        }

        return CupidApiResult<PersonDto>.Created(ToDto(state));
    }

    public bool PersonExists(Guid personId)
    {
        return _peopleById.ContainsKey(personId);
    }

    public async Task SubscribeAsync(Guid personId, WebSocket socket, CancellationToken cancellationToken)
    {
        if (!TryGetPerson(personId, out var person))
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Person is not registered.",
                cancellationToken);
            return;
        }

        var pendingLetter = person.GetPendingLetter();
        var initialEvents = pendingLetter is null
            ? Array.Empty<PubSubEventDto>()
            : new[] { CupidPubSubHub.CreateEvent(CupidEventTypes.LetterReceived, pendingLetter) };

        await _pubSubHub.ConnectAsync(
            personId,
            socket,
            initialEvents,
            (message, token) => HandleSocketCommandAsync(personId, message, token),
            cancellationToken);
    }

    public CupidApiResult<AcknowledgedLetterDto> AcknowledgeLetter(Guid personId, Guid letterId)
    {
        if (!TryGetPerson(personId, out var person))
        {
            return CupidApiResult<AcknowledgedLetterDto>.NotFound("Person is not registered.");
        }

        return person.AcknowledgeLetter(letterId, out var error)
            ? CupidApiResult<AcknowledgedLetterDto>.Ok(new AcknowledgedLetterDto(letterId))
            : CupidApiResult<AcknowledgedLetterDto>.Conflict(error ?? "Letter could not be acknowledged.");
    }

    public CupidApiResult<BlockUserResponseDto> BlockUser(Guid personId, string blockedUsername)
    {
        if (!TryGetPerson(personId, out var person))
        {
            return CupidApiResult<BlockUserResponseDto>.NotFound("Person is not registered.");
        }

        if (!TryGetPerson(blockedUsername, out var blockedPerson))
        {
            return CupidApiResult<BlockUserResponseDto>.NotFound("Blocked person is not registered.");
        }

        if (person.Person.Id == blockedPerson.Person.Id)
        {
            return CupidApiResult<BlockUserResponseDto>.BadRequest("A person cannot block themselves.");
        }

        person.Block(blockedPerson.Person.Id);
        return CupidApiResult<BlockUserResponseDto>.Ok(
            new BlockUserResponseDto(blockedPerson.Person.Id, blockedPerson.Person.Username));
    }

    public Task<CupidTickResult> SendLettersAsync(CancellationToken cancellationToken = default)
    {
        var people = _peopleById.Values
            .Select(person => person.Snapshot())
            .OrderBy(person => person.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var delivered = 0;

        foreach (var recipient in people)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_peopleById.TryGetValue(recipient.Id, out var recipientState))
            {
                continue;
            }

            if (recipientState.HasPendingLetter)
            {
                continue;
            }

            var match = FindBestMatch(recipient, people);
            if (match is null)
            {
                continue;
            }

            var message = PickMessage();
            var phoneVisible = message != UninterestedMessage;
            var letter = new LetterDto(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new PublicPersonDto(match.Sender.Id, match.Sender.Username, match.Sender.City, match.Sender.Age),
                message,
                match.Score,
                phoneVisible,
                phoneVisible ? match.Sender.Phone : null);

            if (recipientState.TryDeliverFrom(match.Sender.Id, letter))
            {
                delivered++;
                _pubSubHub.Publish(recipient.Id, CupidPubSubHub.CreateEvent(CupidEventTypes.LetterReceived, letter));
            }
        }

        return Task.FromResult(new CupidTickResult(DateTimeOffset.UtcNow, people.Length, delivered));
    }

    private Task HandleSocketCommandAsync(
        Guid personId,
        ClientSocketMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (message.Type?.Trim().ToLowerInvariant())
        {
            case CupidCommandTypes.AcknowledgeLetter:
                HandleAcknowledgeCommand(personId, message);
                break;

            case CupidCommandTypes.BlockUser:
                HandleBlockCommand(personId, message);
                break;

            default:
                _pubSubHub.PublishError(personId, $"Unknown socket command '{message.Type}'.");
                break;
        }

        return Task.CompletedTask;
    }

    private void HandleAcknowledgeCommand(Guid personId, ClientSocketMessage message)
    {
        if (message.LetterId is null)
        {
            _pubSubHub.PublishError(personId, "Acknowledgement command must include letterId.");
            return;
        }

        var result = AcknowledgeLetter(personId, message.LetterId.Value);
        if (result.Error is not null)
        {
            _pubSubHub.PublishError(personId, result.Error);
            return;
        }

        _pubSubHub.Publish(
            personId,
            CupidPubSubHub.CreateEvent(CupidEventTypes.LetterAcknowledged, result.Value));
    }

    private void HandleBlockCommand(Guid personId, ClientSocketMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Username))
        {
            _pubSubHub.PublishError(personId, "Block command must include username.");
            return;
        }

        var result = BlockUser(personId, message.Username);
        if (result.Error is not null)
        {
            _pubSubHub.PublishError(personId, result.Error);
            return;
        }

        _pubSubHub.Publish(
            personId,
            CupidPubSubHub.CreateEvent(CupidEventTypes.UserBlocked, result.Value));
    }

    private MatchCandidate? FindBestMatch(RegisteredPerson recipient, IReadOnlyCollection<RegisteredPerson> people)
    {
        if (!_peopleById.TryGetValue(recipient.Id, out var recipientState))
        {
            return null;
        }

        MatchCandidate? best = null;

        foreach (var sender in people)
        {
            if (sender.Id == recipient.Id)
            {
                continue;
            }

            if (!_peopleById.ContainsKey(sender.Id))
            {
                continue;
            }

            if (recipientState.HasBlocked(sender.Id))
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
        var score = GetCryptoRandomInt32(0, 101);

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
        return LetterMessages[GetCryptoRandomInt32(0, LetterMessages.Length)];
    }

    private static int GetCryptoRandomInt32(int minValue, int maxExclusive)
    {
        if (minValue >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Maximum must be greater than minimum.");
        }

        var range = (uint)(maxExclusive - minValue);
        var possibleValues = 1UL + uint.MaxValue;
        var limit = possibleValues - possibleValues % range;
        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        while (true)
        {
            lock (CryptoRandom)
            {
                CryptoRandom.GetBytes(bytes);
            }

            var value = BitConverter.ToUInt32(bytes);
            if (value < limit)
            {
                return (int)(minValue + value % range);
            }
        }
    }

    private bool TryGetPerson(Guid personId, out PersonState person)
    {
        return _peopleById.TryGetValue(personId, out person!);
    }

    private bool TryGetPerson(string username, out PersonState person)
    {
        var normalized = Normalize(username);
        if (normalized.Length == 0 ||
            !_personIdsByUsername.TryGetValue(normalized, out var personId))
        {
            person = default!;
            return false;
        }

        return TryGetPerson(personId, out person);
    }

    private PersonDto ToDto(PersonState state)
    {
        var person = state.Person;
        var blockedUsers = state.GetBlockedUserIds()
            .Select(DescribeBlockedUser)
            .OfType<BlockedUserDto>()
            .OrderBy(blocked => blocked.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PersonDto(
            person.Id,
            person.Username,
            person.City,
            person.Age,
            person.Phone,
            state.HasPendingLetter,
            blockedUsers);
    }

    private BlockedUserDto? DescribeBlockedUser(Guid personId)
    {
        return _peopleById.TryGetValue(personId, out var person)
            ? new BlockedUserDto(person.Person.Id, person.Person.Username)
            : null;
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

        if (phone.StartsWith("-", StringComparison.Ordinal))
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
        Guid Id,
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
        private readonly HashSet<Guid> _blockedUserIds = new();
        private LetterDto? _pendingLetter;

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

        public bool TryDeliverFrom(Guid senderId, LetterDto letter)
        {
            lock (_gate)
            {
                if (_pendingLetter is not null || _blockedUserIds.Contains(senderId))
                {
                    return false;
                }

                _pendingLetter = letter;
                return true;
            }
        }

        public LetterDto? GetPendingLetter()
        {
            lock (_gate)
            {
                return _pendingLetter;
            }
        }

        public bool AcknowledgeLetter(Guid letterId, out string? error)
        {
            lock (_gate)
            {
                if (_pendingLetter is null)
                {
                    error = "There is no pending letter to acknowledge.";
                    return false;
                }

                if (_pendingLetter.Id != letterId)
                {
                    error = "Pending letter id does not match the acknowledgement.";
                    return false;
                }

                _pendingLetter = null;
                error = null;
                return true;
            }
        }

        public void Block(Guid blockedPersonId)
        {
            lock (_gate)
            {
                _blockedUserIds.Add(blockedPersonId);
            }
        }

        public bool HasBlocked(Guid personId)
        {
            lock (_gate)
            {
                return _blockedUserIds.Contains(personId);
            }
        }

        public IReadOnlyCollection<Guid> GetBlockedUserIds()
        {
            lock (_gate)
            {
                return _blockedUserIds.ToArray();
            }
        }
    }
}
