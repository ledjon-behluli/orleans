#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// A single-concurrency, in-order task scheduler for per-activation work scheduling.
    /// </summary>
    [DebuggerDisplay("ActivationTaskScheduler-{myId} RunQueue={workerGroup.WorkItemCount}")]
    internal sealed partial class ActivationTaskScheduler : TaskScheduler
    {
        private readonly ILogger logger;

        private static long idCounter;
        private readonly long myId;
        private readonly WorkItemGroup workerGroup;
#if EXTRA_STATS
        private readonly CounterStatistic turnsExecutedStatistic;
#endif

        internal ActivationTaskScheduler(WorkItemGroup workGroup, ILogger<ActivationTaskScheduler> logger)
        {
            this.logger = logger;
            myId = Interlocked.Increment(ref idCounter);
            workerGroup = workGroup;
#if EXTRA_STATS
            turnsExecutedStatistic = CounterStatistic.FindOrCreate(name + ".TasksExecuted");
#endif
            LogCreatedTaskScheduler(this, workerGroup.GrainContext);
        }

        /// <summary>Gets an enumerable of the tasks currently scheduled on this scheduler.</summary>
        /// <returns>An enumerable of the tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks() => this.workerGroup.GetScheduledTasks();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RunTaskFromWorkItemGroup(Task task)
        {
            bool done = TryExecuteTask(task);
            if (!done)
            {
#if DEBUG
                LogTryExecuteTaskNotDone(task);
#endif
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogTryExecuteTaskNotDone(Task task)
        {
            logger.LogWarning(
                (int)ErrorCode.SchedulerTaskExecuteIncomplete4,
                "RunTask: Incomplete base.TryExecuteTask for Task Id={TaskId} with Status={TaskStatus}",
                task.Id,
                task.Status);
        }

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("{TaskScheduler} QueueTask Task Id={TaskId}", myId, task.Id);
#endif
            workerGroup.EnqueueTask(task);
        }

        /// <summary>
        /// Determines whether the provided <see cref="T:System.Threading.Tasks.Task"/> can be executed synchronously in this call, and if it can, executes it.
        /// </summary>
        /// <returns>
        /// A Boolean value indicating whether the task was executed inline.
        /// </returns>
        /// <param name="task">The <see cref="T:System.Threading.Tasks.Task"/> to be executed.</param>
        /// <param name="taskWasPreviouslyQueued">A Boolean denoting whether or not task has previously been queued. If this parameter is True, then the task may have been previously queued (scheduled); if False, then the task is known not to have been queued, and this call is being made in order to execute the task inline without queuing it.</param>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            var canExecuteInline = !taskWasPreviouslyQueued && Equals(RuntimeContext.Current, workerGroup.GrainContext);

#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Status={Status} PreviouslyQueued={PreviouslyQueued} CanExecute={CanExecute} Queued={Queued}",
                    myId,
                    task.Id,
                    task.Status,
                    taskWasPreviouslyQueued,
                    canExecuteInline,
                    workerGroup.ExternalWorkItemCount);
            }
#endif
            if (!canExecuteInline)
            {
#if DEBUG
                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Status={Status} Execute=No", myId, task.Id, task.Status);
#endif
                return false;
            }

#if EXTRA_STATS
            turnsExecutedStatistic.Increment();
#endif
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace(
                    "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Yes",
                    myId,
                    task.Id,
                    Thread.CurrentThread.ManagedThreadId);
#endif
            // Try to run the task.
            bool done = TryExecuteTask(task);
            if (!done)
            {
#if DEBUG
                LogTryExecuteTaskNotDone(task);
#endif
            }
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace(
                    "{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Done Ok={Ok}",
                    myId,
                    task.Id,
                    Thread.CurrentThread.ManagedThreadId,
                    done);
#endif
            return done;
        }

        public override string ToString() => $"{GetType().Name}-{myId}:Queued={workerGroup.ExternalWorkItemCount}";

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Created {TaskScheduler} with GrainContext={GrainContext}"
        )]
        private partial void LogCreatedTaskScheduler(ActivationTaskScheduler taskScheduler, IGrainContext grainContext);
    }
}
