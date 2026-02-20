using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

class Program
{
    static List<string> GetLabelNames(JsonElement issue)
    {
        var list = new List<string>();
        foreach (var label in issue.GetProperty("labels").GetProperty("nodes").EnumerateArray()) {
            string? name = label.GetProperty("name").GetString();
            if (name != null)
                list.Add(name);
        }
        return list;
    }
    static List<string> GetIssueTypeNames(JsonElement issue)
    {
        var list = new List<string>();
        var typeProp = issue.GetProperty("issueType");
        if (typeProp.ValueKind != JsonValueKind.Null) {
            string? name = typeProp.GetProperty("name").GetString();
            if (name != null)
                list.Add(name);
        }
        return list;
    }
    private static async Task DumpIssueTypes(HttpClient http, string owner, string repo)
    {
        Console.WriteLine($"=== IssueTypes for {owner}/{repo} ===");

        string query = 
    @"query($owner:String!, $repo:String!) {
      repository(owner:$owner, name:$repo) {
        issueTypes(first:50) {
          nodes { name }
        }
      }
    }";

        var payload = new { query, variables = new { owner, repo } };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp    = await http.PostAsync("https://api.github.com/graphql", content);
        resp.EnsureSuccessStatusCode();

        using var doc      = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var repoElem       = doc.RootElement.GetProperty("data").GetProperty("repository");
        var issueTypesProp = repoElem.GetProperty("issueTypes");
        if (issueTypesProp.ValueKind == JsonValueKind.Null)
        {
            Console.WriteLine("(This repository does not use issueTypes)");
            return;
        }
        foreach (var n in issueTypesProp.GetProperty("nodes").EnumerateArray())
            Console.WriteLine($"- {n.GetProperty("name").GetString()}");
    }
    static async Task DumpLabels(HttpClient http, string owner, string repo)
    {
        Console.WriteLine($"=== Labels for {owner}/{repo} ===");
        int page = 1;
        while (true)
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/labels?per_page=100&page={page}";
            var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.EnumerateArray().ToList();
            if (arr.Count == 0)
                break;
            foreach (var label in arr)
                Console.WriteLine($"- {label.GetProperty("name").GetString()}");
            page++;
        }
    }

    static async Task<bool> HandleDumpIfRequested(string[] args)
    {
        // Must have owner + repo + at least one more arg
        if (args.Length < 3)
            return false;

        // Look for -dump anywhere
        bool wantsDump = args.Any(a => a.Equals("-dump", StringComparison.OrdinalIgnoreCase));
        if (!wantsDump)
            return false;

        string owner  = args[0];
        string repo   = args[1];
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Environment variable GITHUB_TOKEN is missing.");
            return true;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add ( new ProductInfoHeaderValue("IssueCrawler", "1.0"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await DumpIssueTypes(http, owner, repo);
        await DumpLabels(http, owner, repo);

        return true; // tell Main to exit
    }

    public static async Task Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: crawler.exe <owner> <repo> <oldestDate in yyyy-MM-dd format>");
            Console.WriteLine("Or:    crawler.exe <owner> <repo> -dump");
            return;
        }

        if (await HandleDumpIfRequested(args))
            return;

        DateTime oldest = DateTime.Parse(args[2]);
        string owner    = args[0];
        string repo     = args[1];
        string? token   = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Environment variable GITHUB_TOKEN is missing.");
            return;
        }

        string[] bugTokens = args[3..];

        string   bugCsv = "bugs.csv";
        string otherCsv = "everything_else.csv";
        string   allCsv = "all.csv";

        using var   bugWriter = new StreamWriter(  bugCsv, false, Encoding.UTF8);
        using var otherWriter = new StreamWriter(otherCsv, false, Encoding.UTF8);
        using var   allWriter = new StreamWriter(  allCsv, false, Encoding.UTF8);

          bugWriter.WriteLine("id,createdAt,closedAt");
        otherWriter.WriteLine("id,createdAt,closedAt");
          allWriter.WriteLine("id,createdAt,closedAt");

        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IssueCrawler", "1.0"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string? cursor = null;
        int total=0, bugs=0;
        string query = @"
            query ($owner: String!, $repo: String!, $cursor: String) {
              repository(owner: $owner, name: $repo) {
                issues(
                  first: 100,
                  after: $cursor,
                  orderBy: { field: CREATED_AT, direction: DESC },
                  states: [OPEN, CLOSED]
                ) {
                  pageInfo {
                    hasNextPage
                    endCursor
                  }
                  nodes {
                    number
                    createdAt
                    closedAt
                    issueType { name }
                    labels(first: 100) {
                      nodes { name }
                    }
                  }
                }
              }
            }";
        bool hasNextPage = true;
        while (hasNextPage)
        {
            var payload = new
            {
                query = query,
                variables = new { owner = owner, repo = repo, cursor = cursor }
            };

            var content   = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response  = await http.PostAsync("https://api.github.com/graphql", content);
            string json   = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (root.TryGetProperty("errors", out var errs))
            {
                Console.WriteLine("GraphQL error:");
                Console.WriteLine(errs.ToString());
                return;
            }
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Null || data.GetProperty("repository").ValueKind == JsonValueKind.Null)
            {
                Console.WriteLine($"Repository '{owner}/{repo}' not found.");
                return;
            }

            var issues  = data.GetProperty("repository").GetProperty("issues");
            hasNextPage = issues.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            cursor      = issues.GetProperty("pageInfo").GetProperty("endCursor").GetString();

            foreach (var issue in issues.GetProperty("nodes").EnumerateArray())
            {
                int number = issue.GetProperty("number").GetInt32();

                DateTime createdAt = issue.GetProperty("createdAt").GetDateTime();
                if (createdAt < oldest) {
                    hasNextPage = false;
                    break;
                }
                string created = createdAt.ToString("yyyy-MM-dd HH:mm:ss");
                string closed  = "";
                if (issue.GetProperty("closedAt").ValueKind != JsonValueKind.Null)
                    closed     = issue.GetProperty("closedAt").GetDateTime().ToString("yyyy-MM-dd HH:mm:ss");

                allWriter.WriteLine($"{number},{created},{closed}");
                ++total;

                // NEW: bug classification using intersection
                if (GetLabelNames    (issue).Intersect(bugTokens, StringComparer.OrdinalIgnoreCase).Any() ||
                    GetIssueTypeNames(issue).Intersect(bugTokens, StringComparer.OrdinalIgnoreCase).Any() )
                {
                    bugWriter.WriteLine($"{number},{created},{closed}");
                    ++bugs;
                }
                else
                    otherWriter.WriteLine($"{number},{created},{closed}");
            }
            Console.WriteLine($"Processed {total} issues so far ({bugs} bugs, {total-bugs} others)...");
        }
        Console.WriteLine($"Done. Bugs: {bugs}, Others: {total-bugs}, Total: {total}");
    }
}