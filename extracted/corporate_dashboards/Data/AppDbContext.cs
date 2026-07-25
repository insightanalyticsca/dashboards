using corporate_dashboards.Models;
using Microsoft.EntityFrameworkCore;

namespace corporate_dashboards.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration cfg) : base(options)
    {
        // EF command timeout (seconds)
        var timeout =
            cfg.GetValue<int?>("SqlCommandTimeout")
            ?? cfg.GetValue<int?>("SqlSettings:QueryTimeout");

        if (timeout.HasValue && timeout.Value > 0)
            Database.SetCommandTimeout(timeout.Value);
    }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocChunk> Chunks => Set<DocChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(260);
            e.Property(x => x.StoredPath).IsRequired().HasMaxLength(1024);
            e.Property(x => x.ContentType).IsRequired().HasMaxLength(200);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.HasMany(x => x.Chunks).WithOne(x => x.Document).HasForeignKey(x => x.DocumentId);
        });

        modelBuilder.Entity<DocChunk>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Text).IsRequired();
            e.Property(x => x.SourceLabel).IsRequired().HasMaxLength(400);
            e.Property(x => x.EmbeddingJson).IsRequired();
            e.HasIndex(x => x.DocumentId);
        });
    }
}