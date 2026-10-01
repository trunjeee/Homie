using Microsoft.UI.Dispatching;

namespace Homie.Services;

/// <summary>
/// Таймеры и напоминания: хранит, считает, срабатывает. Повторяющиеся после срабатывания
/// переносятся на следующий раз; пропущенные (ПК был выключен) срабатывают сразу после запуска.
/// </summary>
public sealed class TimerService
{
    private readonly DispatcherQueueTimer _tick;
    private readonly List<TimerEntry> _items = TimerStore.Load();

    /// <summary>Список поменялся (поставили, отменили, сработало).</summary>
    public event Action? Changed;
    /// <summary>Сработало: запись и было ли пропущено (missed — время прошло, пока Homie не работал).</summary>
    public event Action<TimerEntry, bool>? Fired;

    public IReadOnlyList<TimerEntry> Items => _items.OrderBy(t => t.Due).ToList();
    public IReadOnlyList<TimerEntry> Timers => Items.Where(t => t.Kind == TimerKind.Timer).ToList();

    public TimerService(DispatcherQueue ui)
    {
        _tick = ui.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => Check(missed: false);
        _tick.Start();
        // Пропущенное, пока Homie не работал, — сразу после запуска (чуть позже, когда окна готовы).
        ui.TryEnqueue(DispatcherQueuePriority.Low, () => Check(missed: true));
    }

    public void Add(TimerEntry entry)
    {
        _items.Add(entry);
        Save();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0) Save();
    }

    public void RemoveAll(TimerKind? kind = null)
    {
        if (_items.RemoveAll(t => kind is null || t.Kind == kind) > 0) Save();
    }

    /// <summary>Отложить сработавшее: таймер/разовое — новой записью через N минут.</summary>
    public void Snooze(TimerEntry fired, int minutes)
    {
        Add(new TimerEntry
        {
            Kind = TimerKind.Reminder, Label = fired.Label,
            Due = DateTime.Now.AddMinutes(minutes),
        });
    }

    /// <summary>«+5 минут» к идущему таймеру.</summary>
    public void Extend(string id, int minutes)
    {
        var t = _items.FirstOrDefault(x => x.Id == id);
        if (t is null) return;
        t.Due = t.Due.AddMinutes(minutes);
        t.DurationSeconds += minutes * 60;
        Save();
    }

    /// <summary>Найти по словам («пицца», «позвонить маме»), иначе ближайший нужного вида.</summary>
    public TimerEntry? Find(string query, TimerKind? kind)
    {
        var pool = Items.Where(t => kind is null || t.Kind == kind).ToList();
        if (pool.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(query)) return pool[0];
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return pool.FirstOrDefault(t => words.Any(q => t.Label.Split(' ').Any(l => VoiceCommands.SameWord(l, q)))) ?? null;
    }

    private void Check(bool missed)
    {
        var now = DateTime.Now;
        var due = _items.Where(t => t.Due <= now).ToList();
        if (due.Count == 0) return;
        foreach (var t in due)
        {
            // Пропущено «давно» (больше минуты назад) — пометим, чтобы в окошке было видно.
            bool wasMissed = missed || now - t.Due > TimeSpan.FromMinutes(1);
            if (t.IsRecurring) t.Due = t.NextAfter(now);
            else _items.Remove(t);
            Fired?.Invoke(t, wasMissed);
        }
        Save();
    }

    private void Save()
    {
        TimerStore.Save(_items);
        Changed?.Invoke();
    }

    public void Stop() => _tick.Stop();
}
