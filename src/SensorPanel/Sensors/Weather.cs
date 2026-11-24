using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace SensorPanel.Sensors;

/// <summary>settings.json's "weather": where the weather keys are for. Without both coordinates they show "--".</summary>
public sealed class WeatherSettings
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Temperatures in °F instead of °C.</summary>
    public bool Fahrenheit { get; set; }
}

/// <summary>
/// The weather.* keys: the current conditions and today's high, low and chance of precipitation,
/// from Open-Meteo (open-meteo.com: free, no key, one small request).
///
/// Nothing runs between fetches, and no timer is kept: <see cref="Read"/> is called every tick and
/// starts a fetch when one is due. So fetching stops whenever the panel stops (monitors off, sleep)
/// and catches up on the first tick after, and it ends with the layout: SensorHub keeps one of these
/// only while a widget shows a weather key.
///
/// Open-Meteo's current conditions come in 15-minute steps, so a fetch is due a little after each
/// quarter hour (<see cref="NextSlot"/>). Each reply says which step it holds; if it is still the one
/// already shown, the new step was not out yet, and the fetch is tried once more 5 minutes later.
/// A failed fetch is tried again after 30 s (after a wake the network is often still coming up),
/// then every 5 minutes. The last reading stays up until it is an hour old.
/// </summary>
internal sealed class Weather : IDisposable
{
    public const string Temp = "weather.temp", High = "weather.high", Low = "weather.low",
        Precip = "weather.precip", Code = "weather.code", Day = "weather.day";

    public static readonly string[] Keys = [Temp, High, Low, Precip, Code, Day];

    /// <summary>The keys a text widget showing the conditions ("weather": "icon" or "condition") reads.</summary>
    public static readonly string[] TextKeys = [Code, Day];

    internal static readonly TimeSpan
        StepRetry = TimeSpan.FromMinutes(5),        // the reply still held the step already shown
        FirstRetry = TimeSpan.FromSeconds(30),      // after the first failed fetch in a row
        Retry = TimeSpan.FromMinutes(5),            // after each further one
        MaxAge = TimeSpan.FromHours(1);

    /// <summary>One reply. Time is the 15-minute step the current values are for, as Open-Meteo writes it.</summary>
    internal sealed record Reading(string? Time, double? Temp, double? Code, double? Day, double? High, double? Low, double? Precip);

    // A reply is about 600 bytes; the cap keeps a broken or hostile one from filling memory.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 64 * 1024 };
    private static bool _reported;              // a missing or bad location is logged once per run, not on every layout switch
    private readonly string _url;
    private Task<(Reading? Reading, string? Error)>? _fetch;   // in flight, or done and not yet looked at
    private Reading? _reading;
    private long _readAt, _due;                 // Environment.TickCount64; it keeps counting through sleep
    private bool _retried;                      // the current step has already been asked for twice
    private int _failures;                      // in a row

    private Weather(string url)
    {
        _url = url;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SensorPanel");
    }

