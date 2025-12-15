#:package Octokit@14.0.0
#:package CsvHelper@33.1.0
#:package Humanizer@3.0.1
#:property PublishTrimmed=false

using Octokit;
using CsvHelper;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Console;
using Humanizer;

const int minThumbsUp = 5;
const string csvDbPath = "links.csv";
const string jsonLogPath = "log.json";
const string feedPath = "feed.xml";
const string linksMarker = "<!-- auto-generated content below -->";
const string TableIntro = "Link|Rating|Description\n|-|-|-|\n";
string[] acronyms = ["YouTube", "CI/CD"];
var acronymMap = acronyms.ToDictionary(k => k.ToLower(), v => v);

GitHubClient client = CreateClient(out string owner, out string repo);
string wfEvent = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "unknown";
int? eventIssueNumber = GetEventIssueNumber();
WriteLine($"Created GitHub client for repository: '{owner}/{repo}'. Event: '{wfEvent}', issue id: '{eventIssueNumber}'");
await ProcessOpenIssues();
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

int? GetEventIssueNumber() {
    string? eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
    if (string.IsNullOrEmpty(eventPath) || !File.Exists(eventPath)) {
        return null;
    }

    string eventJson = File.ReadAllText(eventPath);
    
    // Parse JSON to get issue.number
    using var doc = JsonDocument.Parse(eventJson);
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
    bool shouldClose = thumbsUpCount >= minThumbsUp || ownerApproved;
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
        
        // Log link to JSON (append to log file)
        await LogLinkToJson(link);
        WriteLine($"  📝 Logged link to JSON log.");
        
        // Update RSS feed
        await LogLinkToFeed(link);
        WriteLine($"  📡 Updated RSS feed.");

        var sb = new StringBuilder();
        sb.Append("Thank you! The link suggestion is now merged, because ");

        sb.AppendLine(ownerApproved
            ? "it is approved by the repository owner."
            : $"community endorsement with {thumbsUpCount} 👍 reactions (≥ {minThumbsUp}).");

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
        string md = TableIntro + ToMarkdownLink(awl);
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

    return new AwesomeLink(issue.Title, url, description, category, subcategory, DateTime.UtcNow);
}

string Capitalize(string input) {
    input = input.Trim();
    string[] words = input.Split(" ", StringSplitOptions.RemoveEmptyEntries);
    var r = new List<string>();
    foreach (string word in words) {
        if (word.Length == 0)
            continue;
        
        // skipp all caps
        if(word == word.ToUpper()) {
            r.Add(word);
            continue;
        }
        
        if(acronymMap.TryGetValue(word.ToLower(), out string? acronym)) {    
            r.Add(acronym);
            continue;
        }
     
        // capitalize first letter, lowercase rest
        string lowerWord = word.ToLower();
        string capitalizedWord = char.ToUpper(lowerWord[0]) + lowerWord.Substring(1);
        r.Add(capitalizedWord);
    }
    
    return string.Join(' ', r);
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

    return Capitalize(category);
}

string SanitizeSubcategoryName(string subcategory) {
    // make sure it's camel cased
    return Capitalize(subcategory);
}

AwesomeLink SanitizeLink(AwesomeLink link) {
    return new AwesomeLink(
        link.Title.Trim(),
        Sanitize(link.Url, false, false),
        Sanitize(link.Description, true, true),
        SanitizeCategoryName(link.Category),
        SanitizeSubcategoryName(link.Subcategory),
        link.CreatedAt
    );
}

