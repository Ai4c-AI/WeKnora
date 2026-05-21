using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Generators;

public class RdfGeneratorTests
{
    [Fact]
    public void GenerateSchema_WithSubClassOf_CreatesSubClassTriple()
    {
        var tbox = new MicroTBox
        {
            Classes =
            [
                new ClassDecl { Id = "Montague", Label = "Montague", SubClassOf = "Family", Evidence = "test" },
                new ClassDecl { Id = "Family", Label = "Family", SubClassOf = "Organization", Evidence = "test" },
            ],
            Properties = [],
        };

        var graph = RdfGenerator.GenerateSchema([tbox], "http://weknora.io/ontology/");

        Assert.True(graph.Triples.Count > 0);
        var rdfsSubClassOf = graph.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#subClassOf"));
        var montague = graph.CreateUriNode(new Uri("http://weknora.io/ontology/Montague"));
        var family = graph.CreateUriNode(new Uri("http://weknora.io/ontology/Family"));
        Assert.Contains(graph.Triples, t =>
            t.Subject.Equals(montague) &&
            t.Predicate.Equals(rdfsSubClassOf) &&
            t.Object.Equals(family));
    }

    [Fact]
    public void GenerateSchema_WithDomainRange_CreatesTriples()
    {
        var tbox = new MicroTBox
        {
            Classes = [],
            Properties =
            [
                new PropertyDecl
                {
                    Id = "wrote", Label = "wrote", Domain = "Person", Range = "Work",
                    Characteristics = [], Evidence = "test"
                },
            ],
        };

        var graph = RdfGenerator.GenerateSchema([tbox], "http://weknora.io/ontology/");

        var rdfsDomain = graph.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#domain"));
        var wrote = graph.CreateUriNode(new Uri("http://weknora.io/ontology/wrote"));
        Assert.Contains(graph.Triples, t =>
            t.Subject.Equals(wrote) && t.Predicate.Equals(rdfsDomain));
    }
}
