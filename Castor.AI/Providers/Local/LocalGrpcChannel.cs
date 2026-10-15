using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;

namespace Castor.AI.Providers.Local;

/// <summary>
/// gRPC channel to a worker listening on a local named pipe.
/// </summary>
internal static class LocalGrpcChannel
{
    private const int ConnectTimeoutMs = 1000;

    public static GrpcChannel ForNamedPipe(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    impersonationLevel: TokenImpersonationLevel.Anonymous);
                try
                {
                    await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };

        // The host part is ignored: every connection goes through the pipe.
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }
}
