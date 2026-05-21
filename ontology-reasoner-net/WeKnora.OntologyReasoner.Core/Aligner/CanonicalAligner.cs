using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Aligner;

public static class CanonicalAligner
{
    public static MicroTBox Align(MicroTBox tbox, IReadOnlyDictionary<string, string> canonicalMap)
    {
        var localAliasMap = BuildLocalAliasMap(tbox.Aliases);

        string Resolve(string id) =>
            canonicalMap.TryGetValue(id, out var canonical) ? canonical :
            localAliasMap.TryGetValue(id, out var localCanonical) ? localCanonical :
            id;

        return tbox with
        {
            Classes = tbox.Classes.Select(c => c with
            {
                Id = Resolve(c.Id),
                SubClassOf = c.SubClassOf is not null ? Resolve(c.SubClassOf) : null,
                DisjointWith = c.DisjointWith.Select(Resolve).ToList(),
            }).ToList(),
            Properties = tbox.Properties.Select(p => p with
            {
                Id = Resolve(p.Id),
                Domain = Resolve(p.Domain),
                Range = Resolve(p.Range),
                InverseOf = p.InverseOf is not null ? Resolve(p.InverseOf) : null,
            }).ToList(),
            Shapes = tbox.Shapes.Select(s => s with
            {
                TargetClass = Resolve(s.TargetClass),
                Constraints = s.Constraints.Select(c => c with { Property = Resolve(c.Property) }).ToList(),
            }).ToList(),
        };
    }

    private static Dictionary<string, string> BuildLocalAliasMap(IReadOnlyDictionary<string, List<string>> aliases)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliasList) in aliases)
        {
            foreach (var alias in aliasList)
            {
                map.TryAdd(alias, canonical);
            }
        }

        return map;
    }
}
