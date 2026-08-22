using System.Text;
using System.Text.Json;

namespace AutoCoder.Core.Jira;

/// <summary>Turns Jira Cloud ADF (Atlassian Document Format) into plain text the planner can read.</summary>
public static class JiraAdf
{
    public static string ToPlainText(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
            return node.GetString() ?? "";
        if (node.ValueKind != JsonValueKind.Object)
            return "";

        var sb = new StringBuilder();
        Walk(node, sb, headingLevel: 0);
        return sb.ToString().Trim();
    }

    private static void Walk(JsonElement node, StringBuilder sb, int headingLevel)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "doc":
                foreach (var child in Content(node))
                    Walk(child, sb, 0);
                break;
            case "paragraph":
                AppendInline(node, sb);
                sb.AppendLine();
                break;
            case "heading":
                var level = node.TryGetProperty("attrs", out var attrs)
                            && attrs.TryGetProperty("level", out var lv)
                    ? lv.GetInt32()
                    : 2;
                sb.Append('#', Math.Clamp(level, 1, 6)).Append(' ');
                AppendInline(node, sb);
                sb.AppendLine();
                break;
            case "bulletList":
            case "orderedList":
                var i = 1;
                foreach (var item in Content(node))
                {
                    if (type == "orderedList")
                        sb.Append(i++).Append(". ");
                    else
                        sb.Append("- ");
                    AppendListItem(item, sb);
                    sb.AppendLine();
                }
                break;
            case "listItem":
                AppendListItem(node, sb);
                break;
            case "hardBreak":
                sb.AppendLine();
                break;
            case "text":
                if (node.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
                break;
            case "mediaSingle":
            case "media":
            case "mediaGroup":
                sb.AppendLine("[image attached in Jira — open the ticket to view]");
                break;
            case "codeBlock":
                sb.AppendLine("```");
                AppendInline(node, sb);
                sb.AppendLine().AppendLine("```");
                break;
            case "blockquote":
                foreach (var child in Content(node))
                {
                    sb.Append("> ");
                    Walk(child, sb, headingLevel);
                }
                break;
            case "rule":
                sb.AppendLine("---");
                break;
            default:
                foreach (var child in Content(node))
                    Walk(child, sb, headingLevel);
                break;
        }
    }

    private static void AppendListItem(JsonElement item, StringBuilder sb)
    {
        var first = true;
        foreach (var child in Content(item))
        {
            if (!first)
                sb.Append(' ');
            first = false;
            if (child.TryGetProperty("type", out var ct) && ct.GetString() is "paragraph" or "text")
                AppendInline(child, sb);
            else
                Walk(child, sb, 0);
        }
    }

    private static void AppendInline(JsonElement node, StringBuilder sb)
    {
        if (node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            sb.Append(text.GetString());
        foreach (var child in Content(node))
        {
            var type = child.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "hardBreak")
                sb.AppendLine();
            else if (type == "text" && child.TryGetProperty("text", out var tx))
                sb.Append(tx.GetString());
            else if (type is "mention" or "emoji" or "inlineCard")
            {
                if (child.TryGetProperty("attrs", out var a) && a.TryGetProperty("text", out var at))
                    sb.Append(at.GetString());
            }
            else
                AppendInline(child, sb);
        }
    }

    private static IEnumerable<JsonElement> Content(JsonElement node)
    {
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var child in content.EnumerateArray())
            yield return child;
    }
}
