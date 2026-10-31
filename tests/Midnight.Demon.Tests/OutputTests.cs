using System.Text;
using Midnight.Demon.Features.Servers.Output;

namespace Midnight.Demon.Tests;

public class LineSplitterTests
{
    [Fact]
    public void SplitsLinesAcrossChunks_AndRemovesCarriageReturns()
    {
        var splitter = new LineSplitter();
        var lines = new List<string>();

        splitter.Append("hello wo"u8, lines);
        Assert.Empty(lines);

        splitter.Append("rld\r\nsecond\n\nthi"u8, lines);
        Assert.Equal(["hello world", "second", ""], lines);

        lines.Clear();
        splitter.Flush(lines);
        Assert.Equal(["thi"], lines);
    }

    [Fact]
    public void NeverCutsMultiByteCharacters()
    {
        var bytes = Encoding.UTF8.GetBytes("grüße 🌙\n");
        var splitter = new LineSplitter();
        var lines = new List<string>();

        // one byte at a time splits every multi byte character
        foreach (var b in bytes)
            splitter.Append([b], lines);

        Assert.Equal(["grüße 🌙"], lines);
    }

    [Fact]
    public void LongLinesAreEmittedInChunks()
    {
        var splitter = new LineSplitter();
        var lines = new List<string>();

        splitter.Append(Encoding.ASCII.GetBytes(new string('a', LineSplitter.MaxLineBytes + 10) + "\n"), lines);

        Assert.Equal(2, lines.Count);
        Assert.Equal(LineSplitter.MaxLineBytes, lines[0].Length);
        Assert.Equal(10, lines[1].Length);
    }
}

public class ConsoleBufferTests
{
    [Fact]
    public void KeepsTheLastLines_OldestFirst()
    {
        var buffer = new ConsoleBuffer(3);

        for (var i = 1; i <= 5; i++)
            buffer.Add($"line {i}");

        Assert.Equal(["line 3", "line 4", "line 5"], buffer.GetLines(10));
        Assert.Equal(["line 4", "line 5"], buffer.GetLines(2));
        Assert.Empty(buffer.GetLines(0));
    }

    [Fact]
    public void PartiallyFilled()
    {
        var buffer = new ConsoleBuffer(150);
        buffer.Add("a");
        buffer.Add("b");

        Assert.Equal(["a", "b"], buffer.GetLines(150));

        buffer.Clear();
        Assert.Empty(buffer.GetLines(150));
    }
}

public class StartupDetectorTests
{
    [Fact]
    public void MatchesContainedText_IgnoringAnsiColors()
    {
        List<string> patterns = [")! For help, type ", "Server started"];

        Assert.True(StartupDetector.IsDone("[12:00:00 INFO]: Done (3.2s)! For help, type \"help\"", patterns));
        Assert.True(StartupDetector.IsDone("\u001b[32mServer\u001b[0m started", patterns));
        Assert.False(StartupDetector.IsDone("server started", patterns));
        Assert.False(StartupDetector.IsDone("Loading world", patterns));
    }

    [Fact]
    public void EmptyPatterns_MeanOnlineRightAway()
    {
        Assert.False(StartupDetector.HasPatterns([]));
        Assert.False(StartupDetector.HasPatterns(["", ""]));
        Assert.False(StartupDetector.HasPatterns(null));
        Assert.False(StartupDetector.IsDone("anything", []));
        Assert.True(StartupDetector.HasPatterns(["Done"]));
    }
}
