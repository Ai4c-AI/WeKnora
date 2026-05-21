using VDS.RDF;
using VDS.RDF.Shacl;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Core.Engine;

public class ShaclValidator
{
    public ReasonResponse Validate(IGraph dataGraph, IGraph shapesGraph)
    {
        var report = new ShapesGraph(shapesGraph).Validate(dataGraph);
        if (report.Conforms)
        {
            return new ReasonResponse
            {
                Status = "ok",
                DataSource = "provided",
                Results = [new Dictionary<string, object?> { ["conforms"] = true }],
            };
        }

        return new ReasonResponse
        {
            Status = "violations",
            DataSource = "provided",
            Results = report.Results.Select(result => new Dictionary<string, object?>
            {
                ["focusNode"] = result.FocusNode?.ToString(),
                ["resultPath"] = result.ResultPath?.ToString(),
                ["message"] = result.Message?.ToString(),
                ["severity"] = result.Severity?.ToString(),
            }).ToList(),
        };
    }
}
