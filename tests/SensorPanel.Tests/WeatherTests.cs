using SensorPanel.Sensors;

namespace SensorPanel.Tests;

/// <summary><see cref="Weather"/> without the network: the request, reading a reply, the schedule, and the icons and names.</summary>
public class WeatherTests
{
    // A reply as Open-Meteo sent it, trimmed.
    private const string Reply = """
        {"latitude":51.5,"longitude":-0.12,"timezone":"Europe/London",
         "current_units":{"time":"iso8601","interval":"seconds","temperature_2m":"°C","weather_code":"wmo code","is_day":""},
         "current":{"time":"2026-11-24T10:15","interval":900,"temperature_2m":11.6,"weather_code":2,"is_day":1},
         "daily_units":{"time":"iso8601","temperature_2m_max":"°C"},
         "daily":{"time":["2026-11-24"],"temperature_2m_max":[14.8],"temperature_2m_min":[6.9],"precipitation_probability_max":[20]}}
        """;

    private static readonly WeatherSettings London = new() { Latitude = 51.5, Longitude = -0.12 };

    // Three minutes past a slot: the next fetch is 14 minutes off, so no test's Read starts a real one.
    private static readonly DateTime JustFetched = new(2026, 11, 24, 10, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void The_request_rounds_the_location_and_asks_for_what_the_keys_show()
    {
        string url = Weather.Url(51.50735, -0.12776, fahrenheit: false);
        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=51.51&longitude=-0.13&", url);
        Assert.Contains("current=temperature_2m,weather_code,is_day", url);
        Assert.Contains("daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max", url);
        Assert.DoesNotContain("fahrenheit", url);
        Assert.EndsWith("&temperature_unit=fahrenheit", Weather.Url(40.7, -74, fahrenheit: true));
    }

    [Fact]
    public void The_request_does_not_follow_the_PCs_language()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("de-DE");     // writes 51,51
            Assert.Contains("latitude=51.51&", Weather.Url(51.51, 0, false));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void A_reply_gives_every_value()
    {
        var r = Weather.Parse(Reply);
        Assert.Equal(new Weather.Reading("2026-11-24T10:15", 11.6, 2, 1, 14.8, 6.9, 20), r);
    }

    [Fact]
    public void A_value_missing_from_the_reply_is_only_that_value_missing()
    {
        var r = Weather.Parse("""{"current":{"time":"t","temperature_2m":3},"daily":{"precipitation_probability_max":[null]}}""");
        Assert.Equal(new Weather.Reading("t", 3, null, null, null, null, null), r);
    }

    [Fact]
    public void A_reply_without_current_or_daily_values_is_an_error()
    {
        Assert.ThrowsAny<Exception>(() => Weather.Parse("""{"error":true,"reason":"Latitude must be in range of -90 to 90°."}"""));
    }

    [Theory]
    [InlineData("10:00:00", "10:02:00")]
    [InlineData("10:01:59", "10:02:00")]
    [InlineData("10:02:00", "10:17:00")]    // never now: strictly after
    [InlineData("10:16:30", "10:17:00")]
    [InlineData("10:40:00", "10:47:00")]
    [InlineData("10:47:01", "11:02:00")]    // over the hour
    [InlineData("23:59:00", "00:02:00")]    // and the day
    public void Fetches_are_due_two_minutes_past_each_quarter_hour(string now, string next)
    {
        var day = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        var at = day + TimeSpan.Parse(now);
        var expected = day + TimeSpan.Parse(next);
        if (expected < at) expected = expected.AddDays(1);
        Assert.Equal(expected, Weather.NextSlot(at));
    }

    [Fact]
    public void A_reply_still_on_the_last_step_is_asked_for_once_more_five_minutes_later()
    {
        using var weather = Weather.Open(London)!;
        var utc = new DateTime(2026, 11, 24, 10, 17, 0, DateTimeKind.Utc);
        var reading = Weather.Parse(Reply);

        weather.Received((reading, null), now: 1_000, utc);                      // new: next slot, 10:32
        Assert.Equal(1_000 + 15 * 60_000, weather.Due);

        weather.Received((reading, null), now: 2_000, utc.AddMinutes(15));       // same step again: retry in 5 min
        Assert.Equal(2_000 + 5 * 60_000, weather.Due);

        weather.Received((reading, null), now: 3_000, utc.AddMinutes(20));       // 10:37, still the same: back to the slots (10:47)
        Assert.Equal(3_000 + 10 * 60_000, weather.Due);
    }

    [Fact]
    public void A_new_step_after_an_earlier_one_goes_back_to_the_slots()
    {
        using var weather = Weather.Open(London)!;
        var utc = new DateTime(2026, 11, 24, 10, 17, 0, DateTimeKind.Utc);
        weather.Received((Weather.Parse(Reply), null), now: 1_000, utc);
        weather.Received((Weather.Parse(Reply) with { Time = "2026-11-24T10:30" }, null), now: 2_000, utc.AddMinutes(15));   // 10:32
        Assert.Equal(2_000 + 15 * 60_000, weather.Due);
    }

    [Fact]
    public void A_failed_fetch_is_tried_again_after_30_seconds_then_every_five_minutes()
    {
        using var weather = Weather.Open(London)!;
        weather.Received((null, "no network"), now: 1_000, DateTime.UtcNow);
        Assert.Equal(1_000 + 30_000, weather.Due);
        weather.Received((null, "no network"), now: 31_000, DateTime.UtcNow);
        Assert.Equal(31_000 + 5 * 60_000, weather.Due);
        weather.Received((Weather.Parse(Reply), null), now: 400_000, JustFetched);           // back: the slots again
        weather.Received((null, "no network"), now: 500_000, DateTime.UtcNow);              // and a new run of failures starts at 30 s
        Assert.Equal(500_000 + 30_000, weather.Due);
    }

    [Fact]
    public void The_last_reading_stays_up_through_a_failed_fetch()
    {
        using var weather = Weather.Open(London)!;
        weather.Received((Weather.Parse(Reply), null), Environment.TickCount64, JustFetched);
        weather.Received((null, "offline"), Environment.TickCount64, JustFetched);
        var snapshot = new Dictionary<string, double>();
        weather.Read(snapshot);
        Assert.Equal(11.6, snapshot["weather.temp"]);
        Assert.Equal(6, snapshot.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(51.5, null)]
    [InlineData(91.0, 0.0)]
    [InlineData(0.0, -181.0)]
    public void Without_a_valid_location_there_is_no_weather(double? latitude, double? longitude)
    {
        Assert.Null(Weather.Open(new WeatherSettings { Latitude = latitude, Longitude = longitude }));
        Assert.Null(Weather.Open(null));
    }

    [Fact]
    public void A_fresh_source_writes_no_values_until_a_reply_arrives()
    {
        using var weather = Weather.Open(London)!;
        var snapshot = new Dictionary<string, double> { ["weather.temp"] = 99 };   // left from before: must go
        weather.Received((null, "offline"), Environment.TickCount64, DateTime.UtcNow);   // so Read does not fetch
        weather.Read(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void A_reply_is_shown_through_the_keys()
    {
        using var weather = Weather.Open(London)!;
        weather.Received((Weather.Parse(Reply), null), Environment.TickCount64, JustFetched);
        var snapshot = new Dictionary<string, double>();
        weather.Read(snapshot);
        Assert.Equal(11.6, snapshot["weather.temp"]);
        Assert.Equal(14.8, snapshot["weather.high"]);
        Assert.Equal(6.9, snapshot["weather.low"]);
        Assert.Equal(20, snapshot["weather.precip"]);
        Assert.Equal(2, snapshot["weather.code"]);
        Assert.Equal(1, snapshot["weather.day"]);
    }

    [Fact]
    public void A_reading_over_an_hour_old_is_dropped()
    {
        using var weather = Weather.Open(London)!;
        long longAgo = Environment.TickCount64 - (long)Weather.MaxAge.TotalMilliseconds - 1;
        weather.Received((Weather.Parse(Reply), null), longAgo, JustFetched);
        weather.Received((null, "offline"), Environment.TickCount64, DateTime.UtcNow);    // a failure since: nothing newer
        var snapshot = new Dictionary<string, double>();
        weather.Read(snapshot);
        Assert.Empty(snapshot);
    }

    [Theory]
    [InlineData(0, 1, "☀", "Clear")]
    [InlineData(0, 0, "☾", "Clear")]          // the moon at night
    [InlineData(2, 1, "⛅", "Partly cloudy")]
    [InlineData(3, 1, "☁", "Cloudy")]
    [InlineData(45, 1, "☁", "Fog")]
    [InlineData(63, 1, "☂", "Rain")]
    [InlineData(81, 0, "☂", "Showers")]
    [InlineData(73, 1, "❄", "Snow")]
    [InlineData(95, 1, "⛈", "Thunderstorm")]
    [InlineData(42, 1, "☁", "Unknown")]       // not a WMO code Open-Meteo uses
    public void Each_code_has_an_icon_and_a_name(double code, double day, string icon, string name)
    {
        Assert.Equal(icon, Weather.Icon(code, day));
        Assert.Equal(name, Weather.Condition(code));
    }
}
