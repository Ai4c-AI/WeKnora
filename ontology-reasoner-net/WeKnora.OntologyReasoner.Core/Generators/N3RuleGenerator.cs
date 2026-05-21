using System.Text;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Generators;

public static class N3RuleGenerator
{
    public static string Generate(IReadOnlyList<MicroTBox> tboxes, string baseNs)
    {
        var sb = new StringBuilder();

        foreach (var tbox in tboxes)
        {
            foreach (var prop in tbox.Properties)
            {
                var propUri = $"<{baseNs}{prop.Id}>";

                foreach (var characteristic in prop.Characteristics)
                {
                    switch (characteristic.ToLowerInvariant())
                    {
                        case "transitive":
                            sb.AppendLine($"{{ ?a {propUri} ?b . ?b {propUri} ?c }} => {{ ?a {propUri} ?c }} .");
                            break;
                        case "symmetric":
                            sb.AppendLine($"{{ ?a {propUri} ?b }} => {{ ?b {propUri} ?a }} .");
                            break;
                    }
                }

                if (prop.InverseOf is not null)
                {
                    var inverseUri = $"<{baseNs}{prop.InverseOf}>";
                    sb.AppendLine($"{{ ?a {propUri} ?b }} => {{ ?b {inverseUri} ?a }} .");
                    sb.AppendLine($"{{ ?a {inverseUri} ?b }} => {{ ?b {propUri} ?a }} .");
                }
            }
        }

        return sb.ToString();
    }
}