    /// <summary>A source for these settings, or null (logged) when they do not say where.</summary>
    public static Weather? Open(WeatherSettings? settings)
    {
        if (settings is not { Latitude: double lat, Longitude: double lon })
        {
            if (!_reported) Log.Info("The weather shows \"--\": settings.json has no \"weather\" with a latitude and longitude.");
            _reported = true;
            return null;
        }
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            if (!_reported) Log.Warn("settings.json: weather.latitude must be from -90 to 90, and weather.longitude from -180 to 180, so the weather shows \"--\".");
            _reported = true;
            return null;
        }
        return new Weather(Url(lat, lon, settings.Fahrenheit));
    }

    /// <summary>
    /// The request. Coordinates go out rounded to two decimals, about a kilometre, which is finer
    /// than the weather models themselves and says less about where this PC is.
    /// </summary>
    internal static string Url(double latitude, double longitude, bool fahrenheit) => string.Format(CultureInfo.InvariantCulture,
        "https://api.open-meteo.com/v1/forecast?latitude={0:0.##}&longitude={1:0.##}" +
        "&current=temperature_2m,weather_code,is_day" +
        "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
        "&timezone=auto&forecast_days=1{2}",
        latitude, longitude, fahrenheit ? "&temperature_unit=fahrenheit" : "");

    /// <summary>
    /// Called every tick, on the panel's thread, which is the only one that touches this object's
    /// state: a fetch runs on the thread pool, and its result is picked up here on a later tick.
    /// </summary>
    public void Read(Dictionary<string, double> snapshot)
    {
        long now = Environment.TickCount64;
        if (_fetch is { IsCompleted: true } done)
        {
            _fetch = null;
            Received(done.Result, now, DateTime.UtcNow);
        }
        // On the thread pool from the start: the first request resolves the proxy synchronously
        // (WPAD auto-detection can take seconds), which must not hold up the panel.
        if (_fetch is null && now >= _due) _fetch = Task.Run(FetchAsync);

        bool fresh = _reading is not null && now - _readAt < MaxAge.TotalMilliseconds;
        Put(Temp, _reading?.Temp);
        Put(High, _reading?.High);
        Put(Low, _reading?.Low);
        Put(Precip, _reading?.Precip);
        Put(Code, _reading?.Code);
        Put(Day, _reading?.Day);

        void Put(string key, double? value)
        {
            if (fresh && value is double v) snapshot[key] = v;
            else snapshot.Remove(key);
        }
    }

    /// <summary>When the next fetch is due, in Environment.TickCount64 ms. For the tests.</summary>
    internal long Due => _due;

    /// <summary>Takes a finished fetch's result and decides when the next one is due. Internal for the tests.</summary>
    internal void Received((Reading? Reading, string? Error) result, long now, DateTime utcNow)
    {
        if (result.Reading is not { } reading)
        {
            if (++_failures == 2) Log.Info($"Cannot get the weather from Open-Meteo ({result.Error}); trying again every {Retry.TotalMinutes:0} minutes.");
            _due = now + (long)(_failures == 1 ? FirstRetry : Retry).TotalMilliseconds;
            return;
        }

        if (_failures > 1) Log.Info("The weather is back.");
        _failures = 0;
        bool sameStep = _reading is not null && reading.Time == _reading.Time;
        (_reading, _readAt) = (reading, now);
        if (sameStep && !_retried)
        {
            _retried = true;
            _due = now + (long)StepRetry.TotalMilliseconds;
        }
        else
        {
            _retried = false;
            _due = now + (long)(NextSlot(utcNow) - utcNow).TotalMilliseconds;
        }
    }

    /// <summary>The next fetch time after <paramref name="utcNow"/>: 2 minutes past each quarter hour, by then usually published.</summary>
    internal static DateTime NextSlot(DateTime utcNow)
    {
        var quarter = new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc)
                      .AddMinutes(utcNow.Minute / 15 * 15);
        var slot = quarter.AddMinutes(2);
        return slot > utcNow ? slot : slot.AddMinutes(15);
    }

    /// <summary>Never throws: whatever goes wrong (no network, a timeout, an odd reply, being disposed) comes back as the error.</summary>
    private async Task<(Reading?, string?)> FetchAsync()
    {
        try
        {
            string json = await _http.GetStringAsync(_url).ConfigureAwait(false);
            return (Parse(json), null);
        }
        catch (Exception e)
        {
            return (null, e is TaskCanceledException ? "no answer within 20 s" : e.Message);
        }
    }

    /// <summary>The values out of a reply. Any one of them may be missing; a reply without "current" or "daily" throws.</summary>
    internal static Reading Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        return new Reading(
            current.TryGetProperty("time", out var time) ? time.GetString() : null,
            Number(current, "temperature_2m"), Number(current, "weather_code"), Number(current, "is_day"),
            First(daily, "temperature_2m_max"), First(daily, "temperature_2m_min"), First(daily, "precipitation_probability_max"));

        static double? Number(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        static double? First(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
            && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;
    }

    /// <summary>A Segoe UI Symbol glyph for a WMO weather code, as Open-Meteo gives it; the moon for a clear night. "--" for no code.</summary>
    public static string Icon(double? code, double? day)
    {
        bool night = day == 0;
        return (int?)code switch
        {
            null => "--",
            0 or 1 => night ? "\u263E" : "\u2600",                // moon, sun
            2 => night ? "\u2601" : "\u26C5",                     // cloud, sun behind a cloud
            3 or 45 or 48 => "\u2601",                            // cloud: overcast, fog
            >= 51 and <= 67 or >= 80 and <= 82 => "\u2602",       // umbrella: drizzle, rain, showers
            >= 71 and <= 77 or 85 or 86 => "\u2744",              // snowflake
            >= 95 => "\u26C8",                                    // thunder cloud and rain
            _ => "\u2601",
        };
    }

    /// <summary>A short name for a WMO weather code. "--" for no code.</summary>
    public static string Condition(double? code) => (int?)code switch
    {
        null => "--",
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Storm, hail",
        _ => "Unknown",
    };

    /// <summary>Ends any fetch in flight; it then finishes as an error that nothing reads.</summary>
    public void Dispose() => _http.Dispose();
}
