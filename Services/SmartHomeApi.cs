using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homie.Services;

// ---------- модель дома (только то, что нужно для панели) ----------

public sealed record Household(string Id, string Name);
public sealed record Room(string Id, string Name, string? HouseholdId);
public sealed record Scenario(string Id, string Name, bool IsActive);

/// <summary>Умение устройства: вкл/выкл, диапазон (яркость, температура…), цвет и т.п.</summary>
public sealed record Capability(string Type, string Instance, bool Retrievable, JsonElement? Value,
    double Min = 0, double Max = 100, double Precision = 1, string? Unit = null,
    string? ColorModel = null, int TemperatureMin = 0, int TemperatureMax = 0)
{
    public bool IsOnOff => Type == "devices.capabilities.on_off";
    public bool IsRange => Type == "devices.capabilities.range";
    public bool IsColor => Type == "devices.capabilities.color_setting";
    /// <summary>Лампа умеет цвет (модель hsv или rgb).</summary>
    public bool SupportsColor => IsColor && ColorModel is "hsv" or "rgb";
    /// <summary>Лампа умеет оттенки белого (тёплый/холодный).</summary>
    public bool SupportsWhite => IsColor && TemperatureMax > TemperatureMin;
    public bool? OnValue => IsOnOff && Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
    public double? NumberValue => Value is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
}

/// <summary>Показание датчика: температура, влажность, мощность…</summary>
public sealed record Property(string Instance, double? Value, string? Unit);

public sealed record Device(string Id, string Name, string Type, string? RoomId, string? HouseholdId,
    IReadOnlyList<Capability> Capabilities, IReadOnlyList<Property> Properties, bool IsGroup = false)
{
    public Capability? OnOff => Capabilities.FirstOrDefault(c => c.IsOnOff);
    public Capability? Color => Capabilities.FirstOrDefault(c => c.IsColor && (c.SupportsColor || c.SupportsWhite));
    public Capability? Range(string instance) => Capabilities.FirstOrDefault(c => c.IsRange && c.Instance == instance);
}

public sealed record HomeData(
    IReadOnlyList<Household> Households,
    IReadOnlyList<Room> Rooms,
    IReadOnlyList<Device> Devices,   // устройства и группы
    IReadOnlyList<Scenario> Scenarios);

public sealed class UnauthorizedException() : Exception("Токен недействителен или истёк");

/// <summary>
/// Платформа умного дома Яндекса: https://api.iot.yandex.net.
/// Нужен OAuth-токен со scope iot:view и iot:control (получает сам пользователь, см. README).
/// </summary>
public sealed class SmartHomeApi(Func<string?> tokenProvider) : IDisposable
{
    private const string BaseUrl = "https://api.iot.yandex.net/v1.0/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<HomeData> GetHomeAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "user/info", null, ct);
        var root = doc.RootElement;

        var households = Array(root, "households").Select(h => new Household(Str(h, "id"), Str(h, "name"))).ToList();
        var rooms = Array(root, "rooms").Select(r => new Room(Str(r, "id"), Str(r, "name"), OptStr(r, "household_id"))).ToList();
        var scenarios = Array(root, "scenarios").Select(s => new Scenario(Str(s, "id"), Str(s, "name"),
            s.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True)).ToList();

