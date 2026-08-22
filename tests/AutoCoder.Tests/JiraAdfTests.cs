using AutoCoder.Core.Jira;

namespace AutoCoder.Tests;

public sealed class JiraAdfTests
{
    [Fact]
    public void Converts_adf_heading_and_lists_to_plain_text()
    {
        var json = """
            {
              "type": "doc",
              "version": 1,
              "content": [
                {
                  "type": "paragraph",
                  "content": [{ "type": "text", "text": "Current text : for Safe change flow" }]
                },
                {
                  "type": "heading",
                  "attrs": { "level": 2 },
                  "content": [{ "type": "text", "text": "Safe change flow" }]
                },
                {
                  "type": "orderedList",
                  "attrs": { "order": 1 },
                  "content": [
                    {
                      "type": "listItem",
                      "content": [
                        {
                          "type": "paragraph",
                          "content": [{ "type": "text", "text": "Ticket is assigned to AutoCoder." }]
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "heading",
                  "attrs": { "level": 2 },
                  "content": [{ "type": "text", "text": "Workflow :" }]
                },
                {
                  "type": "mediaSingle",
                  "content": [{ "type": "media", "attrs": { "type": "file", "id": "abc" } }]
                }
              ]
            }
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var text = JiraAdf.ToPlainText(doc.RootElement);
        Assert.Contains("Current text : for Safe change flow", text);
        Assert.Contains("## Safe change flow", text);
        Assert.Contains("1. Ticket is assigned to AutoCoder.", text);
        Assert.Contains("## Workflow :", text);
        Assert.Contains("[image attached in Jira", text);
        Assert.DoesNotContain("\"type\":\"doc\"", text);
    }
}
