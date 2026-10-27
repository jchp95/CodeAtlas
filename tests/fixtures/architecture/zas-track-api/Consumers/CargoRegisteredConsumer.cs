// Fixture for cross-service message-flow tracing. It is not intended to be compiled or run.
using System.Threading.Tasks;
using MassTransit;
using Zas.Contracts;
using Zas.Track.Projection;

namespace Zas.Track.Consumers;

public sealed class CargoRegisteredConsumer(CargoProjection projection) : IConsumer<CargoRegistered>
{
    public Task Consume(ConsumeContext<CargoRegistered> context) => projection.ApplyAsync(context.Message.CargoId);
}
