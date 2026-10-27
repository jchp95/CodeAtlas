using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace Zas.Express.Services;

public interface ITrackingClient
{
    Task RegisterAsync(string cargoId);
}

public sealed class TrackingClient(HttpClient httpClient) : ITrackingClient
{
    public Task RegisterAsync(string cargoId) => Task.CompletedTask;

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/tracking", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
