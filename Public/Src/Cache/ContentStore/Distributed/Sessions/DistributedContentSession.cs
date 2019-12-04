// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Stores;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Utilities.Tracing;

namespace BuildXL.Cache.ContentStore.Distributed.Sessions
{
    /// <summary>
    /// A content location based content session with an inner content session for storage.
    /// </summary>
    /// <typeparam name="T">The content locations being stored.</typeparam>
    public class DistributedContentSession<T> : ReadOnlyDistributedContentSession<T>, IContentSession
        where T : PathBase
    {
        private readonly SemaphoreSlim _putFileGate;

        /// <summary>
        /// Initializes a new instance of the <see cref="DistributedContentSession{T}"/> class.
        /// </summary>
        public DistributedContentSession(
            string name,
            IContentSession inner,
            IContentLocationStore contentLocationStore,
            ContentAvailabilityGuarantee contentAvailabilityGuarantee,
            DistributedContentCopier<T> contentCopier,
            MachineLocation localMachineLocation,
            PinCache pinCache = null,
            ContentTrackerUpdater contentTrackerUpdater = null,
            DistributedContentStoreSettings settings = default)
            : base(
                name,
                inner,
                contentLocationStore,
                contentAvailabilityGuarantee,
                contentCopier,
                localMachineLocation,
                pinCache: pinCache,
                contentTrackerUpdater: contentTrackerUpdater,
                settings)
        {
            _putFileGate = new SemaphoreSlim(settings.MaximumConcurrentPutFileOperations);
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutFileCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformPutFileGatedOperationAsync(operationContext, () =>
            {
                return PutCoreAsync(
                    operationContext,
                    (decoratedStreamSession, wrapStream) => decoratedStreamSession.PutFileAsync(operationContext, path, hashType, realizationMode, operationContext.Token, urgencyHint, wrapStream),
                    session => session.PutFileAsync(operationContext, hashType, path, realizationMode, operationContext.Token, urgencyHint),
                    path: path.Path);
            });
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutFileCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            // We are intentionally not gating PutStream operations because we don't expect a high number of them at
            // the same time.
            return PerformPutFileGatedOperationAsync(operationContext, () =>
            {
                return PutCoreAsync(
                    operationContext,
                    (decoratedStreamSession, wrapStream) => decoratedStreamSession.PutFileAsync(operationContext, path, contentHash, realizationMode, operationContext.Token, urgencyHint, wrapStream),
                    session => session.PutFileAsync(operationContext, contentHash, path, realizationMode, operationContext.Token, urgencyHint),
                    path: path.Path);
            });
        }

        private Task<PutResult> PerformPutFileGatedOperationAsync(OperationContext operationContext, Func<Task<PutResult>> func)
        {
            return _putFileGate.GatedOperationAsync(async (timeWaiting) =>
            {
                var gateOccupiedCount = Settings.MaximumConcurrentPutFileOperations - _putFileGate.CurrentCount;

                var result = await func();
                result.Metadata = new PutResult.ExtraMetadata()
                {
                    GateWaitTime = timeWaiting,
                    GateOccupiedCount = gateOccupiedCount,
                };

                return result;
            }, operationContext.Token);
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutCoreAsync(
                operationContext,
                (decoratedStreamSession, wrapStream) => decoratedStreamSession.PutStreamAsync(operationContext, hashType, wrapStream(stream), operationContext.Token, urgencyHint),
                session => session.PutStreamAsync(operationContext, hashType, stream, operationContext.Token, urgencyHint));
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutCoreAsync(
                operationContext,
                (decoratedStreamSession, wrapStream) => decoratedStreamSession.PutStreamAsync(operationContext, contentHash, wrapStream(stream), operationContext.Token, urgencyHint),
                session => session.PutStreamAsync(operationContext, contentHash, stream, operationContext.Token, urgencyHint));
        }

        /// <summary>
        /// Executes a put operation, while providing the logic to retrieve the bytes that were put through a RecordingStream.
        /// RecordingStream makes it possible to see the actual bytes that are being read by the inner ContentSession.
        /// </summary>
        private async Task<PutResult> PutCoreAsync(
            OperationContext context,
            Func<IDecoratedStreamContentSession, Func<Stream, Stream>, Task<PutResult>> putRecordedAsync,
            Func<IContentSession, Task<PutResult>> putAsync,
            string path = null)
        {
            PutResult result;
            if (ContentLocationStore.AreBlobsSupported && Inner is IDecoratedStreamContentSession decoratedStreamSession)
            {
                RecordingStream recorder = null;
                result = await putRecordedAsync(decoratedStreamSession, stream =>
                {
                    if (stream.CanSeek && stream.Length <= ContentLocationStore.MaxBlobSize)
                    {
                        recorder = RecordingStream.ReadRecordingStream(inner: stream, size: stream.Length);
                        return recorder;
                    }

                    return stream;
                });

                if (result && recorder != null)
                {
                    await PutBlobAsync(context, result.ContentHash, recorder.RecordedBytes);
                }
            }
            else
            {
                result = await putAsync(Inner);
            }

            if (!result)
            {
                return result;
            }

            // It is important to register location before requesting the proactive copy; otherwise, we can fail the proactive copy.
            var registerResult = await RegisterPutAsync(context, UrgencyHint.Nominal, result);

            // Only perform proactive copy to other machines if we succeeded in registering our location.
            if (registerResult && Settings.ProactiveCopyMode != ProactiveCopyMode.Disabled)
            {
                // Since the rest of the operation is done asynchronously, create new context to stop cancelling operation prematurely.
                var proactiveCopyTask = WithOperationContext(
                    context,
                    CancellationToken.None,
                    operationContext => ProactiveCopyIfNeededAsync(operationContext, result.ContentHash, path)
                );

                if (Settings.InlineProactiveCopies)
                {
                    var proactiveCopyResult = await proactiveCopyTask;

                    // Only fail if all copies failed.
                    if (!proactiveCopyResult.Succeeded && proactiveCopyResult.RingCopyResult?.Succeeded == false && proactiveCopyResult.OutsideRingCopyResult?.Succeeded == false)
                    {
                        return new PutResult(proactiveCopyResult);
                    }
                }
                else
                {
                    // Tracing task-related errors because normal failures already traced by the operation provider
                    proactiveCopyTask.TraceIfFailure(context, failureSeverity: Severity.Debug, traceTaskExceptionsOnly: true, operation: "ProactiveCopyIfNeeded");
                }
            }

            return registerResult;
        }

        private async Task<PutResult> RegisterPutAsync(OperationContext context, UrgencyHint urgencyHint, PutResult putResult)
        {
            if (putResult.Succeeded)
            {
                var updateResult = await ContentLocationStore.RegisterLocalLocationAsync(
                    context,
                    new[] { new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize) },
                    context.Token,
                    urgencyHint);

                if (!updateResult.Succeeded)
                {
                    return new PutResult(updateResult, putResult.ContentHash);
                }
            }

            return putResult;
        }
    }
}
