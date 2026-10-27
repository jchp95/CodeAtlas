using System.Threading.Tasks;
using MassTransit;
using MediatR;
using Zas.Contracts;
using Zas.Express.Services;

namespace Zas.Express.Handlers;

public sealed record RegisterCargoCommand(string CargoId);
public sealed record RegisterCargoRequest(string CargoId);

public sealed class RegisterCargoCommandHandler(
    ITrackingClient trackingClient,
    IPublishEndpoint publishEndpoint) : IRequestHandler<RegisterCargoCommand>
{
    public async Task Handle(RegisterCargoCommand request)
    {
        await trackingClient.RegisterAsync(request.CargoId);
        await publishEndpoint.Publish(new CargoRegistered(request.CargoId));
    }
}
