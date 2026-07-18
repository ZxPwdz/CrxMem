using CrxMem.Core;
using Xunit;

namespace CrxMem.Tests;

public sealed class JsonTableSerializerTests
{
    private readonly JsonTableSerializer _serializer = new();

    [Fact]
    public void SerializeAndDeserialize_PreservesEntryState()
    {
        var table = new CheatTable
        {
            ProcessName = "SampleTarget",
            Entries =
            [
                new CheatEntry
                {
                    Address = "SampleTarget.exe+1234",
                    Description = "Health",
                    Type = "4 Bytes",
                    Frozen = true,
                    FrozenValue = "100",
                    Active = true,
                    ShowAsHex = true
                }
            ]
        };

        string json = _serializer.Serialize(table);
        CheatTable restored = _serializer.Deserialize(json);

        Assert.Equal(CheatTable.NativeFormat, restored.Format);
        Assert.Equal(CheatTable.CurrentVersion, restored.Version);
        Assert.Equal("SampleTarget", restored.ProcessName);
        CheatEntry entry = Assert.Single(restored.Entries);
        Assert.Equal("Health", entry.Description);
        Assert.True(entry.Frozen);
        Assert.True(entry.ShowAsHex);
    }

    [Fact]
    public void Deserialize_AcceptsLegacyCrxMemJsonWithoutFormatField()
    {
        const string legacyJson = """
            {
              "Version": "1.0",
              "ProcessName": "LegacyTarget",
              "Entries": [{ "Address": "00123456", "Description": "Legacy", "Type": "4 Bytes" }]
            }
            """;

        CheatTable restored = _serializer.Deserialize(legacyJson);

        Assert.Equal("1.0", restored.Version);
        Assert.Equal("LegacyTarget", restored.ProcessName);
        Assert.Single(restored.Entries);
    }

    [Fact]
    public void Deserialize_RejectsNonJsonInput()
    {
        Assert.Throws<InvalidDataException>(() => _serializer.Deserialize("<CheatTable />"));
    }
}
