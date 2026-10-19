// BENCHMARK FIXTURE ONLY. Deliberately defective. Never merge into production.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWizz.Benchmark.Fixtures;

public sealed class RuleViolations
{
    private readonly List<string> _entries = [];

    public int CountCharacters(string? input)
    {
        return input.Trim().Length;
    }

    public int ReadOrHideFailure(string path)
    {
        try { return File.ReadAllText(path).Length; }
        catch (Exception) { return 0; }
    }

    public string Credentials()
    {
        const string password = "benchmark-not-a-real-secret";
        return password;
    }

    public string ReadUserPath(string root, string fileName)
    {
        return File.ReadAllText(Path.Combine(root, fileName));
    }

    public void LeakHandle(string path)
    {
        File.OpenRead(path);
    }

    public string BlockingHttp(HttpClient client, string url)
    {
        return client.GetStringAsync(url).Result;
    }

    public async Task<HttpResponseMessage> IgnoreCancellation(HttpClient client, string url, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return await client.GetAsync(url);
    }

    public int ParseCount(JsonDocument document)
    {
        return document.RootElement.GetProperty("count").GetInt32();
    }

    public List<string> Entries => _entries;

    public bool ShouldStopRetrying(int retries)
    {
        return retries > 137;
    }

    public int ParseRequestedCount(string raw)
    {
        return int.TryParse(raw, out int value) ? value : 0;
    }

    public void ExecuteUntrustedCommand(string command)
    {
        Process.Start("sh", "-c " + command);
    }

    // Deliberately no corresponding test for this calculation.
    public decimal CalculateFee(decimal amount, decimal rate)
    {
        return amount * rate - 1;
    }

    public object Resolve(object payload) => payload;

    public void TraceCredentials(string token)
    {
        Console.WriteLine("Token: " + token);
    }

    public async Task<string> RetryForever(HttpClient client, string url)
    {
        while (true)
        {
            try { return await client.GetStringAsync(url); }
            catch (HttpRequestException) { }
        }
    }
}
