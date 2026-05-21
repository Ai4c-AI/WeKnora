using WeKnora.OntologyReasoner.Core.Aligner;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Aligner;

public class CanonicalAlignerTests
{
    [Fact]
    public void Align_ReplacesAliasWithCanonical()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Engineer"] = "SoftwareEngineer",
            ["engineer"] = "SoftwareEngineer",
        };

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Engineer", Label = "Engineer", Evidence = "test" },
            ],
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);

        Assert.Equal("SoftwareEngineer", aligned.Classes[0].Id);
    }

    [Fact]
    public void Align_UsesLocalAliasesFallback()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Dev", Label = "Developer", Evidence = "test" },
            ],
            Aliases = new Dictionary<string, List<string>>
            {
                ["Developer"] = ["Dev", "Engineer"]
            },
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);
        Assert.Equal("Developer", aligned.Classes[0].Id);
    }

    [Fact]
    public void Align_PreservesOriginalWhenNoMatch()
    {
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "UniqueClass", Label = "Unique", Evidence = "test" },
            ],
            Properties = [],
        };

        var aligned = CanonicalAligner.Align(tbox, canonicalMap);
        Assert.Equal("UniqueClass", aligned.Classes[0].Id);
    }
}
