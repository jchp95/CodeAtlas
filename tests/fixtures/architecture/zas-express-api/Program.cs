// Fixture for static architecture analysis. It is not intended to be compiled or run.
using MediatR;
using MassTransit;
using Zas.Contracts;
using Zas.Express.Handlers;
using Zas.Express.Services;

builder.Services.AddScoped<ITrackingClient, TrackingClient>();
builder.Services.AddScoped<IRequestHandler<RegisterCargoCommand>, RegisterCargoCommandHandler>();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/cargo", async (RegisterCargoRequest request, ISender sender) =>
    await sender.Send(new RegisterCargoCommand(request.CargoId)));
var trackingUrl = "https://zas-track-api:7182/api/tracking";
await httpClient.GetAsync(trackingUrl);

public sealed class CargoQueryHandler(ITrackingClient trackingClient)
{
    public ITrackingClient TrackingClient { get; } = trackingClient;
}

// Endpoint-discovery regressions: cross-file constants, nested MapGroup and MapMethods.
var apiV1 = app.MapGroup(ApiRoutes.V1);
var cargoGroup = apiV1.MapGroup(ApiRoutes.Cargo);
cargoGroup.MapGet(ApiRoutes.ById, (string cargoId) => Results.Ok(cargoId));
cargoGroup.MapMethods("/status", new[] { "PATCH" }, () => Results.Ok());

// A dynamic prefix must not produce a misleading partial route.
var dynamicGroup = app.MapGroup(configuration["RoutePrefix"]);
dynamicGroup.MapGet("/not-a-real-static-route", () => Results.Ok());
