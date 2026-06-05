CREATE TABLE post_revisions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    -- propagate the next two from 0001_CreateTables
    display_title VARCHAR(250) NOT NULL,
    contents TEXT NOT NULL,
    author_id UUID REFERENCES users(id) ON DELETE SET NULL,
    post_id UUID NOT NULL REFERENCES posts(id) ON DELETE CASCADE
);

CREATE INDEX post_revisions_pid
    ON post_revisions(post_id);

CREATE TRIGGER post_revisions_set_timestamp BEFORE UPDATE ON post_revisions
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();

ALTER TABLE posts ADD COLUMN latest_revision_id UUID DEFAULT NULL;

ALTER TABLE posts ADD FOREIGN KEY (latest_revision_id) REFERENCES post_revisions (id) DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE posts ADD COLUMN latest_revision_author_id UUID DEFAULT NULL;

ALTER TABLE posts ADD FOREIGN KEY (latest_revision_author_id) REFERENCES users (id) ON DELETE SET NULL;

BEGIN TRANSACTION;
    WITH revision_ids AS (
         INSERT INTO post_revisions (display_title, contents, created_at, updated_at, author_id, post_id)
             SELECT display_title, contents, created_at, updated_at, author_id, id
             FROM posts
             RETURNING id, post_id, author_id
    )
    MERGE INTO posts
        USING revision_ids
        ON posts.id = revision_ids.post_id
        WHEN MATCHED THEN
            UPDATE SET 
                posts.latest_revision_id        = revision_ids.id,
                posts.latest_revision_author_id = revision_ids.author_id;
COMMIT;

ALTER TABLE posts DROP COLUMN contents;
ALTER TABLE posts DROP COLUMN display_title;

CREATE TABLE media_revisions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    -- when porting to other platforms, the column type MUST be one that can store multi megabyte strings
    -- (the parser limit is 2 GB due to character indexes)
    contents BYTEA NOT NULL,
    -- to prevent access of `contents` in EF core, we store length in-row
    content_length INT NOT NULL CHECK(content_length >= 0),
    -- RFC 4288 4.2 gives a limit of 255; it should be 1-127 characters on either side of the slash but we just
    -- enforce the total length
    content_type VARCHAR(255) NOT NULL CHECK(length(content_type) >= 0),
    author_id UUID REFERENCES users(id) ON DELETE SET NULL,
    media_id UUID NOT NULL REFERENCES media(id) ON DELETE CASCADE
);

CREATE INDEX media_revisions_mid
    ON media_revisions(media_id);

CREATE TRIGGER media_revisions_set_timestamp BEFORE UPDATE ON media_revisions
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();

ALTER TABLE media ADD COLUMN latest_revision_id UUID DEFAULT NULL;

ALTER TABLE media ADD FOREIGN KEY (latest_revision_id) REFERENCES media_revisions (id) DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE media ADD COLUMN latest_revision_author_id UUID DEFAULT NULL;

ALTER TABLE media ADD FOREIGN KEY (latest_revision_author_id) REFERENCES users (id) ON DELETE SET NULL;

BEGIN TRANSACTION;
WITH revision_ids AS (
    INSERT INTO media_revisions (contents, content_length, content_type, created_at, updated_at, author_id, media_id)
        SELECT contents, content_length, content_type, created_at, updated_at, author_id, id
        FROM media
        RETURNING id, media_id, author_id
    )
    MERGE INTO media
USING revision_ids
ON media.id = revision_ids.media_id
WHEN MATCHED THEN
    UPDATE SET
               latest_revision_id = revision_ids.id,
               latest_revision_author_id = revision_ids.author_id;
COMMIT;

ALTER TABLE media DROP COLUMN contents;
ALTER TABLE media DROP COLUMN content_type;
ALTER TABLE media DROP COLUMN content_length;
