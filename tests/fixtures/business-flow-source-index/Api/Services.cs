using System.Threading.Tasks;

namespace SharedFlow.Api;

public interface ITrackingClient
{
    Task RegisterAsync(string cargoId);
}

public sealed class TrackingClient : ITrackingClient
{
    public Task RegisterAsync(string cargoId) => Task.CompletedTask;
}
