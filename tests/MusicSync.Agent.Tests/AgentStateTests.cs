using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Agent;
using MusicSync.Core;

[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]

namespace MusicSync.Agent.Tests;

[TestClass]
public sealed class AgentStateTests
{
    [TestMethod]
    public void PipePublishesAllPublicParticipantsAndSelfIdentity()
    {
        var participants = Enumerable.Range(0, 10).Select(i => new PublicParticipant(Guid.NewGuid(),
            i == 0 ? Role.Leader : Role.Listener, i != 4,
            i == 3 ? new(1, ReportKind.Failed, Detail: "track-unavailable") : null)).ToArray();
        var view = new ConnectionView(ConnectionStatus.Connected, Role.Listener,
            new(new("ROOM", 1, 1, new("1", "2"), Playback.Paused, 0, 1, false), new(true, null), new(true, null)), 1)
        {
            Participants = participants, SelfParticipantId = participants[9].ParticipantId, Capacity = 10
        };
        var state = AgentState.Project(view, 10_000, new(new(true), ReportKind.Failed, "no-player"));
        using var input = new StreamReader(new MemoryStream());
        using var output = new StringWriter();
        new AgentPipe(input, output).Send(state);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.AreEqual("state", root.GetProperty("type").GetString());
        Assert.AreEqual("listener", root.GetProperty("role").GetString());
        Assert.AreEqual(10, root.GetProperty("capacity").GetInt32());
        Assert.AreEqual(participants[9].ParticipantId, root.GetProperty("selfParticipantId").GetGuid());
        var members = root.GetProperty("participants");
        Assert.AreEqual(10, members.GetArrayLength());
        Assert.AreEqual("leader", members[0].GetProperty("role").GetString());
        Assert.AreEqual(JsonValueKind.Null, members[0].GetProperty("report").ValueKind);
        Assert.AreEqual("track-unavailable", members[3].GetProperty("report").GetProperty("detail").GetString());
        Assert.IsFalse(members[4].GetProperty("connected").GetBoolean());
        Assert.AreEqual("no-player", root.GetProperty("playbackError").GetString());
        Assert.IsFalse(output.ToString().Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(state, AgentState.Project(view, 10_000, new(new(true), ReportKind.Failed, "no-player")));
    }

    [TestMethod]
    public void ReconnectingKeepsMembershipAndErrorsWhileEndedAndIdleClearIt()
    {
        var self = Guid.NewGuid();
        var view = new ConnectionView(ConnectionStatus.Reconnecting, Role.Leader, Error: "connection-lost")
        {
            SelfParticipantId = self, Capacity = 10,
            Participants = [new(self, Role.Leader, false, null)]
        };
        var state = AgentState.Project(view, 10_000, null);
        Assert.AreEqual("reconnecting", state.Status);
        Assert.AreEqual(self, state.SelfParticipantId);
        Assert.AreEqual("connection-lost", state.Error);
        Assert.IsFalse(state.PeerConnected);
        Assert.HasCount(1, state.Participants);
        AssertCleared(AgentState.Project(view with { Status = ConnectionStatus.Ended }, 10_000, null));
        AssertCleared(new("idle"));

        static void AssertCleared(AgentState state)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(state, Protocol.Json));
            Assert.AreEqual(0, json.RootElement.GetProperty("participants").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("selfParticipantId").ValueKind);
            Assert.AreEqual(0, json.RootElement.GetProperty("capacity").GetInt32());
        }
    }
}
