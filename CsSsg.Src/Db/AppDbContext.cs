using EntityFramework.Exceptions.PostgreSQL;
using Microsoft.EntityFrameworkCore;

namespace CsSsg.Src.Db;

public class AppDbContext : DbContext
{
    public AppDbContext() { }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public virtual DbSet<MediaRoleGroup> MediaRoleGroups { get; set; }

    public virtual DbSet<MediaRoleUser> MediaRoleUsers { get; set; }

    public virtual DbSet<Medium> Media { get; set; }

    public virtual DbSet<Post> Posts { get; set; }

    public virtual DbSet<PostRoleGroup> PostRoleGroups { get; set; }

    public virtual DbSet<PostRoleUser> PostRoleUsers { get; set; }

    public virtual DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum("role_namespace", ["search", "view", "edit"]);

        modelBuilder.Entity<MediaRoleGroup>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_role_groups_pkey");

            entity.ToTable("media_role_groups");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.MediaId).HasColumnName("media_id");
            entity.Property(e => e.Namespace).HasColumnName("namespace");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Media).WithMany(p => p.MediaRoleGroups)
                .HasForeignKey(d => d.MediaId)
                .HasConstraintName("media_role_groups_media_id_fkey");
        });

        modelBuilder.Entity<MediaRoleUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("media_role_users_pkey");

            entity.ToTable("media_role_users");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.MediaId).HasColumnName("media_id");
            entity.Property(e => e.Namespace).HasColumnName("namespace");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");
            entity.Property(e => e.User).HasColumnName("user");

            entity.HasOne(d => d.Media).WithMany(p => p.MediaRoleUsers)
                .HasForeignKey(d => d.MediaId)
                .HasConstraintName("media_role_users_media_id_fkey");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.MediaRoleUsers)
                .HasForeignKey(d => d.User)
                .HasConstraintName("media_role_users_user_fkey");
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
            entity.Property(e => e.ContentLength).HasColumnName("content_length");
            entity.Property(e => e.ContentType)
                .HasMaxLength(255)
                .HasColumnName("content_type");
            entity.Ignore(e => e.Contents);
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Public).HasColumnName("public");
            entity.Property(e => e.Slug)
                .HasMaxLength(245)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.Media)
                .HasForeignKey(d => d.AuthorId)
                .HasConstraintName("media_author_id_fkey");
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
            entity.Property(e => e.Contents).HasColumnName("contents");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayTitle)
                .HasMaxLength(250)
                .HasColumnName("display_title");
            entity.Property(e => e.Slug)
                .HasMaxLength(250)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Author).WithMany(p => p.Posts)
                .HasForeignKey(d => d.AuthorId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("posts_author_id_fkey");
        });

        modelBuilder.Entity<PostRoleGroup>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_role_groups_pkey");

            entity.ToTable("post_role_groups");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Namespace).HasColumnName("namespace");
            entity.Property(e => e.PostId).HasColumnName("post_id");
            entity.Property(e => e.Tag)
                .HasMaxLength(256)
                .HasColumnName("tag");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Post).WithMany(p => p.RoleGroups)
                .HasForeignKey(d => d.PostId)
                .HasConstraintName("post_role_groups_post_id_fkey");
        });

        modelBuilder.Entity<PostRoleUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("post_role_users_pkey");

            entity.ToTable("post_role_users");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Namespace).HasColumnName("namespace");
            entity.Property(e => e.PostId).HasColumnName("post_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");
            entity.Property(e => e.User).HasColumnName("user");

            entity.HasOne(d => d.Post).WithMany(p => p.RoleUsers)
                .HasForeignKey(d => d.PostId)
                .HasConstraintName("post_role_users_post_id_fkey");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.PostRoleUsers)
                .HasForeignKey(d => d.User)
                .HasConstraintName("post_role_users_user_fkey");
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
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated_at");
        });
    }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseExceptionProcessor();
    }    
}
