namespace Homie.Services;

public enum TimerAction { SetTimer, SetReminder, List, Remaining, Cancel, CancelAll }

/// <summary>Что понято: поставить таймер/напоминание, показать, отменить.</summary>
public sealed record TimerIntent(TimerAction Action)
{
    public TimerEntry? Entry { get; init; }
    /// <summary>Для отмены — о чём («пицца»), если назвали.</summary>
    public string Query { get; init; } = "";
    /// <summary>Не хватило данных («напомни позвонить маме» — а когда?).</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Разбор фраз про таймеры и напоминания (слова, как их выдаёт распознавание: «через полчаса»,
/// «в шесть вечера», «завтра в девять тридцать», «по будням в восемь», «каждую пятницу в десять»).
/// </summary>
public static class TimerCommands
{
    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["одного"] = 1, ["два"] = 2, ["две"] = 2, ["двух"] = 2,
        ["три"] = 3, ["трёх"] = 3, ["трех"] = 3, ["четыре"] = 4, ["четырёх"] = 4, ["четырех"] = 4, ["пять"] = 5, ["пяти"] = 5,
        ["шесть"] = 6, ["шести"] = 6, ["семь"] = 7, ["семи"] = 7, ["восемь"] = 8, ["восьми"] = 8, ["девять"] = 9, ["девяти"] = 9,
        ["десять"] = 10, ["десяти"] = 10, ["одиннадцать"] = 11, ["одиннадцати"] = 11, ["двенадцать"] = 12, ["двенадцати"] = 12,
        ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17,
        ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["двадцати"] = 20, ["тридцать"] = 30,
        ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["девяносто"] = 90, ["сто"] = 100,
        ["пару"] = 2, ["пара"] = 2, ["полтора"] = -15, ["полторы"] = -15, // −15 = «полтора» (×1.5)
    };

    private static readonly string[] SetVerbs = ["поставь", "заведи", "установи", "включи", "запусти", "сделай", "засеки", "засечь"];
    private static readonly string[] CancelVerbs = ["отмени", "отменить", "удали", "удалить", "убери", "сбрось", "выключи", "останови", "отключи"];
    private static readonly string[] Filler =
        ["мне", "пожалуйста", "на", "через", "в", "во", "к", "про", "о", "об", "что", "чтобы", "для", "и", "а", "ровно", "таймер", "таймера", "напомни", "напомнить", "напоминание"];

    private static readonly (string Stem, DayOfWeek Day)[] Weekdays =
    [
        ("понедельн", DayOfWeek.Monday), ("вторн", DayOfWeek.Tuesday), ("сред", DayOfWeek.Wednesday),
        ("четверг", DayOfWeek.Thursday), ("пятниц", DayOfWeek.Friday), ("суббот", DayOfWeek.Saturday), ("воскресен", DayOfWeek.Sunday),
    ];

