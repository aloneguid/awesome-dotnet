#:package Octokit@14.0.0
#:package CsvHelper@33.1.0

using Octokit;
using CsvHelper;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static System.Console;

const int MinThumbsUp = 5;
const string CsvDBPath = "links.csv";
const string LinksHeader = "# Links";

GitHubClient client = CreateClient(out string owner, out string repo);
WriteLine($"Created GitHub client for repository: {owner}/{repo}");
await ProcessOpenIssues(client, owner, repo);
await RebuildReadme();

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

async Task ProcessOpenIssue(GitHubClient client, string owner, string repo, Issue issue) {
    IReadOnlyList<Reaction> reactions = await client.Reaction.Issue.GetAll(owner, repo, issue.Number);
    List<Reaction> thumbsUpReactions = reactions.Where(r => r.Content == ReactionType.Plus1).ToList();
    int thumbsUpCount = thumbsUpReactions.Count;
    bool ownerApproved = thumbsUpReactions.Any(r => r.User.Login.Equals(owner, StringComparison.OrdinalIgnoreCase));
    bool shouldClose = thumbsUpCount >= MinThumbsUp || ownerApproved;

    WriteLine($"              👍: {thumbsUpCount}");
    WriteLine($"  Owner approved: {ownerApproved}");
    WriteLine($"    Should close: {shouldClose}");

    if (shouldClose) {
        AwesomeLink? link = ToAwesomeLink(issue);
        if (link == null) {
            WriteLine($"❌ Unable to parse issue #{issue.Number} body to extract link information. Skipping closure.");
            return;
        }

        // Write link to CSV before closing (deduplicated by URL)
        await SaveLinkToCsv(link);
        WriteLine($"💾 Saved link to CSV: {link.Url}");

        string closeComment = "This issue is being closed because ";

        closeComment += ownerApproved
            ? "it is approved by the repository owner."
            : $"community endorsement with {thumbsUpCount} 👍 reactions (≥ {MinThumbsUp}).";

        // Add a public comment explaining why the issue is being closed
        await client.Issue.Comment.Create(owner, repo, issue.Number, closeComment);
        
        // Close the issue
        var update = new IssueUpdate { State = ItemState.Closed };
        await client.Issue.Update(owner, repo, issue.Number, update);

        WriteLine($"✔️ posted comment and closed.");
    }
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
        await ProcessOpenIssue(client, owner, repo, issue);
    }
}

AwesomeLink? ToAwesomeLink(Issue issue) {

    if(issue.Body == null)
        return null;

    string body = issue.Body;

    // Try to extract fields from issue template format
    // The template generates sections like "### URL\n\nvalue"
    Match urlMatch = Regex.Match(body, @"### URL\s*\n+\s*([^\n]+)");
    Match descMatch = Regex.Match(body, @"### Description\s*\n+\s*([\s\S]*?)(?=\n###|$)");
    Match categoryMatch = Regex.Match(body, @"### Category\s*\n+\s*([^\n]+)");

    string? url = urlMatch.Success ? urlMatch.Groups[1].Value.Trim() : null;
    string? description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;
    string? category = categoryMatch.Success ? categoryMatch.Groups[1].Value.Trim() : "Other";

    if (url == null || description == null || category == null)
        return null;

    return new AwesomeLink(issue.Title.Trim(), url.Trim(), description.Trim(), category.Trim(), "");
}

async Task SaveLinkToCsv(AwesomeLink newLink) {
    List<AwesomeLink> allLinks = new List<AwesomeLink>();

    if (File.Exists(CsvDBPath)) {
        using StreamReader reader = new StreamReader(CsvDBPath);
        using CsvReader csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        List<AwesomeLink> existing = csvReader.GetRecords<AwesomeLink>().ToList();
        allLinks.AddRange(existing);
    }

    // Remove any existing entry with the same URL (case-insensitive)
    allLinks = allLinks
        .Where(l => !l.Url.Equals(newLink.Url, StringComparison.OrdinalIgnoreCase))
        .ToList();

    // Add the new link
    allLinks.Add(newLink);

    // Write back to CSV (sorted by Title for stability)
    using StreamWriter writer = new StreamWriter(CsvDBPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    using CsvWriter csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
    csvWriter.WriteHeader<AwesomeLink>();
    csvWriter.NextRecord();
    foreach (AwesomeLink link in allLinks.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase)) {
        csvWriter.WriteRecord(link);
        csvWriter.NextRecord();
    }
    await writer.FlushAsync();
}

async Task RebuildReadme() {
    // Read all links from CSV
    List<AwesomeLink> allLinks = new List<AwesomeLink>();
    if (File.Exists(CsvDBPath)) {
        using StreamReader reader = new StreamReader(CsvDBPath);
        using CsvReader csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        List<AwesomeLink> existing = csvReader.GetRecords<AwesomeLink>().ToList();
        allLinks.AddRange(existing);
    }

    // Group links by Category and Subcategory; sort all alphabetically
    var grouped = allLinks
        .GroupBy(l => string.IsNullOrWhiteSpace(l.Category) ? "Other" : l.Category)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new {
            Category = g.Key,
            Subgroups = g.GroupBy(x => x.Subcategory ?? "")
                .OrderBy(sg => sg.Key, StringComparer.OrdinalIgnoreCase)
                .Select(sg => new {
                    Subcategory = sg.Key,
                    Links = sg.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList()
                }).ToList()
        }).ToList();

    // Build Markdown lines, then normalize output using Markdig's NormalizeRenderer
    List<string> lines = new List<string>();
    foreach (var category in grouped) {
        lines.Add("");
        lines.Add($"## {category.Category}");
        foreach (var sub in category.Subgroups) {
            if (!string.IsNullOrWhiteSpace(sub.Subcategory)) {
                lines.Add("");
                lines.Add($"### {sub.Subcategory}");
            } else {
                lines.Add("");
            }
            foreach (AwesomeLink link in sub.Links) {
                string descPart = string.IsNullOrWhiteSpace(link.Description) ? string.Empty : $" - {link.Description}";
                lines.Add($"- [{link.Title}]({link.Url}){descPart}");
            }
        }
    }
    string generatedSection = string.Join("\n", lines);
    WriteLine("generated section:");
    WriteLine(generatedSection);

    // Read README.md and replace '# Links' section using Markdig AST
    const string ReadmePath = "README.md";
    if (!File.Exists(ReadmePath)) {
        WriteLine("README.md not found; skipping rebuild.");
        return;
    }

    string readme = await File.ReadAllTextAsync(ReadmePath);

    // delete everything starting with "# Links" to the end of the file
    int idx = readme.IndexOf(LinksHeader);
    if (idx >= 0) {
        readme = readme.Substring(0, idx + LinksHeader.Length);
    } else {
        // If no "# Links" section found, append at the end
        readme += "\n\n" + LinksHeader;
    }

    // append the generated section
    readme += "\n\n" + generatedSection + "\n";
    WriteLine("Writing updated README.md");
    WriteLine(readme);
    await File.WriteAllTextAsync(ReadmePath, readme);
}

record AwesomeLink(string Title, string Url, string Description, string Category, string Subcategory);