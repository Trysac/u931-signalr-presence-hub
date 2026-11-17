namespace Trysac.SignalrPresenceHub;

/// <summary>Immutable value-equatable point-in-time view of all online users and room memberships known to a <see cref="PresenceNode"/>.</summary>
public sealed class PresenceSnapshot : IEquatable<PresenceSnapshot>
{
    /// <summary>Gets the ordinal-sorted list of all currently online user identifiers.</summary>
    public IReadOnlyList<string> OnlineUsers { get; }

    /// <summary>Gets the map of non-empty room names to their ordinal-sorted list of member user identifiers.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Rooms { get; }

    /// <summary>Initializes a new instance of the <see cref="PresenceSnapshot"/> class.</summary>
    /// <param name="onlineUsers">Online user identifiers.</param>
    /// <param name="rooms">Room memberships keyed by room name.</param>
    public PresenceSnapshot(
        IReadOnlyList<string> onlineUsers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rooms)
    {
        ArgumentNullException.ThrowIfNull(onlineUsers);
        ArgumentNullException.ThrowIfNull(rooms);

        var sortedUsers = onlineUsers.Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToArray();
        var sortedRooms = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var kvp in rooms)
        {
            var members = kvp.Value.Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToArray();
            if (members.Length > 0)
            {
                sortedRooms[kvp.Key] = members;
            }
        }

        OnlineUsers = sortedUsers;
        Rooms = sortedRooms;
    }

    /// <inheritdoc />
    public bool Equals(PresenceSnapshot? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!OnlineUsers.SequenceEqual(other.OnlineUsers, StringComparer.Ordinal))
        {
            return false;
        }

        if (Rooms.Count != other.Rooms.Count)
        {
            return false;
        }

        foreach (var kvp in Rooms)
        {
            if (!other.Rooms.TryGetValue(kvp.Key, out var otherMembers))
            {
                return false;
            }

            if (!kvp.Value.SequenceEqual(otherMembers, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PresenceSnapshot);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var user in OnlineUsers)
        {
            hash.Add(user, StringComparer.Ordinal);
        }

        foreach (var kvp in Rooms.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            hash.Add(kvp.Key, StringComparer.Ordinal);
            foreach (var member in kvp.Value)
            {
                hash.Add(member, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Checks two <see cref="PresenceSnapshot"/> values for structural equality.</summary>
    public static bool operator ==(PresenceSnapshot? left, PresenceSnapshot? right) =>
        Equals(left, right);

    /// <summary>Checks two <see cref="PresenceSnapshot"/> values for structural inequality.</summary>
    public static bool operator !=(PresenceSnapshot? left, PresenceSnapshot? right) =>
        !Equals(left, right);
}
