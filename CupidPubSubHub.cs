using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

public sealed class CupidPubSubHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<Guid, ActiveSubscription> _subscriptions = new();

    public async Task ConnectAsync(
        Guid personId,
        WebSocket socket,
        IEnumerable<PubSubEventDto> initialEvents,
        Func<ClientSocketMessage, CancellationToken, Task> handleMessageAsync,
        CancellationToken cancellationToken)
    {
        var subscription = new ActiveSubscription();
        if (!_subscriptions.TryAdd(personId, subscription))
        {
            await SendEventAsync(
                socket,
                CreateErrorEvent("This person already has an active socket subscription."),
                cancellationToken);
            await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "Duplicate subscription", cancellationToken);
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            foreach (var initialEvent in initialEvents)
            {
                subscription.TryPublish(initialEvent);
            }

            var sendTask = RunSendLoopAsync(socket, subscription, linkedCancellation.Token);
            var receiveTask = RunReceiveLoopAsync(socket, subscription, handleMessageAsync, linkedCancellation.Token);

            await Task.WhenAny(sendTask, receiveTask);
            await linkedCancellation.CancelAsync();

            subscription.Complete();
            await IgnoreSocketShutdownAsync(sendTask);
            await IgnoreSocketShutdownAsync(receiveTask);
        }
        finally
        {
            _subscriptions.TryRemove(personId, out _);
            subscription.Complete();
            await CloseSocketAsync(socket, WebSocketCloseStatus.NormalClosure, "Subscription closed", CancellationToken.None);
        }
    }

    public bool Publish(Guid personId, PubSubEventDto pubSubEvent)
    {
        return _subscriptions.TryGetValue(personId, out var subscription) &&
               subscription.TryPublish(pubSubEvent);
    }

    public bool PublishError(Guid personId, string message)
    {
        return Publish(personId, CreateErrorEvent(message));
    }

    public static PubSubEventDto CreateEvent(string type, object? payload)
    {
        return new PubSubEventDto(Guid.NewGuid(), type, DateTimeOffset.UtcNow, payload);
    }

    public static PubSubEventDto CreateErrorEvent(string message)
    {
        return CreateEvent(CupidEventTypes.SocketError, new SocketErrorDto(message));
    }

    private static async Task RunSendLoopAsync(
        WebSocket socket,
        ActiveSubscription subscription,
        CancellationToken cancellationToken)
    {
        await foreach (var pubSubEvent in subscription.ReadAllAsync(cancellationToken))
        {
            await SendEventAsync(socket, pubSubEvent, cancellationToken);
        }
    }

    private static async Task RunReceiveLoopAsync(
        WebSocket socket,
        ActiveSubscription subscription,
        Func<ClientSocketMessage, CancellationToken, Task> handleMessageAsync,
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

            ClientSocketMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ClientSocketMessage>(messageJson, JsonOptions);
            }
            catch (JsonException)
            {
                subscription.TryPublish(CreateErrorEvent("Socket command must be valid JSON."));
                continue;
            }

            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                subscription.TryPublish(CreateErrorEvent("Socket command must include a type."));
                continue;
            }

            await handleMessageAsync(message, cancellationToken);
        }
    }

    private static async Task SendEventAsync(
        WebSocket socket,
        PubSubEventDto pubSubEvent,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(pubSubEvent, JsonOptions);
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            using var message = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new WebSocketException("Only text socket messages are supported.");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CloseSocketAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, description, cancellationToken);
            }
            catch (WebSocketException)
            {
                // The peer may already have closed the transport.
            }
            catch (OperationCanceledException)
            {
                // Shutdown is already in progress.
            }
        }
    }

    private static async Task IgnoreSocketShutdownAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected when either side closes the subscription.
        }
        catch (WebSocketException)
        {
            // Expected when the peer closes the socket abruptly.
        }
    }

    private sealed class ActiveSubscription
    {
        private readonly Channel<PubSubEventDto> _outbox = Channel.CreateUnbounded<PubSubEventDto>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public bool TryPublish(PubSubEventDto pubSubEvent)
        {
            return _outbox.Writer.TryWrite(pubSubEvent);
        }

        public IAsyncEnumerable<PubSubEventDto> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _outbox.Reader.ReadAllAsync(cancellationToken);
        }

        public void Complete()
        {
            _outbox.Writer.TryComplete();
        }
    }
}
