using WeKnora.OntologyReasoner.Core.Assembly;
using WeKnora.OntologyReasoner.Core.Models;

namespace WeKnora.OntologyReasoner.Api.Endpoints;

public static class ReasonEndpoint
{
    public static async Task<IResult> Handle(ReasonRequest request, SliceAssembler assembler)
    {
        try
        {
            return Results.Ok(await assembler.Reason(request));
        }
        catch (Exception ex)
        {
            return Results.Ok(new ReasonResponse
            {
                Status = "error",
                DataSource = "none",
                Warnings = [$"Internal error: {ex.Message}"],
            });
        }
    }
}
