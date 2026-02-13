CREATE TABLE users(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    -- as per RFC 5321 (S)4.5.3.1.3
    email VARCHAR(256) NOT NULL UNIQUE, 
    -- length(argon2id(m=131072, t=3, p=2, salt=[32], hash=[64]))
    pass_argon2id VARCHAR(101) NOT NULL DEFAULT ''
);

CREATE TABLE posts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    -- IE bookmarks limit url lengths to 260; add 10 char buffer for '/blog/'
    slug VARCHAR(250) NOT NULL UNIQUE,
    -- slug is derived from display_title on insert with normalization (but also who tf is going to want a 250 char title?)
    display_title VARCHAR(250) NOT NULL,
    -- when porting to other platforms, the column type MUST be one that can store multi megabyte strings
    -- (the parser limit is 2 GB due to character indexes)
    contents TEXT NOT NULL,
    public BOOLEAN NOT NULL DEFAULT FALSE,
    author_id UUID REFERENCES users(id) ON DELETE CASCADE
);

CREATE FUNCTION set_timestamp() RETURNS trigger
    LANGUAGE plpgsql AS
$$BEGIN
    NEW.updated_at := now();
RETURN NEW;
END;$$;

CREATE TRIGGER users_set_timestamp BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();
CREATE TRIGGER chirps_set_timestamp BEFORE UPDATE ON posts
    FOR EACH ROW EXECUTE PROCEDURE set_timestamp();
