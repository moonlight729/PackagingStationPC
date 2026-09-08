using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public interface IFactoryUploadService
{
    string Mode { get; }
    Task<IntegrationResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IntegrationResult> UploadAsync(ProductionRecord record, CancellationToken cancellationToken = default);
}

public sealed class MockFactoryUploadService : IFactoryUploadService
{
    public string Mode => "mock";

    public async Task<IntegrationResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        return new IntegrationResult(true, "模拟工厂系统连接正常。");
    }

    public async Task<IntegrationResult> UploadAsync(
        ProductionRecord record,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        return new IntegrationResult(true, $"模拟上传成功: {record.PackageSn}", "{\"accepted\":true}");
    }
}

public sealed class SwiftMomFactoryUploadService : IFactoryUploadService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FactorySystemConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly AppLogger? _logger;

    public SwiftMomFactoryUploadService(
        FactorySystemConfiguration configuration,
        HttpClient? httpClient = null,
        AppLogger? logger = null)
    {
        _configuration = configuration;
        _ownsHttpClient = httpClient is null;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(configuration.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 120));
    }

    public string Mode => "swiftmom";

    public async Task<IntegrationResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var validationError = ValidateConfiguration();
        if (validationError is not null)
        {
            return new IntegrationResult(false, validationError);
        }

        try
        {
            var login = await LoginAsync(cancellationToken);
            return login.IsMesSuccess && !string.IsNullOrWhiteSpace(login.Token)
                ? new IntegrationResult(true, "SwiftMOM 登录验证成功。")
                : new IntegrationResult(false, DescribeFailure("SwiftMOM 登录", login));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IntegrationResult(false, $"SwiftMOM 登录失败: {ex.Message}");
        }
    }

    public async Task<IntegrationResult> UploadAsync(
        ProductionRecord record,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateConfiguration();
        if (validationError is not null)
        {
            return new IntegrationResult(false, validationError);
        }

        try
        {
            await LogAsync("INFO", $"SwiftMOM upload start pcbSeq={record.PackageSn}, prodNo={_configuration.ProdNo}, stationNo={_configuration.StationNo}");
            await LogRequestAsync("clientLogin", _configuration.ClientLoginPath, new
            {
                userNo = _configuration.UserNo,
                password = "***"
            });
            var login = await LoginAsync(cancellationToken);
            await LogStageAsync("clientLogin", login);
            if (!login.IsMesSuccess || string.IsNullOrWhiteSpace(login.Token))
            {
                return new IntegrationResult(false, DescribeFailure("SwiftMOM 登录", login));
            }

            var checkPayload = new
            {
                pcbSeq = record.PackageSn,
                prodNo = _configuration.ProdNo,
                stationNo = _configuration.StationNo,
                retest = _configuration.Retest
            };
            await LogRequestAsync("checkRoute", _configuration.CheckRoutePath, checkPayload);
            var check = await PostAsync(
                _configuration.CheckRoutePath,
                checkPayload,
                login.Token,
                cancellationToken);
            await LogStageAsync("checkRoute", check);
            if (!check.IsMesSuccess)
            {
                return new IntegrationResult(false, DescribeFailure("过站检查", check), check.Body);
            }

            var createPayload = CreateRoutePayload(record);
            await LogRequestAsync("createRoute", _configuration.CreateRoutePath, createPayload);
            var create = await PostAsync(
                _configuration.CreateRoutePath,
                createPayload,
                login.Token,
                cancellationToken);
            await LogStageAsync("createRoute", create);
            return create.IsMesSuccess
                ? new IntegrationResult(true, $"SwiftMOM 过站成功: {record.PackageSn}", create.Body)
                : new IntegrationResult(false, DescribeFailure("过站写入", create), create.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync("ERROR", $"SwiftMOM upload exception pcbSeq={record.PackageSn}: {ex}");
            return new IntegrationResult(false, $"SwiftMOM 上传失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private Task<SwiftMomResponse> LoginAsync(CancellationToken cancellationToken) => PostAsync(
        _configuration.ClientLoginPath,
        new
        {
            userNo = _configuration.UserNo,
            password = _configuration.Password
        },
        token: null,
        cancellationToken);

    private object CreateRoutePayload(ProductionRecord record) => new
    {
        pcbSeq = record.PackageSn,
        prodNo = _configuration.ProdNo,
        stationNo = _configuration.StationNo,
        result = "PASS",
        remark = string.Empty,
        testItem = string.Empty,
        userNo = _configuration.UserNo,
        weight = 0d,
        packNo = string.Empty,
        rmk1 = string.Empty,
        rmk2 = CreateVersionString(record),
        rmk3 = string.Empty,
        rmk4 = string.Empty,
        testDetail = Array.Empty<object>()
    };

    private static string CreateVersionString(ProductionRecord record)
    {
        var versions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["deviceSn"] = record.DeviceSerial
        };
        foreach (var component in record.VersionSnapshot.Components.Where(component =>
                     component.Component is "box" or "glasses" or "band"))
        {
            versions[component.Component] = new Dictionary<string, string>(
                component.Values,
                StringComparer.OrdinalIgnoreCase);
        }

        return JsonSerializer.Serialize(versions, JsonOptions);
    }

    private async Task<SwiftMomResponse> PostAsync(
        string path,
        object payload,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("token", token);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse(response, body);
    }

    private static SwiftMomResponse ParseResponse(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return new SwiftMomResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                ReadMsgId(root),
                ReadString(root, "msgStr"),
                ReadString(root, "token"),
                body,
                null);
        }
        catch (JsonException ex)
        {
            return new SwiftMomResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                null,
                string.Empty,
                string.Empty,
                body,
                ex.Message);
        }
    }

    private string? ValidateConfiguration()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_configuration.UserNo)) missing.Add("userNo");
        if (string.IsNullOrWhiteSpace(_configuration.Password)) missing.Add("password");
        if (string.IsNullOrWhiteSpace(_configuration.ProdNo)) missing.Add("prodNo");
        if (string.IsNullOrWhiteSpace(_configuration.StationNo)) missing.Add("stationNo");
        if (missing.Count > 0)
        {
            return $"SwiftMOM 配置缺少字段: {string.Join(", ", missing)}";
        }

        return _configuration.Retest is "0" or "1"
            ? null
            : "SwiftMOM 配置 retest 只能是字符串 0 或 1。";
    }

    private static string DescribeFailure(string operation, SwiftMomResponse response)
    {
        if (!response.HttpSuccess)
        {
            return $"{operation}失败: HTTP {response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        }

        if (response.MsgId is null)
        {
            var detail = string.IsNullOrWhiteSpace(response.ParseError)
                ? "响应缺少 msgId"
                : $"响应不是有效 JSON: {response.ParseError}";
            return $"{operation}失败: {detail}";
        }

        return $"{operation}失败: msgId={response.MsgId}, {response.MsgStr}".TrimEnd(' ', ',');
    }

    private Task LogRequestAsync(string stage, string path, object payload) => LogAsync(
        "INFO",
        $"SwiftMOM request stage={stage}, url={new Uri(_httpClient.BaseAddress!, path.TrimStart('/'))}, body={JsonSerializer.Serialize(payload, JsonOptions)}");

    private Task LogStageAsync(string stage, SwiftMomResponse response) => LogAsync(
        response.IsMesSuccess ? "INFO" : "ERROR",
        $"SwiftMOM response stage={stage}, http={response.StatusCode}, msgId={response.MsgId?.ToString() ?? "null"}, msgStr={response.MsgStr}, body={RedactResponse(response.Body, response.Token)}");

    private static string RedactResponse(string body, string token)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject value && value.ContainsKey("token"))
            {
                value["token"] = "***";
            }

            var sanitized = node?.ToJsonString(JsonOptions) ?? body;
            return string.IsNullOrWhiteSpace(token) ? sanitized : sanitized.Replace(token, "***", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(token) ? body : body.Replace(token, "***", StringComparison.Ordinal);
        }
    }

    private Task LogAsync(string level, string message) =>
        _logger?.WriteAsync(level, message) ?? Task.CompletedTask;

    private static int? ReadMsgId(JsonElement root)
    {
        if (!root.TryGetProperty("msgId", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
    }

    private sealed record SwiftMomResponse(
        bool HttpSuccess,
        int StatusCode,
        string ReasonPhrase,
        int? MsgId,
        string MsgStr,
        string Token,
        string Body,
        string? ParseError)
    {
        public bool IsMesSuccess => HttpSuccess && MsgId == 0;
    }
}

public static class FactoryUploadServiceFactory
{
    public static IFactoryUploadService Create(FactorySystemConfiguration configuration, AppLogger? logger = null) =>
        string.Equals(configuration.Mode, "swiftmom", StringComparison.OrdinalIgnoreCase)
            ? new SwiftMomFactoryUploadService(configuration, logger: logger)
            : new MockFactoryUploadService();
}
