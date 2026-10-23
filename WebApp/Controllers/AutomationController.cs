using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AutomationController : ControllerBase
{
    private readonly APS _aps;

    public AutomationController(APS aps)
    {
        _aps = aps;
    }

    // Uploads the local app bundle as a new version and updates the activity; call after changing the Revit add-in.
    // (Exchange jobs set up Design Automation automatically, and re-publish the bundle when the local zip changes.)
    [HttpPost("setup")]
    public async Task<ActionResult> Setup()
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var activityIds = new List<string>();
        foreach (var revitVersion in await _aps.GetUsableRevitVersions())
        {
            activityIds.Add(await _aps.EnsureActivity(revitVersion, force: true));
        }
        return Ok(new { activityIds });
    }
}
