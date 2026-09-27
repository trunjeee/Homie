using Windows.UI;

namespace Homie.Services;

public enum VoiceAction { On, Off, SetBrightness, Brighter, Dimmer, Color, White, Scenario, Query }

/// <summary>Что понято из фразы: действие, устройства и где.</summary>
public sealed record VoiceIntent(VoiceAction Action, IReadOnlyList<Device> Devices, string What, string Where)
{
    public double Value { get; init; }
    public Color Color { get; init; }
    public string ColorName { get; init; } = "";
    public int Kelvin { get; init; }
    public Scenario? Scenario { get; init; }
    /// <summary>Для вопросов: какое показание (temperature, humidity…).</summary>
    public string Property { get; init; } = "";
}

/// <summary>
/// Разбор голосовых команд как у колонок Яндекса: «выключи свет» — в комнате, где стоит ПК,
/// «включи свет в зале» — в зале, «выключи всё везде» — во всём доме.
/// Русские окончания отбрасываются сравнением по основе слова.
/// </summary>
public static class VoiceCommands
{
    /// <summary>Фраза не похожа ни на одну команду — такую можно отдать нейросети.</summary>
    public const string NotUnderstood = "Не понял команду";

    private static readonly string[] OffVerbs = ["выключ", "выруб", "погас", "отключ", "потуш", "туши", "выкл"];
    private static readonly string[] OnVerbs = ["включ", "вруб", "зажг", "зажеч", "вкл"];
    private static readonly string[] ScenarioWords = ["сценари", "запуст", "активир", "режим"];
    private static readonly string[] BrighterWords = ["ярче", "поярче", "светлее", "посветлее", "прибав", "увелич"];
    private static readonly string[] DimmerWords = ["темнее", "потемнее", "приглуш", "тусклее", "потусклее", "убав", "уменьш", "слабее"];
    private static readonly string[] Filler = ["и", "в", "во", "на", "с", "со", "по", "пожалуйста", "мне", "а", "ну", "сделай", "поставь", "установи"];

    private sealed record DeviceType(string What, string TypePrefix, string[] Stems);

    private static readonly DeviceType Light = new("свет", "devices.types.light", ["ламп", "люстр", "торшер", "ночник", "подсветк", "светильник", "лент", "бра"]);
    private static readonly DeviceType[] Types =
    [
        Light,
        new("розетки", "devices.types.socket", ["розетк"]),
        new("телевизор", "devices.types.media_device.tv", ["телевизор", "телик", "тв"]),
        new("кондиционер", "devices.types.thermostat.ac", ["кондиц", "кондер", "кондей"]),
        new("обогреватель", "devices.types.thermostat", ["обогрев"]),
        new("увлажнитель", "devices.types.humidifier", ["увлажн"]),
        new("очиститель", "devices.types.purifier", ["очистит"]),
        new("пылесос", "devices.types.vacuum_cleaner", ["пылесос"]),
        new("чайник", "devices.types.cooking.kettle", ["чайник"]),
    ];

    private static readonly (string Stem, string Name, Color Color)[] Colors =
    [
        ("красн", "красный", Color.FromArgb(255, 255, 0, 0)),
        ("оранж", "оранжевый", Color.FromArgb(255, 255, 120, 0)),
        ("желт", "жёлтый", Color.FromArgb(255, 255, 210, 0)),
        ("салат", "салатовый", Color.FromArgb(255, 140, 255, 0)),
        ("зелен", "зелёный", Color.FromArgb(255, 0, 255, 0)),
        ("бирюз", "бирюзовый", Color.FromArgb(255, 0, 255, 190)),
        ("голуб", "голубой", Color.FromArgb(255, 0, 180, 255)),
        ("син", "синий", Color.FromArgb(255, 0, 0, 255)),
        ("фиолет", "фиолетовый", Color.FromArgb(255, 140, 0, 255)),
        ("сирен", "сиреневый", Color.FromArgb(255, 170, 90, 255)),
        ("пурпур", "пурпурный", Color.FromArgb(255, 200, 0, 255)),
        ("розов", "розовый", Color.FromArgb(255, 255, 70, 170)),
        ("малин", "малиновый", Color.FromArgb(255, 255, 0, 110)),
    ];

