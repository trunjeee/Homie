using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homie.Services;

public enum TimerKind { Timer, Reminder }

/// <summary>Повтор напоминания.</summary>
public enum TimerRepeat { None, Daily, Weekdays, Weekends, Weekly }

/// <summary>Таймер («на 10 минут») или напоминание («в 18:30 позвонить маме», «по будням в 8»).</summary>
public sealed class TimerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TimerKind Kind { get; set; }
    /// <summary>О чём: «пицца», «позвонить маме». Может быть пустым.</summary>
    public string Label { get; set; } = "";
    /// <summary>Когда сработает (местное время).</summary>
    public DateTime Due { get; set; }
    /// <summary>Для таймера — на сколько ставили (для «+5 минут» и подписи).</summary>
    public int DurationSeconds { get; set; }

    public TimerRepeat Repeat { get; set; }
    /// <summary>Для Weekly — дни недели.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    /// <summary>Для повторов — время срабатывания.</summary>
    public int Hour { get; set; }
    public int Minute { get; set; }

    [JsonIgnore] public bool IsRecurring => Repeat != TimerRepeat.None;

    /// <summary>Следующее срабатывание повторяющегося напоминания после момента after.</summary>
    public DateTime NextAfter(DateTime after)
    {
        var day = after.Date;
        for (int i = 0; i < 8; i++, day = day.AddDays(1))
        {
            var at = day.AddHours(Hour).AddMinutes(Minute);
            if (at <= after) continue;
            bool fits = Repeat switch
            {
                TimerRepeat.Daily => true,
                TimerRepeat.Weekdays => at.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                TimerRepeat.Weekends => at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                TimerRepeat.Weekly => Days.Contains(at.DayOfWeek),
                _ => false,
            };
            if (fits) return at;
        }
        return after.AddDays(1);
    }
}

[JsonSerializable(typeof(List<TimerEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class TimerJsonContext : JsonSerializerContext;

/// <summary>Таймеры хранятся в %LOCALAPPDATA%\Homie\timers.json — переживают перезапуск Homie и ПК.</summary>
public static class TimerStore
{
    private static readonly string File_ = Path.Combine(SettingsStore.Dir, "timers.json");

    public static List<TimerEntry> Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize(File.ReadAllText(File_), TimerJsonContext.Default.ListTimerEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    public static void Save(List<TimerEntry> items)
    {
        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(File_, JsonSerializer.Serialize(items, TimerJsonContext.Default.ListTimerEntry));
    }
}

/// <summary>Подписи для людей: «10 минут», «в 18:30», «по будням в 8:00».</summary>
public static class TimerText
{
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))} {Plural((int)Math.Round(span.TotalSeconds), "секунду", "секунды", "секунд")}";
        int h = (int)span.TotalHours, m = span.Minutes, s = span.Seconds;
        var parts = new List<string>();
        if (h > 0) parts.Add($"{h} {Plural(h, "час", "часа", "часов")}");
        if (m > 0) parts.Add($"{m} {Plural(m, "минуту", "минуты", "минут")}");
        if (h == 0 && s > 0 && m < 5) parts.Add($"{s} {Plural(s, "секунду", "секунды", "секунд")}");
        return string.Join(" ", parts);
    }

    /// <summary>Обратный отсчёт для панели: 9:41 или 1:05:12.</summary>
    public static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    public static string When(TimerEntry e, DateTime now)
    {
        string time = $"{e.Due:H:mm}";
        if (e.IsRecurring)
        {
            string at = $"{e.Hour}:{e.Minute:00}";
            return e.Repeat switch
            {
                TimerRepeat.Daily => $"каждый день в {at}",
                TimerRepeat.Weekdays => $"по будням в {at}",
                TimerRepeat.Weekends => $"по выходным в {at}",
                _ => $"{string.Join(", ", e.Days.Select(DayShort))} в {at}",
            };
        }
        if (e.Due.Date == now.Date) return $"в {time}";
        if (e.Due.Date == now.Date.AddDays(1)) return $"завтра в {time}";
        return $"{e.Due:d MMMM} в {time}";
    }

    public static string DayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "пн", DayOfWeek.Tuesday => "вт", DayOfWeek.Wednesday => "ср", DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт", DayOfWeek.Saturday => "сб", _ => "вс",
    };

    public static string Plural(int n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        if (n is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
