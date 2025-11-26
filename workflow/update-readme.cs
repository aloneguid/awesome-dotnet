#:package Octokit@13.0.1

using System.Text;
using System.Text.RegularExpressions;
using Octokit;

// Get environment variables
string token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
string repositoryEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

if (string.IsNullOrEmpty(token)) {
    Console.WriteLine("Error: GITHUB_TOKEN environment variable is not set");
    return 1;
}

if (string.IsNullOrEmpty(repositoryEnv)) {
    Console.WriteLine("Error: GITHUB_REPOSITORY environment variable is not set");
    return 1;
}

var repoParts = repositoryEnv.Split('/');
if (repoParts.Length != 2) {
    Console.WriteLine($"Error: Invalid GITHUB_REPOSITORY format: {repositoryEnv}");
    return 1;
}

var owner = repoParts[0];
var repo = repoParts[1];

Console.WriteLine($"Processing repository: {owner}/{repo}");

try {
    await UpdateReadmeWithPopularIssues(token, owner, repo);
    return 0;
} catch (Exception ex) {
    Console.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task UpdateReadmeWithPopularIssues(string token, string owner, string repo) {
    var client = new GitHubClient(new ProductHeaderValue("UpdateReadme")) {
        Credentials = new Credentials(token)
    };

    // Get all open issues
    var issueRequest = new RepositoryIssueRequest {
        State = ItemStateFilter.Open
    };

    var issues = await client.Issue.GetAllForRepository(owner, repo, issueRequest);
    Console.WriteLine($"Found {issues.Count} open issues");

    // Filter issues with at least 5 thumbs up reactions or owner approval
    var popularIssues = new List<PopularIssue>();

    foreach (var issue in issues) {
        // Skip pull requests
        if (issue.PullRequest != null) continue;

        // Get reactions for the issue
        var reactions = await client.Reaction.Issue.GetAll(owner, repo, issue.Number);

        // Count thumbs up reactions and check for owner approval
        var thumbsUpReactions = reactions.Where(r => r.Content == ReactionType.Plus1).ToList();
        var thumbsUp = thumbsUpReactions.Count;
        var ownerApproved = thumbsUpReactions.Any(r => r.User.Login.Equals(owner, StringComparison.OrdinalIgnoreCase));

        // Include if owner approved or has at least 5 likes
        if (ownerApproved || thumbsUp >= 5) {
            popularIssues.Add(new PopularIssue(issue.Title, issue.Body, issue.Number));
        }
    }

    Console.WriteLine($"Found {popularIssues.Count} popular issues with 5+ likes or owner approval");

    // Read current README
    var readmePath = "README.md";
    var readme = await File.ReadAllTextAsync(readmePath);

    // Check if there's a section for awesome links
    const string sectionMarker = "# Links";
    const string startMarker = "<!-- AWESOME-LINKS-START -->";
    const string endMarker = "<!-- AWESOME-LINKS-END -->";

    // Build the new links section grouped by category
    var linksSection = BuildLinksSection(popularIssues, sectionMarker, startMarker, endMarker);

    // Update README
    if (readme.Contains(startMarker) && readme.Contains(endMarker)) {
        // Replace existing section
        var pattern = $@"{Regex.Escape(sectionMarker)}\n\n{Regex.Escape(startMarker)}[\s\S]*?{Regex.Escape(endMarker)}\n?";
        var regex = new Regex(pattern);
        var match = regex.Match(readme);

        if (match.Success) {
            Console.WriteLine("--- Existing section to be replaced ---");
            Console.WriteLine(match.Value);
            Console.WriteLine("--- End of existing section ---\n");
        } else {
            Console.WriteLine("WARNING: Primary regex did not match existing section. Trying alternative pattern...");
            // Try alternative regex with more flexible whitespace matching
            var altPattern = $@"{Regex.Escape(sectionMarker)}\s*\n+\s*{Regex.Escape(startMarker)}[\s\S]*?{Regex.Escape(endMarker)}\n?";
            regex = new Regex(altPattern);
            match = regex.Match(readme);
            if (match.Success) {
                Console.WriteLine("Found match with alternative regex");
                Console.WriteLine("--- Existing section to be replaced ---");
                Console.WriteLine(match.Value);
                Console.WriteLine("--- End of existing section ---\n");
            } else{
                Console.WriteLine("ERROR: No regex matched the existing section. Links section may not be updated correctly.");
            }
        }

        // Remove leading newline for replacement
        readme = regex.Replace(readme, linksSection.TrimStart('\n'));
    } else if (!string.IsNullOrEmpty(linksSection)){
        Console.WriteLine("No existing markers found. Appending new section.");
        // Append new section
        readme = readme.TrimEnd() + "\n" + linksSection;
    }

    // Write updated README
    await File.WriteAllTextAsync(readmePath, readme);

    // Log the result
    Console.WriteLine($"Found {popularIssues.Count} popular issues with 5+ likes");
    foreach (var issue in popularIssues) {
        Console.WriteLine($"  - #{issue.Number}: {issue.Title}");
    }
}

static string BuildLinksSection(List<PopularIssue> popularIssues, string sectionMarker, string startMarker, string endMarker) {
    if (popularIssues.Count == 0) {
        return string.Empty;
    }

    // Parse all issues and group by category
    var linksByCategory = new Dictionary<string, List<LinkEntry>>();

    foreach (var issue in popularIssues) {
        var parsed = ParseIssueBody(issue.Body);

        Console.WriteLine($"Processing issue #{issue.Number}: \"{issue.Title}\"");
        Console.WriteLine($"  URL: {parsed.Url ?? "(none)"}");
        Console.WriteLine($"  Description: {parsed.Description ?? "(none)"}");
        Console.WriteLine($"  Category: {parsed.Category ?? "(none)"}");

        if (!string.IsNullOrEmpty(parsed.Url)) {
            // Use issue title, clean up [Link] prefix if present
            var title = Regex.Replace(issue.Title, @"^\[Link\]\s*", "", RegexOptions.IgnoreCase).Trim();

            var category = parsed.Category ?? "Other";
            if (!linksByCategory.ContainsKey(category)) {
                linksByCategory[category] = new List<LinkEntry>();
            }

            linksByCategory[category].Add(new LinkEntry(title, parsed.Url, parsed.Description));
            Console.WriteLine($"  -> Added to category \"{category}\"");
        } else {
            Console.WriteLine("  -> SKIPPED: No URL found");
        }
    }

    // Define category order
    var categoryOrder = new[] { "Libraries", "Frameworks", "Tools", "Documentation", "Other" };

    // Log categories found vs. expected
    var foundCategories = linksByCategory.Keys.ToList();
    Console.WriteLine($"\nCategories found in issues: {(foundCategories.Count > 0 ? string.Join(", ", foundCategories) : "(none)")}");
    Console.WriteLine($"Expected category order: {string.Join(", ", categoryOrder)}");

    var additionalCategories = foundCategories.Where(c => !categoryOrder.Contains(c)).ToList();
    if (additionalCategories.Count > 0) {
        Console.WriteLine($"WARNING: Categories not in predefined order (will be added at the end): {string.Join(", ", additionalCategories)}");
    }

    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine(sectionMarker);
    sb.AppendLine();
    sb.AppendLine(startMarker);

    foreach (var category in categoryOrder) {
        if (linksByCategory.TryGetValue(category, out var links) && links.Count > 0) {
            sb.AppendLine();
            sb.AppendLine($"## {category}");
            sb.AppendLine();
            // Sort links alphabetically by title (case-insensitive)
            foreach (var link in links.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase)) {
                sb.Append($"- [{link.Title}]({link.Url})");
                if (!string.IsNullOrEmpty(link.Description)) {
                    sb.Append($" - {link.Description}");
                }
                sb.AppendLine();
            }
        }
    }

    // Also add any categories that were not in the predefined order
    foreach (var category in additionalCategories) {
        if (linksByCategory.TryGetValue(category, out var links) && links.Count > 0) {
            Console.WriteLine($"Adding additional category \"{category}\" to output");
            sb.AppendLine();
            sb.AppendLine($"## {category}");
            sb.AppendLine();
            // Sort links alphabetically by title (case-insensitive)
            foreach (var link in links.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase)) {
                sb.Append($"- [{link.Title}]({link.Url})");
                if (!string.IsNullOrEmpty(link.Description)) {
                    sb.Append($" - {link.Description}");
                }
                sb.AppendLine();
            }
        }
    }

    sb.AppendLine();
    sb.AppendLine(endMarker);

    var result = sb.ToString();

    // Print the replacement content for diagnostic purposes
    Console.WriteLine("\n--- Replacement content (linksSection) ---");
    Console.WriteLine(result);
    Console.WriteLine("--- End of replacement content ---\n");

    return result;
}