    private static readonly (string Stem, string Name, int Kelvin)[] Whites =
    [
        ("тепл", "тёплый белый", 2700),
        ("холодн", "холодный белый", 6500),
        ("дневн", "дневной белый", 5000),
        ("нейтральн", "нейтральный белый", 4500),
        ("естествен", "нейтральный белый", 4500),
        ("бел", "белый", 4500),
    ];

    private static readonly (string[] Stems, string Property)[] Queries =
    [
        (["температур", "градус", "жарко", "холодно"], "temperature"),
        (["влажн"], "humidity"),
        (["углекисл", "co2", "со2"], "co2_level"),
        (["давлен"], "pressure"),
        (["мощност", "потребл"], "power"),
        (["заряд", "батаре"], "battery_level"),
    ];

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4,
        ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100, ["половину"] = 50,
    };

    public static VoiceIntent? Parse(string text, HomeData data, string? householdId, string? pcRoomId, out string error)
    {
        error = "";
        var words = VoiceCommands.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var used = new bool[words.Length];
        if (words.Length == 0) { error = "Пустая команда"; return null; }

        var devices = data.Devices.Where(d => householdId is null || d.HouseholdId is null || d.HouseholdId == householdId).ToList();
        var rooms = data.Rooms.Where(r => householdId is null || r.HouseholdId is null || r.HouseholdId == householdId).ToList();

        // ---------- где: «везде», названные комнаты или комната ПК ----------
        bool everywhere = false;
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] is "везде" or "всюду") { everywhere = used[i] = true; }
            else if (i + 1 < words.Length && words[i] is "весь" or "всем" or "всему" or "всей" or "во" && (words[i + 1].StartsWith("дом") || words[i + 1].StartsWith("квартир")))
            {
                everywhere = used[i] = used[i + 1] = true;
            }
        }

        var namedRooms = new List<Room>();
        foreach (var room in rooms)
        {
            var hits = MatchName(room.Name, words, used);
            if (hits is null) continue;
            namedRooms.Add(room);
            foreach (int i in hits) used[i] = true;
        }

        var pcRoom = rooms.FirstOrDefault(r => r.Id == pcRoomId);
        IReadOnlyList<Room>? scope = everywhere ? null : namedRooms.Count > 0 ? namedRooms : pcRoom is not null ? [pcRoom] : null;
        string where = everywhere ? "везде" : scope is null ? "во всём доме" : string.Join(", ", scope.Select(r => r.Name));
        bool InScope(Device d) => scope is null || scope.Any(r => r.Id == d.RoomId);

        // ---------- что сделать ----------
        bool Has(string[] stems) => words.Where((w, i) => !used[i]).Any(w => stems.Any(w.StartsWith));
        bool off = Has(OffVerbs), on = !off && Has(OnVerbs);
        bool scenarioWord = Has(ScenarioWords);

        // Сценарий: «запусти сценарий Ночь» или просто «включи ночь», если такого устройства нет.
        var scenario = BestScenario(data.Scenarios.Where(s => s.IsActive), words, used);
        if (scenario is not null && scenarioWord)
            return new VoiceIntent(VoiceAction.Scenario, [], scenario.Name, "") { Scenario = scenario };

        var query = Queries.FirstOrDefault(q => Has(q.Stems));
        if (query.Property is not null && !on && !off)
        {
            var sensors = devices.Where(d => d.Properties.Any(p => p.Instance == query.Property && p.Value is not null)).ToList();
            var local = sensors.Where(InScope).ToList();
            // Спросили без комнаты, а в комнате ПК датчика нет — отвечаем по всему дому.
            if (local.Count == 0 && namedRooms.Count == 0) { local = sensors; where = "во всём доме"; }
            if (local.Count == 0) { error = $"Нет датчиков: {where}"; return null; }
            return new VoiceIntent(VoiceAction.Query, local, query.Property, where) { Property = query.Property };
        }

        var color = Colors.FirstOrDefault(c => Has([c.Stem]));
        var white = color.Name is null ? Whites.FirstOrDefault(w => Has([w.Stem])) : default;
        int? number = ParseNumber(words, used);
        bool brighter = Has(BrighterWords), dimmer = Has(DimmerWords);
        bool maxWord = Has(["максим", "полную"]), minWord = Has(["миним"]);
        bool brightnessWord = Has(["ярк", "процент"]) || (number is not null && !off);

        VoiceAction? action =
            color.Name is not null ? VoiceAction.Color :
            white.Name is not null ? VoiceAction.White :
            brighter ? VoiceAction.Brighter :
            dimmer ? VoiceAction.Dimmer :
            maxWord || minWord || brightnessWord ? VoiceAction.SetBrightness :
            off ? VoiceAction.Off :
            on ? VoiceAction.On : null;

        // ---------- какие устройства ----------
        Func<Device, bool> capable = action switch
        {
            VoiceAction.SetBrightness or VoiceAction.Brighter or VoiceAction.Dimmer => d => d.Range("brightness") is not null,
            VoiceAction.Color => d => d.Color?.SupportsColor == true,
            VoiceAction.White => d => d.Color?.SupportsWhite == true,
            _ => d => d.OnOff is not null,
        };

        string what;
        var typeWords = words.Select((w, i) => (w, i)).Where(p => !used[p.i] && IsTypeWord(p.w)).Select(p => p.i).ToHashSet();
        bool allWord = words.Where((w, i) => !used[i]).Any(w => w is "все" or "всю" or "всего" or "всем");
        // «выключи все лампы» — это все лампы, а не устройство с именем «Лампа».
        var byName = allWord ? [] : BestDevices(devices.Where(d => !d.IsGroup), words, used, typeWords);
        var type = Types.FirstOrDefault(t => Has(t.Stems)) ?? (words.Any(IsLightWord) ? Light : null);
        bool all = type is null && allWord;

        List<Device> targets;
        if (byName.Count > 0 && byName.Any(InScope))
        {
            targets = byName.Where(InScope).ToList();
            what = targets[0].Name;
        }
        else if (byName.Count > 0 && namedRooms.Count == 0 && !everywhere)
        {
            targets = byName; // «включи торшер», а торшер в другой комнате — как у колонки, находим его там
            what = targets[0].Name;
            where = string.Join(", ", targets.Select(d => RoomName(d, rooms)).Distinct());
        }
        else if (scenario is not null && type is null && !all && byName.Count == 0)
        {
            return new VoiceIntent(VoiceAction.Scenario, [], scenario.Name, "") { Scenario = scenario }; // «включи кино»
        }
        else if (type is not null || all || (action is not null && byName.Count == 0))
        {
            // Тип не назван («выключи» в зале) — как колонки, считаем, что про свет.
            var t = all ? null : type ?? Light;
            targets = devices.Where(d => !d.IsGroup && InScope(d) && (t is null || d.Type.StartsWith(t.TypePrefix))).ToList();
            what = t?.What ?? "всё";
            if (targets.Count == 0 && scenario is not null)
                return new VoiceIntent(VoiceAction.Scenario, [], scenario.Name, "") { Scenario = scenario };
            if (targets.Count == 0)
            {
                // Нет такого в комнате ПК — может, это одно устройство где-то ещё («включи телевизор»).
                if (namedRooms.Count == 0 && !everywhere && t is not null)
                {
                    targets = devices.Where(d => !d.IsGroup && d.Type.StartsWith(t.TypePrefix)).ToList();
                    if (targets.Count is > 0 and <= 1) where = RoomName(targets[0], rooms);
                    else targets = [];
                }
                if (targets.Count == 0) { error = $"Не нашёл: {what} — {where}"; return null; }
            }
        }
        else if (byName.Count > 0)
        {
            targets = byName; // устройство названо по имени, но в другой комнате
            what = targets[0].Name;
            where = string.Join(", ", targets.Select(d => RoomName(d, rooms)).Distinct());
        }
        else if (scenario is not null)
        {
            return new VoiceIntent(VoiceAction.Scenario, [], scenario.Name, "") { Scenario = scenario };
        }
        else
        {
            error = NotUnderstood;
            return null;
        }

        if (action is null)
        {
            // Назвали только устройство — переключаем.
            action = targets.Any(d => d.OnOff?.OnValue == true) ? VoiceAction.Off : VoiceAction.On;
        }

        var able = targets.Where(capable).ToList();
        if (able.Count == 0)
        {
            error = action switch
            {
                VoiceAction.Color => $"{Cap(what)} не умеет менять цвет",
                VoiceAction.White => $"{Cap(what)} не умеет оттенки белого",
                VoiceAction.SetBrightness or VoiceAction.Brighter or VoiceAction.Dimmer => $"{Cap(what)} не умеет менять яркость",
                _ => $"{Cap(what)} нельзя включить или выключить",
            };
            return null;
        }

        return new VoiceIntent(action.Value, able, what, where)
        {
            Value = maxWord ? 100 : minWord ? 1 : number ?? 0,
            Color = color.Color,
            ColorName = color.Name ?? white.Name ?? "",
            Kelvin = white.Kelvin,
        };
    }

    // ---------- сравнение слов ----------

    /// <summary>Нижний регистр, «ё» → «е», без знаков препинания и лишних пробелов.</summary>
    public static string Normalize(string text)
    {
        var chars = text.ToLowerInvariant().Replace('ё', 'е').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Одно слово в разных падежах: «зал» = «зале», «кухня» = «кухне», «лампа» = «лампу».</summary>
    public static bool SameWord(string word, string target)
    {
        if (word == target) return true;
        int min = Math.Min(word.Length, target.Length);
        if (min < 3) return false;
        int common = 0;
        while (common < min && word[common] == target[common]) common++;
        return common >= Math.Max(3, Math.Max(word.Length, target.Length) - 3) && common >= min - 2;
    }

    private static string[] Significant(string name)
    {
        var w = VoiceCommands.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = w.Where(x => x.Length >= 3 && !Filler.Contains(x)).ToArray();
        return s.Length > 0 ? s : w;
    }

    /// <summary>Индексы слов фразы, совпавших со всеми значимыми словами названия, или null.</summary>
    private static List<int>? MatchName(string name, string[] words, bool[] used, bool allowFirstWord = true)
    {
        var parts = Significant(name);
        var hits = new List<int>();
        foreach (var part in parts)
        {
            int i = -1;
            for (int k = 0; k < words.Length; k++)
                if (!used[k] && !hits.Contains(k) && SameWord(words[k], part)) { i = k; break; }
            if (i >= 0) hits.Add(i);
            else if (!allowFirstWord || hits.Count == 0) return null; // первое слово названия обязательно
        }
        return hits.Count > 0 ? hits : null;
    }

    private static List<Device> BestDevices(IEnumerable<Device> devices, string[] words, bool[] used, HashSet<int> typeWords)
    {
        int best = 0;
        var result = new List<Device>();
        foreach (var d in devices)
        {
            var parts = Significant(d.Name);
            var hits = MatchName(d.Name, words, used);
            if (hits is null) continue;
            bool full = hits.Count == parts.Length;
            // «свет»/«лампа» сами по себе — это тип, а не имя: неполное совпадение по ним не считаем.
            if (!full && hits.All(typeWords.Contains)) continue;
            int score = (full ? 100 : 0) + hits.Count;
            if (score > best) { best = score; result.Clear(); }
            if (score == best) result.Add(d);
        }
        return result;
    }

    private static Scenario? BestScenario(IEnumerable<Scenario> scenarios, string[] words, bool[] used)
    {
        Scenario? best = null;
        int bestScore = 0;
        foreach (var s in scenarios)
        {
            var parts = Significant(s.Name);
            var hits = MatchName(s.Name, words, used, allowFirstWord: false);
            if (hits is null || hits.Count != parts.Length) continue;
            if (hits.Count > bestScore) { bestScore = hits.Count; best = s; }
        }
        return best;
    }

    private static bool IsLightWord(string w) => w is "свет" or "света" or "свету" or "светом" || Light.Stems.Any(w.StartsWith);
    private static bool IsTypeWord(string w) => IsLightWord(w) || Types.Any(t => t.Stems.Any(w.StartsWith));

    private static int? ParseNumber(string[] words, bool[] used)
    {
        for (int i = 0; i < words.Length; i++)
        {
            if (used[i]) continue;
            if (int.TryParse(words[i], out int digits)) return digits;
            if (!Numbers.TryGetValue(words[i], out int n)) continue;
            // «пятьдесят пять»
            if (n is >= 20 and < 100 && n % 10 == 0 && i + 1 < words.Length && Numbers.TryGetValue(words[i + 1], out int ones) && ones < 10)
                n += ones;
            return n;
        }
        return null;
    }

    private static string RoomName(Device d, IReadOnlyList<Room> rooms) =>
        rooms.FirstOrDefault(r => r.Id == d.RoomId)?.Name ?? "без комнаты";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
