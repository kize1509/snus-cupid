public sealed record RegisterPersonRequest(
    string? Username,
    string? City,
    int Age,
    string? Phone);

public sealed record PersonDto(
    string Username,
    string City,
    int Age,
    string Phone,
    bool HasPendingLetter,
    IReadOnlyCollection<string> BlockedUsers);

public sealed record PublicPersonDto(
    string Username,
    string City,
    int Age);

public sealed record LetterDto(
    PublicPersonDto Sender,
    string Message,
    int Score,
    bool PhoneVisible,
    string? Phone);

public sealed record CupidTickResult(
    DateTimeOffset SentAt,
    int RegisteredPeople,
    int LettersDelivered,
    IReadOnlyCollection<DeliveryAttemptDto> Attempts);

public sealed record DeliveryAttemptDto(
    string Recipient,
    string? Sender,
    int? Score,
    bool Delivered,
    string Reason);
