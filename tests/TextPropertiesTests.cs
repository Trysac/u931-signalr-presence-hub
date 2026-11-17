using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace Trysac.SignalrPresenceHub.Tests;

/// <summary>Property-based tests for Text and PresenceNode convergence.</summary>
public class TextPropertiesTests
{
    [Property]
    public bool NormalizedTextHasNoOuterWhitespaceAndIsLowercase(NonEmptyString raw)
    {
        var trimmed = raw.Get.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var got = Text.Normalize(raw.Get);
        return got == got.Trim() && string.Equals(got, got.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void Acceptance7RandomOperationSequencesConvergeToReferenceModel()
    {
        var rng = new System.Random(7);
        string[] users = ["ana", "ben", "cam", "dan", "eva"];
        string[] rooms = ["lobby", "general", "dev", "ops"];

        for (int run = 0; run < 200; run++)
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => now);
            var backplane = new InMemoryBackplane();

            using var nodeA = new PresenceNode("a", backplane, options);
            using var nodeB = new PresenceNode("b", backplane, options);
            nodeA.Start();
            nodeB.Start();

            var nodes = new[] { nodeA, nodeB };
            var activeByNode = new Dictionary<int, List<string>> { [0] = [], [1] = [] };
            var referenceConnections = new Dictionary<string, (string UserId, HashSet<string> Rooms)>(StringComparer.Ordinal);

            int opCount = rng.Next(10, 35);
            int nextConnId = 1;

            for (int step = 0; step < opCount; step++)
            {
                int nodeIdx = rng.Next(0, 2);
                var node = nodes[nodeIdx];
                var nodeConns = activeByNode[nodeIdx];

                int opKind = nodeConns.Count == 0 ? 0 : rng.Next(0, 4);
                int faultRoll = rng.Next(0, 20);
                if (faultRoll == 0)
                {
                    backplane.DuplicateNext();
                }
                else if (faultRoll == 1)
                {
                    backplane.Hold();
                }

                switch (opKind)
                {
                    case 0:
                        {
                            string cid = $"n{nodeIdx}-c{nextConnId++}";
                            string uid = users[rng.Next(users.Length)];
                            node.Connect(cid, uid);
                            nodeConns.Add(cid);
                            referenceConnections[cid] = (uid, new HashSet<string>(StringComparer.Ordinal));
                            break;
                        }

                    case 1:
                        {
                            int pick = rng.Next(nodeConns.Count);
                            string cid = nodeConns[pick];
                            nodeConns.RemoveAt(pick);
                            node.Disconnect(cid);
                            referenceConnections.Remove(cid);
                            break;
                        }

                    case 2:
                        {
                            string cid = nodeConns[rng.Next(nodeConns.Count)];
                            string room = rooms[rng.Next(rooms.Length)];
                            node.Join(cid, room);
                            referenceConnections[cid].Rooms.Add(room);
                            break;
                        }

                    case 3:
                        {
                            string cid = nodeConns[rng.Next(nodeConns.Count)];
                            string room = rooms[rng.Next(rooms.Length)];
                            node.Leave(cid, room);
                            referenceConnections[cid].Rooms.Remove(room);
                            break;
                        }
                }

                if (faultRoll == 1)
                {
                    backplane.Release();
                }
            }

            now = now.AddSeconds(5);
            nodeA.Tick();
            nodeB.Tick();

            var expectedOnline = referenceConnections.Values
                .Select(v => v.UserId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToArray();

            var expectedRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var room in rooms)
            {
                var members = referenceConnections.Values
                    .Where(v => v.Rooms.Contains(room))
                    .Select(v => v.UserId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .ToArray();
                if (members.Length > 0)
                {
                    expectedRooms[room] = members;
                }
            }

            var referenceSnapshot = new PresenceSnapshot(expectedOnline, expectedRooms);
            var snapA = nodeA.Snapshot();
            var snapB = nodeB.Snapshot();

            Assert.Equal(referenceSnapshot, snapA);
            Assert.Equal(referenceSnapshot, snapB);
            Assert.Equal(snapA, snapB);
        }
    }
}
