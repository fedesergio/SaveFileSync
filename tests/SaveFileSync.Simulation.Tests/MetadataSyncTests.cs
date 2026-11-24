using SaveFileSync.Core.Model;

namespace SaveFileSync.Simulation.Tests;

/// <summary>Phase 4: the cover/official-name match travels in the unit definition, like every other shared setting.</summary>
public sealed class MetadataSyncTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly TestPc _pc;
    private readonly string _unitId;

    public MetadataSyncTests()
    {
        _pc = _h.CreatePc("desktop");
        _unitId = _pc.Service.AddUnitAsync("Test Game", [new LocationDefinition { Id = "saves" }], [], _pc.Bindings).Await(_pc);
    }

    public void Dispose() => _h.Dispose();

    private static UnitMetadata Match() => new()
    {
        SteamGridDbGameId = 4,
        CoverUrl = "https://cdn2.steamgriddb.com/thumb/c1.jpg",
        CoverAuthor = "Ann",
        IgdbId = 113112,
        OfficialName = "Test Game: Official",
    };

    [Fact]
    public void Metadata_IsStoredInTheDefinition_AndAnotherPcSeesIt()
    {
        Assert.True(_pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc));

        var other = _h.CreatePc("laptop");
        var seen = other.Remote.ReadUnitDefinition(_unitId)!.Metadata!;
        Assert.Equal("Test Game: Official", seen.OfficialName);
        Assert.Equal("https://cdn2.steamgriddb.com/thumb/c1.jpg", seen.CoverUrl);
        Assert.Equal("Ann", seen.CoverAuthor);
        Assert.Equal(113112, seen.IgdbId);
        Assert.False(seen.Confirmed);
    }

    [Fact]
    public void SavingTheSameMatchAgain_WritesNoNewRevision()
    {
        _pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc);
        var revision = _pc.Remote.ReadUnitDefinition(_unitId)!.Revision;

        Assert.False(_pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc));

        Assert.Equal(revision, _pc.Remote.ReadUnitDefinition(_unitId)!.Revision);
    }

    [Fact]
    public void OtherEdits_KeepTheMetadata_AndKeepVersions()
    {
        _pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc);
        _pc.Service.SetKeepVersionsAsync(_unitId, 3).Await(_pc);
        var renamed = _pc.Service.GetDefinition(_unitId)!;
        _pc.Service.UpdateUnitAsync(new UnitDefinition { UnitId = _unitId, Name = "Renamed", Locations = renamed.Locations }).Await(_pc);

        var now = _pc.Remote.ReadUnitDefinition(_unitId)!;
        Assert.Equal("Renamed", now.Name);
        Assert.Equal("Test Game: Official", now.Metadata!.OfficialName);
        Assert.Equal(3, now.EffectiveKeepVersions);
    }

    [Fact]
    public void ARemovedGame_GetsNoMetadata()
    {
        _pc.Service.RemoveUnitAsync(_unitId, fromAllPcs: true).Await(_pc);
        Assert.False(_pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc));
    }

    [Fact]
    public void AnOlderApp_ThatDoesNotKnowTheField_StillReadsTheDefinition()
    {
        _pc.Service.SetMetadataAsync(_unitId, Match()).Await(_pc);
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(_pc.Remote.ReadUnitDefinition(_unitId)!));
        Assert.Contains("\"metadata\"", json);

        // Documents are plain JSON with optional fields: removing the field gives a valid, metadata-less definition.
        var without = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        without.Remove("metadata");
        var parsed = Json.Deserialize<UnitDefinition>(without.ToJsonString())!;
        Assert.Null(parsed.Metadata);
        Assert.Equal("Test Game", parsed.Name);
    }
}
