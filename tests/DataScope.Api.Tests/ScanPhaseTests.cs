using DataScope.Api.Endpoints;

namespace DataScope.Api.Tests;

public class ScanPhaseTests
{
    [Theory]
    [InlineData(null, "walk")]
    [InlineData("", "walk")]
    [InlineData("""{"phase":"walk"}""", "walk")]
    [InlineData("""{"after": "0199", "phase": "classify"}""", "classify")]
    [InlineData("""{"phase":"classify","after":null}""", "classify")]
    [InlineData("""{"phase":"other"}""", "walk")]
    [InlineData("not json", "walk")]
    [InlineData("""["classify"]""", "walk")]
    public void Reads_the_phase_from_the_checkpoint(string? checkpoint, string expected) =>
        Assert.Equal(expected, ScanJobEndpoints.Phase(checkpoint));
}
