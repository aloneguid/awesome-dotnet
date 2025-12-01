#:package Octokit@14.0.0
#:package CsvHelper@33.1.0
#:package Humanizer@3.0.1

using Octokit;
using CsvHelper;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Console;
using Humanizer;

const int MinThumbsUp = 5;
const string CsvDBPath = "links.csv";
const string LinksMarker = "<!-- auto-generated content below -->";

GitHubClient client = CreateClient(out string owner, out string repo);
string wfEvent = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "unknown";
int? eventIssueNumber = GetEventIssueNumber();
WriteLine($"Created GitHub client for repository: '{owner}/{repo}'. Event: '{wfEvent}', issue id: '{eventIssueNumber}'");
await ProcessOpenIssues();
await RebuildReadme();
await GenerateHugoContent();

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

int? GetEventIssueNumber() {
    string? eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
    if (string.IsNullOrEmpty(eventPath) || !File.Exists(eventPath)) {
        return null;
    }

    string eventJson = File.ReadAllText(eventPath);
    
    // Parse JSON to get issue.number
    using JsonDocument doc = JsonDocument.Parse(eventJson);
    if (doc.RootElement.TryGetProperty("issue", out JsonElement issueElement) &&
        issueElement.TryGetProperty("number", out JsonElement numberElement)) {
        return numberElement.GetInt32();
    }
    
    return null;
}

async Task ProcessOpenIssue(Issue issue) {
    IReadOnlyList<Reaction> reactions = await client.Reaction.Issue.GetAll(owner, repo, issue.Number);
    List<Reaction> thumbsUpReactions = reactions.Where(r => r.Content == ReactionType.Plus1).ToList();
    int thumbsUpCount = thumbsUpReactions.Count;
    bool ownerApproved = thumbsUpReactions.Any(r => r.User.Login.Equals(owner, StringComparison.OrdinalIgnoreCase));
    bool shouldClose = thumbsUpCount >= MinThumbsUp || ownerApproved;
    WriteLine($"  +1 reactions: {thumbsUpCount}, owner approved: {ownerApproved}, should close: {shouldClose}");

    if (shouldClose) {
        AwesomeLink? link = ToAwesomeLink(issue);
        if (link == null) {
            WriteLine($"  ❌ Unable to parse issue #{issue.Number} body to extract link information. Skipping closure.");
            return;
        }

        // Write link to CSV before closing (deduplicated by URL)
        await SaveLinkToCsv(link);
        WriteLine($"  💾 Saved link to CSV: {link.Url}");

        var sb = new StringBuilder();
        sb.Append("Thank you! The link suggestion is now merged, because ");

        sb.AppendLine(ownerApproved
            ? "it is approved by the repository owner."
            : $"community endorsement with {thumbsUpCount} 👍 reactions (≥ {MinThumbsUp}).");

        sb.AppendLine();
        
        sb.AppendLine("You can also make changes in future by editing the issue and re-opening it.");

        // Add a public comment explaining why the issue is being closed
        await client.Issue.Comment.Create(owner, repo, issue.Number, sb.ToString());
        
        // Close the issue
        var update = new IssueUpdate { State = ItemState.Closed };
        await client.Issue.Update(owner, repo, issue.Number, update);

        WriteLine($"✔️ posted comment and closed.");
    }
}

async Task ProcessIssueUpdatesIfUpdated(Issue issue) {
    if(wfEvent != "issues" || eventIssueNumber != issue.Number)
        return;

    WriteLine($"  processing updates for issue #{issue.Number} due to issue event.");

    var sb = new StringBuilder();
    // We are here if and only if the issue body (or title) is changed (created or updated).
    AwesomeLink? awl = ToAwesomeLink(issue);
    if(awl == null) {
        sb.Append("  ❌ Unable to parse issue body to extract link information. Please ensure the issue follows the template format.");
    }
    else {
        awl = SanitizeLink(awl);
        string md = ToMarkdownLink(awl);
        string titledMd = $"# {awl.Category}\n\n";
        if(!string.IsNullOrEmpty(awl.Subcategory)) {
            titledMd += $"## {awl.Subcategory}\n\n";
        }
        titledMd += md;

        sb.Append("Thanks! This is how the link will appear in the README:\n\n");
        sb.Append(titledMd);
        sb.Append("\n\n");

        sb.Append("If you need to make any changes, please update the issue body accordingly. ");
    }

    // post the comment
    await client.Issue.Comment.Create(owner, repo, issue.Number, sb.ToString());
}

async Task ProcessOpenIssues() {
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

        WriteLine($"> Issue #{issue.Number}: {issue.Title}");
        await ProcessIssueUpdatesIfUpdated(issue);
        await ProcessOpenIssue(issue);
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
    Match subcategoryMatch = Regex.Match(body, @"### Subcategory\s*\n+\s*([^\n]+)");

    string? url = urlMatch.Success ? urlMatch.Groups[1].Value.Trim() : null;
    string? description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;
    string category = categoryMatch.Success ? categoryMatch.Groups[1].Value.Trim() : "Other";
    string subcategory = subcategoryMatch.Success ? subcategoryMatch.Groups[1].Value.Trim() : "";

    if (url == null || description == null)
        return null;

    if(subcategory == "_No response_" || subcategory.ToLower() == "none" || subcategory == "-")
        subcategory = "";

    return new AwesomeLink(issue.Title, url, description, category, subcategory);
}

string Sanitize(string input, bool capitalize = true, bool endWithFullStop = false) {
    input = input.Trim();
    if (capitalize && input.Length > 0) {
        input = char.ToUpper(input[0]) + input.Substring(1);
    }
    if (endWithFullStop && !input.EndsWith('.')) {
        input += ".";
    }
    return input;
}

