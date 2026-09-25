using System.Text.Json;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Serialization;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The configuration and helper documents are read and written from a Native AOT process, so the
/// metadata must come from the source generator and the on-disk spelling (camelCase, camelCase enums,
/// comments allowed) must not drift.
/// </summary>
public sealed class JsonContractTests
{
    [Fact]
    public void Options_UseTheSourceGeneratedResolverOnly()
    {
        Assert.NotNull(NetworkGuardianJson.Options.TypeInfoResolver);

        // An unregistered type must fail instead of silently falling back to the reflection based
        // resolver, which is what would break as soon as the app is published with PublishAot.
        Assert.ThrowsAny<Exception>(() => NetworkGuardianJson.Options.GetTypeInfo(typeof(JsonContractTests)));
    }

    [Fact]
    public void RegisteredTypes_AreResolvable()
    {
        Assert.NotNull(NetworkGuardianJson.Options.GetTypeInfo(typeof(GuardianConfig)));
        Assert.NotNull(NetworkGuardianJson.Options.GetTypeInfo(typeof(HelperRequest)));
        Assert.NotNull(NetworkGuardianJson.Options.GetTypeInfo(typeof(HelperResponse)));
    }

    [Fact]
    public void Enums_AreWrittenInCamelCase()
    {
        var json = ConfigJson.Serialize(GuardianConfig.CreateDefault());

        Assert.Contains("\"minimumLevel\": \"information\"", json);
        Assert.Contains("\"kind\": \"http\"", json);
        Assert.Contains("\"kind\": \"tcp\"", json);
        Assert.Contains("\"mode\": \"disabled\"", json);
    }

    [Fact]
    public void ConnectionCutMode_RoundTripsAsCamelCase()
    {
        var config = GuardianConfig.CreateDefault();
        config.ConnectionMigration.Mode = ConnectionCutMode.AllowList;
        config.ConnectionMigration.ProcessPatterns.Add("yysls.exe");

        var json = ConfigJson.Serialize(config);
        var restored = ConfigJson.Deserialize(json);

        Assert.Contains("\"mode\": \"allowList\"", json);
        Assert.Equal(ConnectionCutMode.AllowList, restored!.ConnectionMigration.Mode);
        Assert.Contains("yysls.exe", restored.ConnectionMigration.ProcessPatterns);
    }

    [Fact]
    public void CommandKind_IsWrittenInCamelCase()
    {
        var config = GuardianConfig.CreateDefault();
        config.OfflineCommands.Add(new CommandDefinition { Id = "c", Name = "n", Kind = CommandKind.Shell });
        config.CampusAuth.Kind = CommandKind.Executable;

        var json = ConfigJson.Serialize(config);
        var restored = ConfigJson.Deserialize(json);

        Assert.Contains("\"shell\"", json);
        Assert.NotNull(restored);
        Assert.Equal(CommandKind.Shell, restored!.OfflineCommands[0].Kind);
        Assert.Equal(CommandKind.Executable, restored.CampusAuth.Kind);
    }

    [Theory]
    [InlineData("{\"version\": 3, \"logging\": {\"minimumLevel\": \"Debug\"}}", GuardianLogLevel.Debug)]
    [InlineData("{\"version\": 3, \"logging\": {\"minimumLevel\": \"warning\"}}", GuardianLogLevel.Warning)]
    [InlineData("{\"version\": 3, \"logging\": {\"minimumLevel\": 2}}", GuardianLogLevel.Information)]
    public void EnumValues_AreReadCaseInsensitivelyAndFromNumbers(string json, GuardianLogLevel expected)
    {
        var config = ConfigJson.Deserialize(json);

        Assert.NotNull(config);
        Assert.Equal(expected, config!.Logging.MinimumLevel);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        const string document = """
            {
              // a hand edited file may contain comments
              "version": 3,
              "probe": { "intervalSeconds": 30, },
            }
            """;

        var config = ConfigJson.Deserialize(document);

        Assert.NotNull(config);
        Assert.Equal(30, config!.Probe.IntervalSeconds);
    }

    [Fact]
    public void HelperDocuments_RoundTrip()
    {
        var request = new HelperRequest
        {
            Operation = HelperOperations.Enable,
            DeviceInstanceId = @"USB\VID_0BDA&PID_8153\001000001",
        };

        var requestJson = HelperProtocol.SerializeRequest(request);
        var restoredRequest = HelperProtocol.DeserializeRequest(requestJson);

        Assert.NotNull(restoredRequest);
        Assert.Equal(HelperOperations.Enable, restoredRequest!.Operation);
        Assert.Equal(request.DeviceInstanceId, restoredRequest.DeviceInstanceId);
        Assert.Equal(request.ProtocolVersion, restoredRequest.ProtocolVersion);
        Assert.Equal(request.CorrelationId, restoredRequest.CorrelationId);

        var response = new HelperResponse
        {
            Success = true,
            Operation = HelperOperations.Enable,
            DeviceInstanceId = request.DeviceInstanceId,
            CorrelationId = request.CorrelationId,
            Outcome = nameof(DeviceOperationOutcome.Succeeded),
            HelperElevated = true,
        };

        var responseJson = HelperProtocol.SerializeResponse(response);
        var restoredResponse = HelperProtocol.DeserializeResponse(responseJson);

        Assert.NotNull(restoredResponse);
        Assert.True(restoredResponse!.Success);
        Assert.True(restoredResponse.HelperElevated);
        Assert.Equal(request.CorrelationId, restoredResponse.CorrelationId);
        Assert.Contains("Succeeded", restoredResponse.Summary);
    }

    [Fact]
    public void UnknownEnumValue_IsRejected()
    {
        Assert.Throws<JsonException>(() => ConfigJson.Deserialize("{\"version\": 3, \"logging\": {\"minimumLevel\": \"verbose\"}}"));
    }
}
