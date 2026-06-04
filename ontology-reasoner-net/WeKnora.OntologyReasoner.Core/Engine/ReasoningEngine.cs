using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query.Inference;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ReasoningEngine
{
    private const int GrowthLimit = 100;

    public IGraph Reason(IGraph dataGraph, IGraph schemaGraph, string n3Rules, int maxIterations = 10)
    {
        var working = new Graph();

        // RDFS reasoner handles rdfs:subClassOf → rdf:type propagation
        // Apply it over the combined data + schema graph, then merge results into working
        var rdfsReasoner = new StaticRdfsReasoner();
        rdfsReasoner.Initialise(schemaGraph);

        var rdfsInput = new Graph();
        rdfsInput.Merge(dataGraph);
        rdfsInput.Merge(schemaGraph);
        rdfsReasoner.Apply(rdfsInput);
        working.Merge(rdfsInput);

        // N3 reasoners handle custom forward rules (transitive, symmetric, inverseOf, etc.).
        // dotNetRDF 3.3 applies a single implication reliably; initialize one reasoner per
        // generated rule line so multiple micro-TBox properties all participate.
        var n3Reasoners = BuildN3Reasoners(n3Rules);
        if (n3Reasoners.Count > 0)
        {
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var prevCount = working.Triples.Count;
                foreach (var n3Reasoner in n3Reasoners)
                {
                    n3Reasoner.Apply(working);
                }

                if (working.Triples.Count - prevCount == 0)
                {
                    break;
                }

                // Growth guard: single iteration added >100x previous count
                if (working.Triples.Count > prevCount * GrowthLimit)
                {
                    break;
                }
            }
        }

        return working;
    }

    private static List<SimpleN3RulesReasoner> BuildN3Reasoners(string n3Rules)
    {
        if (string.IsNullOrWhiteSpace(n3Rules))
        {
            return [];
        }

        var reasoners = new List<SimpleN3RulesReasoner>();
        foreach (var rule in n3Rules.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            var rulesGraph = new Graph();
            rulesGraph.LoadFromString(rule, new Notation3Parser());
            var reasoner = new SimpleN3RulesReasoner();
            reasoner.Initialise(rulesGraph);
            reasoners.Add(reasoner);
        }

        return reasoners;
    }
}
