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
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: crawler.exe <owner> <repo>");
            return;
        }

        string owner = args[0];
        string repo = args[1];

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Environment variable GITHUB_TOKEN is missing.");
            return;
        }

        string bugCsv   = "bugs.csv";
        string otherCsv = "everything_else.csv";
        string allCsv   = "all.csv";

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
        int total=0, bugCount=0, otherCount=0;
        bool hasNextPage = true;
        while (hasNextPage)
        {
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

            var payload = new
            {
                query = query,
                variables = new { owner = owner, repo = repo, cursor = cursor }
            };

            var content   = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response  = await http.PostAsync("https://api.github.com/graphql", content);
            string json   = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var issues    = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("issues");
            hasNextPage   = issues.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
            cursor        = issues.GetProperty("pageInfo").GetProperty("endCursor").GetString();
            foreach (var issue in issues.GetProperty("nodes").EnumerateArray())
            {
                int number = issue.GetProperty("number").GetInt32();
                DateTime created = issue.GetProperty("createdAt").GetDateTime();

                string closed = "";
                if (issue.GetProperty("closedAt").ValueKind != JsonValueKind.Null)
                    closed = issue.GetProperty("closedAt").GetDateTime().ToString("o");

                bool isBug = false;
                foreach (var label in issue.GetProperty("labels").GetProperty("nodes").EnumerateArray())
                {
                    var labelName = label.GetProperty("name").GetString();
                    if (labelName != null && labelName.Equals("bug", StringComparison.OrdinalIgnoreCase))
                    {
                        isBug = true;
                        break;
                    }
                }

                allWriter.WriteLine($"{number},{created:o},{closed}");
                ++total;
                if (isBug)
                {
                    bugWriter.WriteLine($"{number},{created:o},{closed}");
                    ++bugCount;
                }
                else
                {
                    otherWriter.WriteLine($"{number},{created:o},{closed}");
                    ++otherCount;
                }
            }
            Console.WriteLine($"Processed {total} issues so far ({bugCount} bugs, {otherCount} others)...");
        }
        Console.WriteLine($"Done. Bugs: {bugCount}, Others: {otherCount}, Total: {total}");
    }
}