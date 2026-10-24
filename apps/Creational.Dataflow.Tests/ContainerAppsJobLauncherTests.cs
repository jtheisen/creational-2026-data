using Creational.Dataflow.Execution;
using Creational.Dataflow.Targets;
using Azure.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ContainerAppsJobLauncherTests
{
    [Theory]
    [InlineData("Succeeded", ComputeExecutionState.Succeeded)]
    [InlineData("Failed", ComputeExecutionState.Failed)]
    [InlineData("Running", ComputeExecutionState.Running)]
    [InlineData("Processing", ComputeExecutionState.Running)]
    public void AzureExecutionStatusMapsToControlPlaneState(string status, ComputeExecutionState expected)
    {
        Assert.Equal(expected, ContainerAppsJobLauncher.MapStatus(status));
    }

    [Fact]
    public async Task StartSendsCompleteTargetSpecificTemplate()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"name\":\"job-123\"}") };
        });
        var launcher = new ContainerAppsJobLauncher(new HttpClient(handler), new TestCredential(),
            new ContainerJobOptions("/subscriptions/s/resourceGroups/r/providers/Microsoft.App/jobs/j",
                "registry/image:v1", "collection-worker"));
        var execution = await launcher.StartAsync(new TargetId("c", "parse-shard", "p1"),
            "https://example.test/input.json", "https://example.test/output/", "v1", CancellationToken.None);
        Assert.Equal("job-123", execution);
        using var json = JsonDocument.Parse(body!);
        var container = json.RootElement.GetProperty("containers")[0];
        Assert.Equal("registry/image:v1", container.GetProperty("image").GetString());
        Assert.Contains("c/parse-shard/p1", container.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("2Gi", container.GetProperty("resources").GetProperty("memory").GetString());
    }

    [Fact]
    public async Task ResizeStartUsesSameImageAndJobWithResizeCommand()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"name\":\"resize-execution\"}") };
        });
        var launcher = new ContainerAppsJobLauncher(new HttpClient(handler), new TestCredential(),
            new ContainerJobOptions("/subscriptions/s/resourceGroups/r/providers/Microsoft.App/jobs/j",
                "registry/image:v1", "collection-worker"));

        Assert.Equal("resize-execution", await launcher.StartResizeAsync("run-123", CancellationToken.None));
        using var json = JsonDocument.Parse(body!);
        var container = json.RootElement.GetProperty("containers")[0];
        Assert.Equal("registry/image:v1", container.GetProperty("image").GetString());
        Assert.Equal(["python", "-m", "workers.resize"],
            container.GetProperty("command").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["--run-id", "run-123"],
            container.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
