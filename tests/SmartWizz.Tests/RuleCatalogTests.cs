namespace SmartWizz.Tests;

public sealed class RuleCatalogTests
{
    const string Sample = """
        {
          "schemaVersion": 1,
          "version": "1.2.3",
          "lines": [
            { "text": "# Example" },
            { "rule": { "id": "standard-cs-null", "section": "1.3",
              "requirement": "Validate nullable arguments", "appliesTo": ["*.cs"],
              "detection": "llm", "severity": "high" } },
            { "rule": { "id": "standard-ts-alias", "section": "9.TypeScript",
              "requirement": "Use @/ imports", "appliesTo": ["*.ts", "*.tsx"],
              "detection": "llm", "severity": "low",
              "badExample": "import x from '../../x'", "goodExample": "import x from '@/x'",
              "exceptions": ["Third-party modules"] } },
            { "rule": { "id": "standard-manual", "section": "11",
              "requirement": "Follow review process", "appliesTo": [],
              "detection": "manual", "severity": "medium" } },
            { "rule": { "id": "standard-static", "section": "9",
              "requirement": "Compile with nullability", "appliesTo": ["*.cs"],
              "detection": "static", "severity": "medium" } }
          ]
        }
        """;

    [Fact]
    public void Selects_only_llm_rules_applicable_to_batch_files()
    {
        var catalog = RuleCatalog.Parse(Sample);
        Assert.Equal("1.2.3", catalog.Version);
        Assert.Equal(64, catalog.Sha256.Length);
        Assert.Equal(["standard-cs-null"], catalog.Select(["src/Calc.cs"]).Select(rule => rule.Id));
        Assert.Equal(["standard-ts-alias"], catalog.Select(["web/page.tsx"]).Select(rule => rule.Id));
        Assert.Equal(2, catalog.Select(["src/Calc.cs", "web/page.ts"]).Count);
        Assert.Empty(catalog.Select(["README.md"]));
        Assert.Equal(catalog.Sha256, RuleCatalog.Parse(Sample).Sha256);
    }

    [Fact]
    public void Selected_prompt_contains_ids_examples_and_exceptions_not_irrelevant_rules()
    {
        var catalog = RuleCatalog.Parse(Sample);
        string prompt = RuleCatalog.BuildPrompt("BASE", catalog.Select(["web/app.ts"]));
        Assert.Contains("standard-ts-alias", prompt);
        Assert.Contains("import x from '../../x'", prompt);
        Assert.Contains("Third-party modules", prompt);
        Assert.DoesNotContain("standard-cs-null", prompt);
        Assert.DoesNotContain("standard-manual", prompt);
        Assert.DoesNotContain("standard-static", prompt);

        string noRules = RuleCatalog.BuildPrompt("BASE", catalog.Select(["README.md"]));
        Assert.Contains("Do not emit standard-*", noRules);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [InlineData("\"version\": \"1.2.3\"", "\"version\": \"\"")]
    [InlineData("\"detection\": \"llm\"", "\"detection\": \"unknown\"")]
    [InlineData("\"severity\": \"high\"", "\"severity\": \"urgent\"")]
    [InlineData("\"id\": \"standard-cs-null\"", "\"id\": \"not-standard\"")]
    public void Rejects_invalid_catalog_metadata(string from, string to)
    {
        Assert.Throws<SmartWizzException>(() => RuleCatalog.Parse(Sample.Replace(from, to)));
    }

    [Fact]
    public void Repository_catalog_loads_and_rejects_unrelated_markdown_rules()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string rulesPath = Path.Combine(root, "rules", "engineering-rules.json");
        var catalog = RuleCatalog.Load(rulesPath);

        Assert.Equal(172, catalog.Rules.Count);
        Assert.Empty(catalog.Select(["README.md"]));
        Assert.Contains(catalog.Select(["src/Code.cs"]), rule => rule.Id == "standard-4-1-05");
        Assert.Contains(catalog.Select(["web/src/App.tsx"]), rule => rule.Id == "standard-9-ts-08");
        Assert.DoesNotContain(catalog.Select(["src/Code.cs"]), rule => rule.Id == "standard-9-ts-08");
    }

