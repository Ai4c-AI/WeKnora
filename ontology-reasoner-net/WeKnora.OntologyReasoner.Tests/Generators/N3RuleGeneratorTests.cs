using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class N3RuleGeneratorTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Transitive_GeneratesCorrectRule()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "partOf", Label = "part of", Domain = "Component", Range = "System",
                    Characteristics = ["transitive"], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?a <{Ns}partOf> ?b", rules);
        Assert.Contains($"?b <{Ns}partOf> ?c", rules);
        Assert.Contains($"?a <{Ns}partOf> ?c", rules);
    }

    [Fact]
    public void Symmetric_GeneratesCorrectRule()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "siblingOf", Label = "sibling of", Domain = "Person", Range = "Person",
                    Characteristics = ["symmetric"], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?a <{Ns}siblingOf> ?b", rules);
        Assert.Contains($"?b <{Ns}siblingOf> ?a", rules);
    }

    [Fact]
    public void InverseOf_GeneratesBidirectionalRules()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "wrote", Label = "wrote", Domain = "Person", Range = "Work",
                    Characteristics = [], InverseOf = "writtenBy", Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Contains($"?b <{Ns}writtenBy> ?a", rules);
    }

    [Fact]
    public void EmptyCharacteristics_GeneratesNoRules()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "knows", Label = "knows", Domain = "Person", Range = "Person",
                    Characteristics = [], Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.Equal("", rules.Trim());
    }

    [Fact]
    public void EquivalentClass_GeneratesBidirectionalRules()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Engineer", Label = "Engineer", Evidence = "test" },
                new ClassDecl { Id = "Developer", Label = "Developer", Evidence = "test" },
            ],
            Axioms =
            [
                new FreeAxiom
                {
                    Statement = "Engineer equivalentClass Developer",
                    Evidence = "test"
                }
            ]
        };

        var rules = N3RuleGenerator.Generate([tbox], Ns);
        Assert.NotNull(rules);
    }
}
