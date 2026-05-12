CREATE TYPE role_namespace AS ENUM ('search', 'view', 'edit');

CREATE table post_role_groups (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    post_id UUID NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    namespace role_namespace NOT NULL,
    tag VARCHAR(256) NOT NULL,
    UNIQUE (post_id, namespace, tag)
);
CREATE INDEX post_rolegroup_ns ON post_role_groups (post_id, namespace);
CREATE INDEX post_rolegroup_tags ON post_role_groups (namespace, tag);

INSERT INTO post_role_groups (post_id, namespace, tag)
    SELECT id, 'view', 'public'
    FROM posts where public=true;
INSERT INTO post_role_groups (post_id, namespace, tag)
    SELECT id, 'search', 'public'
    FROM posts where public=true;

-- auxiliary user grants
CREATE table post_role_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    post_id UUID NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    namespace role_namespace NOT NULL,
    "user" UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE (post_id, namespace, "user")
);
CREATE INDEX post_roleuser_ns ON post_role_users (post_id, namespace);
CREATE INDEX post_roleuser_tags ON post_role_users (namespace, "user");

CREATE table media_role_groups (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    media_id UUID NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    namespace role_namespace NOT NULL,
    tag VARCHAR(256) NOT NULL,
    UNIQUE (media_id, namespace, tag)
);
CREATE INDEX media_rolegroup_ns ON media_role_groups (media_id, namespace);
CREATE INDEX media_rolegroup_tags ON media_role_groups (namespace, tag);

INSERT INTO media_role_groups (media_id, namespace, tag)
    SELECT id, 'view', 'public'
    FROM media where public=true;
INSERT INTO media_role_groups (media_id, namespace, tag)
    SELECT id, 'search', 'public'
    FROM media where public=true;

-- auxiliary user grants
CREATE table media_role_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now(),
    media_id UUID NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    namespace role_namespace NOT NULL,
    "user" UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE (media_id, namespace, "user")
);
CREATE INDEX media_roleuser_ns ON media_role_users (media_id, namespace);
CREATE INDEX media_roleuser_tags ON media_role_users (namespace, "user");