    /// <summary>Разобрать фразу; null — это не про таймеры (пусть разбирают другие).</summary>
    public static TimerIntent? Parse(string text, DateTime now)
    {
        var w = VoiceCommands.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return null;
        bool Has(params string[] stems) => w.Any(x => stems.Any(x.StartsWith));

        bool aboutTimer = Has("таймер", "засек", "засечь");
        bool aboutReminder = Has("напомин", "напомни", "напомнить");
        // Отмена — повелительное «отмени/выключи» перед словом «таймер/напоминание» («напомни выключить плиту» — не отмена).
        int subject = Array.FindIndex(w, x => x.StartsWith("таймер") || x.StartsWith("напомин"));
        bool cancel = subject > 0 && w.Take(subject).Any(x => CancelVerbs.Contains(x));

        // «Сколько осталось», «какие таймеры», «что у меня напомнить»
        if (!cancel && Has("сколько") && Has("остал")) return new TimerIntent(TimerAction.Remaining);
        if (!cancel && (aboutTimer || aboutReminder) && Has("какие", "какой", "покажи", "список", "все", "мои"))
            return new TimerIntent(TimerAction.List);

        if (!aboutTimer && !aboutReminder) return null;

        if (cancel)
        {
            if (Has("все", "всё")) return new TimerIntent(TimerAction.CancelAll);
            var rest = w.Where(x => !CancelVerbs.Contains(x) && !Filler.Contains(x) && !x.StartsWith("таймер") && !x.StartsWith("напомин"));
            return new TimerIntent(TimerAction.Cancel) { Query = string.Join(' ', rest) };
        }

        var used = new bool[w.Length];
        for (int i = 0; i < w.Length; i++)
            if (SetVerbs.Contains(w[i]) || w[i].StartsWith("таймер") || w[i].StartsWith("напомин") || w[i] is "напомни" or "напомнить" or "засеки" or "засечь")
                used[i] = true;

        if (aboutTimer && !aboutReminder)
        {
            var duration = FindDuration(w, used);
            if (duration is null) return new TimerIntent(TimerAction.SetTimer) { Error = "На сколько поставить таймер?" };
            return new TimerIntent(TimerAction.SetTimer)
            {
                Entry = new TimerEntry
                {
                    Kind = TimerKind.Timer, Label = Label(w, used),
                    DurationSeconds = (int)duration.Value.TotalSeconds, Due = now + duration.Value,
                },
            };
        }

        // ---------- напоминание ----------
        var entry = new TimerEntry { Kind = TimerKind.Reminder };

        // «через 20 минут»
        int through = Array.FindIndex(w, x => x == "через");
        if (through >= 0)
        {
            used[through] = true;
            var after = FindDuration(w, used, through + 1);
            if (after is null) return new TimerIntent(TimerAction.SetReminder) { Error = "Через сколько напомнить?" };
            entry.Due = now + after.Value;
            entry.Label = Label(w, used);
            return Done(entry);
        }

        // Повтор: «каждый день», «ежедневно», «по будням», «по выходным», «по пятницам», «каждый понедельник».
        for (int i = 0; i < w.Length; i++)
        {
            string x = w[i];
            if (x is "ежедневно") { entry.Repeat = TimerRepeat.Daily; used[i] = true; }
            else if (x.StartsWith("будн") || x.StartsWith("рабоч")) { entry.Repeat = TimerRepeat.Weekdays; used[i] = true; MarkPrev(i, "по", "в"); }
            else if (x.StartsWith("выходн")) { entry.Repeat = TimerRepeat.Weekends; used[i] = true; MarkPrev(i, "по", "в"); }
            else if (x.StartsWith("кажд"))
            {
                used[i] = true;
                if (i + 1 < w.Length && (w[i + 1].StartsWith("дн") || w[i + 1].StartsWith("ден") || w[i + 1].StartsWith("утр") || w[i + 1].StartsWith("вечер")))
                {
                    entry.Repeat = TimerRepeat.Daily;
                    used[i + 1] = true;
                }
            }
        }
        // дни недели: «в пятницу» — один раз, «по пятницам» / «каждую пятницу» — каждую неделю
        var days = new List<DayOfWeek>();
        bool weekly = false;
        for (int i = 0; i < w.Length; i++)
        {
            var day = Weekdays.FirstOrDefault(d => w[i].StartsWith(d.Stem));
            if (day.Stem is null) continue;
            used[i] = true;
            days.Add(day.Day);
            string prev = i > 0 ? w[i - 1] : "";
            if (prev.StartsWith("кажд") || prev == "по" || w[i].EndsWith("ам") || w[i].EndsWith("ям")) weekly = true;
            if (prev is "в" or "во" or "по" or "и") used[i - 1] = true;
        }
        if (days.Count > 0 && (weekly || entry.Repeat == TimerRepeat.None && w.Any(x => x.StartsWith("кажд"))))
        {
            entry.Repeat = TimerRepeat.Weekly;
            entry.Days = days.Distinct().ToList();
        }

        // Время: «в 18:30», «в шесть вечера», «в девять тридцать», «в полдень»
        var time = FindTime(w, used);
        for (int i = 0; i < w.Length; i++) if (w[i] is "утром" or "вечером" or "днём" or "днем") used[i] = true; // уточнение, а не текст
        int dayShift = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (w[i] == "сегодня") { used[i] = true; }
            else if (w[i] == "завтра") { dayShift = 1; used[i] = true; }
            else if (w[i] == "послезавтра") { dayShift = 2; used[i] = true; }
        }
        // «завтра утром» без времени — 9:00; «вечером» — 19:00
        if (time is null && (Has("утр") || Has("вечер") || dayShift > 0 || days.Count > 0 || entry.IsRecurring))
        {
            for (int i = 0; i < w.Length; i++) if (w[i].StartsWith("утр") || w[i].StartsWith("вечер")) used[i] = true;
            time = Has("вечер") ? (19, 0) : Has("утр") || dayShift > 0 || days.Count > 0 || entry.IsRecurring ? (9, 0) : null;
        }
        if (time is null) return new TimerIntent(TimerAction.SetReminder) { Error = "Когда напомнить? Например: «через 20 минут» или «в 18:30»" };

