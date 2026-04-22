CREATE TABLE media (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    -- IE bookmarks limit url lengths to 260; add 15 char buffer for '/media/'
    slug VARCHAR(245) NOT NULL UNIQUE,
    contents BYTEA NOT NULL,
    -- RFC 4288 4.2 gives a limit of 255; it should be 1-127 characters on either side of the slash but we just
    -- enforce the total length
    content_type VARCHAR(255) NOT NULL CHECK(length(content_type) >= 0),
    public BOOLEAN NOT NULL DEFAULT FALSE,
    author_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE
);

CREATE TRIGGER media_set_timestamp BEFORE UPDATE ON media
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();