string SanitizeCategoryName(string category) {
    // make sure it's camel cased
    category = category.Trim();
    if(string.IsNullOrEmpty(category))
        return "Other";
    
    return category.Titleize();
}

string SanitizeSubcategoryName(string subcategory) {
    // make sure it's camel cased
    return subcategory.Trim().Titleize();
}

AwesomeLink SanitizeLink(AwesomeLink link) {
    return new AwesomeLink(
        Sanitize(link.Title),
        Sanitize(link.Url, false, false),
        Sanitize(link.Description, true, true),
        SanitizeCategoryName(link.Category),
        SanitizeSubcategoryName(link.Subcategory)
    );
}

async Task SaveLinkToCsv(AwesomeLink newLink) {
    List<AwesomeLink> allLinks = new List<AwesomeLink>();

    if (File.Exists(CsvDBPath)) {
        using StreamReader reader = new StreamReader(CsvDBPath);
        using CsvReader csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        List<AwesomeLink> existing = csvReader.GetRecords<AwesomeLink>().ToList();
        allLinks.AddRange(existing.Select(SanitizeLink));
    }
    newLink = SanitizeLink(newLink);

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
    IOrderedEnumerable<AwesomeLink> orderedLinks = allLinks
        .OrderBy(l => l.Category)
        .ThenBy(l => l.Subcategory)
        .ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase);
    foreach (AwesomeLink link in orderedLinks) {
        csvWriter.WriteRecord(link);
        csvWriter.NextRecord();
    }
    await writer.FlushAsync();
}

string AddLinkExtras(string url) {

    if(url.Contains("github.com")) {
        // get repo owner and name from url
        Match match = Regex.Match(url, @"github\.com/([^/]+)/([^/]+)");
        if (match.Success) {
            string repoOwner = match.Groups[1].Value;
            string repoName = match.Groups[2].Value;
            return $" ![stars](https://img.shields.io/github/stars/{repoOwner}/{repoName}?style=social&label=)";
        }
    }

    return "";
}

string ToMarkdownLink(AwesomeLink link) {
    string extras = AddLinkExtras(link.Url);
    return $"- [{link.Title}{extras}]({link.Url}) - {link.Description}";
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
        lines.Add($"# {category.Category}");
        foreach (var sub in category.Subgroups) {
            if (!string.IsNullOrWhiteSpace(sub.Subcategory)) {
                lines.Add("");
                lines.Add($"## {sub.Subcategory}");
            } else {
                lines.Add("");
            }
            foreach (AwesomeLink link in sub.Links) {
                lines.Add(ToMarkdownLink(link));
            }
        }
    }
    string generatedSection = string.Join("\n", lines);
    // WriteLine("generated section:");
    // WriteLine(generatedSection);

    // Read README.md and replace '# Links' section using Markdig AST
    const string ReadmePath = "README.md";
    if (!File.Exists(ReadmePath)) {
        WriteLine("README.md not found; skipping rebuild.");
        return;
    }

    string readme = await File.ReadAllTextAsync(ReadmePath);

    // delete everything starting with the marker to the end of the file
    int idx = readme.IndexOf(LinksMarker);
    if (idx >= 0) {
        readme = readme.Substring(0, idx + LinksMarker.Length);
    } else {
        // If no marker found, append at the end
        readme += "\n\n" + LinksMarker;
    }

    // append the generated section
    readme += "\n\n" + generatedSection + "\n";
    WriteLine("Writing updated README.md:");
    WriteLine(readme);
    await File.WriteAllTextAsync(ReadmePath, readme);
}

async Task GenerateHugoContent() {
    WriteLine("Generating Hugo content from CSV...");
    
    // Read all links from CSV
    List<AwesomeLink> allLinks = new List<AwesomeLink>();
    if (File.Exists(CsvDBPath)) {
        using StreamReader reader = new StreamReader(CsvDBPath);
        using CsvReader csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        List<AwesomeLink> existing = csvReader.GetRecords<AwesomeLink>().ToList();
        allLinks.AddRange(existing);
    }

    // Clear and recreate the Hugo content directory
    const string ContentDir = "content";
    if (Directory.Exists(ContentDir)) {
        Directory.Delete(ContentDir, true);
    }
    Directory.CreateDirectory(ContentDir);

    // Group links by Category
    var grouped = allLinks
        .GroupBy(l => string.IsNullOrWhiteSpace(l.Category) ? "Other" : l.Category)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    int weight = 1;
    foreach (var category in grouped) {
        // Create a markdown file for each category
        string fileName = $"{weight:D2}-{SlugifyCategory(category.Key)}.md";
        string filePath = Path.Combine(ContentDir, fileName);

        var sb = new StringBuilder();
        
        // Hugo front matter
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{category.Key}\"");
        sb.AppendLine($"weight: {weight}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Group by subcategory within this category
        var subgroups = category
            .GroupBy(x => x.Subcategory ?? "")
            .OrderBy(sg => sg.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var sub in subgroups) {
            if (!string.IsNullOrWhiteSpace(sub.Key)) {
                sb.AppendLine($"### {sub.Key}");
                sb.AppendLine();
            }

            foreach (var link in sub.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)) {
                sb.AppendLine(ToMarkdownLink(link));
            }
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString());
        WriteLine($"  Created: {fileName}");
        weight++;
    }

    WriteLine($"Hugo content generation complete. Generated {weight - 1} category pages.");
}

string SlugifyCategory(string category) {
    // Convert category name to URL-friendly slug
    string slug = category.ToLowerInvariant();
    slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
    slug = Regex.Replace(slug, @"\s+", "-");
    slug = Regex.Replace(slug, @"-+", "-");
    return slug.Trim('-');
}

record AwesomeLink(string Title, string Url, string Description, string Category, string Subcategory);