static ParsedIssue ParseIssueBody(string? body) {
    if (string.IsNullOrEmpty(body)) {
        return new ParsedIssue(null, null, null);
    }

    // Try to extract fields from issue template format
    // The template generates sections like "### URL\n\nvalue"
    var urlMatch = Regex.Match(body, @"### URL\s*\n+\s*([^\n]+)");
    var descMatch = Regex.Match(body, @"### Description\s*\n+\s*([\s\S]*?)(?=\n###|$)");
    var categoryMatch = Regex.Match(body, @"### Category\s*\n+\s*([^\n]+)");

    string? url = urlMatch.Success ? urlMatch.Groups[1].Value.Trim() : null;
    string? description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;
    string? category = categoryMatch.Success ? categoryMatch.Groups[1].Value.Trim() : "Other";

    // Fallback: find any URL in the body
    if (string.IsNullOrEmpty(url)) {
        var anyUrlMatch = Regex.Match(body, @"https?://[^\s)]+");
        url = anyUrlMatch.Success ? anyUrlMatch.Value : null;
    }

    // Fallback: use first non-empty, non-URL line as description
    if (string.IsNullOrEmpty(description)) {
        var lines = body.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) &&
                       !Regex.IsMatch(l, @"^https?://") &&
                       !l.StartsWith('#') &&
                       !l.StartsWith("###"));
        description = lines.FirstOrDefault();
    }

    return new ParsedIssue(url, description, category);
}

record PopularIssue(string Title, string? Body, int Number);
record ParsedIssue(string? Url, string? Description, string? Category);
record LinkEntry(string Title, string Url, string? Description);