        entry.Hour = time.Value.Hour;
        entry.Minute = time.Value.Minute;
        entry.Label = Label(w, used);

        if (entry.IsRecurring)
        {
            entry.Due = entry.NextAfter(now);
            return Done(entry);
        }

        DateTime due;
        if (days.Count > 0)
        {
            // ближайший названный день недели (сегодня — если время ещё не прошло)
            // «в пятницу в восемь» в пятницу днём — сегодня в 20:00
            var today = now.Date.AddHours(entry.Hour).AddMinutes(entry.Minute);
            if (days.Contains(now.DayOfWeek) && today <= now && entry.Hour < 12 && today.AddHours(12) > now && !Has("утр"))
            {
                entry.Hour += 12;
                due = today.AddHours(12);
            }
            else
            {
                due = Enumerable.Range(0, 8).Select(d => now.Date.AddDays(d).AddHours(entry.Hour).AddMinutes(entry.Minute))
                    .First(t => t > now && days.Contains(t.DayOfWeek));
            }
        }
        else
        {
            due = now.Date.AddDays(dayShift).AddHours(entry.Hour).AddMinutes(entry.Minute);
            if (dayShift == 0 && due <= now)
            {
                // «в 6» в 17:00 — значит 18:00; иначе — завтра
                if (entry.Hour < 12 && due.AddHours(12) > now && !Has("утр")) due = due.AddHours(12);
                else due = due.AddDays(1);
            }
        }
        entry.Due = due;
        return Done(entry);

