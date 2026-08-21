using System.Collections.Concurrent;
using GHelper.Daemon.Contract;
using GHelper.Daemon.Core;
using GHelper.Daemon.DBus;
using GHelper.Daemon.Hosting;
using GHelper.Linux.Daemon;
using Tmds.DBus.Protocol;
using static GHelper.Linux.Tests.Harness;

namespace GHelper.Linux.Tests;

public static class DaemonScenarios
{
    public static void RunAll()
    {
        Console.WriteLine("\n Phase 2 daemon boundary ");
        Contract_IsVersionedAndIntrospectable();
        Contract_HasNoSpoofableCallerParameters();
        Capabilities_AdvertiseOnlyImplementedMutationAndAreDefensive();
        UnknownMutation_FailsBeforeIdentityLookup();
        KnownMutations_FailClosedBeforeBusCalls();
        GranularAuthorization_UsesMappedActionAndResolvedCaller();
        AuthorizedMutation_StillHasNoExecutor();
        AuthorizedXgMutation_IsQueuedAfterPolkit();
        AuthorizedDgpuMutation_IsQueuedAfterPolkit();
        InvalidSender_FailsBeforeIdentityLookupWhenEnabled();
        FutureMutationPipeline_IsBounded();
        BoundedTransport_RetainsSlotUntilCancelledCallCompletes();
        Resolver_ExercisesRealValidationAndTransport();
        Resolver_TimeoutLeavesUnderlyingCallBounded();
        Polkit_CancelsWithUniqueIdsOnCancelAndTimeout();
        Polkit_SaturationFailsClosed();
        Polkit_CancelFailureAbortsDedicatedTransport();
        Handler_UsesStablePublicErrors();
        Client_ExposesConnectionOwnership();
        StartupContract_NeverReplacesOrQueues();
    }

    private static void Contract_IsVersionedAndIntrospectable()
        => Scenario(nameof(Contract_IsVersionedAndIntrospectable), _ =>
        {
            AssertEqual(1u, DaemonContract.ApiVersion, "daemon API version");
            Assert(DaemonContract.InterfaceName.EndsWith("1", StringComparison.Ordinal),
                "D-Bus interface name is not major-versioned");
            foreach (string method in new[] { "GetVersion", "GetCapabilities", "GetStatus", "RequestMutation" })
                Assert(DaemonContract.IntrospectionXml.Contains($"method name=\"{method}\"", StringComparison.Ordinal),
                    $"introspection omits {method}");
        });

    private static void Contract_HasNoSpoofableCallerParameters()
        => Scenario(nameof(Contract_HasNoSpoofableCallerParameters), _ =>
        {
            Assert(!DaemonContract.IntrospectionXml.Contains("uid", StringComparison.OrdinalIgnoreCase),
                "contract accepts a caller-supplied UID");
            Assert(!DaemonContract.IntrospectionXml.Contains("pid", StringComparison.OrdinalIgnoreCase),
                "contract accepts a caller-supplied PID");
            Assert(DaemonContract.IntrospectionXml.Contains("name=\"operation\" type=\"s\"", StringComparison.Ordinal),
                "mutation contract is not explicit");
        });

    private static void Capabilities_AdvertiseOnlyImplementedMutationAndAreDefensive()
        => Scenario(nameof(Capabilities_AdvertiseOnlyImplementedMutationAndAreDefensive), sandbox =>
        {
            var core = NewCore(out _, out _);
            string[] first = core.GetCapabilities();
            Assert(first.Length == 5, "unexpected daemon capability count");
            Assert(first.Count(c => c.StartsWith("mutate.", StringComparison.Ordinal)) == 2
                && first.Contains("mutate.xg-mode", StringComparer.Ordinal)
                && first.Contains("mutate.dgpu-mode", StringComparer.Ordinal),
                "daemon mutation capabilities do not match the live executor");
            first[0] = "write.hardware";
            Assert(core.GetCapabilities()[0] == "read.version",
                "caller mutated the daemon capability source");
            AssertEqual(DaemonContract.StatusState, core.GetStatus().State, "daemon status");
        });

