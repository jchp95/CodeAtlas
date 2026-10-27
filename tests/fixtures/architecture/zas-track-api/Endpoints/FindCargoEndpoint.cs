// Syntax-only fixture: FastEndpoints package restore/runtime is intentionally not required.
using System.Threading;
using System.Threading.Tasks;
using FastEndpoints;

namespace Zas.Track.Endpoints;

public sealed record FindCargoRequest(string CargoId);
public sealed record FindCargoResponse(string CargoId);

public sealed class FindCargoEndpoint : Endpoint<FindCargoRequest, FindCargoResponse>
{
    public override void Configure()
    {
        Get("/api/cargos/{cargoId}");
        AllowAnonymous();
    }

    public override Task HandleAsync(FindCargoRequest request, CancellationToken cancellationToken)
        => Send.OkAsync(new FindCargoResponse(request.CargoId), cancellationToken);
}
