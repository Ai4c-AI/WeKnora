using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class ShaclGenerator
{
    private static readonly Uri ShTargetClass = new("http://www.w3.org/ns/shacl#targetClass");
    private static readonly Uri ShProperty = new("http://www.w3.org/ns/shacl#property");
    private static readonly Uri ShPath = new("http://www.w3.org/ns/shacl#path");
    private static readonly Uri ShMinCount = new("http://www.w3.org/ns/shacl#minCount");
    private static readonly Uri ShMaxCount = new("http://www.w3.org/ns/shacl#maxCount");
    private static readonly Uri ShNodeShape = new("http://www.w3.org/ns/shacl#NodeShape");
    private static readonly Uri ShNot = new("http://www.w3.org/ns/shacl#not");
    private static readonly Uri ShClass = new("http://www.w3.org/ns/shacl#class");
    private static readonly Uri RdfType = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
    private static readonly Uri XsdInteger = new("http://www.w3.org/2001/XMLSchema#integer");

    public static IGraph Generate(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("sh", new Uri("http://www.w3.org/ns/shacl#"));
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));
        graph.NamespaceMap.AddNamespace("xsd", new Uri("http://www.w3.org/2001/XMLSchema#"));
        var shapeCounter = 0;

        foreach (var tbox in tboxes)
        {
            foreach (var prop in tbox.Properties)
            {
                if (!prop.Characteristics.Contains("functional", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var shapeNode = graph.CreateBlankNode($"functionalShape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));
                if (!string.IsNullOrEmpty(prop.Domain))
                {
                    graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + prop.Domain)));
                }

                var propShape = graph.CreateBlankNode($"propertyShape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(ShProperty), propShape);
                graph.Assert(propShape, graph.CreateUriNode(ShPath), graph.CreateUriNode(new Uri(baseNs + prop.Id)));
                graph.Assert(propShape, graph.CreateUriNode(ShMaxCount), graph.CreateLiteralNode("1", XsdInteger));
            }

            foreach (var cls in tbox.Classes)
            {
                foreach (var disjointId in cls.DisjointWith)
                {
                    var shapeNode = graph.CreateBlankNode($"disjointShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));
                    graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + cls.Id)));

                    var notNode = graph.CreateBlankNode($"notShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(ShNot), notNode);
                    graph.Assert(notNode, graph.CreateUriNode(ShClass), graph.CreateUriNode(new Uri(baseNs + disjointId)));
                }
            }

            foreach (var shape in tbox.Shapes)
            {
                var shapeNode = graph.CreateBlankNode($"shape{shapeCounter++}");
                graph.Assert(shapeNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(ShNodeShape));
                graph.Assert(shapeNode, graph.CreateUriNode(ShTargetClass), graph.CreateUriNode(new Uri(baseNs + shape.TargetClass)));

                foreach (var constraint in shape.Constraints)
                {
                    var propShape = graph.CreateBlankNode($"constraintShape{shapeCounter++}");
                    graph.Assert(shapeNode, graph.CreateUriNode(ShProperty), propShape);
                    graph.Assert(propShape, graph.CreateUriNode(ShPath), graph.CreateUriNode(new Uri(baseNs + constraint.Property)));

                    if (constraint.MinCount.HasValue)
                    {
                        graph.Assert(propShape, graph.CreateUriNode(ShMinCount), graph.CreateLiteralNode(constraint.MinCount.Value.ToString(), XsdInteger));
                    }

                    if (constraint.MaxCount.HasValue)
                    {
                        graph.Assert(propShape, graph.CreateUriNode(ShMaxCount), graph.CreateLiteralNode(constraint.MaxCount.Value.ToString(), XsdInteger));
                    }
                }
            }
        }

        return graph;
    }
}
