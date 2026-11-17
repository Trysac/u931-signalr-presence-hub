using Xunit;

namespace Trysac.SignalrPresenceHub.Tests;

/// <summary>Tests of the template Text helper and acceptance scenarios for PresenceNode.</summary>
public class TextTests
{
    [Theory]
    [InlineData("  Hello World ", "hello world")]
    [InlineData("ABC", "abc")]
    [InlineData("\tx\n", "x")]
    public void TrimsAndLowercases(string input, string want)
    {
        Assert.Equal(want, Text.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void RejectsBlankInput(string input)
    {
        var error = Assert.Throws<BlankInputException>(() => Text.Normalize(input));
        Assert.Equal("input is blank", error.Message);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Text.Normalize(null!));
    }

    [Fact]
    public void TheExceptionKeepsItsMessageAndCause()
    {
        var cause = new InvalidOperationException("cause");
        Assert.Equal("custom", new BlankInputException("custom").Message);
        Assert.Same(cause, new BlankInputException("custom", cause).InnerException);
    }

    [Fact]
    public void Acceptance1MultiConnectionOnlineAcrossTwoNodes()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        a.Start();
        b.Start();

        a.Connect("c1", "ana");
        b.Connect("c2", "ana");

        a.Disconnect("c1");
        Assert.True(a.IsOnline("ana"));
        Assert.True(b.IsOnline("ana"));

        b.Disconnect("c2");
        Assert.False(a.IsOnline("ana"));
        Assert.False(b.IsOnline("ana"));
    }

    [Fact]
    public void Acceptance2RoomJoinAcrossTwoNodesAndTransitionEvents()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        var eventsA = new List<PresenceEvent>();
        var eventsB = new List<PresenceEvent>();
        a.OnEvent += eventsA.Add;
        b.OnEvent += eventsB.Add;

        a.Start();
        b.Start();

        a.Connect("c1", "ana");
        b.Connect("c2", "ben");

        a.Join("c1", "lobby");
        b.Join("c2", "lobby");

        Assert.Equal(["ana", "ben"], a.UsersIn("lobby"));
        Assert.Equal(["ana", "ben"], b.UsersIn("lobby"));

        var joinedOnA = eventsA.Where(e => e.Kind == "user_joined").ToList();
        var joinedOnB = eventsB.Where(e => e.Kind == "user_joined").ToList();

        Assert.Equal(2, joinedOnA.Count);
        Assert.Single(joinedOnA, e => e.UserId == "ana" && e.Room == "lobby" && e.Reason == "connected");
        Assert.Single(joinedOnA, e => e.UserId == "ben" && e.Room == "lobby" && e.Reason == "connected");

