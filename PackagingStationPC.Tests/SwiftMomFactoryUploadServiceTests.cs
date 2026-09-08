using System.Net;
using System.Text;
using System.Text.Json;
using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class SwiftMomFactoryUploadServiceTests
{
    [Fact]
    public async Task Upload_PerformsLoginCheckAndCreateRouteInOrder()
    {
        var handler = new RecordingHandler(
            JsonResponse("""{"msgId":0,"msgStr":"ok","token":"token-123"}"""),
            JsonResponse("""{"msgId":"0","msgStr":"check ok"}"""),
            JsonResponse("""{"msgId":0,"msgStr":"create ok"}"""));
        using var client = new HttpClient(handler);
        using var service = new SwiftMomFactoryUploadService(Configuration(), client);

        var result = await service.UploadAsync(Record());

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/swiftmom/api/clientLogin", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/swiftmom/api/checkRoute", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("/swiftmom/api/createRoute", handler.Requests[2].Uri.AbsolutePath);
        Assert.Null(handler.Requests[0].Token);
        Assert.Equal("token-123", handler.Requests[1].Token);
        Assert.Equal("token-123", handler.Requests[2].Token);

        using var login = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("mss", login.RootElement.GetProperty("userNo").GetString());
        Assert.Equal("secret", login.RootElement.GetProperty("password").GetString());

        using var check = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("G1F2632000001", check.RootElement.GetProperty("pcbSeq").GetString());
        Assert.Equal("YC20260903001-YZ", check.RootElement.GetProperty("prodNo").GetString());
        Assert.Equal("T01", check.RootElement.GetProperty("stationNo").GetString());
        Assert.Equal("0", check.RootElement.GetProperty("retest").GetString());

        using var create = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("PASS", create.RootElement.GetProperty("result").GetString());
        Assert.Equal("mss", create.RootElement.GetProperty("userNo").GetString());
        Assert.Equal(0, create.RootElement.GetProperty("weight").GetDouble());
        Assert.Empty(create.RootElement.GetProperty("testDetail").EnumerateArray());

        Assert.Equal(string.Empty, create.RootElement.GetProperty("remark").GetString());
        Assert.Equal(string.Empty, create.RootElement.GetProperty("rmk1").GetString());
        var versionJson = create.RootElement.GetProperty("rmk2").GetString();
        Assert.NotNull(versionJson);
        using var versions = JsonDocument.Parse(versionJson);
        Assert.False(versions.RootElement.TryGetProperty("host_app", out _));
        Assert.False(versions.RootElement.TryGetProperty("host_system", out _));
        Assert.Equal("device-001", versions.RootElement.GetProperty("deviceSn").GetString());
        Assert.Equal(
            "V0.0.7-260822.141149-f7ee5ba46",
            versions.RootElement.GetProperty("glasses").GetProperty("app").GetString());
        Assert.Equal("LEFT-SN", versions.RootElement.GetProperty("band").GetProperty("leftSn").GetString());
        Assert.Equal("3.0.3.3", versions.RootElement.GetProperty("band").GetProperty("leftVersion").GetString());
        Assert.Equal("RIGHT-SN", versions.RootElement.GetProperty("band").GetProperty("rightSn").GetString());
        Assert.Equal("3.0.3.2", versions.RootElement.GetProperty("band").GetProperty("rightVersion").GetString());
    }

    [Fact]
    public async Task Upload_DoesNotCreateRouteWhenCheckRouteRejects()
    {
        var handler = new RecordingHandler(
            JsonResponse("""{"msgId":0,"msgStr":"ok","token":"token-123"}"""),
            JsonResponse("""{"msgId":1,"msgStr":"该条码已经过站"}"""));
        using var client = new HttpClient(handler);
        using var service = new SwiftMomFactoryUploadService(Configuration(), client);

        var result = await service.UploadAsync(Record());

        Assert.False(result.Success);
        Assert.Contains("该条码已经过站", result.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TestConnection_OnlyCallsClientLogin()
    {
        var handler = new RecordingHandler(
            JsonResponse("""{"msgId":"0","msgStr":"ok","token":"token-123"}"""));
        using var client = new HttpClient(handler);
        using var service = new SwiftMomFactoryUploadService(Configuration(), client);

        var result = await service.TestConnectionAsync();

        Assert.True(result.Success, result.Message);
        Assert.Single(handler.Requests);
        Assert.Equal("/swiftmom/api/clientLogin", handler.Requests[0].Uri.AbsolutePath);
    }

    private static FactorySystemConfiguration Configuration() => new()
    {
        Mode = "swiftmom",
        BaseUrl = "http://192.168.10.17:8080/swiftmom/api",
        ClientLoginPath = "clientLogin",
        CheckRoutePath = "checkRoute",
        CreateRoutePath = "createRoute",
        UserNo = "mss",
        Password = "secret",
        ProdNo = "YC20260903001-YZ",
        StationNo = "T01",
        Retest = "0"
    };

    private static ProductionRecord Record() => new()
    {
        PackageSn = "G1F2632000001",
        DeviceSerial = "device-001",
        VersionSnapshot = new DeviceVersionSnapshot
        {
            Components =
            [
                new VersionComponentResult
                {
                    Component = "glasses",
                    Values = new Dictionary<string, string>
                    {
                        ["uboot"] = "V0.0.2-260822.141024-f7ee5ba46",
                        ["kernel"] = "V0.0.5-260822.141034-f7ee5ba46",
                        ["rootfs"] = "V0.0.4-260822.141051-f7ee5ba46",
                        ["app"] = "V0.0.7-260822.141149-f7ee5ba46"
                    }
                },
                new VersionComponentResult
                {
                    Component = "band",
                    Values = new Dictionary<string, string>
                    {
                        ["leftSn"] = "LEFT-SN",
                        ["leftVersion"] = "3.0.3.3",
                        ["rightSn"] = "RIGHT-SN",
                        ["rightVersion"] = "3.0.3.2"
                    }
                }
            ]
        }
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var token = request.Headers.TryGetValues("token", out var values)
                ? values.Single()
                : null;
            Requests.Add(new RecordedRequest(request.RequestUri!, token, body));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(Uri Uri, string? Token, string Body);
}
