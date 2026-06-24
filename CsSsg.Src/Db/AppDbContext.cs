using Microsoft.EntityFrameworkCore;

namespace CsSsg.Src.Db;

public partial class AppDbContext : DbContext
{
    public AppDbContext() { }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public virtual DbSet<Medium> Media { get; set; }
    
    public virtual DbSet<MediaTag> MediaTags { get; set; }
    
    public virtual DbSet<MediaRevision> MediaRevisions { get; set; }

    public virtual DbSet<MediaTagHistory> MediaTagHistories { get; set; }

    public virtual DbSet<MediaTagHistoryItem> MediaTagHistoryItems { get; set; }

    public virtual DbSet<Post> Posts { get; set; }

    public virtual DbSet<PostRevision> PostRevisions { get; set; }

    public virtual DbSet<PostTag> PostTags { get; set; }

    public virtual DbSet<PostTagHistory> PostTagHistories { get; set; }

    public virtual DbSet<PostTagHistoryItem> PostTagHistoryItems { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserRole> UserRoles { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("role_namespace", ["search", "view", "edit", "special"])
            .HasPostgresEnum("tag_history_item_type", ["add", "del"]);

        modelBuilder.Entity<MediaRevision>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_revisions_pkey");

            entity.ToTable("media_revisions");

            entity.HasIndex(e => e.MediaId, "media_revisions_mid");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.ContentLength).HasColumnName("content_length");
            entity.Property(e => e.ContentType)
                .HasMaxLength(255)
                .HasColumnName("content_type");
            entity.Ignore(e => e.Contents);
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.MediaId).HasColumnName("media_id");
            entity.Property(e => e.RevisionNumber)
                .HasDefaultValue("1")
                .HasColumnType("integer")
                .HasColumnName("rev_num");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.MediaRevisions)
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("media_revisions_author_id_fkey");

            entity.HasOne(d => d.Media).WithMany(p => p.Revisions)
                .HasForeignKey(d => d.MediaId)
                .HasConstraintName("media_revisions_media_id_fkey");
        });

        modelBuilder.Entity<MediaTag>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_role_groups_pkey");

            entity.ToTable("media_tags");

            entity.HasIndex(e => e.MediaId, "media_tag_mid");

            entity.HasIndex(e => e.Tag, "media_tag_tag");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.MediaId).HasColumnName("media_id");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Media).WithMany(p => p.Tags)
                .HasForeignKey(d => d.MediaId)
                .HasConstraintName("media_role_groups_media_id_fkey");
        });

        modelBuilder.Entity<MediaTagHistory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_tag_history_pkey");

            entity.ToTable("media_tag_history");

            entity.HasIndex(e => e.AuthorId, "media_tag_history_authorid");

            entity.HasIndex(e => e.MediaId, "media_tag_history_mediaid");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.MediaId).HasColumnName("media_id");
            entity.Property(e => e.RevisionNumber)
                .HasDefaultValue(1)
                .HasColumnName("rev_num");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.MediaTagHistories)
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("media_tag_history_author_id_fkey");

            entity.HasOne(d => d.Media).WithMany(p => p.TagHistories)
                .HasForeignKey(d => d.MediaId)
                .HasConstraintName("media_tag_history_media_id_fkey");
        });

        modelBuilder.Entity<MediaTagHistoryItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_tag_history_items_pkey");

            entity.ToTable("media_tag_history_items");

            entity.HasIndex(e => e.Hist, "media_tag_history_item_hist");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Hist).HasColumnName("hist");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.Type).HasColumnName("type");

            entity.HasOne(d => d.HistNavigation).WithMany(p => p.Items)
                .HasForeignKey(d => d.Hist)
                .HasConstraintName("media_tag_history_items_hist_fkey");
        });

        modelBuilder.Entity<Medium>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_pkey");

            entity.ToTable("media");

            entity.HasIndex(e => e.Slug, "media_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.LatestRevisionAuthorId).HasColumnName("latest_revision_author_id");
            entity.Property(e => e.LatestRevisionId).HasColumnName("latest_revision_id");
            entity.Property(e => e.NumberOfRevisions)
                .HasDefaultValueSql("1")
                .HasColumnType("integer")
                .HasColumnName("num_revs");
            entity.ToTable(t => 
                t.HasCheckConstraint("media_num_revs_check", "num_revs > 0"));
            entity.Property(e => e.PVer)
                .HasDefaultValue(1)
                .IsConcurrencyToken()
                .HasColumnName("pver");
            entity.Property(e => e.RowVersion)
                .IsRowVersion();
            entity.Property(e => e.Slug)
                .HasMaxLength(245)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.MediumAuthors)
                .HasForeignKey(d => d.AuthorId)
                .HasConstraintName("media_author_id_fkey");

            entity.HasOne(d => d.LatestRevisionAuthor).WithMany(p => p.MediumLatestRevisionAuthors)
                .HasForeignKey(d => d.LatestRevisionAuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("media_latest_revision_author_id_fkey");

            entity.HasOne(d => d.LatestRevision).WithMany(p => p.MediaNavigation)
                .HasForeignKey(d => d.LatestRevisionId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("media_latest_revision_id_fkey");
        });

        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("posts_pkey");

            entity.ToTable("posts");

            entity.HasIndex(e => e.Slug, "posts_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.LatestRevisionAuthorId).HasColumnName("latest_revision_author_id");
            entity.Property(e => e.LatestRevisionId).HasColumnName("latest_revision_id");
            entity.Property(e => e.NumberOfRevisions)
                .HasDefaultValueSql("1")
                .HasColumnType("integer")
                .HasColumnName("num_revs");
            entity.ToTable(t => 
                t.HasCheckConstraint("posts_num_revs_check", "num_revs > 0"));
            entity.Property(e => e.PVer)
                .HasDefaultValue(1)
                .IsConcurrencyToken()
                .HasColumnName("pver");
            entity.Property(e => e.RowVersion)
                .IsRowVersion();
            entity.Property(e => e.Slug)
                .HasMaxLength(250)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.PostAuthors)
                .HasForeignKey(d => d.AuthorId)
                .HasConstraintName("posts_author_id_fkey");

            entity.HasOne(d => d.LatestRevisionAuthor).WithMany(p => p.PostLatestRevisionAuthors)
                .HasForeignKey(d => d.LatestRevisionAuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("posts_latest_revision_author_id_fkey");

            entity.HasOne(d => d.LatestRevision).WithMany(p => p.Posts)
                .HasForeignKey(d => d.LatestRevisionId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("posts_latest_revision_id_fkey");
        });

        modelBuilder.Entity<PostRevision>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_revisions_pkey");

            entity.ToTable("post_revisions");

            entity.HasIndex(e => e.PostId, "post_revisions_pid");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.Contents).HasColumnName("contents");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayTitle)
                .HasMaxLength(250)
                .HasColumnName("display_title");
            entity.Property(e => e.PostId).HasColumnName("post_id");
            entity.Property(e => e.RevisionNumber)
                .HasDefaultValue("1")
                .HasColumnType("integer")
                .HasColumnName("rev_num");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.PostRevisions)
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("post_revisions_author_id_fkey");

            entity.HasOne(d => d.Post).WithMany(p => p.Revisions)
                .HasForeignKey(d => d.PostId)
                .HasConstraintName("post_revisions_post_id_fkey");
        });

        modelBuilder.Entity<PostTag>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_role_groups_pkey");

            entity.ToTable("post_tags");

            entity.HasIndex(e => new { e.PostId, e.Tag }, "post_role_groups_tags").IsUnique();

            entity.HasIndex(e => e.PostId, "post_rolegroup_pid");

            entity.HasIndex(e => e.Tag, "post_rolegroup_tag");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.PostId).HasColumnName("post_id");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Post).WithMany(p => p.Tags)
                .HasForeignKey(d => d.PostId)
                .HasConstraintName("post_role_groups_post_id_fkey");
        });

        modelBuilder.Entity<PostTagHistory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_tag_history_pkey");

            entity.ToTable("post_tag_history");

            entity.HasIndex(e => e.AuthorId, "post_tag_history_authorid");

            entity.HasIndex(e => e.PostId, "post_tag_history_postid");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.PostId).HasColumnName("post_id");
            entity.Property(e => e.RevisionNumber)
                .HasDefaultValue(1)
                .HasColumnName("rev_num");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.PostTagHistories)
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("post_tag_history_author_id_fkey");

            entity.HasOne(d => d.Post).WithMany(p => p.TagHistories)
                .HasForeignKey(d => d.PostId)
                .HasConstraintName("post_tag_history_post_id_fkey");
        });

        modelBuilder.Entity<PostTagHistoryItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_tag_history_items_pkey");

            entity.ToTable("post_tag_history_items");

            entity.HasIndex(e => e.Hist, "post_tag_history_item_hist");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Hist).HasColumnName("hist");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.Type).HasColumnName("type");

            entity.HasOne(d => d.HistNavigation).WithMany(p => p.Items)
                .HasForeignKey(d => d.Hist)
                .HasConstraintName("post_tag_history_items_hist_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users");

            entity.HasIndex(e => e.Email, "users_email_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Email)
                .HasMaxLength(256)
                .HasColumnName("email");
            entity.Property(e => e.PassArgon2id)
                .HasMaxLength(101)
                .HasDefaultValueSql("''::character varying")
                .HasColumnName("pass_argon2id");
            entity.Property(e => e.PVer)
                .HasDefaultValue(1)
                .HasColumnName("pver");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_roles_pkey");

            entity.ToTable("user_roles");

            entity.HasIndex(e => e.UserId, "userrole_uid");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            
            entity.Property(e => e.Namespace).HasColumnName("namespace");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.Tags)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("user_roles_user_id_fkey");
        });
    }
}
