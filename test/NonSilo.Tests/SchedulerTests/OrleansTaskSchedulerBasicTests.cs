using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost.Utils;
using Orleans.Internal;
using Orleans;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.SchedulerTests
{
    /// <summary>
    /// Test implementation of IGrainContext for unit testing the Orleans task scheduler without requiring a full grain activation.
    /// </summary>
    internal class UnitTestSchedulingContext : IGrainContext, IDisposable
    {
        public static UnitTestSchedulingContext Create(ILoggerFactory loggerFactory)
        {
            var result = new UnitTestSchedulingContext();
            result.WorkItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(result, loggerFactory);
            return result;
        }

        private UnitTestSchedulingContext() { }

        public WorkItemGroup WorkItemGroup { get; private set; }

        public GrainReference GrainReference => throw new NotImplementedException();

        public GrainId GrainId => throw new NotImplementedException();

        public IAddressable GrainInstance => throw new NotImplementedException();

        public ActivationId ActivationId => throw new NotImplementedException();

        public GrainAddress Address => throw new NotImplementedException();

        public IServiceProvider ActivationServices => throw new NotImplementedException();

        public IDictionary<object, object> Items => throw new NotImplementedException();

        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();

        public IWorkItemScheduler Scheduler => WorkItemGroup;

        public bool IsExemptFromCollection => throw new NotImplementedException();

        public PlacementStrategy PlacementStrategy => throw new NotImplementedException();

        object IGrainContext.GrainInstance => throw new NotImplementedException();

        public void Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }
        public Task Deactivated => Task.CompletedTask;
        public void Dispose() => (Scheduler as IDisposable)?.Dispose();
        public TComponent GetComponent<TComponent>() where TComponent : class => throw new NotImplementedException();
        public TTarget GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();
        public void ReceiveMessage(object message) => throw new NotImplementedException();

        public void SetComponent<TComponent>(TComponent value) where TComponent : class => throw new NotImplementedException();

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);
        void IGrainContext.Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        void IGrainContext.Migrate(Dictionary<string, object> requestContext, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
    
    /// <summary>
    /// Basic tests for the Orleans task scheduler, which is the core component responsible for scheduling grain work items.
    /// The scheduler ensures single-threaded execution semantics for grains while efficiently utilizing thread pool resources.
    /// These tests verify fundamental scheduling operations without requiring a full Orleans runtime.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Scheduler")]
    public class OrleansTaskSchedulerBasicTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private static readonly object Lockable = new object();
        private readonly UnitTestSchedulingContext rootContext;
        private readonly ILoggerFactory loggerFactory;
        public OrleansTaskSchedulerBasicTests(ITestOutputHelper output)
        {
            this.output = output;
            SynchronizationContext.SetSynchronizationContext(null);
            this.loggerFactory = InitSchedulerLogging();
            this.rootContext = UnitTestSchedulingContext.Create(loggerFactory);
        }
        
        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        /// <summary>
        /// Tests that tasks can be scheduled and executed on the Orleans activation task scheduler.
        /// Verifies basic task execution flow in the grain's single-threaded execution context.
        /// </summary>
        [Fact, TestCategory("AsynchronyPrimitives")]
        public async Task Async_Task_Start_ActivationTaskScheduler()
        {
            int expected = 2;
            bool done = false;
            Task<int> t = new Task<int>(() => { done = true; return expected; });
            rootContext.Scheduler.QueueTask(t);

            int received = await t;
            Assert.True(t.IsCompleted, "Task should have completed");
            Assert.False(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.True(done, "Task should be done");
            Assert.Equal(expected, received);
        }

        [Fact]
        public void Sched_SimpleFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"

            int n = 0;
            // ReSharper disable AccessToModifiedClosure
            void item1() { n = n + 5; }
            void item2() { n = n * 3; }
            // ReSharper restore AccessToModifiedClosure
            this.rootContext.Scheduler.QueueAction(item1);
            rootContext.Scheduler.QueueAction(item2);

            // Pause to let things run
            Thread.Sleep(1000);

            // N should be 15, because the two tasks should execute in order
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
            this.output.WriteLine("Test executed OK.");
        }

        [Fact]
        public async Task Sched_Task_TplFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"

            int n = 0;

            // ReSharper disable AccessToModifiedClosure
            Task task1 = new Task(() => { Thread.Sleep(1000); n = n + 5; });
            Task task2 = new Task(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure

            rootContext.Scheduler.QueueTask(task1);
            rootContext.Scheduler.QueueTask(task2);

            await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(5));

            // N should be 15, because the two tasks should execute in order
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
        }

        [Fact]
        public async Task Sched_Task_ClosureWorkItem_Wait()
        {
            const int NumTasks = 10;

            ManualResetEvent[] flags = new ManualResetEvent[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                flags[i] = new ManualResetEvent(false);
            }

            Task[] tasks = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                tasks[i] = new Task(() => { this.output.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
            }

            Action[] workItems = new Action[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                workItems[i] = () =>
                {
                    this.output.WriteLine("Inside ClosureWorkItem-" + taskNum);
                    tasks[taskNum].Start(TaskScheduler.Default);
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    bool ok = tasks[taskNum].Wait(TimeSpan.FromMilliseconds(NumTasks * 100));
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                    Assert.True(ok, "Wait completed successfully inside ClosureWorkItem-" + taskNum);
                };
            }

            foreach (var workItem in workItems) this.rootContext.Scheduler.QueueAction(workItem);
            foreach (var flag in flags) flag.Set();
            for (int i = 0; i < tasks.Length; i++)
            {
                await tasks[i].WaitAsync(TimeSpan.FromMilliseconds(NumTasks * 150));
            }


            for (int i = 0; i < tasks.Length; i++)
            {
                Assert.False(tasks[i].IsFaulted, "Task.IsFaulted-" + i + " Exception=" + tasks[i].Exception);
                Assert.True(tasks[i].IsCompleted, "Task.IsCompleted-" + i);
            }
        }

        [Fact]
        public async Task Sched_Task_TaskWorkItem_CurrentScheduler()
        {
            var result0 = new TaskCompletionSource<bool>();
            var result1 = new TaskCompletionSource<bool>();

            Task t1 = null;
            rootContext.Scheduler.QueueAction(() =>
            {
                try
                {
                    this.output.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    var taskScheduler = ((WorkItemGroup)rootContext.Scheduler).TaskScheduler;
                    Assert.Equal(taskScheduler, TaskScheduler.Current); //

                    t1 = new Task(() =>
                    {
                        this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        var taskScheduler = ((WorkItemGroup)rootContext.Scheduler).TaskScheduler;  // "TaskScheduler.Current #1"
                        Assert.Equal(taskScheduler, TaskScheduler.Current); //
                        result1.SetResult(true);
                    });
                    t1.Start();

                    result0.SetResult(true);
                }
                catch (Exception exc)
                {
                    result0.SetException(exc);
                }
            });

            await result0.Task.WaitAsync(TimeSpan.FromMinutes(1));
            Assert.True(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.True(await result0.Task, "Task-0 completed");

            Assert.NotNull(t1); // Task-1 started
            await result1.Task.WaitAsync(TimeSpan.FromMinutes(1));
            // give a minimum extra chance to yield after result0 has been set, as it might not have finished the t1 task
            await t1.WaitAsync(TimeSpan.FromMilliseconds(1));

            Assert.True(t1.IsCompleted, "Task-1 completed");
            Assert.False(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.True(await result1.Task, "Task-1 completed");
        }
                
        [Fact]
        public async Task Sched_Task_SubTaskExecutionSequencing()
        {
            LogContext("Main-task " + Task.CurrentId);

            int n = 0;
            TaskCompletionSource<int> finished = new TaskCompletionSource<int>();
            var numCompleted = new[] {0};
            void closure()
            {
                LogContext("ClosureWorkItem-task " + Task.CurrentId);

                for (int i = 0; i < 10; i++)
                {
                    int id = -1;
                    void action()
                    {
                        id = Task.CurrentId.HasValue ? (int)Task.CurrentId : -1;

                        // ReSharper disable AccessToModifiedClosure
                        LogContext("Sub-task " + id + " n=" + n);

                        int k = n;
                        this.output.WriteLine("Sub-task " + id + " sleeping");
                        Thread.Sleep(100);
                        this.output.WriteLine("Sub-task " + id + " awake");
                        n = k + 1;
                        // ReSharper restore AccessToModifiedClosure
                    }
                    Task.Factory.StartNew(action).ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        this.output.WriteLine("Sub-task " + id + " Done");
                        if (Interlocked.Increment(ref numCompleted[0]) == 10)
                        {
                            finished.SetResult(0);
                        }
                    });
                }
            }

            rootContext.Scheduler.QueueAction(closure);

            // Pause to let things run
            this.output.WriteLine("Main-task sleeping");
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), finished.Task);
            this.output.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10, n);  // "Work items executed concurrently"
        }
        
        [Fact]
        public async Task Sched_AC_RequestContext_StartNew_ContinueWith()
        {
            const string key = "A";
            int val = Random.Shared.Next();
            RequestContext.Set(key, val);

            this.output.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get Initial"

            Task t0 = Task.Factory.StartNew(async () =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #0"

                Task t1 = Task.Factory.StartNew(() =>
                {
                    this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #1"
                });
                Task t2 = t1.ContinueWith((_) =>
                {
                    this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #2"
                });
                await t2.WaitAsync(TimeSpan.FromSeconds(5));
            }).Unwrap();
            await t0.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(t0.IsCompleted, "Task #0 FAULTED=" + t0.Exception);
        }

        [Fact]
        public async Task RequestContextProtectedInQueuedTasksTest()
        {
            string key = Guid.NewGuid().ToString();
            string value = Guid.NewGuid().ToString();

            // Caller RequestContext is protected from clear within QueueTask
            RequestContext.Set(key, value);
            await rootContext.QueueTask(() => AsyncCheckClearRequestContext(key));
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is protected from clear within QueueTask even if work is not actually asynchronous.
            await this.rootContext.QueueTask(() => NonAsyncCheckClearRequestContext(key));
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is protected from clear when work is asynchronous.
            async Task asyncCheckClearRequestContext()
            {
                RequestContext.Clear();
                Assert.Null(RequestContext.Get(key));
                await Task.Delay(TimeSpan.Zero);
            }
            await asyncCheckClearRequestContext();
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is NOT protected from clear when work is not asynchronous.
            Task nonAsyncCheckClearRequestContext()
            {
                RequestContext.Clear();
                Assert.Null(RequestContext.Get(key));
                return Task.CompletedTask;
            }
            await nonAsyncCheckClearRequestContext();
            Assert.Null(RequestContext.Get(key));
        }

        private static async Task AsyncCheckClearRequestContext(string key)
        {
            Assert.Null(RequestContext.Get(key));
            await Task.Delay(TimeSpan.Zero);
        }

        private static Task NonAsyncCheckClearRequestContext(string key)
        {
            Assert.Null(RequestContext.Get(key));
            return Task.CompletedTask;
        }

        private void LogContext(string what)
        {
            lock (Lockable)
            {
                this.output.WriteLine(
                    "{0}\n"
                    + " TaskScheduler.Current={1}\n"
                    + " Task.Factory.Scheduler={2}\n"
                    + " SynchronizationContext.Current={3}\n"
                    + " Orleans-RuntimeContext.Current={4}",
                    what,
                    (TaskScheduler.Current == null ? "null" : TaskScheduler.Current.ToString()),
                    (Task.Factory.Scheduler == null ? "null" : Task.Factory.Scheduler.ToString()),
                    (SynchronizationContext.Current == null ? "null" : SynchronizationContext.Current.ToString()),
                    (RuntimeContext.Current == null ? "null" : RuntimeContext.Current.ToString())
                );

                //var st = new StackTrace();
                //output.WriteLine("Backtrace: " + st);
            }
        }

        internal static ILoggerFactory InitSchedulerLogging()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter("Scheduler", LogLevel.Trace);
            filters.AddFilter("Scheduler.WorkerPoolThread", LogLevel.Trace);
            var loggerFactory = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("Silo", DateTime.UtcNow.ToString("yyyyMMdd_hhmmss")), filters);
            return loggerFactory;
        }
    }
}

// ReSharper restore ConvertToConstant.Local