        var devices = Array(root, "devices").Select(d => ParseDevice(d, isGroup: false)).ToList();
        devices.AddRange(Array(root, "groups").Select(g => ParseDevice(g, isGroup: true)));
        return new HomeData(households, rooms, devices, scenarios);
    }

    public Task SetOnOffAsync(Device device, bool on, CancellationToken ct = default) =>
        ActAsync(device, new JsonObject { ["type"] = "devices.capabilities.on_off", ["state"] = new JsonObject { ["instance"] = "on", ["value"] = on } }, ct);

    public Task SetRangeAsync(Device device, string instance, double value, CancellationToken ct = default) =>
        ActAsync(device, new JsonObject { ["type"] = "devices.capabilities.range", ["state"] = new JsonObject { ["instance"] = instance, ["value"] = value } }, ct);

    /// <summary>Цвет лампы: в модели самой лампы (hsv или rgb).</summary>
    public Task SetColorAsync(Device device, Windows.UI.Color color, CancellationToken ct = default)
    {
        var model = device.Color?.ColorModel ?? "hsv";
        JsonNode value;
        if (model == "rgb")
        {
            value = (color.R << 16) | (color.G << 8) | color.B;
        }
        else
        {
            var (h, s, v) = ToHsv(color);
            value = new JsonObject { ["h"] = h, ["s"] = s, ["v"] = v };
        }
        return ActAsync(device, new JsonObject
        {
            ["type"] = "devices.capabilities.color_setting",
            ["state"] = new JsonObject { ["instance"] = model, ["value"] = value },
        }, ct);
    }

    /// <summary>Оттенок белого в кельвинах (2700 — тёплый, 6500 — холодный), в пределах лампы.</summary>
    public Task SetWhiteAsync(Device device, int kelvin, CancellationToken ct = default)
    {
        var c = device.Color;
        int k = c is null ? kelvin : Math.Clamp(kelvin, c.TemperatureMin, c.TemperatureMax);
        return ActAsync(device, new JsonObject
        {
            ["type"] = "devices.capabilities.color_setting",
            ["state"] = new JsonObject { ["instance"] = "temperature_k", ["value"] = k },
        }, ct);
    }

    private static (int H, int S, int V) ToHsv(Windows.UI.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return ((int)Math.Round(h), (int)Math.Round(max == 0 ? 0 : d / max * 100), (int)Math.Round(max * 100));
    }

    public async Task RunScenarioAsync(string scenarioId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"scenarios/{Uri.EscapeDataString(scenarioId)}/actions", null, ct);
    }

    /// <summary>Одно действие для устройства или для всей группы.</summary>
    private async Task ActAsync(Device device, JsonObject action, CancellationToken ct)
    {
        JsonObject body;
        string path;
        if (device.IsGroup)
        {
            path = $"groups/{Uri.EscapeDataString(device.Id)}/actions";
            body = new JsonObject { ["actions"] = new JsonArray(action) };
        }
        else
        {
            path = "devices/actions";
            body = new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["id"] = device.Id, ["actions"] = new JsonArray(action) }) };
        }
        using var _ = await SendAsync(HttpMethod.Post, path, body.ToJsonString(), ct);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        var token = tokenProvider() ?? throw new UnauthorizedException();
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedException();
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static Device ParseDevice(JsonElement d, bool isGroup)
    {
        var capabilities = Array(d, "capabilities").Select(c =>
        {
            JsonElement? value = null;
            string instance = "";
            if (c.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
            {
                instance = OptStr(state, "instance") ?? "";
                if (state.TryGetProperty("value", out var v)) value = v.Clone();
            }
            double min = 0, max = 100, precision = 1;
            string? unit = null, colorModel = null;
            int tMin = 0, tMax = 0;
            if (c.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(instance)) instance = OptStr(p, "instance") ?? "";
                unit = OptStr(p, "unit");
                colorModel = OptStr(p, "color_model");
                if (p.TryGetProperty("range", out var r))
                {
                    min = r.TryGetProperty("min", out var mn) ? mn.GetDouble() : min;
                    max = r.TryGetProperty("max", out var mx) ? mx.GetDouble() : max;
                    precision = r.TryGetProperty("precision", out var pr) ? pr.GetDouble() : precision;
                }
                if (p.TryGetProperty("temperature_k", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    tMin = t.TryGetProperty("min", out var a) ? a.GetInt32() : 0;
                    tMax = t.TryGetProperty("max", out var b) ? b.GetInt32() : 0;
                }
            }
            return new Capability(Str(c, "type"), instance,
                !c.TryGetProperty("retrievable", out var ret) || ret.ValueKind != JsonValueKind.False,
                value, min, max, precision, unit, colorModel, tMin, tMax);
        }).ToList();

        var properties = Array(d, "properties").Select(p =>
        {
            double? value = null;
            string instance = "";
            if (p.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
            {
                instance = OptStr(state, "instance") ?? "";
                if (state.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) value = v.GetDouble();
            }
            bool hasParams = p.TryGetProperty("parameters", out var par) && par.ValueKind == JsonValueKind.Object;
            string? unit = hasParams ? OptStr(par, "unit") : null;
            if (string.IsNullOrEmpty(instance) && hasParams) instance = OptStr(par, "instance") ?? "";
            return new Property(instance, value, unit);
        }).ToList();

        string? roomId = OptStr(d, "room");
        return new Device(Str(d, "id"), Str(d, "name"), OptStr(d, "type") ?? "", roomId, OptStr(d, "household_id"),
            capabilities, properties, isGroup);
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) => OptStr(e, name) ?? "";
    private static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
