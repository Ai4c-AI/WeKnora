using VDS.RDF;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class RdfGenerator
{
    private static readonly Uri RdfsSubClassOf = new("http://www.w3.org/2000/01/rdf-schema#subClassOf");
    private static readonly Uri RdfsDomain = new("http://www.w3.org/2000/01/rdf-schema#domain");
    private static readonly Uri RdfsRange = new("http://www.w3.org/2000/01/rdf-schema#range");
    private static readonly Uri RdfType = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
    private static readonly Uri RdfsClass = new("http://www.w3.org/2000/01/rdf-schema#Class");
    private static readonly Uri RdfProperty = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#Property");

    public static IGraph GenerateSchema(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("rdfs", new Uri("http://www.w3.org/2000/01/rdf-schema#"));
        graph.NamespaceMap.AddNamespace("rdf", new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#"));
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));

        foreach (var tbox in tboxes)
        {
            foreach (var cls in tbox.Classes)
            {
                var classNode = graph.CreateUriNode(new Uri(baseNs + cls.Id));
                graph.Assert(classNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(RdfsClass));

                if (cls.SubClassOf is not null)
                {
                    graph.Assert(classNode, graph.CreateUriNode(RdfsSubClassOf), graph.CreateUriNode(new Uri(baseNs + cls.SubClassOf)));
                }
            }

            foreach (var prop in tbox.Properties)
            {
                var propNode = graph.CreateUriNode(new Uri(baseNs + prop.Id));
                graph.Assert(propNode, graph.CreateUriNode(RdfType), graph.CreateUriNode(RdfProperty));

                if (!string.IsNullOrEmpty(prop.Domain))
                {
                    graph.Assert(propNode, graph.CreateUriNode(RdfsDomain), graph.CreateUriNode(new Uri(baseNs + prop.Domain)));
                }

                if (!string.IsNullOrEmpty(prop.Range))
                {
                    graph.Assert(propNode, graph.CreateUriNode(RdfsRange), graph.CreateUriNode(new Uri(baseNs + prop.Range)));
                }
            }
        }

        return graph;
    }

    public static IGraph GenerateDataGraph(IReadOnlyList<TripleDto> facts, string baseNs)
    {
        var graph = new Graph();
        graph.NamespaceMap.AddNamespace("ont", new Uri(baseNs));

        foreach (var fact in facts)
        {
            var subject = graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.S)));
            var predicate = fact.P == "rdf:type"
                ? graph.CreateUriNode(RdfType)
                : graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.P)));
            var obj = graph.CreateUriNode(new Uri(baseNs + Uri.EscapeDataString(fact.O)));

            graph.Assert(subject, predicate, obj);
        }

        return graph;
    }
}