    private static void UnknownMutation_FailsBeforeIdentityLookup()
        => Scenario(nameof(UnknownMutation_FailsBeforeIdentityLookup), _ =>
        {
            var core = NewCore(out FakeIdentityResolver identity, out FakeAuthorization authorization);
            DaemonRequestException ex = CaptureRequest(() =>
                core.RequestMutationAsync(":1.42", "arbitrary-write", default).AsTask().GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorInvalidArguments, ex.ErrorName, "unknown mutation error");
            AssertEqual(0, identity.Calls, "identity calls for unknown mutation");
            AssertEqual(0, authorization.Calls, "authorization calls for unknown mutation");
        });

    private static void KnownMutations_FailClosedBeforeBusCalls()
        => Scenario(nameof(KnownMutations_FailClosedBeforeBusCalls), _ =>
        {
            var core = NewCore(out FakeIdentityResolver identity, out FakeAuthorization authorization);
            foreach (MutationDefinition mutation in DaemonContract.Mutations)
            {
                DaemonRequestException ex = CaptureRequest(() =>
                    core.RequestMutationAsync("spoofed-and-irrelevant", mutation.Operation, default)
                        .AsTask().GetAwaiter().GetResult());
                AssertEqual(DaemonContract.ErrorUnsupportedOperation, ex.ErrorName,
                    $"production result for {mutation.Operation}");
            }
            AssertEqual(0, identity.Calls, "production identity calls");
            AssertEqual(0, authorization.Calls, "production polkit calls");
        });

    private static void GranularAuthorization_UsesMappedActionAndResolvedCaller()
        => Scenario(nameof(GranularAuthorization_UsesMappedActionAndResolvedCaller), _ =>
        {
            var identity = new FakeIdentityResolver();
            var authorization = new FakeAuthorization { Result = false };
            var core = new DaemonCore(identity, authorization, mutationExecutionEnabled: true);
            foreach (MutationDefinition mutation in DaemonContract.Mutations)
            {
                DaemonRequestException ex = CaptureRequest(() =>
                    core.RequestMutationAsync(":8.9", mutation.Operation, default).AsTask().GetAwaiter().GetResult());
                AssertEqual(DaemonContract.ErrorNotAuthorized, ex.ErrorName,
                    $"denial result for {mutation.Operation}");
                AssertEqual(mutation.PolkitAction, authorization.LastAction!,
                    $"polkit action for {mutation.Operation}");
                AssertEqual(":8.9", authorization.LastCaller.UniqueBusName,
                    "authorization caller bus name");
                AssertEqual(1000u, authorization.LastCaller.UnixUserId, "resolved UID");
                AssertEqual(4242u, authorization.LastCaller.UnixProcessId, "resolved PID");
            }
        });

    private static void AuthorizedMutation_StillHasNoExecutor()
        => Scenario(nameof(AuthorizedMutation_StillHasNoExecutor), _ =>
        {
            var authorization = new FakeAuthorization { Result = true };
            var core = new DaemonCore(new FakeIdentityResolver(), authorization, mutationExecutionEnabled: true);
            DaemonRequestException ex = CaptureRequest(() =>
                core.RequestMutationAsync(":1.2", "set-charge-limit", default).AsTask().GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorUnsupportedOperation, ex.ErrorName,
                "authorized mutation without executor");
            AssertEqual(1, authorization.Calls, "authorized call count");
        });

    private static void AuthorizedXgMutation_IsQueuedAfterPolkit()
        => Scenario(nameof(AuthorizedXgMutation_IsQueuedAfterPolkit), _ =>
        {
            var authorization = new FakeAuthorization { Result = true };
            var executor = new FakeMutationExecutor("disable-xg-mode");
            var core = new DaemonCore(
                new FakeIdentityResolver(),
                authorization,
                mutationExecutionEnabled: true,
                mutationExecutor: executor);
            core.RequestMutationAsync(":1.2", "disable-xg-mode", default)
                .AsTask().GetAwaiter().GetResult();
            AssertEqual(1, authorization.Calls, "XG authorization count");
            AssertEqual(1, executor.QueueCalls, "XG queue count");
            AssertEqual("disable-xg-mode", executor.LastOperation!, "queued XG operation");
        });

