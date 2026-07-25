using corporate_dashboards.Data;
using corporate_dashboards.Models;
using corporate_dashboards.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace corporate_dashboards.Controllers;

public sealed class DocumentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHostEnvironment _env;
    private readonly StorageOptions _storage;
    private readonly IDocumentQueue _queue;

    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".txt"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "text/plain"
    };

    public DocumentsController(AppDbContext db, IHostEnvironment env, IOptions<StorageOptions> storage, IDocumentQueue queue)
    {
        _db = db;
        _env = env;
        _storage = storage.Value;
        _queue = queue;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // SQLite (EF provider) can't ORDER BY DateTimeOffset in SQL; sort on client.
        var docs = (await _db.Documents.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(x => x.UploadedAt)
            .ToList();

        return View(docs);
    }

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
        {
            ModelState.AddModelError("", "Please choose a file to upload.");
            return View();
        }

        var originalName = Path.GetFileName(file.FileName ?? "");
        if (string.IsNullOrWhiteSpace(originalName))
        {
            ModelState.AddModelError("", "File name is missing.");
            return View();
        }

        var ext = Path.GetExtension(originalName);
        if (!AllowedExt.Contains(ext))
        {
            ModelState.AddModelError("", "Only PDF, DOCX, or TXT files are allowed.");
            return View();
        }

        // ContentType is not fully trustworthy, but can catch obvious mismatches.
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        if (contentType != "application/octet-stream" && !AllowedContentTypes.Contains(contentType))
        {
            ModelState.AddModelError("", "Unsupported file type.");
            return View();
        }

        var safeBase = SanitizeFileName(Path.GetFileNameWithoutExtension(originalName));
        if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "document";

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var storedFile = $"{stamp}_{Guid.NewGuid():N}_{safeBase}{ext}";

        var uploadsRootAbs = Path.Combine(_env.ContentRootPath, _storage.UploadsRoot);
        Directory.CreateDirectory(uploadsRootAbs);

        var relPath = Path.Combine(_storage.UploadsRoot, storedFile).Replace('\\', '/');
        var fullPath = Path.Combine(_env.ContentRootPath, relPath);

        await using (var fs = System.IO.File.Create(fullPath))
            await file.CopyToAsync(fs, ct);

        var doc = new Document
        {
            FileName = originalName,
            StoredPath = relPath,
            ContentType = contentType,
            SizeBytes = file.Length,
            UploadedAt = DateTimeOffset.UtcNow,
            Status = "Queued",
            ErrorMessage = null
        };

        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueAsync(doc.Id, ct);

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var doc = await _db.Documents
            .AsNoTracking()
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        return doc is null ? NotFound() : View(doc);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reprocess(long id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        doc.Status = "Queued";
        doc.ErrorMessage = null;
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueAsync(doc.Id, ct);
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string SanitizeFileName(string name)
    {
        name = Regex.Replace(name, @"[^\w\s-]", "");
        name = name.Trim();
        name = Regex.Replace(name, @"\s+", "_");
        return name.Length > 80 ? name[..80] : name;
    }
}
