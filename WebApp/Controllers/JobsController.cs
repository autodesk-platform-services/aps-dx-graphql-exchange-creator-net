using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    // Characters Revit does not allow in view names
    private static readonly Regex InvalidViewNameCharacters = new(@"[\\:{}\[\]|;<>?`~]");
    private readonly APS _aps;
    private readonly ExchangePipeline _pipeline;

    public JobsController(APS aps, ExchangePipeline pipeline)
    {
        _aps = aps;
        _pipeline = pipeline;
    }

    [HttpPost()]
    public async Task<ActionResult> CreateJob([FromBody] CreateJobRequest request)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (string.IsNullOrEmpty(request.HubId) || string.IsNullOrEmpty(request.ProjectId) || string.IsNullOrEmpty(request.VersionId))
        {
            return BadRequest("Missing hub, project, or version ID.");
        }
        if (string.IsNullOrWhiteSpace(request.ViewName) || InvalidViewNameCharacters.IsMatch(request.ViewName))
        {
            return BadRequest(@"The view name must not be empty, nor contain any of \ : { } [ ] | ; < > ? ` ~");
        }
        if (string.IsNullOrWhiteSpace(request.ExchangeName))
        {
            return BadRequest("The exchange name must not be empty.");
        }
        if (request.ExternalIds == null || !request.ExternalIds.Any(id => !string.IsNullOrEmpty(id)))
        {
            return BadRequest("The selection set is empty.");
        }
        if (request.SaveMode != "newFile" && request.SaveMode != "newVersion")
        {
            return BadRequest("The save mode must be \"newFile\" or \"newVersion\".");
        }
        request.ExternalIds = request.ExternalIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        var job = _pipeline.CreateJob(request);
        // Don't wait for the first steps, which can take a while (e.g., when the app bundle gets uploaded), so that the
        // extension shows the progress right away. Advance runs synchronously until its first network call, so the job
        // is already marked as in progress here; status requests meanwhile return the current state (the job is locked).
        _ = _pipeline.Advance(job, tokens);
        return Accepted(job);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetJob(string id)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var job = _pipeline.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }
        await _pipeline.Advance(job, tokens);
        return Ok(job);
    }
}
