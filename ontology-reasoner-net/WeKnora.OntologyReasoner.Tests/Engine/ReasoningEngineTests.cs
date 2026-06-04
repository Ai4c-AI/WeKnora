using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Engine;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Engine;

public class ReasoningEngineTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Reason_TransitiveRule_InfersIndirectRelation()
    {
        var dataGraph = new Graph();
        var a = dataGraph.CreateUriNode(new Uri(Ns + "A"));
        var b = dataGraph.CreateUriNode(new Uri(Ns + "B"));
        var c = dataGraph.CreateUriNode(new Uri(Ns + "C"));
        var partOf = dataGraph.CreateUriNode(new Uri(Ns + "partOf"));
        dataGraph.Assert(a, partOf, b);
        dataGraph.Assert(b, partOf, c);

        var schemaGraph = new Graph();
        var n3Rules = $"{{ ?x <{Ns}partOf> ?y . ?y <{Ns}partOf> ?z }} => {{ ?x <{Ns}partOf> ?z }} .";

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, n3Rules);

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(a) &&
            t.Predicate.Equals(partOf) &&
            t.Object.Equals(c));
    }

    [Fact]
    public void Reason_MultipleTransitiveRules_InfersIndirectRelationsForEachPredicate()
    {
        var dataGraph = new Graph();
        var alice = dataGraph.CreateUriNode(new Uri(Ns + "alice"));
        var bob = dataGraph.CreateUriNode(new Uri(Ns + "bob"));
        var carol = dataGraph.CreateUriNode(new Uri(Ns + "carol"));
        var wheel = dataGraph.CreateUriNode(new Uri(Ns + "wheel"));
        var car = dataGraph.CreateUriNode(new Uri(Ns + "car"));
        var fleet = dataGraph.CreateUriNode(new Uri(Ns + "fleet"));
        var ancestorOf = dataGraph.CreateUriNode(new Uri(Ns + "ancestorOf"));
        var partOf = dataGraph.CreateUriNode(new Uri(Ns + "partOf"));
        dataGraph.Assert(alice, ancestorOf, bob);
        dataGraph.Assert(bob, ancestorOf, carol);
        dataGraph.Assert(wheel, partOf, car);
        dataGraph.Assert(car, partOf, fleet);

        var schemaGraph = new Graph();
        var n3Rules = $"{{ ?a <{Ns}ancestorOf> ?b . ?b <{Ns}ancestorOf> ?c }} => {{ ?a <{Ns}ancestorOf> ?c }} .\n" +
            $"{{ ?a <{Ns}partOf> ?b . ?b <{Ns}partOf> ?c }} => {{ ?a <{Ns}partOf> ?c }} .";

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, n3Rules);

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(alice) &&
            t.Predicate.Equals(ancestorOf) &&
            t.Object.Equals(carol));
        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(wheel) &&
            t.Predicate.Equals(partOf) &&
            t.Object.Equals(fleet));
    }

    [Fact]
    public void Reason_EmptyRules_ReturnsOriginalTriples()
    {
        var dataGraph = new Graph();
        var a = dataGraph.CreateUriNode(new Uri(Ns + "A"));
        var b = dataGraph.CreateUriNode(new Uri(Ns + "B"));
        var knows = dataGraph.CreateUriNode(new Uri(Ns + "knows"));
        dataGraph.Assert(a, knows, b);

        var schemaGraph = new Graph();

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "");

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(a) && t.Predicate.Equals(knows) && t.Object.Equals(b));
    }

    [Fact]
    public void Reason_RdfsSubClassOf_InfersTypeHierarchy()
    {
        var dataGraph = new Graph();
        var alice = dataGraph.CreateUriNode(new Uri(Ns + "alice"));
        var rdfType = dataGraph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"));
        var softwareEngineer = dataGraph.CreateUriNode(new Uri(Ns + "SoftwareEngineer"));
        dataGraph.Assert(alice, rdfType, softwareEngineer);

        var schemaGraph = new Graph();
        var engineer = schemaGraph.CreateUriNode(new Uri(Ns + "Engineer"));
        var rdfsSubClassOf = schemaGraph.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#subClassOf"));
        schemaGraph.Assert(softwareEngineer, rdfsSubClassOf, engineer);

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "");

        Assert.Contains(result.Triples, t =>
            t.Subject.Equals(alice) &&
            t.Predicate.Equals(rdfType) &&
            t.Object.Equals(engineer));
    }

    [Fact]
    public void Reason_RespectsMaxIterations()
    {
        var dataGraph = new Graph();
        var schemaGraph = new Graph();

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "", maxIterations: 1);

        Assert.NotNull(result);
    }
}
