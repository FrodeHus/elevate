using System.Text;
using System.Text.Json;
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Tests.Support;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// <c>elevate token</c> and <c>run --export-token</c>: which resource a token is minted for, what it
/// is called when it reaches a command, and the Graph ceiling a caller has to acknowledge first.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class TokenCommandTests
{
    [Theory]
    [InlineData("arm", "arm", "https://management.azure.com")]
    [InlineData("ARM", "arm", "https://management.azure.com")]
    [InlineData("azure", "arm", "https://management.azure.com")]
    [InlineData("graph", "graph", "https://graph.microsoft.com")]
    [InlineData("https://vault.azure.net", "vault.azure.net", "https://vault.azure.net")]
    // A path is not part of the audience, and a bare host is the common slip: both land on the resource root.
    [InlineData("https://vault.azure.net/.default", "vault.azure.net", "https://vault.azure.net")]
    [InlineData("vault.azure.net", "vault.azure.net", "https://vault.azure.net")]
    public void AResourceIsAnAliasAUriOrAHost(string text, string name, string uri)
    {
        var resource = TokenResource.Parse(text);
        resource.Name.Should().Be(name);
        resource.Uri.Should().Be(uri);
    }

    [Fact]
    public void TheAliasesCarryTheScopesTheyMean()
    {
        TokenResource.Parse("arm").Scopes.Should().Equal("https://management.azure.com/user_impersonation");
        TokenResource.Parse("graph").Scopes.Should().Contain("https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory");
        TokenResource.Parse("https://vault.azure.net").Scopes.Should().Equal("https://vault.azure.net/.default");
        TokenResource.Parse("arm").IsGraph.Should().BeFalse();
        TokenResource.Parse("graph").IsGraph.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("ftp://vault.azure.net")]
    public void SomethingThatNamesNoResourceIsAUsageError(string text)
    {
        var act = () => TokenResource.Parse(text);
        act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
    }

    [Fact]
    public void TheVariableNamesTheResourceSoNoTokenArrivesUnattributed()
    {
        TokenResource.Parse("arm").EnvironmentVariable.Should().Be("ELEVATE_ARM_TOKEN");
        TokenResource.Parse("graph").EnvironmentVariable.Should().Be("ELEVATE_GRAPH_TOKEN");
        TokenResource.Parse("https://vault.azure.net").EnvironmentVariable.Should().Be("ELEVATE_VAULT_AZURE_NET_TOKEN");
    }

    [Fact]
    public void ExpiryComesFromTheTokenItself()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(75).ToUnixTimeSeconds();
        TokenResource.ExpiresOn(Jwt($$"""{"aud":"https://management.azure.com","exp":{{expires}}}"""))
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(expires));

        // An opaque or malformed token is not an error: the caller simply learns nothing about expiry.
        TokenResource.ExpiresOn("opaque").Should().BeNull();
        TokenResource.ExpiresOn(Jwt("""{"aud":"x"}""")).Should().BeNull();
        TokenResource.ExpiresOn(Jwt("not json")).Should().BeNull();
        TokenResource.ExpiresOn(string.Empty).Should().BeNull();
    }

    [Fact]
    public void TheKubectlFormatIsAnExecCredential()
    {
        // The shape kubectl's exec plugin contract requires; the serializer options are the CLI's own.
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var json = JsonSerializer.Serialize(
            new
            {
                apiVersion = "client.authentication.k8s.io/v1",
                kind = "ExecCredential",
                status = new { expirationTimestamp = expires, token = "abc" },
            },
            Output.JsonOptions);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("apiVersion").GetString().Should().Be("client.authentication.k8s.io/v1");
        document.RootElement.GetProperty("kind").GetString().Should().Be("ExecCredential");
        document.RootElement.GetProperty("status").GetProperty("token").GetString().Should().Be("abc");
        document.RootElement.GetProperty("status").GetProperty("expirationTimestamp").GetDateTimeOffset().Should().Be(expires);
    }

    [Fact]
    public async Task AChildSeesTheExportedVariableAndNothingElseDoes()
    {
        const string name = "ELEVATE_ARM_TOKEN";
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? (CommandLauncher.Resolve("cmd.exe")!, new[] { "/c", $"if \"%{name}%\"==\"secret\" (exit 0) else (exit 4)" })
            : (CommandLauncher.Resolve("sh")!, new[] { "-c", $"test \"${name}\" = secret" });

        var exports = new Dictionary<string, string> { [name] = "secret" };
        (await CommandLauncher.RunAsync(executable, arguments, exports)).Should().Be(0);

        // Without it the same command fails, and elevate's own environment was never touched.
        (await CommandLauncher.RunAsync(executable, arguments)).Should().NotBe(0);
        Environment.GetEnvironmentVariable(name).Should().BeNull();
    }

    // MARK: Through the command tree

    [Fact]
    public async Task AGraphTokenIsRefusedUntilTheCallerAcknowledgesTheCeiling()
    {
        using var session = new TestSession();
        var (code, output, error) = await RunAsync(session, "token", "--resource", "graph");
        code.Should().Be(ExitCodes.Usage);
        output.Should().BeEmpty();
        error.Should().Contain("RoleAssignmentSchedule.ReadWrite.Directory").And.Contain("--i-know");
    }

    [Fact]
    public async Task TokenActivatesNothingAndNeedsAnAccount()
    {
        using var session = new TestSession();
        session.Session.State.Identities.Clear();
        session.Session.Persist();

        var (code, output, _) = await RunAsync(session, "token", "--resource", "arm");
        code.Should().Be(ExitCodes.SignInRequired);
        output.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunThatCouldNotExportItsTokenActivatesNothing()
    {
        using var session = new TestSession();
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

        var (code, _, error) = await RunAsync(session, "run", "--role", "Reader", "--export-token", "nonsense", "--", command);
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("is not a resource");

        (code, _, error) = await RunAsync(session, "run", "--role", "Reader", "--export-token", "graph", "--", command);
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("--i-know");

        session.Entra.Activated.Should().BeEmpty("a bad --export-token must not cost an activation");
    }

    private static string Jwt(string payload) =>
        "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    /// <summary>Runs the real command tree against the test session's directory, capturing both streams.</summary>
    private static async Task<(int Code, string Out, string Err)> RunAsync(TestSession session, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main([.. args, "--no-color", "--data-dir", session.Directory]);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
