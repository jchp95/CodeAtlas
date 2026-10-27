using System.Threading.Tasks;

namespace MediatR
{
    public interface ISender
    {
        Task Send<TRequest>(TRequest request);
    }

    public interface IRequestHandler<in TRequest>
    {
        Task Handle(TRequest request);
    }
}

namespace MassTransit
{
    public interface IPublishEndpoint
    {
        Task Publish<TMessage>(TMessage message);
    }
}
