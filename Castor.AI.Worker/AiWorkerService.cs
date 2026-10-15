using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Strategies;
using Grpc.Core;

namespace Castor.AI.Worker;

public sealed class AiWorkerService(
    WorkerState state,
    StrategyCatalog catalog,
    ILogger<AiWorkerService> logger) : AiWorker.AiWorkerBase
{
    public override Task<WorkerCapabilities> GetCapabilities(GetCapabilitiesRequest request, ServerCallContext context) =>
        Task.FromResult(catalog.Capabilities);

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) =>
        Task.FromResult(new HealthResponse
        {
            Status = state.Status,
            ActiveSessions = (uint)state.ActiveSessions,
            Detail = state.Detail,
        });

    public override async Task Session(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<WorkerMessage> responseStream,
        ServerCallContext context)
    {
        var callToken = context.CancellationToken;
        if (!await requestStream.MoveNext(callToken))
        {
            return;
        }

        if (requestStream.Current.BodyCase != ClientMessage.BodyOneofCase.Start)
        {
            await responseStream.WriteAsync(Refuse(SessionError.Types.Code.BadFormat, "First message must be SessionStart"));
            return;
        }

        var start = requestStream.Current.Start;
        if (Validate(start) is { } refusal)
        {
            logger.LogWarning("Session {SessionId} refused: {Reason}", start.SessionId, refusal.Error.Message);
            await responseStream.WriteAsync(refusal);
            return;
        }

        SessionMedia media;
        try
        {
            media = SessionMedia.Open(start.Transport);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Session {SessionId} refused: media transport failed: {Message}", start.SessionId, ex.Message);
            await responseStream.WriteAsync(Refuse(SessionError.Types.Code.TransportFailed, ex.Message));
            return;
        }

        state.SessionStarted();
        try
        {
            using (media)
            using (var strategy = catalog.Create())
            {
                var runner = new SessionRunner(start, strategy, media, catalog.Capabilities, logger);
                await runner.RunAsync(requestStream, responseStream, callToken);
            }
        }
        finally
        {
            state.SessionEnded();
        }
    }

    private WorkerMessage? Validate(SessionStart start)
    {
        if (state.Status != HealthResponse.Types.Status.Ready)
        {
            return Refuse(SessionError.Types.Code.Internal, "Worker not ready");
        }

        if (!catalog.Supports(start.Domain))
        {
            return Refuse(SessionError.Types.Code.UnsupportedDomain, $"Domain '{start.Domain}' not handled by this worker");
        }

        if (start.Cameras.Count == 0)
        {
            return Refuse(SessionError.Types.Code.BadFormat, "At least one camera is required");
        }

        if (start.Cameras.Count > catalog.Capabilities.MaxCameras)
        {
            return Refuse(SessionError.Types.Code.TooManyCameras,
                $"{start.Cameras.Count} cameras requested, max {catalog.Capabilities.MaxCameras}");
        }

        return null;
    }

    private static WorkerMessage Refuse(SessionError.Types.Code code, string message) => new()
    {
        Error = new SessionError { Code = code, Message = message, Fatal = true },
    };
}
