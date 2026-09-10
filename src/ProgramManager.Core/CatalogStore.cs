using System.Security.Cryptography;

namespace ProgramManager.Core;

public sealed partial class CatalogStore
{
    private readonly string root;
    private string CatalogPath => Path.Combine(root, "catalog.json");

    public CatalogStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public Catalog Read() => CatalogRules.Validate(JsonFiles.Read(CatalogPath, new Catalog()));

    public async Task<Catalog> PublishAsync(string id, string name, string description, string version, string notes, string installerPath, string platform = "win10-x64", CancellationToken token = default)
    {
        CatalogRules.Id(id);
        CatalogRules.Platform(platform);
        CatalogRules.Text(name, 200, true); CatalogRules.Text(description, 10000); CatalogRules.Text(notes, 30000);
        version = CatalogRules.NormalizeVersion(version);
        var fileName = CatalogRules.InstallerName(Path.GetFileName(installerPath));
        // One writer per catalog; a second application receives a retryable file-in-use error.
        using var writerLock = new FileStream(Path.Combine(root, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var catalog = Read();
        var app = catalog.Apps.SingleOrDefault(a => a.Id == id);
        if (app?.Releases.Any(r => CatalogRules.Version(r.Version) == CatalogRules.Version(version) && r.Platform == platform) == true)
            throw new InvalidOperationException("이미 게시된 버전입니다. 새 버전 번호를 사용하세요.");
        var packageFolder = Path.Combine(root, "packages", id, version, platform);
        Directory.CreateDirectory(packageFolder);
        var packagePath = Path.Combine(packageFolder, "installer" + Path.GetExtension(fileName).ToLowerInvariant());
        var temporary = packagePath + "." + Guid.NewGuid().ToString("N") + ".part";
        var moved = false;
        try
        {
            long size;
            string hash;
            using (var input = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                size = input.Length;
                if (size is <= 0 or > CatalogRules.MaxPackageBytes) throw new InvalidDataException("설치 파일 크기가 허용 범위를 벗어났습니다.");
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
                await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
                if (output.Length != size) throw new IOException("게시 중 원본 파일 크기가 바뀌었습니다.");
                output.Position = 0;
                hash = Compat.Hash(output, token);
            }
            token.ThrowIfCancellationRequested();
            app ??= new CatalogApp { Id = id };
            app.Name = name; app.Description = description;
            app.Releases.Add(new AppRelease { Version = version, Platform = platform, Notes = notes, FileName = fileName, Size = size, Sha256 = hash, PublishedUtc = DateTimeOffset.UtcNow });
            if (!catalog.Apps.Contains(app)) catalog.Apps.Add(app);
            CatalogRules.Validate(catalog);
            File.Move(temporary, packagePath);
            moved = true;
            JsonFiles.Write(CatalogPath, catalog);
            return catalog;
        }
        catch
        {
            if (moved && File.Exists(packagePath)) File.Delete(packagePath);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string GetPackagePath(string id, string version, string platform = "win10-x64")
    {
        CatalogRules.Id(id);
        CatalogRules.Platform(platform);
        var normalized = CatalogRules.NormalizeVersion(version);
        var app = Read().Apps.SingleOrDefault(a => a.Id == id) ?? throw new FileNotFoundException("프로그램이 없습니다.");
        var release = app.Releases.SingleOrDefault(r => CatalogRules.Version(r.Version) == CatalogRules.Version(normalized) && r.Platform == platform) ?? throw new FileNotFoundException("버전이 없습니다.");
        return Path.Combine(root, "packages", id, normalized, platform, "installer" + Path.GetExtension(release.FileName).ToLowerInvariant());
    }
}
