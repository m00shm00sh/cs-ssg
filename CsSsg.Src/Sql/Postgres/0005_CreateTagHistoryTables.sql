CREATE TYPE tag_history_item_type AS ENUM('add', 'del');

CREATE TABLE post_tag_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    post_id UUID NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    author_id UUID REFERENCES users(id) ON DELETE SET NULL,
    rev_num INTEGER NOT NULL DEFAULT 1 CHECK (rev_num > 0)
);

CREATE INDEX post_tag_history_postid ON post_tag_history(post_id);

CREATE INDEX post_tag_history_authorid ON post_tag_history(author_id);

CREATE TRIGGER post_tag_history_set_timestamp BEFORE UPDATE ON post_tag_history
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();

CREATE TABLE post_tag_history_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    type tag_history_item_type NOT NULL,
    tag VARCHAR(256) NOT NULL,
    hist UUID NOT NULL REFERENCES post_tag_history(id) ON DELETE CASCADE
);

CREATE INDEX post_tag_history_item_hist ON post_tag_history_items(hist);

BEGIN TRANSACTION;
    INSERT INTO post_tag_history (created_at, updated_at, post_id, author_id)
        SELECT created_at, updated_at, id, author_id
        FROM posts;

    INSERT INTO post_tag_history_items (type, tag, hist)
        SELECT 'add', post_tags.tag, post_tag_history.id
        FROM posts
             JOIN post_tags ON posts.id = post_tags.post_id
             JOIN post_tag_history on post_tag_history.post_id = post_tags.post_id;

COMMIT;

CREATE TABLE media_tag_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    media_id UUID NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    author_id UUID REFERENCES users(id) ON DELETE SET NULL,
    rev_num INTEGER NOT NULL DEFAULT 1 CHECK (rev_num > 0)
);

CREATE INDEX media_tag_history_mediaid ON media_tag_history(media_id);

CREATE INDEX media_tag_history_authorid ON media_tag_history(author_id);

CREATE TRIGGER media_tag_history_set_timestamp BEFORE UPDATE ON media_tag_history
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();

CREATE TABLE media_tag_history_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    type tag_history_item_type NOT NULL,
    tag VARCHAR(256) NOT NULL,
    hist UUID NOT NULL REFERENCES media_tag_history(id) ON DELETE CASCADE
);

CREATE INDEX media_tag_history_item_hist ON media_tag_history_items(hist);

BEGIN TRANSACTION;
    INSERT INTO media_tag_history (created_at, updated_at, media_id, author_id)
        SELECT created_at, updated_at, id, author_id
        FROM media;

    INSERT INTO media_tag_history_items (type, tag, hist)
        SELECT 'add', media_tags.tag, media_tag_history.id
        FROM media
             JOIN media_tags ON media.id = media_tags.media_id
             JOIN media_tag_history on media_tag_history.media_id = media_tags.media_id;
COMMIT;
