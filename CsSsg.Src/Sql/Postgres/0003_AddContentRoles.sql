CREATE TYPE role_namespace AS ENUM ('search', 'view', 'edit', 'special');

CREATE TABLE user_roles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    namespace role_namespace NOT NULL,
    tag VARCHAR(256) NOT NULL,
    UNIQUE (user_id, namespace, tag)
);
CREATE INDEX userrole_uid on user_roles (user_id);

ALTER TABLE users ADD COLUMN pver INTEGER NOT NULL DEFAULT 1;

INSERT INTO user_roles (user_id, namespace, tag)
    SELECT id, 'search', 'public'
    FROM users;

INSERT INTO user_roles (user_id, namespace, tag)
    SELECT id, 'view', 'public'
    FROM users;

INSERT INTO user_roles (user_id, namespace, tag)
    SELECT id, 'view', 'unlisted'
    FROM users;

INSERT INTO user_roles (user_id, namespace, tag)
    SELECT id, 'view', 'create-media'
    FROM users;

INSERT INTO user_roles (user_id, namespace, tag)
    SELECT id, 'special', 'create-post'
    FROM users;

CREATE table post_tags (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    post_id UUID NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    tag VARCHAR(256) NOT NULL,
    UNIQUE (post_id, tag)
);
CREATE INDEX post_rolegroup_pid ON post_tags (post_id);
CREATE INDEX post_rolegroup_tag ON post_tags (tag);

INSERT INTO post_tags (post_id, tag)
    SELECT id, 'public'
    FROM posts where public=true;
ALTER TABLE posts DROP COLUMN public;
ALTER TABLE posts ADD COLUMN pver INTEGER NOT NULL DEFAULT 1;

CREATE table media_tags (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    media_id UUID NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    tag VARCHAR(256) NOT NULL,
    UNIQUE (media_id, tag)
);
CREATE INDEX media_rolegroup_mid ON media_tags (media_id);
CREATE INDEX media_rolegroup_tag ON media_tags (tag);

INSERT INTO media_tags (media_id, tag)
    SELECT id, 'public'
    FROM media where public=true;
ALTER TABLE media DROP COLUMN public;
ALTER TABLE media ADD COLUMN pver INTEGER NOT NULL DEFAULT 1;
