using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Serialization;

namespace Drtui;

internal sealed record Plan(JsonObject Document)
{
    public JsonObject Metadata => Document["metadata"]!.AsObject();
    public JsonObject Inputs => Document["inputs"] as JsonObject ?? new JsonObject();
    public JsonObject Providers => Document["providers"]!.AsObject();
    public JsonArray Steps => Document["steps"]!.AsArray();
}

internal static class PlanLoader
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "drp.schema.json")));

    public static string[] Discover(string root)
    {
        if (File.Exists(root)) return [root];
        if (!Directory.Exists(root)) throw new FileNotFoundException($"Plan path not found: {root}");
        return Directory.GetDirectories(root)
            .Select(folder => Path.Combine(folder, "plan.drp.yaml"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static Plan Load(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Plan exceeds 4 MiB limit");

        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build();
        var value = yaml.Deserialize<object>(File.ReadAllText(path));
        var document = JsonSerializer.SerializeToNode(value) as JsonObject
            ?? throw new InvalidDataException("Plan must be a YAML object");
        var result = Schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
            throw new InvalidDataException($"Invalid plan: {JsonSerializer.Serialize(result)}");

        ValidateDependencies(document["steps"]!.AsArray());
        return new Plan(document);
    }

    internal static string[] ValidateDependencies(JsonArray steps)
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < steps.Count; index++)
        {
            var id = (string?)steps[index]?["id"] ?? throw new InvalidDataException($"Step at position {index} has no id");
            if (!indices.TryAdd(id, index)) throw new InvalidDataException($"Duplicate step id {id}");
        }

        var indegree = indices.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var dependents = indices.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var id = (string)step!["id"]!;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in step["needs"]?.AsArray() ?? new JsonArray())
            {
                var needed = (string)dependency!;
                if (!indices.ContainsKey(needed)) throw new InvalidDataException($"Step {id} needs unknown step {needed}");
                if (!seen.Add(needed)) throw new InvalidDataException($"Step {id} repeats dependency {needed}");
                indegree[id]++;
                dependents[needed].Add(id);
            }
        }

        var order = new List<string>();
        var ready = new SortedSet<int>(indices.Where(item => indegree[item.Key] == 0).Select(item => item.Value));
        while (ready.Count > 0)
        {
            var index = ready.Min;
            ready.Remove(index);
            var id = (string)steps[index]!["id"]!;
            order.Add(id);
            foreach (var dependent in dependents[id])
                if (--indegree[dependent] == 0) ready.Add(indices[dependent]);
        }
        if (order.Count != steps.Count) throw new InvalidDataException("Plan contains a cyclic dependency");
        return order.ToArray();
    }
}