using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class LatestResultsTests
{
    public static async Task R03_CompletionOrderingAndIsolation()
    {
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var handler = new FakeHttpHandler();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var profile = await service.SaveProfileAsync(new RelayProfile { Name = "Latest", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        var time = DateTimeOffset.UtcNow.AddHours(-1);
        var success = new ConnectionTestResult
        {
            ProfileId = profile.Id,
            ModelId = "m",
            BaseUrl = profile.BaseUrl,
            StartedAt = time.AddMinutes(5),
            CompletedAt = time.AddMinutes(6),
            Stage = ConnectionTestStage.ModelResponded,
            ModelResponded = true
        };
        var failure = success with
        {
            RecordId = Guid.NewGuid(),
            StartedAt = time,
            CompletedAt = time.AddMinutes(10),
            Stage = ConnectionTestStage.Failed,
            ModelResponded = false,
            HttpStatusCode = 503
        };
        var probeSuccess = new ProbeRecord
        {
            ProfileId = profile.Id,
            ModelId = "m",
            BaseUrl = profile.BaseUrl,
            CreatedAt = time.AddMinutes(5),
            CompletedAt = time.AddMinutes(6),
            State = LocalRunState.Completed
        };
        var probeFailure = probeSuccess with
        {
            LocalId = Guid.NewGuid(),
            CreatedAt = time,
            CompletedAt = time.AddMinutes(10),
            State = LocalRunState.Failed
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            MaxDepth = 32,
            Converters = { new JsonStringEnumConverter() }
        };
        void Save(IReadOnlyList<ConnectionTestResult> connections, IReadOnlyList<ProbeRecord> probes)
        {
            var path = Path.Combine(tmp.Path, "desktop-state.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            var vault = JsonNode.Parse(protector.Unprotect(Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
            vault["connectionHistory"] = JsonSerializer.SerializeToNode(connections, options);
            vault["history"] = JsonSerializer.SerializeToNode(probes, options);
            root["protectedState"] = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString(options))));
            File.WriteAllText(path, root.ToJsonString(options));
        }

        Save([success, failure,
            failure with { RecordId = Guid.NewGuid(), ModelId = "other", CompletedAt = time.AddMinutes(20) },
            failure with { RecordId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), CompletedAt = time.AddMinutes(20) },
            failure with { RecordId = Guid.NewGuid(), Stage = ConnectionTestStage.LocalValidationPassed, CompletedAt = time.AddMinutes(30) },
            failure with { RecordId = Guid.NewGuid(), CompletedAt = null }],
            [probeSuccess, probeFailure,
            probeFailure with { LocalId = Guid.NewGuid(), ModelId = "other", CompletedAt = time.AddMinutes(20) },
            probeFailure with { LocalId = Guid.NewGuid(), State = LocalRunState.Running, CompletedAt = time.AddMinutes(30) },
            probeFailure with { LocalId = Guid.NewGuid(), CompletedAt = null, LastPolledAt = time.AddMinutes(40) }]);
        var result = await service.GetLatestResultsAsync(profile.Id, "m");
        Check.Equal(failure.RecordId, result.Connection!.RecordId, "latest completion wins even when failed and started earlier");
        Check.Equal(probeFailure.LocalId, result.Probe!.LocalId, "latest terminal Probe failure wins by completion");
        Check.Equal(time.AddMinutes(10), result.Connection.CompletedAt!.Value, "original direct completion is preserved");
        Check.Equal(time.AddMinutes(10), result.Probe.CompletedAt!.Value, "original Probe completion is preserved");
        var again = await service.GetLatestResultsAsync(profile.Id, "m");
        Check.Equal(result.Connection.CompletedAt, again.Connection!.CompletedAt, "reading never changes direct time");
        Check.Equal(result.Probe.CompletedAt, again.Probe!.CompletedAt, "reading never changes Probe time");
        var none = await service.GetLatestResultsAsync(profile.Id, "missing");
        Check.Null(none.Connection, "missing model never uses another connection");
        Check.Null(none.Probe, "missing model never uses another Probe");

        var sameTimeConnection = failure with { RecordId = Guid.NewGuid(), StartedAt = time.AddMinutes(1) };
        var sameTimeProbe = probeFailure with { LocalId = Guid.NewGuid(), CreatedAt = time.AddMinutes(1) };
        Save([failure, sameTimeConnection], [probeFailure, sameTimeProbe]);
        var tie = await service.GetLatestResultsAsync(profile.Id, "m");
        Check.Equal(sameTimeConnection.RecordId, tie.Connection!.RecordId, "equal completion follows existing start-desc history order");
        Check.Equal(sameTimeProbe.LocalId, tie.Probe!.LocalId, "equal completion follows existing created-desc Probe order");
        Check.Equal(0, handler.Count, "local history queries never send HTTP");
    }
}
