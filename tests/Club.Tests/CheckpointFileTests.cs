using System.Text;
using Club.Protocol;
using Xunit;

namespace Club.Tests;

public sealed class CheckpointFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "club-checkpoint-" + Guid.NewGuid().ToString("N"));
    public CheckpointFileTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string PathName => Path.Combine(root, "session.json");
    private void Save(string value) => CheckpointFile.Write(PathName, stream => stream.Write(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void ReplacesRepeatedlyWhileReaderKeepsOldCheckpointOpen()
    {
        Save("original");
        using var old = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        for (var i = 0; i < 50; i++)
        {
            Save("revision-" + i);
            Assert.Equal("revision-" + i, File.ReadAllText(PathName));
        }
        using var reader = new StreamReader(old);
        Assert.Equal("original", reader.ReadToEnd());
        Assert.False(File.Exists(PathName + ".tmp"));
    }

    [Fact]
    public void FailedSerializationPreservesLastCompleteCheckpoint()
    {
        Save("previous");
        Assert.Throws<IOException>(() => CheckpointFile.Write(PathName, stream =>
        {
            stream.Write("partial"u8);
            throw new IOException("injected failure");
        }));
        Assert.Equal("previous", File.ReadAllText(PathName));
        Assert.False(File.Exists(PathName + ".tmp"));
    }
}
