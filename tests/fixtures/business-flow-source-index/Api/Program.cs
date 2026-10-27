// Static-analysis fixture. The API project references Shared.Contracts, but the test
// deliberately does not build project references before the scanner runs.
using MassTransit;
using MediatR;
using Shared.Contracts;
using SharedFlow.Api;

builder.Services.AddScoped<ITrackingClient, TrackingClient>();
app.MapPost("/api/cargo", async (RegisterCargoRequest request, ISender sender) =>
    await sender.Send(new RegisterCargoCommand(request.CargoId)));
