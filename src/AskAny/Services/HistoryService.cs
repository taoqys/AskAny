using AskAny.Models;

namespace AskAny.Services;

public sealed class HistoryService
{
    private const int MaximumEntries = 200;

    // 历史文件每次新增都是「读全量 → 插入 → 写全量」，单条太长会让文件迅速膨胀
    // （实测一份 98 条的历史已有 559KB）。这里对单条长度设上限。
    private const int MaximumPromptLength = 4_000;
    private const int MaximumResponseLength = 20_000;
    private const string TruncationMarker = "\n\n…（内容过长，已截断）";

    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HistoryService()
        : this(null)
    {
    }

    // 传入路径时从该文件读写，便于测试；默认仍是 %APPDATA%\AskAny\history.json。
    public HistoryService(string? historyPath)
    {
        if (!string.IsNullOrWhiteSpace(historyPath))
        {
            _historyPath = historyPath;
            return;
        }

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
            entry.Prompt = Truncate(entry.Prompt, MaximumPromptLength);
            entry.Response = Truncate(entry.Response, MaximumResponseLength);
            entry.Reasoning = Truncate(entry.Reasoning, MaximumResponseLength);
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

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength] + TruncationMarker;
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
