#:package Octokit@13.0.1

using Octokit;
using static System.Console;

GitHubClient client = CreateClient(out string owner, out string repo);
WriteLine($"Authenticated as: {client.Credentials.Login}");


GitHubClient CreateClient(out string owner, out string repo) {

    // Get environment variables
    string token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    string repositoryEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

    if (string.IsNullOrEmpty(token))
        throw new InvalidOperationException("GITHUB_TOKEN environment variable is not set");
    
    if (string.IsNullOrEmpty(repositoryEnv))
        throw new InvalidOperationException("GITHUB_REPOSITORY environment variable is not set");

    var repoParts = repositoryEnv.Split('/');
    if (repoParts.Length != 2)
        throw new InvalidOperationException($"Invalid GITHUB_REPOSITORY format: {repositoryEnv}");

    owner = repoParts[0];
    repo = repoParts[1];

    var client = new GitHubClient(new ProductHeaderValue("UpdateReadme")) {
        Credentials = new Credentials(token)
    };

    return client;
}