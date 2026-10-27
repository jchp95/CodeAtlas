using System.Threading.Tasks;

namespace Zas.Track.Projection;

public sealed class CargoProjection
{
    public Task ApplyAsync(string cargoId) => Task.CompletedTask;
}
