#:package Octokit@13.0.1

using Octokit;
using static System.Console;

const int MinThumbsUp = 5;

GitHubClient client = CreateClient(out string owner, out string repo);
WriteLine($"Created GitHub client for repository: {owner}/{repo}");
await ProcessOpenIssues(client, owner, repo);

GitHubClient CreateClient(out string owner, out string repo) {

    // Get environment variables
    string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    string? repositoryEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

    if (string.IsNullOrEmpty(token))
        throw new InvalidOperationException("GITHUB_TOKEN environment variable is not set");
    
    if (string.IsNullOrEmpty(repositoryEnv))
        throw new InvalidOperationException("GITHUB_REPOSITORY environment variable is not set");

    string[] repoParts = repositoryEnv.Split('/');
    if (repoParts.Length != 2)
        throw new InvalidOperationException($"Invalid GITHUB_REPOSITORY format: {repositoryEnv}");

    owner = repoParts[0];
    repo = repoParts[1];

    var client = new GitHubClient(new ProductHeaderValue("UpdateReadme")) {
        Credentials = new Credentials(token)
    };

    return client;
}

async Task ProcessOpenIssues(GitHubClient client, string owner, string repo) {
    IReadOnlyList<Issue> issues = await client.Issue.GetAllForRepository(owner, repo, new RepositoryIssueRequest {
        State = ItemStateFilter.Open
    });

    foreach (Issue issue in issues) {
        // Skip pull requests
        if (issue.PullRequest != null) {
            WriteLine($"Skipping issue #{issue.Number}: is a pull request");
            continue;
        }

        // Skip issues without the "link" label (case-insensitive)
        bool hasLinkLabel = issue.Labels.Any(l => l.Name.Equals("link", StringComparison.OrdinalIgnoreCase));
        if (!hasLinkLabel) {
            WriteLine($"Skipping issue #{issue.Number}: missing 'link' label");
            continue;
        }

        WriteLine($"Issue #{issue.Number}: {issue.Title}");
        IReadOnlyList<Reaction> reactions = await client.Reaction.Issue.GetAll(owner, repo, issue.Number);
        List<Reaction> thumbsUpReactions = reactions.Where(r => r.Content == ReactionType.Plus1).ToList();
        int thumbsUpCount = thumbsUpReactions.Count;
        bool ownerApproved = thumbsUpReactions.Any(r => r.User.Login.Equals(owner, StringComparison.OrdinalIgnoreCase));
        bool shouldClose = thumbsUpCount >= MinThumbsUp || ownerApproved;

        WriteLine($"              👍: {thumbsUpCount}");
        WriteLine($"  Owner approved: {ownerApproved}");
        WriteLine($"    Should close: {shouldClose}");
    }
}