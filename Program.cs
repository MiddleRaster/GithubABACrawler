using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: crawler.exe <owner> <repo> <oldestDate in yyyy-MM-dd format>");
            return;
        }
        DateTime oldest = DateTime.Parse(args[2]);
        string owner    = args[0];
        string repo     = args[1];
        string? token   = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Environment variable GITHUB_TOKEN is missing.");
            return;
        }

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
                    labels(first: 20) {
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
            {   // Check for GraphQL errors
                Console.WriteLine("GraphQL error:");
                Console.WriteLine(errs.ToString());
                return;
            }
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Null || data.GetProperty("repository").ValueKind == JsonValueKind.Null)
            {   // Check that data.repository is not null
                Console.WriteLine($"Repository '{owner}/{repo}' not found.");
                return;
            }
            var issues    = data.GetProperty("repository").GetProperty("issues");
            hasNextPage = issues.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            cursor        = issues.GetProperty("pageInfo").GetProperty("endCursor").GetString();
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

                bool isBug = false;
                foreach (var label in issue.GetProperty("labels").GetProperty("nodes").EnumerateArray())
                {
                    string? labelName = label.GetProperty("name").GetString();
                    if (labelName != null && labelName.Equals("bug", StringComparison.OrdinalIgnoreCase))
                    {
                        isBug = true;
                        ++bugs;
                        break;
                    }
                }
                if (isBug)
                    bugWriter.WriteLine($"{number},{created},{closed}");
                else
                    otherWriter.WriteLine($"{number},{created},{closed}");
            }
            Console.WriteLine($"Processed {total} issues so far ({bugs} bugs, {total-bugs} others)...");
        }
        Console.WriteLine($"Done. Bugs: {bugs}, Others: {total-bugs}, Total: {total}");
    }
}