        Assert.Equal(2, joinedOnB.Count);
        Assert.Single(joinedOnB, e => e.UserId == "ana" && e.Room == "lobby" && e.Reason == "connected");
        Assert.Single(joinedOnB, e => e.UserId == "ben" && e.Room == "lobby" && e.Reason == "connected");
    }

    [Fact]
    public void Acceptance3InstanceExpiryRaisesUserLeftAndUserOfflineWithInstanceExpiredReason()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        var eventsA = new List<PresenceEvent>();
        a.OnEvent += eventsA.Add;

        a.Start();
        b.Start();

        a.Connect("c1", "ana");
        a.Join("c1", "lobby");
        b.Connect("c2", "ben");
        b.Join("c2", "lobby");

        eventsA.Clear();

        now = now.AddSeconds(16);
        a.Tick();

        Assert.False(a.IsOnline("ben"));
        Assert.Contains(new PresenceEvent("user_left", "lobby", "ben", "instance_expired"), eventsA);
        Assert.Contains(new PresenceEvent("user_offline", string.Empty, "ben", "instance_expired"), eventsA);
    }

    [Fact]
    public void Acceptance4DuplicateDeltaIgnoredAndSequenceGapTriggersSyncRequest()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        a.Start();
        b.Start();

        backplane.DuplicateNext();
        a.Connect("c1", "ana");

        var snapshotAfterDuplicate = b.Snapshot();
        Assert.Equal(a.Snapshot(), snapshotAfterDuplicate);
        Assert.Equal(["ana"], snapshotAfterDuplicate.OnlineUsers);

        int syncRequestsBefore = backplane.PublishedMessages.Count(m => m.Kind == "sync_request");

        backplane.DropNext();
        a.Join("c1", "lobby");
        Assert.Empty(b.UsersIn("lobby"));

        a.Connect("c2", "ben");

        int syncRequestsAfter = backplane.PublishedMessages.Count(m => m.Kind == "sync_request");
        Assert.Equal(syncRequestsBefore + 1, syncRequestsAfter);
        Assert.Equal(a.Snapshot(), b.Snapshot());
        Assert.Equal(["ana"], b.UsersIn("lobby"));
        Assert.True(b.IsOnline("ben"));
    }

    [Fact]
    public void Acceptance5LateJoiningNodeSyncsExistingUsersOnStart()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        a.Start();

        a.Connect("c1", "ana");
        a.Join("c1", "lobby");
        a.Connect("c2", "ben");
        a.Join("c2", "lobby");
        a.Connect("c3", "cam");
        a.Join("c3", "lobby");

        using var c = new PresenceNode("c", backplane, options);
        c.Start();

        Assert.Equal(a.UsersIn("lobby"), c.UsersIn("lobby"));
        Assert.Equal(["ana", "ben", "cam"], c.UsersIn("lobby"));
    }

    [Fact]
    public void Acceptance6ValidationAndCapacityErrorsProduceExpectedCodes()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now, MaxConnections: 10_000, MaxRoomsPerConnection: 100);
        var backplane = new InMemoryBackplane();

        using var node = new PresenceNode("a", backplane, options);

        var notStartedEx = Assert.Throws<PresenceException>(() => node.Connect("c1", "ana"));
        Assert.Equal("not_started", notStartedEx.Code);

        node.Start();

        node.Connect("c1", "ana");
        var dupEx = Assert.Throws<PresenceException>(() => node.Connect("c1", "ana"));
        Assert.Equal("duplicate_connection", dupEx.Code);

        var unknownEx = Assert.Throws<PresenceException>(() => node.Join("zz", "lobby"));
        Assert.Equal("unknown_connection", unknownEx.Code);

        string room129 = new('r', 129);
        var invalidNameEx = Assert.Throws<PresenceException>(() => node.Join("c1", room129));
        Assert.Equal("invalid_name", invalidNameEx.Code);

        for (int i = 1; i <= 100; i++)
        {
            node.Join("c1", $"room-{i}");
        }

        var tooManyRoomsEx = Assert.Throws<PresenceException>(() => node.Join("c1", "room-101"));
        Assert.Equal("too_many_rooms", tooManyRoomsEx.Code);

        for (int i = 2; i <= 10_000; i++)
        {
            node.Connect($"c{i}", $"u{i}");
        }

        var capacityEx = Assert.Throws<PresenceException>(() => node.Connect("c10001", "u10001"));
        Assert.Equal("capacity", capacityEx.Code);
    }

    [Fact]
    public void Acceptance8PresenceEventPayloadsNeverContainConnectionId()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        var capturedEvents = new List<PresenceEvent>();
        a.OnEvent += capturedEvents.Add;
        b.OnEvent += capturedEvents.Add;

        a.Start();
        b.Start();

        const string connId1 = "secret-conn-id-alpha-991";
        const string connId2 = "secret-conn-id-beta-992";

        a.Connect(connId1, "ana");
        a.Join(connId1, "lobby");
        a.Leave(connId1, "lobby");
        a.Disconnect(connId1);

        b.Connect(connId2, "ben");
        b.Join(connId2, "lobby");
        now = now.AddSeconds(20);
        a.Tick();

        Assert.NotEmpty(capturedEvents);

        var properties = typeof(PresenceEvent).GetProperties();
        Assert.Equal(
            ["Kind", "Reason", "Room", "UserId"],
            properties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        foreach (var evt in capturedEvents)
        {
            Assert.DoesNotContain(connId1, evt.Kind, StringComparison.Ordinal);
            Assert.DoesNotContain(connId1, evt.Room, StringComparison.Ordinal);
            Assert.DoesNotContain(connId1, evt.UserId, StringComparison.Ordinal);
            Assert.DoesNotContain(connId1, evt.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(connId1, evt.ToString(), StringComparison.Ordinal);

            Assert.DoesNotContain(connId2, evt.Kind, StringComparison.Ordinal);
            Assert.DoesNotContain(connId2, evt.Room, StringComparison.Ordinal);
            Assert.DoesNotContain(connId2, evt.UserId, StringComparison.Ordinal);
            Assert.DoesNotContain(connId2, evt.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(connId2, evt.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EdgeCasesTwoConnectionsInRoomLeaveOneExpiredInstanceComesBackUnjoinedLeaveHoldRelease()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        using var a = new PresenceNode("a", backplane, options);
        using var b = new PresenceNode("b", backplane, options);
        var eventsA = new List<PresenceEvent>();
        a.OnEvent += eventsA.Add;

        a.Start();
        b.Start();

        a.Connect("c1", "ana");
        a.Connect("c2", "ana");
        a.Join("c1", "lobby");
        a.Join("c2", "lobby");
        a.Join("c2", "lobby");

        eventsA.Clear();
        a.Leave("c1", "lobby");
        Assert.Empty(eventsA);
        Assert.Equal(["ana"], a.UsersIn("lobby"));

        a.Leave("c1", "never-joined");
        Assert.Empty(eventsA);

        a.Leave("c2", "lobby");
        Assert.Single(eventsA, e => e.Kind == "user_left" && e.Room == "lobby" && e.UserId == "ana" && e.Reason == "left");

        b.Connect("cb1", "ben");
        b.Join("cb1", "general");
        Assert.True(a.IsOnline("ben"));

        now = now.AddSeconds(16);
        a.Tick();
        Assert.False(a.IsOnline("ben"));

        b.Connect("cb2", "cara");
        Assert.True(a.IsOnline("ben"));
        Assert.True(a.IsOnline("cara"));
        Assert.Equal(["ben"], a.UsersIn("general"));
        Assert.Equal(a.Snapshot(), b.Snapshot());

        backplane.Hold();
        a.Join("c1", "held-room");
        Assert.Empty(b.UsersIn("held-room"));
        backplane.Release();
        Assert.Equal(["ana"], b.UsersIn("held-room"));
    }

    [Fact]
    public void InvalidNamesAndConstructorGuardsAndExceptionOverloads()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
        var backplane = new InMemoryBackplane();

        Assert.Throws<PresenceException>(() => new PresenceNode("", backplane, options));
        Assert.Throws<PresenceException>(() => new PresenceNode("bad\0name", backplane, options));
        Assert.Throws<PresenceException>(() => new PresenceNode("bad\nname", backplane, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceNode("a", backplane, options with { HeartbeatInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceNode("a", backplane, options with { InstanceTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceNode("a", backplane, options with { MaxConnections = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceNode("a", backplane, options with { MaxRoomsPerConnection = 0 }));

        var defaultEx = new PresenceException();
        Assert.Equal("invalid_name", defaultEx.Code);

        var msgEx = new PresenceException("short");
        Assert.Equal("short", msgEx.Message);

        var longMsgEx = new PresenceException("custom_code", new string('x', 250));
        Assert.Equal(200, longMsgEx.Message.Length);
        Assert.Equal("custom_code", longMsgEx.Code);

        var inner = new InvalidOperationException("inner");
        var innerEx1 = new PresenceException("msg", inner);
        Assert.Same(inner, innerEx1.InnerException);
        var innerEx2 = new PresenceException("code2", "msg2", inner);
        Assert.Equal("code2", innerEx2.Code);
        Assert.Same(inner, innerEx2.InnerException);

        var snap1 = new PresenceSnapshot(["b", "a"], new Dictionary<string, IReadOnlyList<string>> { ["r"] = ["b", "a"] });
        var snap2 = new PresenceSnapshot(["a", "b"], new Dictionary<string, IReadOnlyList<string>> { ["r"] = ["a", "b"] });
        var snap3 = new PresenceSnapshot(["a"], new Dictionary<string, IReadOnlyList<string>>());
        Assert.True(snap1 == snap2);
        Assert.False(snap1 != snap2);
        Assert.False(snap1.Equals(snap3));
        Assert.False(snap1.Equals((object?)null));
        Assert.True(snap1.Equals((object)snap2));
        Assert.Equal(snap1.GetHashCode(), snap2.GetHashCode());
    }
}
