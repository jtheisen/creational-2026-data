using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public sealed record ContainerJobOptions(string ResourceId, string Image, string ContainerName,
    double Cpu = 1, string Memory = "2Gi");

public sealed class ContainerAppsJobLauncher(HttpClient http, TokenCredential credential,
    ContainerJobOptions options) : IComputeJobLauncher
{
    private readonly string _base = "https://management.azure.com" + options.ResourceId.TrimEnd('/');
    private const string Version = "2025-07-01";

    public async Task<string> StartAsync(TargetId target, string inputManifestUri, string outputPrefix,
        string recipeVersion, CancellationToken cancellationToken)
    {
        var payload = new
        {
            containers = new[]
            {
                new
                {
                    name = options.ContainerName, image = options.Image,
                    resources = new { cpu = options.Cpu, memory = options.Memory },
                    command = new[] { "python", "-m", "workers.collection" },
                    args = new[] { "--target-id", target.ToString(), "--inputs-manifest", inputManifestUri,
                        "--output-prefix", outputPrefix, "--recipe-version", recipeVersion }
                }
            }
        };
        using var request = await AuthorizedAsync(HttpMethod.Post, _base + "/start?api-version=" + Version, cancellationToken);
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Job start response had no execution name");
        }
        var location = response.Headers.Location
            ?? throw new InvalidDataException("Accepted job start response had no operation location");
        for (var poll = 0; poll < 15; poll++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var statusRequest = await AuthorizedAsync(HttpMethod.Get, location.ToString(), cancellationToken);
            using var statusResponse = await http.SendAsync(statusRequest, cancellationToken);
            if (statusResponse.StatusCode == HttpStatusCode.Accepted) continue;
            statusResponse.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await statusResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                return value;
        }
        throw new TimeoutException("Container Apps start operation did not return an execution name");
    }

    public async Task<ComputeExecutionStatus> GetStatusAsync(string executionId, CancellationToken cancellationToken)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get,
            _base + "/executions/" + Uri.EscapeDataString(executionId) + "?api-version=" + Version, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            return new(ComputeExecutionState.Unknown, $"Execution status HTTP {(int)response.StatusCode}; retry on next reconciliation");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var properties = document.RootElement.GetProperty("properties");
        return new(MapStatus(properties.GetProperty("status").GetString()),
            properties.TryGetProperty("message", out var message) ? message.GetString() : null);
    }

    public static ComputeExecutionState MapStatus(string? status) => status switch
    {
        "Succeeded" => ComputeExecutionState.Succeeded,
        "Failed" or "Stopped" => ComputeExecutionState.Failed,
        "Running" or "Processing" => ComputeExecutionState.Running,
        _ => ComputeExecutionState.Unknown
    };

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }
}
