using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static partial class StackPilotCli
{
private sealed record ActionResult(bool Success, string Message);
    private sealed record ComposeResult(int ExitCode, string Output, string Error);
}
