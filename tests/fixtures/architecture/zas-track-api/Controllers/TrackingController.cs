// Fixture for static architecture analysis. It is not intended to be compiled or run.
[ApiController]
[Route("api/tracking")]
public sealed class TrackingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
