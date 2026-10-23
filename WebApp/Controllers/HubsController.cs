using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class HubsController : ControllerBase
{
    private readonly APS _aps;

    public HubsController(APS aps)
    {
        _aps = aps;
    }

    [HttpGet()]
    public async Task<ActionResult> ListHubs()
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(
            from hub in await _aps.GetHubs(tokens)
            select new { id = hub.Id, name = hub.Name }
        );
    }

    [HttpGet("{hub}/projects")]
    public async Task<ActionResult> ListProjects(string hub)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(
            from project in await _aps.GetProjects(hub, tokens)
            select new { id = project.Id, name = project.Name }
        );
    }

    [HttpGet("{hub}/projects/{project}/contents")]
    public async Task<ActionResult> ListItems(string hub, string project, [FromQuery] string folder_id)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (string.IsNullOrEmpty(folder_id))
        {
            return Ok(
                from folder in await _aps.GetTopFolders(hub, project, tokens)
                select new { id = folder.Id, name = folder.Name, folder = true }
            );
        }
        else
        {
            var contents = await _aps.GetFolderContents(project, folder_id, tokens);
            return Ok(
                from entry in contents
                orderby entry.IsFolder descending
                select new { id = entry.Id, name = entry.Name, folder = entry.IsFolder, type = entry.Type }
            );
        }
    }

    [HttpGet("{hub}/projects/{project}/contents/{item}/versions")]
    public async Task<ActionResult> ListVersions(string hub, string project, string item)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(
            from version in await _aps.GetVersions(project, item, tokens)
            select new { id = version.Id, name = version.Name, type = version.Type }
        );
    }
}
