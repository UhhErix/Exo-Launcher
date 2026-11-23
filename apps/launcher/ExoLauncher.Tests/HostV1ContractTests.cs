using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed partial class HostV1ContractTests
{
    private static readonly HashSet<string> AnsweredBeforeSwitch = ["rpc.cancel"];

    private static string FindLauncherRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var manifest = Path.Combine(dir.FullName, "ui", "contracts", "host-v1.json");
            var bridge = Path.Combine(dir.FullName, "ExoLauncher", "Services", "WebHostBridge.cs");
            if (File.Exists(manifest) && File.Exists(bridge))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate apps/launcher root containing ui/contracts/host-v1.json and ExoLauncher/Services/WebHostBridge.cs.");
    }

    private static string? ExtractBalanced(string text, int openIndex)
    {
        var stack = new Stack<char>();
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i);
                if (i < 0) return null;
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (i < 0) return null;
                i++;
                continue;
            }
            if (ch is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != ch; i++)
                {
                    if (text[i] == '\\') i++;
                }
                continue;
            }
            if (ch == '{') stack.Push('}');
            else if (ch == '(') stack.Push(')');
            else if (ch == '[') stack.Push(']');
            else if (stack.Count > 0 && ch == stack.Peek())
            {
                stack.Pop();
                if (stack.Count == 0)
                    return text.Substring(openIndex + 1, i - openIndex - 1);
            }
        }
        return null;
    }

    private static List<string> SplitTopLevel(string body)
    {
        var items = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '/' && i + 1 < body.Length && body[i + 1] == '/')
            {
                var end = body.IndexOf('\n', i);
                i = end < 0 ? body.Length : end;
                continue;
            }
            if (ch == '/' && i + 1 < body.Length && body[i + 1] == '*')
            {
                var end = body.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? body.Length : end + 1;
                continue;
            }
            if (ch is '"' or '\'')
            {
                var start = i;
                for (i++; i < body.Length && body[i] != ch; i++)
                {
                    if (body[i] == '\\') i++;
                }
                var length = Math.Min(i + 1, body.Length) - start;
                current.Append(body, start, length);
                continue;
            }
            if (ch is '(' or '{' or '[') depth++;
            else if (ch is ')' or '}' or ']') depth--;
            if (ch == ',' && depth == 0)
            {
                var trimmed = LineCommentRegex().Replace(current.ToString(), "").Trim();
                if (trimmed.Length > 0) items.Add(trimmed);
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        var last = LineCommentRegex().Replace(current.ToString(), "").Trim();
        if (last.Length > 0) items.Add(last);
        return items;
    }

    internal static Dictionary<string, string> ExtractDispatchArms(string csSource)
    {
        var match = DispatchSwitchRegex().Match(csSource);
        Assert.True(match.Success, "WebHostBridge.cs must contain `object? result = method switch { ... }`.");
        var openBrace = csSource.IndexOf('{', match.Index);
        var body = ExtractBalanced(csSource, openBrace);
        Assert.NotNull(body);

        var arms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arm in SplitTopLevel(body!))
        {
            var armMatch = ArmRegex().Match(arm);
            if (!armMatch.Success) continue;
            var expr = armMatch.Groups[2].Value.Trim();
            foreach (Match label in LabelRegex().Matches(armMatch.Groups[1].Value))
                arms[label.Groups[1].Value] = expr;
        }
        return arms;
    }

    internal static bool IsRealHandler(string? expression) =>
        !string.IsNullOrEmpty(expression) && !NonHandlerRegex().IsMatch(expression);

    internal static bool IsStubHandler(string? expression)
    {
        var normalized = WhitespaceRegex().Replace(expression ?? "", " ").Trim();
        normalized = ConfigureAwaitSuffixRegex().Replace(normalized, "").Trim();
        if (normalized.StartsWith("await ", StringComparison.Ordinal))
            normalized = normalized["await ".Length..].Trim();
        return StubBoolRegex().IsMatch(normalized)
            || StubCompletedTaskRegex().IsMatch(normalized)
            || StubFromResultRegex().IsMatch(normalized)
            || StubOkObjectRegex().IsMatch(normalized)
            || StubOkMessageObjectRegex().IsMatch(normalized);
    }

    [Fact]
    public void EveryMethodInHostV1ManifestHasARealDispatcherArm()
    {
        var root = FindLauncherRoot();
        var manifestJson = File.ReadAllText(Path.Combine(root, "ui", "contracts", "host-v1.json"));
        var bridgeSource = File.ReadAllText(Path.Combine(root, "ExoLauncher", "Services", "WebHostBridge.cs")).Replace("\r\n", "\n");

        using var doc = JsonDocument.Parse(manifestJson);
        Assert.Equal(ExoBridgeProtocol.Version, doc.RootElement.GetProperty("version").GetInt32());

        var arms = ExtractDispatchArms(bridgeSource);
        Assert.NotEmpty(arms);

        var methods = doc.RootElement.GetProperty("methods");
        var missing = new List<string>();
        var invalid = new List<string>();
        var methodCount = 0;

        foreach (var prop in methods.EnumerateObject())
        {
            methodCount++;
            var method = prop.Name;
            if (AnsweredBeforeSwitch.Contains(method))
            {
                Assert.Contains($"method == \"{method}\"", bridgeSource, StringComparison.Ordinal);
                continue;
            }
            if (!arms.TryGetValue(method, out var handler))
            {
                missing.Add(method);
                continue;
            }
            if (!IsRealHandler(handler) || IsStubHandler(handler))
                invalid.Add($"{method} => {handler}");
        }

        Assert.True(methodCount > 0, "host-v1.json must list host RPC methods.");
        Assert.True(missing.Count == 0, "Missing dispatcher arms for host-v1.json methods: " + string.Join(", ", missing));
        Assert.True(invalid.Count == 0, "Non-real or stub dispatcher arms for host-v1.json methods: " + string.Join("; ", invalid));
    }

    [Fact]
    public void DispatcherArmParserHandlesMultiLabelsNestedBracesAndDetectsStubs()
    {
        var snippet = """
            object? result = method switch
            {
                "alpha.get" => GetAlpha(new { a = 1, b = "x,y" }),
                "beta.one" or "beta.two" => await RunBetaAsync(method).ConfigureAwait(false),
                "gamma.stub" => new { ok = true },
                "delta.null" => null,
                _ => throw new InvalidOperationException("unknown")
            };
            """;

        var arms = ExtractDispatchArms(snippet);
        Assert.Equal(5, arms.Count);
        Assert.True(IsRealHandler(arms["alpha.get"]) && !IsStubHandler(arms["alpha.get"]));
        Assert.True(IsRealHandler(arms["beta.one"]) && !IsStubHandler(arms["beta.one"]));
        Assert.True(IsRealHandler(arms["beta.two"]) && !IsStubHandler(arms["beta.two"]));
        Assert.True(IsStubHandler(arms["gamma.stub"]));
        Assert.False(IsRealHandler(arms["delta.null"]));
    }

    [GeneratedRegex(@"object\?\s+result\s*=\s*method\s+switch\s*\{")]
    private static partial Regex DispatchSwitchRegex();

    [GeneratedRegex(@"^((?:""[^""]+""\s*(?:or\s*)?)+)\s*=>\s*([\s\S]+)$")]
    private static partial Regex ArmRegex();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"^(throw\b|null\b|default\b|new\s*\{\s*\})")]
    private static partial Regex NonHandlerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\.ConfigureAwait\([^)]*\)$")]
    private static partial Regex ConfigureAwaitSuffixRegex();

    [GeneratedRegex(@"^(\(object\)\s*)?(true|false)$")]
    private static partial Regex StubBoolRegex();

    [GeneratedRegex(@"^(Value)?Task\.CompletedTask$")]
    private static partial Regex StubCompletedTaskRegex();

    [GeneratedRegex(@"^(Value)?Task\.FromResult\(\s*(true|false|null|""[^""]*"")?\s*\)$")]
    private static partial Regex StubFromResultRegex();

    [GeneratedRegex(@"^new \{ ok = (true|false) \}$")]
    private static partial Regex StubOkObjectRegex();

    [GeneratedRegex(@"^new \{ ok = (true|false), (message|error) = ""[^""]*"" \}$")]
    private static partial Regex StubOkMessageObjectRegex();
}