    private static void AuthorizedDgpuMutation_IsQueuedAfterPolkit()
        => Scenario(nameof(AuthorizedDgpuMutation_IsQueuedAfterPolkit), _ =>
        {
            var authorization = new FakeAuthorization { Result = true };
            var executor = new FakeMutationExecutor("disable-dgpu-mode");
            var core = new DaemonCore(
                new FakeIdentityResolver(),
                authorization,
                mutationExecutionEnabled: true,
                mutationExecutor: executor);
            core.RequestMutationAsync(":1.2", "disable-dgpu-mode", default)
                .AsTask().GetAwaiter().GetResult();
            AssertEqual(1, authorization.Calls, "dGPU authorization count");
            AssertEqual(1, executor.QueueCalls, "dGPU queue count");
            AssertEqual("disable-dgpu-mode", executor.LastOperation!, "queued dGPU operation");
        });

    private static void InvalidSender_FailsBeforeIdentityLookupWhenEnabled()
        => Scenario(nameof(InvalidSender_FailsBeforeIdentityLookupWhenEnabled), _ =>
        {
            var identity = new FakeIdentityResolver();
            var core = new DaemonCore(identity, new FakeAuthorization(), mutationExecutionEnabled: true);
            foreach (string sender in new[] { "", "org.ghelper.Gui", ":1.user", ":.2", ":1." })
            {
                DaemonRequestException ex = CaptureRequest(() =>
                    core.RequestMutationAsync(sender, "set-charge-limit", default)
                        .AsTask().GetAwaiter().GetResult());
                AssertEqual(DaemonContract.ErrorCallerUnknown, ex.ErrorName, $"invalid sender {sender}");
            }
            AssertEqual(0, identity.Calls, "identity calls for invalid senders");
        });

    private static void FutureMutationPipeline_IsBounded()
        => Scenario(nameof(FutureMutationPipeline_IsBounded), _ =>
        {
            var identity = new BlockingIdentityResolver();
            var core = new DaemonCore(identity, new FakeAuthorization(), mutationExecutionEnabled: true);
            Task first = core.RequestMutationAsync(":1.1", "set-charge-limit", default).AsTask();
            Task second = core.RequestMutationAsync(":1.2", "set-charge-limit", default).AsTask();
            Assert(SpinWait.SpinUntil(() => identity.Calls == 2, 1000), "two calls did not enter bounded pipeline");
            DaemonRequestException busy = CaptureRequest(() =>
                core.RequestMutationAsync(":1.3", "set-charge-limit", default).AsTask().GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorBusy, busy.ErrorName, "saturation error");
            identity.Complete();
            CaptureRequest(() => first.GetAwaiter().GetResult());
            CaptureRequest(() => second.GetAwaiter().GetResult());
        });

    private static void BoundedTransport_RetainsSlotUntilCancelledCallCompletes()
        => Scenario(nameof(BoundedTransport_RetainsSlotUntilCancelledCallCompletes), _ =>
        {
            var runner = new BoundedCallRunner(1);
            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            Task first = runner.RunAsync(() => pending.Task, cancellation.Token);
            cancellation.Cancel();
            AssertThrowsCancelled(first, "bounded call cancellation");
            DaemonRequestException busy = CaptureRequest(() =>
                runner.RunAsync(() => Task.FromResult(2), default).GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorBusy, busy.ErrorName, "slot released before underlying completion");
            pending.SetResult(1);
            Assert(SpinWait.SpinUntil(() =>
            {
                try
                {
                    return runner.RunAsync(() => Task.FromResult(2), default).GetAwaiter().GetResult() == 2;
                }
                catch (DaemonRequestException)
                {
                    return false;
                }
            }, 1000), "slot was not released after observed completion");
        });

