using System.Net;
using System.Text;

namespace SmartWizz.Tests;

public class ParallelReviewTests
{
    sealed class ConcurrentHandler : HttpMessageHandler
    {
        int _active;
        int _peak;
        public int Peak => Volatile.Read(ref _peak);
        readonly TaskCompletionSource _overlap = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            int old;
            do { old = Volatile.Read(ref _peak); }
            while (active > old && Interlocked.CompareExchange(ref _peak, active, old) != old);
            if (active >= 2) _overlap.TrySetResult();
            try
            {
                await _overlap.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"{\\"findings\\":[]}"}}]}""".Replace(@"\\", @"\"), Encoding.UTF8, "application/json"),
                };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    [Fact]
    public async Task Independent_batches_overlap_and_timings_remain_ordered()
    {
        using var handler = new ConcurrentHandler();
        using var http = new HttpClient(handler);
        var provider = new Provider(http, new ProviderConfig { Endpoint = "http://test/v1", Model = "m" }, null);
        List<FileDiff> files = [];
        for (int i = 0; i < 4; i++)
        {
            var file = new FileDiff { Path = $"file{i}.cs" };
            file.NewLines.Add(1, "x");
            file.AddedLines.Add(1);
            file.Annotated.Append("1234567890");
            files.Add(file);
        }
        ReviewResult result = await Review.RunAsync(files,
            new ReviewConfig { MaxCharsPerRequest = 10, MaxParallelRequests = 2 },
            provider, TestPrompts.Default, default);
        Assert.Equal(2, handler.Peak);
        Assert.Equal(4, result.ReviewedFiles);
        Assert.Equal(4, result.Requests);
        Assert.Equal([1, 2, 3, 4], result.RequestTimings.Select(t => t.Request));
    }
}