    [Fact]
    public void Rejects_duplicate_ids()
    {
        string duplicate = Sample.Replace("\"standard-ts-alias\"", "\"standard-cs-null\"");
        Assert.Throws<SmartWizzException>(() => RuleCatalog.Parse(duplicate));
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        Assert.Throws<SmartWizzException>(() => RuleCatalog.Parse("{"));
    }

    [Fact]
    public void Rejects_unknown_and_inapplicable_standard_ids_but_keeps_generic_bug_findings()
    {
        var files = GitDiff.Parse("""
            diff --git a/src/Calc.cs b/src/Calc.cs
            --- a/src/Calc.cs
            +++ b/src/Calc.cs
            @@ -0,0 +1,1 @@
            +bad
            """).ToDictionary(file => file.Path);
        string Finding(string id) => System.Text.Json.JsonSerializer.Serialize(new
        {
            findings = new[] { new { ruleId = id, severity = "high", title = "Issue",
                message = "Fix this", filePath = "src/Calc.cs", startLine = 1, confidence = 0.9 } }
        });

        var catalog = RuleCatalog.Parse(Sample);
        var allowed = catalog.Select(["src/Calc.cs"]).Select(rule => rule.Id).ToHashSet();
        Assert.Single(Review.Parse(Finding("standard-cs-null"), files, 0.5, [], allowed));
        Assert.Single(Review.Parse(Finding("null-dereference"), files, 0.5, [], allowed));
        var unknown = new List<string>();
        Assert.Empty(Review.Parse(Finding("standard-made-up"), files, 0.5, unknown, allowed));
        Assert.Contains("unknown or inapplicable", Assert.Single(unknown));
        Assert.Empty(Review.Parse(Finding("standard-ts-alias"), files, 0.5, [], allowed));
        Assert.Empty(Review.Parse(Finding("standard-cs-null"), files, 0.5, [], new HashSet<string>()));
    }

    [Fact]
    public async Task Review_passes_batch_selected_rules_and_records_catalog_provenance()
    {
        List<FileDiff> files = GitDiff.Parse("""
            diff --git a/src/Calc.cs b/src/Calc.cs
            --- a/src/Calc.cs
            +++ b/src/Calc.cs
            @@ -0,0 +1,1 @@
            +bad
            """);
        const string reply = """
            {"findings":[
              {"ruleId":"standard-cs-null","severity":"high","title":"Issue","message":"Fix",
                "filePath":"src/Calc.cs","startLine":1,"confidence":0.9},
              {"ruleId":"standard-ts-alias","severity":"low","title":"Wrong","message":"Not applicable",
                "filePath":"src/Calc.cs","startLine":1,"confidence":0.9}
            ]}
            """;
        var stub = new StubHandler((_, _) => StubHandler.Chat(reply));
        var provider = new Provider(new HttpClient(stub),
            new ProviderConfig { Endpoint = "http://x/v1/chat/completions", Model = "m" }, null);
        var catalog = RuleCatalog.Parse(Sample);
        ReviewResult result = await Review.RunAsync(files, new ReviewConfig(), provider, "BASE", default, catalog);

        Assert.Single(result.Findings);
        Assert.Single(result.Rejected);
        Assert.Equal("standard-cs-null", result.Findings[0].RuleId);
        Assert.Equal("1.2.3", result.RulesetVersion);
        Assert.Equal(catalog.Sha256, result.RulesetSha256);
        Assert.True(result.SystemPromptCharacters > 0);
        Assert.True(result.UserPromptCharacters > 0);
        Assert.Null(result.PromptTokens); // stub provider does not report usage
        Assert.Contains("standard-cs-null", stub.Requests[0].Body);
        Assert.DoesNotContain("standard-ts-alias", stub.Requests[0].Body);
        string json = Report.ToJson(result);
        Assert.Contains("rulesetVersion", json);
        Assert.Contains("rulesetSha256", json);
    }
}
