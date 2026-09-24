using AskAny.Models;

namespace AskAny.Services;

public sealed class HistoryService
{
    private const int MaximumEntries = 200;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HistoryService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AskAny");
        Directory.CreateDirectory(directory);
        _historyPath = Path.Combine(directory, "history.json");
    }

    public async Task<List<HistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            try
            {
                await using var stream = File.OpenRead(_historyPath);
                return await JsonSerializer.DeserializeAsync<List<HistoryEntry>>(
                           stream,
                           JsonDefaults.Options,
                           cancellationToken) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadWithoutLockAsync(cancellationToken);
            entries.Insert(0, entry);
            if (entries.Count > MaximumEntries)
            {
                entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
            }

            await WriteWithoutLockAsync(entries, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadWithoutLockAsync(cancellationToken);
            entries.RemoveAll(entry => entry.Id == entryId);
            await WriteWithoutLockAsync(entries, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteWithoutLockAsync([], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<HistoryEntry>> ReadWithoutLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_historyPath);
            return await JsonSerializer.DeserializeAsync<List<HistoryEntry>>(
                       stream,
                       JsonDefaults.Options,
                       cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteWithoutLockAsync(
        List<HistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var temporaryPath = _historyPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                entries,
                JsonDefaults.Options,
                cancellationToken);
        }

        File.Move(temporaryPath, _historyPath, true);
    }
}
