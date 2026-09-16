using System.Reflection;
using ProgramManager;
using ProgramManager.Core;

internal static class OfflineDocumentationChecks
{
    public static void Run(string root)
    {
        var source = "<h1 align=\"center\">HTML 안내</h1>\n\n<p>설명 <strong>강조</strong></p>\n\n| 항목 | 값 |\n| --- | --- |\n| 표시 | 정상 |\n\n- **목록**\n\n<script>document.title='FAILED'</script>\n<img src=\"https://example.invalid/image\" onerror=\"document.title='FAILED'\" alt=\"그림\">\n\n```html\n<b>코드 예제</b>\n```";
        Check(source, "fixture");
        var realReadme = Environment.GetEnvironmentVariable("PROGRAM_MANAGER_DOCS_README");
        if (!string.IsNullOrWhiteSpace(realReadme)) Check(File.ReadAllText(realReadme), "real-readme");

        void Check(string markdown, string name)
        {
            var app = new CatalogApp { Id = "documentation", Name = "설명 표시 확인", Description = "오프라인 설명", Releases = [new AppRelease { Version = "1.0", Notes = "<p>릴리스 <b>강조</b></p>" }] };
            var render = typeof(CatalogStore).GetMethod("RenderDocumentation", BindingFlags.Static | BindingFlags.NonPublic)!;
            var bytes = (byte[])render.Invoke(null, new object[] { app, markdown })!;
            var path = Path.Combine(root, "offline-" + name + ".html"); File.WriteAllBytes(path, bytes);
            using var owner = new Form();
            using var timer = new System.Windows.Forms.Timer { Interval = 25 };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            Exception? error = null; var checkedDocument = false;
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Modal);
                if (dialog is null) return;
                try
                {
                    var browser = dialog.Controls.OfType<WebBrowser>().Single();
                    if (browser.ReadyState != WebBrowserReadyState.Complete)
                    { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Offline HTML viewer did not load"); return; }
                    timer.Stop();
                    var document = browser.Document!;
                    Assert(document.GetElementsByTagName("script").Count == 0 && document.GetElementsByTagName("img").Count == 0 && document.GetElementsByTagName("iframe").Count == 0, "viewer contains no active or remote resource elements");
                    Assert(document.GetElementsByTagName("table").Count > 0 && document.GetElementsByTagName("li").Count > 0 && document.GetElementsByTagName("strong").Count > 0, "actual viewer renders tables, lists and emphasis");
                    if (name == "fixture")
                    {
                        Assert(document.GetElementsByTagName("h3")[0]?.InnerText == "HTML 안내", "raw HTML heading is rendered, not exposed as tag text");
                        Assert(document.GetElementsByTagName("pre")[0]?.InnerText?.Contains("<b>코드 예제</b>") == true, "intentional HTML code examples stay literal");
                    }
                    Assert(document.GetElementsByTagName("html")[0]!.ScrollRectangle.Width <= browser.ClientSize.Width + 2, "scaled document fits the viewer width");
                    dialog.ClientSize = new Size(dialog.ClientSize.Width * 3 / 4, dialog.ClientSize.Height);
                    dialog.PerformLayout(); Application.DoEvents();
                    Assert(document.GetElementsByTagName("html")[0]!.ScrollRectangle.Width <= browser.ClientSize.Width + 2, "resized document wraps within the viewer width");
                    var output = Environment.GetEnvironmentVariable("PROGRAM_MANAGER_DOCS_ARTIFACTS");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, name + ".html"), bytes);
                        using var image = new Bitmap(dialog.Width, dialog.Height);
                        dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size)); image.Save(Path.Combine(output, name + ".png"));
                    }
                    checkedDocument = true;
                }
                catch (Exception ex) { error = ex; }
                dialog.Close();
            };
            timer.Start(); Dialogs.ShowHtml(owner, path); timer.Stop();
            if (error != null) throw error;
            Assert(checkedDocument, "offline explanation was inspected in the production viewer");
        }
        Console.WriteLine("PASS: production offline HTML viewer renders mixed HTML/Markdown, preserves code examples and omits active content");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
