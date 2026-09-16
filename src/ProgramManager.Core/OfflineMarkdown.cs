using System.Net;
using System.Text;
using HtmlAgilityPack;
using Markdig;

namespace ProgramManager.Core;

internal static class OfflineMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseAutoIdentifiers().Build();
    private static readonly HashSet<string> Tags = new("p div span h1 h2 h3 h4 h5 h6 blockquote pre code ul ol li dl dt dd table thead tbody tfoot tr th td br hr strong em b i u s del sup sub kbd a".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Excluded = new("script style head iframe object embed svg math form input button textarea xmp noembed noframes plaintext noscript template".Split(' '), StringComparer.Ordinal);

    public static string Render(string source)
    {
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 128 };
        try { document.LoadHtml(Markdown.ToHtml(source, Pipeline)); }
        catch (Exception ex) { throw new InvalidDataException("설명 문서의 HTML 구조를 읽지 못했습니다.", ex); }
        var output = new StringBuilder();
        Append(output, document.DocumentNode);
        return output.ToString();
    }

    // Rebuild only passive formatting. No source styles, event handlers or resource URLs
    // reach the Windows 7 viewer, whose HTML engine does not enforce modern CSP rules.
    private static void Append(StringBuilder output, HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text)
        { output.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(((HtmlTextNode)node).Text))); return; }
        if (node.NodeType == HtmlNodeType.Comment || Excluded.Contains(node.Name)) return;
        if (node.Name == "img")
        {
            var alt = WebUtility.HtmlDecode(node.GetAttributeValue("alt", ""));
            if (alt.Length > 0) output.Append("<span>[이미지: ").Append(WebUtility.HtmlEncode(alt)).Append("]</span>");
            return;
        }
        var tag = node.Name switch { "details" => "div", "summary" => "strong", _ => node.Name };
        var allowed = Tags.Contains(tag);
        if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6') tag = "h" + Math.Min(6, tag[1] - '0' + 2);
        if (allowed)
        {
            output.Append('<').Append(tag);
            var id = WebUtility.HtmlDecode(node.GetAttributeValue("id", ""));
            if (id.Length > 0) output.Append(" id=\"").Append(WebUtility.HtmlEncode(id)).Append('"');
            var href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
            if (tag == "a" && href.StartsWith("#", StringComparison.Ordinal)) output.Append(" href=\"").Append(WebUtility.HtmlEncode(href)).Append('"');
            var align = node.GetAttributeValue("align", "").ToLowerInvariant();
            if (align is "left" or "center" or "right") output.Append(" align=\"").Append(align).Append('"');
            if (tag is "th" or "td")
                foreach (var name in new[] { "colspan", "rowspan" })
                    if (int.TryParse(node.GetAttributeValue(name, ""), out var count) && count is > 0 and <= 100)
                        output.Append(' ').Append(name).Append("=\"").Append(count).Append('"');
            output.Append('>');
        }
        foreach (var child in node.ChildNodes) Append(output, child);
        if (allowed && tag is not "br" and not "hr") output.Append("</").Append(tag).Append('>');
    }
}
