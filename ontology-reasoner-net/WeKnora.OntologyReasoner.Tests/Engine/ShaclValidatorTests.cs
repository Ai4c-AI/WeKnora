using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Engine;
using WeKnora.OntologyReasoner.Core.Generators;
using WeKnora.OntologyReasoner.Core.Models;
using Xunit;

namespace WeKnora.OntologyReasoner.Tests.Engine;

public class ShaclValidatorTests
{
    private const string Ns = "http://weknora.io/ontology/";

    [Fact]
    public void Validate_WhenDataConforms_ReturnsOk()
    {
        var dataGraph = CreatePersonGraph(includeName: true);
        var shapesGraph = CreateRequiredNameShape();

        var response = new ShaclValidator().Validate(dataGraph, shapesGraph);

        Assert.Equal("ok", response.Status);
        Assert.True((bool)response.Results[0]["conforms"]!);
    }

    [Fact]
    public void Validate_WhenDataViolatesShape_ReturnsViolations()
    {
        var dataGraph = CreatePersonGraph(includeName: false);
        var shapesGraph = CreateRequiredNameShape();

        var response = new ShaclValidator().Validate(dataGraph, shapesGraph);

        Assert.Equal("violations", response.Status);
        Assert.NotEmpty(response.Results);
    }

    private static IGraph CreatePersonGraph(bool includeName)
    {
        var graph = new Graph();
        var romeo = graph.CreateUriNode(new Uri(Ns + "Romeo"));
        var person = graph.CreateUriNode(new Uri(Ns + "Person"));
        var rdfType = graph.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"));
        graph.Assert(romeo, rdfType, person);

        if (includeName)
        {
            var name = graph.CreateUriNode(new Uri(Ns + "name"));
            graph.Assert(romeo, name, graph.CreateLiteralNode("Romeo"));
        }

        return graph;
    }

    private static IGraph CreateRequiredNameShape()
    {
        var tbox = new MicroTBox
        {
            Shapes =
            [
                new ShapeDecl
                {
                    TargetClass = "Person",
                    Evidence = "test",
                    Constraints = [new ShapeConstraint { Property = "name", MinCount = 1 }]
                }
            ]
        };

        return ShaclGenerator.Generate([tbox], Ns);
    }
}