        void MarkPrev(int i, params string[] words)
        {
            if (i > 0 && words.Contains(w[i - 1])) used[i - 1] = true;
        }
        static TimerIntent Done(TimerEntry e) => new(TimerAction.SetReminder) { Entry = e };
    }

    /// <summary>«10 минут», «полчаса», «час двадцать», «полтора часа», «2 минуты 30 секунд», «минуту».</summary>
    private static TimeSpan? FindDuration(string[] w, bool[] used, int from = 0)
    {
        var total = TimeSpan.Zero;
        bool found = false;
        int? pending = null; // число, ждущее единицу измерения
        int pendingAt = -1;
        for (int i = from; i < w.Length; i++)
        {
            if (used[i] && pending is null) continue;
            string x = w[i];
            if (x is "полчаса" or "пол" && (x == "полчаса" || (i + 1 < w.Length && w[i + 1].StartsWith("час"))))
            {
                total += TimeSpan.FromMinutes(30);
                used[i] = true;
                if (x == "пол") used[++i] = true;
                found = true;
                continue;
            }
            if (TryNumber(w, i, out int n, out int len))
            {
                pending = n;
                pendingAt = i;
                for (int k = 0; k < len; k++) used[i + k] = true;
                i += len - 1;
                continue;
            }
            TimeSpan unit = x.StartsWith("секунд") ? TimeSpan.FromSeconds(1)
                : x.StartsWith("минут") || x == "мин" ? TimeSpan.FromMinutes(1)
                : x.StartsWith("час") ? TimeSpan.FromHours(1)
                : TimeSpan.Zero;
            if (unit == TimeSpan.Zero)
            {
                if (pending is not null && found) break; // «час двадцать» — хвост без единицы
                continue;
            }
            used[i] = true;
            double count = pending switch { null => 1, -15 => 1.5, _ => pending.Value };
            total += unit * count;
            pending = null;
            found = true;
        }
        // «час двадцать» → 20 минут после часов
        if (pending is > 0 && found && total.TotalHours >= 1 && total.Minutes == 0) { total += TimeSpan.FromMinutes(pending.Value); found = true; }
        else if (pending is not null && !found && pendingAt >= 0) used[pendingAt] = false;
        return found && total > TimeSpan.Zero ? total : null;
    }

    /// <summary>Время дня после «в/во/к»: «в 18 30», «в шесть вечера», «в девять», «в полдень».</summary>
    private static (int Hour, int Minute)? FindTime(string[] w, bool[] used)
    {
        for (int i = 0; i < w.Length; i++)
        {
            if (w[i] is "полдень" or "полдня") { used[i] = true; MarkIn(i); return (12, 0); }
            if (w[i] is "полночь") { used[i] = true; MarkIn(i); return (0, 0); }
            if (w[i] is not ("в" or "во" or "к" or "на")) continue;
            int j = i + 1;
            // «в 18:30» после Normalize — «18 30»
            if (j >= w.Length || !TryNumber(w, j, out int hour, out int len) || hour is < 0 or > 24) continue;
            if (w[i] == "на" && !(j + len < w.Length && w[j + len].StartsWith("час"))) continue; // «на 10 минут» — это не время
            int k = j + len;
            int minute = 0;
            if (k < w.Length && w[k].StartsWith("час")) k++;
            if (k < w.Length && TryNumber(w, k, out int m, out int mlen) && m is >= 0 and < 60)
            {
                minute = m;
                k += mlen;
                if (k < w.Length && w[k].StartsWith("минут")) k++;
            }
            if (k < w.Length && (w[k].StartsWith("вечер") || w[k] == "дня" || w[k].StartsWith("ночи")))
            {
                if (hour < 12 && !w[k].StartsWith("ночи")) hour += 12;
                if (w[k].StartsWith("ночи") && hour == 12) hour = 0;
                k++;
            }
            else if (k < w.Length && w[k].StartsWith("утр")) k++;
            if (k - j == len && hour > 24) continue;
            for (int x = i; x < k; x++) used[x] = true;
            return (hour % 24, minute);
        }
        return null;

        void MarkIn(int i) { if (i > 0 && w[i - 1] is "в" or "во" or "к") used[i - 1] = true; }
    }

    /// <summary>Число словами или цифрами: «двадцать пять», «18», «одну».</summary>
    private static bool TryNumber(string[] w, int i, out int value, out int length)
    {
        value = 0;
        length = 0;
        if (int.TryParse(w[i], out value)) { length = 1; return true; }
        if (!Numbers.TryGetValue(w[i], out value)) return false;
        length = 1;
        if (value is >= 20 and < 100 && value % 10 == 0 && i + 1 < w.Length && Numbers.TryGetValue(w[i + 1], out int ones) && ones is > 0 and < 10)
        {
            value += ones;
            length = 2;
        }
        return true;
    }

    /// <summary>О чём таймер/напоминание: всё, что не время и не служебные слова.</summary>
    private static string Label(string[] w, bool[] used)
    {
        var words = w.Where((x, i) => !used[i]).ToList();
        while (words.Count > 0 && Filler.Contains(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && Filler.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }
}