    private static void Resolver_ExercisesRealValidationAndTransport()
        => Scenario(nameof(Resolver_ExercisesRealValidationAndTransport), _ =>
        {
            var transport = new FakeIdentityTransport();
            var resolver = new DbusCallerIdentityResolver(transport, transportTimeout: TimeSpan.FromSeconds(1));
            CallerIdentity caller = resolver.ResolveAsync(":7.8", default).AsTask().GetAwaiter().GetResult();
            AssertEqual(1000u, caller.UnixUserId, "resolver UID");
            AssertEqual(4242u, caller.UnixProcessId, "resolver PID");
            AssertEqual("GetConnectionUnixUser", transport.Members[0], "first resolver query");
            AssertEqual("GetConnectionUnixProcessID", transport.Members[1], "second resolver query");
        });

    private static void Resolver_TimeoutLeavesUnderlyingCallBounded()
        => Scenario(nameof(Resolver_TimeoutLeavesUnderlyingCallBounded), _ =>
        {
            var transport = new FakeIdentityTransport { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var resolver = new DbusCallerIdentityResolver(
                transport, maximumInFlight: 1, transportTimeout: TimeSpan.FromMilliseconds(25));
            AssertThrowsCancelled(
                resolver.ResolveAsync(":1.2", default).AsTask(), "resolver timeout");
            DaemonRequestException busy = CaptureRequest(() =>
                resolver.ResolveAsync(":1.3", default).AsTask().GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorBusy, busy.ErrorName, "resolver orphan saturation");
            transport.Pending.SetResult(1000);
        });

    private static void Polkit_CancelsWithUniqueIdsOnCancelAndTimeout()
        => Scenario(nameof(Polkit_CancelsWithUniqueIdsOnCancelAndTimeout), _ =>
        {
            string[] ids = ["ghelperd-one", "ghelperd-two"];
            int idIndex = 0;
            var transport = new FakePolkitTransport();
            var service = new PolkitAuthorizationService(
                transport,
                () => ids[idIndex++],
                maximumInFlight: 2,
                interactiveTimeout: TimeSpan.FromMilliseconds(40),
                cancellationTimeout: TimeSpan.FromMilliseconds(100));
            var caller = new CallerIdentity(":1.2", 1000, 4242);

            using (var cancelled = new CancellationTokenSource())
            {
                Task first = service.IsAuthorizedAsync(
                    caller, DaemonContract.Mutations[0].PolkitAction, true, cancelled.Token).AsTask();
                Assert(transport.Started.Wait(1000), "polkit call did not start");
                cancelled.Cancel();
                AssertThrowsCancelled(first, "polkit caller cancellation");
            }

            transport.Started.Reset();
            Task second = service.IsAuthorizedAsync(
                caller, DaemonContract.Mutations[1].PolkitAction, true, default).AsTask();
            Assert(transport.Started.Wait(1000), "second polkit call did not start");
            AssertThrowsCancelled(second, "polkit interactive timeout");

            AssertEqual(2, transport.CancelIds.Count, "polkit cancellation calls");
            AssertEqual(ids[0], transport.CancelIds[0], "first cancellation id");
            AssertEqual(ids[1], transport.CancelIds[1], "second cancellation id");
            Assert(ids[0] != ids[1], "polkit cancellation ids are not unique");
            AssertEqual(0, transport.AbortCalls, "healthy cancellation aborted transport");
        });

    private static void Polkit_SaturationFailsClosed()
        => Scenario(nameof(Polkit_SaturationFailsClosed), _ =>
        {
            var transport = new FakePolkitTransport();
            var service = new PolkitAuthorizationService(
                transport,
                maximumInFlight: 1,
                interactiveTimeout: TimeSpan.FromSeconds(10),
                cancellationTimeout: TimeSpan.FromMilliseconds(100));
            var caller = new CallerIdentity(":1.2", 1000, 4242);
            using var cancellation = new CancellationTokenSource();
            Task first = service.IsAuthorizedAsync(
                caller, DaemonContract.Mutations[0].PolkitAction, true, cancellation.Token).AsTask();
            Assert(transport.Started.Wait(1000), "saturation polkit call did not start");
            DaemonRequestException busy = CaptureRequest(() =>
                service.IsAuthorizedAsync(
                    caller, DaemonContract.Mutations[1].PolkitAction, true, default)
                    .AsTask().GetAwaiter().GetResult());
            AssertEqual(DaemonContract.ErrorBusy, busy.ErrorName, "polkit saturation error");
            cancellation.Cancel();
            AssertThrowsCancelled(first, "polkit saturation cleanup");
        });

    private static void Polkit_CancelFailureAbortsDedicatedTransport()
        => Scenario(nameof(Polkit_CancelFailureAbortsDedicatedTransport), _ =>
        {
            var transport = new FakePolkitTransport { CancelNeverCompletes = true };
            var service = new PolkitAuthorizationService(
                transport,
                maximumInFlight: 1,
                interactiveTimeout: TimeSpan.FromSeconds(10),
                cancellationTimeout: TimeSpan.FromMilliseconds(25));
            using var cancellation = new CancellationTokenSource();
            Task check = service.IsAuthorizedAsync(
                new CallerIdentity(":1.2", 1000, 4242),
                DaemonContract.Mutations[0].PolkitAction,
                true,
                cancellation.Token).AsTask();
            Assert(transport.Started.Wait(1000), "abort-path polkit call did not start");
            cancellation.Cancel();
            AssertThrowsCancelled(check, "polkit cancellation transport failure");
            AssertEqual(1, transport.AbortCalls, "failed cancellation did not abort dedicated transport");
        });

    private static void Handler_UsesStablePublicErrors()
        => Scenario(nameof(Handler_UsesStablePublicErrors), sandbox =>
        {
            var handler = new DaemonMethodHandler(NewCore(out _, out _));
            AssertEqual(DaemonContract.ObjectPath, handler.Path, "handler path");
            Assert(!handler.HandlesChildPaths, "handler accepts child paths");
            foreach (string error in new[]
            {
                DaemonContract.ErrorInvalidArguments,
                DaemonContract.ErrorCallerUnknown,
                DaemonContract.ErrorNotAuthorized,
                DaemonContract.ErrorUnsupportedOperation,
                DaemonContract.ErrorTimedOut,
                DaemonContract.ErrorCancelled,
                DaemonContract.ErrorBusy,
                DaemonContract.ErrorFailed
            })
            {
                string message = DaemonMethodHandler.PublicErrorMessage(error);
                Assert(!string.IsNullOrWhiteSpace(message), $"empty stable message for {error}");
                Assert(!message.Contains("secret", StringComparison.OrdinalIgnoreCase),
                    "public error leaks internal detail");
            }
            AssertEqual(DaemonContract.MessageFailed,
                DaemonMethodHandler.PublicErrorMessage("internal-secret"),
                "unknown public error mapping");
        });

    private static void Client_ExposesConnectionOwnership()
        => Scenario(nameof(Client_ExposesConnectionOwnership), _ =>
        {
            using var borrowedConnection = new DBusConnection("unix:path=/nonexistent-ghelper-test");
            using var borrowed = new GHelperDaemonClient(borrowedConnection, ownsConnection: false);
            Assert(!borrowed.OwnsConnection, "borrowed client claims ownership");

            var ownedConnection = new DBusConnection("unix:path=/nonexistent-ghelper-test");
            using var owned = new GHelperDaemonClient(ownedConnection, ownsConnection: true);
            Assert(owned.OwnsConnection, "owned client lost ownership flag");
        });

    private static void StartupContract_NeverReplacesOrQueues()
        => Scenario(nameof(StartupContract_NeverReplacesOrQueues), _ =>
        {
            AssertEqual(RequestNameOptions.None, DaemonStartupContract.NameRequestOptions,
                "service ownership flags");
            AssertEqual(73, DaemonStartupContract.ExitCodeFor(DaemonTerminationReason.NameUnavailable),
                "name acquisition failure exit");
            AssertEqual(1, DaemonStartupContract.ExitCodeFor(DaemonTerminationReason.ConnectionLost),
                "name/connection loss exit");
        });

    private static DaemonCore NewCore(
        out FakeIdentityResolver identity,
        out FakeAuthorization authorization)
    {
        identity = new FakeIdentityResolver();
        authorization = new FakeAuthorization();
        return new DaemonCore(identity, authorization);
    }

    private static DaemonRequestException CaptureRequest(Action action)
    {
        try
        {
            action();
        }
        catch (DaemonRequestException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Expected DaemonRequestException was not thrown.");
    }

    private static void AssertThrowsCancelled(Task task, string label)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException($"{label}: expected cancellation");
    }

    private sealed class FakeIdentityResolver : ICallerIdentityResolver
    {
        public int Calls { get; private set; }

        public ValueTask<CallerIdentity> ResolveAsync(
            string uniqueBusName,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CallerIdentity(uniqueBusName, 1000, 4242));
        }
    }

