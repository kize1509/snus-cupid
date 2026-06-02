public sealed record RegisterPersonRequest(
    string? Username,
    string? City,
    int Age,
    string? Phone);

public sealed record PersonDto(
    Guid Id,
    string Username,
    string City,
    int Age,
    string Phone,
    bool HasPendingLetter,
    IReadOnlyCollection<BlockedUserDto> BlockedUsers);

public sealed record PublicPersonDto(
    Guid Id,
    string Username,
    string City,
    int Age);

public sealed record LetterDto(
    Guid Id,
    DateTimeOffset SentAt,
    PublicPersonDto Sender,
    string Message,
    int Score,
    bool PhoneVisible,
    string? Phone);

public sealed record CupidTickResult(
    DateTimeOffset SentAt,
    int RegisteredPeople,
    int LettersDelivered);

public sealed record BlockedUserDto(
    Guid Id,
    string Username);

public sealed record PubSubEventDto(
    Guid EventId,
    string Type,
    DateTimeOffset OccurredAt,
    object? Payload);

public sealed record PubSubEventDto<TPayload>(
    Guid EventId,
    string Type,
    DateTimeOffset OccurredAt,
    TPayload Payload);

public sealed record ClientSocketMessage(
    string? Type,
    Guid? LetterId,
    string? Username);

public sealed record SocketErrorDto(string Message);

public sealed record AcknowledgedLetterDto(Guid LetterId);

public sealed record BlockUserResponseDto(Guid BlockedPersonId, string Username);

public static class CupidEventTypes
{
    public const string LetterReceived = "letter.received";
    public const string LetterAcknowledged = "letter.acknowledged";
    public const string UserBlocked = "user.blocked";
    public const string SocketError = "socket.error";
}

public static class CupidCommandTypes
{
    public const string AcknowledgeLetter = "letter.ack";
    public const string BlockUser = "user.block";
}
