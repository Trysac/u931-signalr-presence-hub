namespace Trysac.SignalrPresenceHub;

/// <summary>Represents a user-level presence or room membership transition.</summary>
/// <param name="Kind">Transition kind: <c>user_joined</c>, <c>user_left</c>, <c>user_online</c>, or <c>user_offline</c>.</param>
/// <param name="Room">Room name for room transitions, or empty string for online/offline transitions.</param>
/// <param name="UserId">Identifier of the user whose state changed.</param>
/// <param name="Reason">Transition cause: <c>connected</c>, <c>disconnected</c>, <c>left</c>, or <c>instance_expired</c>.</param>
public sealed record PresenceEvent(
    string Kind,
    string Room,
    string UserId,
    string Reason);
