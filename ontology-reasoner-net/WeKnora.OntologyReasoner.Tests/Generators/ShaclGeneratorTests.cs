using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class ShaclGeneratorTests
{
    private const string Ns = "http://weknora.io/ontology/";
    private static readonly Uri ShNs = new("http://www.w3.org/ns/shacl#");

    [Fact]
    public void FunctionalProperty_GeneratesMaxCount1()
    {
        var tbox = new MicroTBox
        {
            Properties =
            [
                new PropertyDecl
                {
                    Id = "father", Label = "father", Domain = "Person", Range = "Person",
                    Characteristics = ["functional"], Evidence = "test"
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        Assert.True(graph.Triples.Count > 0);

        var maxCount = graph.CreateUriNode(new Uri(ShNs + "maxCount"));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(maxCount) &&
            t.Object is ILiteralNode lit && lit.Value == "1");
    }

    [Fact]
    public void ShapeConstraint_GeneratesMinCount()
    {
        var tbox = new MicroTBox
        {
            Shapes =
            [
                new ShapeDecl
                {
                    TargetClass = "Person",
                    Evidence = "test",
                    Constraints =
                    [
                        new ShapeConstraint { Property = "name", MinCount = 1 }
                    ]
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        var minCount = graph.CreateUriNode(new Uri(ShNs + "minCount"));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(minCount) &&
            t.Object is ILiteralNode lit && lit.Value == "1");
    }

    [Fact]
    public void DisjointWith_GeneratesShNotShape()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl
                {
                    Id = "Montague", Label = "Montague",
                    DisjointWith = ["Capulet"], Evidence = "test"
                }
            ]
        };

        var graph = ShaclGenerator.Generate([tbox], Ns);
        var shNot = graph.CreateUriNode(new Uri(ShNs + "not"));
        var shClass = graph.CreateUriNode(new Uri(ShNs + "class"));
        Assert.Contains(graph.Triples, t => t.Predicate.Equals(shNot));
        Assert.Contains(graph.Triples, t =>
            t.Predicate.Equals(shClass) &&
            t.Object.Equals(graph.CreateUriNode(new Uri(Ns + "Capulet"))));
    }
}
