using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResearchHive.Core.Services;

/// <summary>
/// Defines the research tools that LLMs can invoke during synthesis and remediation phases.
/// Uses the OpenAI function-calling schema (compatible with Anthropic, GitHub Models, etc.).
/// </summary>
public static class ResearchTools
{
    /// <summary>
    /// All tool definitions in OpenAI function-calling format.
    /// </summary>
    public static readonly ToolDefinition[] All = new[]
    {
        new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "search_evidence",
                Description = "Search the locally indexed evidence chunks using hybrid keyword + semantic search. " +
                              "Use this to find specific facts, statistics, or claims from already-acquired sources.",
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolProperty>
                    {
                        ["query"] = new() { Type = "string", Description = "The search query to find relevant evidence chunks" }
                    },
                    Required = new[] { "query" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "search_web",
                Description = "Trigger a new web search and fetch cycle to acquire additional sources. " +
                              "Use this when the existing evidence is insufficient to answer a specific aspect of the research question.",
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolProperty>
                    {
                        ["query"] = new() { Type = "string", Description = "The web search query to find new sources" }
                    },
                    Required = new[] { "query" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "get_source",
                Description = "Retrieve the full text content of a specific source by its citation label (e.g. '[1]', '[3]'). " +
                              "Use this to read more context from a source that was only partially shown in evidence chunks.",
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolProperty>
                    {
                        ["citation_label"] = new() { Type = "string", Description = "The citation label like '[1]' or '1'" }
                    },
                    Required = new[] { "citation_label" }
                }
            }
        },
        new ToolDefinition
        {
            Type = "function",
            Function = new ToolFunction
            {
                Name = "verify_claim",
                Description = "Check whether a specific claim is supported by the indexed evidence. " +
                              "Returns matching evidence chunks with relevance scores. Use this to verify factual accuracy.",
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolProperty>
                    {
                        ["claim"] = new() { Type = "string", Description = "The factual claim to verify against indexed evidence" }
                    },
                    Required = new[] { "claim" }
                }
            }
        }
    };

    /// <summary>
    /// Returns only the tool definitions suitable for Anthropic's format.
    /// Anthropic wraps tools differently in the API request.
    /// </summary>
    public static object[] ToAnthropicFormat()
    {
        return All.Select(t => new
        {
            name = t.Function.Name,
            description = t.Function.Description,
            input_schema = new
            {
                type = t.Function.Parameters.Type,
                properties = t.Function.Parameters.Properties.ToDictionary(
                    kv => kv.Key,
                    kv => new { type = kv.Value.Type, description = kv.Value.Description }),
                required = t.Function.Parameters.Required
            }
        }).ToArray<object>();
    }
}

#region Tool-calling JSON models

public class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolFunction Function { get; set; } = new();
}

public class ToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public ToolParameters Parameters { get; set; } = new();
}

public class ToolParameters
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, ToolProperty> Properties { get; set; } = new();

    [JsonPropertyName("required")]
    public string[] Required { get; set; } = Array.Empty<string>();
}

public class ToolProperty
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// Represents a tool call returned by the model in a chat completion response.
/// </summary>
public class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; } = new();
}

public class ToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";

    /// <summary>Parse the arguments JSON into a dictionary.</summary>
    public Dictionary<string, string> ParseArgs()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Arguments)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>
/// Result of executing a tool call, to be sent back to the model.
/// </summary>
public class ToolResult
{
    public string ToolCallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

#endregion
