using Kugutsu.Contracts;

namespace Kugutsu.App;

/// <summary>Compiling, executing and confirming helper exit all belong to one queued request.</summary>
internal sealed class AloeBatchRunner(AloeCompilerAdapter compiler, AloeRuntimeAdapter runtime, OperationRequestQueue queue)
{
    internal QueuedOperation Prepare(string source, OperationExecutionContext context)
    {
        if (!KeyboardOperations.ValidText(source) || context.OwnerId == Guid.Empty)
        { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
        return new("aloe", async (ownerId, token) =>
        {
            if (ownerId != context.OwnerId) { throw new WindowReferenceException(ErrorCode.INVALID_ARGUMENT); }
            token.ThrowIfCancellationRequested();
            if (context.LockId is { } lockId) { queue.VerifySignalLock(ownerId, lockId); }
            await using var session = await compiler.CompileAsync(source, token);
            token.ThrowIfCancellationRequested();
            return await runtime.RunAsync(session, context, token);
        }, UseRequestDeadline: true);
    }
}