    private sealed class BlockingIdentityResolver : ICallerIdentityResolver
    {
        private readonly TaskCompletionSource<CallerIdentity> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public ValueTask<CallerIdentity> ResolveAsync(
            string uniqueBusName,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<CallerIdentity>(_completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete() => _completion.TrySetResult(new CallerIdentity(":1.1", 1000, 4242));
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public int Calls { get; private set; }
        public bool Result { get; init; }
        public string? LastAction { get; private set; }
        public CallerIdentity LastCaller { get; private set; }

        public ValueTask<bool> IsAuthorizedAsync(
            CallerIdentity caller,
            string polkitAction,
            bool allowUserInteraction,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastCaller = caller;
            LastAction = polkitAction;
            Assert(allowUserInteraction, "future GUI authorization cannot request interaction");
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeMutationExecutor(string supportedOperation) : IMutationExecutor
    {
        public int QueueCalls { get; private set; }
        public string? LastOperation { get; private set; }

        public bool CanExecute(MutationDefinition mutation)
            => mutation.Operation == supportedOperation;

        public bool TryQueue(MutationDefinition mutation)
        {
            QueueCalls++;
            LastOperation = mutation.Operation;
            return true;
        }
    }

    private sealed class FakeIdentityTransport : ICallerIdentityTransport
    {
        public List<string> Members { get; } = [];
        public TaskCompletionSource<uint>? Pending { get; init; }

        public Task<uint> QueryUInt32Async(string member, string uniqueBusName)
        {
            Members.Add(member);
            if (Pending is not null)
                return Pending.Task;
            return Task.FromResult(member == "GetConnectionUnixUser" ? 1000u : 4242u);
        }
    }

    private sealed class FakePolkitTransport : IPolkitTransport
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _checks = new();
        public ManualResetEventSlim Started { get; } = new(false);
        public List<string> CheckIds { get; } = [];
        public List<string> CancelIds { get; } = [];
        public int AbortCalls { get; private set; }
        public bool CancelNeverCompletes { get; init; }

        public Task<bool> CheckAuthorizationAsync(
            CallerIdentity caller,
            string polkitAction,
            bool allowUserInteraction,
            string cancellationId)
        {
            lock (CheckIds)
                CheckIds.Add(cancellationId);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert(_checks.TryAdd(cancellationId, completion), "duplicate cancellation id");
            Started.Set();
            return completion.Task;
        }

        public Task CancelCheckAuthorizationAsync(string cancellationId)
        {
            lock (CancelIds)
                CancelIds.Add(cancellationId);
            if (CancelNeverCompletes)
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            if (_checks.TryRemove(cancellationId, out TaskCompletionSource<bool>? completion))
                completion.TrySetCanceled();
            return Task.CompletedTask;
        }

        public void Abort()
        {
            AbortCalls++;
            foreach (TaskCompletionSource<bool> completion in _checks.Values)
                completion.TrySetCanceled();
            _checks.Clear();
        }
    }
}
