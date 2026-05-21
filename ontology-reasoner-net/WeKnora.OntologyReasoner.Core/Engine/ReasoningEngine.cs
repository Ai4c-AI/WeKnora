using System.Text.RegularExpressions;
using VDS.RDF;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ReasoningEngine
{
    private static readonly Regex TransitiveRulePattern = new(
        @"\?x\s+<(?<predicate>[^>]+)>\s+\?y\s*\.\s*\?y\s+<\k<predicate>>\s+\?z[\s\S]*\?x\s+<\k<predicate>>\s+\?z",
        RegexOptions.Compiled);

    public IGraph Reason(IGraph dataGraph, IGraph schemaGraph, string n3Rules, int maxIterations = 10)
    {
        var working = new Graph();
        working.Merge(dataGraph);
        working.Merge(schemaGraph);

        var match = TransitiveRulePattern.Match(n3Rules);
        if (!match.Success)
        {
            return working;
        }

        var predicate = working.CreateUriNode(new Uri(match.Groups["predicate"].Value));
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var inferred = InferTransitiveTriples(working, predicate).ToList();
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
