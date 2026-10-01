using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using Xunit;

namespace AskAny.Tests;

// 历史文件每次新增都是「读全量 → 插入 → 写全量」，所以条数与单条长度都必须封顶。
public class HistoryServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "askany-history-test-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static HistoryEntry Entry(string prompt, string response)
    {
        return new HistoryEntry
        {
            Prompt = prompt,
            Response = response,
            Reasoning = string.Empty,
            ModeName = "回答",
            ProviderName = "P",
            Model = "m"
        };
    }

    [Fact]
    public async Task EntriesAreStoredNewestFirst()
    {
        var service = new HistoryService(_path);

        await service.AddAsync(Entry("第一问", "第一答"));
        await service.AddAsync(Entry("第二问", "第二答"));

        var entries = await service.LoadAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("第二问", entries[0].Prompt);
        Assert.Equal("第一问", entries[1].Prompt);
    }

    [Fact]
    public async Task LongPromptIsTruncated()
    {
        var service = new HistoryService(_path);
        await service.AddAsync(Entry(new string('p', 10_000), "答"));

        var stored = (await service.LoadAsync()).Single();

        Assert.True(stored.Prompt.Length < 10_000);
        Assert.Contains("已截断", stored.Prompt);
    }

    [Fact]
    public async Task LongResponseIsTruncated()
    {
        var service = new HistoryService(_path);
        await service.AddAsync(Entry("问", new string('r', 50_000)));

        var stored = (await service.LoadAsync()).Single();

        Assert.True(stored.Response.Length < 50_000);
        Assert.Contains("已截断", stored.Response);
    }

    [Fact]
    public async Task ShortContentIsLeftAlone()
    {
        var service = new HistoryService(_path);
        await service.AddAsync(Entry("短问", "短答"));

        var stored = (await service.LoadAsync()).Single();

        Assert.Equal("短问", stored.Prompt);
        Assert.Equal("短答", stored.Response);
        Assert.DoesNotContain("已截断", stored.Response);
    }

    // 上限 200 条，超出后丢最旧的。
    [Fact]
    public async Task EntryCountIsCapped()
    {
        var service = new HistoryService(_path);

        for (var index = 0; index < 205; index++)
        {
            await service.AddAsync(Entry($"问{index}", "答"));
        }

        var entries = await service.LoadAsync();

        Assert.Equal(200, entries.Count);
        Assert.Equal("问204", entries[0].Prompt);
        Assert.Equal("问5", entries[^1].Prompt);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheTargetEntry()
    {
        var service = new HistoryService(_path);
        await service.AddAsync(Entry("甲", "答"));
        await service.AddAsync(Entry("乙", "答"));

        var target = (await service.LoadAsync()).First(entry => entry.Prompt == "甲");
        await service.DeleteAsync(target.Id);

        var remaining = await service.LoadAsync();
        Assert.Single(remaining);
        Assert.Equal("乙", remaining[0].Prompt);
    }

    [Fact]
    public async Task MissingFileLoadsAsEmpty()
    {
        var entries = await new HistoryService(
            Path.Combine(Path.GetTempPath(), "askany-does-not-exist-" + Guid.NewGuid().ToString("N") + ".json"))
            .LoadAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task CorruptFileLoadsAsEmptyInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(_path, "{ this is not a json array");

        var entries = await new HistoryService(_path).LoadAsync();

        Assert.Empty(entries);
    }
}
