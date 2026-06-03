using System.Text.RegularExpressions;
using VDS.RDF;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ReasoningEngine
{
    private static readonly Regex TransitiveRulePattern = new(
        @"\?\w+\s+<(?<predicate>[^>]+)>\s+\?\w+\s*\.\s*\?\w+\s+<\k<predicate>>\s+\?\w+[\s\S]*\?\w+\s+<\k<predicate>>\s+\?\w+",
        RegexOptions.Compiled);

    public IGraph Reason(IGraph dataGraph, IGraph schemaGraph, string n3Rules, int maxIterations = 10)
    {
        var working = new Graph();
        working.Merge(dataGraph);
        working.Merge(schemaGraph);

        var predicates = TransitiveRulePattern.Matches(n3Rules)
            .Select(match => match.Groups["predicate"].Value)
            .Distinct(StringComparer.Ordinal)
            .Select(predicate => working.CreateUriNode(new Uri(predicate)))
            .ToList();
        if (predicates.Count == 0)
        {
            return working;
        }

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var inferred = predicates.SelectMany(predicate => InferTransitiveTriples(working, predicate)).ToList();
            var added = false;
            foreach (var triple in inferred)
            {
                added |= working.Assert(triple);
            }

            if (!added)
            {
                break;
            }
        }

        return working;
    }

    private static IEnumerable<Triple> InferTransitiveTriples(IGraph graph, IUriNode predicate)
    {
        var triples = graph.Triples.WithPredicate(predicate).ToList();
        foreach (var left in triples)
        {
            foreach (var right in triples.Where(t => t.Subject.Equals(left.Object)))
            {
                yield return new Triple(left.Subject, predicate, right.Object);
            }
        }
    }
}