async Task SaveLinkToCsv(AwesomeLink newLink) {
    var allLinks = new List<AwesomeLink>();

    if (File.Exists(csvDbPath)) {
        using var reader = new StreamReader(csvDbPath);
        using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
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
    using var writer = new StreamWriter(csvDbPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
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

async Task LogLinkToJson(AwesomeLink link) {
    // serialize link to JSON line
    string jsonLine = JsonSerializer.Serialize(link, new JsonSerializerOptions { WriteIndented = false });
    
    // append to log file
    await File.AppendAllTextAsync(jsonLogPath, jsonLine + Environment.NewLine);
}

async Task LogLinkToFeed(AwesomeLink link) {
    const int maxFeedItems = 50;
    
    // Read all links from CSV and sort by CreatedAt descending
    List<AwesomeLink> allLinks = new List<AwesomeLink>();
    if (File.Exists(csvDbPath)) {
        using var reader = new StreamReader(csvDbPath);
        using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
        allLinks = csvReader.GetRecords<AwesomeLink>()
            .OrderByDescending(l => l.CreatedAt ?? DateTime.MinValue)
            .Take(maxFeedItems)
            .ToList();
    }
    
    // Build RSS feed
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
    sb.AppendLine("<rss version=\"2.0\">");
    sb.AppendLine("  <channel>");
    sb.AppendLine("    <title>Awesome .NET Links</title>");
    sb.AppendLine($"    <link>https://github.com/{owner}/{repo}</link>");
    sb.AppendLine("    <description>Community-curated awesome .NET resources and links</description>");
    sb.AppendLine($"    <lastBuildDate>{DateTime.UtcNow:R}</lastBuildDate>");
    
    foreach (var item in allLinks) {
        sb.AppendLine("    <item>");
        sb.AppendLine($"      <title>{System.Security.SecurityElement.Escape(item.Title)}</title>");
        sb.AppendLine($"      <link>{System.Security.SecurityElement.Escape(item.Url)}</link>");
        sb.AppendLine($"      <description>{System.Security.SecurityElement.Escape(item.Description)}</description>");
        sb.AppendLine($"      <category>{System.Security.SecurityElement.Escape(item.Category)}</category>");
        if (item.CreatedAt.HasValue) {
            sb.AppendLine($"      <pubDate>{item.CreatedAt.Value:R}</pubDate>");
        }
        sb.AppendLine("    </item>");
    }
    
    sb.AppendLine("  </channel>");
    sb.AppendLine("</rss>");
    
    await File.WriteAllTextAsync(feedPath, sb.ToString());
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
    return $"[{link.Title}]({link.Url})|{extras}|{link.Description}";
}

async Task RebuildReadme() {
    // Read all links from CSV
    List<AwesomeLink> allLinks = new List<AwesomeLink>();
    if (File.Exists(csvDbPath)) {
        using StreamReader reader = new StreamReader(csvDbPath);
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

    // Build Markdown using StringBuilder
    var sb = new StringBuilder();
    sb.AppendLine($"Enjoy awesome {"link".ToQuantity(allLinks.Count)} contributed by the community.");
    sb.AppendLine();
    
    // Build TOC
    sb.AppendLine("# Table of Contents");
    foreach (var category in grouped) {
        sb.AppendLine($"- [{category.Category}](#{category.Category.ToLower().Replace(' ', '-')})");
        foreach (var sub in category.Subgroups) {
            if (!string.IsNullOrWhiteSpace(sub.Subcategory)) {
                sb.AppendLine($"  - [{sub.Subcategory}](#{sub.Subcategory.ToLower().Replace(' ', '-')})");
            }
        }
    }
    
    // Build links (table style)
    foreach(var category in grouped) {
        sb.AppendLine();
        sb.AppendLine($"# {category.Category}");

        foreach(var sub in category.Subgroups) {
            sb.AppendLine();
            if(!string.IsNullOrWhiteSpace(sub.Subcategory)) {
                sb.AppendLine($"## {sub.Subcategory}");
                sb.AppendLine();
            }

            sb.Append(TableIntro);
            
            foreach(AwesomeLink link in sub.Links) {
                sb.AppendLine(ToMarkdownLink(link));
            }
        }
    }
    
    string generatedSection = sb.ToString().TrimEnd();
    // WriteLine("generated section:");
    // WriteLine(generatedSection);

    // Read README.md and replace '# Links' section using Markdig AST
    const string readmePath = "README.md";
    if (!File.Exists(readmePath)) {
        WriteLine("README.md not found; skipping rebuild.");
        return;
    }

    string readme = await File.ReadAllTextAsync(readmePath);

    // delete everything starting with the marker to the end of the file
    int idx = readme.IndexOf(linksMarker, StringComparison.Ordinal);
    if (idx >= 0) {
        readme = readme.Substring(0, idx + linksMarker.Length);
    } else {
        // If no marker found, append at the end
        readme += "\n\n" + linksMarker;
    }

    // append the generated section
    readme += "\n\n" + generatedSection + "\n";
    WriteLine("Writing updated README.md:");
    WriteLine(readme);
    await File.WriteAllTextAsync(readmePath, readme);
}

record AwesomeLink(string Title, string Url, string Description, string Category, string Subcategory, DateTime? CreatedAt);