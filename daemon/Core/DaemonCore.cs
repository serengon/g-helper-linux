using GHelper.Daemon.Contract;

namespace GHelper.Daemon.Core;

public readonly record struct CallerIdentity(string UniqueBusName, uint UnixUserId, uint UnixProcessId);

public interface ICallerIdentityResolver
{
    ValueTask<CallerIdentity> ResolveAsync(string uniqueBusName, CancellationToken cancellationToken);
}

public interface IAuthorizationService
{
    ValueTask<bool> IsAuthorizedAsync(
        CallerIdentity caller,
        string polkitAction,
        bool allowUserInteraction,
        CancellationToken cancellationToken);
}

public interface IMutationExecutor
{
    bool CanExecute(MutationDefinition mutation);
    bool TryQueue(MutationDefinition mutation);
}

public sealed class DaemonRequestException : Exception
{
    public string ErrorName { get; }

    public DaemonRequestException(string errorName, string message) : base(message)
        => ErrorName = errorName;
}

public sealed class DaemonCore
{
    private const int MaximumConcurrentMutations = 2;
    private readonly ICallerIdentityResolver _identityResolver;
    private readonly IAuthorizationService _authorization;
    private readonly bool _mutationExecutionEnabled;
    private readonly IMutationExecutor? _mutationExecutor;
    private readonly SemaphoreSlim _mutationSlots = new(MaximumConcurrentMutations, MaximumConcurrentMutations);

    public DaemonCore(
        ICallerIdentityResolver identityResolver,
        IAuthorizationService authorization,
        bool mutationExecutionEnabled = false,
        IMutationExecutor? mutationExecutor = null)
    {
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _mutationExecutionEnabled = mutationExecutionEnabled;
        _mutationExecutor = mutationExecutor;
    }

    public DaemonVersionInfo GetVersion()
        => new(DaemonContract.ApiVersion, DaemonContract.DaemonVersion);

    public string[] GetCapabilities()
        => (string[])DaemonContract.Capabilities.Clone();

    public DaemonStatus GetStatus()
        => new(DaemonContract.StatusState, DaemonContract.StatusDetail);

    public async ValueTask RequestMutationAsync(
        string uniqueBusName,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!DaemonContract.TryGetMutation(operation, out MutationDefinition mutation))
            throw new DaemonRequestException(
                DaemonContract.ErrorInvalidArguments,
                "The requested mutation name is invalid or is not in the versioned allowlist.");

        // A disabled boundary stops before sender validation, UID/PID resolution
        // or polkit so a rejected request cannot leave pending bus calls or auth
        // prompts. Production enables only the reviewed XG executor below.
        if (!_mutationExecutionEnabled)
            throw new DaemonRequestException(
                DaemonContract.ErrorUnsupportedOperation,
                $"Mutation '{mutation.Operation}' is disabled in phase 2.");

        if (!DaemonContract.IsUniqueBusName(uniqueBusName))
            throw new DaemonRequestException(
                DaemonContract.ErrorCallerUnknown,
                "The D-Bus caller has no valid unique bus identity.");

        bool entered = await _mutationSlots.WaitAsync(TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false);
        if (!entered)
            throw new DaemonRequestException(
                DaemonContract.ErrorBusy,
                "The bounded future mutation pipeline is saturated.");

        try
        {
            // UID/PID are resolved from the authenticated D-Bus sender. The
            // contract deliberately has no caller-supplied identity fields.
            CallerIdentity caller = await _identityResolver.ResolveAsync(
                uniqueBusName, cancellationToken).ConfigureAwait(false);

            bool authorized = await _authorization.IsAuthorizedAsync(
                caller, mutation.PolkitAction, allowUserInteraction: true, cancellationToken)
                .ConfigureAwait(false);
            if (!authorized)
                throw new DaemonRequestException(
                    DaemonContract.ErrorNotAuthorized,
                    $"Authorization denied for '{mutation.Operation}'.");

            if (_mutationExecutor is null || !_mutationExecutor.CanExecute(mutation))
                throw new DaemonRequestException(
                    DaemonContract.ErrorUnsupportedOperation,
                    $"Mutation '{mutation.Operation}' has no executor.");

            // Queue only after sender attribution and polkit authorization.
            // The XG transition stops the graphical session, so execution must
            // outlive the GUI D-Bus connection that requested it.
            if (!_mutationExecutor.TryQueue(mutation))
                throw new DaemonRequestException(
                    DaemonContract.ErrorBusy,
                    "An XG Mobile transition is already in progress.");
        }
        finally
        {
            _mutationSlots.Release();
        }
    }
}
