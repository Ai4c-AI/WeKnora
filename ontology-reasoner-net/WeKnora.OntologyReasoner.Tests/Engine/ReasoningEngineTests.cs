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
    public void Reason_RespectsMaxIterations()
    {
        var dataGraph = new Graph();
        var schemaGraph = new Graph();

        var engine = new ReasoningEngine();
        var result = engine.Reason(dataGraph, schemaGraph, "", maxIterations: 1);

        Assert.NotNull(result);
    }
}
