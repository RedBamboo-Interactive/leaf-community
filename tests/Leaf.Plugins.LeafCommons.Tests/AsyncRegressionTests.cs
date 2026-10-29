using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class AsyncRegressionTests
{
    [Fact]
    public async Task Relay_settings_paths_complete_on_a_single_threaded_dispatcher()
    {
        var providerCalls = 0;

        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var dispatcherThread = Environment.CurrentManagedThreadId;
            async Task<(Uri BaseUri, string? Credential)> Settings(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                Assert.Equal(dispatcherThread, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref providerCalls);
                return (new Uri("https://relay.example"), "envoy.credential");
            }

            var relay = new RelayHttpClient(new HttpClient(new YieldingJsonHandler()), Settings);

            var authorize = await relay.BuildAuthorizeUriAsync(
                new Uri("leaf://oauth/callback"),
                agentEntityId: "c816c9c9-c2c9-4d59-a2d8-caba898dcfaa", agentName: "Ada");
            var threads = await relay.GetThreadsAsync();

            Assert.Equal("https://relay.example/v1/oauth/authorize", authorize.GetLeftPart(UriPartial.Path));
            Assert.DoesNotContain("scenarios=", authorize.Query);
            Assert.Contains("agentEntityId=c816c9c9-c2c9-4d59-a2d8-caba898dcfaa", authorize.Query);
            Assert.Contains("agentName=Ada", authorize.Query);
            Assert.Empty(threads);
            Assert.Equal(dispatcherThread, Environment.CurrentManagedThreadId);
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, providerCalls);
        Assert.Null(typeof(RelayHttpClient).GetMethod("BuildAuthorizeUri"));
    }

    private sealed class YieldingJsonHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

        public override void Post(SendOrPostCallback callback, object? state)
            => queue.Add((callback, state));

        public static Task RunAsync(Func<Task> operation)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var context = new SingleThreadSynchronizationContext();
                SetSynchronizationContext(context);
                context.Post(async _ =>
                {
                    try
                    {
                        await operation();
                        completion.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                    finally
                    {
                        context.queue.CompleteAdding();
                    }
                }, null);

                foreach (var work in context.queue.GetConsumingEnumerable())
                    work.Callback(work.State);
            })
            {
                IsBackground = true,
                Name = "leaf-commons-single-thread-context",
            };
            thread.Start();
            return completion.Task;
        }
    }
}
